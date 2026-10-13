using System.Globalization;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed partial class InterventionNotesTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 13, 14, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Modifications appliquées : la note garde sa place et son heure avec son dernier texte, le texte d'origine et l'heure
    /// de la dernière modification sont gardés ; une modification dont la note manque reste une note à son heure.
    /// </summary>
    [Fact]
    public void EditsReplaceTheNoteTextInPlace()
    {
        var entries = new[]
        {
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(10), InterventionKind.Note, "", "Disque /var plein"),
            new InterventionEntry(T0.AddSeconds(20), InterventionKind.Note, "", "Redémarrage de httpd"),
            new InterventionEntry(T0.AddSeconds(30), InterventionKind.Note, "", "Disque /var plein sur srv01", Edits: T0.AddSeconds(10)),
            new InterventionEntry(T0.AddSeconds(40), InterventionKind.Note, "", "Disque /var plein sur srv01 (logs)", Edits: T0.AddSeconds(10)),
            new InterventionEntry(T0.AddSeconds(50), InterventionKind.Note, "", "Note perdue modifiée", Edits: T0.AddSeconds(5)),
        };

        var applied = InterventionNotes.Apply(entries, out var edits);

        Assert.Equal(["jdoe", "Disque /var plein sur srv01 (logs)", "Redémarrage de httpd", "Note perdue modifiée"], applied.Select(e => e.Text));
        Assert.Equal([T0, T0.AddSeconds(10), T0.AddSeconds(20), T0.AddSeconds(50)], applied.Select(e => e.Time));
        Assert.All(applied, e => Assert.Null(e.Edits));
        var edit = Assert.Single(edits);
        Assert.Equal(T0.AddSeconds(10), edit.Key);
        Assert.Equal(new InterventionNoteEdit("Disque /var plein", T0.AddSeconds(40)), edit.Value);
    }

    /// <summary>La modification passe par le journal (chiffré, ajout seul) et se relit avec l'heure de la note modifiée.</summary>
    [Fact]
    public void JournalKeepsTheEditedNoteTime()
    {
        string path;
        var protector = new XorProtector();
        using (var journal = InterventionJournal.Start(_dir, protector, T0))
        {
            path = journal.Path;
            journal.Write(new InterventionEntry(T0.AddSeconds(10), InterventionKind.Note, "", "avant"));
            journal.Write(new InterventionEntry(T0.AddSeconds(30), InterventionKind.Note, "", "après", Edits: T0.AddSeconds(10).AddTicks(1234567)));
            journal.Stop(new InterventionEntry(T0.AddMinutes(1), InterventionKind.Stopped, "", ""));
        }

        var recording = InterventionJournal.Read(path, protector);
        Assert.Null(recording.Entries[0].Edits);
        Assert.Equal(T0.AddSeconds(10).AddTicks(1234567), recording.Entries[1].Edits);
    }

    /// <summary>Historique affiché : la note modifiée prend le nouveau texte et l'heure de modification, à sa place.</summary>
    [Fact]
    public void HistoryBlockOfTheNoteIsEdited()
    {
        var builder = new InterventionBlockBuilder();
        var note = new InterventionEntry(T0, InterventionKind.Note, "", "première version");
        builder.Add(note);
        builder.Add(new InterventionEntry(T0.AddSeconds(1), InterventionKind.Terminal, "root@srv01", "uptime"));
        Assert.Null(builder.Edit(new InterventionEntry(T0.AddSeconds(2), InterventionKind.Note, "", "pas une modification")));
        Assert.Null(builder.Edit(new InterventionEntry(T0.AddSeconds(2), InterventionKind.Note, "", "note absente", Edits: T0.AddSeconds(-5))));

        var edited = builder.Edit(new InterventionEntry(T0.AddSeconds(3), InterventionKind.Note, "", "seconde\nversion", Edits: T0));

        Assert.Same(builder.Blocks[0], edited);
        Assert.Equal(["seconde", "version"], edited!.Lines);
        Assert.Equal(T0.AddSeconds(3), edited.EditedAt);
        Assert.Equal(2, builder.Blocks.Count);
    }

    /// <summary>
    /// Rapport : la note modifiée à sa place avec son dernier texte, le texte d'origine et l'heure de modification dessous,
    /// une seule note au résumé ; sous le lecteur, un repère par note prise pendant la session (texte échappé), la ligne de
    /// temps allant jusqu'à la fermeture de la session même si l'écran n'a plus bougé.
    /// </summary>
    [Fact]
    public void ReportShowsEditedNotesAndMarksThemUnderThePlayer()
    {
        var frame = new ScreenTileEncoder(stream: 1).Encode(T0.AddSeconds(1), new byte[128 * 64 * 4], 128, 64, 128 * 4)!.Data;
        var recording = new InterventionRecording("x", [
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(1), InterventionKind.Screen, "admin-srv01@srv01", "", frame),
            new InterventionEntry(T0.AddSeconds(90), InterventionKind.Note, "", "Service <stopped> & restarted"),
            new InterventionEntry(T0.AddSeconds(95), InterventionKind.Note, "", "Service \"stopped\" then restarted", Edits: T0.AddSeconds(90)),
            new InterventionEntry(T0.AddSeconds(121), InterventionKind.Connection, "admin-srv01@srv01", "Session fermée"),
            new InterventionEntry(T0.AddSeconds(200), InterventionKind.Note, "", "After the session"),
            new InterventionEntry(T0.AddSeconds(300), InterventionKind.Stopped, "", ""),
        ], Damaged: false, Unfinished: false);
        var text = new InterventionReportText { Count = (k, n) => $"{k}={n}" };

        string html = InterventionReport.ToHtml(recording, text, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.Contains("<li>Note=2</li>", html, StringComparison.Ordinal);
        Assert.Contains("Service &quot;stopped&quot; then restarted<br><span class=\"edited\">Edited on 10/13/2026 14:01. Original text: Service &lt;stopped&gt; &amp; restarted</span>",
            html, StringComparison.Ordinal);
        Assert.DoesNotContain("<stopped>", html, StringComparison.Ordinal);
        // Ligne de temps de la session jusqu'à sa fermeture (de 1 s à 121 s, plus sa dernière seconde) ; note de 90 s à 89 s.
        Assert.Contains("data-duration=\"121000\"", html, StringComparison.Ordinal);
        var mark = Assert.Single(MarkTag().Matches(html));
        Assert.Contains("data-ms=\"89000\"", mark.Value, StringComparison.Ordinal);
        Assert.Contains("left:73.554%", mark.Value, StringComparison.Ordinal);
        Assert.Contains("title=\"Note at 14:01:30: Service &quot;stopped&quot; then restarted\"", mark.Value, StringComparison.Ordinal);
        Assert.Contains("<span class=\"kind\">Note</span> After the session", html, StringComparison.Ordinal);
    }

    [GeneratedRegex("<button type=\"button\" class=\"mark\"[^>]*>")]
    private static partial Regex MarkTag();

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
