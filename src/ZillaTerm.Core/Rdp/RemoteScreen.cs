using System.Buffers.Binary;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Écran d'une session tel que le client FreeRDP l'a décodé, reçu par le tube de son option « /frame-pipe » (fork,
/// client/Windows/wf_frames.h) : le bureau distant à sa taille, quoi que montre la fenêtre (mise à l'échelle, cachée,
/// plein écran), mis à jour par les seules zones modifiées.
/// </summary>
/// <remarks>
/// Rempli sur un fil à part (<see cref="Read"/>), lu par l'enregistrement de l'écran (<see cref="Copy"/>). Un flux
/// illisible (format inconnu, taille ou rectangle hors du bureau, longueur fausse) arrête la lecture :
/// <see cref="Failed"/>, et l'enregistrement revient à la capture de la fenêtre.
/// </remarks>
public sealed class RemoteScreen
{
    /// <summary>« WFR1 » : début du flux.</summary>
    public static ReadOnlySpan<byte> Magic => "WFR1"u8;

    /// <summary>Plus grand côté de bureau accepté (comme le protocole et l'enregistrement de l'écran).</summary>
    public const int MaxSide = 8192;

    /// <summary>Rectangles au plus dans un message (le client en envoie au plus 256).</summary>
    public const int MaxRectangles = 4096;

    private readonly object _lock = new();
    private byte[] _pixels = [];
    private int _width;
    private int _height;
    private long _version;
    private long _partial;
    private bool _ended;

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
    public (byte[] Bgra, int Width, int Height)? Copy(byte[]? buffer, out long version)
    {
        lock (_lock)
        {
            version = _version;
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

            Span<byte> head = stackalloc byte[16];
            byte[] body = [];
            while (ReadExactly(stream, head))
            {
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(head);
                int width = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[4..]), int.MaxValue);
                int height = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[8..]), int.MaxValue);
                int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[12..]), int.MaxValue);
                // Longueur annoncée bornée par ce que le bureau peut contenir : pas d'allocation démesurée.
                long most = 12 + (8L * MaxRectangles) + (4L * MaxSide * MaxSide);
                if (width is <= 0 or > MaxSide || height is <= 0 or > MaxSide || count is <= 0 or > MaxRectangles
                    || length < 12 + (8L * count) || length > most)
                {
                    Fail();
                    return;
                }

                int size = (int)(length - 12);
                if (body.Length < size)
                {
                    body = new byte[size];
                }

                if (!ReadExactly(stream, body.AsSpan(0, size)) || !Apply(width, height, count, body.AsSpan(0, size)))
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

    /// <summary>Un message : rectangles dans le bureau, sans chevauchement de longueur ; faux (et <see cref="Failed"/>) sinon.</summary>
    private bool Apply(int width, int height, int count, ReadOnlySpan<byte> body)
    {
        long expected = 8L * count;
        for (int i = 0; i < count; i++)
        {
            var rect = body.Slice(8 * i, 8);
            int x = BinaryPrimitives.ReadUInt16LittleEndian(rect), y = BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]), h = BinaryPrimitives.ReadUInt16LittleEndian(rect[6..]);
            if (w == 0 || h == 0 || x + w > width || y + h > height)
            {
                Fail();
                return false;
            }

            expected += 4L * w * h;
        }

        if (expected != body.Length)
        {
            Fail();
            return false;
        }

        lock (_lock)
        {
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                _pixels = new byte[width * height * 4];
            }

            int offset = 8 * count;
            for (int i = 0; i < count; i++)
            {
                var rect = body.Slice(8 * i, 8);
                int x = BinaryPrimitives.ReadUInt16LittleEndian(rect), y = BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]);
                int w = BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]), h = BinaryPrimitives.ReadUInt16LittleEndian(rect[6..]);
                for (int row = 0; row < h; row++)
                {
                    var source = body.Slice(offset + (row * w * 4), w * 4);
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
            var first = body[..8];
            bool whole = count == 1 && BinaryPrimitives.ReadUInt32LittleEndian(first) == 0
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
