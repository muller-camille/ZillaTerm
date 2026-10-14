using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App;

/// <summary>Dossier de l'arbre des sessions (un safe, une plateforme...).</summary>
public sealed class FolderNode(string name, IReadOnlyList<AccountNode> children, bool isExpanded)
{
    public string Name { get; } = name;

    public IReadOnlyList<AccountNode> Children { get; } = children;

    public int Count => Children.Count;

    public bool IsExpanded { get; set; } = isExpanded;

    /// <summary>Nom lu par les lecteurs d'écran (les éléments d'arbre liés à des objets utilisent ToString).</summary>
    public override string ToString() => $"{Name} ({Count})";
}

/// <summary>
/// Compte affiché dans l'arbre des sessions. <paramref name="distinction"/> le distingue des comptes du même nom dans
/// son dossier (voir <see cref="AccountGrouping.Distinctions"/>) ; pendant une recherche, <paramref name="query"/> et
/// <paramref name="folder"/> (nom du dossier, déjà visible) font dire quel champ caché correspond.
/// </summary>
public sealed class AccountNode(PvwaAccount account, string? distinction = null, string? query = null, string? folder = null)
{
    public PvwaAccount Account { get; } = account;

    public string Title => $"{Account.UserName}@{Account.Address}";

    /// <summary>
    /// Texte discret après le nom : ce qui distingue le compte d'un autre du même nom, et pourquoi il correspond à la
    /// recherche quand le nom ne le montre pas (« correspond : machine srv01 »). Vide sinon.
    /// </summary>
    public string Hint { get; } = HintFor(account, distinction, query, folder);

    /// <summary>La dernière opération du CPM (changement, vérification, réconciliation) a échoué.</summary>
    public bool CpmFailed => Account.SecretManagement?.Failed == true;

    /// <summary>
    /// Nom lu par les lecteurs d'écran : compte, plateforme, safe, ce qui le distingue ou fait correspondre la recherche,
    /// et l'échec du CPM (signalé à l'écran par un triangle).
    /// </summary>
    public override string ToString() =>
        $"{Title}, {Account.PlatformId}, {Account.SafeName}" + (Hint.Length > 0 ? ", " + Hint : "") + (CpmFailed ? ", " + Strings.A11yCpmFailed : "");

    private static string HintFor(PvwaAccount account, string? distinction, string? query, string? folder)
    {
        var shown = $"{account.UserName}@{account.Address} {distinction} {folder}";
        var matches = AccountFilter.HiddenMatches(account, query, shown);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(distinction))
        {
            parts.Add(distinction);
        }

        if (matches.Count > 0)
        {
            parts.Add(Text.Format(Strings.MatchReason, string.Join(", ", matches)));
        }

        return string.Join(" · ", parts);
    }

    public string Details
    {
        get
        {
            var lines = new List<string> { $"{Account.PlatformId} · {Account.SafeName}" };
            if (!string.IsNullOrWhiteSpace(Account.RemoteMachines))
            {
                lines.Add(Text.Format(Strings.AccountMachines, Account.RemoteMachines));
            }

            lines.AddRange(CpmText.Describe(Account.SecretManagement));
            return string.Join("\n", lines);
        }
    }
}

/// <summary>État du mot de passe vu par le CPM, pour les info-bulles.</summary>
internal static class CpmText
{
    public static IEnumerable<string> Describe(SecretManagement? management)
    {
        if (management is null)
        {
            yield break;
        }

        yield return management.AutomaticManagementEnabled ? Strings.CpmManagedAuto
            : string.IsNullOrWhiteSpace(management.ManualManagementReason) ? Strings.CpmManagedManual
            : Text.Format(Strings.CpmManagedManualReason, management.ManualManagementReason);
        if (management.Failed)
        {
            yield return Strings.CpmLastFailed;
        }

        if (management.LastModified is { } changed)
        {
            yield return Text.Format(Strings.CpmLastChanged, changed.ToString("g", CultureInfo.CurrentCulture));
        }

        if (management.LastVerified is { } verified)
        {
            yield return Text.Format(Strings.CpmLastVerified, verified.ToString("g", CultureInfo.CurrentCulture));
        }

        if (management.LastReconciled is { } reconciled)
        {
            yield return Text.Format(Strings.CpmLastReconciled, reconciled.ToString("g", CultureInfo.CurrentCulture));
        }
    }
}

/// <summary>Icône selon le type de cible (compte, nœud de l'arbre, type ou session récente).</summary>
public sealed class KindIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            AccountNode n => IconFor(AccountClassifier.Classify(n.Account)),
            PvwaAccount a => IconFor(AccountClassifier.Classify(a)),
            AccountKind k => IconFor(k),
            RecentSession r => r.Mode == RecentModes.Ssh ? "IconSsh" : r.Mode == RecentModes.Sftp ? "IconFiles" : "IconConnect",
            SavedSessionNode n => n.Account is { } a ? IconFor(AccountClassifier.Classify(a)) : IconFor(KindOf(n.Session)),
            SavedSession s => IconFor(KindOf(s)),
            SharedServerNode n => n.Account is { } a ? IconFor(AccountClassifier.Classify(a)) : IconFor(KindOf(n.Session)),
            KeePassFolderNode => "IconKeePass",
            KeePassHintNode => "IconPermissions",
            KeePassEntryNode e => e.Target.Protocol switch
            {
                RemoteProtocol.Ssh => "IconUnix",
                RemoteProtocol.Rdp => "IconWindows",
                RemoteProtocol.Vnc => "IconConnect",
                RemoteProtocol.Sftp or RemoteProtocol.Ftp or RemoteProtocol.Ftpes or RemoteProtocol.Ftps => "IconFiles",
                _ => "IconOther",
            },
            _ => "IconOther",
        };
        return Icons.Find(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static AccountKind KindOf(SavedSession s) => AccountClassifier.Classify(new PvwaAccount { PlatformId = s.PlatformId });

    private static string IconFor(AccountKind kind) => kind switch
    {
        AccountKind.Windows => "IconWindows",
        AccountKind.Unix => "IconUnix",
        AccountKind.Database => "IconDatabase",
        AccountKind.Network => "IconNetwork",
        _ => "IconOther",
    };
}

