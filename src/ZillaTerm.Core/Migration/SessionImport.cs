using System.Globalization;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Migration;

/// <summary>État d'une session importée, avant puis après l'import.</summary>
public enum ImportState
{
    /// <summary>Un compte du PVWA correspond.</summary>
    Ready,

    /// <summary>
    /// Plusieurs comptes correspondent (le plus probable est choisi), ou un seul, d'un autre domaine que le serveur : à
    /// vérifier.
    /// </summary>
    Check,

    /// <summary>Aucun compte du PVWA : non importé (la session demanderait un mot de passe, hors CyberArk).</summary>
    NoAccount,

    /// <summary>Type de connexion non repris (VNC, FTP, port série...).</summary>
    Unsupported,

    /// <summary>Le même compte est déjà dans ce dossier de « Mes serveurs ».</summary>
    AlreadyPresent,

    /// <summary>Décoché par l'utilisateur.</summary>
    Excluded,

    Imported,
}

/// <summary>Session lue dans l'autre logiciel, le compte du PVWA retenu et la connexion qui sera enregistrée.</summary>
public sealed class ImportItem
{
    internal ImportItem(ImportedSession session, IReadOnlyList<ImportCandidate> candidates)
    {
        Session = session;
        Candidates = candidates;
        Chosen = candidates.FirstOrDefault();
        // Plusieurs comptes possibles, ou un compte d'un autre domaine que le serveur : ce n'est qu'une proposition, la
        // session n'est cochée qu'une fois le compte choisi (ou cochée à la main).
        Include = candidates.Count <= 1 && Chosen?.OtherDomain != true;
    }

    public ImportedSession Session { get; }

    /// <summary>Comptes possibles, du plus probable au moins probable.</summary>
    public IReadOnlyList<ImportCandidate> Candidates { get; }

    public ImportCandidate? Chosen { get; internal set; }

    /// <summary>Coché : à importer s'il est prêt (ou à vérifier, une fois le compte choisi).</summary>
    public bool Include { get; set; }

    public ImportState State { get; internal set; }

    /// <summary>Connexion enregistrée : PSM, ou SSH / SFTP via le PSMP.</summary>
    public ConnectMode Mode { get; internal set; }

    /// <summary>Composant PSM imposé ; null : celui de la plateforme.</summary>
    public string? Component { get; internal set; }

    /// <summary>Dossier de « Mes serveurs » où la session est (ou serait) rangée.</summary>
    public string TargetFolder { get; internal set; } = "";

    /// <summary>Dossier où se trouve déjà ce compte (<see cref="ImportState.AlreadyPresent"/>).</summary>
    public string? ExistingFolder { get; internal set; }

    /// <summary>Serveur ajouté à « Mes serveurs ».</summary>
    public SavedSession? Created { get; internal set; }

    /// <summary>
    /// Étiquette du serveur importé (PROD, QA, DEV…) : devinée d'après le nom, le serveur, le compte et les dossiers,
    /// ou choisie (<see cref="SessionImport.SetTag"/>) ; null = aucune.
    /// </summary>
    public string? Tag { get; internal set; }

    /// <summary>Étiquette choisie à la main : elle n'est plus devinée à nouveau quand le compte change.</summary>
    internal bool TagChosen { get; set; }

    /// <summary>Connexion affichée : « PSM », « PSM (PSM-WinSCP) », « SSH », « SFTP ».</summary>
    public string ConnectionText => State is ImportState.NoAccount or ImportState.Unsupported ? ""
        : Component is { } component ? $"{SessionLibrary.ModeName(Mode)} ({component})" : SessionLibrary.ModeName(Mode);

    /// <summary>Résultat affiché et exporté.</summary>
    public string StateText => State switch
    {
        ImportState.Ready => CoreStrings.MigrationReady,
        ImportState.Check => Candidates.Count == 1 ? CoreStrings.MigrationCheckOtherDomain
            : string.Format(CultureInfo.CurrentCulture, CoreStrings.MigrationCheck, Candidates.Count),
        ImportState.NoAccount => string.Format(CultureInfo.CurrentCulture, CoreStrings.MigrationNoAccount,
            Session.User is null ? Session.Host : $"{Session.DisplayUser}@{Session.Host}"),
        ImportState.Unsupported => string.Format(CultureInfo.CurrentCulture, CoreStrings.MigrationUnsupported, Session.ProtocolName),
        ImportState.AlreadyPresent => ExistingFolder is { Length: > 0 } folder
            ? string.Format(CultureInfo.CurrentCulture, CoreStrings.MigrationAlreadyPresent, folder)
            : CoreStrings.MigrationAlreadyPresentRoot,
        ImportState.Excluded => CoreStrings.MigrationExcluded,
        _ => CoreStrings.MigrationImported,
    };

