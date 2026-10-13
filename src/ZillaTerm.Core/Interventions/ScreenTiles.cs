using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZillaTerm.Core.Interventions;

/// <summary>En-tête d'une image d'écran enregistrée (<see cref="ScreenTileEncoder"/>).</summary>
/// <param name="Stream">Enregistrement d'écran (une session Bureau à distance) auquel l'image appartient.</param>
/// <param name="Activity">Part de l'écran modifiée depuis l'image précédente, en millièmes.</param>
public readonly record struct ScreenFrameHeader(
    int Stream, int Width, int Height, bool Key, int TileSize, int Activity, int FirstNewTile, int NewTiles, int Placements)
{
    /// <summary>« ZTS1 » : image d'écran, format 1.</summary>
    public static ReadOnlySpan<byte> Magic => "ZTS1"u8;

    public const int Length = 28;

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
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            (data[12] & 1) != 0,
            data[13],
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[24..]));
        return header.Width > 0 && header.Height > 0 && header.TileSize == ScreenTileEncoder.TileSize && header.Activity <= 1000
               && header.NewTiles >= 0 && header.Placements >= 0;
    }

    internal void Write(Span<byte> data)
    {
        Magic.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], (uint)Stream);
        BinaryPrimitives.WriteUInt16LittleEndian(data[8..], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(data[10..], (ushort)Height);
        data[12] = (byte)(Key ? 1 : 0);
        data[13] = (byte)TileSize;
        BinaryPrimitives.WriteUInt16LittleEndian(data[14..], (ushort)Activity);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..], (uint)FirstNewTile);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)NewTiles);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], (uint)Placements);
    }
}

/// <summary>Image d'écran encodée, à écrire dans le journal (<see cref="InterventionKind.Screen"/>).</summary>
public sealed record ScreenFrame(byte[] Data, ScreenFrameHeader Header);

/// <summary>
/// Écran d'une session Bureau à distance enregistré comme le protocole RDP l'envoie : seules les zones modifiées.
/// Chaque capture est découpée en tuiles de 64×64 pixels, en 16 bits (RGB565, la profondeur des sessions PSM) ; seules
/// les tuiles modifiées depuis l'image précédente sont gardées, et une tuile déjà vue (même menu, même boîte de dialogue)
/// n'est gardée qu'une fois, désignée ensuite par son numéro. Les tuiles nouvelles d'une capture et leurs positions sont
/// compressées ensemble (zlib).
/// </summary>
/// <remarks>
/// <para>
/// Seuil de bruit : une tuile dont au plus <see cref="NoiseThreshold"/> pixels changent (curseur texte, horloge) n'est
/// pas gardée ; elle l'est dès que les changements cumulés le dépassent, la comparaison se faisant avec ce qui a été
/// gardé. Une image clé (écran entier) ouvre l'enregistrement, puis revient toutes les <see cref="KeyFrameInterval"/>,
/// après un changement de taille et à la demande (onglet de nouveau affiché) : le cache de tuiles repart d'elle, si bien
/// qu'un lecteur peut aller à une heure en rejouant depuis l'image clé précédente.
/// </para>
/// <para>
/// Format d'une image (petit-boutiste) : l'en-tête <see cref="ScreenFrameHeader"/> (28 octets), puis un flux zlib avec
/// les positions (numéro de tuile dans la grille, numéro de tuile gardée : 2 × 4 octets chacune), puis les pixels des
/// tuiles nouvelles (64 × 64 × 2 octets chacune, bords de l'écran complétés par des zéros), numérotées à la suite de
/// <see cref="ScreenFrameHeader.FirstNewTile"/>.
/// </para>
/// </remarks>
public sealed class ScreenTileEncoder
{
    public const int TileSize = 64;

    private const int TilePixels = TileSize * TileSize;

    /// <summary>Pixels modifiés au plus d'une tuile sans qu'elle soit gardée (curseur texte, horloge).</summary>
    public const int NoiseThreshold = 32;

    /// <summary>Plus grand côté d'écran accepté (pixels).</summary>
    public const int MaxSide = 8192;

    public static readonly TimeSpan KeyFrameInterval = TimeSpan.FromMinutes(5);

    private readonly int _stream;
    private readonly Dictionary<UInt128, int> _ids = [];
    private int _nextId;
    private int _width;
    private int _height;
    private int _columns;
    private int _rows;
    private ushort[] _current = [];
    private ushort[]? _shown;
    private DateTime _lastKey;

    /// <param name="stream">Numéro de l'enregistrement d'écran (une session), écrit dans chaque image.</param>
    public ScreenTileEncoder(int stream) => _stream = stream;

