using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZillaTerm.Core.Interventions;

/// <summary>En-tête d'une image d'écran enregistrée (<see cref="ScreenTileEncoder"/>).</summary>
/// <param name="Stream">Enregistrement d'écran (une session Bureau à distance) auquel l'image appartient.</param>
/// <param name="Sequence">
/// Numéro de l'image dans son enregistrement (0, 1, 2…) : une image manquante (bloc du journal abîmé) se voit, et l'écran
/// n'est repris qu'à l'image clé suivante.
/// </param>
/// <param name="Activity">Part de l'écran modifiée depuis l'image précédente, en millièmes.</param>
public readonly record struct ScreenFrameHeader(
    int Stream, int Sequence, int Width, int Height, bool Key, int TileSize, int Activity, int FirstNewTile, int NewTiles, int Placements)
{
    /// <summary>« ZTS2 » : image d'écran, format 2.</summary>
    public static ReadOnlySpan<byte> Magic => "ZTS2"u8;

    public const int Length = 32;

    /// <summary>Tuiles de la grille d'un écran de cette taille.</summary>
    public int Tiles => ((Width + TileSize - 1) / TileSize) * ((Height + TileSize - 1) / TileSize);

    /// <summary>Lit l'en-tête ; faux si ce n'est pas une image d'écran de ce format.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out ScreenFrameHeader header)
    {
        header = default;
        if (data.Length < Length || !data[..4].SequenceEqual(Magic))
        {
            return false;
        }

        header = new ScreenFrameHeader(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            (data[16] & 1) != 0,
            data[17],
            BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]));
        // Écran pas plus grand que ce que l'encodeur accepte ; chaque tuile nouvelle est posée au moins une fois, et une image
        // ne pose pas plus de tuiles que la grille n'en a.
        return header.Width is > 0 and <= ScreenTileEncoder.MaxSide && header.Height is > 0 and <= ScreenTileEncoder.MaxSide
               && header.TileSize == ScreenTileEncoder.TileSize && header.Activity <= 1000
               && header.Sequence >= 0 && header.FirstNewTile >= 0 && header.NewTiles >= 0 && header.Placements >= 0
               && header.NewTiles <= header.Placements && header.Placements <= header.Tiles;
    }

    internal void Write(Span<byte> data)
    {
        Magic.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], (uint)Stream);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], (uint)Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(data[12..], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(data[14..], (ushort)Height);
        data[16] = (byte)(Key ? 1 : 0);
        data[17] = (byte)TileSize;
        BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)Activity);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)FirstNewTile);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], (uint)NewTiles);
        BinaryPrimitives.WriteUInt32LittleEndian(data[28..], (uint)Placements);
    }
}

/// <summary>Image d'écran encodée, à écrire dans le journal (<see cref="InterventionKind.Screen"/>).</summary>
public sealed record ScreenFrame(byte[] Data, ScreenFrameHeader Header);

