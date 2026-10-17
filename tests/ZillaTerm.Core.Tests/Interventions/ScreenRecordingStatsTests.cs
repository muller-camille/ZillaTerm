using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

/// <summary>Bilan de l'écran enregistré pour le journal de débogage : une ligne par minute, puis une à la fin.</summary>
public sealed class ScreenRecordingStatsTests
{
    private static byte[] Screen(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Motif qui dépend de la position : chaque tuile est différente des autres.
            pixels[i] = (byte)(shade + (i / 4 % width));
            pixels[i + 1] = (byte)(shade + (i / 4 / width));
            pixels[i + 2] = shade;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    [Fact]
    public void OneLinePerMinuteWithKeptFramesWeightAndTiles()
    {
        var start = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
        var encoder = new ScreenTileEncoder(1);
        var stats = new ScreenRecordingStats();
        var first = Screen(256, 128, 10);
        var second = Screen(256, 128, 90);
        long kept = 0;
        for (int s = 0; s < 60; s++)
        {
            var now = start.AddSeconds(s);
            // Image complète, puis un autre écran à 10 s, puis immobile : rien de plus à garder.
            var frame = encoder.Encode(now, s < 10 ? first : second, 256, 128, 256 * 4);
            stats.Captured(now);
            if (frame is not null)
            {
                stats.Kept(frame);
                kept += frame.Data.Length;
            }

            Assert.Null(stats.Report(now));
        }

        var line = stats.Report(start.AddSeconds(60));
        Assert.Equal($"60 s : 60 capture(s), 2 image(s) gardée(s) dont 1 complète(s), {ScreenRecordingStats.Bytes(kept)} "
                     + $"(plus grosse {line!.Split("plus grosse ")[1].Split(')')[0]}), 16 tuile(s) nouvelle(s), écran 256×128", line);

        // Compteurs repartis de zéro ; un écran immobile ne garde rien (la taille de l'écran reste la dernière connue) ; le
        // bilan final n'attend pas la minute.
        Assert.Null(stats.Report(start.AddSeconds(200), final: true));
        stats.Captured(start.AddSeconds(200));
        Assert.Null(encoder.Encode(start.AddSeconds(200), second, 256, 128, 256 * 4));
        Assert.Equal("5 s : 1 capture(s), 0 image(s) gardée(s) dont 0 complète(s), 0 o (plus grosse 0 o), 0 tuile(s) nouvelle(s), "
                     + "écran 256×128", stats.Report(start.AddSeconds(205), final: true));
    }

    [Theory]
    [InlineData(512, "512 o")]
    [InlineData(1536, "1.5 Ko")]
    [InlineData(2_200_000, "2.1 Mo")]
    public void ReadableSizes(long bytes, string text) => Assert.Equal(text, ScreenRecordingStats.Bytes(bytes));
}