    /// <summary>
    /// Encode une capture (pixels BGRA 32 bits, de haut en bas) ; null si rien n'a changé de façon visible et qu'aucune
    /// image clé n'est due.
    /// </summary>
    /// <param name="stride">Octets par ligne de <paramref name="bgra"/>.</param>
    /// <param name="key">Image clé demandée (onglet de nouveau affiché).</param>
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
            _shown = null;
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
        key |= first || time - _lastKey >= KeyFrameInterval;
        if (key)
        {
            // Le cache repart de l'image clé : un lecteur rejoue depuis elle sans garder toutes les tuiles vues.
            _ids.Clear();
            _lastKey = time;
        }

        var shown = _shown ??= new ushort[_current.Length];
        var placements = new List<(int Index, int Id)>();
        var fresh = new MemoryStream();
        int firstNew = _nextId;
        int newTiles = 0;
        int changed = 0;
        var tile = new ushort[TilePixels];
        for (int row = 0; row < _rows; row++)
        {
            for (int column = 0; column < _columns; column++)
            {
                int origin = (row * TileSize * pitch) + (column * TileSize);
                bool modified = first || Differences(_current, shown, origin, pitch) > NoiseThreshold;
                if (modified)
                {
                    changed++;
                }

                if (!modified && !key)
                {
                    continue;
                }

                for (int y = 0; y < TileSize; y++)
                {
                    _current.AsSpan(origin + (y * pitch), TileSize).CopyTo(tile.AsSpan(y * TileSize));
                    _current.AsSpan(origin + (y * pitch), TileSize).CopyTo(shown.AsSpan(origin + (y * pitch)));
                }

                var bytes = MemoryMarshal.AsBytes(tile.AsSpan());
                var hash = BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(bytes));
                if (!_ids.TryGetValue(hash, out int id))
                {
                    id = _nextId++;
                    _ids[hash] = id;
                    newTiles++;
                    WriteLittleEndian(fresh, tile);
                }

                placements.Add(((row * _columns) + column, id));
            }
        }

        if (placements.Count == 0)
        {
            return null;
        }

        var header = new ScreenFrameHeader(_stream, width, height, key, TileSize, changed * 1000 / (_rows * _columns), firstNew, newTiles,
            placements.Count);
        var output = new MemoryStream();
        output.Write(new byte[ScreenFrameHeader.Length]);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            Span<byte> pair = stackalloc byte[8];
            foreach (var (index, id) in placements)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(pair, (uint)index);
                BinaryPrimitives.WriteUInt32LittleEndian(pair[4..], (uint)id);
                zlib.Write(pair);
            }

            fresh.Position = 0;
            fresh.CopyTo(zlib);
        }

        var data = output.ToArray();
        header.Write(data);
        return new ScreenFrame(data, header);
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

/// <summary>Relecture d'un enregistrement d'écran (<see cref="ScreenTileEncoder"/>) : l'écran en RGB565 après chaque image.</summary>
public sealed class ScreenTileDecoder
{
    private const int TileSize = ScreenTileEncoder.TileSize;
    private const int TilePixels = TileSize * TileSize;

    private readonly Dictionary<int, ushort[]> _tiles = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Écran (RGB565, ligne par ligne) ; vide avant la première image clé.</summary>
    public ushort[] Pixels { get; private set; } = [];

    /// <summary>Applique une image ; faux si elle est illisible ou arrive avant une image clé.</summary>
    public bool Apply(byte[] data)
    {
        if (!ScreenFrameHeader.TryRead(data, out var header))
        {
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
        else if (Pixels.Length == 0 || header.Width != Width || header.Height != Height)
        {
            return false;
        }

        byte[] body;
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data, ScreenFrameHeader.Length, data.Length - ScreenFrameHeader.Length), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            zlib.CopyTo(plain);
            body = plain.ToArray();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        long expected = (8L * header.Placements) + (2L * TilePixels * header.NewTiles);
        if (body.Length != expected)
        {
            return false;
        }

        int tilesAt = 8 * header.Placements;
        for (int i = 0; i < header.NewTiles; i++)
        {
            var tile = new ushort[TilePixels];
            var bytes = body.AsSpan(tilesAt + (i * 2 * TilePixels), 2 * TilePixels);
            for (int p = 0; p < TilePixels; p++)
            {
                tile[p] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * p)..]);
            }

            _tiles[header.FirstNewTile + i] = tile;
        }

        int columns = (Width + TileSize - 1) / TileSize;
        for (int i = 0; i < header.Placements; i++)
        {
            int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8 * i));
            int id = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan((8 * i) + 4));
            if (!_tiles.TryGetValue(id, out var tile))
            {
                return false;
            }

            int x0 = index % columns * TileSize;
            int y0 = index / columns * TileSize;
            for (int y = 0; y < TileSize && y0 + y < Height; y++)
            {
                int width = Math.Min(TileSize, Width - x0);
                if (width <= 0)
                {
                    break;
                }

                tile.AsSpan(y * TileSize, width).CopyTo(Pixels.AsSpan(((y0 + y) * Width) + x0));
            }
        }

        return true;
    }
}