internal static class RecentModes
{
    public const string Ssh = RecentSession.SshMode;
    public const string Sftp = RecentSession.SftpMode;
}

/// <summary>Dossier de « Mes serveurs ».</summary>
public sealed class SavedFolderNode(string path, List<object> children, bool isExpanded)
{
    public string Path { get; } = path;

    public string Name => SessionFolders.Name(Path);

    public List<object> Children { get; } = children;

    public int Count { get; init; }

    public bool IsExpanded { get; set; } = isExpanded;

    public override string ToString() => $"{Name} ({Count})";
}

/// <summary>
/// Serveur de « Mes serveurs » ; <see cref="Account"/> est null si le compte n'est plus visible dans CyberArk, ou tant que
/// la liste des comptes n'est pas chargée (<paramref name="accountsKnown"/> faux : rien n'est alors signalé).
/// </summary>
public sealed class SavedSessionNode(SavedSession session, PvwaAccount? account, bool accountsKnown = true) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isMarked;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public SavedSession Session { get; } = session;

    /// <summary>Choisi avec Ctrl+clic ou Maj+clic, pour ouvrir plusieurs serveurs ensemble.</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_isMarked != value)
            {
                _isMarked = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsMarked)));
            }
        }
    }

    public PvwaAccount? Account { get; } = account;

    public string Title => Session.Name;

    public string ModeText => Session.Mode == ConnectMode.Psm ? Session.Component ?? "PSM" : SessionLibrary.ModeName(Session.Mode);

    /// <summary>Étiquette d'environnement (PROD, QA, DEV…) avec sa couleur ; null : aucune.</summary>
    public ServerTag? Tag { get; init; }

    public bool HasTag => Tag is not null;

    public Avalonia.Media.IBrush? TagBackground => Tag is { } tag ? ServerTagView.Background(tag) : null;

    public Avalonia.Media.IBrush? TagForeground => Tag is { } tag ? ServerTagView.Foreground(tag) : null;

    /// <summary>Compte introuvable dans CyberArk : « ⚠ introuvable dans CyberArk » après le nom, en clair (pas d'opacité).</summary>
    public bool IsMissing => accountsKnown && Account is null;

    public bool IsExpanded { get; set; }

    public override string ToString() =>
        $"{Title}, {ModeText}" + (Tag is { } tag ? ", " + tag.Name : "") + (IsMarked ? ", " + Strings.A11yMarked : "")
        + (IsMissing ? ", " + Strings.MissingInCyberArk : "");

    public string Details
    {
        get
        {
            var lines = new List<string> { $"{Session.UserName}@{Session.Address}", $"{Session.PlatformId} · {Session.SafeName}" };
            if (!string.IsNullOrWhiteSpace(Session.RemoteMachine))
            {
                lines.Add(Text.Format(Strings.SavedTargetMachine, Session.RemoteMachine));
            }

            if (!string.IsNullOrWhiteSpace(Session.StartDirectory))
            {
                lines.Add(Text.Format(Strings.SavedSftpFolder, Session.StartDirectory));
            }

            if (IsMissing)
            {
                lines.Add(Strings.SavedAccountMissing);
            }
            else if (Account is not null)
            {
                lines.AddRange(CpmText.Describe(Account.SecretManagement));
            }

            return string.Join("\n", lines);
        }
    }
}

/// <summary>Icône d'un fichier distant : dossier, lien, fichier, « .. ».</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Icons.Find(value switch
        {
            RemoteEntry { IsParentLink: true } => "IconParentFolder",
            RemoteEntry { IsDirectory: true } => "IconFolder",
            RemoteEntry { IsSymbolicLink: true } => "IconFileLink",
            _ => "IconFile",
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Date d'une connexion récente : « Aujourd'hui 09:28 », « Hier 18:02 », sinon la date courte et l'heure (format des
/// réglages régionaux de Windows, comme le reste de l'application).
/// </summary>
public sealed class RecentDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime when ? Describe(when, DateTime.Now) : "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string Describe(DateTime when, DateTime now)
    {
        var time = when.ToString("t", CultureInfo.CurrentCulture);
        return (now.Date - when.Date).Days switch
        {
            0 => Text.Format(Strings.RecentToday, time),
            1 => Text.Format(Strings.RecentYesterday, time),
            _ => $"{when.ToString("d", CultureInfo.CurrentCulture)} {time}",
        };
    }
}

/// <summary>
/// Texte traduit mis en forme avec la valeur liée (ConverterParameter : le texte), comme StringFormat mais avec l'accord
/// des mots avec un nombre (« {0:# ligne|# lignes} »).
/// </summary>
public sealed class TextFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string format ? Text.Format(format, value) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
