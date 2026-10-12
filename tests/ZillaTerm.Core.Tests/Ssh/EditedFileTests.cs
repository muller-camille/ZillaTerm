using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public sealed class EditedFileTests : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    /// <summary>Garde-fou contre un blocage, pas une mesure de durée : large, pour une machine de CI chargée.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-edit-" + Guid.NewGuid().ToString("N"));

    public EditedFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Save_WithNewContent_RaisesSavedOnce()
    {
        var path = Write("app.conf", "port=80\n");
        using var file = new EditedFile("/etc/app/app.conf", path, new DateTime(2026, 1, 1), 8, Delay);
        var saves = Count(file);

        Assert.False(file.HasUnsentChanges);
        File.WriteAllText(path, "port=8080\n");

        await saves.WaitForAsync(1, Wait);
        Assert.True(file.HasUnsentChanges);
        Assert.Equal("app.conf", file.Name);

        // Même contenu réenregistré : pas de nouvelle demande.
        File.WriteAllText(path, "port=8080\n");
        await Task.Delay(Delay * 6);
        Assert.Equal(1, saves.Count);
    }

    [Fact]
    public async Task Save_BackToServerContent_IsIgnored_AndMarkSentResetsTheBaseline()
    {
        var path = Write("a.txt", "v1");
        using var file = new EditedFile("/tmp/a.txt", path, default, 2, Delay);
        var saves = Count(file);

        File.WriteAllText(path, "v2");
        await saves.WaitForAsync(1, Wait);
        File.WriteAllText(path, "v1");
        await Task.Delay(Delay * 6);
        Assert.Equal(1, saves.Count);
        Assert.False(file.HasUnsentChanges);

        File.WriteAllText(path, "v3");
        await saves.WaitForAsync(2, Wait);
        file.MarkSent("v3"u8.ToArray(), new DateTime(2026, 2, 2), 2);
        Assert.False(file.HasUnsentChanges);
        Assert.Equal(new DateTime(2026, 2, 2), file.RemoteWriteTime);

        File.WriteAllText(path, "v3");
        await Task.Delay(Delay * 6);
        Assert.Equal(2, saves.Count);
    }

    [Fact]
    public async Task Save_ByReplacingTheFile_IsDetected()
    {
        // Enregistrement « atomique » : écriture dans un fichier temporaire puis remplacement.
        var path = Write("b.sh", "echo 1");
        using var file = new EditedFile("/opt/b.sh", path, default, 6, Delay);
        var saves = Count(file);

        var temp = Write("b.sh.tmp", "echo 2");
        File.Move(temp, path, overwrite: true);

        await saves.WaitForAsync(1, Wait);
    }

    [Fact]
    public async Task Dispose_StopsWatching()
    {
        var path = Write("c.txt", "1");
        var file = new EditedFile("/c.txt", path, default, 1, Delay);
        var saves = Count(file);
        file.Dispose();

        File.WriteAllText(path, "2");
        await Task.Delay(Delay * 6);
        Assert.Equal(0, saves.Count);
    }

    /// <summary>
    /// Fermeture d'un onglet : la copie déjà relue après son enregistrement n'est pas relue (gros fichier, fil de
    /// l'interface) ; elle l'est de nouveau dès que sa taille ou sa date change.
    /// </summary>
    [Fact]
    public async Task UnsentChanges_AreKnownWithoutReadingTheCopyAgain()
    {
        var path = Write("big.log", "v1");
        using var file = new EditedFile("/var/log/big.log", path, default, 2, Delay);
        var saves = Count(file);
        File.WriteAllText(path, "v2, plus long");
        await saves.WaitForAsync(1, Wait);

        // Copie verrouillée : relue, elle passerait pour inchangée (lecture impossible).
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(file.HasUnsentChanges);
            Assert.True(await file.HasUnsentChangesAsync());
        }

        // Retour au contenu du serveur, avant que la surveillance ne le relise : taille changée, la copie est relue.
        File.WriteAllText(path, "v1");
        Assert.False(await file.HasUnsentChangesAsync());
        File.WriteAllText(path, "v3");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Assert.True(file.HasUnsentChanges);
    }

    /// <summary>
    /// Fichier rouvert après un changement sur le serveur : la nouvelle copie remplace l'ancienne sans être prise pour un
    /// enregistrement, et devient la référence des enregistrements suivants.
    /// </summary>
    [Fact]
    public async Task Refresh_ReplacesTheCopyWithoutRaisingSaved()
    {
        var path = Write("app.conf", "port=80\n");
        using var file = new EditedFile("/etc/app/app.conf", path, new DateTime(2026, 1, 1), 8, Delay);
        var saves = Count(file);
        file.MarkWriteInterrupted();

        var fresh = Write("app.conf.new", "port=8443\n");
        file.Refresh(fresh, new DateTime(2026, 3, 3), 10);

        Assert.Equal("port=8443\n", File.ReadAllText(path));
        Assert.False(File.Exists(fresh));
        Assert.Equal(new DateTime(2026, 3, 3), file.RemoteWriteTime);
        Assert.Equal(10, file.RemoteLength);
        Assert.False(file.WriteInterrupted);
        Assert.False(await file.HasUnsentChangesAsync());
        await Task.Delay(Delay * 6);
        Assert.Equal(0, saves.Count);

        File.WriteAllText(path, "port=80\n");
        await saves.WaitForAsync(1, Wait);
        Assert.True(file.HasUnsentChanges);
    }

    /// <summary>Copie verrouillée par l'éditeur : elle reste, avec la date, la taille et la référence d'avant.</summary>
    [Fact]
    public void Refresh_OnALockedCopy_KeepsEverything()
    {
        var path = Write("b.txt", "v1");
        using var file = new EditedFile("/b.txt", path, new DateTime(2026, 1, 1), 2, Delay);
        var fresh = Write("b.txt.new", "v2");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                // Hors de Windows, un fichier ouvert se remplace : rien à vérifier ici.
                return;
            }

            var error = Record.Exception(() => file.Refresh(fresh, new DateTime(2026, 3, 3), 2));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        Assert.Equal("v1", File.ReadAllText(path));
        Assert.Equal(new DateTime(2026, 1, 1), file.RemoteWriteTime);
        Assert.False(file.HasUnsentChanges);
    }

    [Fact]
    public void InterruptedWrite_IsRememberedUntilTheNextSuccessfulSend()
    {
        var local = Path.Combine(_dir, "sshd_config");
        File.WriteAllText(local, "Port 22\n");
        using var file = new EditedFile("/etc/ssh/sshd_config", local, DateTime.UnixEpoch, 8);
        Assert.False(file.WriteInterrupted);

        file.MarkWriteInterrupted();
        Assert.True(file.WriteInterrupted);

        file.MarkSent(File.ReadAllBytes(local), DateTime.UnixEpoch.AddMinutes(1), 8);
        Assert.False(file.WriteInterrupted);
    }

    [Theory]
    [InlineData("nginx.conf", "nginx.conf")]
    [InlineData("rapport final.txt", "rapport final.txt")]
    [InlineData("a&b|c>d.sh", "a_b_c_d.sh")]
    [InlineData("x:y*z?.log", "x_y_z_.log")]
    [InlineData("...", "fichier")]
    [InlineData("été.txt", "été.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("console.log", "console.log")]
    public void LocalCopyName_KeepsOnlySafeCharacters(string remote, string expected) =>
        Assert.Equal(expected, EditedFile.LocalCopyName(remote));

    /// <summary>Double-clic dans l'onglet Fichiers : les fichiers texte s'ouvrent dans l'éditeur, les binaires connus sont téléchargés.</summary>
    [Theory]
    [InlineData("app.conf", false)]
    [InlineData("messages", false)]
    [InlineData(".bashrc", false)]
    [InlineData("deploy.sh", false)]
    [InlineData("server.log.1", false)]
    [InlineData("cert.pem", false)]
    [InlineData("backup-2026-09-30.tar.gz", true)]
    [InlineData("RELEASE.ZIP", true)]
    [InlineData("app.jar", true)]
    [InlineData("logo.png", true)]
    [InlineData("rapport.pdf", true)]
    [InlineData("keystore.p12", true)]
    public void LooksBinary_UsesTheExtension(string name, bool binary) =>
        Assert.Equal(binary, EditedFile.LooksBinary(name));

    /// <summary>Fichier sans extension parlante : un octet nul au début (ELF, core, données) le dit binaire.</summary>
    [Fact]
    public void LooksBinary_ReadsTheFirstBytes()
    {
        Assert.False(EditedFile.LooksBinary(System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\necho « été »\n")));
        Assert.False(EditedFile.LooksBinary([]));
        Assert.True(EditedFile.LooksBinary([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]));
        // Au-delà de la zone lue, un octet nul ne compte pas.
        var late = new byte[EditedFile.SniffLength + 10];
        Array.Fill(late, (byte)'a');
        late[^1] = 0;
        Assert.False(EditedFile.LooksBinary(late));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static SaveCounter Count(EditedFile file)
    {
        var counter = new SaveCounter();
        file.Saved += _ => counter.Increment();
        return counter;
    }

    private sealed class SaveCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public async Task WaitForAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Count < expected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(expected, Count);
        }
    }
}