    /// <summary>Protocole affiché : « SSH », « RDP (PSM) », « SSH (PSMP) », « VNC ».</summary>
    public string ProtocolText => Session.Protocol switch
    {
        ImportProtocol.Other => Session.ProtocolName ?? "",
        var p => (p switch { ImportProtocol.Sftp => "SFTP", ImportProtocol.Rdp => "RDP", ImportProtocol.Telnet => "Telnet", _ => "SSH" })
                 + (Session.ViaPsm ? p == ImportProtocol.Rdp ? " (PSM)" : " (PSMP)" : ""),
    };

    /// <summary>Importé, ou importable tant que l'import n'a pas eu lieu.</summary>
    public bool CanImport => State is ImportState.Ready or ImportState.Check;

    /// <summary>Serveur affiché, avec son port s'il n'est pas celui par défaut du protocole (22, 3389, 23).</summary>
    public string ServerText => Session.Port is { } port && port != DefaultPort(Session.Protocol)
        ? $"{Session.Host}:{port.ToString(CultureInfo.InvariantCulture)}"
        : Session.Host;

    private static int DefaultPort(ImportProtocol protocol) => protocol switch
    {
        ImportProtocol.Rdp => 3389,
        ImportProtocol.Telnet => 23,
        ImportProtocol.Other => 0,
        _ => 22,
    };
}

/// <summary>
/// Import dans « Mes serveurs » de sessions lues dans un autre logiciel : pour chacune, le compte du PVWA qui lui
/// correspond, la connexion par PSM ou par le PSMP (jamais directe), et le même rangement en dossiers, sous un dossier
/// commun. Une session sans compte dans le PVWA n'est pas importée. Le résultat de chaque session s'exporte en CSV.
/// </summary>
public sealed class SessionImport
{
    public const string PsmWinScp = "PSM-WinSCP";
    public const string PsmTelnet = "PSM-Telnet";

    private readonly AppSettings _settings;
    private readonly string _pvwaHost;
    private string _rootFolder = "";
    // Serveurs de « Mes serveurs » par compte : un serveur déjà présent se cherche parmi ceux de son compte seulement
    // (des milliers de sessions importées face à des milliers de serveurs enregistrés).
    private Dictionary<string, List<SavedSession>> _savedByAccount = new(StringComparer.Ordinal);

    public SessionImport(AppSettings settings, string pvwaHost, SessionMatcher matcher, IEnumerable<ImportedSession> sessions, string rootFolder = "")
    {
        _settings = settings;
        _pvwaHost = pvwaHost;
        Items = sessions.Select(s => new ImportItem(s, matcher.Candidates(s))).ToList();
        foreach (var item in Items)
        {
            item.Tag = GuessTag(item);
        }

        RootFolder = rootFolder;
    }

    public IReadOnlyList<ImportItem> Items { get; }

    /// <summary>Dossier de « Mes serveurs » sous lequel l'arborescence de l'autre logiciel est recréée (vide : racine).</summary>
    public string RootFolder
    {
        get => _rootFolder;
        set
        {
            _rootFolder = SessionFolders.Normalize(value);
            Refresh();
        }
    }

    /// <summary>L'import a eu lieu : les états sont définitifs.</summary>
    public bool Applied { get; private set; }

    public int Count(ImportState state) => Items.Count(i => i.State == state);

    /// <summary>Choisit un autre compte parmi ceux proposés.</summary>
    public void Choose(ImportItem item, ImportCandidate candidate)
    {
        if (Applied || !item.Candidates.Contains(candidate))
        {
            return;
        }

        item.Chosen = candidate;
        item.Include = true;
        if (!item.TagChosen)
        {
            // Un autre compte (autre safe, autre machine) peut désigner un autre environnement.
            item.Tag = GuessTag(item);
        }

        IndexSaved();
        Refresh(item);
    }

    /// <summary>Choisit l'étiquette d'une session (null : aucune) ; elle ne sera plus devinée.</summary>
    public void SetTag(ImportItem item, string? tag)
    {
        if (Applied)
        {
            return;
        }

        item.Tag = ServerTagRules.NormalizeName(tag);
        item.TagChosen = true;
    }

    /// <summary>Étiquette d'après la session de l'autre logiciel et le compte retenu (son safe, sa machine cible).</summary>
    private string? GuessTag(ImportItem item) => ServerTagGuess.ForServer(_settings.ServerTags, item.Session.Name,
        item.Chosen?.RemoteMachine, item.Session.Host, item.Chosen?.Account.SafeName, item.Session.Folder)?.Name;

