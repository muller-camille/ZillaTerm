using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Tests.Terminal;

public sealed class TerminalInputTests
{
    [Fact]
    public void EachTerminalEncodesTheKeyForItsOwnMode()
    {
        var shell = new TerminalEmulator();
        var vim = new TerminalEmulator();
        vim.Feed("\x1b[?1h\x1b[?2004h");   // curseur « application » et collage encadré, comme vim

        var up = TerminalInput.Special(TerminalKey.Up);
        Assert.Equal("\x1b[A", up.Encode(shell));
        Assert.Equal("\x1bOA", up.Encode(vim));
        Assert.Equal("\x1b[1;5A", TerminalInput.Special(TerminalKey.Up, ctrl: true).Encode(vim));

        var paste = TerminalInput.Pasted("ls\nuptime");
        Assert.Equal("ls\ruptime", paste.Encode(shell));
        Assert.Equal("\x1b[200~ls\ruptime\x1b[201~", paste.Encode(vim));

        Assert.Equal("\x03", TerminalInput.Typed("\x03").Encode(vim));
        Assert.Equal("\x1bOB", TerminalInput.Scrolled(TerminalKey.Down).Encode(vim));
    }

    [Fact]
    public void TellsWhatIsTypingAndWhatIsAMultiLinePaste()
    {
        Assert.True(TerminalInput.Typed("a").IsTyping);
        Assert.True(TerminalInput.Special(TerminalKey.Enter).IsTyping);
        Assert.True(TerminalInput.Pasted("x").IsTyping);
        Assert.False(TerminalInput.Scrolled(TerminalKey.Up).IsTyping);

        Assert.True(TerminalInput.Typed("").IsEmpty);
        Assert.True(TerminalInput.Pasted("").IsEmpty);
        Assert.False(TerminalInput.Special(TerminalKey.Up).IsEmpty);

        Assert.False(TerminalInput.Pasted("uptime").IsMultiLinePaste);
        Assert.False(TerminalInput.Pasted("uptime\r\n").IsMultiLinePaste);
        Assert.True(TerminalInput.Pasted("cd /tmp\r\nrm -rf x").IsMultiLinePaste);
        Assert.True(TerminalInput.Pasted("a\rb").IsMultiLinePaste);
        Assert.False(TerminalInput.Typed("a\nb").IsMultiLinePaste);
        // Caractères de contrôle retirés dès le collage (vue parallèle comprise).
        Assert.Equal("id", TerminalInput.Pasted("\x1b[201~id\x0f").Text.Replace("[201~", ""));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    [InlineData(7, 2, 4)]
    [InlineData(8, 2, 4)]
    [InlineData(0, 1, 1)]
    [InlineData(12, 2, 4)]
    public void LaysOutUpToEightSessions(int count, int rows, int columns)
    {
        Assert.Equal((rows, columns), ParallelLayout.For(count));
        Assert.True(rows * columns >= Math.Min(Math.Max(count, 1), ParallelLayout.MaxSessions));
    }

    private sealed record Pane(string Name, bool Included, bool Connected);

    [Fact]
    public void SimultaneousTypingGoesToTheTickedConnectedSessions()
    {
        var a = new Pane("a", true, true);
        var b = new Pane("b", true, true);
        var c = new Pane("c", false, true);
        var d = new Pane("d", true, false);
        Pane[] panes = [a, b, c, d];

        IReadOnlyList<Pane> Targets(Pane source, bool broadcast) =>
            ParallelLayout.Targets(source, panes, broadcast, p => p.Included, p => p.Connected);

        // Sans saisie simultanée : la session où l'on tape, seule.
        Assert.Equal([a], Targets(a, broadcast: false));

        // Avec : les sessions cochées et connectées, dans l'ordre de la vue.
        Assert.Equal([a, b], Targets(a, broadcast: true));
        Assert.Equal([a, b], Targets(b, broadcast: true));

        // Une session décochée ne reçoit rien des autres, et ce qu'on y tape ne va qu'à elle.
        Assert.Equal([c], Targets(c, broadcast: true));

        // La session où l'on tape reçoit toujours sa saisie, même si elle n'est pas connectée.
        Assert.Equal([a, b, d], Targets(d, broadcast: true));
    }
}

public sealed class TerminalThemeAndSearchTests
{
    [Fact]
    public void ThemesMapColors()
    {
        Assert.Same(TerminalTheme.Campbell, TerminalTheme.Find(null));
        Assert.Same(TerminalTheme.Campbell, TerminalTheme.Find("unknown"));
        Assert.Same(TerminalTheme.SolarizedLight, TerminalTheme.Find("Solarized-Light"));
        Assert.Equal(TerminalTheme.All.Count, TerminalTheme.All.Select(t => t.Id).Distinct().Count());

        // Campbell : mêmes couleurs que la palette d'origine.
        Assert.Equal(TerminalColor.ToRgb(1, true), TerminalTheme.Campbell.ToRgb(1, true));
        Assert.Equal(TerminalColor.DefaultBackground, TerminalTheme.Campbell.ToRgb(TerminalColor.Default, false));
        Assert.Equal(TerminalColor.ToRgb(9, true), TerminalTheme.Campbell.ToRgb(1, true, bold: true));

        // Autre palette : 16 couleurs et fond propres, 256 couleurs et couleurs vraies inchangées.
        var light = TerminalTheme.OneHalfLight;
        Assert.True(light.IsLight);
        Assert.False(TerminalTheme.Campbell.IsLight);
        Assert.Equal(0xFAFAFAu, light.ToRgb(TerminalColor.Default, false));
        Assert.Equal(0xE45649u, light.ToRgb(1, true));
        Assert.Equal(TerminalColor.ToRgb(100, true), light.ToRgb(100, true));
        Assert.Equal(0x123456u, light.ToRgb(TerminalColor.Rgb(0x12, 0x34, 0x56), true));
    }