/// <summary>
/// Écran d'une session Bureau à distance enregistré comme le protocole RDP l'envoie : seules les zones modifiées.
/// Chaque capture est découpée en tuiles de 64×64 pixels, en 16 bits (RGB565, la profondeur des sessions PSM) ; seules
/// les tuiles modifiées depuis l'image précédente sont gardées, et une tuile déjà vue (même menu, même boîte de dialogue)
/// n'est gardée qu'une fois, désignée ensuite par son numéro. Une tuile peu modifiée (caractère tapé) est gardée en
/// différence (XOR) avec ce qu'elle montrait : presque des zéros, que la compression (zlib) réduit à peu de chose.
/// </summary>
/// <remarks>
/// <para>
/// Seuil de bruit : une tuile dont au plus <see cref="NoiseThreshold"/> pixels changent (curseur texte, horloge) n'est
/// pas gardée tout de suite ; elle l'est dès que les changements cumulés le dépassent (la comparaison se fait avec ce
/// qui a été gardé), ou en rattrapage : une fois la tuile immobile pendant <see cref="CatchUpDelay"/> (le dernier
/// caractère tapé), au plus tard après <see cref="CatchUpMaxDelay"/> (une tuile qui bouge sans cesse).
/// </para>
/// <para>
/// Image clé (écran entier, cache de tuiles reparti d'elle) : à la première capture, après un changement de taille, à la
/// demande, et quand les images depuis la précédente pèsent <see cref="KeyFrameBytesFactor"/> fois sa taille (au plus
/// tôt après <see cref="KeyFrameMinInterval"/>), ou au plus tard après <see cref="KeyFrameMaxInterval"/> si l'écran a
/// changé. Un écran immobile n'en coûte aucune ; un lecteur va à une heure en rejouant depuis l'image clé précédente.
/// </para>
/// <para>
/// Format d'une image (petit-boutiste) : l'en-tête <see cref="ScreenFrameHeader"/> (32 octets), puis un flux zlib avec
/// les positions (numéro de tuile dans la grille, numéro de tuile gardée : 2 × 4 octets chacune), la base de chaque tuile
/// nouvelle (4 octets : <see cref="FullTile"/> pour une tuile entière, ou le numéro de la tuile de la grille dont elle est
/// la différence, avec l'écran d'avant cette image), puis les pixels des tuiles nouvelles (64 × 64 × 2 octets chacune,
/// bords de l'écran complétés par des zéros), numérotées à la suite de <see cref="ScreenFrameHeader.FirstNewTile"/>.
/// </para>
/// </remarks>
public sealed class ScreenTileEncoder
{
    public const int TileSize = 64;

    private const int TilePixels = TileSize * TileSize;

    /// <summary>Base d'une tuile nouvelle gardée entière (pas en différence).</summary>
    public const uint FullTile = uint.MaxValue;

    /// <summary>Pixels modifiés au plus d'une tuile sans qu'elle soit gardée tout de suite (curseur texte, horloge).</summary>
    public const int NoiseThreshold = 32;

    /// <summary>
    /// Pixels modifiés au plus d'une tuile gardée en différence (un mot tapé) ; au-delà, elle l'est entière : une tuile
    /// très modifiée se compresse mieux entière qu'en différence.
    /// </summary>
    public const int DeltaMaxPixels = TilePixels / 16;

    /// <summary>Plus grand côté d'écran accepté (pixels).</summary>
    public const int MaxSide = 8192;

    /// <summary>Écart minimal entre deux images clés venues du poids des images.</summary>
    public static readonly TimeSpan KeyFrameMinInterval = TimeSpan.FromMinutes(5);

    /// <summary>Écart maximal entre deux images clés quand l'écran a changé entre-temps (reprise après une image perdue).</summary>
    public static readonly TimeSpan KeyFrameMaxInterval = TimeSpan.FromMinutes(30);

    /// <summary>Poids des images depuis l'image clé, en multiple de sa taille, qui en appelle une nouvelle.</summary>
    public const int KeyFrameBytesFactor = 4;

    /// <summary>Immobilité d'une tuile après laquelle son reste sous le seuil de bruit est gardé.</summary>
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(3);

    /// <summary>Délai au plus avant qu'un reste sous le seuil de bruit soit gardé, même si la tuile bouge encore.</summary>
    public static readonly TimeSpan CatchUpMaxDelay = TimeSpan.FromSeconds(60);

    private readonly int _stream;
    private readonly Dictionary<UInt128, int> _ids = [];
    private int _nextId;
    private int _sequence;
    private int _width;
    private int _height;
    private int _columns;
    private int _rows;
    private ushort[] _current = [];
    private ushort[] _previous = [];
    private ushort[]? _shown;

    // Par tuile : depuis quand un reste sous le seuil attend, et depuis quand la tuile n'a pas bougé.
    private DateTime[] _residueSince = [];
    private DateTime[] _stableSince = [];
    private DateTime _lastKey;
    private long _lastKeyBytes;
    private long _sinceKey;

