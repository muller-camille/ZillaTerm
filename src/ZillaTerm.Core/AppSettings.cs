using System.Text.Json;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core;

/// <summary>
/// Préférences mémorisées entre deux lancements. Le mot de passe n'est jamais enregistré.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Langue de l'interface (« fr », « en », « it ») ; vide = langue de Windows.</summary>
    public string Language { get; set; } = "";

    public string PvwaUrl { get; set; } = "";

    public string UserName { get; set; } = "";

    public AuthMethod AuthMethod { get; set; } = AuthMethod.CyberArk;

    /// <summary>
    /// Adresse du PSM for SSH (PSMP) ; vide = connexions SSH désactivées. Renseignée, les comptes Unix s'ouvrent par
    /// défaut en SSH via le PSMP.
    /// </summary>
    public string PsmpAddress { get; set; } = "";

    public int PsmpPort { get; set; } = 22;

    /// <summary>
    /// Autres PSMP : chaque serveur passe par celui de son domaine le plus proche, sinon par <see cref="PsmpAddress"/>.
    /// Voir <see cref="PsmpRouting"/>.
    /// </summary>
    public List<PsmpServer> PsmpServers { get; set; } = [];

    public GroupBy GroupBy { get; set; } = GroupBy.Safe;

    /// <summary>Anciens favoris (remplacés par « Mes serveurs », migrés au chargement des comptes).</summary>
    public List<string> Favorites { get; set; } = [];

    /// <summary>
    /// Fichier d'environnement central (partage réseau) : relu à chaque démarrage, ses changements sont montrés avant
    /// d'être appliqués. Voir <see cref="EnvironmentProfile"/>.
    /// </summary>
    public string EnvironmentFile { get; set; } = "";

    /// <summary>
    /// Empreinte SHA-256 de chaque fichier d'environnement déjà proposé (chemin en minuscules → empreinte) : un fichier
    /// n'est reproposé que s'il a changé.
    /// </summary>
    public Dictionary<string, string> EnvironmentFileHashes { get; set; } = [];

    /// <summary>Fenêtre « Sur quel serveur ? » (compte de domaine) : dernier choix de « Garder dans « Mes serveurs » ».</summary>
    public bool KeepChosenServer { get; set; }

    /// <summary>Dossier de « Mes serveurs » choisi la dernière fois dans la fenêtre « Sur quel serveur ? ».</summary>
    public string KeepChosenServerFolder { get; set; } = "";

    /// <summary>Dossiers de « Mes serveurs » (« Prod/Web »...), y compris les dossiers vides.</summary>
    public List<string> SessionFolderList { get; set; } = [];

    /// <summary>Serveurs de « Mes serveurs », avec leur configuration.</summary>
    public List<SavedSession> Sessions { get; set; } = [];

    /// <summary>Requête légère régulière pour que la session PVWA n'expire pas par inactivité.</summary>
    public bool KeepPvwaSessionAlive { get; set; } = true;

    /// <summary>Coffres KeePass affichés comme dossiers de « Mes serveurs » (accès d'urgence hors CyberArk).</summary>
    public List<KeePassFolder> KeePassFolders { get; set; } = [];

    /// <summary>Listes de serveurs partagées (fichiers sur un partage réseau) affichées dans « Mes serveurs ».</summary>
    public List<string> SharedLists { get; set; } = [];

    /// <summary>Sessions SSH dans un onglet ZillaTerm (terminal + navigateur de fichiers) plutôt que Windows Terminal.</summary>
    public bool SshInApp { get; set; } = true;

    /// <summary>
    /// Sessions PSM Windows (bureau) dans un onglet de ZillaTerm, avec le contrôle Bureau à distance, plutôt que dans
    /// Connexion Bureau à distance (mstsc). Les composants en application distante (PSM-SSH, WinSCP…) restent dans mstsc.
    /// </summary>
    public bool PsmDesktopInTab { get; set; }

    /// <summary>
    /// Sessions PSM dans l'onglet : la résolution du bureau distant suit la taille de l'onglet au lieu d'une image mise à
    /// l'échelle (activé par défaut ; à désactiver si la connexion du PSM vers la cible garde sa taille).
    /// </summary>
    public bool PsmDynamicResolution { get; set; } = true;

    /// <summary>
    /// Sessions PSM Windows (onglet et Connexion Bureau à distance) : tous les disques de ce poste partagés avec la session
    /// (activé par défaut). Désactivé : seulement si le fichier du PVWA le demande. Le serveur PSM peut le refuser.
    /// </summary>
    public bool PsmRedirectDrives { get; set; } = true;

    /// <summary>Journal de débogage (déroulement des connexions, sans secret), désactivé par défaut.</summary>
    public bool DebugLogEnabled { get; set; }

    /// <summary>
    /// Protocole essayé d'abord pour déposer des fichiers sur le serveur ; refusé par le serveur, l'autre prend le
    /// relais. Nouvelle clé (l'ancienne, « UploadProtocol », valait SCP par défaut) : toutes les installations passent
    /// à SFTP une fois.
    /// </summary>
    public TransferProtocol PreferredUploadProtocol { get; set; } = TransferProtocol.Sftp;

    /// <summary>Proposer d'envoyer une seule archive .tar.gz quand on dépose au moins <see cref="ArchiveThreshold"/> fichiers.</summary>
    public bool OfferArchive { get; set; } = true;

    /// <summary>Nombre de fichiers à partir duquel l'archive .tar.gz est proposée.</summary>
    public int ArchiveThreshold { get; set; } = 200;

    /// <summary>
    /// Le suivi d'un fichier (tail -f) ouvre sa propre connexion SFTP (une session PSMP de plus) au lieu de partager
    /// celle de l'onglet Fichiers.
    /// </summary>
    public bool TailIndependentSession { get; set; }

    /// <summary>Suivi d'un fichier : couleur des lignes selon leur niveau (erreur, avertissement).</summary>
    public bool TailLevelColors { get; set; } = true;

    /// <summary>Suivi d'un fichier : mots surlignés, séparés par des virgules.</summary>
    public string TailHighlights { get; set; } = "";

    /// <summary>Suivi d'un fichier : mots qui déclenchent une alerte, séparés par des virgules.</summary>
    public string TailAlerts { get; set; } = "";

    /// <summary>Suivi d'un fichier : notification Windows (sans le contenu de la ligne) pour une alerte.</summary>
    public bool TailAlertNotify { get; set; } = true;

    /// <summary>
    /// Rechercher une nouvelle version au démarrage (au plus une fois par jour) : une requête vers GitHub, désactivée
    /// par défaut. La recherche reste possible à la demande depuis « À propos ».
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>Dernière recherche de nouvelle version (UTC).</summary>
    public DateTime LastUpdateCheck { get; set; }

    /// <summary>Couleurs de l'interface : celles de Windows (clair ou sombre), claires ou sombres.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Palette de couleurs des terminaux SSH (identifiant d'une palette de <c>TerminalTheme</c>).</summary>
    public string TerminalTheme { get; set; } = "campbell";

    /// <summary>Taille de police par défaut des terminaux SSH (Ctrl+molette la change pour un terminal).</summary>
    public double TerminalFontSize { get; set; } = 14;

    /// <summary>Le clic droit dans le terminal colle le presse-papiers au lieu d'ouvrir le menu (Maj+clic droit l'ouvre).</summary>
    public bool TerminalRightClickPastes { get; set; }

    /// <summary>Confirmation avant de fermer une session connectée (SSH, Bureau à distance, VNC).</summary>
    public bool ConfirmCloseSession { get; set; } = true;

    /// <summary>
    /// Avertissement avant de coller plusieurs lignes dans un terminal dont le shell n'a pas activé le collage protégé
    /// (bash avant 5.1, ksh…) : chaque ligne y part comme une commande.
    /// </summary>
    public bool ConfirmMultiLinePaste { get; set; } = true;

    /// <summary>Outil de comparaison de fichiers (exécutable) proposé dans la fenêtre de comparaison ; vide = aucun.</summary>
    public string CompareTool { get; set; } = "";

    /// <summary>Arguments de l'outil de comparaison : {0} = fichier de gauche, {1} = fichier de droite.</summary>
    public string CompareToolArguments { get; set; } = DefaultCompareArguments;

    public const string DefaultCompareArguments = "\"{0}\" \"{1}\"";

    /// <summary>Installe PROMPT_COMMAND à l'ouverture d'une session SSH pour que le navigateur suive le dossier du terminal.</summary>
    public bool FollowTerminalFolder { get; set; } = true;

    /// <summary>
    /// Consignes d'astreinte (bonnes pratiques, numéros d'urgence…) affichées dans la fenêtre « Intervention » ; en général
    /// diffusé par le fichier d'environnement de l'équipe.
    /// </summary>
    public string DutyText { get; set; } = "";

    /// <summary>
    /// Fichier des consignes d'astreinte (partage réseau en général), relu à chaque ouverture de la fenêtre « Intervention » ;
    /// remplace <see cref="DutyText"/>, montré si le fichier est illisible. Vide : pas de fichier.
    /// </summary>
    public string DutyTextFile { get; set; } = "";

    /// <summary>Serveur X de ce poste pour le transfert X11 (voir <see cref="Ssh.X11Display"/>), « :0 » par défaut.</summary>
    public string X11Display { get; set; } = Ssh.X11Display.Default;

    /// <summary>
    /// Serveurs de « Mes serveurs » (identifiants) dont les sessions SSH transfèrent X11 : un choix personnel, jamais
    /// exporté ni partagé (le serveur pourra utiliser le serveur X de ce poste).
    /// </summary>
    public List<string> X11Servers { get; set; } = [];

    /// <summary>
    /// Étiquettes proposées pour les serveurs de « Mes serveurs » (PROD, QA, DEV par défaut), avec leur couleur ;
    /// modifiables dans les Paramètres et diffusées par le fichier d'environnement.
    /// </summary>
    public List<ServerTag> ServerTags { get; set; } = ServerTagRules.Defaults();

    public bool ShowHiddenFiles { get; set; }

    /// <summary>Largeur du panneau de gauche (pixels indépendants) ; 0 = largeur par défaut.</summary>
    public double SidePanelWidth { get; set; }

    /// <summary>Panneau de gauche replié (Ctrl+B, double-clic sur le séparateur).</summary>
    public bool SidePanelCollapsed { get; set; }

    /// <summary>Position, taille et état de la fenêtre principale à sa dernière fermeture ; null = centrée, taille par défaut.</summary>
    public WindowPlacement? MainWindowPlacement { get; set; }

    /// <summary>Tri de l'onglet Fichiers : colonne dont l'en-tête a été cliqué, et sens.</summary>
    public RemoteSortColumn FileSortColumn { get; set; } = RemoteSortColumn.Name;

    public bool FileSortDescending { get; set; }

    /// <summary>Colonnes de l'onglet Fichiers masquées par l'utilisateur (menu de l'en-tête) ; la colonne Nom reste toujours.</summary>
    public List<RemoteSortColumn> HiddenFileColumns { get; set; } = [];

    /// <summary>Éditeur de texte pour « Modifier » dans l'onglet Fichiers (chemin d'un exécutable) ; vide = Bloc-notes.</summary>
    public string TextEditor { get; set; } = "";

    /// <summary>Empreintes des clés d'hôte PSMP acceptées (« hôte:port » → « algorithme SHA256:... »).</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = [];

    /// <summary>Serveurs FTP (« hôte:port ») déjà vus avec TLS (voir <see cref="Ftp.FtpTlsMemory"/>).</summary>
    public List<string> FtpTlsServers { get; set; } = [];

    public List<RecentSession> Recent { get; set; } = [];

    /// <summary>Composant PSM choisi par l'utilisateur, par ID de plateforme.</summary>
    public Dictionary<string, string> ComponentByPlatform { get; set; } = [];

    /// <summary>
    /// Composant PSM des comptes Windows (domaine ou locaux) sans composant mémorisé pour leur plateforme, par ex. WIN-PSM ;
    /// vide : PSM-RDP.
    /// </summary>
    public string WindowsComponent { get; set; } = "";

    public const int MaxRecent = 15;

    public void AddRecent(RecentSession session)
    {
        Recent.RemoveAll(r => r.AccountId == session.AccountId && r.Mode == session.Mode
                              && string.Equals(r.PvwaHost, session.PvwaHost, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, session);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
    }

    /// <summary>Composant mémorisé pour la plateforme du compte, sinon celui déduit de la plateforme.</summary>
    public string ResolveComponent(PvwaAccount account)
    {
        foreach (var (platform, component) in ComponentByPlatform)
        {
            if (string.Equals(platform, account.PlatformId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(component))
            {
                return component;
            }
        }

        if (!string.IsNullOrWhiteSpace(WindowsComponent) && AccountClassifier.Classify(account) == AccountKind.Windows)
        {
            return WindowsComponent.Trim();
        }

        return AccountClassifier.DefaultComponent(account);
    }

    /// <summary>Nom de composant PSM acceptable (lettres, chiffres, « - », « _ », « . »), par ex. PSM-RDP ou WIN-PSM.</summary>
    public static bool IsValidComponentName(string? name) =>
        (name ?? "").Trim() is { Length: > 0 and <= 100 } value && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Composants proposés dans les listes : celui mémorisé pour la plateforme <paramref name="platformId"/>, puis ceux
    /// déjà utilisés (mémorisés, serveurs de « Mes serveurs », connexions récentes), puis les composants usuels de CyberArk.
    /// Un PVWA peut nommer les siens autrement (par ex. WIN-PSM) : saisi une fois, un composant est ensuite proposé.
    /// </summary>
    public IReadOnlyList<string> KnownComponents(string? platformId)
    {
        var list = new List<string>();
        void Add(string? component)
        {
            var name = component?.Trim();
            if (!string.IsNullOrEmpty(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name);
            }
        }

        foreach (var (platform, component) in ComponentByPlatform)
        {
            if (string.Equals(platform, platformId, StringComparison.OrdinalIgnoreCase))
            {
                Add(component);
            }
        }

        Add(WindowsComponent);
        foreach (var component in ComponentByPlatform.Values)
        {
            Add(component);
        }

        foreach (var session in Sessions.Where(s => s.Mode == ConnectMode.Psm))
        {
            Add(session.Component);
        }

        // Le mode d'une connexion récente est son composant PSM, ou « SSH » (PSMP).
        foreach (var recent in Recent.Where(r => !string.Equals(r.Mode, RecentSession.SshMode, StringComparison.OrdinalIgnoreCase)))
        {
            Add(recent.Mode);
        }

        foreach (var component in AccountClassifier.CommonComponents)
        {
            Add(component);
        }

        return list;
    }

    /// <summary>
    /// Remplace les étiquettes (Paramètres). Les serveurs de « Mes serveurs » suivent les renommages
    /// (<paramref name="renamed"/> : ancien nom → nouveau) et perdent une étiquette supprimée.
    /// </summary>
    public void ReplaceServerTags(IEnumerable<ServerTag> tags, IReadOnlyDictionary<string, string> renamed)
    {
        ServerTags = ServerTagRules.Sanitize(tags);
        var renames = new Dictionary<string, string>(renamed, StringComparer.OrdinalIgnoreCase);
        foreach (var session in Sessions.Where(s => s.Tag is not null))
        {
            if (renames.TryGetValue(session.Tag!, out var newName))
            {
                session.Tag = newName;
            }

            session.Tag = ServerTagRules.Find(ServerTags, session.Tag)?.Name;
        }
    }

    public void RememberComponent(string? platformId, string component)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return;
        }

        foreach (var key in ComponentByPlatform.Keys.Where(k => string.Equals(k, platformId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ComponentByPlatform.Remove(key);
        }

        ComponentByPlatform[platformId] = component;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZillaTerm",
        "settings.json");

    /// <summary>Nom de l'application jusqu'à la version 0.18 : son dossier de réglages est repris au premier démarrage.</summary>
    public const string LegacyName = "CyberArkTerm";

    public static string LegacyDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);

    /// <summary>
    /// Premier démarrage sous le nom ZillaTerm : copie le dossier de CyberArkTerm (réglages et leur sauvegarde, coffre local,
    /// historique des transferts…) quand <paramref name="directory"/> n'a pas encore de réglages. L'ancien dossier est gardé
    /// (retour possible à l'ancienne version) et un fichier déjà présent n'est jamais remplacé. <c>settings.json</c> est
    /// copié en dernier : une copie interrompue est reprise au démarrage suivant. Des réglages ZillaTerm illisibles mis de
    /// côté (voir <see cref="Load"/>) ne sont jamais remplacés par les anciens : ZillaTerm a déjà servi.
    /// </summary>
    /// <returns>Vrai si les réglages ont été repris.</returns>
    public static bool ImportLegacyFolder(string legacyDirectory, string directory)
    {
        var settings = Path.Combine(directory, "settings.json");
        var legacySettings = Path.GetFullPath(Path.Combine(legacyDirectory, "settings.json"));
        if (File.Exists(settings) || !File.Exists(legacySettings)
            || (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "settings.json" + SetAsideSuffix + "*").Any()))
        {
            return false;
        }

        CopyMissing(new DirectoryInfo(legacyDirectory), directory, legacySettings);
        File.Copy(legacySettings, settings, overwrite: false);
        return true;
    }

    private static void CopyMissing(DirectoryInfo source, string target, string last)
    {
        Directory.CreateDirectory(target);
        foreach (var file in source.EnumerateFiles())
        {
            var to = Path.Combine(target, file.Name);
            if (!string.Equals(file.FullName, last, StringComparison.OrdinalIgnoreCase) && !File.Exists(to))
            {
                file.CopyTo(to);
            }
        }

        foreach (var sub in source.EnumerateDirectories())
        {
            CopyMissing(sub, Path.Combine(target, sub.Name), last);
        }
    }

    /// <summary>
    /// Fichier de réglages illisible (écriture interrompue, disque plein…) mis de côté au chargement : il n'est pas écrasé
    /// par les valeurs par défaut, et l'application le signale. Null si le fichier a été lu.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SetAsideFile { get; private set; }

    /// <summary>Réglages repris de la sauvegarde précédente (<c>settings.json.bak</c>) après un fichier illisible.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RestoredFromBackup { get; private set; }

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            // Remplacement interrompu (partage réseau, antivirus) : l'ancien fichier est resté en sauvegarde. Sans cette
            // reprise, les réglages par défaut reviendraient, puis écraseraient les vrais au prochain enregistrement.
            if (File.Exists(BackupPath(path)))
            {
                try
                {
                    var restored = Read(BackupPath(path));
                    restored.RestoredFromBackup = true;
                    return restored;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Sauvegarde illisible aussi : réglages par défaut, la sauvegarde reste en place.
                }
            }

            return new AppSettings();
        }

        try
        {
            return Read(path);
        }
        catch (JsonException)
        {
            // Fichier abîmé : mis de côté (jamais écrasé), puis la sauvegarde précédente si elle se lit.
            var aside = $"{path}{SetAsideSuffix}{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                aside = path;
            }

            AppSettings settings;
            bool restored = false;
            try
            {
                settings = File.Exists(BackupPath(path)) ? Read(BackupPath(path)) : new AppSettings();
                restored = File.Exists(BackupPath(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                settings = new AppSettings();
            }

            settings.SetAsideFile = aside;
            settings.RestoredFromBackup = restored;
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fichier momentanément inaccessible (verrouillé, partage réseau absent) : valeurs par défaut pour cette fois.
            return new AppSettings();
        }
    }

    private static AppSettings Read(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        settings.Favorites ??= [];
        settings.Recent ??= [];
        // Connexions récentes des versions sans PVWA : leur ID de compte peut désigner un autre compte sur un autre PVWA,
        // elles sont oubliées.
        settings.Recent.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.PvwaHost));
        settings.ComponentByPlatform ??= [];
        settings.SessionFolderList ??= [];
        settings.Sessions ??= [];
        settings.Sessions.RemoveAll(s => s is null);
        settings.KeePassFolders ??= [];
        settings.KeePassFolders.RemoveAll(f => f is null);
        settings.SharedLists ??= [];
        settings.KnownHosts ??= [];
        settings.FtpTlsServers ??= [];
        settings.HiddenFileColumns ??= [];
        settings.HiddenFileColumns.RemoveAll(c => c == RemoteSortColumn.Name || !Enum.IsDefined(c));
        settings.Language ??= "";
        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = AppTheme.System;
        }

        settings.PvwaUrl ??= "";
        settings.UserName ??= "";
        settings.PsmpAddress ??= "";
        settings.KeepChosenServerFolder ??= "";
        settings.WindowsComponent ??= "";
        settings.EnvironmentFile ??= "";
        settings.EnvironmentFileHashes ??= [];
        settings.PsmpServers ??= [];
        settings.PsmpServers.RemoveAll(p => p is null);
        foreach (var psmp in settings.PsmpServers)
        {
            psmp.Address ??= "";
            psmp.Domain ??= "";
        }

        settings.TextEditor ??= "";
        settings.TailHighlights ??= "";
        settings.TailAlerts ??= "";
        settings.CompareTool ??= "";
        settings.TerminalTheme ??= "campbell";
        if (!Ssh.X11Display.TryParse(settings.X11Display, out _))
        {
            settings.X11Display = Ssh.X11Display.Default;
        }

        settings.DutyText ??= "";
        settings.DutyTextFile = DutyReminderFile.IsValidPath(settings.DutyTextFile) ? settings.DutyTextFile.Trim() : "";
        settings.X11Servers ??= [];
        settings.X11Servers.RemoveAll(string.IsNullOrWhiteSpace);
        settings.ServerTags = ServerTagRules.Sanitize(settings.ServerTags);
        settings.CompareToolArguments ??= DefaultCompareArguments;
        foreach (var session in settings.Sessions)
        {
            session.TailFiles ??= [];
            session.Tag = ServerTagRules.NormalizeName(session.Tag);
        }

        return settings;
    }

    private static string BackupPath(string path) => DurableFile.BackupPath(path);

    /// <summary>Suffixe d'un fichier de réglages illisible mis de côté (suivi de la date).</summary>
    private const string SetAsideSuffix = ".illisible-";

    /// <summary>
    /// Écrit dans un fichier temporaire puis le met à la place de l'ancien (gardé en <c>.bak</c>) : une écriture interrompue
    /// ne laisse jamais un fichier de réglages à moitié écrit.
    /// </summary>
    public void Save(string path)
    {
        var temp = DurableFile.WriteTemp(path, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
        DurableFile.Replace(temp, path);
    }
}

