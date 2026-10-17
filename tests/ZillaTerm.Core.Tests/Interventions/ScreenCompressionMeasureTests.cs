using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed class ScreenCompressionMeasureTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private const int Width = 320;
    private const int Height = 200;

    /// <summary>Hauteur d'une ligne de texte : comme une vraie police, pas un diviseur de la taille des tuiles.</summary>
    private const int Line = 15;

    /// <summary>
    /// Écran BGRA : fond à motif (qui change d'une ligne à l'autre), et une fenêtre de texte (lignes de <see cref="Line"/>
    /// pixels, caractères de 8 pixels tirés d'après le numéro de ligne) dont la première ligne affichée est
    /// <paramref name="firstLine"/>.
    /// </summary>
    private static byte[] Screen(int firstLine, int left = 64, int top = 32, int width = 256, int height = 160, int lineSalt = 0)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte value;
                if (x >= left && x < left + width && y >= top && y < top + height)
                {
                    int line = firstLine + ((y - top) / Line), column = (x - left) / 8, row = (y - top) % Line;
                    int glyph = Glyph(line, column, lineSalt);
                    // Caractère : quelques pixels sombres selon le motif tiré, sur fond clair.
                    bool ink = row is >= 3 and <= 11 && ((glyph >> ((x - left) % 8 % 5)) & 1) == 1 && (row + glyph) % 3 != 0;
                    value = ink ? (byte)24 : (byte)250;
                }
                else
                {
                    value = (byte)((x / 8 + y / 8) % 7 == 0 ? 90 : 200);
                }

                int o = ((y * Width) + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = value;
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>Caractère tiré d'après sa place (le même à chaque passage des tests).</summary>
    private static int Glyph(int line, int column, int salt)
    {
        uint h = (uint)((line * 73856093) ^ (column * 19349663) ^ (salt * 83492791));
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (int)(h % 97);
    }

    private static byte[] Paint(byte[] screen, int x0, int y0, int w, int h, byte value)
    {
        var copy = (byte[])screen.Clone();
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int o = ((y * Width) + x) * 4;
                copy[o] = copy[o + 1] = copy[o + 2] = value;
            }
        }

        return copy;
    }

    private static ushort[] Rgb565(byte[] bgra) =>
        [.. Enumerable.Range(0, bgra.Length / 4).Select(i => (ushort)(((bgra[(4 * i) + 2] >> 3) << 11) | ((bgra[(4 * i) + 1] >> 2) << 5) | (bgra[4 * i] >> 3)))];

    /// <summary>Images de l'enregistrement de ces captures, une par seconde, comme dans un journal.</summary>
    private static List<InterventionEntry> Record(IEnumerable<byte[]> screens, bool live = true)
    {
        var encoder = new ScreenTileEncoder(stream: 3, live);
        var entries = new List<InterventionEntry>();
        int second = 0;
        foreach (var screen in screens)
        {
            var time = T0.AddSeconds(second++);
            if (encoder.Encode(time, screen, Width, Height, Width * 4) is { } frame)
            {
                entries.Add(new InterventionEntry(time, InterventionKind.Screen, "srv", "", frame.Data));
            }
        }

        return entries;
    }

    /// <summary>
    /// Un journal enregistré au format d'aujourd'hui (copies, Brotli d'une image clé à l'autre) se relit et se mesure ; il
    /// pèse moins que le même écran au format d'avant la 0.42 (tuile entière, en différence, déjà vue, défilement).
    /// </summary>
    [Fact]
    public void CurrentFormatIsReadAndLighterThanTheFormerOne()
    {
        var start = Screen(0);
        var entries = Record([
            start,
            Paint(start, 100, 100, 10, 10, 0),
            Paint(start, 20, 20, 100, 80, 128),
            start,
            Screen(1),
        ]);

        var result = ScreenCompressionMeasure.Measure(entries);

        Assert.Equal(1, result.Streams);
        Assert.Equal(5, result.Frames);
        Assert.Equal(1, result.KeyFrames);
        Assert.Equal(0, result.Lost);
        Assert.Equal(8, result.Methods.Count);
        Assert.Equal(entries.Sum(e => (long)e.Image!.Length), result.JournalBytes);
        Assert.True(result.JournalBytes < result.Methods[0].Bytes, $"journal {result.JournalBytes}, format d'avant {result.Methods[0].Bytes}");
    }

    /// <summary>
    /// Une fenêtre de texte qui défile d'une ligne à chaque image : défilement reconnu à chaque fois, sans perte (vérifié
    /// pendant la mesure), pour bien moins que le format d'avant la 0.42. Le gain de Brotli dépend de l'écran : pas vérifié ici.
    /// </summary>
    [Fact]
    public void ScrollIsRecognizedAndCostsMuchLess()
    {
        var result = ScreenCompressionMeasure.Measure(Record(Enumerable.Range(0, 11).Select(line => Screen(line))));
        // Poids des 10 images qui défilent, sans l'image clé du début.
        var key = ScreenCompressionMeasure.Measure(Record([Screen(0)]));
        long Scrolling(int method) => result.Methods[method].Bytes - key.Methods[method].Bytes;

        Assert.Equal(10, result.Scrolled);
        Assert.True(Scrolling(3) < Scrolling(0) / 2, $"défilement {Scrolling(3)}, format d'avant {Scrolling(0)}");
        Assert.All(result.Methods, method => Assert.True(method.Bytes > key.Methods[0].Bytes));
    }

    /// <summary>
    /// Une image manquante (bloc du journal abîmé) : les suivantes sont comptées perdues jusqu'à l'image clé suivante,
    /// sans erreur ; une marque de pause est comptée, sans tuile.
    /// </summary>
    [Fact]
    public void MissingFrameAndPauseAreCounted()
    {
        var encoder = new ScreenTileEncoder(stream: 1, live: true);
        var entries = new List<InterventionEntry>();
        void Add(ScreenFrame? frame, int second)
        {
            if (frame is not null)
            {
                entries.Add(new InterventionEntry(T0.AddSeconds(second), InterventionKind.Screen, "srv", "", frame.Data));
            }
        }

        Add(encoder.Encode(T0, Screen(0), Width, Height, Width * 4), 0);
        Add(encoder.Encode(T0.AddSeconds(1), Screen(1), Width, Height, Width * 4), 1);
        Add(encoder.Encode(T0.AddSeconds(2), Screen(2), Width, Height, Width * 4), 2);
        Add(encoder.Encode(T0.AddSeconds(3), Screen(3), Width, Height, Width * 4), 3);
        Add(encoder.Mark(T0.AddSeconds(4), ScreenPause.Hidden), 4);
        Add(encoder.Mark(T0.AddSeconds(5), ScreenPause.None), 5);
        Add(encoder.Encode(T0.AddSeconds(6), Screen(4), Width, Height, Width * 4), 6);
        entries.RemoveAt(1);

        var result = ScreenCompressionMeasure.Measure(entries);

        // Images 2 et 3, puis les deux marques : perdues jusqu'à l'image clé d'après la pause.
        Assert.Equal(6, result.Frames);
        Assert.Equal(4, result.Lost);
        Assert.Equal(2, result.KeyFrames);
        Assert.Equal(TimeSpan.FromSeconds(6), result.Duration);
    }

    /// <summary>
    /// Retour après une pause sur un écran inchangé : l'enregistrement repart d'une image clé entière (comptée à part) ;
    /// en un seul flux elle se compresse avec ce qui précède, et allégée elle ne garde rien.
    /// </summary>
    [Fact]
    public void LightKeyFramesKeepOnlyWhatChangedAfterAPause()
    {
        var encoder = new ScreenTileEncoder(stream: 1, live: true);
        var frames = new List<(int Second, ScreenFrame? Frame)>
        {
            (0, encoder.Encode(T0, Screen(0), Width, Height, Width * 4)),
            (1, encoder.Encode(T0.AddSeconds(1), Screen(1), Width, Height, Width * 4)),
            (2, encoder.Mark(T0.AddSeconds(2), ScreenPause.Hidden)),
            (3, encoder.Mark(T0.AddSeconds(3), ScreenPause.None)),
            (4, encoder.Encode(T0.AddSeconds(4), Screen(1), Width, Height, Width * 4)),
        };
        var entries = frames.Select(f => new InterventionEntry(T0.AddSeconds(f.Second), InterventionKind.Screen, "srv", "", f.Frame!.Data)).ToList();

        var result = ScreenCompressionMeasure.Measure(entries);

        Assert.Equal(2, result.KeyFrames);
        Assert.Equal(entries[0].Image!.Length + entries[4].Image!.Length, result.KeyFrameBytes);
        long perKey = result.Methods[4].Bytes, oneStream = result.Methods[6].Bytes, light = result.Methods[7].Bytes;
        Assert.True(oneStream < perKey, $"un seul flux {oneStream}, flux par image clé {perKey}");
        Assert.True(light < oneStream, $"allégées {light}, un seul flux {oneStream}");
    }

    /// <summary>
    /// Fenêtre qui défile au milieu d'un bureau immobile, pas alignée sur les bandes : seules les bandes entièrement dans
    /// la fenêtre sont copiées, et la copie redonne au pixel près l'écran d'après sur ces bandes.
    /// </summary>
    [Fact]
    public void ScrollInsideAWindowIsCopiedBandByBand()
    {
        var before = Rgb565(Screen(0, left: 40, width: 260));
        var after = Rgb565(Screen(3, left: 40, width: 260));

        var copies = ScreenScroll.Detect(before, after, Width, Height, Width);

        Assert.Equal([64, 128, 192], copies.Select(copy => copy.Left));
        Assert.All(copies, copy => Assert.Equal(3 * Line, copy.Shift));
        var screen = (ushort[])before.Clone();
        foreach (var copy in copies)
        {
            ScreenScroll.Apply(screen, Width, copy);
            for (int y = copy.Top; y < copy.Top + copy.Height; y++)
            {
                Assert.True(screen.AsSpan((y * Width) + copy.Left, copy.Width).SequenceEqual(after.AsSpan((y * Width) + copy.Left, copy.Width)));
            }
        }
    }

    /// <summary>Retour en arrière (contenu qui descend) : décalage négatif, copié de bas en haut sans s'écraser.</summary>
    [Fact]
    public void ScrollBackIsCopiedWithoutOverwriting()
    {
        var before = Rgb565(Screen(5));
        var after = Rgb565(Screen(2));

        var copies = ScreenScroll.Detect(before, after, Width, Height, Width);

        Assert.NotEmpty(copies);
        Assert.All(copies, copy => Assert.Equal(-3 * Line, copy.Shift));
        var screen = (ushort[])before.Clone();
        foreach (var copy in copies)
        {
            ScreenScroll.Apply(screen, Width, copy);
            for (int y = copy.Top; y < copy.Top + copy.Height; y++)
            {
                Assert.True(screen.AsSpan((y * Width) + copy.Left, copy.Width).SequenceEqual(after.AsSpan((y * Width) + copy.Left, copy.Width)));
            }
        }
    }

    /// <summary>Texte remplacé sur place (rien n'a défilé), ou écran inchangé : aucune copie.</summary>
    [Fact]
    public void ReplacedTextIsNotAScroll()
    {
        var before = Rgb565(Screen(0));

        Assert.Empty(ScreenScroll.Detect(before, Rgb565(Screen(0, lineSalt: 1)), Width, Height, Width));
        Assert.Empty(ScreenScroll.Detect(before, before, Width, Height, Width));
    }

    /// <summary>Lignes du journal de débogage : bilan, une ligne par méthode avec l'écart à la première.</summary>
    [Fact]
    public void LinesShowEachMethodAgainstTheCurrentOne()
    {
        var result = new ScreenCompressionResult(2, 120, 3, 0, 2_000_000, 500_000, TimeSpan.FromMinutes(12), 30,
        [
            new ScreenCompressionMethod("méthode actuelle (zlib par image, contrôle)", 2_000_000, TimeSpan.FromSeconds(4)),
            new ScreenCompressionMethod("Brotli 5 en flux", 1_000_000, TimeSpan.FromSeconds(1.5)),
        ]);

        var lines = result.Lines().ToList();

        Assert.Equal("2 écran(s), 120 image(s) dont 3 complète(s), sur 12 min ; journal 1.91 Mo, dont images complètes 488.3 Ko (25 %)", lines[0]);
        Assert.Equal("  méthode actuelle (zlib par image, contrôle) : 1.91 Mo, 4.0 s de calcul", lines[1]);
        Assert.Equal("  Brotli 5 en flux : 976.6 Ko (-50 %), 1.5 s de calcul", lines[2]);
        Assert.Equal("  défilement reconnu sur 30 image(s) (25 %)", lines[3]);
    }
}
