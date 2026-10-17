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
    private static List<InterventionEntry> Record(IEnumerable<byte[]> screens) => Record(screens, Width, Height);

    /// <summary>Images de l'enregistrement d'écrans de cette taille, une par seconde, comme dans un journal.</summary>
    private static List<InterventionEntry> Record(IEnumerable<byte[]> screens, int width, int height)
    {
        var encoder = new ScreenTileEncoder(stream: 3, live: true);
        var entries = new List<InterventionEntry>();
        int second = 0;
        foreach (var screen in screens)
        {
            var time = T0.AddSeconds(second++);
            if (encoder.Encode(time, screen, width, height, width * 4) is { } frame)
            {
                entries.Add(new InterventionEntry(time, InterventionKind.Screen, "srv", "", frame.Data));
            }
        }

        return entries;
    }

    /// <summary>Écran BGRA de pixels tirés au hasard parmi <paramref name="colors"/> (gris), le même pour une même graine.</summary>
    private static byte[] Noise(int width, int height, int seed, params byte[] colors)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = colors[random.Next(colors.Length)];
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// Le format actuel réencodé redonne le journal à l'octet près (mêmes images, copies d'écran, images clés, Brotli),
    /// enregistrement avec seuil de bruit ou en direct, pauses comprises ; chaque façon est mesurée.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentFormatIsReencodedToTheByte(bool live)
    {
        var encoder = new ScreenTileEncoder(stream: 2, live);
        var screens = new[]
        {
            Screen(0), Paint(Screen(0), 100, 100, 10, 10, 0), Paint(Screen(0), 20, 20, 100, 80, 129), Screen(0), Screen(1), Screen(4),
        };
        var entries = new List<InterventionEntry>();
        for (int second = 0; second < 12; second++)
        {
            var time = T0.AddSeconds(second);
            var frame = second switch
            {
                6 => encoder.Mark(time, ScreenPause.Hidden),
                7 => encoder.Mark(time, ScreenPause.None),
                _ => encoder.Encode(time, screens[Math.Min(second, screens.Length - 1)], Width, Height, Width * 4),
            };
            if (frame is not null)
            {
                entries.Add(new InterventionEntry(time, InterventionKind.Screen, "srv", "", frame.Data));
            }
        }

        var result = ScreenCompressionMeasure.Measure(entries);

        Assert.Equal(1, result.Streams);
        Assert.Equal(0, result.Lost);
        Assert.Equal(2, result.KeyFrames);
        Assert.Equal(9, result.Methods.Count);
        Assert.Equal("format actuel (contrôle)", result.Methods[0].Name);
        Assert.Equal(entries.Sum(e => (long)e.Image!.Length), result.JournalBytes);
        Assert.Equal(result.JournalBytes, result.Methods[0].Bytes);
        Assert.All(result.Methods, method => Assert.True(method.Bytes > 0));
    }

    /// <summary>
    /// Une fenêtre de texte qui défile d'une ligne à chaque image : copies d'écran à chaque fois, pour bien moins que sans
    /// elles (tout ce qui a bougé en tuiles).
    /// </summary>
    [Fact]
    public void MovesCostMuchLessThanTiles()
    {
        var result = ScreenCompressionMeasure.Measure(Record(Enumerable.Range(0, 11).Select(line => Screen(line))));
        // Poids des 10 images qui défilent, sans l'image clé du début.
        var key = ScreenCompressionMeasure.Measure(Record([Screen(0)]));
        long Scrolling(int method) => result.Methods[method].Bytes - key.Methods[method].Bytes;

        Assert.Equal(10, result.Moved);
        Assert.True(Scrolling(0) < Scrolling(1) / 2, $"avec copies {Scrolling(0)}, sans {Scrolling(1)}");
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
    /// en un seul flux elle se compresse avec ce qui précède ; sans elle (une image clé toutes les 30 minutes, ou seulement
    /// la première), rien n'est à garder.
    /// </summary>
    [Fact]
    public void FewerKeyFramesKeepOnlyWhatChangedAfterAPause()
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
        long perKey = result.Methods[0].Bytes, oneStream = result.Methods[6].Bytes, halfHour = result.Methods[7].Bytes, first = result.Methods[8].Bytes;
        Assert.True(oneStream < perKey, $"un seul flux {oneStream}, flux par image clé {perKey}");
        Assert.True(first < oneStream, $"seule la première complète {first}, un seul flux {oneStream}");
        Assert.Equal(first, halfHour);
    }

    /// <summary>
    /// Une tuile en couleurs 24 bits parmi des tuiles 16 bits (couleurs tirées au hasard, que Brotli ne resserre pas) :
    /// tuile par tuile, seule elle prend 3 octets par pixel, au lieu de toute l'image. Des tuiles de quatre couleurs en
    /// palette : écrites autrement (un octet par 4 pixels), relues sans perte comme toutes les façons (vérifié pendant la
    /// mesure) ; ce qu'elles font gagner dépend de l'écran, pas vérifié ici.
    /// </summary>
    [Fact]
    public void TileByTileColorsAndPalettesAreMeasured()
    {
        const int width = 512, height = 128;
        var random = new Random(1);
        byte[] Colors(Func<uint> pick)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                uint color = pick();
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)color, (byte)(color >> 8), (byte)(color >> 16), 255);
            }

            return pixels;
        }

        uint Exact16() => ScreenTileEncoder.From565((ushort)random.Next(65536));
        var first = Colors(Exact16);
        var second = Colors(Exact16);
        // 0x818181 : pas une couleur 16 bits.
        second[0] = second[1] = second[2] = 0x81;
        var result = ScreenCompressionMeasure.Measure(Record([first, second], width, height));
        long frame = result.Methods[0].Bytes, tile = result.Methods[2].Bytes;
        Assert.True(tile < frame, $"tuile par tuile {tile}, toute l'image {frame}");

        uint[] four = [0x202020, 0x818181, 0xC0C0C0, 0x0000FF];
        var palettes = ScreenCompressionMeasure.Measure(Record([Colors(() => four[random.Next(4)])], width, height));
        Assert.NotEqual(palettes.Methods[2].Bytes, palettes.Methods[3].Bytes);
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
            new ScreenCompressionMethod("format actuel (contrôle)", 2_000_000, TimeSpan.FromSeconds(4)),
            new ScreenCompressionMethod("Brotli 11", 1_000_000, TimeSpan.FromSeconds(1.5)),
            new ScreenCompressionMethod("Brotli 9", 1_999_000, TimeSpan.FromSeconds(4.5)),
        ]);

        var lines = result.Lines().ToList();

        Assert.Equal("2 écran(s), 120 image(s) dont 3 complète(s), sur 12 min ; journal 1.91 Mo, dont images complètes 488.3 Ko (25 %)", lines[0]);
        Assert.Equal("  format actuel (contrôle) : 1.91 Mo, 4.0 s de calcul", lines[1]);
        Assert.Equal("  Brotli 11 : 976.6 Ko (-50 %), 1.5 s de calcul", lines[2]);
        Assert.Equal("  Brotli 9 : 1.91 Mo (+0 %), 4.5 s de calcul", lines[3]);
        Assert.Equal("  copies d'écran (défilement, fenêtre déplacée) sur 30 image(s) (25 %)", lines[4]);
    }
}