    /// <summary>Ajoute à « Mes serveurs » les sessions prêtes et cochées ; renvoie le nombre de serveurs ajoutés.</summary>
    public int Apply()
    {
        if (Applied)
        {
            return 0;
        }

        Refresh();
        int added = 0;
        foreach (var item in Items.Where(i => i.CanImport))
        {
            if (!item.Include)
            {
                item.State = ImportState.Excluded;
                continue;
            }

            // Doublon dans l'import même (même compte, même machine, même dossier) : ajouté une seule fois.
            if (Existing(item) is { } existing)
            {
                item.ExistingFolder = existing.Folder;
                item.State = ImportState.AlreadyPresent;
                continue;
            }

            var chosen = item.Chosen!;
            var session = SessionLibrary.AddConnection(_settings, chosen.Account, _pvwaHost, item.TargetFolder, item.Mode, item.Component,
                chosen.RemoteMachine);
            if (item.Session.Name.Length > 0)
            {
                session.Name = item.Session.Name;
            }

            session.Tag = item.Tag;
            item.Created = session;
            item.State = ImportState.Imported;
            Index(session);
            added++;
        }

        Applied = true;
        return added;
    }

    /// <summary>Résultat de chaque session en CSV (aucun mot de passe : il n'y en a jamais eu).</summary>
    public void WriteCsv(TextWriter writer, char separator)
    {
        CsvExporter.WriteLine(writer, separator,
        [
            CoreStrings.MigrationColFolder, CoreStrings.ColumnName, CoreStrings.MigrationColProtocol, CoreStrings.ColumnServer,
            CoreStrings.ColumnUser, CoreStrings.MigrationColAccount, CoreStrings.ColumnSafe, CoreStrings.MigrationColConnection,
            CoreStrings.MigrationColTarget, CoreStrings.MigrationColTag, CoreStrings.MigrationColResult,
        ]);
        foreach (var item in Items)
        {
            var s = item.Session;
            bool placed = item.State is ImportState.Ready or ImportState.Check or ImportState.Imported;
            var account = item.State is ImportState.NoAccount or ImportState.Unsupported ? null : item.Chosen;
            CsvExporter.WriteLine(writer, separator,
            [
                s.Folder,
                s.Name,
                item.ProtocolText,
                item.ServerText,
                s.DisplayUser,
                account?.Display ?? "",
                account?.Account.SafeName ?? "",
                item.ConnectionText,
                placed ? item.TargetFolder : "",
                placed ? item.Tag ?? "" : "",
                item.StateText,
            ]);
        }
    }

    /// <summary>Plateformes standard de CyberArk proposées dans le fichier des comptes manquants (à vérifier).</summary>
    public const string WindowsLocalPlatform = "WinServerLocal";
    public const string WindowsDomainPlatform = "WinDomain";
    public const string UnixPlatform = "UnixSSH";

    /// <summary>Sessions non importées faute de compte dans le PVWA.</summary>
    public int MissingAccounts => Count(ImportState.NoAccount);

    /// <summary>
    /// Fichier d'import de comptes (format de « Importer des comptes (CSV) ») pour les sessions sans compte dans le PVWA,
    /// à compléter (safe, plateforme) puis à importer pour créer les comptes dans un safe. Un compte local par serveur et
    /// utilisateur ; un compte de domaine par domaine et utilisateur, avec ses serveurs pour machines autorisées. Les
    /// colonnes safe et mot de passe restent vides (le safe peut être choisi à l'import) ; la plateforme proposée est
    /// une plateforme standard. Renvoie le nombre de comptes écrits.
    /// </summary>
    public int WriteMissingAccounts(TextWriter writer, char separator)
    {
        var accounts = new List<(string Address, string User, string Domain, string Platform, List<string> Machines)>();
        foreach (var item in Items.Where(i => i.State == ImportState.NoAccount))
        {
            var s = item.Session;
            var user = s.User ?? "";
            var shortHost = PsmpRouting.NormalizeHost(s.Host).Split('.')[0];
            bool domainAccount = s.Domain is { } d && d != "." && !string.Equals(PsmpRouting.NormalizeHost(d), shortHost, StringComparison.Ordinal);
            if (domainAccount)
            {
                var existing = accounts.FindIndex(a => a.Platform == WindowsDomainPlatform
                                                       && string.Equals(a.Address, s.Domain, StringComparison.OrdinalIgnoreCase)
                                                       && string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase));
                if (existing < 0)
                {
                    accounts.Add((s.Domain!, user, s.Domain!, WindowsDomainPlatform, [s.Host]));
                }
                else if (!accounts[existing].Machines.Contains(s.Host, StringComparer.OrdinalIgnoreCase))
                {
                    accounts[existing].Machines.Add(s.Host);
                }

                continue;
            }

            var platform = s.Protocol switch
            {
                ImportProtocol.Rdp => WindowsLocalPlatform,
                ImportProtocol.Ssh or ImportProtocol.Sftp => UnixPlatform,
                _ => "",
            };
            if (!accounts.Any(a => a.Machines.Count == 0 && string.Equals(a.Address, s.Host, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase)))
            {
                accounts.Add((s.Host, user, "", platform, []));
            }
        }

