using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Tests.Terminal;

public class TerminalEmulatorTests
{
    private static string Row(TerminalEmulator t, int row) => string.Concat(t.GetLine(row).Select(t.CellText)).TrimEnd();

    private static string[] Screen(TerminalEmulator t) => Enumerable.Range(0, t.Rows).Select(r => Row(t, r)).ToArray();

    [Fact]
    public void PrintsTextAndHandlesCrLf()
    {
        var t = new TerminalEmulator(10, 3);

        t.Feed("ab\r\ncd");

        Assert.Equal(["ab", "cd", ""], Screen(t));
        Assert.Equal((1, 2), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void WrapsAtLastColumnOnlyWhenNextCharArrives()
    {
        var t = new TerminalEmulator(4, 3);

        t.Feed("abcd");
        Assert.Equal((0, 3), (t.CursorRow, t.CursorColumn));

        t.Feed("e");
        Assert.Equal(["abcd", "e", ""], Screen(t));
    }

    [Fact]
    public void CarriageReturnCancelsPendingWrap()
    {
        var t = new TerminalEmulator(4, 3);

        t.Feed("abcd\rX");

        Assert.Equal(["Xbcd", "", ""], Screen(t));
    }

    [Fact]
    public void ScrollsIntoScrollback()
    {
        var t = new TerminalEmulator(5, 2);

        t.Feed("1\r\n2\r\n3\r\n4");

        Assert.Equal(["3", "4"], Screen(t));
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("1", Row(t, -2));
        Assert.Equal("2", Row(t, -1));
    }

    /// <summary>
    /// Historique plein : les plus anciennes lignes partent une à une, sans ralentir (une sortie abondante ou piégée
    /// figeait l'interface) ; une séquence aux paramètres sans fin ne remplit pas la mémoire.
    /// </summary>
    [Fact]
    public void FullScrollbackKeepsTheLatestLinesQuickly()
    {
        var t = new TerminalEmulator(10, 3, maxScrollback: 100);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        t.Feed(string.Concat(Enumerable.Range(1, 200_000).Select(i => $"{i}\r\n")));
        t.Feed(string.Concat(Enumerable.Repeat("\x1b[999S", 20_000)));
        t.Feed("\x1b[" + new string(';', 1_000_000) + "m");
        watch.Stop();

        Assert.Equal(100, t.ScrollbackCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        t.ClearScrollback();
        t.Feed("\x1b[2J\x1b[Ha\r\nb\r\nc\r\nd");
        Assert.Equal(1, t.ScrollbackCount);
        Assert.Equal("a", Row(t, -1));
    }

    [Fact]
    public void ClearScrollbackKeepsTheScreen()
    {
        var t = new TerminalEmulator(5, 2);
        t.Feed("1\r\n2\r\n3\r\n4");
        long version = t.Version;

        t.ClearScrollback();

        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(["3", "4"], Screen(t));
        Assert.True(t.Version > version);
        t.Feed("\r\n5");
        Assert.Equal(1, t.ScrollbackCount);
        Assert.Equal("3", Row(t, -1));
    }

    [Fact]
    public void CursorPositionAndErase()
    {
        var t = new TerminalEmulator(6, 3);
        t.Feed("aaaaaa\r\nbbbbbb\r\ncccccc");

        t.Feed("\x1b[2;3H\x1b[K");
        Assert.Equal(["aaaaaa", "bb", "cccccc"], Screen(t));

        t.Feed("\x1b[1K");
        Assert.Equal(["aaaaaa", "", "cccccc"], Screen(t));

        t.Feed("\x1b[J");
        Assert.Equal(["aaaaaa", "", ""], Screen(t));

        t.Feed("\x1b[2J");
        Assert.Equal(["", "", ""], Screen(t));
        Assert.Equal((1, 2), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void RelativeMovesAreClamped()
    {
        var t = new TerminalEmulator(10, 5);

        t.Feed("\x1b[3;3H\x1b[10A\x1b[2C\x1b[B\x1b[20D");

        Assert.Equal((1, 0), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void BackspaceAndTab()
    {
        var t = new TerminalEmulator(20, 2);

        t.Feed("abc\bX\tY");

        Assert.Equal("abX     Y", Row(t, 0));
        Assert.Equal(9, t.CursorColumn);
    }

    [Fact]
    public void GraphicRendition()
    {
        var t = new TerminalEmulator(10, 2);

        t.Feed("\x1b[1;31mA\x1b[0;38;5;208;48;2;1;2;3mB\x1b[7mC\x1b[mD\x1b[94mE");

        var line = t.GetLine(0);
        Assert.Equal(1, line[0].Foreground);
        Assert.True(line[0].Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(208, line[1].Foreground);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), line[1].Background);
        Assert.False(line[1].Flags.HasFlag(CellFlags.Bold));
        Assert.True(line[2].Flags.HasFlag(CellFlags.Inverse));
        Assert.Equal(TerminalColor.Default, line[3].Foreground);
        Assert.Equal(CellFlags.None, line[3].Flags);
        Assert.Equal(12, line[4].Foreground);
    }

    [Fact]
    public void ScrollRegionKeepsLinesOutsideAndSkipsScrollback()
    {
        var t = new TerminalEmulator(5, 4);
        t.Feed("top\r\na\r\nb\r\nbot");

        t.Feed("\x1b[2;3r\x1b[3;1H\nX");

        Assert.Equal(["top", "b", "X", "bot"], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void InsertAndDeleteLinesAndChars()
    {
        var t = new TerminalEmulator(6, 3);
        t.Feed("abcdef\r\n2\r\n3");

        t.Feed("\x1b[1;3H\x1b[2@");
        Assert.Equal("ab  cd", Row(t, 0));

        t.Feed("\x1b[3P");
        Assert.Equal("abd", Row(t, 0));

        t.Feed("\x1b[2;1H\x1b[L");
        Assert.Equal(["abd", "", "2"], Screen(t));

        t.Feed("\x1b[M");
        Assert.Equal(["abd", "2", ""], Screen(t));

        t.Feed("\x1b[1;1H\x1b[2X");
        Assert.Equal("  d", Row(t, 0));
    }

    [Fact]
    public void AlternateScreenRestoresMainScreenAndCursor()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("shell$ vi");

        t.Feed("\x1b[?1049h\x1b[Hvim content");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal("vim conten", Row(t, 0));
        Assert.Equal(0, t.ScrollbackCount);

        t.Feed("\x1b[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("shell$ vi", Row(t, 0));
        Assert.Equal((0, 9), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void ReportsTheWorkingDirectoryAndIgnoresTheTitle()
    {
        var t = new TerminalEmulator();
        var dirs = new List<string>();
        t.WorkingDirectoryChanged += dirs.Add;

        t.Feed("\x1b]0;root@srv: ~\a");
        t.Feed("\x1b]7;file://srv01/var/log/my%20app\x1b\\");
        t.Feed("\x1b]7;/opt/app\a");
        t.Feed("\x1b]7;relative\a");

        Assert.Equal(["/var/log/my app", "/opt/app"], dirs);
        Assert.Equal("", Row(t, 0));
    }

    [Theory]
    [InlineData("file://h/tmp/a%0Ab")]
    [InlineData("file://h/tmp/a%1B[31m")]
    [InlineData("file://h/tmp/%E2%80%AEtxt.exe")]
    [InlineData("file://h/tmp/a%E2%80%8Bb")]
    [InlineData("/tmp/a\u0085b")]
    public void WorkingDirectoryWithHiddenCharactersIsIgnored(string value)
    {
        var t = new TerminalEmulator();
        var dirs = new List<string>();
        t.WorkingDirectoryChanged += dirs.Add;

        t.Feed($"\x1b]7;{value}\a");

        Assert.Empty(dirs);
    }

    [Fact]
    public void LinePositionCountsFromTheScrollRegionInOriginMode()
    {
        var t = new TerminalEmulator(10, 10);

        t.Feed("\x1b[3;8r\x1b[?6h\x1b[2dX");

        Assert.Equal("X", Row(t, 3));
        t.Feed("\x1b[?6l\x1b[2dY");
        Assert.Equal("Y", Row(t, 1).Trim());
    }

    [Fact]
    public void SequencesSplitAcrossFeedsAreParsed()
    {
        var t = new TerminalEmulator(10, 2);
        var dirs = new List<string>();
        t.WorkingDirectoryChanged += dirs.Add;

        foreach (var chunk in new[] { "\x1b", "[3", "1mR", "\x1b]7;/t", "mp\x1b", "\\ok" })
        {
            t.Feed(chunk);
        }

        Assert.Equal("Rok", Row(t, 0));
        Assert.Equal(1, t.GetLine(0)[0].Foreground);
        Assert.Equal(["/tmp"], dirs);
    }

    [Fact]
    public void AnswersDeviceQueries()
    {
        var t = new TerminalEmulator(10, 5);
        var responses = new List<string>();
        t.Response += responses.Add;

        t.Feed("\x1b[3;4H\x1b[6n\x1b[5n\x1b[c");

        Assert.Equal(["\x1b[3;4R", "\x1b[0n", "\x1b[?1;2c"], responses);
    }

    [Fact]
    public void ModesAreTracked()
    {
        var t = new TerminalEmulator();

        t.Feed("\x1b[?1h\x1b[?2004h\x1b[?25l");
        Assert.True(t.ApplicationCursorKeys);
        Assert.True(t.BracketedPaste);
        Assert.False(t.CursorVisible);

        t.Feed("\x1b[?1l\x1b[?2004l\x1b[?25h");
        Assert.False(t.ApplicationCursorKeys);
        Assert.False(t.BracketedPaste);
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void DecLineDrawing()
    {
        var t = new TerminalEmulator(10, 2);

        t.Feed("\x1b(0lqk\x1b(Bq");

        Assert.Equal("┌─┐q", Row(t, 0));
    }

    [Fact]
    public void SaveAndRestoreCursorKeepsAttributes()
    {
        var t = new TerminalEmulator(10, 3);

        t.Feed("\x1b[32m\u001b7\x1b[0m\x1b[3;5HZ\u001b8A");

        Assert.Equal(2, t.GetLine(0)[0].Foreground);
        Assert.Equal('A', t.GetLine(0)[0].CodePoint);
    }

    [Fact]
    public void RestoreWithoutSaveUsesDefaults()
    {
        var t = new TerminalEmulator(10, 3);

        t.Feed("\x1b[31m\u001b8X");

        Assert.Equal(TerminalColor.Default, t.GetLine(0)[0].Foreground);
    }

    [Fact]
    public void ResizeKeepsCursorLineAndPushesTopToScrollback()
    {
        var t = new TerminalEmulator(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");

        t.Resize(4, 3);

        Assert.Equal(["3", "4", "5"], Screen(t));
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal((2, 1), (t.CursorRow, t.CursorColumn));

        t.Resize(12, 4);
        Assert.Equal(["3", "4", "5", ""], Screen(t));
        Assert.Equal(12, t.GetLine(0).Length);
    }

    [Fact]
    public void GetTextJoinsLinesAndTrims()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("hello   \r\nworld");

        Assert.Equal("llo\nwor", t.GetText(0, 2, 1, 2));
        Assert.Equal("llo\nwor", t.GetText(1, 2, 0, 2));
    }

    /// <summary>
    /// Ligne longue repliée par le terminal : copiée d'un seul tenant, espaces au bord compris (collée ailleurs, elle ne
    /// devient pas deux commandes tronquées), même une fois passée dans l'historique.
    /// </summary>
    [Fact]
    public void CopyJoinsALineWrappedByTheTerminal()
    {
        const string command = "$ rm -rf /var/app/cache/old-releases/2024-01-15/tmp";
        var t = new TerminalEmulator(40, 10);
        t.Feed(command);

        Assert.True(t.IsWrapped(0));
        Assert.False(t.IsWrapped(1));
        Assert.Equal(command, t.GetText(0, 0, 1, 39));
        Assert.Equal(command[37..], t.GetText(0, 37, 1, 39));
        Assert.Equal("$ rm", t.GetText(0, 0, 0, 3));   // sélection dans la première ligne seulement

        // Espaces au bord droit : ils font partie du texte ; un retour à la ligne demandé reste un saut de ligne.
        t.Feed("\r\n" + new string('x', 38) + "  b\r\n" + new string('y', 40) + "\r\nz");
        Assert.Equal(new string('x', 38) + "  b\n" + new string('y', 40) + "\nz", t.GetText(2, 0, 5, 39));

        t.Feed(string.Concat(Enumerable.Range(1, 9).Select(i => $"\r\n{i}")));
        Assert.Equal(5, t.ScrollbackCount);
        Assert.True(t.IsWrapped(-5));
        Assert.Equal(command, t.GetText(-5, 0, -4, 39));
    }

    /// <summary>
    /// Le repli est un attribut de la ligne : il reste quand le shell réécrit, insère ou supprime des caractères dans la
    /// commande, et disparaît quand la ligne est effacée jusqu'au bord droit.
    /// </summary>
    [Fact]
    public void WrapMarkSurvivesEditsAndIsClearedByErasing()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("0123456789abc");
        Assert.True(t.IsWrapped(0));

        t.Feed("\x1b[1;10HX");                     // dernière case réécrite
        Assert.True(t.IsWrapped(0));
        Assert.Equal("012345678Xabc", t.GetText(0, 0, 1, 9));

        t.Feed("\x1b[1;3H\x1b[2P");                // suppression : la marque ne glisse pas au milieu
        Assert.True(t.IsWrapped(0));
        Assert.Equal(CellFlags.None, t.GetLine(0)[7].Flags);
        Assert.Equal("0145678X  abc", t.GetText(0, 0, 1, 9));
        t.Feed("\x1b[1;3H\x1b[2@");                // insertion
        Assert.True(t.IsWrapped(0));

        t.Feed("\x1b[1;5H\x1b[K");                 // effacement jusqu'au bord droit
        Assert.False(t.IsWrapped(0));
        Assert.Equal("01\nabc", t.GetText(0, 0, 1, 9));

        t.Feed("\x1b[H0123456789abc");
        Assert.True(t.IsWrapped(0));
        t.Feed("\x1b[2J");
        Assert.False(t.IsWrapped(0));
    }

    /// <summary>
    /// Largeur changée (sans remise en forme des lignes) : les lignes de l'écran ne se poursuivent plus, celles de
    /// l'historique gardent leur repli ; un changement du nombre de lignes seul ne change rien.
    /// </summary>
    [Fact]
    public void ResizeKeepsTheWrapOnlyWhereTheTextIsUnchanged()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("0123456789abcde");
        t.Resize(10, 5);
        Assert.True(t.IsWrapped(0));

        t.Resize(20, 5);
        Assert.False(t.IsWrapped(0));
        Assert.Equal("0123456789\nabcde", t.GetText(0, 0, 1, 19));
        Assert.All(t.GetLine(0), cell => Assert.Equal(CellFlags.None, cell.Flags));

        var narrower = new TerminalEmulator(10, 3);
        narrower.Feed("0123456789abcde");
        narrower.Resize(6, 3);
        Assert.False(narrower.IsWrapped(0));
        Assert.Equal("012345\nabcde", narrower.GetText(0, 0, 1, 5));

        var history = new TerminalEmulator(10, 3);
        history.Feed("0123456789abcde\r\n1\r\n2\r\n3");
        history.Resize(20, 3);
        Assert.True(history.IsWrapped(-2));
        Assert.Equal("0123456789abcde", history.GetText(-2, 0, -1, 19));
    }

    /// <summary>« \e[3J » (Erase Saved Lines) vide l'historique seul : l'écran affiché reste.</summary>
    [Fact]
    public void EraseSavedLinesKeepsTheScreen()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("1\r\n2\r\n3\r\nabc\x1b[3J");

        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(["2", "3", "abc"], Screen(t));
        Assert.Equal((2, 3), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void ControlCharactersInsideCsiAreExecuted()
    {
        var t = new TerminalEmulator(10, 3);

        t.Feed("ab\x1b[\r1Cc");

        Assert.Equal("ac", Row(t, 0));
    }

    [Fact]
    public void IgnoresDcsAndUnknownSequences()
    {
        var t = new TerminalEmulator(10, 2);

        t.Feed("\x1bP1$r0m\x1b\\a\x1b[>4;1mb\x1b[2 qc\x1b=d");

        Assert.Equal("abcd", Row(t, 0));
    }

    [Fact]
    public void FullResetClearsEverything()
    {
        var t = new TerminalEmulator(5, 2);
        t.Feed("1\r\n2\r\n3\x1b[31m\x1b[?1h");

        t.Feed("\u001bcX");

        Assert.Equal(["X", ""], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);
        Assert.False(t.ApplicationCursorKeys);
        Assert.Equal(TerminalColor.Default, t.GetLine(0)[0].Foreground);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(28)]
    public void EraseMarkerErasesTheTypedCommandFromTheMarkedLine(int length)
    {
        // Le serveur peut couper l'écho sur plus de lignes que ZillaTerm ne le prévoirait : tout part quand même.
        var t = new TerminalEmulator(10, 6);
        t.Feed("out1\r\nout2\r\n$ ");
        t.MarkEraseFromCursorLine();

        t.Feed(new string('x', length) + "\r\n" + WorkingDirectory.EraseMarker + "$ ");

        Assert.Equal(["out1", "out2", "$", "", "", ""], Screen(t));
        Assert.Equal((2, 2), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void EraseMarkerFollowsTheMarkedLineWhenTheEchoScrolls()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("out1\r\nout2\r\nout3\r\n$ ");
        t.MarkEraseFromCursorLine();

        t.Feed(new string('y', 15) + "\r\n");
        Assert.Equal(2, t.ScrollbackCount);
        t.Feed(WorkingDirectory.EraseMarker + "$ ");

        Assert.Equal(["out3", "$", "", ""], Screen(t));
        Assert.Equal("out2", Row(t, -1));
    }

    [Fact]
    public void EraseMarkerIsIgnoredOnceTheMarkWasCancelledAndReportsOnlyArmedMarks()
    {
        var t = new TerminalEmulator(10, 3);
        int received = 0;
        t.EraseMarkerReceived += () => received++;

        t.Feed("$ ");
        t.MarkEraseFromCursorLine();
        t.CancelEraseMark();
        t.Feed("cmd\r\n" + WorkingDirectory.EraseMarker);
        Assert.Equal(["$ cmd", "", ""], Screen(t));
        Assert.Equal(0, received);

        t.MarkEraseFromCursorLine();
        t.Feed("x\r\n" + WorkingDirectory.EraseMarker + WorkingDirectory.EraseMarker);
        Assert.Equal(["$ cmd", "", ""], Screen(t));
        Assert.Equal(1, received);

        // En écran alternatif, rien n'est effacé, mais la fin de l'attente est signalée.
        t.MarkEraseFromCursorLine();
        t.Feed("\x1b[?1049hvi" + WorkingDirectory.EraseMarker);
        Assert.Equal(2, received);
    }

    [Fact]
    public void EraseMarkerActsOnlyOnceAfterAMarkOnTheMainScreen()
    {
        var t = new TerminalEmulator(10, 3);

        t.Feed("abc" + WorkingDirectory.EraseMarker);
        Assert.Equal(["abc", "", ""], Screen(t));

        t.MarkEraseFromCursorLine();
        t.Feed("\r\n" + WorkingDirectory.EraseMarker + "def\r\n" + WorkingDirectory.EraseMarker);
        Assert.Equal(["def", "", ""], Screen(t));

        t.MarkEraseFromCursorLine();
        t.Feed("\x1b[?1049hvim" + WorkingDirectory.EraseMarker);
        Assert.Equal(["", "vim", ""], Screen(t));
    }

    /// <summary>
    /// Fenêtre réduite pendant vim : l'écran principal est redimensionné comme s'il était affiché (le haut part dans
    /// l'historique), et en sortant l'invite revient sous la commande, pas sur une ancienne ligne.
    /// </summary>
    [Fact]
    public void ResizeDuringTheAlternateScreenKeepsTheMainScreenAndItsCursor()
    {
        var t = new TerminalEmulator(10, 6);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5\r\n$ vim");
        t.Feed("\x1b[?1049h\x1b[H~\r\n~\r\n~\r\n~\r\n~\r\n~ file");

        t.Resize(10, 3);

        Assert.True(t.IsAlternateScreen);
        Assert.Equal(["~", "~", "~ file"], Screen(t));
        t.Feed("\x1b[?1049l");
        Assert.Equal(["4", "5", "$ vim"], Screen(t));
        Assert.Equal((2, 5), (t.CursorRow, t.CursorColumn));
        Assert.Equal(["1", "2", "3"], Enumerable.Range(-3, 3).Select(r => Row(t, r)));
        t.Feed("\r\n$ ");
        Assert.Equal(["5", "$ vim", "$"], Screen(t));

        // Agrandie pendant less : l'écran principal garde ses lignes, le curseur sa place.
        t.Feed("\x1b[?1049h\x1b[2J\x1b[Hless");
        t.Resize(12, 5);
        t.Feed("\x1b[?1049l");
        Assert.Equal(["5", "$ vim", "$", "", ""], Screen(t));
        Assert.Equal((2, 2), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void ResizeDuringTheOtherAlternateScreenModesKeepsTheMainScreen()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("a\r\nb\r\nc\r\n$ top\x1b[?1047h\x1b[Htop");

        t.Resize(10, 2);
        t.Feed("\x1b[?1047l");

        Assert.Equal(["c", "$ top"], Screen(t));
        Assert.Equal(["a", "b"], Enumerable.Range(-2, 2).Select(r => Row(t, r)));
    }

    [Fact]
    public void SavedCursorFollowsTheLinesWhenTheScreenShrinks()
    {
        var t = new TerminalEmulator(10, 5);
        t.Feed("1\r\n2\r\n3\u001b7\r\n4\r\n5");

        t.Resize(10, 3);
        t.Feed("\u001b8X");

        Assert.Equal(["3X", "4", "5"], Screen(t));
    }

    /// <summary>Chaque écran a son curseur sauvegardé (xterm) : un programme qui sauvegarde le sien n'écrase pas celui du shell.</summary>
    [Fact]
    public void EachScreenHasItsOwnSavedCursor()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("$ vi");

        t.Feed("\x1b[?1049h\x1b[3;3H\u001b7\x1b[HX\u001b8Y\x1b[?1049l");

        Assert.Equal((0, 4), (t.CursorRow, t.CursorColumn));
        Assert.Equal("$ vi", Row(t, 0));
    }

    /// <summary>
    /// Seul le défilement du texte (saut de ligne, CSI S) en haut de l'écran principal alimente l'historique ; jamais la
    /// suppression de lignes (CSI M), le défilement vers le bas (CSI T, RI), une zone qui ne commence pas en haut, ni
    /// l'écran alternatif.
    /// </summary>
    [Fact]
    public void OnlyTextScrollingOffTheTopOfTheMainScreenFeedsTheHistory()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("a\r\nb\r\nc\r\nd");

        t.Feed("\x1b[H\x1b[2M");
        Assert.Equal(["c", "d", "", ""], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);

        t.Feed("\x1b[2T\x1b[H\u001bM");
        Assert.Equal(["", "", "", "c"], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);

        t.Feed("\x1b[2;4r\x1b[4;1H\n\n\x1b[2;1H\x1b[3M\x1b[S");
        Assert.Equal(0, t.ScrollbackCount);

        t.Feed("\x1b[r\x1b[Hx\x1b[2S");
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal(["x", ""], Enumerable.Range(-2, 2).Select(r => Row(t, r)));

        t.Feed("\x1b[?1049h1\r\n2\r\n3\r\n4\r\n5\x1b[H\x1b[M\x1b[S\x1b[?1049l");
        Assert.Equal(2, t.ScrollbackCount);
    }

    /// <summary>
    /// Zone de défilement qui commence en haut de l'écran, au-dessus d'une ligne d'état (barre de progression d'apt) :
    /// les lignes qui en sortent vont dans l'historique, comme dans xterm ; la ligne d'état reste.
    /// </summary>
    [Fact]
    public void ScrollRegionFromTheTopFeedsTheHistory()
    {
        var t = new TerminalEmulator(12, 4);
        t.Feed("\x1b[4;1HProgress\x1b[1;3r\x1b[1;1Hl1\r\nl2\r\nl3\r\nl4\r\nl5");

        Assert.Equal(["l3", "l4", "l5", "Progress"], Screen(t));
        Assert.Equal(["l1", "l2"], Enumerable.Range(-2, 2).Select(r => Row(t, r)));

        // Sans ligne effacée de l'historique par CSI M en haut de cette zone.
        t.Feed("\x1b[1;1H\x1b[M");
        Assert.Equal(["l4", "l5", "", "Progress"], Screen(t));
        Assert.Equal(2, t.ScrollbackCount);
    }
}

public class TerminalSupportTests
{
    [Theory]
    [InlineData(TerminalKey.Up, false, false, false, false, "\x1b[A")]
    [InlineData(TerminalKey.Up, false, false, false, true, "\x1bOA")]
    [InlineData(TerminalKey.Right, false, false, true, false, "\x1b[1;5C")]
    [InlineData(TerminalKey.Delete, false, false, false, false, "\x1b[3~")]
    [InlineData(TerminalKey.PageUp, true, false, false, false, "\x1b[5;2~")]
    [InlineData(TerminalKey.F1, false, false, false, false, "\x1bOP")]
    [InlineData(TerminalKey.F5, false, false, false, false, "\x1b[15~")]
    [InlineData(TerminalKey.Backspace, false, false, false, false, "\x7f")]
    [InlineData(TerminalKey.Tab, true, false, false, false, "\x1b[Z")]
    [InlineData(TerminalKey.Enter, false, false, false, false, "\r")]
    public void EncodesKeys(TerminalKey key, bool shift, bool alt, bool ctrl, bool app, string expected) =>
        Assert.Equal(expected, TerminalKeys.Encode(key, shift, alt, ctrl, app));

    [Theory]
    [InlineData('c', "\x03")]
    [InlineData('D', "\x04")]
    [InlineData('[', "\x1b")]
    [InlineData(' ', "\0")]
    [InlineData('é', null)]
    public void EncodesControlCharacters(char c, string? expected) => Assert.Equal(expected, TerminalKeys.Control(c));

    [Fact]
    public void PasteNormalizesNewlinesAndBrackets()
    {
        Assert.Equal("a\rb\rc", TerminalKeys.Paste("a\r\nb\nc", bracketed: false));
        Assert.Equal("\x1b[200~ls\r[201~\x1b[201~", TerminalKeys.Paste("ls\n\x1b[201~", bracketed: true));
    }

    [Fact]
    public void PastedTextCannotLeaveTheBracketsNorRunACommand()
    {
        // Marqueur de fin imbriqué (« \e[20\e[201~1~ ») puis Ctrl-O : avant, le collage se fermait et « id » s'exécutait.
        Assert.Equal("\x1b[200~[20[201~1~ id\x1b[201~", TerminalKeys.Paste("\x1b[20\x1b[201~1~ id\x0f", bracketed: true));
        Assert.Equal("a\tb\rc", TerminalKeys.Paste("a\tb\u009b\u007f\nc\0", bracketed: false));
        Assert.Equal("echo ok", TerminalKeys.CleanPaste("echo ok"));
    }

    [Theory]
    [InlineData(-1, true, false, 0xCCCCCCu)]
    [InlineData(-1, false, false, 0x0C0C0Cu)]
    [InlineData(1, true, true, 0xE74856u)]
    [InlineData(1, false, true, 0xC50F1Fu)]
    [InlineData(196, true, false, 0xFF0000u)]
    [InlineData(232, true, false, 0x080808u)]
    public void MapsColors(int color, bool foreground, bool bold, uint expected) =>
        Assert.Equal(expected, TerminalColor.ToRgb(color, foreground, bold));

    [Fact]
    public void InjectionCommandIsSingleLineStartingWithSpace()
    {
        var command = WorkingDirectory.InjectionCommand();

        Assert.StartsWith(" ", command);
        Assert.EndsWith("\r", command);
        Assert.DoesNotContain('\n', command);
        Assert.EndsWith(WorkingDirectory.EraseMarker.Replace("\x1b", "\\033").Replace("\a", "\\007") + "'\r", command);
        Assert.Contains("PROMPT_COMMAND=", command);
    }

    [Fact]
    public void InjectionCommandIsIdempotent()
    {
        var command = WorkingDirectory.InjectionCommand();

        Assert.Contains("case \";${PROMPT_COMMAND-};\" in *\";__catosc7;\"*)", command);
        Assert.Contains("precmd_functions[(I)__catosc7]", command);
    }

    [Fact]
    public void InjectionCommandRunsEachPartOnlyInItsShellFamily()
    {
        // csh refuse toute la ligne s'il y lit du code bash : chaque partie passe entre apostrophes à eval.
        var command = WorkingDirectory.InjectionCommand();

        Assert.StartsWith(" test \"\\\\\" = '\\\\' || eval '", command);
        Assert.Contains(";test \"\\\\\" = '\\\\' && test -n \"$shell\" && eval '", command);
        Assert.EndsWith(";printf '\\033]6973;\\007'\r", command);

        var words = ShellWords(command, fish: false);
        var posix = words[words.IndexOf("eval") + 1];
        var csh = words[words.LastIndexOf("eval") + 1];
        Assert.StartsWith("__catosc7(){ printf '\\033]7;%s\\007' \"$PWD\";};", posix);
        Assert.Contains("PROMPT_COMMAND=\"__catosc7${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"", posix);
        Assert.StartsWith("if ($?tcsh && ! $?__catosc7 && `alias cwdcmd | wc -c` == 0) set __catosc7;", csh);
        Assert.Contains("alias cwdcmd 'printf \"\\033]7;%s\\007\" \"$cwd\"'", csh);
    }

    [Fact]
    public void InjectionCommandReadsNoUnsetVariableInPosixShells()
    {
        // Un profil avec « set -u » interrompt la ligne à la première variable non définie, avant le marqueur
        // d'effacement : la commande resterait affichée (« shell: parameter not set »).
        var command = WorkingDirectory.InjectionCommand("/srv");

        // Un shell POSIX n'exécute que le test des familles, sans variable, puis la partie passée à eval.
        Assert.DoesNotContain("$", command[..command.IndexOf(" || eval ", StringComparison.Ordinal)]);
        var words = ShellWords(command, fish: false);
        var posix = words[words.IndexOf("eval") + 1];
        // Dans cette partie : PWD, toujours définie, ou une forme avec valeur par défaut (${X-}, ${X:+…}).
        var unguarded = System.Text.RegularExpressions.Regex.Replace(posix, @"\$PWD\b|\$\{\w+(\[[^\]]*\])?(-|:\+)[^}]*\}", "");
        Assert.DoesNotMatch(@"\$\{?[A-Za-z_]", unguarded);
        Assert.Contains("[ -z \"${ZSH_VERSION-}\" ]||eval", posix);
    }

    [Theory]
    [InlineData("/var/log")]
    [InlineData("it's a!b\\c")]
    [InlineData("-dash")]
    public void InjectionCommandQuotesTheStartDirectoryForEveryShell(string directory)
    {
        var command = WorkingDirectory.InjectionCommand(directory);

        // Même découpage pour les shells POSIX et csh que pour fish (« \ » entre apostrophes) ; chaque « ! » est
        // précédé de « \ », sinon bash, zsh et csh y verraient l'historique.
        var words = ShellWords(command, fish: false);
        var fishWords = ShellWords(command, fish: true);
        Assert.Equal(words.Count, fishWords.Count);
        // Seul le test des familles de shell est lu autrement par fish (« '\\' » : « \ »), c'est voulu.
        Assert.All(Enumerable.Range(0, words.Count).Where(i => words[i] != fishWords[i]),
            i => Assert.Equal(["test", "\"\\\"", "=", "\\\\"], words[(i - 3)..(i + 1)]));
        Assert.DoesNotMatch(@"(?<!\\)!", command);

        var posix = words[words.IndexOf("eval") + 1];
        Assert.StartsWith($"cd -- {WorkingDirectory.ShellQuote(directory)} 2>/dev/null||:;", posix);
        var csh = words[words.LastIndexOf("eval") + 1];
        var cshPath = WorkingDirectory.ShellQuote(directory.StartsWith('-') ? "./" + directory : directory).Replace("!", "\\!");
        Assert.Contains($"if (-d {cshPath}) cd {cshPath};", csh);

        // fish : « cd » seul, ni shell POSIX ni csh (pas de variable « shell »).
        var fish = words.LastIndexOf("cd");
        Assert.Equal(["test", "\"\\\"", "=", "\\\\", "&&", "test", "-z", "\"$shell\"", "&&", "cd"], words[(fish - 9)..(fish + 1)]);
        Assert.Equal(directory.StartsWith('-') ? "./" + directory : directory, words[fish + 1]);
    }

    /// <summary>Mots d'une ligne de commande : apostrophes, et « \ » hors apostrophes (fish : « \' » et « \\ » aussi dedans).</summary>
    private static List<string> ShellWords(string line, bool fish)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '\'')
                {
                    quoted = false;
                }
                else if (fish && c == '\\' && i + 1 < line.Length && line[i + 1] is '\'' or '\\')
                {
                    word.Append(line[++i]);
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c == '\'')
            {
                quoted = any = true;
            }
            else if (c == '\\' && i + 1 < line.Length)
            {
                word.Append(line[++i]);
                any = true;
            }
            else if (c is ' ' or ';' or '\r')
            {
                if (any)
                {
                    words.Add(word.ToString());
                }

                word.Clear();
                any = false;
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        Assert.False(quoted);
        if (any)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    [Theory]
    [InlineData("[root@srv01 log]# ", true)]
    [InlineData("jdupont@srv01:~$", true)]
    [InlineData("srv01% ", true)]
    [InlineData("> ", true)]
    [InlineData("[root@srv01 log]# ls", false)]
    [InlineData("Password:", false)]
    [InlineData("PSM for SSH (demo): this session is recorded.", false)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    public void LooksLikePrompt(string text, bool expected) => Assert.Equal(expected, WorkingDirectory.LooksLikePrompt(text));
}