/// <summary>Position et taille d'une fenêtre (pixels indépendants), et si elle était agrandie.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>
    /// Placement utilisable sur l'écran actuel (<paramref name="screen"/> : bureau virtuel, tous écrans) : taille au moins
    /// minimale, et au moins 100 × 50 px de la barre de titre visibles. Null si la fenêtre serait hors de tout écran
    /// (écran débranché…).
    /// </summary>
    public WindowPlacement? FitIn((double Left, double Top, double Width, double Height) screen, double minWidth, double minHeight)
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top) || !double.IsFinite(Width) || !double.IsFinite(Height))
        {
            return null;
        }

        var width = Math.Min(Math.Max(Width, minWidth), Math.Max(screen.Width, minWidth));
        var height = Math.Min(Math.Max(Height, minHeight), Math.Max(screen.Height, minHeight));
        bool visible = Left + width - 100 >= screen.Left && Left + 100 <= screen.Left + screen.Width
                       && Top >= screen.Top - 10 && Top + 50 <= screen.Top + screen.Height;
        return visible ? this with { Width = width, Height = height } : null;
    }
}

/// <summary>Couleurs de l'interface (le contraste élevé de Windows l'emporte toujours).</summary>
public enum AppTheme
{
    /// <summary>Comme Windows : « Choisir votre mode d'application par défaut » (clair ou sombre).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Connexion lancée récemment, affichée sur l'écran d'accueil.</summary>
public sealed class RecentSession
{
    public string AccountId { get; set; } = "";

    /// <summary>PVWA du compte : un même ID désigne d'autres comptes sur un autre PVWA.</summary>
    public string PvwaHost { get; set; } = "";

    public bool IsForHost(string pvwaHost) => string.Equals(PvwaHost, pvwaHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Libellé affiché, par ex. « adm-t0@srv01.corp.local ».</summary>
    public string Label { get; set; } = "";

    /// <summary>Valeur de <see cref="Mode"/> pour une connexion SSH via PSMP.</summary>
    public const string SshMode = "SSH";

    /// <summary>Valeur de <see cref="Mode"/> pour des fichiers seuls (SFTP) via PSMP.</summary>
    public const string SftpMode = "SFTP";

    /// <summary>Composant PSM utilisé, ou « SSH » / « SFTP » pour une connexion via PSMP.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Machine cible choisie pour un compte de domaine.</summary>
    public string? RemoteMachine { get; set; }

    public DateTime When { get; set; }

    /// <summary>Nom lu par les lecteurs d'écran dans la liste des connexions récentes.</summary>
    public override string ToString() => $"{Label}, {Mode}";
}
