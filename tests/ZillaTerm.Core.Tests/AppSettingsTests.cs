namespace ZillaTerm.Core.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Premier démarrage de ZillaTerm : tout le dossier de CyberArkTerm est copié (réglages, sauvegarde, coffre local,
    /// sous-dossiers), l'ancien gardé ; plus rien n'est copié une fois les réglages ZillaTerm présents.
    /// </summary>
    [Fact]
    public void ImportLegacyFolder_CopiesTheOldFolderOnce()
    {
        var legacy = Path.Combine(_dir, "CyberArkTerm");
        var target = Path.Combine(_dir, "ZillaTerm");
        Directory.CreateDirectory(Path.Combine(legacy, "keepass"));
        new AppSettings { PvwaUrl = "pvwa.corp.com", UserName = "jdupont" }.Save(Path.Combine(legacy, "settings.json"));
        File.WriteAllText(Path.Combine(legacy, "transfers.json"), "[]");
        File.WriteAllText(Path.Combine(legacy, "keepass", "store.bin"), "coffre");

        Assert.True(AppSettings.ImportLegacyFolder(legacy, target));
        Assert.Equal(("pvwa.corp.com", "jdupont"), (AppSettings.Load(Path.Combine(target, "settings.json")).PvwaUrl,
            AppSettings.Load(Path.Combine(target, "settings.json")).UserName));
        Assert.Equal("coffre", File.ReadAllText(Path.Combine(target, "keepass", "store.bin")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));

        // Réglages ZillaTerm déjà présents : l'ancien dossier n'est plus relu.
        File.WriteAllText(Path.Combine(legacy, "transfers.json"), "[1]");
        Assert.False(AppSettings.ImportLegacyFolder(legacy, target));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(target, "transfers.json")));
        Assert.False(AppSettings.ImportLegacyFolder(Path.Combine(_dir, "absent"), Path.Combine(_dir, "neuf")));
    }

    /// <summary>Copie interrompue (réglages absents, autres fichiers déjà copiés) : reprise sans rien écraser.</summary>
    [Fact]
    public void ImportLegacyFolder_ResumesWithoutOverwriting()
    {
        var legacy = Path.Combine(_dir, "CyberArkTerm");
        var target = Path.Combine(_dir, "ZillaTerm");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        new AppSettings { PvwaUrl = "pvwa.corp.com" }.Save(Path.Combine(legacy, "settings.json"));
        File.WriteAllText(Path.Combine(legacy, "transfers.json"), "ancien");
        File.WriteAllText(Path.Combine(target, "transfers.json"), "déjà copié");

        Assert.True(AppSettings.ImportLegacyFolder(legacy, target));
        Assert.Equal("déjà copié", File.ReadAllText(Path.Combine(target, "transfers.json")));
        Assert.Equal("pvwa.corp.com", AppSettings.Load(Path.Combine(target, "settings.json")).PvwaUrl);
    }

    /// <summary>
    /// Réglages ZillaTerm illisibles mis de côté, sauvegarde reprise en mémoire, puis fermeture sans enregistrer : au
    /// démarrage suivant, les anciens réglages CyberArkTerm ne reviennent pas à leur place.
    /// </summary>
    [Fact]
    public void ImportLegacyFolder_NeverReplacesSettingsSetAside()
    {
        var legacy = Path.Combine(_dir, "CyberArkTerm");
        var target = Path.Combine(_dir, "ZillaTerm");
        var path = Path.Combine(target, "settings.json");
        new AppSettings { PvwaUrl = "ancien.corp.com" }.Save(Path.Combine(legacy, "settings.json"));
        new AppSettings { PvwaUrl = "pvwa.corp.com" }.Save(path);
        new AppSettings { PvwaUrl = "pvwa.corp.com" }.Save(path);
        File.WriteAllText(path, "{ abîmé");

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.RestoredFromBackup);
        Assert.False(File.Exists(path));
        Assert.False(AppSettings.ImportLegacyFolder(legacy, target));
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// settings.json supprimé (ou remplacement interrompu), sauvegarde restée en place, ancien dossier CyberArkTerm encore
    /// là : les anciens réglages ne reviennent pas, la sauvegarde est reprise et n'est pas écrasée par eux.
    /// </summary>
    [Fact]
    public void ImportLegacyFolder_NeverReplacesTheBackupOfZillaTerm()
    {
        var legacy = Path.Combine(_dir, "CyberArkTerm");
        var target = Path.Combine(_dir, "ZillaTerm");
        var path = Path.Combine(target, "settings.json");
        new AppSettings { PvwaUrl = "ancien.corp.local" }.Save(Path.Combine(legacy, "settings.json"));
        new AppSettings { PvwaUrl = "pvwa-new.corp.local" }.Save(path);
        new AppSettings { PvwaUrl = "pvwa-newer.corp.local" }.Save(path);
        File.Delete(path);

        Assert.False(AppSettings.ImportLegacyFolder(legacy, target));
        var loaded = AppSettings.Load(path);

        Assert.Equal("pvwa-new.corp.local", loaded.PvwaUrl);
        Assert.True(loaded.RestoredFromBackup);
        Assert.Null(loaded.SetAsideFile);
        Assert.Equal("pvwa-new.corp.local", AppSettings.Load(DurableFile.BackupPath(path)).PvwaUrl);
    }

    /// <summary>
    /// Ancien dossier avec sa sauvegarde : elle est copiée après les réglages, si bien qu'une copie interrompue avant
    /// eux est reprise (aucune sauvegarde n'est encore là pour la bloquer).
    /// </summary>
    [Fact]
    public void ImportLegacyFolder_CopiesTheBackupAfterTheSettings()
    {
        var legacy = Path.Combine(_dir, "CyberArkTerm");
        var target = Path.Combine(_dir, "ZillaTerm");
        new AppSettings { PvwaUrl = "avant.corp.local" }.Save(Path.Combine(legacy, "settings.json"));
        new AppSettings { PvwaUrl = "ancien.corp.local" }.Save(Path.Combine(legacy, "settings.json"));

        Assert.True(AppSettings.ImportLegacyFolder(legacy, target));

        var path = Path.Combine(target, "settings.json");
        Assert.Equal("ancien.corp.local", AppSettings.Load(path).PvwaUrl);
        Assert.Equal("avant.corp.local", AppSettings.Load(DurableFile.BackupPath(path)).PvwaUrl);
    }

    /// <summary>
    /// Sessions PSM dans l'onglet : la résolution suit l'onglet par défaut, aussi avec un fichier de réglages qui ne
    /// mentionne pas l'option ; l'option désactivée le reste.
    /// </summary>
    [Fact]
    public void PsmDynamicResolutionIsOnByDefaultAndKept()
    {
        Assert.True(new AppSettings().PsmDynamicResolution);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "dynamic.json");
        File.WriteAllText(path, "{}");
        Assert.True(AppSettings.Load(path).PsmDynamicResolution);
        new AppSettings { PsmDynamicResolution = false }.Save(path);
        Assert.False(AppSettings.Load(path).PsmDynamicResolution);
    }

    /// <summary>Disques du poste partagés avec les sessions PSM Windows : par défaut, y compris pour des réglages plus anciens.</summary>
    [Fact]
    public void PsmRedirectDrivesIsOnByDefaultAndKept()
    {
        Assert.True(new AppSettings().PsmRedirectDrives);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "drives.json");
        File.WriteAllText(path, "{}");
        Assert.True(AppSettings.Load(path).PsmRedirectDrives);
        new AppSettings { PsmRedirectDrives = false }.Save(path);
        Assert.False(AppSettings.Load(path).PsmRedirectDrives);
    }

    /// <summary>Thème de l'interface : celui de Windows par défaut, le choix gardé, une valeur inconnue ramenée au défaut.</summary>
    [Fact]
    public void ThemeDefaultsToWindowsAndIsKept()
    {
        Assert.Equal(AppTheme.System, new AppSettings().Theme);
        var path = Path.Combine(_dir, "theme.json");
        new AppSettings { Theme = AppTheme.Dark }.Save(path);
        Assert.Equal(AppTheme.Dark, AppSettings.Load(path).Theme);

        File.WriteAllText(path, """{ "PvwaUrl": "https://pvwa", "Theme": 7 }""");
        Assert.Equal(AppTheme.System, AppSettings.Load(path).Theme);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        new AppSettings { PvwaUrl = "https://pvwa", UserName = "jdoe", AuthMethod = AuthMethod.LDAP, Language = "it" }.Save(path);

        var loaded = AppSettings.Load(path);

        Assert.Equal("it", loaded.Language);

        Assert.Equal("https://pvwa", loaded.PvwaUrl);
        Assert.Equal("jdoe", loaded.UserName);
        Assert.Equal(AuthMethod.LDAP, loaded.AuthMethod);
        Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Envois : SFTP d'abord, y compris pour une installation existante dont l'ancienne clé valait SCP (défaut de
    /// l'époque) ; un choix fait avec la nouvelle clé est gardé.
    /// </summary>
    [Fact]
    public void Load_UploadsPreferSftp_ExceptWhenScpChosenWithTheNewKey()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "PvwaUrl": "https://pvwa", "UploadProtocol": 0 }""");
        Assert.Equal(Core.Ssh.TransferProtocol.Sftp, AppSettings.Load(path).PreferredUploadProtocol);
        Assert.Equal(Core.Ssh.TransferProtocol.Sftp, new AppSettings().PreferredUploadProtocol);

        new AppSettings { PreferredUploadProtocol = Core.Ssh.TransferProtocol.Scp }.Save(path);
        Assert.Equal(Core.Ssh.TransferProtocol.Scp, AppSettings.Load(path).PreferredUploadProtocol);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsDefaults()
    {
        Assert.Equal("", AppSettings.Load(Path.Combine(_dir, "missing.json")).PvwaUrl);

        Directory.CreateDirectory(_dir);
        var corrupt = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.Equal(AuthMethod.CyberArk, AppSettings.Load(corrupt).AuthMethod);
    }

    /// <summary>
    /// Connexions récentes : propres à chaque PVWA (le même ID de compte y désigne un autre compte) ; celles des versions
    /// qui ne notaient pas le PVWA sont oubliées au chargement.
    /// </summary>
    [Fact]
    public void RecentSessionsBelongToTheirPvwa()
    {
        var settings = new AppSettings();
        settings.AddRecent(new RecentSession { AccountId = "12_3", PvwaHost = "pvwa-a", Mode = "PSM-RDP" });
        settings.AddRecent(new RecentSession { AccountId = "12_3", PvwaHost = "pvwa-b", Mode = "PSM-RDP" });
        settings.AddRecent(new RecentSession { AccountId = "12_3", PvwaHost = "PVWA-A", Mode = "PSM-RDP" });
        Assert.Equal(["PVWA-A", "pvwa-b"], settings.Recent.Select(r => r.PvwaHost));
        Assert.True(settings.Recent[0].IsForHost("pvwa-a"));
        Assert.False(settings.Recent[1].IsForHost("pvwa-a"));

        var path = Path.Combine(Path.GetTempPath(), $"cat-recent-{Guid.NewGuid():N}.json");
        try
        {
            settings.Recent.Add(new RecentSession { AccountId = "7_1", Mode = "SSH" });
            settings.Save(path);
            Assert.Equal(2, AppSettings.Load(path).Recent.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Compte de domaine utilisé sur deux serveurs : deux connexions récentes, et les deux serveurs restent proposés par
    /// « Sur quel serveur ? » ; le même serveur une seconde fois remplace la première connexion (machine sans tenir compte
    /// des majuscules ni des espaces).
    /// </summary>
    [Fact]
    public void RecentSessionsOfADomainAccountKeepEachServer()
    {
        var settings = new AppSettings();
        var account = new PvwaAccount { Id = "5_2", UserName = "jdoe-adm", Address = "corp.local", PlatformId = "WinDomain" };
        settings.AddRecent(new RecentSession { AccountId = "5_2", PvwaHost = "pvwa", Mode = "PSM-RDP", RemoteMachine = "srv01.corp.local" });
        settings.AddRecent(new RecentSession { AccountId = "5_2", PvwaHost = "pvwa", Mode = "PSM-RDP", RemoteMachine = "srv02.corp.local" });

        Assert.Equal(["srv02.corp.local", "srv01.corp.local"], settings.Recent.Select(r => r.RemoteMachine));
        Assert.Equal(["srv02.corp.local", "srv01.corp.local"], SessionLibrary.KnownMachines(settings, account, "pvwa").Take(2));

        settings.AddRecent(new RecentSession { AccountId = "5_2", PvwaHost = "pvwa", Mode = "PSM-RDP", RemoteMachine = " SRV01.corp.local" });
        Assert.Equal([" SRV01.corp.local", "srv02.corp.local"], settings.Recent.Select(r => r.RemoteMachine));
    }

    /// <summary>
    /// Composants proposés : celui mémorisé pour la plateforme d'abord, puis ceux déjà utilisés (même absents de la liste
    /// usuelle, comme WIN-PSM), puis les usuels, sans doublon ni connexion SSH.
    /// </summary>
    [Fact]
    public void KnownComponentsPutThePlatformAndUsedOnesFirst()
    {
        var settings = new AppSettings();
        settings.RememberComponent("WinDomain", "WIN-PSM");
        settings.RememberComponent("UnixSSH", "PSM-SSH");
        settings.Sessions.Add(new SavedSession { Mode = ConnectMode.Psm, Component = " PSM-RDP-Custom " });
        settings.Sessions.Add(new SavedSession { Mode = ConnectMode.Ssh, Component = "Ignored" });
        settings.AddRecent(new RecentSession { AccountId = "1", Mode = "SSH" });
        settings.AddRecent(new RecentSession { AccountId = "2", Mode = "win-psm" });
        settings.AddRecent(new RecentSession { AccountId = "3", Mode = "PSM-WebApp" });
        // Fichiers seuls (PSMP) : « SFTP » n'est pas un composant PSM.
        settings.AddRecent(new RecentSession { AccountId = "4", Mode = RecentSession.SftpMode });

        var components = settings.KnownComponents("windomain");

        Assert.Equal(["WIN-PSM", "PSM-SSH", "PSM-RDP-Custom", "PSM-WebApp"], components.Take(4));
        Assert.Contains("PSM-RDP", components);
        Assert.DoesNotContain("SSH", components);
        Assert.DoesNotContain("SFTP", components, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ignored", components);
        Assert.Equal(components.Count, components.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(AccountClassifier.CommonComponents, new AppSettings().KnownComponents("WinDomain"));
    }

    /// <summary>
    /// Fenêtre principale remise où elle était, sauf si elle tomberait hors des écrans actuels (écran débranché) ; une
    /// taille plus grande que l'écran est ramenée à l'écran, une taille trop petite au minimum.
    /// </summary>
    [Fact]
    public void WindowPlacementIsKeptOnlyWhenVisible()
    {
        var screen = (0d, 0d, 1920d, 1080d);
        var kept = new WindowPlacement(100, 80, 1280, 780, false).FitIn(screen, 820, 480);
        Assert.Equal(new WindowPlacement(100, 80, 1280, 780, false), kept);

        Assert.Null(new WindowPlacement(2500, 80, 1280, 780, false).FitIn(screen, 820, 480));
        Assert.Null(new WindowPlacement(100, 1200, 1280, 780, true).FitIn(screen, 820, 480));
        Assert.Null(new WindowPlacement(double.NaN, 0, 1280, 780, false).FitIn(screen, 820, 480));

        var resized = new WindowPlacement(0, 0, 4000, 100, true).FitIn(screen, 820, 480);
        Assert.Equal(new WindowPlacement(0, 0, 1920, 480, true), resized);
    }

    [Fact]
    public void LayoutIsSavedWithTheSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cat-layout-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings
            {
                SidePanelWidth = 455,
                SidePanelCollapsed = true,
                MainWindowPlacement = new WindowPlacement(10, 20, 1300, 800, true),
                // La colonne Nom ne se masque pas, même dans un fichier modifié à la main.
                HiddenFileColumns = [Core.Ssh.RemoteSortColumn.Modified, Core.Ssh.RemoteSortColumn.Name, Core.Ssh.RemoteSortColumn.Group],
            }.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.Equal(455, loaded.SidePanelWidth);
            Assert.True(loaded.SidePanelCollapsed);
            Assert.Equal(new WindowPlacement(10, 20, 1300, 800, true), loaded.MainWindowPlacement);
            Assert.Equal([Core.Ssh.RemoteSortColumn.Modified, Core.Ssh.RemoteSortColumn.Group], loaded.HiddenFileColumns);
            Assert.Empty(new AppSettings().HiddenFileColumns);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
