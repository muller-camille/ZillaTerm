using System.Globalization;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App;

/// <summary>Liste de serveurs partagée, en tête de « Mes serveurs » (après les coffres KeePass).</summary>
public sealed class SharedListNode(SharedServerList list, ServerListFile? content, List<object> children, bool isExpanded, string pvwaHost)
{
    public SharedServerList List { get; } = list;

    public string Name => List.Name;

    public List<object> Children { get; } = children;

    public bool IsExpanded { get; set; } = isExpanded;

    public bool IsReadable => content is not null;

    public string StateText => content is not null ? $" ({content.Servers.Count})"
        : "  " + (List.Error is null ? Strings.SharedListLoadingSuffix : Strings.SharedListUnreadable);

    /// <summary>Nom lu par les lecteurs d'écran.</summary>
    public override string ToString() => Name + StateText;

    public double Opacity => List.Error is null ? 1 : 0.7;

    public string Details
    {
        get
        {
            var lines = new List<string> { Strings.SharedListTip, List.Path };
            if (content is not null)
            {
                var last = content.Changes.LastOrDefault();
                lines.Add(last is null
                    ? Text.Format(Strings.SharedListRevision, content.Revision)
                    : Text.Format(Strings.SharedListLastChange, content.Revision, SharedText.Date(last.At), last.By));
                if (content.Pvwa.Length > 0 && !string.Equals(content.Pvwa, pvwaHost, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(Text.Format(Strings.SharedListOtherPvwa, content.Pvwa));
                }
            }

            if (List.Error is { } error)
            {
                lines.Add(error);
            }

            return string.Join("\n", lines);
        }
    }
}

/// <summary>Dossier d'une liste partagée.</summary>
public sealed class SharedFolderNode(SharedServerList list, string path, List<object> children, bool isExpanded)
{
    public SharedServerList List { get; } = list;

    public string Path { get; } = path;

    public string Name => SessionFolders.Name(Path);

    public override string ToString() => Name;

    public List<object> Children { get; } = children;

    public int Count { get; init; }

    public bool IsExpanded { get; set; } = isExpanded;
}

/// <summary>
/// Serveur d'une liste partagée. <see cref="Session"/> en est la copie utilisée pour se connecter (jamais enregistrée
/// dans les préférences) ; <see cref="Account"/> est null si le compte n'est pas visible dans CyberArk, ou tant que la
/// liste des comptes n'est pas chargée (<paramref name="accountsKnown"/> faux : rien n'est alors signalé).
/// </summary>
public sealed class SharedServerNode(SharedServerList list, ServerEntry entry, SavedSession session, PvwaAccount? account, bool accountsKnown = true)
{
    public SharedServerList List { get; } = list;

    public ServerEntry Entry { get; } = entry;

    public SavedSession Session { get; } = session;

    public PvwaAccount? Account { get; } = account;

    public string Title => Session.Name;

    public string ModeText => Session.Mode == ConnectMode.Psm ? Session.Component ?? "PSM" : SessionLibrary.ModeName(Session.Mode);

    /// <summary>Étiquette d'environnement (PROD, QA, DEV…) avec sa couleur ; null : aucune.</summary>
    public ServerTag? Tag { get; init; }

    public bool HasTag => Tag is not null;

    public Avalonia.Media.IBrush? TagBackground => Tag is { } tag ? ServerTagView.Background(tag) : null;

    public Avalonia.Media.IBrush? TagForeground => Tag is { } tag ? ServerTagView.Foreground(tag) : null;

    public override string ToString() =>
        $"{Title}, {ModeText}" + (Tag is { } tag ? ", " + tag.Name : "") + (IsMissing ? ", " + Strings.MissingInCyberArk : "");

    /// <summary>Compte introuvable dans CyberArk : « ⚠ introuvable dans CyberArk » après le nom, en clair (pas d'opacité).</summary>
    public bool IsMissing => accountsKnown && Account is null;

    public bool IsExpanded { get; set; }

    /// <summary>
    /// Info-bulle : le compte tel que CyberArk le décrit (pas tel que la liste le nomme), la machine cible et le dossier
    /// SFTP venus de la liste, qui l'a ajouté.
    /// </summary>
    public string Details
    {
        get
        {
            var lines = Account is { } a
                ? new List<string> { $"{a.UserName}@{a.Address}", $"{a.PlatformId} · {a.SafeName}" }
                : new List<string> { $"{Session.UserName}@{Session.Address}", $"{Session.PlatformId} · {Session.SafeName}" };
            if (!string.IsNullOrWhiteSpace(Session.RemoteMachine))
            {
                lines.Add(Text.Format(Strings.SavedTargetMachine, Session.RemoteMachine));
            }

            if (!string.IsNullOrWhiteSpace(Session.StartDirectory))
            {
                lines.Add(Text.Format(Strings.SavedSftpFolder, Session.StartDirectory));
            }

            if (!string.IsNullOrWhiteSpace(Entry.AddedBy))
            {
                lines.Add(Entry.AddedAt is { } at
                    ? Text.Format(Strings.SharedAddedByOn, Entry.AddedBy, SharedText.Date(at))
                    : Text.Format(Strings.SharedAddedBy, Entry.AddedBy));
            }

            lines.Add(IsMissing ? Strings.SavedAccountMissing : Text.Format(Strings.SharedServerInList, List.Name));
            return string.Join("\n", lines);
        }
    }
}

internal static class SharedText
{
    /// <summary>Date UTC du fichier, en heure locale.</summary>
    public static string Date(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Action(SharedChange change) => change.Action switch
    {
        SharedAction.Created => Strings.SharedActionCreated,
        SharedAction.Added => Strings.SharedActionAdded,
        SharedAction.Removed => Strings.SharedActionRemoved,
        SharedAction.Restored => Text.Format(Strings.SharedActionRestored, change.Detail ?? ""),
        _ => change.Action.ToString(),
    };
}
