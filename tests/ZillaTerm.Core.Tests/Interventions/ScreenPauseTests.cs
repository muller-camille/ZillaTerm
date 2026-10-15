using System.Globalization;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

/// <summary>
/// Pause de l'affichage d'une session Bureau à distance dans son enregistrement d'écran : image sans tuile marquée (avec
/// sa raison), écran figé dessous, image clé à la reprise ; dans le rapport, bande grise sur la frise et repère cliquable.
/// </summary>
public sealed partial class ScreenPauseTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);

    private const int Width = 128;
    private const int Height = 64;

    private static byte[] Screen(byte value) => Enumerable.Repeat(value, Width * Height * 4).ToArray();

    [Fact]
    public void NothingIsMarkedBeforeTheFirstImage()
    {
        Assert.Null(new ScreenTileEncoder(stream: 1).Mark(T0, ScreenPause.Idle));
    }

    [Fact]
    public void PauseIsAnEmptyMarkedImageAndTheScreenComesBackWhole()
    {
        var encoder = new ScreenTileEncoder(stream: 3);
        var decoder = new ScreenTileDecoder();
        var screen = Screen(200);
        Assert.True(decoder.Apply(encoder.Encode(T0, screen, Width, Height, Width * 4)!.Data));
        var shown = decoder.Pixels.ToArray();

        var pause = encoder.Mark(T0.AddSeconds(10), ScreenPause.Idle)!;
        Assert.Equal(ScreenPause.Idle, pause.Header.Pause);
        Assert.False(pause.Header.Key);
        Assert.Equal((0, 0, 0), (pause.Header.Placements, pause.Header.NewTiles, pause.Header.Activity));
        Assert.Equal(1, pause.Header.Sequence);
        Assert.True(ScreenFrameHeader.TryRead(pause.Data, out var read));
        Assert.Equal(ScreenPause.Idle, read.Pause);
        // Corps : un vrai flux zlib vide (un navigateur refuse de décompresser un corps vide).
        Assert.Equal([0x78, 0xDA, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01], pause.Data[ScreenFrameHeader.Length..]);
        // Écran figé dessous : la dernière image reste.
        Assert.True(decoder.Apply(pause.Data));
        Assert.Equal(ScreenPause.Idle, decoder.Pause);
        Assert.Equal(shown, decoder.Pixels);
        // Déjà en pause pour cette raison : rien de plus ; autre raison : nouvelle marque.
        Assert.Null(encoder.Mark(T0.AddSeconds(20), ScreenPause.Idle));
        var hidden = encoder.Mark(T0.AddSeconds(30), ScreenPause.Hidden)!;
        Assert.Equal(ScreenPause.Hidden, hidden.Header.Pause);
        Assert.True(decoder.Apply(hidden.Data));

        // Reprise : marque de fin, puis l'écran entier en image clé, même sans changement.
        var resume = encoder.Mark(T0.AddSeconds(60), ScreenPause.None)!;
        Assert.Equal(ScreenPause.None, resume.Header.Pause);
        Assert.True(decoder.Apply(resume.Data));
        Assert.Equal(ScreenPause.None, decoder.Pause);
        var back = encoder.Encode(T0.AddSeconds(61), screen, Width, Height, Width * 4)!;
        Assert.True(back.Header.Key);
        Assert.Equal(4, back.Header.Sequence);
        Assert.True(decoder.Apply(back.Data));
        Assert.Null(encoder.Encode(T0.AddSeconds(62), screen, Width, Height, Width * 4));
    }

    [Fact]
    public void PauseCannotCarryTilesOrBeAKeyImage()
    {
        var encoder = new ScreenTileEncoder(stream: 4);
        var key = encoder.Encode(T0, Screen(10), Width, Height, Width * 4)!.Data;
        var forged = (byte[])key.Clone();
        forged[16] |= 2 << 1;
        Assert.False(ScreenFrameHeader.TryRead(forged, out _));

        var pause = encoder.Mark(T0.AddSeconds(1), ScreenPause.Hidden)!.Data;
        var unknown = (byte[])pause.Clone();
        unknown[16] = 3 << 1;
        Assert.False(ScreenFrameHeader.TryRead(unknown, out _));
    }

    /// <summary>
    /// Rapport : chaque pause est une bande sous le lecteur, de son début à l'image suivante (ou à la fin de la ligne de
    /// temps), avec son heure et sa raison ; le lecteur reçoit les textes de l'écran en pause.
    /// </summary>
    [Fact]
    public void ReportMarksEachPauseOnTheTimeline()
    {
        var encoder = new ScreenTileEncoder(stream: 1);
        var first = encoder.Encode(T0.AddSeconds(1), Screen(30), Width, Height, Width * 4)!.Data;
        var idle = encoder.Mark(T0.AddSeconds(11), ScreenPause.Idle)!.Data;
        var resumed = encoder.Mark(T0.AddSeconds(41), ScreenPause.None)!.Data;
        var key = encoder.Encode(T0.AddSeconds(41), Screen(30), Width, Height, Width * 4)!.Data;
        var hidden = encoder.Mark(T0.AddSeconds(61), ScreenPause.Hidden)!.Data;
        const string Session = "admin-srv01@srv01";
        var recording = new InterventionRecording("x", [
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(1), InterventionKind.Screen, Session, "", first),
            new InterventionEntry(T0.AddSeconds(11), InterventionKind.Screen, Session, "", idle),
            new InterventionEntry(T0.AddSeconds(41), InterventionKind.Screen, Session, "", resumed),
            new InterventionEntry(T0.AddSeconds(41), InterventionKind.Screen, Session, "", key),
            new InterventionEntry(T0.AddSeconds(61), InterventionKind.Screen, Session, "", hidden),
            new InterventionEntry(T0.AddSeconds(101), InterventionKind.Connection, Session, "Session fermée"),
            new InterventionEntry(T0.AddSeconds(120), InterventionKind.Stopped, "", ""),
        ], Damaged: false, Unfinished: false);

        string html = InterventionReport.ToHtml(recording, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        // Ligne de temps de 1 s à 101 s (fermeture de la session), plus sa dernière seconde : 101 000 ms.
        Assert.Contains("data-duration=\"101000\"", html, StringComparison.Ordinal);
        var marks = PauseTag().Matches(html).Select(m => m.Value).ToList();
        Assert.Equal(2, marks.Count);
        Assert.Contains("data-ms=\"10000\"", marks[0], StringComparison.Ordinal);
        Assert.Contains("left:9.901%;width:29.703%", marks[0], StringComparison.Ordinal);
        Assert.Contains("title=\"Display paused from 14:00:11 to 14:00:41 (computer idle)\"", marks[0], StringComparison.Ordinal);
        // Dernière pause : jusqu'à la fin de la ligne de temps (fermeture de la session).
        Assert.Contains("data-ms=\"60000\"", marks[1], StringComparison.Ordinal);
        Assert.Contains("title=\"Display paused from 14:01:01 to 14:01:41 (session not shown)\"", marks[1], StringComparison.Ordinal);
        Assert.Contains("\"paused\":\"Display paused\"", html, StringComparison.Ordinal);
        Assert.Contains("\"idle\":\"computer idle\"", html, StringComparison.Ordinal);
    }

    [GeneratedRegex("<button type=\"button\" class=\"mark pause\"[^>]*>")]
    private static partial Regex PauseTag();
}
