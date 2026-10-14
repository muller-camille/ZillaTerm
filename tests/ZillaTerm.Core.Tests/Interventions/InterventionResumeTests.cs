using System.Globalization;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Tests.Interventions;

/// <summary>Enregistrement repris après un arrêt de ZillaTerm, et plusieurs enregistrements dans un même rapport.</summary>
public sealed partial class InterventionResumeTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-resume-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// ZillaTerm arrêté pendant l'écriture : le journal n'a pas de fin et se termine par un bloc coupé. Il est proposé à la
    /// reprise ; la suite s'écrit dans le même journal (bloc coupé retiré, numéros de bloc à la suite) et se relit d'un
    /// seul tenant, sans être marquée abîmée.
    /// </summary>
    [Fact]
    public void InterruptedJournalIsResumedInPlace()
    {
        var protector = new XorProtector();
        string path;
        // Pas de Stop ni de Dispose : le minuteur d'écriture est arrêté à la main, comme un processus tué.
        var crashed = InterventionJournal.Start(_dir, protector, T0, terminalTextLimit: 100);
        path = crashed.Path;
        crashed.Write(new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"));
        crashed.Write(new InterventionEntry(T0.AddSeconds(5), InterventionKind.Title, "", "INC0012345"));
        crashed.Write(new InterventionEntry(T0.AddSeconds(10), InterventionKind.Terminal, "root@srv01", new string('x', 60)));
        crashed.Flush();
        crashed.Write(new InterventionEntry(T0.AddSeconds(20), InterventionKind.Note, "", "avant l'arrêt"));
        crashed.Flush();
        crashed.Dispose();
        using (var tail = new FileStream(path, FileMode.Append))
        {
            // Bloc coupé : sa longueur annonce plus que ce qui a été écrit.
            tail.Write([0x00, 0x10, 0x00, 0x00, 1, 2, 3]);
        }

        Assert.True(InterventionJournal.IsUnfinished(path, protector));
        var found = InterventionJournal.FindUnfinished(_dir, protector, DateTime.UtcNow);
        Assert.Equal(path, found?.File.Path);
        Assert.Equal(T0.AddSeconds(20), found?.LastEvent);

        using (var resumed = InterventionJournal.Resume(path, protector, out var before, terminalTextLimit: 100))
        {
            Assert.Equal(["jdoe", "INC0012345", new string('x', 60), "avant l'arrêt"], before.Entries.Select(e => e.Text));
            Assert.True(before.Unfinished);
            Assert.Equal(4, resumed.Count);
            resumed.Write(new InterventionEntry(T0.AddHours(1), InterventionKind.Action, "", "repris"));
            // Le plafond du texte des terminaux compte ce qui était déjà écrit : 60 + 60 > 100.
            resumed.Write(new InterventionEntry(T0.AddHours(1).AddSeconds(1), InterventionKind.Terminal, "root@srv01", new string('y', 60)));
            Assert.True(resumed.TerminalTextCapped);
            resumed.Stop(new InterventionEntry(T0.AddHours(2), InterventionKind.Stopped, "", "fin"));
        }

        var recording = InterventionJournal.Read(path, protector);
        Assert.False(recording.Damaged);
        Assert.False(recording.Unfinished);
        Assert.Equal(["jdoe", "INC0012345", new string('x', 60), "avant l'arrêt", "repris", "…", "fin"], recording.Entries.Select(e => e.Text));
        Assert.Equal("INC0012345", recording.Title);
        Assert.False(InterventionJournal.IsUnfinished(path, protector));
        Assert.Null(InterventionJournal.FindUnfinished(_dir, protector, DateTime.UtcNow));
    }

    /// <summary>
    /// Seul le plus récent journal est proposé, s'il a été écrit il y a moins de 12 heures ; un journal fini, vide ou d'un
    /// ancien format ne l'est pas.
    /// </summary>
    [Fact]
    public void OnlyRecentUnfinishedJournalsAreOffered()
    {
        var protector = new XorProtector();
        var journal = InterventionJournal.Start(_dir, protector, T0);
        journal.Write(new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"));
        journal.Flush();
        journal.Dispose();
        Assert.NotNull(InterventionJournal.FindUnfinished(_dir, protector, DateTime.UtcNow));
        File.SetLastWriteTimeUtc(journal.Path, DateTime.UtcNow - InterventionJournal.ResumeWindow - TimeSpan.FromMinutes(1));
        Assert.Null(InterventionJournal.FindUnfinished(_dir, protector, DateTime.UtcNow));
        File.SetLastWriteTimeUtc(journal.Path, DateTime.UtcNow);

        // Un journal plus récent, fini : rien à reprendre (l'ancien, interrompu, ne l'est plus non plus).
        using (var finished = InterventionJournal.Start(_dir, protector, T0.AddHours(1)))
        {
            finished.Write(new InterventionEntry(T0.AddHours(1), InterventionKind.Started, "", "jdoe"));
            finished.Stop(new InterventionEntry(T0.AddHours(2), InterventionKind.Stopped, "", "fin"));
        }

        Assert.Null(InterventionJournal.FindUnfinished(_dir, protector, DateTime.UtcNow));

        var empty = InterventionJournal.Start(_dir, protector, T0.AddHours(3));
        empty.Dispose();
        Assert.False(InterventionJournal.IsUnfinished(empty.Path, protector));
        var old = Path.Combine(_dir, "intervention-20261014-060000.zij");
        File.WriteAllBytes(old, "ZTINTV2\n"u8.ToArray());
        Assert.False(InterventionJournal.IsUnfinished(old, protector));
        Assert.Throws<InvalidDataException>(() => InterventionJournal.Resume(old, protector, out _).Dispose());
    }

    /// <summary>
    /// Deux enregistrements (la nuit, puis le matin) dans un même rapport : leurs événements à la suite, leurs titres, la
    /// liste des enregistrements, et un lecteur par écran même si les deux journaux ont le même numéro d'écran.
    /// </summary>
    [Fact]
    public void SeveralRecordingsMakeOneReport()
    {
        InterventionRecording Recording(DateTime start, string title, byte fill)
        {
            const int width = 128, height = 64;
            var screen = new byte[width * height * 4];
            Array.Fill(screen, fill);
            var frame = new ScreenTileEncoder(stream: 1).Encode(start.AddSeconds(2), screen, width, height, width * 4)!;
            return new InterventionRecording($"{title}.zij",
            [
                new(start, InterventionKind.Started, "", "jdoe"),
                new(start.AddSeconds(1), InterventionKind.Title, "", title),
                new(start.AddSeconds(2), InterventionKind.Screen, "srv01", "", frame.Data),
                new(start.AddSeconds(3), InterventionKind.Note, "", "note " + title),
                new(start.AddMinutes(30), InterventionKind.Stopped, "", "fin"),
            ], false, false);
        }

        var night = Recording(T0, "INC1 nuit", 0x20);
        var morning = Recording(T0.AddHours(6), "INC1 matin", 0x80);

        var merged = InterventionRecording.Merge([morning, night]);

        Assert.Equal([night, morning], merged.Parts);
        Assert.Equal(T0, merged.Start);
        Assert.Equal(T0.AddHours(6).AddMinutes(30), merged.End);
        Assert.Equal("INC1 nuit / INC1 matin", merged.Title);
        Assert.Same(night, InterventionRecording.Merge([night]));

        var html = InterventionReport.ToHtml(merged, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.Contains("This report brings together 2 recordings:", html, StringComparison.Ordinal);
        Assert.Contains("— INC1 matin</li>", html, StringComparison.Ordinal);
        Assert.Equal(2, Sections().Matches(html).Count);
        Assert.Contains("note INC1 nuit", html, StringComparison.Ordinal);
        Assert.Contains("note INC1 matin", html, StringComparison.Ordinal);

        // Masquage : appliqué à chaque enregistrement, la réunion est gardée.
        var redacted = InterventionRedaction.Apply(merged, "[masked]", out int count);
        Assert.Equal(0, count);
        Assert.Equal(2, redacted.Parts.Count);
    }

    [GeneratedRegex("<section class=\"screen\"")]
    private static partial Regex Sections();

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
