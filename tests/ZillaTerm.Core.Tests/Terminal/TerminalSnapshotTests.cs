using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Tests.Terminal;

/// <summary>
/// Instantané de l'écran pour une session partagée : rejoué dans un émulateur neuf de même taille, il donne le même
/// écran (cases, couleurs, attributs, lignes repliées), les mêmes modes et le même curseur ; la suite du flux s'y
/// applique de la même façon.
/// </summary>
public sealed class TerminalSnapshotTests
{
    private static void AssertSame(TerminalEmulator expected, TerminalEmulator actual)
    {
        Assert.Equal((expected.Columns, expected.Rows), (actual.Columns, actual.Rows));
        for (int row = 0; row < expected.Rows; row++)
        {
            var a = expected.GetLine(row);
            var b = actual.GetLine(row);
            for (int column = 0; column < expected.Columns; column++)
            {
                Assert.True(expected.CellText(a[column]) == actual.CellText(b[column]) && a[column].Foreground == b[column].Foreground
                            && a[column].Background == b[column].Background && a[column].Flags == b[column].Flags,
                    $"case {row},{column} : « {expected.CellText(a[column])} » {a[column].Foreground}/{a[column].Background}/{a[column].Flags} "
                    + $"au lieu de « {actual.CellText(b[column])} » {b[column].Foreground}/{b[column].Background}/{b[column].Flags}");
            }
        }

        Assert.Equal((expected.CursorRow, expected.CursorColumn, expected.CursorVisible), (actual.CursorRow, actual.CursorColumn, actual.CursorVisible));
        Assert.Equal((expected.ApplicationCursorKeys, expected.BracketedPaste, expected.AutoWrap, expected.IsAlternateScreen),
            (actual.ApplicationCursorKeys, actual.BracketedPaste, actual.AutoWrap, actual.IsAlternateScreen));
    }

    private static TerminalEmulator Replay(TerminalEmulator source)
    {
        var copy = new TerminalEmulator(source.Columns, source.Rows);
        copy.Feed(source.Snapshot());
        return copy;
    }

    [Fact]
    public void ShellScreenIsReproduced()
    {
        var source = new TerminalEmulator(40, 8);
        source.Feed("\x1b[1;32mjdoe@srv01\x1b[0m:\x1b[34m~\x1b[0m$ ls --color\r\n");
        source.Feed("\x1b[38;5;208morange\x1b[0m \x1b[38;2;10;20;30;48;2;200;100;50mvraie\x1b[0m \x1b[7minverse\x1b[0m \x1b[4;3msouligné\x1b[0m\r\n");
        source.Feed("中文 é\u0301 😀 fin\r\n");
        source.Feed("une ligne bien plus longue que quarante colonnes, repliée automatiquement\r\n");
        source.Feed("\x1b[44m  fond bleu jusqu'au bout\x1b[K\x1b[0m\r\n$ \x1b[?2004h");
        var copy = Replay(source);
        AssertSame(source, copy);
        Assert.True(copy.IsWrapped(3));

        // La suite du flux s'applique pareil des deux côtés.
        const string more = "echo ok\r\nok\r\n$ ";
        source.Feed(more);
        copy.Feed(more);
        AssertSame(source, copy);
    }

    [Fact]
    public void FullScreenProgramIsReproducedAndExitRestoresTheShell()
    {
        var source = new TerminalEmulator(30, 6);
        source.Feed("$ vim /etc/hosts\r\n");
        source.Feed("\x1b[?1049h\x1b[?1h\x1b[2;5r\x1b[H\x1b[2J127.0.0.1 localhost\r\n\x1b[1m~\x1b[0m\r\n\x1b[6;1H\"hosts\" 2L");
        source.Feed("\x1b[1;5H\x1b[?25l");
        var copy = Replay(source);
        AssertSame(source, copy);

        // Les lignes ajoutées après coup défilent dans la même zone.
        const string scroll = "\x1b[5;1H\r\nnouvelle ligne";
        source.Feed(scroll);
        copy.Feed(scroll);
        AssertSame(source, copy);

        const string quit = "\x1b[r\x1b[?25h\x1b[?1l\x1b[?1049l";
        source.Feed(quit);
        copy.Feed(quit);
        AssertSame(source, copy);
        Assert.Equal("$ vim /etc/hosts", copy.GetText(0, 0, 0, 29));
    }

    [Fact]
    public void LineDrawingAndFedEventsFollow()
    {
        var source = new TerminalEmulator(20, 4);
        var fed = new List<string>();
        int resized = 0;
        source.Fed += fed.Add;
        source.Resized += () => resized++;
        source.Feed("\x1b(0lqqk\x1b(B box\r\n\x1b(0");
        var copy = Replay(source);
        AssertSame(source, copy);
        // Le jeu de caractères graphique reste actif dans la copie : « x » dessine un trait vertical.
        source.Feed("x");
        copy.Feed("x");
        AssertSame(source, copy);
        Assert.Equal(["\x1b(0lqqk\x1b(B box\r\n\x1b(0", "x"], fed);
        source.Resize(20, 4);
        Assert.Equal(0, resized);
        source.Resize(30, 5);
        Assert.Equal(1, resized);
    }
}
