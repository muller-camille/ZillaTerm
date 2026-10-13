using System.Globalization;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed class InterventionJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-intervention-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 10, 21, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void EntriesAreReadBackInOrderAndTheFileIsEncrypted()
    {
        var protector = new XorProtector();
        string path;
        using (var journal = InterventionJournal.Start(_dir, protector, T0))
        {
            path = journal.Path;
            journal.Write(new InterventionEntry(T0, InterventionKind.Started, "", "jdoe sur POSTE01"));
            journal.Write(new InterventionEntry(T0.AddSeconds(1), InterventionKind.Terminal, "root@srv01.corp.local", "[root@srv01 ~]# systemctl restart httpd"));
            journal.Flush();
            journal.Write(new InterventionEntry(T0.AddSeconds(2), InterventionKind.Screenshot, "srv02", "Capture", [0x89, 0x50, 0x4E, 0x47]));
            journal.Stop(new InterventionEntry(T0.AddMinutes(5), InterventionKind.Stopped, "", ""));
        }

        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("systemctl", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var recording = InterventionJournal.Read(path, protector);
        Assert.False(recording.Damaged);
        Assert.False(recording.Unfinished);
        Assert.Equal([InterventionKind.Started, InterventionKind.Terminal, InterventionKind.Screenshot, InterventionKind.Stopped], recording.Entries.Select(e => e.Kind));
        Assert.Equal("[root@srv01 ~]# systemctl restart httpd", recording.Entries[1].Text);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], recording.Entries[2].Image);
        Assert.Equal(T0, recording.Start);
        Assert.Equal(T0.AddMinutes(5), recording.End);
    }

    /// <summary>ZillaTerm arrêté pendant l'enregistrement : ce qui a été écrit se relit, la fin manque.</summary>
    [Fact]
    public void JournalWithoutItsEndIsUnfinished()
    {
        var protector = new XorProtector();
        var journal = InterventionJournal.Start(_dir, protector, T0);
        journal.Write(new InterventionEntry(T0, InterventionKind.Started, "", ""));
        journal.Write(new InterventionEntry(T0.AddSeconds(1), InterventionKind.Note, "", "Incident INC0012345"));
        journal.Flush();

        var recording = InterventionJournal.Read(journal.Path, protector);
        Assert.True(recording.Unfinished);
        Assert.False(recording.Damaged);
        Assert.Equal("Incident INC0012345", recording.Entries[1].Text);
        journal.Dispose();
    }

    /// <summary>Un bloc retiré, abîmé, ou tronqué à la fin : ce qui reste se relit, le journal est marqué abîmé.</summary>
    [Fact]
    public void RemovedOrDamagedBlocksAreReported()
    {
        var protector = new ChecksumProtector();
        var journal = InterventionJournal.Start(_dir, protector, T0);
        for (int i = 0; i < 3; i++)
        {
            journal.Write(new InterventionEntry(T0.AddSeconds(i), InterventionKind.Note, "", $"note {i}"));
            journal.Flush();
        }

        journal.Stop(new InterventionEntry(T0.AddSeconds(9), InterventionKind.Stopped, "", ""));
        var original = File.ReadAllBytes(journal.Path);
        var blocks = Blocks(original);
        Assert.Equal(4, blocks.Count);

        // Deuxième bloc retiré.
        File.WriteAllBytes(journal.Path, [.. original[..8], .. blocks[0], .. blocks[2], .. blocks[3]]);
        var removed = InterventionJournal.Read(journal.Path, protector);
        Assert.True(removed.Damaged);
        Assert.Equal(["note 0", "note 2", ""], removed.Entries.Select(e => e.Text));

        // Contenu d'un bloc modifié : refusé (DPAPI vérifie l'intégrité, comme ce faux protecteur).
        var changed = original.ToArray();
        changed[8 + 4 + 2] ^= 0xFF;
        File.WriteAllBytes(journal.Path, changed);
        Assert.True(InterventionJournal.Read(journal.Path, protector).Damaged);

        // Fin coupée au milieu d'un bloc.
        File.WriteAllBytes(journal.Path, original[..^5]);
        var truncated = InterventionJournal.Read(journal.Path, protector);
        Assert.True(truncated.Damaged);
        Assert.True(truncated.Unfinished);
        Assert.Equal(3, truncated.Entries.Count);

        File.WriteAllText(journal.Path, "pas un journal");
        Assert.Throws<InvalidDataException>(() => InterventionJournal.Read(journal.Path, protector));
    }

    /// <summary>
    /// Une commande qui affiche des dizaines de milliers de lignes entre deux écritures : plusieurs blocs bornés, tout se
    /// relit.
    /// </summary>
    [Fact]
    public void LargeOutputIsWrittenInBoundedBlocks()
    {
        var protector = new XorProtector();
        var line = new string('x', 100);
        // De quoi remplir trois blocs (une écriture du minuteur au milieu n'en ferait que deux sans la limite).
        int lines = (int)(3 * InterventionJournal.MaxWrittenChunk / InterventionJournal.EstimatedSize(new(T0, InterventionKind.Terminal, "s", line))) + 1000;
        var journal = InterventionJournal.Start(_dir, protector, T0);
        journal.Write(new InterventionEntry(T0, InterventionKind.Note, "", "before"));
        for (int i = 0; i < lines; i++)
        {
            journal.Write(new InterventionEntry(T0, InterventionKind.Terminal, "s", line));
        }

        journal.Write(new InterventionEntry(T0, InterventionKind.Note, "", "after"));
        journal.Stop(new InterventionEntry(T0, InterventionKind.Stopped, "", ""));

        Assert.True(Blocks(File.ReadAllBytes(journal.Path)).Count >= 4);
        var recording = InterventionJournal.Read(journal.Path, protector);
        Assert.False(recording.Damaged);
        Assert.False(recording.Unfinished);
        Assert.Equal(lines + 3, recording.Entries.Count);
        Assert.Equal("after", recording.Entries[^2].Text);
    }

    /// <summary>
    /// Bloc trop gros pour être relu (écrit par une version précédente) : sauté, le journal est abîmé, mais la suite (note,
    /// fin de l'enregistrement) se relit.
    /// </summary>
    [Fact]
    public void OversizedBlockIsSkipped()
    {
        var protector = new XorProtector();
        var journal = InterventionJournal.Start(_dir, protector, T0);
        journal.Write(new InterventionEntry(T0, InterventionKind.Note, "", "before"));
        journal.Flush();
        journal.Write(new InterventionEntry(T0.AddSeconds(1), InterventionKind.Note, "", "after"));
        journal.Stop(new InterventionEntry(T0.AddSeconds(2), InterventionKind.Stopped, "", ""));
        var blocks = Blocks(File.ReadAllBytes(journal.Path));

        const int huge = (64 * 1024 * 1024) + 1;
        using (var file = new FileStream(journal.Path, FileMode.Create))
        {
            file.Write("ZTINTV1\n"u8);
            file.Write(blocks[0]);
            file.Write(BitConverter.GetBytes(huge));
            file.SetLength(file.Position + huge);
            file.Seek(0, SeekOrigin.End);
            file.Write(blocks[1]);
        }

        var recording = InterventionJournal.Read(journal.Path, protector);
        Assert.True(recording.Damaged);
        Assert.False(recording.Unfinished);
        Assert.Equal(["before", "after", ""], recording.Entries.Select(e => e.Text));
    }

    /// <summary>
    /// Disque qui refuse une écriture : signalé une fois, avec l'heure depuis laquelle plus rien n'est gardé ; les
    /// événements suivants ne sont plus ni comptés ni gardés en mémoire. Ce qui était écrit se relit.
    /// </summary>
    [Fact]
    public void WriteFailureIsReportedOnceAndStopsTheJournal()
    {
        var protector = new FailingProtector(failFrom: 2);
        var journal = InterventionJournal.Start(_dir, protector, T0);
        int failed = 0;
        journal.Failed += () => failed++;
        journal.Write(new InterventionEntry(T0, InterventionKind.Started, "", ""));
        journal.Flush();
        Assert.Null(journal.Failure);

        journal.Write(new InterventionEntry(T0.AddMinutes(2), InterventionKind.Note, "", "perdue"));
        journal.Write(new InterventionEntry(T0.AddMinutes(1), InterventionKind.Note, "", "perdue aussi"));
        journal.Flush();
        Assert.IsType<System.Security.Cryptography.CryptographicException>(journal.Failure);
        Assert.Equal(T0.AddMinutes(1), journal.FailedAt);
        Assert.Equal(1, failed);
        Assert.Equal(3, journal.Count);

        journal.Write(new InterventionEntry(T0.AddMinutes(3), InterventionKind.Note, "", "ignorée"));
        journal.Flush();
        Assert.Equal(3, journal.Count);
        Assert.Equal(1, failed);
        journal.Dispose();

        var recording = InterventionJournal.Read(journal.Path, protector);
        Assert.Equal([InterventionKind.Started], recording.Entries.Select(e => e.Kind));
        Assert.True(recording.Unfinished);
    }

    [Fact]
    public void TerminalTextIsCapped()
    {
        var protector = new XorProtector();
        using var journal = InterventionJournal.Start(_dir, protector, T0, terminalTextLimit: 10);
        journal.Write(new InterventionEntry(T0, InterventionKind.Terminal, "s", "123456"));
        Assert.False(journal.TerminalTextCapped);
        journal.Write(new InterventionEntry(T0, InterventionKind.Terminal, "s", "7890123"));
        journal.Write(new InterventionEntry(T0, InterventionKind.Terminal, "s", "encore"));
        journal.Write(new InterventionEntry(T0, InterventionKind.Note, "", "les notes restent"));
        Assert.True(journal.TerminalTextCapped);
        journal.Stop(new InterventionEntry(T0, InterventionKind.Stopped, "", ""));

        var texts = InterventionJournal.Read(journal.Path, protector).Entries.Select(e => e.Text).ToList();
        Assert.Equal(["123456", "…", "les notes restent", ""], texts);
    }

    [Fact]
    public void JournalsAreListedNewestFirst()
    {
        var protector = new XorProtector();
        InterventionJournal.Start(_dir, protector, T0).Dispose();
        InterventionJournal.Start(_dir, protector, T0).Dispose();
        InterventionJournal.Start(_dir, protector, T0.AddDays(1)).Dispose();
        File.WriteAllText(Path.Combine(_dir, "autre.txt"), "");

        var list = InterventionJournal.List(_dir);
        Assert.Equal(3, list.Count);
        Assert.Equal(T0.AddDays(1), list[0].Start);
        Assert.All(list.Skip(1), f => Assert.Equal(T0, f.Start));
        Assert.Empty(InterventionJournal.List(Path.Combine(_dir, "absent")));
    }

    /// <summary>
    /// Texte des terminaux : lignes complètes, morceaux d'une ligne coupée réunis, rien de l'écran alternatif (vim), ni la
    /// commande que ZillaTerm tape puis efface pour suivre le dossier.
    /// </summary>
    [Fact]
    public void TranscriptKeepsWholeLinesOfTheMainScreenOnly()
    {
        var emulator = new TerminalEmulator(10, 5);
        var lines = new List<string>();
        using var transcript = new InterventionTerminalTranscript(emulator, lines.Add);

        emulator.Feed("$ ls\r\na  b\r\n");
        emulator.Feed("0123456789abcdef\r\n");
        emulator.Feed("\x1b[?1049hvim screen\r\nmore\r\n\x1b[?1049l");
        emulator.Feed("$ ");
        Assert.Equal(["$ ls", "a  b", "0123456789abcdef"], lines);

        // Commande de ZillaTerm : marque, écho, puis marqueur d'effacement ; rien n'en reste.
        emulator.MarkEraseFromCursorLine();
        emulator.Feed("PROMPT_COMMAND=...\r\n" + WorkingDirectory.EraseMarker);
        emulator.Feed("$ uptime\r\n 21:30 up\r\n");
        Assert.Equal(["$ ls", "a  b", "0123456789abcdef", "$ uptime", " 21:30 up"], lines);

        // Marque retirée sans effacement (shell non Unix) : les lignes retenues étaient réelles.
        emulator.MarkEraseFromCursorLine();
        emulator.Feed("C:\\> dir\r\n");
        emulator.CancelEraseMark();
        emulator.Feed("x\r\n");
        Assert.Equal(["C:\\> dir", "x"], lines[^2..]);
    }

    [Fact]
    public void ReportEscapesTheServersTextAndForbidsScripts()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var recording = new InterventionRecording("x", [
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(1), InterventionKind.Terminal, "root@srv01", "<script>alert(1)</script>"),
            new InterventionEntry(T0.AddSeconds(2), InterventionKind.Terminal, "root@srv01", "evil\u202Etxt.exe & \"quoted\""),
            new InterventionEntry(T0.AddSeconds(3), InterventionKind.Terminal, "root@srv02", "other"),
            new InterventionEntry(T0.AddSeconds(4), InterventionKind.Screenshot, "srv03", "Capture", png),
            new InterventionEntry(T0.AddSeconds(5), InterventionKind.Screenshot, "srv03", "Pas une image", "<svg>"u8.ToArray()),
            new InterventionEntry(T0.AddMinutes(90), InterventionKind.Stopped, "", ""),
        ], Damaged: false, Unfinished: false);

        string html = InterventionReport.ToHtml(recording, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.Contains("default-src 'none'; img-src data:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        Assert.Contains("&amp; &quot;quoted&quot;", html, StringComparison.Ordinal);
        // Les deux lignes de srv01 forment un seul bloc ; srv02 a le sien.
        Assert.Equal(2, html.Split("<pre>").Length - 1);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(png), html, StringComparison.Ordinal);
        Assert.Single(html.Split("data:image/").Skip(1), s => s.StartsWith("png", StringComparison.Ordinal));
        Assert.Equal(1, html.Split("<img").Length - 1);
    }

    /// <summary>
    /// Titre de l'intervention : une ligne propre (sans retour à la ligne ni inverseur de sens de lecture), gardé chiffré à
    /// côté du journal pour la liste, retiré avec lui ; le dernier titre du journal fait foi pour le rapport.
    /// </summary>
    [Fact]
    public void TitleIsCleanedListedAndDeletedWithTheJournal()
    {
        var protector = new XorProtector();
        string path;
        using (var journal = InterventionJournal.Start(_dir, protector, T0))
        {
            path = journal.Path;
            journal.Write(new InterventionEntry(T0, InterventionKind.Title, "", "INC0012345"));
            journal.Write(new InterventionEntry(T0.AddMinutes(1), InterventionKind.Title, "", "INC0012345 disque plein srv01"));
            journal.Stop(new InterventionEntry(T0.AddMinutes(2), InterventionKind.Stopped, "", ""));
        }

        Assert.Equal("INC0012345 disque plein srv01", InterventionJournal.CleanTitle("  INC0012345\r\n disque\tplein \u202Esrv01  "));
        Assert.Equal(InterventionJournal.MaxTitleLength, InterventionJournal.CleanTitle(new string('a', 500)).Length);
        Assert.Equal("", InterventionJournal.CleanTitle(" \u200B "));

        InterventionJournal.WriteTitle(path, protector, "INC0012345 disque plein srv01");
        Assert.DoesNotContain("INC0012345", File.ReadAllText(InterventionJournal.TitlePath(path)), StringComparison.Ordinal);
        Assert.Equal("INC0012345 disque plein srv01", InterventionJournal.ReadTitle(path, protector));
        Assert.Equal("INC0012345 disque plein srv01", Assert.Single(InterventionJournal.List(_dir, protector)).Title);
        Assert.Null(Assert.Single(InterventionJournal.List(_dir)).Title);
        Assert.Equal("INC0012345 disque plein srv01", InterventionJournal.Read(path, protector).Title);

        // Fichier du titre illisible : la liste reste, sans titre.
        File.WriteAllBytes(InterventionJournal.TitlePath(path), new byte[40]);
        Assert.Null(InterventionJournal.ReadTitle(path, new ChecksumProtector()));

        // Titre effacé : plus de fichier, et le rapport n'en a plus.
        InterventionJournal.WriteTitle(path, protector, "  ");
        Assert.False(File.Exists(InterventionJournal.TitlePath(path)));
        var cleared = new InterventionRecording(path, [
            new InterventionEntry(T0, InterventionKind.Title, "", "INC0012345"),
            new InterventionEntry(T0.AddMinutes(1), InterventionKind.Title, "", ""),
        ], Damaged: false, Unfinished: false);
        Assert.Null(cleared.Title);

        InterventionJournal.WriteTitle(path, protector, "INC0012345");
        InterventionJournal.Delete(path);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(InterventionJournal.TitlePath(path)));
        Assert.Empty(InterventionJournal.List(_dir, protector));
    }

    [Fact]
    public void ReportShowsTheTitleAndSuggestsAFileName()
    {
        var recording = new InterventionRecording("x", [
            new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"),
            new InterventionEntry(T0.AddSeconds(1), InterventionKind.Title, "", "INC0012345 <disque> plein"),
            new InterventionEntry(T0.AddMinutes(5), InterventionKind.Stopped, "", ""),
        ], Damaged: false, Unfinished: false);
        var text = new InterventionReportText { Kind = k => k == InterventionKind.Title ? "Titre" : k.ToString() };

        string html = InterventionReport.ToHtml(recording, text, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.Contains("<h1>On-call intervention — INC0012345 &lt;disque&gt; plein</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<title>On-call intervention — INC0012345 &lt;disque&gt; plein — ", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"kind\">Titre</span>", html, StringComparison.Ordinal);

        var start = new DateTime(2026, 10, 10, 23, 30, 0, DateTimeKind.Local);
        Assert.Equal("intervention-20261010-2330.html", InterventionReport.FileName(start, null));
        Assert.Equal("intervention-20261010-2330 INC0012345 disque plein srv01.html",
            InterventionReport.FileName(start, "INC0012345 : disque/plein \\srv01?"));
        Assert.Equal("intervention-20261010-2330 " + new string('a', 60) + ".html", InterventionReport.FileName(start, new string('a', 150)));
        Assert.Equal("intervention-20261010-2330.html", InterventionReport.FileName(start, "../\u202E"));
    }

    /// <summary>
    /// Historique affiché : les événements sans les images des captures, le texte des terminaux plafonné ; chaque lecture
    /// rend ce qui est arrivé depuis la précédente.
    /// </summary>
    [Fact]
    public void HistoryKeepsEventsWithoutImagesAndCapsTerminalText()
    {
        var history = new InterventionHistory(maxTerminalText: 10);
        history.Add(new InterventionEntry(T0, InterventionKind.Started, "", "jdoe"));
        history.Add(new InterventionEntry(T0, InterventionKind.Screenshot, "srv03", "Capture", [0x89, 0x50]));
        history.Add(new InterventionEntry(T0, InterventionKind.Terminal, "s", "123456"));
        Assert.Equal(3, history.Count);
        Assert.Null(history.Since(0)[1].Image);

        history.Add(new InterventionEntry(T0, InterventionKind.Terminal, "s", "7890123"));
        Assert.True(history.TerminalCapped);
        history.Add(new InterventionEntry(T0, InterventionKind.Terminal, "s", "1"));
        history.Add(new InterventionEntry(T0, InterventionKind.Note, "", "les notes restent"));
        Assert.Equal(["les notes restent"], history.Since(3).Select(e => e.Text));
        Assert.Empty(history.Since(4));
        Assert.Empty(history.Since(99));
    }

    /// <summary>
    /// Blocs de l'historique : une commande et son retour forment un bloc ; une pause de la sortie en commence un autre,
    /// sauf quand le bloc n'a encore que la commande (retour lent) ; une autre session ou un autre événement aussi.
    /// </summary>
    [Fact]
    public void TerminalLinesAreGroupedByCommand()
    {
        var builder = new InterventionBlockBuilder();
        InterventionEntry Line(double seconds, string text, string source = "root@srv01") =>
            new(T0.AddSeconds(seconds), InterventionKind.Terminal, source, text);

        Assert.False(builder.Add(Line(0, "[root@srv01 ~]# find / -name core")));
        // Retour au bout de 30 s : il rejoint sa commande.
        Assert.True(builder.Add(Line(30, "/var/core")));
        Assert.True(builder.Add(Line(30.5, "/tmp/core")));
        // Commande suivante après une pause : nouveau bloc.
        Assert.False(builder.Add(Line(40, "[root@srv01 ~]# df -h")));
        Assert.True(builder.Add(Line(40.1, "/dev/sda1  20G")));
        // Autre session, puis un autre événement : nouveaux blocs.
        Assert.False(builder.Add(Line(40.2, "[root@srv02 ~]#", "root@srv02")));
        Assert.False(builder.Add(new InterventionEntry(T0.AddSeconds(41), InterventionKind.Transfer, "srv01", "Envoi de a.txt\na.txt  SHA-256 00")));
        Assert.False(builder.Add(Line(41.5, "evil\u202Etxt\tok")));

        var blocks = builder.Blocks;
        Assert.Equal(5, blocks.Count);
        Assert.Equal(["[root@srv01 ~]# find / -name core", "/var/core", "/tmp/core"], blocks[0].Lines);
        Assert.Equal(T0, blocks[0].Start);
        Assert.Equal(T0.AddSeconds(30.5), blocks[0].End);
        Assert.Equal(2, blocks[1].Lines.Count);
        Assert.Equal("root@srv02", blocks[2].Source);
        Assert.Equal(["Envoi de a.txt", "a.txt  SHA-256 00"], blocks[3].Lines);
        Assert.Equal(["evil\uFFFDtxt\tok"], blocks[4].Lines);

        // Sortie sans fin : le bloc s'arrête à MaxTerminalLines lignes, la suite en commence un autre.
        var endless = new InterventionBlockBuilder();
        for (int i = 0; i <= InterventionBlockBuilder.MaxTerminalLines; i++)
        {
            endless.Add(Line(i * 0.001, "ligne " + i));
        }

        Assert.Equal(2, endless.Blocks.Count);
        Assert.Equal(InterventionBlockBuilder.MaxTerminalLines, endless.Blocks[0].Lines.Count);
    }

    private static List<byte[]> Blocks(byte[] file)
    {
        var blocks = new List<byte[]>();
        for (int at = 8; at < file.Length;)
        {
            int size = BitConverter.ToInt32(file, at);
            blocks.Add(file[at..(at + 4 + size)]);
            at += 4 + size;
        }

        return blocks;
    }

    /// <summary>Comme DPAPI : un bloc modifié est refusé.</summary>
    private sealed class ChecksumProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => [.. System.Security.Cryptography.SHA256.HashData(data), .. data];

        public byte[] Unprotect(byte[] data)
        {
            var plain = data[32..];
            if (!System.Security.Cryptography.SHA256.HashData(plain).AsSpan().SequenceEqual(data.AsSpan(0, 32)))
            {
                throw new System.Security.Cryptography.CryptographicException("altered");
            }

            return plain;
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    /// <summary>Protecteur qui refuse à partir du bloc <c>failFrom</c> (comme un disque plein au moment d'écrire).</summary>
    private sealed class FailingProtector(int failFrom) : ISecretProtector
    {
        private readonly XorProtector _inner = new();
        private int _calls;

        public byte[] Protect(byte[] data) => ++_calls >= failFrom
            ? throw new System.Security.Cryptography.CryptographicException("refusé")
            : _inner.Protect(data);

        public byte[] Unprotect(byte[] data) => _inner.Unprotect(data);
    }
}
