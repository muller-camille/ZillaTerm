using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Tests.KeePass;

/// <summary>Écriture de coffres : relecture à l'identique, modifications, historique, corbeille, conflits.</summary>
public sealed class KeePassVaultTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cat-kp-").FullName;

    public static TheoryData<string, string?, string?> AllVaults => new()
    {
        { "kxc-kdbx31.kdbx", KdbxReadTests.Password, null },
        { "kxc-kdbx41.kdbx", KdbxReadTests.Password, null },
        { "kxc-kdbx31-keyonly.kdbx", null, "kxc-v2.keyx" },
        { "py-kdbx4-argon2d-aes.kdbx", KdbxReadTests.Password, null },
        { "py-kdbx4-argon2id-chacha20.kdbx", KdbxReadTests.Password, null },
        { "py-kdbx4-aeskdf-aes.kdbx", KdbxReadTests.Password, null },
        { "py-kdbx4-kxc-v2.kdbx", KdbxReadTests.Password, "kxc-v2.keyx" },
    };

    /// <summary>
    /// Tout est gardé ; la copie de sécurité (.bak) ne sert que pendant le remplacement : elle ne reste pas à côté du
    /// coffre, où elle s'ouvrirait encore avec un ancien mot de passe maître. Une copie restée d'avant disparaît aussi.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVaults))]
    public async Task SavingKeepsEverythingAndRemovesTheBackup(string vault, string? password, string? keyFile)
    {
        var path = Copy(vault);
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path + ".bak", original);
        List<(string Title, string User, string Url, string Notes, string? Password)> before;
        string format;
        using (var v = await Open(path, password, keyFile))
        {
            before = Snapshot(v.Database);
            format = v.Database.FormatName;
            await v.SaveAsync(_ => { });
        }

        Assert.False(File.Exists(path + ".bak"));
        Assert.NotEqual(original, await File.ReadAllBytesAsync(path));
        using var reopened = await Open(path, password, keyFile);
        Assert.Equal(format, reopened.Database.FormatName);
        Assert.Equal(before, Snapshot(reopened.Database));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [MemberData(nameof(AllVaults))]
    public async Task AddUpdateAndDeleteEntries(string vault, string? password, string? keyFile)
    {
        var path = Copy(vault);
        using var v = await Open(path, password, keyFile);
        var count = v.Database.Entries.Count;

        var id = await v.SaveAsync(db => db.AddEntry(new KeePassEntryData("urgence-01", "secours", "Mot de passe <&> é ✓", "ssh://10.0.0.1:2222", "créée par le test"), "Urgence/Linux"));
        var added = v.Database.Entries.Single(e => e.Id == id);
        Assert.Equal("Urgence/Linux", added.Group);
        Assert.Equal("Mot de passe <&> é ✓", v.Database.RevealPassword(id));
        Assert.Equal(count + 1, v.Database.Entries.Count);

        await v.SaveAsync(db => db.UpdateEntry(id, new KeePassEntryData("urgence-01", "secours2", "Nouveau!", "ssh://10.0.0.1:22", "modifiée"), added.Modified.AddSeconds(0)));
        var updated = v.Database.Entries.Single(e => e.Id == id);
        Assert.Equal("secours2", updated.UserName);
        Assert.Equal("Nouveau!", v.Database.RevealPassword(id));
        Assert.Equal(1, v.Database.HistoryCount(id));
        Assert.Equal("Mot de passe <&> é ✓", v.Database.HistoryPassword(id, 0));

        // Mot de passe non fourni : inchangé.
        await v.SaveAsync(db => db.UpdateEntry(id, new KeePassEntryData("urgence-01", "secours3", null, "", ""), updated.Modified));
        Assert.Equal("Nouveau!", v.Database.RevealPassword(id));
        Assert.Equal(2, v.Database.HistoryCount(id));

        var last = v.Database.Entries.Single(e => e.Id == id);
        await v.SaveAsync(db => db.DeleteEntry(id, last.Modified));
        Assert.DoesNotContain(v.Database.Entries, e => e.Id == id);
        Assert.Contains("urgence-01", v.Database.RecycledTitles);

        // Tout est relu par un nouveau chargement du fichier.
        using var reopened = await Open(path, password, keyFile);
        Assert.Equal(count, reopened.Database.Entries.Count);
        Assert.Contains("urgence-01", reopened.Database.RecycledTitles);
    }

    [Fact]
    public async Task MovesEntryToAnotherGroup()
    {
        var path = Copy("kxc-kdbx31.kdbx");
        using var v = await Open(path, KdbxReadTests.Password, null);
        var entry = v.Database.Entries.Single(e => e.Title == "srv-lnx01");

        await v.SaveAsync(db => db.MoveEntry(entry.Id, "Linux/Prod"));

        using var reopened = await Open(path, KdbxReadTests.Password, null);
        Assert.Equal("Linux/Prod", reopened.Database.Entries.Single(e => e.Id == entry.Id).Group);
        Assert.Equal("Root-Pass 1", reopened.Database.RevealPassword(entry.Id));
    }

    [Fact]
    public async Task StaleEditIsRefused()
    {
        var path = Copy("py-kdbx4-argon2d-aes.kdbx");
        using var v = await Open(path, KdbxReadTests.Password, null);
        var entry = v.Database.Entries.First();
        await v.SaveAsync(db => db.UpdateEntry(entry.Id, new KeePassEntryData("A", "a", "a", "", ""), entry.Modified));

        var ex = await Assert.ThrowsAsync<KeePassException>(() =>
            v.SaveAsync(db => db.UpdateEntry(entry.Id, new KeePassEntryData("B", "b", "b", "", ""), entry.Modified.AddDays(-1))));

        Assert.Equal(KeePassError.Conflict, ex.Kind);
        Assert.Equal("A", v.Database.Entries.Single(e => e.Id == entry.Id).Title);
    }

    [Fact]
    public async Task ChangesMadeElsewhereAreKept()
    {
        var path = Copy("kxc-kdbx31.kdbx");
        using var first = await Open(path, KdbxReadTests.Password, null);
        using (var second = await Open(path, KdbxReadTests.Password, null))
        {
            await second.SaveAsync(db => db.AddEntry(new KeePassEntryData("ajout-ailleurs", "x", "x", "", "")));
        }

        // « first » n'a pas relu le fichier : son enregistrement part pourtant de la version actuelle.
        await first.SaveAsync(db => db.AddEntry(new KeePassEntryData("ajout-ici", "y", "y", "", "")));

        var titles = first.Database.Entries.Select(e => e.Title).ToList();
        Assert.Contains("ajout-ailleurs", titles);
        Assert.Contains("ajout-ici", titles);
    }

    [Fact]
    public async Task ReloadPicksUpChangesMadeElsewhere()
    {
        var path = Copy("py-kdbx4-argon2d-aes.kdbx");
        using var first = await Open(path, KdbxReadTests.Password, null);
        using (var second = await Open(path, KdbxReadTests.Password, null))
        {
            await second.SaveAsync(db => db.AddEntry(new KeePassEntryData("autre", "x", "x", "", "")));
        }

        Assert.DoesNotContain(first.Database.Entries, e => e.Title == "autre");
        await first.ReloadAsync();
        Assert.Contains(first.Database.Entries, e => e.Title == "autre");
    }

    /// <summary>
    /// Chaque enregistrement prend une nouvelle graine de dérivation (sel Argon2, graine AES-KDF ou TransformSeed) : la
    /// clé dérivée d'une version ne déchiffre pas la suivante. Le coffre s'ouvre toujours avec la même clé.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllVaults))]
    public async Task SavingRenewsTheKeyDerivationSeed(string vault, string? password, string? keyFile)
    {
        var path = Copy(vault);
        using var v = await Open(path, password, keyFile);
        var seed = Seed(v.Database);
        var transformed = v.Database.TransformedKey.Reveal();

        await v.SaveAsync(_ => { });

        Assert.NotEqual(seed, Seed(v.Database));
        Assert.NotEqual(transformed, v.Database.TransformedKey.Reveal());
        using var reopened = await Open(path, password, keyFile);
        Assert.Equal(Seed(v.Database), Seed(reopened.Database));

        static byte[] Seed(KeePassDatabase db) =>
            db.Version >= 0x00040000 ? VariantDictionary.Parse(db.KdfParameters).GetBytes("S")! : db.TransformSeed;
    }

    /// <summary>Champ inconnu de l'en-tête interne (version future du format) : réécrit tel quel.</summary>
    [Fact]
    public async Task UnknownInnerHeaderFieldsAreKept()
    {
        var path = Copy("kxc-kdbx41.kdbx");
        using (var v = await Open(path, KdbxReadTests.Password, null))
        {
            await v.SaveAsync(db => db.OtherInnerFields.Add((0x77, new SecretBytes([1, 2, 3]))));
        }

        using var reopened = await Open(path, KdbxReadTests.Password, null);
        var (id, data) = Assert.Single(reopened.Database.OtherInnerFields);
        Assert.Equal(0x77, id);
        Assert.Equal([1, 2, 3], data.Reveal());
    }

    [Fact]
    public async Task FailedChangeLeavesFileUntouched()
    {
        var path = Copy("py-kdbx4-argon2d-aes.kdbx");
        var original = await File.ReadAllBytesAsync(path);
        using var v = await Open(path, KdbxReadTests.Password, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => v.SaveAsync(_ => throw new InvalidOperationException()));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public async Task WrongKeyIsReportedAndKeyIsReleased()
    {
        var path = Copy("py-kdbx4-argon2d-aes.kdbx");
        var key = KeePassKey.Create("faux", (byte[]?)null);

        var ex = await Assert.ThrowsAsync<KeePassException>(() => KeePassVault.OpenAsync(path, key));

        Assert.Equal(KeePassError.InvalidKey, ex.Kind);
    }

    [Fact]
    public async Task GroupNamesMayContainSlashes()
    {
        var path = Copy("kxc-kdbx41.kdbx");
        using var v = await Open(path, KdbxReadTests.Password, null);
        var group = KeePassGroupPath.Combine("Urgence", "Linux/Unix");

        var id = await v.SaveAsync(db => db.AddEntry(new KeePassEntryData("srv-lnx01", "root", "x", "ssh://srv-lnx01", ""), group));
        int groups = v.Database.Groups.Count;
        await v.SaveAsync(db => db.AddEntry(new KeePassEntryData("srv-lnx02", "root", "x", "ssh://srv-lnx02", ""), group));

        Assert.Equal(group, v.Database.Entries.Single(e => e.Id == id).Group);
        Assert.Contains(group, v.Database.Groups);
        Assert.DoesNotContain("Urgence/Linux", v.Database.Groups);
        // Le deuxième ajout retrouve le dossier « Linux/Unix » au lieu d'en créer un autre.
        Assert.Equal(groups, v.Database.Groups.Count);
        Assert.Equal(2, v.Database.Entries.Count(e => e.Group == group));
    }

    /// <summary>Verrouillage (Win+L) pendant un enregistrement : il se termine, puis les secrets sont effacés.</summary>
    [Fact]
    public async Task LockingDuringASaveLetsItFinish()
    {
        var path = Copy("kxc-kdbx41.kdbx");
        var v = await Open(path, KdbxReadTests.Password, null);
        var id = v.Database.Entries.First(e => e.HasPassword).Id;
        Assert.NotNull(v.RevealPassword(id));
        using var changing = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();

        var save = v.SaveAsync(db =>
        {
            changing.Set();
            resume.Wait(TimeSpan.FromSeconds(30));
            return db.AddEntry(new KeePassEntryData("pendant le verrouillage", "", "p", "", ""));
        });
        Assert.True(changing.Wait(TimeSpan.FromSeconds(30)));
        v.Dispose();
        Assert.Null(v.RevealPassword(id));
        resume.Set();
        await save;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => v.SaveAsync(_ => { }));
        using var reopened = await Open(path, KdbxReadTests.Password, null);
        Assert.Contains(reopened.Database.Entries, e => e.Title == "pendant le verrouillage");
    }

    /// <summary>ReplaceFile peut renommer l'original en .bak puis échouer : le nouveau fichier doit quand même être mis en place.</summary>
    [Fact]
    public void InstallRecoversWhenReplaceMovedTheOriginalFirst()
    {
        var (path, temp, backup) = InstallFiles();

        KeePassVault.Install(temp, path, backup, (_, p, b) =>
        {
            File.Move(p, b, overwrite: true);
            throw new IOException("ERROR_UNABLE_TO_MOVE_REPLACEMENT_2");
        });

        Assert.Equal("nouveau", File.ReadAllText(path));
        Assert.Equal("ancien", File.ReadAllText(backup));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void InstallCopiesWhenReplaceIsNotSupported()
    {
        var (path, temp, backup) = InstallFiles();

        KeePassVault.Install(temp, path, backup, (_, _, _) => throw new PlatformNotSupportedException());

        Assert.Equal("nouveau", File.ReadAllText(path));
        Assert.Equal("ancien", File.ReadAllText(backup));
        Assert.False(File.Exists(temp));
    }

    /// <summary>
    /// Fichier en place différent de celui écrit (modifié entre-temps), ou copie de sécurité différente de la version
    /// modifiée (un autre poste a enregistré juste avant le remplacement : sa version n'est plus que dans la copie) : la
    /// copie de sécurité est gardée.
    /// </summary>
    [Fact]
    public async Task BackupIsKeptUnlessTheInstalledFileIsTheOneWritten()
    {
        var (path, _, backup) = InstallFiles();
        File.WriteAllText(backup, "précédent");

        await KeePassVault.RemoveBackupAsync(path, backup, "autre"u8.ToArray(), "précédent"u8.ToArray());
        Assert.Equal("précédent", File.ReadAllText(backup));

        await KeePassVault.RemoveBackupAsync(path, backup, "ancien"u8.ToArray(), "version du poste 1"u8.ToArray());
        Assert.Equal("précédent", File.ReadAllText(backup));

        await KeePassVault.RemoveBackupAsync(path, backup, "ancien"u8.ToArray(), "précédent"u8.ToArray());
        Assert.False(File.Exists(backup));
        Assert.Equal("ancien", File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private (string Path, string Temp, string Backup) InstallFiles()
    {
        var path = Path.Combine(_directory, "urgence.kdbx");
        File.WriteAllText(path, "ancien");
        File.WriteAllText(path + ".tmp", "nouveau");
        return (path, path + ".tmp", path + ".bak");
    }

    private static List<(string, string, string, string, string?)> Snapshot(KeePassDatabase db) =>
        [.. db.Entries.OrderBy(e => e.Id).Select(e => (e.Title, e.UserName, e.Url, e.Notes, db.RevealPassword(e.Id)))];

    private static Task<KeePassVault> Open(string path, string? password, string? keyFile) =>
        KeePassVault.OpenAsync(path, KeePassKey.Create(password ?? "", keyFile is null ? null : KdbxReadTests.VaultPath(keyFile)));

    private string Copy(string vault)
    {
        var path = Path.Combine(_directory, vault);
        File.Copy(KdbxReadTests.VaultPath(vault), path);
        return path;
    }
}
