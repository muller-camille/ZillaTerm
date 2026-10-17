using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core;

/// <summary>Réglage qu'un fichier d'environnement change.</summary>
public enum EnvironmentSetting
{
    PvwaUrl,
    AuthMethod,
    DefaultPsmp,
    PsmpServers,
    WindowsComponent,
    PlatformComponent,
    SharedList,
    HostKey,
    CentralFile,
    KeepPvwaSessionAlive,
    SshInApp,
    CheckForUpdates,
    UploadProtocol,
    DutyText,
    DutyTextFile,
    InterventionReminders,
    SessionSharing,
    ShareDirect,
    SharePort,
    ShareRelayFolder,
    ServerTags,
    PsmDesktopInTab,
    PsmRedirectDrives,
    PsmShareFoldersOnly,
    PsmSharedFolders,
    PauseRdpDisplay,
    KeepAccountListCopy,
}

/// <summary>
/// Changement qu'appliquerait un fichier d'environnement. <see cref="Sensitive"/> : adresse qui recevra le mot de passe
/// CyberArk ou clé de serveur, à vérifier ; <see cref="Ignored"/> : non appliqué (clé différente de celle déjà acceptée).
/// </summary>
/// <param name="Ignored">Non appliqué : clé déjà acceptée et différente, ou clé d'un serveur qui n'est pas un PSMP.</param>
/// <param name="NotPsmp">Clé ignorée parce que le serveur n'est pas un PSMP (ni des réglages, ni du fichier).</param>
public sealed record EnvironmentChange(EnvironmentSetting Setting, string? Detail, string Current, string New, bool Sensitive,
    bool Ignored = false, bool NotPsmp = false);

/// <summary>Raison du refus d'un fichier d'environnement.</summary>
public enum EnvironmentProblem
{
    TooLarge,
    InvalidJson,
    UnsupportedFormat,
    InvalidPvwa,
    InvalidPsmpAddress,
    InvalidPort,
    InvalidDomain,
    DuplicateDomain,
    InvalidComponent,
    InvalidHostKey,
    InvalidPath,
    InvalidDutyText,
    InvalidServerTag,
    InvalidReminder,
    InvalidSharedFolder,
}

/// <summary>Fichier d'environnement refusé ; <see cref="Detail"/> nomme la valeur en cause.</summary>
public sealed class EnvironmentFileException(EnvironmentProblem problem, string detail)
    : Exception($"{problem}: {detail}")
{
    public EnvironmentProblem Problem { get; } = problem;

    public string Detail { get; } = detail;
}

/// <summary>Fichier d'environnement lu : son contenu et l'empreinte SHA-256 de ses octets.</summary>
public sealed record EnvironmentFile(EnvironmentProfile Profile, string Sha256);

/// <summary>
/// Environnement partagé (<c>ZillaTerm.env.json</c>) : réglages communs à une équipe pour un coffre CyberArk (PVWA,
/// PSMP, composants PSM, listes partagées, clés des PSMP, quelques options). Jamais de secret ni de donnée personnelle
/// (identifiant, « Mes serveurs », sessions récentes, coffre local) : un fichier qui en contiendrait n'en apporte pas,
/// seuls les champs ci-dessous sont lus. Un champ absent ne change rien.
/// </summary>
public sealed class EnvironmentProfile
{
    /// <summary>Nom du fichier lu à côté de l'exécutable.</summary>
    public const string FileName = "ZillaTerm.env.json";

    /// <summary>Nom du fichier sous l'ancien nom de l'application, encore lu à côté de l'exécutable.</summary>
    public const string LegacyFileName = AppSettings.LegacyName + ".env.json";

    public const int CurrentFormat = 1;

