using ZillaTerm.App.Localization;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App;

/// <summary>Coffre KeePass dans « Mes serveurs » (accès d'urgence hors CyberArk).</summary>
public sealed class KeePassFolderNode(KeePassFolder folder, KeePassDatabase? database, List<object> children, bool isExpanded)
{
    public KeePassFolder Folder { get; } = folder;

    public string Name => Folder.DisplayName;

    public bool IsUnlocked { get; } = database is not null;

    public List<object> Children { get; } = children;

    public bool IsExpanded { get; set; } = isExpanded;

    public string StateText => IsUnlocked ? $" ({database!.Entries.Count})" : "  " + Strings.KeePassLockedSuffix;

    /// <summary>Nom lu par les lecteurs d'écran.</summary>
    public override string ToString() => Name + StateText;

    public double Opacity => IsUnlocked ? 1 : 0.7;

    public string Details
    {
        get
        {
            var lines = new List<string> { Strings.KeePassFolderTip, Folder.FilePath };
            if (!string.IsNullOrEmpty(Folder.KeyFilePath))
            {
                lines.Add(Text.Format(Strings.KeePassKeyFileInfo, Folder.KeyFilePath));
            }

            if (database is not null)
            {
                lines.Add(database.FormatName);
            }

            return string.Join("\n", lines);
        }
    }
}

/// <summary>Dossier (groupe) à l'intérieur d'un coffre KeePass.</summary>
public sealed class KeePassGroupNode(KeePassFolder folder, string path, List<object> children, bool isExpanded)
{
    public KeePassFolder Folder { get; } = folder;

    /// <summary>Chemin dans le coffre, « Serveurs/Prod ».</summary>
    public string Path { get; } = path;

    public string Name => KeePassGroupPath.Name(Path);

    public override string ToString() => Name;

    public List<object> Children { get; } = children;

    public int Count { get; init; }

    public bool IsExpanded { get; set; } = isExpanded;
}

/// <summary>Entrée d'un coffre KeePass : un serveur joignable en SSH ou en RDP.</summary>
public sealed class KeePassEntryNode(KeePassFolder folder, KeePassEntry entry)
{
    public KeePassFolder Folder { get; } = folder;

    public KeePassEntry Entry { get; } = entry;

    public KeePassTarget Target { get; } = KeePassTarget.From(entry);

    public string Title => Entry.Title.Length > 0 ? Entry.Title : Target.Host;

    public string ModeText => KeePassTarget.Name(Target.Protocol);

    public override string ToString() => $"{Title}, {ModeText}";

    public bool IsExpanded { get; set; }

    public string Details
    {
        get
        {
            var lines = new List<string>
            {
                Target.Host.Length > 0 ? $"{Target.UserName}@{Target.Address}" : Strings.KeePassNoHost,
                Text.Format(Strings.KeePassEntryOrigin, Folder.DisplayName),
            };
            if (Entry.Notes.Length > 0)
            {
                var first = Entry.Notes.Split('\n')[0].Trim();
                lines.Add(first.Length > 80 ? first[..80] + "…" : first);
            }

            return string.Join("\n", lines);
        }
    }
}

/// <summary>Ligne affichée sous un coffre verrouillé : double-clic pour le déverrouiller.</summary>
public sealed class KeePassHintNode(KeePassFolder folder, string text)
{
    public KeePassFolder Folder { get; } = folder;

    public string Text { get; } = text;

    public override string ToString() => Text;

    public bool IsExpanded { get; set; }
}
