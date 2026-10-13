using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed partial class ScreenTilesTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 13, 14, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-screen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Écran BGRA : fond uni, quelques motifs de 8×8 (texte simulé) qui se répètent.</summary>
    private static byte[] Screen(int width, int height, Func<int, int, (byte R, byte G, byte B)>? paint = null)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = paint?.Invoke(x, y) ?? ((x / 8 + y / 8) % 7 == 0 ? ((byte)20, (byte)20, (byte)20) : ((byte)240, (byte)244, (byte)248));
                int o = ((y * width) + x) * 4;
                pixels[o] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    private static ushort[] Rgb565(byte[] bgra) =>
        Enumerable.Range(0, bgra.Length / 4).Select(i => (ushort)(((bgra[(4 * i) + 2] >> 3) << 11) | ((bgra[(4 * i) + 1] >> 2) << 5) | (bgra[4 * i] >> 3))).ToArray();

    private static void Paint(byte[] bgra, int width, int x0, int y0, int w, int h, byte value)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int o = ((y * width) + x) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = value;
            }
        }
    }

    /// <summary>
    /// Première image : image clé avec toutes les tuiles (bords complétés), tuiles identiques gardées une fois, relue au
    /// pixel près en 16 bits ; la même image ensuite ne donne rien.
    /// </summary>
    [Fact]
    public void FirstFrameIsAKeyFrameThatDecodesExactly()
    {
        const int width = 200, height = 130;
        var screen = Screen(width, height, (_, _) => (240, 244, 248));
        var encoder = new ScreenTileEncoder(stream: 7);
        var frame = encoder.Encode(T0, screen, width, height, width * 4)!;

        Assert.True(frame.Header.Key);
        Assert.Equal(7, frame.Header.Stream);
        Assert.Equal(0, frame.Header.Sequence);
        Assert.Equal(4 * 3, frame.Header.Placements);
        Assert.Equal(1000, frame.Header.Activity);
        // Fond uni : tuiles pleines toutes pareilles ; celles du bord (complétées de zéros) diffèrent selon leur découpe.
        Assert.InRange(frame.Header.NewTiles, 1, 4);
        Assert.True(frame.Data.Length < 1500, $"{frame.Data.Length} octets");

        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(frame.Data));
        Assert.Equal(Rgb565(screen), decoder.Pixels);
        Assert.Null(encoder.Encode(T0.AddSeconds(1), screen, width, height, width * 4));
    }

    /// <summary>
    /// Seuil de bruit : quelques pixels changés (curseur) ne sont pas gardés, mais ils comptent quand d'autres s'y ajoutent ;
    /// une boîte de dialogue ne garde que les tuiles qu'elle touche, et sa fermeture reprend les tuiles du cache.
    /// </summary>
    [Fact]
    public void OnlyChangedTilesAreKeptAndSeenTilesAreReused()
    {
        const int width = 320, height = 192;
        var before = Screen(width, height);
        var encoder = new ScreenTileEncoder(stream: 1);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(encoder.Encode(T0, before, width, height, width * 4)!.Data));

        // Curseur texte : 2 × 16 pixels.
        var caret = (byte[])before.Clone();
        Paint(caret, width, 70, 70, 2, 16, 0);
        Assert.Null(encoder.Encode(T0.AddSeconds(1), caret, width, height, width * 4));
        // Un mot tapé à côté, dans la même tuile : 40 pixels de plus, la tuile est gardée.
        Paint(caret, width, 80, 70, 4, 10, 0);
        var typed = encoder.Encode(T0.AddSeconds(2), caret, width, height, width * 4)!;
        Assert.False(typed.Header.Key);
        Assert.Equal(1, typed.Header.Placements);
        Assert.True(decoder.Apply(typed.Data));
        Assert.Equal(Rgb565(caret), decoder.Pixels);

        // Boîte de dialogue sur 2 × 2 tuiles.
        var dialog = (byte[])caret.Clone();
        Paint(dialog, width, 128, 64, 128, 128, 200);
        var opened = encoder.Encode(T0.AddSeconds(3), dialog, width, height, width * 4)!;
        Assert.Equal(4, opened.Header.Placements);
        Assert.Equal(1, opened.Header.NewTiles);
        Assert.Equal(4 * 1000 / 15, opened.Header.Activity);
        Assert.True(decoder.Apply(opened.Data));
        Assert.Equal(Rgb565(dialog), decoder.Pixels);

        // Fermée : l'écran d'avant revient, ses tuiles sont déjà connues.
        var closed = encoder.Encode(T0.AddSeconds(4), caret, width, height, width * 4)!;
        Assert.Equal(4, closed.Header.Placements);
        Assert.Equal(0, closed.Header.NewTiles);
        Assert.True(decoder.Apply(closed.Data));
        Assert.Equal(Rgb565(caret), decoder.Pixels);
    }

    /// <summary>
    /// Images clés : pas sur un écran immobile, même longtemps ; à la demande et après un changement de taille ; au plus
    /// tard 30 minutes après la précédente si l'écran a changé ; plus tôt (5 minutes au moins) quand les images depuis
    /// pèsent 4 fois l'image clé. Le cache repart de chacune.
    /// </summary>
    [Fact]
    public void KeyFramesFollowTheActivity()
    {
        const int width = 128, height = 128;
        var screen = Screen(width, height);
        var encoder = new ScreenTileEncoder(stream: 2);
        var first = encoder.Encode(T0, screen, width, height, width * 4)!;
        Assert.True(first.Header.Key);
        Assert.Null(encoder.Encode(T0 + TimeSpan.FromHours(2), screen, width, height, width * 4));

        var asked = encoder.Encode(T0 + TimeSpan.FromHours(2), screen, width, height, width * 4, key: true)!;
        Assert.True(asked.Header.Key);
        Assert.Equal(4, asked.Header.Placements);
        Assert.Equal(first.Header.NewTiles, asked.Header.NewTiles);
        Assert.Equal(0, asked.Header.Activity);
        Assert.Equal(1, asked.Header.Sequence);

        // Un petit changement, puis plus rien : image clé 30 minutes après la précédente, pas avant.
        var start = T0 + TimeSpan.FromHours(2);
        var changed = (byte[])screen.Clone();
        Paint(changed, width, 0, 0, 20, 20, 0);
        Assert.False(encoder.Encode(start.AddSeconds(1), changed, width, height, width * 4)!.Header.Key);
        Assert.Null(encoder.Encode(start + ScreenTileEncoder.KeyFrameMaxInterval - TimeSpan.FromSeconds(1), changed, width, height, width * 4));
        var late = encoder.Encode(start + ScreenTileEncoder.KeyFrameMaxInterval, changed, width, height, width * 4)!;
        Assert.True(late.Header.Key);
        Assert.Equal(0, late.Header.Activity);

        // Écran très actif (bruit) : images clés selon leur poids, au plus tôt 5 minutes après la précédente.
        var noisy = new ScreenTileEncoder(stream: 3);
        var random = new Random(3);
        var pixels = new byte[width * height * 4];
        Assert.True(noisy.Encode(T0, Screen(width, height, (_, _) => (240, 244, 248)), width, height, width * 4)!.Header.Key);
        var keys = new List<int>();
        for (int second = 1; second <= 11 * 60; second++)
        {
            random.NextBytes(pixels);
            if (noisy.Encode(T0.AddSeconds(second), pixels, width, height, width * 4)!.Header.Key)
            {
                keys.Add(second);
            }
        }

        Assert.Equal([300, 600], keys);

        var wider = Screen(192, 128);
        var resized = encoder.Encode(start + TimeSpan.FromHours(1), wider, 192, 128, 192 * 4)!;
        Assert.True(resized.Header.Key);
        Assert.Equal(6, resized.Header.Placements);
        Assert.False(ScreenFrameHeader.TryRead("pas une image"u8, out _));
        // En-tête trafiqué : écran de 65535 × 65535 (grille d'un million de tuiles) refusé.
        var huge = (byte[])resized.Data.Clone();
        huge[12] = huge[13] = huge[14] = huge[15] = 0xFF;
        Assert.False(ScreenFrameHeader.TryRead(huge, out _));
        Assert.Throws<ArgumentException>(() => encoder.Encode(T0, new byte[10], 192, 128, 192 * 4));
    }

    /// <summary>
    /// Rattrapage : un caractère tapé sous le seuil de bruit est gardé une fois la tuile immobile 3 secondes ; un curseur
    /// qui clignote seul ne coûte rien ; à côté d'un caractère tapé, la tuile est gardée au plus tard après une minute.
    /// </summary>
    [Fact]
    public void ResidueBelowTheNoiseThresholdIsCaughtUp()
    {
        const int width = 192, height = 128;
        var screen = Screen(width, height, (_, _) => (240, 244, 248));
        var encoder = new ScreenTileEncoder(stream: 4);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(encoder.Encode(T0, screen, width, height, width * 4)!.Data));

        // « y » : 5 × 5 pixels, sous le seuil.
        Paint(screen, width, 10, 10, 5, 5, 0);
        Assert.Null(encoder.Encode(T0.AddSeconds(1), screen, width, height, width * 4));
        Assert.Null(encoder.Encode(T0.AddSeconds(2), screen, width, height, width * 4));
        Assert.Null(encoder.Encode(T0.AddSeconds(3), screen, width, height, width * 4));
        var caughtUp = encoder.Encode(T0.AddSeconds(4), screen, width, height, width * 4)!;
        Assert.False(caughtUp.Header.Key);
        Assert.Equal(1, caughtUp.Header.Placements);
        Assert.True(decoder.Apply(caughtUp.Data));
        Assert.Equal(Rgb565(screen), decoder.Pixels);
        Assert.Null(encoder.Encode(T0.AddSeconds(10), screen, width, height, width * 4));

        // Curseur seul qui clignote, dans une autre tuile : un de ses deux états est l'écran gardé, rien à garder.
        var blink = (byte[])screen.Clone();
        Paint(blink, width, 100, 10, 1, 12, 0);
        for (int second = 11; second <= 80; second++)
        {
            Assert.Null(encoder.Encode(T0.AddSeconds(second), second % 2 == 0 ? blink : screen, width, height, width * 4));
        }

        // Caractère tapé juste avant le curseur qui clignote : la tuile ne s'immobilise jamais, gardée après 60 s.
        var off = (byte[])screen.Clone();
        Paint(off, width, 90, 10, 5, 5, 0);
        var on = (byte[])off.Clone();
        Paint(on, width, 100, 10, 1, 6, 0);
        var frames = new List<int>();
        for (int second = 81; second <= 220; second++)
        {
            var image = second % 2 == 0 ? on : off;
            if (encoder.Encode(T0.AddSeconds(second), image, width, height, width * 4) is { } frame)
            {
                frames.Add(second);
                Assert.True(decoder.Apply(frame.Data));
                Assert.Equal(Rgb565(image), decoder.Pixels);
            }
        }

        // Reste en attente depuis 80 s (dernier clignotement seul) : gardé à 140 s.
        Assert.Equal([140], frames);
    }

    /// <summary>
    /// Différence : sur un fond qui ne se compresse pas, un caractère tapé ne coûte que quelques octets (la tuile entière en
    /// coûterait 8 Ko) ; une tuile beaucoup modifiée est gardée entière ; l'écran relu est exact.
    /// </summary>
    [Fact]
    public void SmallChangesAreKeptAsDifferences()
    {
        const int width = 128, height = 64;
        var random = new Random(5);
        var screen = new byte[width * height * 4];
        random.NextBytes(screen);
        var encoder = new ScreenTileEncoder(stream: 5);
        var decoder = new ScreenTileDecoder();
        var key = encoder.Encode(T0, screen, width, height, width * 4)!;
        Assert.True(key.Data.Length > 2 * 8000, $"{key.Data.Length} octets");
        Assert.True(decoder.Apply(key.Data));

        Paint(screen, width, 20, 20, 7, 12, 0);
        var typed = encoder.Encode(T0.AddSeconds(1), screen, width, height, width * 4)!;
        Assert.Equal(1, typed.Header.NewTiles);
        Assert.True(typed.Data.Length < 300, $"{typed.Data.Length} octets");
        Assert.True(decoder.Apply(typed.Data));
        Assert.Equal(Rgb565(screen), decoder.Pixels);

        // Moitié de la tuile repeinte : gardée entière.
        random.NextBytes(screen.AsSpan(0, width * 4 * 32));
        var repainted = encoder.Encode(T0.AddSeconds(2), screen, width, height, width * 4)!;
        Assert.True(decoder.Apply(repainted.Data));
        Assert.Equal(Rgb565(screen), decoder.Pixels);
    }

    /// <summary>
    /// Image manquante (bloc du journal abîmé) : la suivante ne s'applique pas, ni rien jusqu'à l'image clé suivante,
    /// qui reprend l'écran exact ; une image qui n'est pas une image clé ne se lit pas seule.
    /// </summary>
    [Fact]
    public void MissingFrameStopsDecodingUntilTheNextKeyFrame()
    {
        const int width = 128, height = 128;
        var screen = Screen(width, height);
        var encoder = new ScreenTileEncoder(stream: 6);
        var frames = new List<ScreenFrame> { encoder.Encode(T0, screen, width, height, width * 4)! };
        for (int i = 1; i <= 3; i++)
        {
            Paint(screen, width, 10 * i, 10, 8, 8, (byte)(40 * i));
            frames.Add(encoder.Encode(T0.AddSeconds(i), screen, width, height, width * 4)!);
        }

        frames.Add(encoder.Encode(T0.AddSeconds(4), screen, width, height, width * 4, key: true)!);
        Assert.Equal([0, 1, 2, 3, 4], frames.Select(f => f.Header.Sequence));
        Assert.False(new ScreenTileDecoder().Apply(frames[1].Data));

        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(frames[0].Data));
        Assert.True(decoder.Apply(frames[1].Data));
        Assert.False(decoder.Apply(frames[3].Data));
        Assert.False(decoder.Apply(frames[2].Data));
        Assert.True(decoder.Apply(frames[4].Data));
        Assert.Equal(Rgb565(screen), decoder.Pixels);
    }

    /// <summary>
    /// Journal au format 3 : captures et images d'écran en binaire après le JSON compressé (pas de base64), relues à
    /// l'identique ; les images d'écran ne comptent pas comme événements ; la taille du fichier est suivie (bridage à
    /// 100 Mo) ; le texte des terminaux prend bien moins de place qu'en clair.
    /// </summary>
    [Fact]
    public void JournalKeepsImagesInBinary()
    {
        var protector = new XorProtector();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var frame = new ScreenTileEncoder(stream: 3).Encode(T0, Screen(128, 64), 128, 64, 128 * 4)!.Data;
        string path;
        long written;
        using (var journal = InterventionJournal.Start(_dir, protector, T0))
        {
            path = journal.Path;
            Assert.Equal(8, journal.Length);
            journal.Write(new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"));
            journal.Write(new InterventionEntry(T0.AddSeconds(1), InterventionKind.Screenshot, "srv01", "Capture", png));
            journal.Write(new InterventionEntry(T0.AddSeconds(2), InterventionKind.Screen, "srv01", "", frame));
            for (int i = 0; i < 2000; i++)
            {
                journal.Write(new InterventionEntry(T0.AddSeconds(3), InterventionKind.Terminal, "root@srv01",
                    $"-rw-r--r--. 1 root root {i * 37,8} Oct 13 14:{i % 60:00} /var/log/messages-2026101{i % 10}"));
            }

            Assert.Equal(2002, journal.Count);
            journal.Flush();
            written = journal.Length;
            Assert.Equal(new FileInfo(path).Length, written);
            journal.Stop(new InterventionEntry(T0.AddMinutes(1), InterventionKind.Stopped, "", ""));
        }

        // Le JSON du bloc, compressé, ne contient que la longueur des images, pas leur base64 ; les images suivent.
        var plain = protector.Unprotect(File.ReadAllBytes(path)[12..(int)written]);
        int packed = BitConverter.ToInt32(plain, 0);
        using (var zlib = new System.IO.Compression.ZLibStream(new MemoryStream(plain, 4, packed), System.IO.Compression.CompressionMode.Decompress))
        using (var reader = new StreamReader(zlib))
        {
            var json = reader.ReadToEnd();
            Assert.Contains("\"ImageLength\":7", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(png), json, StringComparison.Ordinal);
            Assert.True(written < json.Length / 5, $"{written} octets pour {json.Length} de JSON");
        }

        Assert.Equal(png, plain[(4 + packed)..(4 + packed + png.Length)]);
        Assert.Equal("ZTINTV3\n", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 8));

        var recording = InterventionJournal.Read(path, protector);
        Assert.False(recording.Damaged);
        Assert.Equal(png, recording.Entries[1].Image);
        Assert.Equal(frame, recording.Entries[2].Image);
        Assert.Equal(InterventionKind.Screen, recording.Entries[2].Kind);
        Assert.Null(recording.Entries[0].Image);
        Assert.Equal("-rw-r--r--. 1 root root    73963 Oct 13 14:19 /var/log/messages-20261019", recording.Entries[^2].Text);
    }

    /// <summary>Journal du format 2 (JSON non compressé, images en binaire) : toujours relu.</summary>
    [Fact]
    public void FormatTwoJournalIsStillRead()
    {
        var protector = new XorProtector();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "intervention-20261013-140000.zij");
        var json = Encoding.UTF8.GetBytes(
            "{\"Id\":\"abc\",\"Sequence\":0,\"Entries\":[{\"Time\":\"2026-10-13T14:00:00Z\",\"Kind\":\"Started\",\"Source\":\"\",\"Text\":\"jdoe\"},"
            + "{\"Time\":\"2026-10-13T14:00:01Z\",\"Kind\":\"Screenshot\",\"Source\":\"srv01\",\"Text\":\"Capture\",\"ImageLength\":3}]}");
        var plain = BitConverter.GetBytes(json.Length).Concat(json).Concat(new byte[] { 7, 8, 9 }).ToArray();
        var block = protector.Protect(plain);
        using (var file = File.Create(path))
        {
            file.Write("ZTINTV2\n"u8);
            file.Write(BitConverter.GetBytes(block.Length));
            file.Write(block);
        }

        var recording = InterventionJournal.Read(path, protector);
        Assert.False(recording.Damaged);
        Assert.Equal(new byte[] { 7, 8, 9 }, recording.Entries[1].Image);
    }

    /// <summary>
    /// Bloc dont le JSON compressé dépasserait 64 Mo une fois décompressé : marqué abîmé sans être décompressé en entier,
    /// le bloc suivant se relit.
    /// </summary>
    [Fact]
    public void OversizedCompressedBlockIsRefused()
    {
        var protector = new XorProtector();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "intervention-20261013-140000.zij");
        byte[] Block(byte[] json)
        {
            var packed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(json);
            }

            var plain = BitConverter.GetBytes((int)packed.Length).Concat(packed.ToArray()).ToArray();
            var block = protector.Protect(plain);
            return BitConverter.GetBytes(block.Length).Concat(block).ToArray();
        }

        using (var file = File.Create(path))
        {
            file.Write("ZTINTV3\n"u8);
            file.Write(Block(new byte[65 * 1024 * 1024]));
            file.Write(Block(Encoding.UTF8.GetBytes(
                "{\"Id\":\"abc\",\"Sequence\":1,\"Entries\":[{\"Time\":\"2026-10-13T14:00:00Z\",\"Kind\":\"Note\",\"Source\":\"\",\"Text\":\"suite\"}]}")));
        }

        var recording = InterventionJournal.Read(path, protector);
        Assert.True(recording.Damaged);
        Assert.Equal("suite", Assert.Single(recording.Entries).Text);
    }

    /// <summary>Journal du format 1 (images en base64 dans le JSON) : toujours relu.</summary>
    [Fact]
    public void FormatOneJournalIsStillRead()
    {
        var protector = new XorProtector();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "intervention-20261013-140000.zij");
        var json = Encoding.UTF8.GetBytes(
            "{\"Id\":\"abc\",\"Sequence\":0,\"Entries\":[{\"Time\":\"2026-10-13T14:00:00Z\",\"Kind\":\"Started\",\"Source\":\"\",\"Text\":\"jdoe\"},"
            + "{\"Time\":\"2026-10-13T14:00:01Z\",\"Kind\":\"Screenshot\",\"Source\":\"srv01\",\"Text\":\"Capture\",\"Image\":\"iVBORw==\"}]}");
        var block = protector.Protect(json);
        using (var file = File.Create(path))
        {
            file.Write("ZTINTV1\n"u8);
            file.Write(BitConverter.GetBytes(block.Length));
            file.Write(block);
        }

        var recording = InterventionJournal.Read(path, protector);
        Assert.False(recording.Damaged);
        Assert.Equal(2, recording.Entries.Count);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, recording.Entries[1].Image);
    }

    [Fact]
    public void HistoryLeavesScreenFramesOut()
    {
        var history = new InterventionHistory();
        history.Add(new InterventionEntry(T0, InterventionKind.Screen, "srv01", "", [1, 2, 3]));
        history.Add(new InterventionEntry(T0, InterventionKind.Note, "", "note"));
        Assert.Equal([InterventionKind.Note], history.Since(0).Select(e => e.Kind));
    }

    /// <summary>
    /// Rapport avec un écran enregistré : un lecteur par session, son script seul autorisé (empreinte de son contenu exact),
    /// les images en base64 dans un bloc de données, le nom de la session échappé, rien de l'écran dans la chronologie ;
    /// sans écran, aucun script.
    /// </summary>
    [Fact]
    public void ReportPlaysTheScreenWithOnlyItsOwnScript()
    {
        const int width = 192, height = 128;
        var encoder = new ScreenTileEncoder(stream: 9);
        var screen = Screen(width, height);
        var changed = (byte[])screen.Clone();
        Paint(changed, width, 64, 0, 64, 64, 0);
        const string label = "srv01\u202Egnp</script><script>alert(1)</script>";
        var recording = new InterventionRecording("x", [
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(1), InterventionKind.Screen, label, "", encoder.Encode(T0.AddSeconds(1), screen, width, height, width * 4)!.Data),
            new InterventionEntry(T0.AddSeconds(5), InterventionKind.Screen, label, "", encoder.Encode(T0.AddSeconds(5), changed, width, height, width * 4)!.Data),
            new InterventionEntry(T0.AddMinutes(1), InterventionKind.Stopped, "", ""),
        ], Damaged: false, Unfinished: false);

        string html = InterventionReport.ToHtml(recording, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        var scripts = ScriptBlocks().Matches(html);
        var code = Assert.Single(scripts, m => m.Groups["type"].Value.Length == 0).Groups["body"].Value;
        var hash = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code))) + "'";
        Assert.Contains("default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src " + hash + "\"", html, StringComparison.Ordinal);
        var frames = Assert.Single(scripts, m => m.Groups["type"].Value.Contains("octet-stream", StringComparison.Ordinal)).Groups["body"].Value;
        Assert.Matches("^[A-Za-z0-9+/=]+$", frames);
        // Conteneur : décalage (ms) et longueur de chaque image, puis l'image ; relu comme le lecteur le fait.
        var container = Convert.FromBase64String(frames);
        var read = new List<(uint Ms, ScreenFrameHeader Header)>();
        for (int at = 0; at + 8 <= container.Length;)
        {
            uint ms = BitConverter.ToUInt32(container, at);
            int length = (int)BitConverter.ToUInt32(container, at + 4);
            Assert.True(ScreenFrameHeader.TryRead(container.AsSpan(at + 8, length), out var header));
            read.Add((ms, header));
            at += 8 + length;
        }

        Assert.Equal([0u, 4000u], read.Select(r => r.Ms));
        Assert.Equal([true, false], read.Select(r => r.Header.Key));
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        // Nom de la session (venu des serveurs) échappé, sans inversion du sens d'écriture.
        Assert.Contains("srv01�gnp&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "<section class=\"screen\""));
        Assert.Contains("width=\"192\" height=\"128\"", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"note warn gap\" hidden>", html, StringComparison.Ordinal);
        // Chronologie : le début et la fin, pas les images d'écran.
        Assert.Equal(2, Regex.Matches(html, "<td class=\"time\">").Count);

        var plain = new InterventionRecording("x", [recording.Entries[0], recording.Entries[^1]], Damaged: false, Unfinished: false);
        string without = InterventionReport.ToHtml(plain, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.DoesNotContain("<script", without, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", without, StringComparison.Ordinal);
    }

    [GeneratedRegex("<script(?<type> type=\"[^\"]*\"(?: [^>]*)?)?>(?<body>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptBlocks();

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