    /// <param name="stream">Numéro de l'enregistrement d'écran (une session), écrit dans chaque image.</param>
    /// <param name="live">
    /// Écran partagé en direct : tout changement part tout de suite, même un caractère tapé (pas de seuil de bruit ni
    /// de rattrapage différé).
    /// </param>
    public ScreenTileEncoder(int stream, bool live = false)
    {
        _stream = stream;
        _noise = live ? 0 : NoiseThreshold;
    }

    private readonly int _noise;

    /// <summary>
    /// Encode une capture (pixels BGRA 32 bits, de haut en bas) ; null si rien n'a changé de façon visible, qu'aucun reste
    /// n'est à rattraper et qu'aucune image clé n'est due. À appeler à chaque capture, même sans changement.
    /// </summary>
    /// <param name="stride">Octets par ligne de <paramref name="bgra"/>.</param>
    /// <param name="key">Image clé demandée.</param>
    public ScreenFrame? Encode(DateTime time, ReadOnlySpan<byte> bgra, int width, int height, int stride, bool key = false)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || stride < width * 4
            || bgra.Length < ((long)stride * (height - 1)) + (width * 4))
        {
            throw new ArgumentException("Invalid screen capture.", nameof(bgra));
        }

        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _columns = (width + TileSize - 1) / TileSize;
            _rows = (height + TileSize - 1) / TileSize;
            _current = new ushort[_columns * TileSize * _rows * TileSize];
            _previous = new ushort[_current.Length];
            _residueSince = new DateTime[_columns * _rows];
            _stableSince = new DateTime[_columns * _rows];
            _shown = null;
        }
        else
        {
            // La capture d'avant reste à côté : une tuile qui n'a pas bougé depuis se reconnaît.
            (_previous, _current) = (_current, _previous);
        }

        int pitch = _columns * TileSize;
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var target = _current.AsSpan(y * pitch, width);
            for (int x = 0; x < width; x++)
            {
                int b = row[4 * x], g = row[(4 * x) + 1], r = row[(4 * x) + 2];
                target[x] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            }
        }

        bool first = _shown is null;
        var shown = _shown ??= new ushort[_current.Length];
        key |= first || KeyDue(time);

        // Tuiles à garder : modifiées au-delà du seuil, ou dont le reste sous le seuil est à rattraper (toutes pour une
        // image clé).
        var kept = new List<(int Index, int Origin, int Differences)>();
        int changed = 0;
        for (int index = 0; index < _rows * _columns; index++)
        {
            int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
            int differences = first ? TilePixels : Differences(_current, shown, origin, pitch);
            bool visible = differences > _noise;
            bool catchUp = false;
            if (differences == 0 || visible)
            {
                _residueSince[index] = default;
            }
            else
            {
                if (_residueSince[index] == default)
                {
                    _residueSince[index] = time;
                }

                if (!Same(_current, _previous, origin, pitch))
                {
                    _stableSince[index] = time;
                }

                catchUp = time - _stableSince[index] >= CatchUpDelay || time - _residueSince[index] >= CatchUpMaxDelay;
            }

            if (visible || catchUp)
            {
                changed++;
            }

            if (visible || catchUp || key)
            {
                kept.Add((index, origin, differences));
            }
        }

        if (kept.Count == 0)
        {
            return null;
        }

        if (key)
        {
            // Le cache repart de l'image clé : un lecteur rejoue depuis elle sans garder toutes les tuiles vues.
            _ids.Clear();
        }

        var placements = new List<(int Index, int Id)>(kept.Count);
        var bases = new List<uint>();
        var fresh = new MemoryStream();
        int firstNew = _nextId;
        var tile = new ushort[TilePixels];
        var delta = new ushort[TilePixels];
        foreach (var (index, origin, differences) in kept)
        {
            for (int y = 0; y < TileSize; y++)
            {
                _current.AsSpan(origin + (y * pitch), TileSize).CopyTo(tile.AsSpan(y * TileSize));
            }

            var hash = BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(MemoryMarshal.AsBytes(tile.AsSpan())));
            if (!_ids.TryGetValue(hash, out int id))
            {
                id = _nextId++;
                _ids[hash] = id;
                if (!key && differences <= DeltaMaxPixels)
                {
                    // Différence avec ce que la tuile montrait (l'écran d'avant cette image, que le lecteur a aussi).
                    for (int y = 0; y < TileSize; y++)
                    {
                        var before = shown.AsSpan(origin + (y * pitch), TileSize);
                        for (int x = 0; x < TileSize; x++)
                        {
                            delta[(y * TileSize) + x] = (ushort)(tile[(y * TileSize) + x] ^ before[x]);
                        }
                    }

                    bases.Add((uint)index);
                    WriteLittleEndian(fresh, delta);
                }
                else
                {
                    bases.Add(FullTile);
                    WriteLittleEndian(fresh, tile);
                }
            }

            placements.Add((index, id));
            for (int y = 0; y < TileSize; y++)
            {
                tile.AsSpan(y * TileSize, TileSize).CopyTo(shown.AsSpan(origin + (y * pitch)));
            }

            _residueSince[index] = default;
        }

        int activity = first ? 1000 : changed * 1000 / (_rows * _columns);
        var header = new ScreenFrameHeader(_stream, _sequence, width, height, key, TileSize, activity, firstNew, bases.Count, placements.Count);
        var output = new MemoryStream();
        output.Write(new byte[ScreenFrameHeader.Length]);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            Span<byte> value = stackalloc byte[8];
            foreach (var (index, id) in placements)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)index);
                BinaryPrimitives.WriteUInt32LittleEndian(value[4..], (uint)id);
                zlib.Write(value);
            }

            foreach (uint origin in bases)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, origin);
                zlib.Write(value[..4]);
            }

            fresh.Position = 0;
            fresh.CopyTo(zlib);
        }

        var data = output.ToArray();
        header.Write(data);
        _sequence++;
        if (key)
        {
            _lastKey = time;
            _lastKeyBytes = data.Length;
            _sinceKey = 0;
        }
        else
        {
            _sinceKey += data.Length;
        }

        return new ScreenFrame(data, header);
    }

    /// <summary>
    /// Image clé due : l'écran a changé depuis la précédente, et les images depuis pèsent assez lourd (au plus tôt après
    /// <see cref="KeyFrameMinInterval"/>) ou <see cref="KeyFrameMaxInterval"/> est passé.
    /// </summary>
    private bool KeyDue(DateTime time)
    {
        if (_sinceKey == 0)
        {
            return false;
        }

        var since = time - _lastKey;
        return since >= KeyFrameMaxInterval || (since >= KeyFrameMinInterval && _sinceKey >= KeyFrameBytesFactor * _lastKeyBytes);
    }

    /// <summary>Pixels différents entre deux tuiles (même position) de deux écrans.</summary>
    private static int Differences(ushort[] a, ushort[] b, int origin, int pitch)
    {
        int count = 0;
        for (int y = 0; y < TileSize; y++)
        {
            var rowA = a.AsSpan(origin + (y * pitch), TileSize);
            var rowB = b.AsSpan(origin + (y * pitch), TileSize);
            if (rowA.SequenceEqual(rowB))
            {
                continue;
            }

            for (int x = 0; x < TileSize; x++)
            {
                if (rowA[x] != rowB[x])
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Même tuile (même position) dans deux écrans.</summary>
    private static bool Same(ushort[] a, ushort[] b, int origin, int pitch)
    {
        for (int y = 0; y < TileSize; y++)
        {
            if (!a.AsSpan(origin + (y * pitch), TileSize).SequenceEqual(b.AsSpan(origin + (y * pitch), TileSize)))
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteLittleEndian(Stream stream, ushort[] pixels)
    {
        Span<byte> bytes = stackalloc byte[TilePixels * 2];
        for (int i = 0; i < pixels.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(2 * i)..], pixels[i]);
        }

        stream.Write(bytes);
    }
}

/// <summary>
/// Relecture d'un enregistrement d'écran (<see cref="ScreenTileEncoder"/>) : l'écran en RGB565 après chaque image. Après
/// une image illisible ou manquante (numéro qui ne suit pas), plus rien ne se lit jusqu'à l'image clé suivante : les
/// différences et les tuiles déjà vues ne s'appliqueraient plus au bon écran.
/// </summary>
public sealed class ScreenTileDecoder
{
    private const int TileSize = ScreenTileEncoder.TileSize;
    private const int TilePixels = TileSize * TileSize;

    private readonly Dictionary<int, ushort[]> _tiles = [];

    // Numéro de l'image attendue ; -1 : en attente d'une image clé.
    private int _next = -1;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Écran (RGB565, ligne par ligne) ; vide avant la première image clé.</summary>
    public ushort[] Pixels { get; private set; } = [];

    /// <summary>Applique une image ; faux si elle est illisible, ne suit pas la précédente ou arrive avant une image clé.</summary>
    public bool Apply(byte[] data)
    {
        if (!ScreenFrameHeader.TryRead(data, out var header))
        {
            _next = -1;
            return false;
        }

        if (header.Key)
        {
            _tiles.Clear();
            if (header.Width != Width || header.Height != Height)
            {
                Width = header.Width;
                Height = header.Height;
                Pixels = new ushort[Width * Height];
            }
        }
        else if (_next < 0 || header.Sequence != _next || header.Width != Width || header.Height != Height)
        {
            _next = -1;
            return false;
        }

        _next = -1;
        long expected = (8L * header.Placements) + (4L * header.NewTiles) + (2L * TilePixels * header.NewTiles);
        var body = new byte[expected];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data, ScreenFrameHeader.Length, data.Length - ScreenFrameHeader.Length), CompressionMode.Decompress);
            // Ni plus court ni plus long que l'annonce de l'en-tête (pas de décompression sans fin).
            if (zlib.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) != body.Length || zlib.ReadByte() >= 0)
            {
                return false;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        int columns = (Width + TileSize - 1) / TileSize;
        int basesAt = 8 * header.Placements;
        int tilesAt = basesAt + (4 * header.NewTiles);
        // Tuiles nouvelles d'abord, les différences d'après l'écran d'avant cette image.
        var fresh = new ushort[header.NewTiles][];
        for (int i = 0; i < header.NewTiles; i++)
        {
            var tile = new ushort[TilePixels];
            var bytes = body.AsSpan(tilesAt + (i * 2 * TilePixels), 2 * TilePixels);
            for (int p = 0; p < TilePixels; p++)
            {
                tile[p] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * p)..]);
            }

            uint origin = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(basesAt + (4 * i)));
            if (origin != ScreenTileEncoder.FullTile)
            {
                if (header.Key || origin >= (uint)header.Tiles)
                {
                    return false;
                }

                Visit((int)origin, columns, (offset, pixel) => tile[offset] ^= Pixels[pixel]);
            }

            fresh[i] = tile;
        }

        for (int i = 0; i < fresh.Length; i++)
        {
            _tiles[header.FirstNewTile + i] = fresh[i];
        }

        for (int i = 0; i < header.Placements; i++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8 * i));
            int id = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan((8 * i) + 4));
            if (index >= (uint)header.Tiles || !_tiles.TryGetValue(id, out var tile))
            {
                return false;
            }

            Visit((int)index, columns, (offset, pixel) => Pixels[pixel] = tile[offset]);
        }

        _next = header.Sequence + 1;
        return true;
    }

    /// <summary>Chaque pixel de l'écran couvert par une tuile de la grille : sa place dans la tuile, puis dans l'écran.</summary>
    private void Visit(int index, int columns, Action<int, int> pixel)
    {
        int x0 = index % columns * TileSize;
        int y0 = index / columns * TileSize;
        int width = Math.Min(TileSize, Width - x0);
        for (int y = 0; y < TileSize && y0 + y < Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixel((y * TileSize) + x, ((y0 + y) * Width) + x0 + x);
            }
        }
    }
}