    private const long MaxFileSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ExactEnumConverter() },
    };

    public int Format { get; set; } = CurrentFormat;

    /// <summary>Nom affiché (« Production »…).</summary>
    public string? Name { get; set; }

    public string? PvwaUrl { get; set; }

    public AuthMethod? AuthMethod { get; set; }

    public string? PsmpAddress { get; set; }

    public int? PsmpPort { get; set; }

    public List<PsmpServer>? PsmpServers { get; set; }

    public string? WindowsComponent { get; set; }

    public Dictionary<string, string>? ComponentByPlatform { get; set; }

    /// <summary>Listes partagées à ouvrir (ajoutées, jamais retirées).</summary>
    public List<string>? SharedLists { get; set; }

    /// <summary>Clés des PSMP (« hôte:port » → « type SHA256:empreinte ») : ajoutées, jamais à la place d'une clé déjà acceptée.</summary>
    public Dictionary<string, string>? HostKeys { get; set; }

    /// <summary>Fichier d'environnement central (partage réseau), relu à chaque démarrage.</summary>
    public string? CentralFile { get; set; }

    public bool? KeepPvwaSessionAlive { get; set; }

    /// <summary>Copie chiffrée de la liste des comptes, affichée aussitôt à la connexion suivante.</summary>
    public bool? KeepAccountListCopy { get; set; }

    public bool? SshInApp { get; set; }

    /// <summary>Sessions PSM Windows (bureau) dans un onglet de ZillaTerm plutôt que dans Connexion Bureau à distance.</summary>
    public bool? PsmDesktopInTab { get; set; }

    /// <summary>Disques du poste partagés avec les sessions PSM Windows.</summary>
    public bool? PsmRedirectDrives { get; set; }

    /// <summary>« Ces dossiers seulement » à la place de tous les disques (voir <see cref="AppSettings.PsmShareFoldersOnly"/>).</summary>
    public bool? PsmShareFoldersOnly { get; set; }

    /// <summary>Dossiers partagés avec « Ces dossiers seulement », à la place de ceux du poste ; vide pour n'en garder aucun.</summary>
    public List<Rdp.SharedFolder>? PsmSharedFolders { get; set; }

    /// <summary>Affichage des sessions Bureau à distance en pause quand personne ne les regarde.</summary>
    public bool? PauseRdpDisplay { get; set; }

    public bool? CheckForUpdates { get; set; }

    public TransferProtocol? UploadProtocol { get; set; }

    /// <summary>
    /// Consignes d'astreinte (bonnes pratiques, numéros d'urgence…), affichées dans la fenêtre « Intervention » ; vide pour
    /// l'effacer. Texte brut : aucun lien ni mise en forme n'est interprété.
    /// </summary>
    public string? DutyText { get; set; }

    /// <summary>
    /// Fichier des consignes d'astreinte (texte brut, souvent sur un partage), relu à chaque ouverture de la fenêtre
    /// « Intervention » ; vide pour ne plus en avoir.
    /// </summary>
    public string? DutyTextFile { get; set; }

    /// <summary>
    /// Rappels pendant une intervention (minutes depuis le début de l'enregistrement et texte), à la place de ceux du poste ;
    /// vide pour n'en garder aucun.
    /// </summary>
    public List<Interventions.InterventionReminder>? InterventionReminders { get; set; }

    /// <summary>Partage de sessions avec un autre ZillaTerm permis (voir <see cref="AppSettings.SessionSharing"/>).</summary>
    public bool? SessionSharing { get; set; }

    /// <summary>Partages : connexions directes acceptées.</summary>
    public bool? ShareDirect { get; set; }

    /// <summary>Partages : port des connexions directes (0 : au hasard).</summary>
    public int? SharePort { get; set; }

    /// <summary>Partages : dossier relais (partage réseau de l'équipe) ; vide pour ne plus en avoir.</summary>
    public string? ShareRelayFolder { get; set; }

    /// <summary>Étiquettes des serveurs (nom et couleur), à la place de celles du poste ; vide pour n'en garder aucune.</summary>
    public List<ServerTag>? ServerTags { get; set; }

    /// <summary>Longueur maximale des consignes d'astreinte.</summary>
    public const int MaxDutyText = 8000;

    /// <summary>Environnement des réglages actuels, sans rien de personnel ; clés des PSMP configurés déjà acceptées.</summary>
    /// <summary>
    /// Réglages que ce fichier définit (les autres restent ceux de chacun) : les Paramètres les marquent « Équipe ».
    /// </summary>
    public IReadOnlySet<EnvironmentSetting> DefinedSettings()
    {
        var defined = new HashSet<EnvironmentSetting>();
        void If(bool present, EnvironmentSetting setting)
        {
            if (present)
            {
                defined.Add(setting);
            }
        }

        If(PvwaUrl is not null, EnvironmentSetting.PvwaUrl);
        If(AuthMethod is not null, EnvironmentSetting.AuthMethod);
        If(PsmpAddress is not null || PsmpPort is not null, EnvironmentSetting.DefaultPsmp);
        If(PsmpServers is not null, EnvironmentSetting.PsmpServers);
        If(WindowsComponent is not null, EnvironmentSetting.WindowsComponent);
        If(ComponentByPlatform is { Count: > 0 }, EnvironmentSetting.PlatformComponent);
        If(SharedLists is { Count: > 0 }, EnvironmentSetting.SharedList);
        If(HostKeys is { Count: > 0 }, EnvironmentSetting.HostKey);
        If(CentralFile is not null, EnvironmentSetting.CentralFile);
        If(KeepPvwaSessionAlive is not null, EnvironmentSetting.KeepPvwaSessionAlive);
        If(KeepAccountListCopy is not null, EnvironmentSetting.KeepAccountListCopy);
        If(SshInApp is not null, EnvironmentSetting.SshInApp);
        If(PsmDesktopInTab is not null, EnvironmentSetting.PsmDesktopInTab);
        If(PsmRedirectDrives is not null, EnvironmentSetting.PsmRedirectDrives);
        If(PsmShareFoldersOnly is not null, EnvironmentSetting.PsmShareFoldersOnly);
        If(PsmSharedFolders is not null, EnvironmentSetting.PsmSharedFolders);
        If(PauseRdpDisplay is not null, EnvironmentSetting.PauseRdpDisplay);
        If(CheckForUpdates is not null, EnvironmentSetting.CheckForUpdates);
        If(UploadProtocol is not null, EnvironmentSetting.UploadProtocol);
        If(DutyText is not null, EnvironmentSetting.DutyText);
        If(DutyTextFile is not null, EnvironmentSetting.DutyTextFile);
        If(InterventionReminders is not null, EnvironmentSetting.InterventionReminders);
        If(SessionSharing is not null, EnvironmentSetting.SessionSharing);
        If(ShareDirect is not null, EnvironmentSetting.ShareDirect);
        If(SharePort is not null, EnvironmentSetting.SharePort);
        If(ShareRelayFolder is not null, EnvironmentSetting.ShareRelayFolder);
        If(ServerTags is not null, EnvironmentSetting.ServerTags);
        return defined;
    }

    public static EnvironmentProfile FromSettings(AppSettings settings, string? name)
    {
        var psmps = settings.PsmpServers.Select(p => (Address: p.Address, Port: p.Port))
            .Prepend((Address: settings.PsmpAddress, Port: settings.PsmpPort))
            .Where(p => !string.IsNullOrWhiteSpace(p.Address));
        // Clé acceptée à la connexion, sinon celle reçue d'un fichier d'environnement.
        var hostKeys = psmps.Select(p => KnownHosts.Key(p.Address, p.Port))
            .Distinct()
            .Select(k => (Server: k, Key: settings.KnownHosts.GetValueOrDefault(k)
                                         ?? settings.KnownHosts.GetValueOrDefault(KnownHosts.EnvironmentPrefix + k)))
            .Where(k => k.Key is not null)
            .ToDictionary(k => k.Server, k => k.Key!);
        return new EnvironmentProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            PvwaUrl = NullIfEmpty(settings.PvwaUrl),
            AuthMethod = settings.AuthMethod,
            PsmpAddress = NullIfEmpty(settings.PsmpAddress),
            PsmpPort = string.IsNullOrWhiteSpace(settings.PsmpAddress) ? null : settings.PsmpPort,
            PsmpServers = settings.PsmpServers.Count == 0
                ? null
                : settings.PsmpServers.Select(p => new PsmpServer { Address = p.Address, Port = p.Port, Domain = p.Domain }).ToList(),
            WindowsComponent = NullIfEmpty(settings.WindowsComponent),
            ComponentByPlatform = settings.ComponentByPlatform.Count == 0 ? null : new Dictionary<string, string>(settings.ComponentByPlatform),
            SharedLists = settings.SharedLists.Count == 0 ? null : [.. settings.SharedLists],
            HostKeys = hostKeys.Count == 0 ? null : hostKeys,
            CentralFile = NullIfEmpty(settings.EnvironmentFile),
            KeepPvwaSessionAlive = settings.KeepPvwaSessionAlive,
            KeepAccountListCopy = settings.KeepAccountListCopy,
            SshInApp = settings.SshInApp,
            PsmDesktopInTab = settings.PsmDesktopInTab,
            PsmRedirectDrives = settings.PsmRedirectDrives,
            PsmShareFoldersOnly = settings.PsmShareFoldersOnly,
            PsmSharedFolders = settings.PsmSharedFolders.Count == 0 && !settings.PsmShareFoldersOnly ? null : [.. settings.PsmSharedFolders],
            PauseRdpDisplay = settings.PauseRdpDisplay,
            CheckForUpdates = settings.CheckForUpdates,
            UploadProtocol = settings.PreferredUploadProtocol,
            DutyText = string.IsNullOrWhiteSpace(settings.DutyText) ? null : NormalizeDutyText(settings.DutyText),
            DutyTextFile = NullIfEmpty(settings.DutyTextFile),
            InterventionReminders = settings.InterventionReminders.Count == 0 ? null : [.. settings.InterventionReminders],
            SessionSharing = settings.SessionSharing,
            ShareDirect = settings.ShareDirect,
            SharePort = settings.SharePort == 0 ? null : settings.SharePort,
            ShareRelayFolder = NullIfEmpty(settings.ShareRelayFolder),
            ServerTags = settings.ServerTags.Select(t => new ServerTag(t.Name, t.Color)).ToList(),
        };
    }

    /// <summary>Lit et vérifie un fichier ; <see cref="EnvironmentFileException"/> s'il est refusé.</summary>
    /// <exception cref="IOException">Fichier absent ou illisible (partage indisponible…).</exception>
    public static EnvironmentFile Load(string path)
    {
        if (new FileInfo(path).Length > MaxFileSize)
        {
            throw new EnvironmentFileException(EnvironmentProblem.TooLarge, path);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > MaxFileSize)
        {
            throw new EnvironmentFileException(EnvironmentProblem.TooLarge, path);
        }

        // Fichier modifié à la main : un BOM UTF-8 (PowerShell 5.1, « UTF-8 avec BOM ») est ignoré ; l'empreinte reste
        // celle du fichier tel quel.
        var json = bytes.AsSpan();
        if (json.StartsWith(System.Text.Encoding.UTF8.Preamble))
        {
            json = json[System.Text.Encoding.UTF8.Preamble.Length..];
        }

        EnvironmentProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<EnvironmentProfile>(json, JsonOptions)
                      ?? throw new EnvironmentFileException(EnvironmentProblem.InvalidJson, path);
        }
        catch (JsonException ex)
        {
            throw new EnvironmentFileException(EnvironmentProblem.InvalidJson, ex.Message);
        }

        profile.Validate();
        return new EnvironmentFile(profile, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Refuse un fichier dont une valeur serait inutilisable ou dangereuse (adresse en http, nom invalide…).</summary>
    public void Validate()
    {
        if (Format is < 1 or > CurrentFormat)
        {
            Fail(EnvironmentProblem.UnsupportedFormat, Format.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Envoi par le PSMP : SCP ou SFTP seulement (FTP est réservé aux entrées KeePass, après confirmation).
        if ((AuthMethod is { } method && !Enum.IsDefined(method)) || UploadProtocol is not (null or TransferProtocol.Scp or TransferProtocol.Sftp))
        {
            Fail(EnvironmentProblem.InvalidJson, $"{AuthMethod} {UploadProtocol}".Trim());
        }

        if (PvwaUrl is not null)
        {
            try
            {
                PvwaClient.NormalizeBaseUri(PvwaUrl);
            }
            catch (ArgumentException)
            {
                Fail(EnvironmentProblem.InvalidPvwa, PvwaUrl);
            }
        }

        if (!string.IsNullOrWhiteSpace(PsmpAddress) && Uri.CheckHostName(PsmpAddress.Trim()) == UriHostNameType.Unknown)
        {
            Fail(EnvironmentProblem.InvalidPsmpAddress, PsmpAddress);
        }

        CheckPort(PsmpPort);
        var domains = new PsmpDomainCheck(PsmpAddress);
        foreach (var psmp in PsmpServers ?? [])
        {
            if (psmp is null || Uri.CheckHostName((psmp.Address ?? "").Trim()) == UriHostNameType.Unknown)
            {
                Fail(EnvironmentProblem.InvalidPsmpAddress, psmp?.Address ?? "");
            }

            CheckPort(psmp!.Port);
            switch (domains.Check(psmp.Address, psmp.Domain, out var domain))
            {
                case PsmpDomainProblem.Missing or PsmpDomainProblem.Invalid:
                    Fail(EnvironmentProblem.InvalidDomain, psmp.Address ?? "");
                    break;
                case PsmpDomainProblem.Duplicate:
                    Fail(EnvironmentProblem.DuplicateDomain, domain);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(WindowsComponent) && !AppSettings.IsValidComponentName(WindowsComponent))
        {
            Fail(EnvironmentProblem.InvalidComponent, WindowsComponent);
        }

        foreach (var (platform, component) in ComponentByPlatform ?? [])
        {
            if (string.IsNullOrWhiteSpace(platform) || !AppSettings.IsValidComponentName(component))
            {
                Fail(EnvironmentProblem.InvalidComponent, $"{platform} : {component}");
            }
        }

        foreach (var (host, key) in HostKeys ?? [])
        {
            if (!IsHostKeyEntry(host, key))
            {
                Fail(EnvironmentProblem.InvalidHostKey, host);
            }
        }

        foreach (var path in CentralFile is null ? SharedLists ?? [] : (SharedLists ?? []).Append(CentralFile))
        {
            if (!IsFullPath(path))
            {
                Fail(EnvironmentProblem.InvalidPath, path ?? "");
            }
        }

        if (DutyText is not null && !IsValidDutyText(DutyText))
        {
            Fail(EnvironmentProblem.InvalidDutyText, DutyText.Length > 40 ? DutyText[..40] + "…" : DutyText);
        }

        if (!string.IsNullOrWhiteSpace(DutyTextFile) && !DutyReminderFile.IsValidPath(DutyTextFile))
        {
            Fail(EnvironmentProblem.InvalidPath, DutyTextFile);
        }

        if (SharePort is { } sharePort && !AppSettings.IsSharePort(sharePort))
        {
            Fail(EnvironmentProblem.InvalidPort, sharePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(ShareRelayFolder) && !Sharing.ShareInvitation.IsRelayFolder(ShareRelayFolder.Trim()))
        {
            Fail(EnvironmentProblem.InvalidPath, ShareRelayFolder);
        }

        if (InterventionReminders is not null)
        {
            if (InterventionReminders.Count > Interventions.InterventionReminder.MaxReminders)
            {
                Fail(EnvironmentProblem.InvalidReminder, InterventionReminders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var reminder in InterventionReminders)
            {
                if (!Interventions.InterventionReminder.IsValid(reminder))
                {
                    Fail(EnvironmentProblem.InvalidReminder, reminder is null ? ""
                        : $"{reminder.Minutes} {(reminder.Text?.Length > 40 ? reminder.Text[..40] + "…" : reminder.Text)}");
                }
            }
        }

        if (PsmSharedFolders is not null && Rdp.SharedFolderRules.Check(PsmSharedFolders) is { } folderProblem)
        {
            Fail(EnvironmentProblem.InvalidSharedFolder, folderProblem.Detail);
        }

        if (ServerTags is not null)
        {
            if (ServerTags.Count > ServerTagRules.MaxTags)
            {
                Fail(EnvironmentProblem.InvalidServerTag, ServerTags.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var tag in ServerTags)
            {
                if (tag is null || !ServerTagRules.IsValidName(tag.Name) || !ServerTagRules.IsValidColor(tag.Color)
                    || ServerTags.Count(t => t is not null && string.Equals(t.Name?.Trim(), tag.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
                {
                    Fail(EnvironmentProblem.InvalidServerTag, tag?.Name is { } name && name.Length > 40 ? name[..40] + "…" : tag?.Name ?? "");
                }
            }
        }
    }

    /// <summary>
    /// Consignes d'astreinte acceptables : <see cref="MaxDutyText"/> caractères au plus, sans caractère de contrôle (sauf
    /// sauts de ligne et tabulations) ni caractère invisible qui changerait le sens de lecture.
    /// </summary>
    public static bool IsValidDutyText(string text) =>
        text.Length <= MaxDutyText
        && text.EnumerateRunes().All(r => r.Value is '\r' or '\n' or '\t'
            || (!System.Text.Rune.IsControl(r) && System.Text.Rune.GetUnicodeCategory(r) != System.Globalization.UnicodeCategory.Format));

    /// <summary>Consignes d'astreinte telles qu'elles sont gardées : sauts de ligne « \n », sans espaces ni lignes vides à la fin.</summary>
    public static string NormalizeDutyText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

    /// <summary>Changements par rapport aux réglages actuels (rien de ce qui est déjà identique).</summary>
    public IReadOnlyList<EnvironmentChange> Diff(AppSettings settings)
    {
        var changes = new List<EnvironmentChange>();
        void Add(EnvironmentSetting setting, string? detail, string current, string next, bool sensitive)
        {
            if (!string.Equals(current, next, StringComparison.Ordinal))
            {
                changes.Add(new EnvironmentChange(setting, detail, current, next, sensitive));
            }
        }

        if (PvwaUrl is not null && !SamePvwa(settings.PvwaUrl, PvwaUrl))
        {
            changes.Add(new EnvironmentChange(EnvironmentSetting.PvwaUrl, null, settings.PvwaUrl, PvwaUrl.Trim(), true));
        }

        if (AuthMethod is { } method)
        {
            Add(EnvironmentSetting.AuthMethod, null, settings.AuthMethod.ToString(), method.ToString(), false);
        }

        if (PsmpAddress is not null || PsmpPort is not null)
        {
            Add(EnvironmentSetting.DefaultPsmp, null, Endpoint(settings.PsmpAddress, settings.PsmpPort),
                Endpoint(PsmpAddress ?? settings.PsmpAddress, PsmpPort ?? settings.PsmpPort), true);
        }

        if (PsmpServers is not null)
        {
            Add(EnvironmentSetting.PsmpServers, null, Describe(settings.PsmpServers), Describe(PsmpServers), true);
        }

        if (WindowsComponent is not null)
        {
            Add(EnvironmentSetting.WindowsComponent, null, settings.WindowsComponent.Trim(), WindowsComponent.Trim(), false);
        }

        foreach (var (platform, component) in ComponentByPlatform ?? [])
        {
            var current = settings.ComponentByPlatform
                .FirstOrDefault(kv => string.Equals(kv.Key, platform, StringComparison.OrdinalIgnoreCase)).Value ?? "";
            Add(EnvironmentSetting.PlatformComponent, platform.Trim(), current, component.Trim(), false);
        }

        foreach (var path in (SharedLists ?? []).Where(p => !settings.SharedLists.Contains(p.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            // Liste sur un partage réseau : à chaque démarrage, Windows s'y authentifie (empreinte du mot de passe
            // envoyée au serveur nommé). Signalée comme sensible, avec le serveur en clair.
            var server = NetworkServer(path);
            changes.Add(new EnvironmentChange(EnvironmentSetting.SharedList, server, "", path.Trim(), server is not null));
        }

        var psmps = PsmpEndpoints(settings);
        foreach (var (host, key) in HostKeys ?? [])
        {
            var id = host.Trim().ToLowerInvariant();
            var known = AcceptedKeys(settings, id).Select(k => KnownHosts.Format(k.Algorithm, k.Sha256)).ToList();
            if (known.Contains(key.Trim(), StringComparer.Ordinal))
            {
                // Clé déjà acceptée : rien à changer.
                continue;
            }

            if (!psmps.Contains(id))
            {
                // Seules les clés des PSMP viennent du fichier : celle d'un serveur d'accès d'urgence, acceptée d'avance,
                // supprimerait la vérification à la première connexion (où partent les mots de passe KeePass).
                changes.Add(new EnvironmentChange(EnvironmentSetting.HostKey, id, "", key.Trim(), true, Ignored: true, NotPsmp: true));
            }
            else if (known.Count == 0)
            {
                changes.Add(new EnvironmentChange(EnvironmentSetting.HostKey, id, "", key.Trim(), true));
            }
            else
            {
                // Un serveur dont une clé est déjà acceptée n'en reçoit jamais d'un fichier (ni à la place, ni d'un autre
                // type) : le changement se vérifie à la connexion.
                changes.Add(new EnvironmentChange(EnvironmentSetting.HostKey, id, string.Join(", ", known), key.Trim(), true, Ignored: true));
            }
        }

        if (CentralFile is not null)
        {
            Add(EnvironmentSetting.CentralFile, null, settings.EnvironmentFile.Trim(), CentralFile.Trim(), true);
        }

        AddFlag(EnvironmentSetting.KeepPvwaSessionAlive, settings.KeepPvwaSessionAlive, KeepPvwaSessionAlive);
        AddFlag(EnvironmentSetting.KeepAccountListCopy, settings.KeepAccountListCopy, KeepAccountListCopy);
        AddFlag(EnvironmentSetting.SshInApp, settings.SshInApp, SshInApp);
        AddFlag(EnvironmentSetting.PsmDesktopInTab, settings.PsmDesktopInTab, PsmDesktopInTab);
        AddFlag(EnvironmentSetting.PsmRedirectDrives, settings.PsmRedirectDrives, PsmRedirectDrives);
        AddFlag(EnvironmentSetting.PsmShareFoldersOnly, settings.PsmShareFoldersOnly, PsmShareFoldersOnly);
        if (PsmSharedFolders is not null)
        {
            // Dossier sur un partage réseau : Windows s'y authentifie à chaque session qui le partage ; signalé avec le
            // serveur (comme une liste partagée).
            var folders = Rdp.SharedFolderRules.Sanitize(PsmSharedFolders);
            var server = Rdp.SharedFolderRules.NetworkServer(folders);
            Add(EnvironmentSetting.PsmSharedFolders, server, Rdp.SharedFolderRules.Describe(settings.PsmSharedFolders),
                Rdp.SharedFolderRules.Describe(folders), server is not null);
        }

        AddFlag(EnvironmentSetting.PauseRdpDisplay, settings.PauseRdpDisplay, PauseRdpDisplay);
        AddFlag(EnvironmentSetting.CheckForUpdates, settings.CheckForUpdates, CheckForUpdates);
        if (UploadProtocol is { } protocol)
        {
            Add(EnvironmentSetting.UploadProtocol, null, settings.PreferredUploadProtocol.ToString().ToUpperInvariant(),
                protocol.ToString().ToUpperInvariant(), false);
        }

        if (DutyText is not null)
        {
            Add(EnvironmentSetting.DutyText, null, NormalizeDutyText(settings.DutyText), NormalizeDutyText(DutyText), false);
        }

        if (DutyTextFile is not null)
        {
            // Fichier sur un partage réseau : à chaque ouverture de la fenêtre Intervention, Windows s'y authentifie.
            // Signalé comme sensible, avec le serveur en clair (comme une liste partagée).
            var server = NetworkServer(DutyTextFile);
            Add(EnvironmentSetting.DutyTextFile, server, settings.DutyTextFile.Trim(), DutyTextFile.Trim(), server is not null);
        }

        AddFlag(EnvironmentSetting.SessionSharing, settings.SessionSharing, SessionSharing);
        AddFlag(EnvironmentSetting.ShareDirect, settings.ShareDirect, ShareDirect);
        if (SharePort is { } port)
        {
            Add(EnvironmentSetting.SharePort, null, settings.SharePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                port.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        }

        if (ShareRelayFolder is not null)
        {
            // Dossier sur un partage réseau : Windows s'y authentifie à chaque partage ; signalé avec le serveur.
            var server = NetworkServer(ShareRelayFolder);
            Add(EnvironmentSetting.ShareRelayFolder, server, settings.ShareRelayFolder.Trim(), ShareRelayFolder.Trim(), server is not null);
        }

        if (InterventionReminders is not null)
        {
            Add(EnvironmentSetting.InterventionReminders, null, Interventions.InterventionReminder.Format(settings.InterventionReminders),
                Interventions.InterventionReminder.Format(Interventions.InterventionReminder.Sanitize(InterventionReminders)), false);
        }

        if (ServerTags is not null)
        {
            Add(EnvironmentSetting.ServerTags, null, Describe(settings.ServerTags), Describe(ServerTagRules.Sanitize(ServerTags)), false);
        }

        return changes;

        void AddFlag(EnvironmentSetting setting, bool current, bool? next)
        {
            if (next is { } value)
            {
                Add(setting, null, current ? "true" : "false", value ? "true" : "false", false);
            }
        }
    }

    /// <summary>Applique l'environnement (validé) ; une clé de serveur déjà acceptée et différente reste inchangée.</summary>
    public void ApplyTo(AppSettings settings)
    {
        if (PvwaUrl is not null)
        {
            settings.PvwaUrl = PvwaUrl.Trim();
        }

        if (AuthMethod is { } method)
        {
            settings.AuthMethod = method;
        }

        if (PsmpAddress is not null)
        {
            settings.PsmpAddress = PsmpAddress.Trim();
        }

        if (PsmpPort is { } port)
        {
            settings.PsmpPort = port;
        }

        if (PsmpServers is not null)
        {
            settings.PsmpServers = PsmpServers
                .Select(p => new PsmpServer { Address = p.Address.Trim(), Port = p.Port, Domain = (p.Domain ?? "").Trim() })
                .ToList();
        }

        if (WindowsComponent is not null)
        {
            settings.WindowsComponent = WindowsComponent.Trim();
        }

        foreach (var (platform, component) in ComponentByPlatform ?? [])
        {
            settings.RememberComponent(platform.Trim(), component.Trim());
        }

        foreach (var path in (SharedLists ?? []).Select(p => p.Trim()))
        {
            if (!settings.SharedLists.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                settings.SharedLists.Add(path);
            }
        }

        var psmps = PsmpEndpoints(settings);
        foreach (var (host, key) in HostKeys ?? [])
        {
            var id = host.Trim().ToLowerInvariant();
            if (psmps.Contains(id) && AcceptedKeys(settings, id).Count == 0)
            {
                // Rangée à part : valable pour les connexions au PSMP seulement (voir KnownHosts.EnvironmentPrefix).
                settings.KnownHosts[KnownHosts.EnvironmentPrefix + id] = key.Trim();
            }
        }

        if (CentralFile is not null)
        {
            settings.EnvironmentFile = CentralFile.Trim();
        }

        settings.KeepPvwaSessionAlive = KeepPvwaSessionAlive ?? settings.KeepPvwaSessionAlive;
        settings.KeepAccountListCopy = KeepAccountListCopy ?? settings.KeepAccountListCopy;
        settings.SshInApp = SshInApp ?? settings.SshInApp;
        settings.PsmDesktopInTab = PsmDesktopInTab ?? settings.PsmDesktopInTab;
        settings.PsmRedirectDrives = PsmRedirectDrives ?? settings.PsmRedirectDrives;
        settings.PsmShareFoldersOnly = PsmShareFoldersOnly ?? settings.PsmShareFoldersOnly;
        if (PsmSharedFolders is not null)
        {
            settings.PsmSharedFolders = Rdp.SharedFolderRules.Sanitize(PsmSharedFolders);
        }
        settings.PauseRdpDisplay = PauseRdpDisplay ?? settings.PauseRdpDisplay;
        settings.CheckForUpdates = CheckForUpdates ?? settings.CheckForUpdates;
        settings.PreferredUploadProtocol = UploadProtocol ?? settings.PreferredUploadProtocol;
        if (DutyText is not null)
        {
            settings.DutyText = NormalizeDutyText(DutyText);
        }

        if (DutyTextFile is not null)
        {
            settings.DutyTextFile = DutyTextFile.Trim();
        }

        if (InterventionReminders is not null)
        {
            settings.InterventionReminders = Interventions.InterventionReminder.Sanitize(InterventionReminders);
        }

        settings.SessionSharing = SessionSharing ?? settings.SessionSharing;
        settings.ShareDirect = ShareDirect ?? settings.ShareDirect;
        settings.SharePort = SharePort ?? settings.SharePort;
        if (ShareRelayFolder is not null)
        {
            settings.ShareRelayFolder = ShareRelayFolder.Trim();
        }

        if (ServerTags is not null)
        {
            // Les serveurs gardent leur étiquette, même absente de la nouvelle liste (affichée alors en gris).
            settings.ServerTags = ServerTagRules.Sanitize(ServerTags);
        }
    }

    /// <summary>Clés de ce serveur (« hôte:port ») acceptées à la connexion ou reçues d'un fichier d'environnement.</summary>
    private static List<(string Algorithm, string Sha256)> AcceptedKeys(AppSettings settings, string server) =>
        [.. KnownHosts.KnownKeys(settings.KnownHosts, server), .. KnownHosts.KnownKeys(settings.KnownHosts, KnownHosts.EnvironmentPrefix + server)];

    /// <summary>
    /// « hôte:port » de chaque PSMP : ceux des réglages et ceux de ce fichier (qui seront appliqués avec lui). Seules
    /// leurs clés peuvent venir d'un fichier d'environnement, et elles ne valent que pour les connexions au PSMP.
    /// </summary>
    private HashSet<string> PsmpEndpoints(AppSettings settings)
    {
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? address, int port)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                endpoints.Add(KnownHosts.Key(address, port));
            }
        }

        Add(settings.PsmpAddress, settings.PsmpPort);
        Add(PsmpAddress ?? settings.PsmpAddress, PsmpPort ?? settings.PsmpPort);
        foreach (var psmp in settings.PsmpServers.Concat(PsmpServers ?? []))
        {
            Add(psmp.Address, psmp.Port);
        }

        return endpoints;
    }

    /// <summary>Serveur d'un chemin réseau (<c>\\serveur\partage\…</c>) ; null pour un chemin local (<c>C:\…</c>).</summary>
    public static string? NetworkServer(string? path)
    {
        var p = (path ?? "").Trim();
        if (!p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = p[2..];
        int end = rest.IndexOfAny(['\\', '/']);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Clé de fichier : « hôte:port » → « type SHA256:empreinte » (empreinte en base64).</summary>
    private static bool IsHostKeyEntry(string host, string key)
    {
        int colon = (host ?? "").LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(host![(colon + 1)..], out var port) || port is < 1 or > 65535
            || Uri.CheckHostName(host[..colon]) == UriHostNameType.Unknown)
        {
            return false;
        }

        var parts = (key ?? "").Trim().Split(' ');
        return parts.Length == 2 && parts[0].Length is > 0 and <= 64 && parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@')
               && parts[1].StartsWith("SHA256:", StringComparison.Ordinal) && parts[1].Length > 7
               && parts[1][7..].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');
    }

    /// <summary>Chemin complet (« C:\… » ou partage UNC « \\serveur\… ») : jamais relatif au dossier ou au lecteur courant.</summary>
    public static bool IsFullPath(string? path)
    {
        var p = (path ?? "").Trim();
        return p.IndexOfAny(Path.GetInvalidPathChars()) < 0
               && ((p.Length > 2 && p.StartsWith(@"\\", StringComparison.Ordinal) && p[2] is not ('\\' or '/'))
                   || (p.Length > 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is ('\\' or '/')));
    }

    private static bool SamePvwa(string current, string next)
    {
        try
        {
            return PvwaClient.NormalizeBaseUri(current) == PvwaClient.NormalizeBaseUri(next);
        }
        catch (ArgumentException)
        {
            return string.Equals(current.Trim(), next.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Endpoint(string address, int port) =>
        string.IsNullOrWhiteSpace(address) ? "" : $"{address.Trim()}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string Describe(IEnumerable<ServerTag> tags) => string.Join(", ", tags.Select(t => $"{t.Name} ({t.Color})"));

    private static string Describe(IEnumerable<PsmpServer> servers) => string.Join(", ", servers.Select(p =>
    {
        var domain = PsmpRouting.NormalizeDomain(p.Domain);
        return Endpoint(p.Address, p.Port) + (domain.Length > 0 && domain != PsmpRouting.DomainOf(p.Address) ? $" ({domain})" : "");
    }));

    private static void CheckPort(int? port)
    {
        if (port is { } value && value is < 1 or > 65535)
        {
            Fail(EnvironmentProblem.InvalidPort, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void Fail(EnvironmentProblem problem, string detail) => throw new EnvironmentFileException(problem, detail);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Énumérations par leur nom exact (casse ignorée) : ni nombre ni combinaison « LDAP, RADIUS ».</summary>
    private sealed class ExactEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private sealed class Converter<T> : JsonConverter<T>
            where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, text?.Trim(), StringComparison.OrdinalIgnoreCase));
                return name is null ? throw new JsonException($"{typeof(T).Name} : {text}") : Enum.Parse<T>(name);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.ToString());
        }
    }
}
