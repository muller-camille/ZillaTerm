using System.Text;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public sealed class TailViewTests
{
    [Theory]
    [InlineData("2026-10-05 14:32:01,123 [main] ERROR com.app.Db - pool exhausted", TailLevel.Error)]
    [InlineData("Oct  5 14:32:01 srv01 kernel: FATAL oops", TailLevel.Error)]
    [InlineData("{\"time\":\"14:32\",\"level\":\"error\",\"msg\":\"x\"}", TailLevel.Error)]
    [InlineData("14:32 CRIT disk full", TailLevel.Error)]
    [InlineData("14:32 WARN slow request", TailLevel.Warning)]
    [InlineData("14:32 [Warning] retrying", TailLevel.Warning)]
    [InlineData("14:32 WARN retry after ERROR", TailLevel.Warning)]
    [InlineData("14:32 INFO started, error_count=0", TailLevel.None)]
    [InlineData("14:32 INFO terror and warnings", TailLevel.None)]
    [InlineData("14:32 DEBUG err=0", TailLevel.None)]
    [InlineData("", TailLevel.None)]
    public void DetectsTheLevel(string line, TailLevel expected) => Assert.Equal(expected, TailText.DetectLevel(line));

    [Fact]
    public void LevelIsLookedForAtTheStartOfTheLine()
    {
        var line = "14:32 INFO " + new string('x', TailText.LevelSearchLength) + " ERROR";
        Assert.Equal(TailLevel.None, TailText.DetectLevel(line));
    }

    [Fact]
    public void ParsesAndFindsTerms()
    {
        var terms = TailText.ParseTerms(" ERROR, OutOfMemory ; Connection refused,, error ");
        Assert.Equal(["ERROR", "OutOfMemory", "Connection refused"], terms);
        Assert.Empty(TailText.ParseTerms(null));

        Assert.True(TailText.ContainsAny("java.lang.outofmemory: heap", terms));
        Assert.False(TailText.ContainsAny("all good", terms));

        // Occurrences multiples trouvées ; plages qui se chevauchent ou se touchent réunies.
        Assert.Equal([(0, 10), (11, 7)], TailText.FindTerms("ERRORerror connect", ["error", "connect", "conn"]));
        Assert.Equal([(0, 6)], TailText.FindTerms("ababab", ["ab"]));
        Assert.Equal([(0, 2), (3, 2)], TailText.FindTerms("ab ab", ["AB"]));
        Assert.Empty(TailText.FindTerms("abc", []));
    }

    [Fact]
    public void SplitsALineIntoSegments()
    {
        // « pool ERROR at db01 » : ERROR surligné, « at db » trouvé par la recherche, « db » chevauche les deux.
        var segments = TailText.Segments(18, [(5, 5)], [(11, 5)]);
        Assert.Equal(
        [
            new TailSegment(0, 5, false, false),
            new TailSegment(5, 5, true, false),
            new TailSegment(10, 1, false, false),
            new TailSegment(11, 5, false, true),
            new TailSegment(16, 2, false, false),
        ], segments);

        Assert.Equal([new TailSegment(0, 4, true, true)], TailText.Segments(4, [(0, 4)], [(0, 9)]));
        Assert.Empty(TailText.Segments(0, [], []));
    }

    [Fact]
    public void PatternsMatchTextOrRegex()
    {
        var text = TailPattern.Create("Refused", regex: false, out var error)!;
        Assert.Null(error);
        Assert.True(text.IsMatch("connection refused"));
        Assert.True(text.IsMatch("a.b") == false);
        Assert.Equal([(11, 7)], text.Find("connection refused"));

        var regex = TailPattern.Create(@"user=\w+ (denied|refused)", regex: true, out error)!;
        Assert.True(regex.IsMatch("login USER=bob denied"));
        Assert.False(regex.IsMatch("login user=bob accepted"));
        Assert.Equal([(6, 16)], regex.Find("login user=bob refused"));

        Assert.Null(TailPattern.Create("(unclosed", regex: true, out error));
        Assert.NotNull(error);
        Assert.Null(TailPattern.Create("", regex: false, out error));
        Assert.Null(error);

        // Un point n'est qu'un point sans expression régulière.
        Assert.False(TailPattern.Create("a.c", regex: false, out _)!.IsMatch("abc"));
        Assert.True(TailPattern.Create("a.c", regex: true, out _)!.IsMatch("abc"));
    }

    [Fact]
    public void ASlowRegexIsAbandoned()
    {
        var slow = TailPattern.Create("^(a+)+$", regex: true, out _)!;
        var line = new string('a', 40) + "!";
        Assert.False(slow.IsMatch(line));
        Assert.True(slow.TimedOut);
        Assert.False(slow.IsMatch("aaa"));
        Assert.Empty(slow.Find("aaa"));
    }

    private static List<string> Run(TailFilter filter, params string[] lines)
    {
        var run = filter.Start<string>();
        var output = new List<TailShown<string>>();
        foreach (var line in lines)
        {
            run.Feed(line, line, always: line.StartsWith('#'), output);
        }

        return output.Select(s => s.Kind switch
        {
            TailShownKind.Separator => "--",
            TailShownKind.Context => "  " + s.Item,
            _ => s.Item!,
        }).ToList();
    }

    private static TailPattern? Text(string text) => TailPattern.Create(text, false, out _);

    [Fact]
    public void WithoutFilterEveryLineIsShown()
    {
        Assert.False(TailFilter.None.IsActive);
        Assert.Equal(["a", "b"], Run(TailFilter.None, "a", "b"));
    }

    [Fact]
    public void FiltersAndExcludes()
    {
        var filter = new TailFilter(Text("error"), Text("timeout"), 0);
        Assert.Equal(["ERROR db", "error disk"], Run(filter, "info", "ERROR db", "error timeout", "error disk", "warn"));

        // Exclusion seule (grep -v).
        Assert.Equal(["a", "c"], Run(new TailFilter(null, Text("debug"), 0), "a", "DEBUG b", "c"));

        // Les repères restent affichés.
        Assert.Equal(["# repère", "error"], Run(filter, "info", "# repère", "error"));
    }

    [Fact]
    public void ShowsContextLikeGrep()
    {
        var filter = new TailFilter(Text("error"), null, 1);
        Assert.Equal(
            ["  2", "error 3", "  4", "--", "  7", "error 8", "error 9", "  10"],
            Run(filter, "1", "2", "error 3", "4", "5", "6", "7", "error 8", "error 9", "10", "11"));

        // Groupes contigus : pas de séparateur.
        Assert.Equal(["error 1", "  2", "error 3"], Run(filter, "error 1", "2", "error 3"));

        // Contexte borné.
        Assert.Equal(TailFilter.MaxContext, new TailFilter(null, null, 1000).Context);
        Assert.Equal(0, new TailFilter(null, null, -3).Context);
    }

    [Fact]
    public void ContextFollowsLinesAsTheyArrive()
    {
        var run = new TailFilter(Text("error"), null, 2).Start<string>();
        var output = new List<TailShown<string>>();
        run.Feed("a", "a", false, output);
        run.Feed("b", "b", false, output);
        Assert.Empty(output);

        run.Feed("error", "error", false, output);
        Assert.Equal(["a", "b", "error"], output.Select(s => s.Item));

        output.Clear();
        run.Feed("c", "c", false, output);
        Assert.Equal([TailShownKind.Context], output.Select(s => s.Kind));
    }

    [Fact]
    public void SplitsLinesAndKeepsTheUnfinishedOne()
    {
        var splitter = new TailLineSplitter();
        Assert.Equal(["un", "deux"], splitter.Push("un\ndeux\ntro"));
        Assert.Equal("tro", splitter.Partial);
        Assert.Empty(splitter.Push(""));
        Assert.Equal(["trois"], splitter.Push("is\n"));
        Assert.Equal("", splitter.Partial);
        Assert.Null(splitter.Flush());

        splitter.Push("sans fin");
        Assert.Equal("sans fin", splitter.Flush());
        Assert.Equal("", splitter.Partial);
    }

    /// <summary>
    /// Fichier qui grossit sans fin de ligne (barre de progression, fichier piégé) ou lignes géantes : chaque ligne est
    /// coupée avec une mention, son reste ignoré jusqu'à sa fin ; la ligne en cours reste bornée.
    /// </summary>
    [Fact]
    public void CutsLinesThatAreTooLong()
    {
        const int max = TailLineSplitter.MaxLineLength;
        var splitter = new TailLineSplitter();
        var megabyte = new string('x', 1024 * 1024);
        var lines = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            lines.AddRange(splitter.Push(megabyte));
            Assert.True(splitter.Partial.Length <= max);
        }

        Assert.Equal(new string('x', max) + TailLineSplitter.CutMark, Assert.Single(lines));

        // Fin de la ligne coupée : les suivantes reprennent normalement.
        Assert.Equal(["suite"], splitter.Push("xxx\nsuite\nen cours"));
        Assert.Equal("en cours", splitter.Partial);
        Assert.Equal("en cours", splitter.Flush());

        // Ligne géante terminée dans le même relevé ; une ligne de la longueur permise reste entière ; un emoji n'est
        // pas coupé en deux.
        var exact = new string('e', max);
        var emoji = new string('a', max - 1) + "😀b";
        Assert.Equal([new string('y', max) + TailLineSplitter.CutMark, exact, new string('a', max - 1) + TailLineSplitter.CutMark, "ok"],
            splitter.Push($"{new string('y', 3 * max)}\n{exact}\n{emoji}\nok\n"));

        // Fichier relu (Flush) pendant une ligne coupée : la suite n'est plus ignorée.
        Assert.Single(splitter.Push(new string('z', max + 1)));
        Assert.Null(splitter.Flush());
        Assert.Equal(["après"], splitter.Push("après\n"));
    }

    [Fact]
    public void CutMarkTellsTheLength()
    {
        using var culture = UiCulture.Use("fr-FR");
        Assert.Equal($" … [ligne coupée après {TailLineSplitter.MaxLineLength:N0} caractères]", TailLineSplitter.CutMark);
    }

    [Fact]
    public async Task AFollowedFileResumesOnANewConnection()
    {
        var before = new MemoryFile();
        before.Append("1\n2\n");
        var tail = new FileTail(before);
        Assert.Equal("1\n2\n", (await tail.PollAsync(default)).Text);

        // Reconnexion : nouvel accès au même fichier, qui a grossi pendant la coupure.
        var after = new MemoryFile();
        after.Append("1\n2\n3\n");
        tail.Source = after;
        var update = await tail.PollAsync(default);
        Assert.Equal("3\n", update.Text);
        Assert.False(update.Restarted);
    }

    [Fact]
    public void RemembersFollowedFilesPerServer()
    {
        var saved = new SavedSession();
        for (int i = 0; i < SavedSession.MaxTailFiles + 3; i++)
        {
            saved.RememberTail($"/var/log/{i}.log");
        }

        saved.RememberTail("/var/log/5.log");
        Assert.Equal(SavedSession.MaxTailFiles, saved.TailFiles.Count);
        Assert.Equal("/var/log/5.log", saved.TailFiles[0]);
        Assert.Single(saved.TailFiles, "/var/log/5.log");
        Assert.DoesNotContain("/var/log/0.log", saved.TailFiles);
    }

    [Fact]
    public void TailSettingsSurviveSaving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-tail-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings { TailIndependentSession = true, TailHighlights = "db01", TailAlerts = "ERROR", TailAlertNotify = false };
            var saved = new SavedSession { Name = "srv01" };
            saved.RememberTail("/var/log/app.log");
            settings.Sessions.Add(saved);
            settings.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.True(loaded.TailIndependentSession);
            Assert.Equal("db01", loaded.TailHighlights);
            Assert.Equal("ERROR", loaded.TailAlerts);
            Assert.False(loaded.TailAlertNotify);
            Assert.True(loaded.TailLevelColors);
            Assert.Equal(["/var/log/app.log"], loaded.Sessions[0].TailFiles);

            File.WriteAllText(path, "{\"TailHighlights\":null,\"Sessions\":[{\"Name\":\"x\",\"TailFiles\":null}]}");
            loaded = AppSettings.Load(path);
            Assert.Equal("", loaded.TailHighlights);
            Assert.Empty(loaded.Sessions[0].TailFiles);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class MemoryFile : ITailSource
    {
        private readonly List<byte> _content = [];

        public void Append(string text) => _content.AddRange(Encoding.UTF8.GetBytes(text));

        public Task<long> GetSizeAsync(CancellationToken ct) => Task.FromResult((long)_content.Count);

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) =>
            Task.FromResult(_content.Skip((int)offset).Take(count).ToArray());
    }
}
