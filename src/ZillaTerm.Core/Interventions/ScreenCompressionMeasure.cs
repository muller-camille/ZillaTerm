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
/// <param name="Duration">De la première à la dernière image.</param>
/// <param name="Scrolled">Images où un défilement a été reconnu.</param>
/// <param name="Methods">Poids réencodé, la méthode actuelle d'abord (contrôle : proche de <paramref name="JournalBytes"/>).</param>
public sealed record ScreenCompressionResult(
    int Streams, int Frames, int KeyFrames, int Lost, long JournalBytes, TimeSpan Duration, int Scrolled,
    IReadOnlyList<ScreenCompressionMethod> Methods)
{
    /// <summary>Lignes du journal de débogage : le bilan, puis une ligne par méthode (écart avec la méthode actuelle).</summary>
    public IEnumerable<string> Lines()
    {
        var c = CultureInfo.InvariantCulture;
        var lost = Lost > 0 ? string.Create(c, $", {Lost} illisible(s) ou perdue(s)") : "";
        yield return string.Create(c,
            $"{Streams} écran(s), {Frames} image(s) dont {KeyFrames} complète(s){lost}, sur {Duration.TotalMinutes:0.#} min ; journal {ScreenRecordingStats.Bytes(JournalBytes)}");
        long reference = Methods.Count > 0 ? Methods[0].Bytes : 0;
        for (int i = 0; i < Methods.Count; i++)
        {
            var method = Methods[i];
            var gain = i > 0 && reference > 0 ? string.Create(c, $" ({100.0 * (method.Bytes - reference) / reference:+0;-0} %)") : "";
            yield return string.Create(c, $"  {method.Name} : {ScreenRecordingStats.Bytes(method.Bytes)}{gain}, {method.Time.TotalSeconds:0.0} s de calcul");
        }

        int images = Frames - Lost;
        yield return string.Create(c, $"  défilement reconnu sur {Scrolled} image(s){(images > 0 ? $" ({100.0 * Scrolled / images:0.#} %)" : "")}");
    }
}

/// <summary>
/// Ce que pèserait l'écran d'un enregistrement existant avec d'autres façons de l'encoder, toutes sans perte : chaque
/// image relue (<see cref="ScreenTileDecoder"/>) est réencodée en tuiles comme aujourd'hui, avec ou sans défilement
/// reconnu (<see cref="ScreenScroll"/>), puis compressée en zlib image par image (comme aujourd'hui) ou en Brotli d'une
/// image clé à la suivante (chaque image compressée avec les précédentes en mémoire). Pour le journal de débogage : rien
/// n'est écrit, l'écran relu ne quitte pas la mémoire.
/// </summary>
/// <remarks>
/// Les images clés restent celles de l'enregistrement, pour comparer à poids d'images clés égal. La méthode actuelle
/// réencodée retrouve le poids du journal, au seuil de bruit près (déjà appliqué à l'enregistrement, il ne l'est plus).
/// </remarks>
public static class ScreenCompressionMeasure
{
    /// <summary>Fenêtre de Brotli (2^24 : 16 Mo), assez pour une image clé d'un écran 4K en 16 bits.</summary>
    private const int BrotliWindow = 24;

    public static ScreenCompressionResult Measure(IEnumerable<InterventionEntry> entries, CancellationToken cancel = default)
    {
        var streams = new Dictionary<int, ScreenStream>();
        int frames = 0, keys = 0, lost = 0;
        long journal = 0;
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

            keys += header.Key ? 1 : 0;
            stream.Add(header, stream.Decoder.Pixels);
        }

        foreach (var stream in streams.Values)
        {
            stream.End();
        }

        var methods = new List<ScreenCompressionMethod>();
        for (int i = 0; i < ScreenStream.Methods.Length; i++)
        {
            long bytes = 0;
            var time = TimeSpan.Zero;
            foreach (var stream in streams.Values)
            {
                var (sink, tiles) = stream.Sink(i);
                bytes += sink.Bytes;
                time += sink.Time + tiles.Time;
            }

            methods.Add(new ScreenCompressionMethod(ScreenStream.Methods[i].Name, bytes, time));
        }

        int scrolled = streams.Values.Sum(stream => stream.Scrolled);
        return new ScreenCompressionResult(streams.Count, frames, keys, lost, journal, (last - first) ?? TimeSpan.Zero, scrolled, methods);
    }

    /// <summary>Un enregistrement d'écran : sa relecture, ses deux encodages en tuiles et leurs compressions.</summary>
    private sealed class ScreenStream
    {
        public static readonly (string Name, bool Scroll, int Brotli)[] Methods =
        [
            ("méthode actuelle (zlib par image, contrôle)", false, 0),
            ("Brotli 5 en flux", false, 5),
            ("Brotli 7 en flux", false, 7),
            ("défilement + zlib par image", true, 0),
            ("défilement + Brotli 5 en flux", true, 5),
            ("défilement + Brotli 7 en flux", true, 7),
        ];

        private readonly Tiles _plain = new(scroll: false);
        private readonly Tiles _scroll = new(scroll: true);
        private readonly Sink[] _sinks = [.. Methods.Select(m => m.Brotli == 0 ? (Sink)new ZlibSink() : new BrotliSink(m.Brotli))];

        public ScreenTileDecoder Decoder { get; } = new();

        public int Scrolled => _scroll.Scrolled;

        public void Add(ScreenFrameHeader header, ushort[] pixels)
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

            var plain = _plain.Encode(pixels, header.Width, header.Height, header.Key);
            var scroll = _scroll.Encode(pixels, header.Width, header.Height, header.Key);
            for (int i = 0; i < _sinks.Length; i++)
            {
                _sinks[i].Frame(Methods[i].Scroll ? scroll : plain, header.Key);
            }
        }

        public void End()
        {
            foreach (var sink in _sinks)
            {
                sink.End();
            }
        }

        public (Sink Sink, Tiles Tiles) Sink(int method) => (_sinks[method], Methods[method].Scroll ? _scroll : _plain);
    }

    /// <summary>
    /// Image encodée en tuiles comme <see cref="ScreenTileEncoder"/> (positions, bases, tuiles nouvelles), sans seuil de
    /// bruit, précédée des copies de défilement si <c>scroll</c> ; non compressée.
    /// </summary>
    private sealed class Tiles(bool scroll)
    {
        private const int TileSize = ScreenTileEncoder.TileSize;
        private const int TilePixels = TileSize * TileSize;

        private readonly Dictionary<UInt128, int> _ids = [];
        private readonly Stopwatch _clock = new();
        private int _nextId;
        private int _width;
        private int _height;
        private int _columns;
        private int _rows;
        private ushort[] _current = [];
        private ushort[] _shown = [];

        public int Scrolled { get; private set; }

        public TimeSpan Time => _clock.Elapsed;

        public byte[] Encode(ushort[] pixels, int width, int height, bool key)
        {
            _clock.Start();
            if (width != _width || height != _height)
            {
                (_width, _height) = (width, height);
                _columns = (width + TileSize - 1) / TileSize;
                _rows = (height + TileSize - 1) / TileSize;
                _current = new ushort[_columns * TileSize * _rows * TileSize];
                _shown = new ushort[_current.Length];
                key = true;
            }

            int pitch = _columns * TileSize;
            for (int y = 0; y < height; y++)
            {
                pixels.AsSpan(y * width, width).CopyTo(_current.AsSpan(y * pitch));
            }

            var output = new MemoryStream();
            Span<byte> value = stackalloc byte[8];
            if (scroll)
            {
                var copies = key ? [] : ScreenScroll.Detect(_shown, _current, width, height, pitch);
                Scrolled += copies.Count > 0 ? 1 : 0;
                BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)copies.Count);
                output.Write(value[..2]);
                foreach (var copy in copies)
                {
                    ScreenScroll.Apply(_shown, pitch, copy);
                    foreach (int number in (ReadOnlySpan<int>)[copy.Left, copy.Top, copy.Width, copy.Height, copy.Shift])
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(value, (short)number);
                        output.Write(value[..2]);
                    }
                }
            }

            if (key)
            {
                _ids.Clear();
            }

            var placements = new List<(int Index, int Id)>();
            var bases = new List<uint>();
            var fresh = new MemoryStream();
            var tile = new ushort[TilePixels];
            var delta = new ushort[TilePixels];
            for (int index = 0; index < _rows * _columns; index++)
            {
                int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
                int differences = Differences(origin, pitch);
                if (differences == 0 && !key)
                {
                    continue;
                }

                for (int y = 0; y < TileSize; y++)
                {
                    _current.AsSpan(origin + (y * pitch), TileSize).CopyTo(tile.AsSpan(y * TileSize));
                }

                var hash = BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(MemoryMarshal.AsBytes(tile.AsSpan())));
                if (!_ids.TryGetValue(hash, out int id))
                {
                    id = _nextId++;
                    _ids[hash] = id;
                    if (!key && differences <= ScreenTileEncoder.DeltaMaxPixels)
                    {
                        for (int y = 0; y < TileSize; y++)
                        {
                            var before = _shown.AsSpan(origin + (y * pitch), TileSize);
                            for (int x = 0; x < TileSize; x++)
                            {
                                delta[(y * TileSize) + x] = (ushort)(tile[(y * TileSize) + x] ^ before[x]);
                            }
                        }

                        bases.Add((uint)index);
                        fresh.Write(MemoryMarshal.AsBytes(delta.AsSpan()));
                    }
                    else
                    {
                        bases.Add(ScreenTileEncoder.FullTile);
                        fresh.Write(MemoryMarshal.AsBytes(tile.AsSpan()));
                    }
                }

                placements.Add((index, id));
                for (int y = 0; y < TileSize; y++)
                {
                    tile.AsSpan(y * TileSize, TileSize).CopyTo(_shown.AsSpan(origin + (y * pitch)));
                }
            }

            foreach (var (index, id) in placements)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)index);
                BinaryPrimitives.WriteUInt32LittleEndian(value[4..], (uint)id);
                output.Write(value);
            }

            foreach (uint origin in bases)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, origin);
                output.Write(value[..4]);
            }

            fresh.Position = 0;
            fresh.CopyTo(output);
            // Sans perte, vérifié : l'écran qu'aurait le lecteur après cette image est l'image relue, au pixel près.
            if (!_shown.AsSpan().SequenceEqual(_current))
            {
                throw new InvalidOperationException("Screen re-encoding is not lossless.");
            }

            _clock.Stop();
            return output.ToArray();
        }

        private int Differences(int origin, int pitch)
        {
            int count = 0;
            for (int y = 0; y < TileSize; y++)
            {
                var a = _current.AsSpan(origin + (y * pitch), TileSize);
                var b = _shown.AsSpan(origin + (y * pitch), TileSize);
                if (a.SequenceEqual(b))
                {
                    continue;
                }

                for (int x = 0; x < TileSize; x++)
                {
                    count += a[x] != b[x] ? 1 : 0;
                }
            }

            return count;
        }
    }

    /// <summary>Compression des images d'un enregistrement d'écran : octets écrits (en-têtes compris) et temps pris.</summary>
    private abstract class Sink
    {
        protected readonly Stopwatch Clock = new();

        public long Bytes { get; protected set; }

        public TimeSpan Time => Clock.Elapsed;

        public abstract void Frame(byte[] payload, bool key);

        /// <summary>Marque de pause : en-tête et contenu vide (flux zlib vide, comme aujourd'hui).</summary>
        public void Mark() => Bytes += ScreenFrameHeader.Length + 8;

        public virtual void End()
        {
        }
    }

    /// <summary>Aujourd'hui : chaque image compressée seule (zlib, taux le plus fort).</summary>
    private sealed class ZlibSink : Sink
    {
        public override void Frame(byte[] payload, bool key)
        {
            Clock.Start();
            var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(payload);
            }

            Bytes += ScreenFrameHeader.Length + output.Length;
            Clock.Stop();
        }
    }

    /// <summary>
    /// Un flux Brotli d'une image clé à la suivante, vidé après chaque image : chaque image se compresse avec les
    /// précédentes en mémoire, et se décompresse dès qu'elle est lue.
    /// </summary>
    private sealed class BrotliSink(int quality) : Sink
    {
        private readonly byte[] _buffer = new byte[1 << 20];
        private BrotliEncoder _encoder;
        private bool _open;

        public override void Frame(byte[] payload, bool key)
        {
            Clock.Start();
            if (key || !_open)
            {
                Finish();
                _encoder = new BrotliEncoder(quality, BrotliWindow);
                _open = true;
            }

            long written = 0;
            ReadOnlySpan<byte> source = payload;
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

            Bytes += ScreenFrameHeader.Length + written;
            Clock.Stop();
        }

        public override void End()
        {
            Clock.Start();
            Finish();
            Clock.Stop();
        }

        /// <summary>Fin du flux en cours (quelques octets).</summary>
        private void Finish()
        {
            if (!_open)
            {
                return;
            }

            OperationStatus status;
            do
            {
                status = Check(_encoder.Compress(ReadOnlySpan<byte>.Empty, _buffer, out _, out int count, isFinalBlock: true));
                Bytes += count;
            }
            while (status == OperationStatus.DestinationTooSmall);

            _encoder.Dispose();
            _open = false;
        }

        private static OperationStatus Check(OperationStatus status) =>
            status == OperationStatus.InvalidData ? throw new InvalidOperationException("Brotli compression failed.") : status;
    }
}
