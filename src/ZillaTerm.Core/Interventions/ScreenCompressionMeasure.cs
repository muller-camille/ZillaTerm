using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZillaTerm.Core.Interventions;

/// <summary>Poids de l'écran enregistré avec une façon de l'encoder, et le temps de calcul qu'elle a pris.</summary>
public sealed record ScreenCompressionMethod(string Name, long Bytes, TimeSpan Time);

/// <summary>Résultat de <see cref="ScreenCompressionMeasure.Measure"/>.</summary>
/// <param name="Streams">Enregistrements d'écran (sessions Bureau à distance) relus.</param>
/// <param name="Frames">Images relues (pauses comprises).</param>
/// <param name="KeyFrames">Images clés parmi elles.</param>
/// <param name="Lost">Images illisibles, ou perdues jusqu'à l'image clé suivante (bloc du journal abîmé).</param>
/// <param name="JournalBytes">Poids des images dans le journal, tel qu'enregistré.</param>
/// <param name="KeyFrameBytes">Poids des images clés dans le journal.</param>
/// <param name="Duration">De la première à la dernière image.</param>
/// <param name="Moved">
/// Images avec des copies d'écran (défilement, fenêtre déplacée) : celles de l'enregistrement, ou un défilement reconnu
/// pour un enregistrement d'avant la 0.42.
/// </param>
/// <param name="Methods">
/// Poids réencodé, le format actuel d'abord (contrôle : le poids de <paramref name="JournalBytes"/> pour un journal
/// enregistré depuis la 0.42).
/// </param>
public sealed record ScreenCompressionResult(
    int Streams, int Frames, int KeyFrames, int Lost, long JournalBytes, long KeyFrameBytes, TimeSpan Duration, int Moved,
    IReadOnlyList<ScreenCompressionMethod> Methods)
{
    /// <summary>Lignes du journal de débogage : le bilan, puis une ligne par méthode (écart avec la première).</summary>
    public IEnumerable<string> Lines()
    {
        var c = CultureInfo.InvariantCulture;
        var lost = Lost > 0 ? string.Create(c, $", {Lost} illisible(s) ou perdue(s)") : "";
        var keys = JournalBytes > 0 ? string.Create(c, $" ({100.0 * KeyFrameBytes / JournalBytes:0} %)") : "";
        yield return string.Create(c,
            $"{Streams} écran(s), {Frames} image(s) dont {KeyFrames} complète(s){lost}, sur {Duration.TotalMinutes:0.#} min ; journal {ScreenRecordingStats.Bytes(JournalBytes)}, dont images complètes {ScreenRecordingStats.Bytes(KeyFrameBytes)}{keys}");
        long reference = Methods.Count > 0 ? Methods[0].Bytes : 0;
        for (int i = 0; i < Methods.Count; i++)
        {
            var method = Methods[i];
            // Écart arrondi d'abord : « +0 % » plutôt que « -0 % » pour un écart infime.
            int change = reference > 0 ? (int)Math.Round(100.0 * (method.Bytes - reference) / reference) : 0;
            var gain = i > 0 && reference > 0 ? string.Create(c, $" ({change:+0;-0;+0} %)") : "";
            yield return string.Create(c, $"  {method.Name} : {ScreenRecordingStats.Bytes(method.Bytes)}{gain}, {method.Time.TotalSeconds:0.0} s de calcul");
        }

        int images = Frames - Lost;
        yield return string.Create(c, $"  copies d'écran (défilement, fenêtre déplacée) sur {Moved} image(s){(images > 0 ? $" ({100.0 * Moved / images:0.#} %)" : "")}");
    }
}

/// <summary>
/// Ce que pèserait l'écran d'un enregistrement existant avec d'autres façons de l'encoder que le format actuel, toutes
/// sans perte : chaque image relue (<see cref="ScreenTileDecoder"/>) est réencodée comme le fait
/// <see cref="ScreenTileEncoder"/> (copies d'écran, tuiles entières, en différence ou déjà vues, 16 ou 24 bits, Brotli
/// d'une image clé à la suivante), puis avec une seule chose changée à la fois. Pour le journal de débogage : rien n'est
/// écrit, l'écran relu ne quitte pas la mémoire.
/// </summary>
/// <remarks>
/// <para>
/// Le format actuel réencodé redonne le poids d'un journal enregistré depuis la 0.42, à l'octet près : les mêmes images
/// (seuil de bruit déjà appliqué à l'enregistrement), les mêmes copies d'écran, les mêmes images clés. Pour un journal
/// plus ancien, sans copies enregistrées, un défilement est cherché comme le fait l'encodeur sans le client FreeRDP.
/// </para>
/// <para>
/// Façons comparées : sans copie d'écran (ce qu'elles apportent) ; 16 ou 24 bits choisis tuile par tuile plutôt que pour
/// toute l'image ; tuiles d'au plus 256 couleurs en palette (couleurs, puis 1, 2, 4 ou 8 bits par pixel) ; Brotli plus
/// fort (9, 11) ; un seul flux Brotli pour tout l'enregistrement (une image clé se compresse avec les précédentes, mais ne
/// se lit plus seule) ; moins d'images clés (une toutes les 30 minutes au plus, ou seulement la première). Chaque contenu
/// réencodé est relu et comparé à l'image : une façon qui perdrait quelque chose arrête la mesure.
/// </para>
/// </remarks>
public static class ScreenCompressionMeasure
{
    private const int TileSize = ScreenTileEncoder.TileSize;
    private const int TilePixels = TileSize * TileSize;

    /// <summary>Couleurs au plus d'une tuile en palette.</summary>
    private const int PaletteMax = 256;

    /// <summary>Façon d'encoder une tuile nouvelle (octet devant elle, sauf pour <see cref="TileFormat.Frame"/>).</summary>
    private const byte Narrow = 0, Wide = 1, Palette = 2;

    /// <summary>Façons mesurées, le format actuel d'abord.</summary>
    private static readonly Method[] Methods =
    [
        new("format actuel (contrôle)", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Frame, 7, OneStream: false),
        new("sans copie d'écran", MoveMode.None, KeyMode.Recorded, TileFormat.Frame, 7, OneStream: false),
        new("16 ou 24 bits tuile par tuile", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Tile, 7, OneStream: false),
        new("tuiles d'au plus 256 couleurs en palette", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Palette, 7, OneStream: false),
        new("Brotli 9", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Frame, 9, OneStream: false),
        new("Brotli 11", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Frame, 11, OneStream: false),
        new("un seul flux Brotli (images clés comprises)", MoveMode.Recorded, KeyMode.Recorded, TileFormat.Frame, 7, OneStream: true),
        new("une image clé toutes les 30 min au plus", MoveMode.Recorded, KeyMode.HalfHour, TileFormat.Frame, 7, OneStream: false),
        new("seule la première image complète", MoveMode.Recorded, KeyMode.FirstOnly, TileFormat.Frame, 7, OneStream: false),
    ];

    public static ScreenCompressionResult Measure(IEnumerable<InterventionEntry> entries, CancellationToken cancel = default)
    {
        var streams = new Dictionary<int, ScreenStream>();
        int frames = 0, keys = 0, lost = 0;
        long journal = 0, keyBytes = 0;
        DateTime? first = null, last = null;
        foreach (var entry in entries)
        {
            if (entry.Kind != InterventionKind.Screen || entry.Image is not { } data || !ScreenFrameHeader.TryRead(data, out var header))
            {
                continue;
            }

            cancel.ThrowIfCancellationRequested();
            frames++;
            journal += data.Length;
            first ??= entry.Time;
            last = entry.Time;
            if (!streams.TryGetValue(header.Stream, out var stream))
            {
                streams[header.Stream] = stream = new ScreenStream();
            }

            if (!stream.Decoder.Apply(data))
            {
                lost++;
                continue;
            }

            if (header.Key)
            {
                keys++;
                keyBytes += data.Length;
            }

            stream.Add(entry.Time, header);
        }

        var methods = new List<ScreenCompressionMethod>();
        for (int i = 0; i < Methods.Length; i++)
        {
            methods.Add(new ScreenCompressionMethod(Methods[i].Name, streams.Values.Sum(s => s.Bytes(i)),
                streams.Values.Aggregate(TimeSpan.Zero, (time, s) => time + s.Time(i))));
        }

        foreach (var stream in streams.Values)
        {
            stream.Dispose();
        }

        int moved = streams.Values.Sum(stream => stream.Moved);
        return new ScreenCompressionResult(streams.Count, frames, keys, lost, journal, keyBytes, (last - first) ?? TimeSpan.Zero, moved, methods);
    }

    private enum MoveMode
    {
        /// <summary>Copies d'écran de l'enregistrement (défilement reconnu avant la 0.42).</summary>
        Recorded,

        /// <summary>Aucune copie : tout ce qui a bougé part en tuiles.</summary>
        None,
    }

    private enum KeyMode
    {
        /// <summary>Images clés de l'enregistrement.</summary>
        Recorded,

        /// <summary>Une image clé toutes les <see cref="ScreenTileEncoder.KeyFrameMaxInterval"/> au plus.</summary>
        HalfHour,

        /// <summary>Seule la première image (ou celle d'un changement de taille) est complète.</summary>
        FirstOnly,
    }

    private enum TileFormat
    {
        /// <summary>Comme aujourd'hui : toutes les tuiles nouvelles d'une image en 16 bits si elles le permettent, sinon en 24.</summary>
        Frame,

        /// <summary>16 bits pour chaque tuile qui le permet, 24 pour les autres.</summary>
        Tile,

        /// <summary>Palette pour les tuiles d'au plus <see cref="PaletteMax"/> couleurs, sinon comme <see cref="Tile"/>.</summary>
        Palette,
    }

    private sealed record Method(string Name, MoveMode Moves, KeyMode Keys, TileFormat Format, int Quality, bool OneStream);

    /// <summary>Image réencodée en tuiles, avant d'être écrite : copies, positions, tuiles nouvelles (base, pixels, écran d'avant).</summary>
    private sealed record TileFrame(bool Key, List<ScreenMove> Moves, List<(int Index, int Id)> Placements,
        List<(uint Base, uint[] Tile, uint[]? Before)> Fresh);

    /// <summary>Un enregistrement d'écran : sa relecture, ses réencodages en tuiles et leurs compressions.</summary>
    private sealed class ScreenStream : IDisposable
    {
        private readonly (MoveMode Moves, KeyMode Keys)[] _modes;
        private readonly Tiles[] _tiles;
        private readonly int[] _tilesOf;
        private readonly BrotliSink[] _sinks;
        private readonly TimeSpan[] _writing;
        private readonly Dictionary<(int Tiles, TileFormat Format), (byte[] Body, TimeSpan Time)> _bodies = [];
        private uint[] _screen = [];
        private int _width;
        private int _height;

        public ScreenStream()
        {
            _modes = [.. Methods.Select(m => (m.Moves, m.Keys)).Distinct()];
            _tiles = [.. _modes.Select(m => new Tiles(m.Moves, m.Keys))];
            _tilesOf = [.. Methods.Select(m => Array.IndexOf(_modes, (m.Moves, m.Keys)))];
            _sinks = [.. Methods.Select(m => new BrotliSink(m.Quality, m.OneStream))];
            _writing = new TimeSpan[Methods.Length];
        }

        public ScreenTileDecoder Decoder { get; } = new();

        /// <summary>Images avec des copies d'écran, dans le format actuel réencodé.</summary>
        public int Moved => _tiles[_tilesOf[0]].Moved;

        public long Bytes(int method) => _sinks[method].Bytes;

        public TimeSpan Time(int method) => _sinks[method].Time + _writing[method] + _tiles[_tilesOf[method]].Time;

        public void Add(DateTime time, ScreenFrameHeader header)
        {
            if (header.Pause != ScreenPause.None)
            {
                // Marque de pause : en-tête seul, même poids pour toutes les méthodes.
                foreach (var sink in _sinks)
                {
                    sink.Mark();
                }

                return;
            }

            // Écran relu, en lignes de tuiles entières (bords à zéro), partagé par les réencodages.
            int width = header.Width, height = header.Height, pitch = Pitch(width);
            if (width != _width || height != _height)
            {
                (_width, _height) = (width, height);
                _screen = new uint[pitch * Pitch(height)];
            }

            for (int y = 0; y < height; y++)
            {
                Decoder.Pixels.AsSpan(y * width, width).CopyTo(_screen.AsSpan(y * pitch));
            }

            var frames = new TileFrame[_tiles.Length];
            for (int i = 0; i < _tiles.Length; i++)
            {
                frames[i] = _tiles[i].Encode(_screen, header, Decoder.Moves, time);
            }

            _bodies.Clear();
            var clock = new Stopwatch();
            for (int i = 0; i < Methods.Length; i++)
            {
                var frame = frames[_tilesOf[i]];
                if (!_bodies.TryGetValue((_tilesOf[i], Methods[i].Format), out var body))
                {
                    clock.Restart();
                    var data = Write(frame, Methods[i].Format);
                    body = (data, clock.Elapsed);
                    _bodies[(_tilesOf[i], Methods[i].Format)] = body;
                }

                _writing[i] += body.Time;
                _sinks[i].Frame(body.Body, frame.Key);
            }
        }

        public void Dispose()
        {
            foreach (var sink in _sinks)
            {
                sink.Dispose();
            }
        }
    }

    /// <summary>Largeur (ou hauteur) arrondie à des tuiles entières.</summary>
    private static int Pitch(int side) => (side + TileSize - 1) / TileSize * TileSize;

    /// <summary>
    /// Réencodage en tuiles comme <see cref="ScreenTileEncoder"/>, sans seuil de bruit (déjà appliqué à l'enregistrement),
    /// avec ses copies d'écran et ses images clés ou d'autres ; vérifié sans perte à chaque image.
    /// </summary>
    private sealed class Tiles(MoveMode moves, KeyMode keys)
    {
        private readonly Dictionary<UInt128, int> _ids = [];
        private readonly Stopwatch _clock = new();
        private int _nextId;
        private int _width;
        private int _height;
        private int _columns;
        private int _rows;
        private uint[] _shown = [];
        private uint[] _predicted = [];
        private DateTime _lastKey;

        public int Moved { get; private set; }

        public TimeSpan Time => _clock.Elapsed;

        public TileFrame Encode(uint[] current, ScreenFrameHeader header, IReadOnlyList<ScreenMove> recorded, DateTime time)
        {
            _clock.Start();
            int width = header.Width, height = header.Height;
            bool key = keys switch
            {
                KeyMode.Recorded => header.Key,
                KeyMode.HalfHour => time - _lastKey >= ScreenTileEncoder.KeyFrameMaxInterval,
                _ => false,
            };
            if (width != _width || height != _height)
            {
                (_width, _height) = (width, height);
                _columns = Pitch(width) / TileSize;
                _rows = Pitch(height) / TileSize;
                _shown = new uint[current.Length];
                key = true;
            }

            if (key)
            {
                // Le cache repart de l'image clé, comme dans l'enregistrement.
                _ids.Clear();
                _lastKey = time;
            }

            int pitch = _columns * TileSize;
            var applied = key || moves == MoveMode.None ? [] : header.Format == 2 ? Scroll(current, pitch) : recorded.ToList();
            foreach (var move in applied)
            {
                move.Apply<uint>(_shown, pitch);
            }

            Moved += applied.Count > 0 ? 1 : 0;
            var placements = new List<(int Index, int Id)>();
            var fresh = new List<(uint Base, uint[] Tile, uint[]? Before)>();
            for (int index = 0; index < _rows * _columns; index++)
            {
                int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
                int differences = Differences(current, _shown, origin, pitch);
                if (differences == 0 && !key)
                {
                    continue;
                }

                var tile = Tile(current, origin, pitch);
                var hash = BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(MemoryMarshal.AsBytes(tile.AsSpan())));
                if (!_ids.TryGetValue(hash, out int id))
                {
                    id = _nextId++;
                    _ids[hash] = id;
                    fresh.Add(!key && differences <= ScreenTileEncoder.DeltaMaxPixels
                        ? ((uint)index, tile, Tile(_shown, origin, pitch))
                        : (ScreenTileEncoder.FullTile, tile, null));
                }

                placements.Add((index, id));
                for (int y = 0; y < TileSize; y++)
                {
                    tile.AsSpan(y * TileSize, TileSize).CopyTo(_shown.AsSpan(origin + (y * pitch)));
                }
            }

            // Sans perte, vérifié : l'écran qu'aurait le lecteur après cette image est l'image relue, au pixel près.
            if (!_shown.AsSpan().SequenceEqual(current))
            {
                throw new InvalidOperationException("Screen re-encoding is not lossless.");
            }

            _clock.Stop();
            return new TileFrame(key, applied, placements, fresh);
        }

        /// <summary>
        /// Défilement reconnu (enregistrement d'avant la 0.42, sans copie enregistrée), gardé s'il laisse moins de tuiles
        /// modifiées, comme le fait l'encodeur sans le client FreeRDP.
        /// </summary>
        private List<ScreenMove> Scroll(uint[] current, int pitch)
        {
            var candidates = ScreenScroll.Detect<uint>(_shown, current, _width, _height, pitch)
                .Select(c => new ScreenMove(c.Left, c.Top + c.Shift, c.Width, c.Height, c.Left, c.Top))
                .Where(m => m.FitsIn(_width, _height))
                .Take(ScreenTileEncoder.MaxMoves)
                .ToList();
            if (candidates.Count == 0)
            {
                return [];
            }

            if (_predicted.Length != _shown.Length)
            {
                _predicted = new uint[_shown.Length];
            }

            _shown.CopyTo(_predicted, 0);
            foreach (var move in candidates)
            {
                move.Apply<uint>(_predicted, pitch);
            }

            int without = 0, with = 0;
            for (int index = 0; index < _rows * _columns; index++)
            {
                int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
                without += Differences(current, _shown, origin, pitch) > 0 ? 1 : 0;
                with += Differences(current, _predicted, origin, pitch) > 0 ? 1 : 0;
            }

            return with < without ? candidates : [];
        }

        private static uint[] Tile(uint[] screen, int origin, int pitch)
        {
            var tile = new uint[TilePixels];
            for (int y = 0; y < TileSize; y++)
            {
                screen.AsSpan(origin + (y * pitch), TileSize).CopyTo(tile.AsSpan(y * TileSize));
            }

            return tile;
        }

        private static int Differences(uint[] a, uint[] b, int origin, int pitch)
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
                    count += rowA[x] != rowB[x] ? 1 : 0;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Contenu d'une image (avant compression), comme celui de <see cref="ScreenTileEncoder"/> : copies, positions, bases,
    /// puis, sauf pour <see cref="TileFormat.Frame"/>, la façon de chaque tuile nouvelle (un octet), puis les tuiles.
    /// Relu et comparé à l'image avant d'être compté.
    /// </summary>
    private static byte[] Write(TileFrame frame, TileFormat format)
    {
        var output = new ArrayBufferWriter<byte>();
        foreach (var move in frame.Moves)
        {
            foreach (int n in (ReadOnlySpan<int>)[move.SourceX, move.SourceY, move.Width, move.Height, move.X, move.Y])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.GetSpan(2), (ushort)n);
                output.Advance(2);
            }
        }

        foreach (var (index, id) in frame.Placements)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(4), (uint)index);
            BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(8)[4..], (uint)id);
            output.Advance(8);
        }

        foreach (var (origin, _, _) in frame.Fresh)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(4), origin);
            output.Advance(4);
        }

        // Toute l'image en 16 bits si toutes ses tuiles nouvelles le permettent (format actuel), sinon tuile par tuile.
        bool wide = frame.Fresh.Exists(f => !Exact565(f.Tile));
        var modes = frame.Fresh.Select(f => format switch
        {
            TileFormat.Frame => wide ? Wide : Narrow,
            TileFormat.Palette when Colors(f.Tile, f.Before).Count <= PaletteMax => Palette,
            _ => Exact565(f.Tile) ? Narrow : Wide,
        }).ToArray();
        int modesAt = output.WrittenCount;
        if (format != TileFormat.Frame)
        {
            output.Write(modes);
        }

        int tilesAt = output.WrittenCount;
        for (int i = 0; i < frame.Fresh.Count; i++)
        {
            WriteTile(output, frame.Fresh[i].Tile, frame.Fresh[i].Before, modes[i]);
        }

        var body = output.WrittenSpan.ToArray();
        Verify(body, frame, format == TileFormat.Frame ? modes : body.AsSpan(modesAt, modes.Length).ToArray(), tilesAt);
        return body;
    }

    /// <summary>Valeurs à garder d'une tuile : ses pixels, ou leur différence (XOR) avec l'écran d'avant.</summary>
    private static uint Value(uint[] tile, uint[]? before, int i) => before is null ? tile[i] : tile[i] ^ before[i];

    /// <summary>Valeurs différentes d'une tuile (au plus <see cref="PaletteMax"/> + 1), dans l'ordre où elles paraissent.</summary>
    private static Dictionary<uint, int> Colors(uint[] tile, uint[]? before)
    {
        var colors = new Dictionary<uint, int>();
        for (int i = 0; i < TilePixels && colors.Count <= PaletteMax; i++)
        {
            colors.TryAdd(Value(tile, before, i), colors.Count);
        }

        return colors;
    }

    /// <summary>Bits par pixel d'une palette de <paramref name="count"/> couleurs.</summary>
    private static int Bits(int count) => count switch { <= 1 => 0, <= 2 => 1, <= 4 => 2, <= 16 => 4, _ => 8 };

    private static void WriteTile(ArrayBufferWriter<byte> output, uint[] tile, uint[]? before, byte mode)
    {
        switch (mode)
        {
            case Narrow:
            {
                // RGB565 : différence dans cet espace, comme le format actuel.
                var target = output.GetSpan(2 * TilePixels);
                for (int i = 0; i < TilePixels; i++)
                {
                    ushort v = ScreenTileEncoder.To565(tile[i]);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[(2 * i)..], before is null ? v : (ushort)(v ^ ScreenTileEncoder.To565(before[i])));
                }

                output.Advance(2 * TilePixels);
                break;
            }

            case Wide:
            {
                var target = output.GetSpan(3 * TilePixels);
                for (int i = 0; i < TilePixels; i++)
                {
                    uint v = Value(tile, before, i);
                    target[3 * i] = (byte)v;
                    target[(3 * i) + 1] = (byte)(v >> 8);
                    target[(3 * i) + 2] = (byte)(v >> 16);
                }

                output.Advance(3 * TilePixels);
                break;
            }

            default:
            {
                // Palette : nombre de couleurs moins un, couleurs (3 octets), puis l'indice de chaque pixel, octets remplis
                // depuis le bit de poids fort.
                var colors = Colors(tile, before);
                int bits = Bits(colors.Count), length = 1 + (3 * colors.Count) + (bits * TilePixels / 8);
                var target = output.GetSpan(length)[..length];
                target.Clear();
                target[0] = (byte)(colors.Count - 1);
                foreach (var (color, index) in colors)
                {
                    target[1 + (3 * index)] = (byte)color;
                    target[2 + (3 * index)] = (byte)(color >> 8);
                    target[3 + (3 * index)] = (byte)(color >> 16);
                }

                var indices = target[(1 + (3 * colors.Count))..];
                for (int i = 0; bits > 0 && i < TilePixels; i++)
                {
                    int bit = i * bits;
                    indices[bit / 8] |= (byte)(colors[Value(tile, before, i)] << (8 - bits - (bit % 8)));
                }

                output.Advance(length);
                break;
            }
        }
    }

    /// <summary>
    /// Relit les tuiles nouvelles écrites (à partir de <paramref name="tilesAt"/>, selon <paramref name="modes"/>) et les
    /// compare à celles de l'image : une façon d'écrire qui perdrait quelque chose arrête la mesure.
    /// </summary>
    private static void Verify(byte[] body, TileFrame frame, byte[] modes, int tilesAt)
    {
        int at = tilesAt;
        var tile = new uint[TilePixels];
        for (int t = 0; t < frame.Fresh.Count; t++)
        {
            var (_, expected, before) = frame.Fresh[t];
            switch (modes[t])
            {
                case Narrow:
                    for (int i = 0; i < TilePixels; i++)
                    {
                        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + (2 * i)));
                        tile[i] = ScreenTileEncoder.From565(before is null ? v : (ushort)(v ^ ScreenTileEncoder.To565(before[i])));
                    }

                    at += 2 * TilePixels;
                    break;
                case Wide:
                    for (int i = 0; i < TilePixels; i++)
                    {
                        uint v = body[at + (3 * i)] | ((uint)body[at + (3 * i) + 1] << 8) | ((uint)body[at + (3 * i) + 2] << 16);
                        tile[i] = before is null ? v : v ^ before[i];
                    }

                    at += 3 * TilePixels;
                    break;
                default:
                    int count = body[at] + 1, bits = Bits(count);
                    int indices = at + 1 + (3 * count);
                    for (int i = 0; i < TilePixels; i++)
                    {
                        int bit = i * bits;
                        int index = bits == 0 ? 0 : (body[indices + (bit / 8)] >> (8 - bits - (bit % 8))) & ((1 << bits) - 1);
                        uint v = index < count
                            ? body[at + 1 + (3 * index)] | ((uint)body[at + 2 + (3 * index)] << 8) | ((uint)body[at + 3 + (3 * index)] << 16)
                            : uint.MaxValue;
                        tile[i] = before is null ? v : v ^ before[i];
                    }

                    at = indices + (bits * TilePixels / 8);
                    break;
            }

            if (!tile.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException("Screen tile writing is not lossless.");
            }
        }

        if (at != body.Length)
        {
            throw new InvalidOperationException("Screen tile writing is not lossless.");
        }
    }

    /// <summary>Tuile dont chaque pixel est un pixel 16 bits élargi.</summary>
    private static bool Exact565(uint[] tile) => Array.TrueForAll(tile, p => ScreenTileEncoder.From565(ScreenTileEncoder.To565(p)) == p);

    /// <summary>
    /// Un flux Brotli d'une image clé à la suivante (ou pour tout l'enregistrement, <c>oneStream</c>), vidé après chaque
    /// image, comme <see cref="ScreenTileEncoder"/> : octets écrits (en-têtes compris) et temps pris.
    /// </summary>
    private sealed class BrotliSink(int quality, bool oneStream) : IDisposable
    {
        private readonly Stopwatch _clock = new();
        private readonly byte[] _buffer = new byte[1 << 20];
        private BrotliEncoder _encoder;
        private bool _open;

        public long Bytes { get; private set; }

        public TimeSpan Time => _clock.Elapsed;

        /// <summary>Marque de pause : en-tête seul.</summary>
        public void Mark() => Bytes += ScreenFrameHeader.Length3;

        public void Frame(byte[] body, bool key)
        {
            _clock.Start();
            if (!_open || (key && !oneStream))
            {
                // Comme l'encodeur : le flux précédent n'est pas terminé, le nouveau repart de l'image clé.
                Dispose();
                _encoder = new BrotliEncoder(quality, ScreenTileEncoder.BrotliWindow);
                _open = true;
            }

            long written = 0;
            ReadOnlySpan<byte> source = body;
            OperationStatus status;
            do
            {
                status = Check(_encoder.Compress(source, _buffer, out int consumed, out int count, isFinalBlock: false));
                source = source[consumed..];
                written += count;
            }
            while (status == OperationStatus.DestinationTooSmall || source.Length > 0);

            do
            {
                status = Check(_encoder.Flush(_buffer, out int count));
                written += count;
            }
            while (status == OperationStatus.DestinationTooSmall);

            Bytes += ScreenFrameHeader.Length3 + written;
            _clock.Stop();
        }

        public void Dispose()
        {
            if (_open)
            {
                _encoder.Dispose();
                _open = false;
            }
        }

        private static OperationStatus Check(OperationStatus status) =>
            status == OperationStatus.InvalidData ? throw new InvalidOperationException("Brotli compression failed.") : status;
    }
}
