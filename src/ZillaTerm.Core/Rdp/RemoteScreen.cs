using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Écran d'une session tel que le client FreeRDP l'a décodé, reçu par le tube de son option « /frame-pipe » (fork,
/// client/Windows/wf_frames.h) : le bureau distant à sa taille, quoi que montre la fenêtre (mise à l'échelle, cachée,
/// plein écran), mis à jour par les copies d'écran demandées par le serveur (défilement, fenêtre déplacée) et les seules
/// zones modifiées.
/// </summary>
/// <remarks>
/// Rempli sur un fil à part (<see cref="Read"/>), lu par l'enregistrement de l'écran (<see cref="Copy(byte[], long, out long, out ScreenMove[])"/>),
/// qui reçoit aussi les copies d'écran depuis sa lecture précédente. Un flux illisible (format inconnu, taille, copie ou
/// rectangle hors du bureau, longueur fausse) arrête la lecture : <see cref="Failed"/>, et l'enregistrement revient à la
/// capture de la fenêtre.
/// </remarks>
public sealed class RemoteScreen
{
    /// <summary>« WFR2 » : début du flux.</summary>
    public static ReadOnlySpan<byte> Magic => "WFR2"u8;

    /// <summary>Plus grand côté de bureau accepté (comme le protocole et l'enregistrement de l'écran).</summary>
    public const int MaxSide = 8192;

    /// <summary>Rectangles au plus dans un message (le client en envoie au plus 256).</summary>
    public const int MaxRectangles = 4096;

    /// <summary>Copies d'écran au plus dans un message (le client en envoie au plus 64).</summary>
    public const int MaxMoves = 1024;

    /// <summary>Messages dont les copies restent connues : de quoi couvrir quelques secondes entre deux lectures.</summary>
    private const int MovesKept = 128;

    private static long s_ids;

    private readonly object _lock = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _version;
    private long _partial;
    private long _moved;
    private bool _ended;

    // Copies de chaque message récent (numéro, copies) ; avant _movesFrom (changement de taille), plus rien ne vaut.
    private readonly Queue<(long Version, ScreenMove[] Moves)> _moves = new();
    private long _movesFrom;

    /// <summary>
    /// Numéro de cet écran, unique dans le processus (à partir de 1) : la version d'un écran ne se confond pas avec celle
    /// d'un autre (nouvelle connexion de la même session).
    /// </summary>
    public long Id { get; } = Interlocked.Increment(ref s_ids);

    /// <summary>Messages appliqués depuis le début : change à chaque mise à jour de l'écran.</summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    /// <summary>Messages qui ne couvraient pas tout le bureau : seules les zones modifiées étaient envoyées.</summary>
    public long PartialUpdates
    {
        get
        {
            lock (_lock)
            {
                return _partial;
            }
        }
    }

    /// <summary>Copies d'écran reçues (défilement, fenêtre déplacée).</summary>
    public long Moves
    {
        get
        {
            lock (_lock)
            {
                return _moved;
            }
        }
    }

    /// <summary>Flux illisible : plus rien ne sera reçu.</summary>
    public bool Failed { get; private set; }

    /// <summary>Flux terminé (client fermé, tube coupé, ou <see cref="Failed"/>).</summary>
    public bool Ended
    {
        get
        {
            lock (_lock)
            {
                return _ended;
            }
        }
    }

    /// <summary>
    /// Copie de l'écran (pixels BGRA de haut en bas, 4 octets par pixel) dans <paramref name="buffer"/> s'il est assez
    /// grand, sinon dans un nouveau tableau ; null avant le premier message. <paramref name="version"/> : la
    /// <see cref="Version"/> copiée.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? Copy(byte[]? buffer, out long version) => Copy(buffer, 0, out version, out _);

    /// <summary>
    /// Comme <see cref="Copy(byte[], out long)"/>, avec les copies d'écran reçues depuis la <see cref="Version"/>
    /// <paramref name="since"/> (lecture précédente), dans l'ordre : appliquées à l'écran de cette lecture-là, elles en
    /// déplacent le contenu comme le serveur l'a fait. Null si elles ne sont plus connues (lecture trop ancienne,
    /// changement de taille entre-temps) ou sans lecture précédente (<paramref name="since"/> à 0).
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? Copy(byte[]? buffer, long since, out long version, out ScreenMove[]? moves)
    {
        lock (_lock)
        {
            version = _version;
            moves = null;
            if (_version == 0)
            {
                return null;
            }

            int length = _width * _height * 4;
            if (buffer is null || buffer.Length < length)
            {
                buffer = new byte[length];
            }

            _pixels.AsSpan(0, length).CopyTo(buffer);
            if (since > 0 && since >= _movesFrom && since <= _version
                && (since == _version || (_moves.Count > 0 && _moves.Peek().Version <= since + 1)))
            {
                moves = [.. _moves.Where(m => m.Version > since).SelectMany(m => m.Moves)];
            }

            return (buffer, _width, _height);
        }
    }

    /// <summary>
    /// Attend un écran plus récent que <paramref name="version"/>, au plus <paramref name="timeout"/> ; faux si rien n'est
    /// arrivé (ou si le flux est terminé).
    /// </summary>
    public bool WaitNewer(long version, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_version <= version && !_ended)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_lock, left))
                {
                    return _version > version;
                }
            }

            return _version > version;
        }
    }

    /// <summary>
    /// Lit le flux jusqu'à sa fin (sur un fil à part : lectures bloquantes) et met l'écran à jour à chaque message.
    /// Ne lève pas : un flux illisible ou une erreur de lecture le termine.
    /// </summary>
    public void Read(Stream stream)
    {
        try
        {
            Span<byte> magic = stackalloc byte[4];
            if (!ReadExactly(stream, magic) || !magic.SequenceEqual(Magic))
            {
                Fail();
                return;
            }

            Span<byte> head = stackalloc byte[20];
            byte[] body = [];
            while (ReadExactly(stream, head))
            {
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(head);
                int width = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[4..]), int.MaxValue);
                int height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[8..]), int.MaxValue);
                int moves = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[12..]), int.MaxValue);
                int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[16..]), int.MaxValue);
                // Longueur annoncée bornée par ce que le bureau peut contenir : pas d'allocation démesurée.
                long most = 16 + ((long)ScreenMove.Length * MaxMoves) + (8L * MaxRectangles) + (4L * MaxSide * MaxSide);
                if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide || count is < 0 or > MaxRectangles
                    || moves is < 0 or > MaxMoves || count + moves == 0
                    || length < 16 + ((long)ScreenMove.Length * moves) + (8L * count) || length > most)
                {
                    Fail();
                    return;
                }

                int size = (int)(length - 16);
                if (body.Length < size)
                {
                    body = new byte[size];
                }

                if (!ReadExactly(stream, body.AsSpan(0, size)) || !Apply(width, height, moves, count, body.AsSpan(0, size)))
                {
                    if (!Failed)
                    {
                        End();
                    }

                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Tube coupé, client fermé.
        }

        End();
    }

    /// <summary>
    /// Un message : copies puis rectangles, dans le bureau, longueur exacte ; faux (et <see cref="Failed"/>) sinon. Un
    /// bureau d'une autre taille arrive entier, sans copie.
    /// </summary>
    private bool Apply(int width, int height, int moveCount, int count, ReadOnlySpan<byte> body)
    {
        var moves = new ScreenMove[moveCount];
        for (int i = 0; i < moveCount; i++)
        {
            var m = body.Slice(ScreenMove.Length * i, ScreenMove.Length);
            moves[i] = new ScreenMove(
                BinaryPrimitives.ReadUInt16LittleEndian(m), BinaryPrimitives.ReadUInt16LittleEndian(m[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(m[4..]), BinaryPrimitives.ReadUInt16LittleEndian(m[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(m[8..]), BinaryPrimitives.ReadUInt16LittleEndian(m[10..]));
            if (!moves[i].FitsIn(width, height))
            {
                Fail();
                return false;
            }
        }

        var rects = body[(ScreenMove.Length * moveCount)..];
        long expected = (ScreenMove.Length * moveCount) + (8L * count);
        for (int i = 0; i < count; i++)
        {
            var rect = rects.Slice(8 * i, 8);
            int x = BinaryPrimitives.ReadUInt16LittleEndian(rect), y = BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]), h = BinaryPrimitives.ReadUInt16LittleEndian(rect[6..]);
            if (w == 0 || h == 0 || x + w > width || y + h > height)
            {
                Fail();
                return false;
            }

            expected += 4L * w * h;
        }

        bool resized = width != _width || height != _height;
        if (expected != body.Length || (resized && moveCount > 0))
        {
            Fail();
            return false;
        }

        lock (_lock)
        {
            if (resized)
            {
                _width = width;
                _height = height;
                _pixels = new byte[width * height * 4];
                // Les copies d'avant ne s'appliquent pas à un bureau d'une autre taille.
                _moves.Clear();
                _movesFrom = _version + 1;
            }

            var screen = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan(0, width * height * 4));
            foreach (var move in moves)
            {
                move.Apply(screen, width);
            }

            int offset = 8 * count;
            for (int i = 0; i < count; i++)
            {
                var rect = rects.Slice(8 * i, 8);
                int x = BinaryPrimitives.ReadUInt16LittleEndian(rect), y = BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]);
                int w = BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]), h = BinaryPrimitives.ReadUInt16LittleEndian(rect[6..]);
                for (int row = 0; row < h; row++)
                {
                    var source = rects.Slice(offset + (row * w * 4), w * 4);
                    var target = _pixels.AsSpan((((y + row) * width) + x) * 4, w * 4);
                    source.CopyTo(target);
                    // Quatrième octet « inutilisé » : opaque, comme une capture d'écran.
                    for (int a = 3; a < target.Length; a += 4)
                    {
                        target[a] = 255;
                    }
                }

                offset += w * h * 4;
            }

            _version++;
            _moved += moveCount;
            _moves.Enqueue((_version, moves));
            if (_moves.Count > MovesKept)
            {
                _moves.Dequeue();
            }

            var first = rects;
            bool whole = count == 1 && moveCount == 0 && BinaryPrimitives.ReadUInt32LittleEndian(first) == 0
                         && BinaryPrimitives.ReadUInt16LittleEndian(first[4..]) == width && BinaryPrimitives.ReadUInt16LittleEndian(first[6..]) == height;
            _partial += whole ? 0 : 1;
            Monitor.PulseAll(_lock);
        }

        return true;
    }

    private void Fail()
    {
        Failed = true;
        End();
    }

    private void End()
    {
        lock (_lock)
        {
            _ended = true;
            Monitor.PulseAll(_lock);
        }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