    [Fact]
    public void FindsTextInTheScreenAndTheHistory()
    {
        var emulator = new TerminalEmulator(40, 3);
        emulator.Feed("error one\r\nok\r\nError two error\r\nlast\r\n");
        var matches = TerminalSearch.Find(emulator, "ERROR");
        Assert.Equal(3, matches.Count);
        Assert.True(matches[0].Row < 0);                       // dans l'historique
        Assert.Equal((0, 5), (matches[1].Column, matches[1].Length));
        Assert.Equal(10, matches[2].Column);
        Assert.Equal(matches[1].Row, matches[2].Row);
        Assert.Empty(TerminalSearch.Find(emulator, ""));
        Assert.Empty(TerminalSearch.Find(emulator, "absent"));

        var all = TerminalSearch.AllText(emulator);
        Assert.StartsWith("error one" + Environment.NewLine + "ok", all);
        Assert.EndsWith("last", all);
    }

    /// <summary>
    /// Mot coupé par le repli d'une ligne longue : trouvé, l'occurrence continuant sur la ligne suivante ; enregistré
    /// d'un seul tenant. Deux lignes séparées par un vrai saut de ligne ne se joignent pas.
    /// </summary>
    [Fact]
    public void FindsAndSavesTextCutByTheWrapOfALongLine()
    {
        var emulator = new TerminalEmulator(10, 5);
        emulator.Feed("path /var/log/app.log\r\nab\r\ncd");

        Assert.Equal([new TerminalMatch(1, 4, 7)], TerminalSearch.Find(emulator, "APP.LOG"));
        Assert.Equal([new TerminalMatch(0, 5, 8)], TerminalSearch.Find(emulator, "/var/log"));
        Assert.Empty(TerminalSearch.Find(emulator, "bc"));
        Assert.Equal(string.Join(Environment.NewLine, "path /var/log/app.log", "ab", "cd"), TerminalSearch.AllText(emulator));
    }
}