        CsvExporter.WriteLine(writer, separator, [.. AccountCsv.TemplateColumns]);
        foreach (var (address, user, domain, platform, machines) in accounts)
        {
            CsvExporter.WriteLine(writer, separator, ["", platform, address, user, "", domain, "", string.Join(';', machines), "", ""]);
        }

        return accounts.Count;
    }

    private void Refresh()
    {
        if (Applied)
        {
            return;
        }

        IndexSaved();
        foreach (var item in Items)
        {
            Refresh(item);
        }
    }

    private void IndexSaved()
    {
        _savedByAccount = new Dictionary<string, List<SavedSession>>(StringComparer.Ordinal);
        foreach (var session in _settings.Sessions)
        {
            Index(session);
        }
    }

    private void Index(SavedSession session)
    {
        if (!_savedByAccount.TryGetValue(session.AccountId, out var list))
        {
            _savedByAccount[session.AccountId] = list = [];
        }

        list.Add(session);
    }

    private void Refresh(ImportItem item)
    {
        item.TargetFolder = SessionFolders.Combine(_rootFolder, item.Session.Folder);
        item.ExistingFolder = null;
        if (item.Session.Protocol == ImportProtocol.Other || item.Session.Host.Length == 0)
        {
            item.State = ImportState.Unsupported;
            return;
        }

        if (item.Chosen is not { } chosen)
        {
            item.State = ImportState.NoAccount;
            return;
        }

        (item.Mode, item.Component) = Connection(item.Session, chosen);
        if (Existing(item) is { } existing)
        {
            item.ExistingFolder = existing.Folder;
            item.State = ImportState.AlreadyPresent;
            return;
        }

        item.State = item.Candidates.Count > 1 || chosen.OtherDomain ? ImportState.Check : ImportState.Ready;
    }

    /// <summary>
    /// Bureau à distance : PSM (composant de la session PSM d'origine, sinon celui de la plateforme). SSH et SFTP : via le
    /// PSMP s'il y en a un pour ce serveur (fichiers seuls pour un compte de plateforme « SFTP », comme au double-clic),
    /// sinon PSM (PSM-WinSCP pour les fichiers). Telnet : PSM-Telnet.
    /// </summary>
    private (ConnectMode, string?) Connection(ImportedSession session, ImportCandidate candidate)
    {
        bool psmp = PsmpRouting.Resolve(_settings, candidate.RemoteMachine ?? candidate.Account.Address) is not null;
        return session.Protocol switch
        {
            ImportProtocol.Ssh when psmp && AccountClassifier.DefaultMode(candidate.Account, hasPsmp: true) == ConnectMode.Sftp
                => (ConnectMode.Sftp, null),
            ImportProtocol.Ssh when psmp => (ConnectMode.Ssh, null),
            ImportProtocol.Sftp when psmp => (ConnectMode.Sftp, null),
            ImportProtocol.Sftp => (ConnectMode.Psm, PsmWinScp),
            ImportProtocol.Telnet => (ConnectMode.Psm, PsmTelnet),
            ImportProtocol.Rdp => (ConnectMode.Psm, string.IsNullOrWhiteSpace(session.Component) ? null : session.Component.Trim()),
            _ => (ConnectMode.Psm, null),
        };
    }

    /// <summary>Même serveur de « Mes serveurs » (voir <see cref="SessionLibrary.IsSameServer"/>), dans le dossier visé.</summary>
    private SavedSession? Existing(ImportItem item) =>
        item.Chosen is not { } chosen || !_savedByAccount.TryGetValue(chosen.Account.Id, out var saved)
            ? null
            : saved.FirstOrDefault(s => SessionLibrary.IsSameServer(s, chosen.Account.Id, _pvwaHost, chosen.RemoteMachine)
                                        && string.Equals(SessionFolders.Normalize(s.Folder), item.TargetFolder,
                                            StringComparison.OrdinalIgnoreCase));
}
