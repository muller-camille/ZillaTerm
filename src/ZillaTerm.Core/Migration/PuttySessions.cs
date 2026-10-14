namespace ZillaTerm.Core.Migration;

/// <summary>
/// Sessions PuTTY et KiTTY : registre (HKCU\Software\SimonTatham\PuTTY\Sessions, HKCU\Software\9bis.com\KiTTY\Sessions,
/// lu directement ou exporté en .reg), dossier « Sessions » de KiTTY portable (un fichier par session, une ligne
/// « nom\valeur\ » par réglage, sous-dossiers = dossiers) ou dossier « sessions » de PuTTY sous Linux et les autres
/// Unix (~/.putty/sessions : un fichier par session, nom encodé en %XX, une ligne « nom=valeur » par réglage). Seuls le serveur, le port, le protocole, l'utilisateur et le
/// dossier sont lus. Un « / » ou un « \ » dans le nom d'une session la range dans des dossiers.
/// </summary>
public static class PuttySessions
{
    public const string PuttyKey = @"Software\SimonTatham\PuTTY\Sessions";

    public const string KittyKey = @"Software\9bis.com\KiTTY\Sessions";

    /// <summary>Valeurs lues ; tout le reste (mots de passe de KiTTY compris) est ignoré.</summary>
    public static readonly IReadOnlyList<string> ValueNames = ["HostName", "PortNumber", "Protocol", "UserName", "Folder"];

    private const string DefaultSettings = "Default Settings";

    public static List<ImportedSession> FromRegistry(IEnumerable<RegKey> keys)
    {
        var list = keys.ToList();
        var sessions = new List<ImportedSession>();
        foreach (var root in new[] { PuttyKey, KittyKey })
        {
            foreach (var (name, key) in RegKey.ChildrenOf(list, root))
            {
                if (FromValues("", name, key.GetString, key.GetInt) is { } session)
                {
                    sessions.Add(session);
                }
            }
        }

        return sessions;
    }

    /// <summary>Dossier « Sessions » de KiTTY portable (ou le dossier de KiTTY qui le contient).</summary>
    public static List<ImportedSession> FromKittyFolder(string directory)
    {
        var root = Path.Combine(directory, "Sessions");
        root = Directory.Exists(root) ? root : directory;
        var sessions = new List<ImportedSession>();
        foreach (var (folder, path) in ImportText.Files(root, "*"))
        {
            var text = ImportText.ReadSessionFile(path);
            if (text is null)
            {
                continue;
            }

            var values = ParseKittyFile(text);
            var decodedFolder = string.Join('/', folder.Split('/').Select(ImportText.Unescape));
            if (FromValues(decodedFolder, Path.GetFileName(path), n => values.GetValueOrDefault(n), n => ImportText.Int(values.GetValueOrDefault(n)))
                is { } session)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    /// <summary>Dossier « sessions » de PuTTY sous Unix (~/.putty/sessions), ou le dossier ~/.putty qui le contient.</summary>
    public static List<ImportedSession> FromUnixFolder(string directory)
    {
        var root = Path.Combine(directory, "sessions");
        root = Directory.Exists(root) ? root : directory;
        var sessions = new List<ImportedSession>();
        foreach (var (_, path) in ImportText.Files(root, "*"))
        {
            var text = ImportText.ReadSessionFile(path);
            if (text is null)
            {
                continue;
            }

            var values = ParseUnixFile(text);
            if (FromValues("", Path.GetFileName(path), n => values.GetValueOrDefault(n), n => ImportText.Int(values.GetValueOrDefault(n)))
                is { } session)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    /// <summary>Réglages utiles d'un fichier de session PuTTY sous Unix (« HostName=srv01 »), valeurs telles quelles.</summary>
    internal static Dictionary<string, string> ParseUnixFile(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int sep = line.IndexOf('=');
            if (sep <= 0)
            {
                continue;
            }

            var name = line[..sep];
            if (ValueNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                values[name] = line[(sep + 1)..];
            }
        }

        return values;
    }

    /// <summary>Réglages utiles d'un fichier de session KiTTY (« HostName\srv01\ »), valeurs décodées.</summary>
    internal static Dictionary<string, string> ParseKittyFile(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int sep = line.IndexOf('\\');
            if (sep <= 0)
            {
                continue;
            }

            var name = line[..sep];
            if (!ValueNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(sep + 1)..];
            values[name] = ImportText.Unescape(value.EndsWith('\\') ? value[..^1] : value);
        }

        return values;
    }

    private static ImportedSession? FromValues(string parentFolder, string encodedName, Func<string, string?> text, Func<string, int?> number)
    {
        var name = ImportText.Unescape(encodedName).Trim();
        var host = text("HostName")?.Trim() ?? "";
        if (name.Length == 0 || name.Equals(DefaultSettings, StringComparison.OrdinalIgnoreCase) || host.Length == 0)
        {
            return null;
        }

        // Dossier : celui de KiTTY (réglage « Folder »), puis le chemin contenu dans le nom.
        var folder = parentFolder;
        if (text("Folder")?.Trim() is { Length: > 0 } kittyFolder && !kittyFolder.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            folder = SessionFolders.Combine(folder, kittyFolder);
        }

        folder = SessionFolders.Combine(folder, SessionFolders.Parent(name));
        name = SessionFolders.Name(name);
        var port = ImportText.Port(number("PortNumber"));
        var protocol = (text("Protocol") ?? "ssh").Trim();
        return protocol.ToLowerInvariant() switch
        {
            "ssh" or "" => ImportedSession.Terminal(folder, name, ImportProtocol.Ssh, host, port, text("UserName")),
            "telnet" => ImportedSession.Terminal(folder, name, ImportProtocol.Telnet, host, port, text("UserName")),
            _ => ImportedSession.Unsupported(folder, name, protocol, host, port),
        };
    }
}
