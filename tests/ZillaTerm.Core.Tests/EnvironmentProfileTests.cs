using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests;

public sealed class EnvironmentProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-env-" + Guid.NewGuid().ToString("N"));

    public EnvironmentProfileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AppSettings Team()
    {
        var settings = new AppSettings
        {
            PvwaUrl = "pvwa.corp.com",
            UserName = "jdupont",
            AuthMethod = AuthMethod.LDAP,
            PsmpAddress = "psmp.corp.com",
            PsmpPort = 2222,
            WindowsComponent = "WIN-PSM",
            EnvironmentFile = @"\\srv\partage\zillaterm\prod.env.json",
            PsmDesktopInTab = true,
            PsmRedirectDrives = false,
            PauseRdpDisplay = false,
            KeepAccountListCopy = false,
        };
        settings.PsmpServers.Add(new PsmpServer { Address = "psmp.paris.corp.com", Port = 22 });
        settings.ComponentByPlatform["UnixSSH"] = "PSM-SSH";
        settings.SharedLists.Add(@"\\srv\partage\listes\prod.json");
        KnownHosts.Remember(settings.KnownHosts, "psmp.corp.com", 2222, "ssh-ed25519", "AAAA");
        KnownHosts.Remember(settings.KnownHosts, "ftp.perso.local", 990, "X.509", "BBBB");
        settings.Recent.Add(new RecentSession { AccountId = "1", PvwaHost = "pvwa.corp.com", Label = "root@srv" });
        settings.Sessions.Add(new SavedSession { AccountId = "1", Name = "root@srv" });
        return settings;
    }

    /// <summary>
    /// Export : réglages d'équipe seulement. Ni identifiant, ni « Mes serveurs », ni récents, ni clé d'un serveur qui n'est
    /// pas un PSMP configuré.
    /// </summary>
    [Fact]
    public void ExportKeepsTeamSettingsOnly()
    {
        var path = Path.Combine(_dir, EnvironmentProfile.FileName);
        EnvironmentProfile.FromSettings(Team(), "Production").Save(path);
        var json = File.ReadAllText(path);

        Assert.Contains("psmp.paris.corp.com", json);
        Assert.Contains("WIN-PSM", json);
        Assert.Contains("\"LDAP\"", json);
        Assert.DoesNotContain("jdupont", json);
        Assert.DoesNotContain("root@srv", json);
        Assert.DoesNotContain("ftp.perso.local", json);

        var loaded = EnvironmentProfile.Load(path);
        Assert.Equal("Production", loaded.Profile.Name);
        Assert.Equal(["psmp.corp.com:2222"], loaded.Profile.HostKeys!.Keys);
        Assert.Equal(64, loaded.Sha256.Length);
    }

    /// <summary>
    /// Nouveau poste : tout ce que porte l'environnement est montré puis appliqué ; adresses et clés marquées sensibles. Un
    /// second passage ne trouve plus rien à changer.
    /// </summary>
    [Fact]
    public void FreshInstallGetsTheWholeEnvironment()
    {
        var profile = EnvironmentProfile.FromSettings(Team(), "Production");
        var fresh = new AppSettings();

        var changes = profile.Diff(fresh);
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PvwaUrl, New: "pvwa.corp.com", Sensitive: true });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.DefaultPsmp, New: "psmp.corp.com:2222", Sensitive: true });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PsmpServers, New: "psmp.paris.corp.com:22" });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.HostKey, Detail: "psmp.corp.com:2222", Ignored: false });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PlatformComponent, Detail: "UnixSSH", New: "PSM-SSH" });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.CentralFile, Sensitive: true });
        Assert.DoesNotContain(changes, c => c.Setting == EnvironmentSetting.KeepPvwaSessionAlive);
        // Sessions PSM Windows dans un onglet : choix d'équipe, repris par le nouveau poste.
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PsmDesktopInTab, Sensitive: false });

        // Disques du poste non partagés avec les sessions PSM : choix d'équipe aussi.
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PsmRedirectDrives, Current: "true", New: "false" });

        // Affichage des sessions Bureau à distance gardé quand personne ne les regarde : choix d'équipe aussi.
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.PauseRdpDisplay, Current: "true", New: "false", Sensitive: false });

        // Pas de copie de la liste des comptes sur les postes : choix d'équipe aussi.
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.KeepAccountListCopy, Current: "true", New: "false", Sensitive: false });

        profile.ApplyTo(fresh);
        Assert.True(fresh.PsmDesktopInTab);
        Assert.False(fresh.PsmRedirectDrives);
        Assert.False(fresh.PauseRdpDisplay);
        Assert.False(fresh.KeepAccountListCopy);
        Assert.Equal(("pvwa.corp.com", AuthMethod.LDAP, "psmp.corp.com", 2222, "WIN-PSM"),
            (fresh.PvwaUrl, fresh.AuthMethod, fresh.PsmpAddress, fresh.PsmpPort, fresh.WindowsComponent));
        Assert.Equal("", fresh.UserName);
        Assert.Empty(fresh.Sessions);
        Assert.Equal("PSM-SSH", fresh.ComponentByPlatform["UnixSSH"]);
        Assert.Single(fresh.SharedLists);
        Assert.Empty(profile.Diff(fresh));
    }

    /// <summary>
    /// Une clé de serveur déjà acceptée n'est jamais remplacée par un fichier (le changement se vérifie à la connexion) ;
    /// seules les clés des PSMP (des réglages ou du fichier) sont reprises, jamais celle d'un serveur d'accès d'urgence ;
    /// les listes partagées s'ajoutent sans retirer celles de l'utilisateur, et un partage réseau est signalé avec son
    /// serveur.
    /// </summary>
    [Fact]
    public void KnownHostKeysAreNeverReplaced()
    {
        var user = Team();
        user.SharedLists.Add(@"\\srv\perso\mes-serveurs.json");
        var profile = new EnvironmentProfile
        {
            PsmpServers = [new PsmpServer { Address = "psmp.lyon.corp.com", Port = 22 }],
            HostKeys = new()
            {
                ["PSMP.corp.com:2222"] = "ssh-ed25519 SHA256:ZZZZ",
                ["psmp.lyon.corp.com:22"] = "ssh-rsa SHA256:CCCC",
                ["srv01.corp.local:22"] = "ssh-ed25519 SHA256:DDDD",
            },
            SharedLists = [@"\\other.example\partage\recette.json", @"C:\Listes\locale.json"],
        };

        var changes = profile.Diff(user);
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.HostKey, Detail: "psmp.corp.com:2222", Ignored: true, NotPsmp: false });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.HostKey, Detail: "psmp.lyon.corp.com:22", Ignored: false });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.HostKey, Detail: "srv01.corp.local:22", Ignored: true, NotPsmp: true });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.SharedList, Detail: "other.example", Sensitive: true });
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.SharedList, Detail: null, Sensitive: false });

        profile.ApplyTo(user);
        Assert.Equal("ssh-ed25519 SHA256:AAAA", user.KnownHosts["psmp.corp.com:2222"]);
        Assert.Equal("ssh-rsa SHA256:CCCC", user.KnownHosts["psmp://psmp.lyon.corp.com:22"]);
        Assert.False(user.KnownHosts.ContainsKey("psmp.lyon.corp.com:22"));
        Assert.False(user.KnownHosts.ContainsKey("srv01.corp.local:22"));
        Assert.Equal(4, user.SharedLists.Count);
    }

    /// <summary>
    /// Serveur d'accès d'urgence déclaré PSMP dans le fichier, avec « sa » clé : elle ne vaut que pour les connexions au
    /// PSMP ; jointe directement (KeePass), la clé du serveur se vérifie toujours. Une clé acceptée à la connexion passe
    /// avant celle du fichier, et l'export reprend celle du fichier.
    /// </summary>
    [Fact]
    public void KeysFromAFileAreForPsmpConnectionsOnly()
    {
        var user = new AppSettings { PsmpAddress = "psmp.corp.com" };
        var profile = new EnvironmentProfile
        {
            PsmpServers = [new PsmpServer { Address = "srv-urgence.corp.local", Port = 22, Domain = "lab.corp.local" }],
            HostKeys = new() { ["srv-urgence.corp.local:22"] = "ssh-ed25519 SHA256:EVIL" },
        };
        Assert.Contains(profile.Diff(user), c => c is { Setting: EnvironmentSetting.HostKey, Detail: "srv-urgence.corp.local:22", Ignored: false });
        profile.ApplyTo(user);
        Assert.Empty(profile.Diff(user));

        var store = user.KnownHosts;
        var direct = KnownHosts.HostFor(store, "srv-urgence.corp.local", 22, psmp: false);
        Assert.Equal(HostKeyStatus.Unknown, KnownHosts.Check(store, direct, 22, "ssh-ed25519", "EVIL"));
        var psmp = KnownHosts.HostFor(store, "srv-urgence.corp.local", 22, psmp: true);
        Assert.Equal(HostKeyStatus.Trusted, KnownHosts.Check(store, psmp, 22, "ssh-ed25519", "EVIL"));
        Assert.Equal(HostKeyStatus.Changed, KnownHosts.Check(store, psmp, 22, "ssh-ed25519", "AUTRE"));
        Assert.Equal("ssh-ed25519 SHA256:EVIL", EnvironmentProfile.FromSettings(user, null).HostKeys!["srv-urgence.corp.local:22"]);

        // Clé acceptée à la connexion au PSMP : elle seule compte ensuite.
        KnownHosts.Remember(store, "srv-urgence.corp.local", 22, "ssh-ed25519", "VRAIE");
        Assert.Equal("srv-urgence.corp.local", KnownHosts.HostFor(store, "srv-urgence.corp.local", 22, psmp: true));
        Assert.Equal(HostKeyStatus.Changed, KnownHosts.Check(store, "srv-urgence.corp.local", 22, "ssh-ed25519", "EVIL"));
        Assert.Equal("ssh-ed25519 SHA256:VRAIE", EnvironmentProfile.FromSettings(user, null).HostKeys!["srv-urgence.corp.local:22"]);
    }

    /// <summary>
    /// Clés de plusieurs types : un fichier n'ajoute pas de clé à un serveur qui en a déjà une (ni d'un autre type, ni du
    /// même type à côté d'une clé gardée à part), et une clé déjà acceptée, quel que soit son type, n'est pas un changement.
    /// </summary>
    [Fact]
    public void FilesNeverAddKeysToAKnownServer()
    {
        var user = new AppSettings();
        KnownHosts.Remember(user.KnownHosts, "psmp.corp.com", 22, "rsa-sha2-512", "RSA1");
        KnownHosts.Remember(user.KnownHosts, "psmp.corp.com", 22, "ssh-ed25519", "ED1");
        user.KnownHosts.Remove("psmp.corp.com:22");
        var profile = new EnvironmentProfile
        {
            HostKeys = new() { ["psmp.corp.com:22"] = "ssh-ed25519 SHA256:EVIL", ["psmp.lyon.corp.com:22"] = "ssh-rsa SHA256:CCCC" },
        };

        var changes = profile.Diff(user);
        Assert.Contains(changes, c => c is { Setting: EnvironmentSetting.HostKey, Detail: "psmp.corp.com:22", Ignored: true });
        profile.ApplyTo(user);
        Assert.Equal(HostKeyStatus.Changed, KnownHosts.Check(user.KnownHosts, "psmp.corp.com", 22, "ssh-ed25519", "EVIL"));
        Assert.False(user.KnownHosts.ContainsKey("psmp.corp.com:22"));

        var same = new EnvironmentProfile { HostKeys = new() { ["psmp.corp.com:22"] = "ssh-ed25519 SHA256:ED1" } };
        Assert.Empty(same.Diff(user));
    }

    /// <summary>Exporté d'un poste sans PSMP ni composant Windows : n'efface pas ceux du poste qui l'importe.</summary>
    [Fact]
    public void EmptySettingsAreNotExported()
    {
        var profile = EnvironmentProfile.FromSettings(new AppSettings { PvwaUrl = "pvwa.corp.com", AuthMethod = AuthMethod.LDAP }, null);
        Assert.Equal((null, null, null, null), (profile.PsmpAddress, profile.PsmpPort, profile.PsmpServers, profile.WindowsComponent));

        var changes = profile.Diff(Team());
        Assert.DoesNotContain(changes, c => c.Setting is EnvironmentSetting.DefaultPsmp or EnvironmentSetting.PsmpServers
            or EnvironmentSetting.WindowsComponent or EnvironmentSetting.CentralFile);
    }

    /// <summary>Même PVWA écrit autrement : pas de changement.</summary>
    [Fact]
    public void SamePvwaWrittenDifferentlyIsNoChange()
    {
        var profile = new EnvironmentProfile { PvwaUrl = "https://PVWA.corp.com/PasswordVault/v10/logon" };
        Assert.Empty(profile.Diff(new AppSettings { PvwaUrl = "pvwa.corp.com" }));
    }

    [Theory]
    [InlineData("""{ "Format": 2 }""", EnvironmentProblem.UnsupportedFormat)]
    [InlineData("""{ "PvwaUrl": "http://pvwa.corp.com" }""", EnvironmentProblem.InvalidPvwa)]
    [InlineData("""{ "PsmpAddress": "psmp corp" }""", EnvironmentProblem.InvalidPsmpAddress)]
    [InlineData("""{ "PsmpPort": 70000 }""", EnvironmentProblem.InvalidPort)]
    [InlineData("""{ "PsmpServers": [ { "Address": "psmp" } ] }""", EnvironmentProblem.InvalidDomain)]
    [InlineData("""{ "PsmpAddress": "psmp.corp.com", "PsmpServers": [ { "Address": "psmp2.corp.com" } ] }""", EnvironmentProblem.DuplicateDomain)]
    [InlineData("""{ "WindowsComponent": "WIN PSM" }""", EnvironmentProblem.InvalidComponent)]
    [InlineData("""{ "ComponentByPlatform": { "WinDomain": "a;b" } }""", EnvironmentProblem.InvalidComponent)]
    [InlineData("""{ "HostKeys": { "psmp.corp.com": "ssh-ed25519 SHA256:AAAA" } }""", EnvironmentProblem.InvalidHostKey)]
    [InlineData("""{ "HostKeys": { "psmp.corp.com:22": "ssh-ed25519 AAAA" } }""", EnvironmentProblem.InvalidHostKey)]
    [InlineData("""{ "SharedLists": [ "listes\\prod.json" ] }""", EnvironmentProblem.InvalidPath)]
    [InlineData("""{ "CentralFile": "env.json" }""", EnvironmentProblem.InvalidPath)]
    [InlineData("""{ "CentralFile": "C:env.json" }""", EnvironmentProblem.InvalidPath)]
    [InlineData("""{ "SharedLists": [ null ] }""", EnvironmentProblem.InvalidPath)]
    [InlineData("""{ "AuthMethod": 1 }""", EnvironmentProblem.InvalidJson)]
    [InlineData("""{ "AuthMethod": "7" }""", EnvironmentProblem.InvalidJson)]
    [InlineData("""{ "AuthMethod": "LDAP, RADIUS" }""", EnvironmentProblem.InvalidJson)]
    [InlineData("""{ "UploadProtocol": "Ftp" }""", EnvironmentProblem.InvalidJson)]
    [InlineData("""{ "PvwaUrl": """, EnvironmentProblem.InvalidJson)]
    public void InvalidFilesAreRefused(string json, EnvironmentProblem problem)
    {
        var path = Path.Combine(_dir, "bad.env.json");
        File.WriteAllText(path, json);
        Assert.Equal(problem, Assert.Throws<EnvironmentFileException>(() => EnvironmentProfile.Load(path)).Problem);
    }

    /// <summary>
    /// Fichier modifié à la main et écrit avec un BOM UTF-8 (PowerShell 5.1, « UTF-8 avec BOM ») : lu comme sans BOM ;
    /// l'empreinte reste celle du fichier tel quel.
    /// </summary>
    [Fact]
    public void FileWithAByteOrderMarkIsRead()
    {
        var path = Path.Combine(_dir, "bom.env.json");
        File.WriteAllText(path, """{ "PvwaUrl": "pvwa.corp.com", "PsmpAddress": "psmp.corp.com" }""", new System.Text.UTF8Encoding(true));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xEF, bytes[0]);

        var loaded = EnvironmentProfile.Load(path);
        Assert.Equal(("pvwa.corp.com", "psmp.corp.com"), (loaded.Profile.PvwaUrl, loaded.Profile.PsmpAddress));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), loaded.Sha256);
    }

    /// <summary>Champs inconnus (un mot de passe ajouté à la main…) : ignorés, jamais repris dans les réglages.</summary>
    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var path = Path.Combine(_dir, "extra.env.json");
        File.WriteAllText(path, """{ "PvwaUrl": "pvwa.corp.com", "Password": "secret", "UserName": "admin" }""");
        var settings = new AppSettings { UserName = "jdupont" };

        EnvironmentProfile.Load(path).Profile.ApplyTo(settings);

        Assert.Equal(("pvwa.corp.com", "jdupont"), (settings.PvwaUrl, settings.UserName));
    }

    /// <summary>
    /// Consignes d'astreinte : diffusées par le fichier d'environnement en texte brut ; absent, il ne change rien, vide, il
    /// l'efface ; trop long ou avec des caractères invisibles, le fichier est refusé.
    /// </summary>
    [Fact]
    public void DutyTextIsSharedAsPlainTextAndChecked()
    {
        var settings = new AppSettings { DutyText = "Astreinte : 01 23 45 67 89\r\nJamais de redémarrage en production sans ticket.\r\n\r\n" };
        var profile = EnvironmentProfile.FromSettings(settings, null);
        Assert.Equal("Astreinte : 01 23 45 67 89\nJamais de redémarrage en production sans ticket.", profile.DutyText);
        Assert.Null(EnvironmentProfile.FromSettings(new AppSettings(), null).DutyText);

        var target = new AppSettings();
        var change = Assert.Single(profile.Diff(target), c => c.Setting == EnvironmentSetting.DutyText);
        Assert.False(change.Sensitive);
        profile.ApplyTo(target);
        Assert.Equal("Astreinte : 01 23 45 67 89\nJamais de redémarrage en production sans ticket.", target.DutyText);
        Assert.DoesNotContain(profile.Diff(target), c => c.Setting == EnvironmentSetting.DutyText);

        new EnvironmentProfile().ApplyTo(target);
        Assert.NotEmpty(target.DutyText);
        new EnvironmentProfile { DutyText = "" }.ApplyTo(target);
        Assert.Empty(target.DutyText);

        var reversed = Assert.Throws<EnvironmentFileException>(new EnvironmentProfile { DutyText = "appeler le\u202E 01 23" }.Validate);
        Assert.Equal(EnvironmentProblem.InvalidDutyText, reversed.Problem);
        Assert.Throws<EnvironmentFileException>(new EnvironmentProfile { DutyText = new string('x', EnvironmentProfile.MaxDutyText + 1) }.Validate);
        new EnvironmentProfile { DutyText = "Tél.\t01 23 45 67 89" }.Validate();
    }

    /// <summary>
    /// Fichier des consignes d'astreinte : diffusé par le fichier d'environnement ; sur un partage, le changement est
    /// signalé avec le serveur (Windows s'y authentifie) ; vide, il est retiré ; un chemin de périphérique est refusé.
    /// </summary>
    [Fact]
    public void DutyTextFileIsSharedAndChecked()
    {
        var settings = new AppSettings { DutyTextFile = @"\\files.corp.local\astreinte\consignes.txt" };
        var profile = EnvironmentProfile.FromSettings(settings, null);
        Assert.Equal(@"\\files.corp.local\astreinte\consignes.txt", profile.DutyTextFile);
        Assert.Null(EnvironmentProfile.FromSettings(new AppSettings(), null).DutyTextFile);

        var target = new AppSettings();
        var change = Assert.Single(profile.Diff(target), c => c.Setting == EnvironmentSetting.DutyTextFile);
        Assert.True(change.Sensitive);
        Assert.Equal("files.corp.local", change.Detail);
        profile.ApplyTo(target);
        Assert.Equal(@"\\files.corp.local\astreinte\consignes.txt", target.DutyTextFile);
        Assert.DoesNotContain(profile.Diff(target), c => c.Setting == EnvironmentSetting.DutyTextFile);

        var local = Assert.Single(new EnvironmentProfile { DutyTextFile = @"C:\Equipe\consignes.txt" }.Diff(target),
            c => c.Setting == EnvironmentSetting.DutyTextFile);
        Assert.False(local.Sensitive);
        new EnvironmentProfile { DutyTextFile = "" }.ApplyTo(target);
        Assert.Empty(target.DutyTextFile);

        foreach (var path in new[] { "consignes.txt", @"\\.\pipe\consignes", @"\\srv\pipe\consignes", @"C:\Equipe\CON.txt", "C:\\a\u202Etxt.exe" })
        {
            var refused = Assert.Throws<EnvironmentFileException>(new EnvironmentProfile { DutyTextFile = path }.Validate);
            Assert.Equal(EnvironmentProblem.InvalidPath, refused.Problem);
        }

        new EnvironmentProfile { DutyTextFile = @"C:\Equipe\consignes.txt" }.Validate();
    }
    /// <summary>
    /// Réglages définis par un fichier (pastille « Équipe » des Paramètres) : ceux qu'il porte, aucun autre ; un fichier
    /// complet les couvre tous (un nouveau réglage de l'environnement doit y être ajouté).
    /// </summary>
    [Fact]
    public void DefinedSettingsAreThoseTheFileCarries()
    {
        Assert.Empty(new EnvironmentProfile().DefinedSettings());
        Assert.Equal([EnvironmentSetting.DefaultPsmp, EnvironmentSetting.KeepPvwaSessionAlive],
            new EnvironmentProfile { PsmpAddress = "psmp.corp.local", KeepPvwaSessionAlive = false }.DefinedSettings().Order());

        var full = new EnvironmentProfile
        {
            PvwaUrl = "https://pvwa.corp.local", AuthMethod = AuthMethod.LDAP, PsmpAddress = "psmp.corp.local", PsmpServers = [],
            WindowsComponent = "WIN-PSM", ComponentByPlatform = new() { ["WinDomain"] = "PSM-RDP" }, SharedLists = [@"\\fs\l.json"],
            HostKeys = new() { ["psmp.corp.local:22"] = "ssh-ed25519 SHA256:AAAA" }, CentralFile = @"\\fs\env.json",
            KeepPvwaSessionAlive = true, KeepAccountListCopy = true, SshInApp = true, PsmDesktopInTab = true, PsmRedirectDrives = true,
            PauseRdpDisplay = true, CheckForUpdates = false, UploadProtocol = TransferProtocol.Sftp, DutyText = "N2", DutyTextFile = @"\\fs\d.txt",
            InterventionReminders = [], SessionSharing = true, ShareDirect = true, SharePort = 50000, ShareRelayFolder = @"\\fs\relais",
            ServerTags = [],
        };
        Assert.Equal(Enum.GetValues<EnvironmentSetting>().Order(), full.DefinedSettings().Order());
    }
}
