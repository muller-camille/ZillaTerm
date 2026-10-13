using System.Globalization;
using System.Text;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core;

/// <summary>Version enregistrée d'une liste partagée : copie de la révision <paramref name="Revision"/>, écrite le <paramref name="Saved"/> (UTC).</summary>
public sealed record SharedVersion(int Revision, DateTime Saved, string Path);

/// <summary>
/// Liste de serveurs partagée : fichier JSON (sans secret) sur un partage réseau, que chacun peut compléter ou
/// élaguer. Chaque modification se fait fichier ouvert en exclusivité (les autres attendent leur tour, les
/// modifications de chacun se cumulent), garde une copie de la version précédente dans le dossier
/// « nom.versions » à côté du fichier, et s'inscrit au journal de la liste (qui, quand, quoi). Les droits d'écriture
/// sont ceux du partage réseau.
/// </summary>
public sealed class SharedServerList(string path)
{
    /// <summary>Versions gardées (les plus anciennes sont supprimées).</summary>
    internal static int MaxVersions { get; set; } = 100;

    /// <summary>Essais quand un autre poste est en train d'écrire la liste.</summary>
    internal static int LockAttempts { get; set; } = 20;

    internal static TimeSpan LockDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Dernier contenu lu (null si jamais lu ou illisible).</summary>
    public ServerListFile? Content { get; private set; }

    /// <summary>Erreur de la dernière lecture.</summary>
    public string? Error { get; private set; }

    /// <summary>Nom affiché : celui de la liste, sinon le nom du fichier.</summary>
    public string Name => Content?.Name is { Length: > 0 } name ? name : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Dossier des versions précédentes, à côté du fichier.</summary>
    public string VersionsDirectory => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileNameWithoutExtension(Path) + ".versions");

    /// <summary>Auteur inscrit au journal : compte CyberArk et compte Windows (déclaratif : le partage fait foi).</summary>
    public static string Who(string? cyberArkUser)
    {
        var windows = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return string.IsNullOrWhiteSpace(cyberArkUser) ? windows : $"{cyberArkUser} ({windows})";
    }

    /// <summary>Crée une liste vide (le fichier ne doit pas exister).</summary>
    public static SharedServerList Create(string path, string name, string pvwaHost, string who)
    {
        var file = new ServerListFile
        {
            Format = ServerListFile.SharedFormat,
            Name = name,
            Pvwa = pvwaHost,
            Saved = DateTime.UtcNow,
            Revision = 1,
            Changes = [new SharedChange { Revision = 1, At = DateTime.UtcNow, By = who, Action = SharedAction.Created, Detail = name }],
        };
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(file.ToJson()));
        }

        var list = new SharedServerList(path);
        list.Content = file;
        return list;
    }

    /// <summary>Relit le fichier ; en cas d'échec, <see cref="Error"/> le dit et le contenu précédent reste.</summary>
    public bool Load()
    {
        try
        {
            var bytes = ReadShared();
            Content = Parse(bytes);
            Error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                                  or System.Security.SecurityException)
        {
            Error = e.Message;
            return false;
        }
    }

    /// <summary>Ajoute des serveurs (ceux déjà présents sont ignorés) ; renvoie le nombre ajouté.</summary>
    public int Add(IEnumerable<ServerEntry> servers, string who)
    {
        var toAdd = servers.ToList();
        int added = 0;
        Update(file =>
        {
            added = 0;
            var changes = new List<SharedChange>();
            var keys = file.Servers.Select(s => s.Key()).ToHashSet(StringComparer.Ordinal);
            foreach (var server in toAdd)
            {
                if (!keys.Add(server.Key()))
                {
                    continue;
                }

                // Le motif reste personnel ; le reste décrit la connexion.
                var entry = Copy(server);
                entry.Reason = null;
                entry.AddedBy = who;
                entry.AddedAt = DateTime.UtcNow;
                file.Servers.Add(entry);
                changes.Add(new SharedChange { Action = SharedAction.Added, Server = entry.Name, Folder = NullIfEmpty(entry.Folder) });
                added++;
            }

            return changes;
        }, who);
        return added;
    }

    /// <summary>Retire des serveurs (par identifiant) ; renvoie le nombre retiré (déjà retirés par un autre : ignorés).</summary>
    public int Remove(IEnumerable<string> ids, string who)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        int removed = 0;
        Update(file =>
        {
            var gone = file.Servers.Where(s => wanted.Contains(s.Id)).ToList();
            file.Servers.RemoveAll(s => wanted.Contains(s.Id));
            removed = gone.Count;
            return gone.Select(s => new SharedChange { Action = SharedAction.Removed, Server = s.Name, Folder = NullIfEmpty(s.Folder) }).ToList();
        }, who);
        return removed;
    }

    /// <summary>Remet les serveurs d'une version enregistrée (la restauration est elle-même une nouvelle révision).</summary>
    public void Restore(SharedVersion version, string who)
    {
        var old = Parse(File.ReadAllBytes(version.Path));
        Update(file =>
        {
            file.Servers = old.Servers;
            file.Folders = old.Folders;
            return [new SharedChange { Action = SharedAction.Restored, Detail = old.Revision.ToString(CultureInfo.InvariantCulture) }];
        }, who, whenUnreadable: () =>
        {
            // Liste illisible (écriture interrompue par une coupure réseau) : la restauration repart de la version choisie,
            // numérotée après la plus récente des versions gardées.
            var restored = Parse(File.ReadAllBytes(version.Path));
            restored.Revision = Math.Max(restored.Revision, Versions().Select(v => v.Revision).DefaultIfEmpty(0).Max());
            return restored;
        });
    }

    /// <summary>Versions enregistrées, la plus récente d'abord.</summary>
    public List<SharedVersion> Versions()
    {
        var directory = VersionsDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
        var versions = new List<SharedVersion>();
        foreach (var file in Directory.EnumerateFiles(directory, stem + ".r*.json"))
        {
            var parts = System.IO.Path.GetFileNameWithoutExtension(file)[(stem.Length + 2)..].Split('.');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                && DateTime.TryParseExact(parts[1], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var saved))
            {
                versions.Add(new SharedVersion(revision, saved, file));
            }
        }

        return versions.OrderByDescending(v => v.Revision).ThenByDescending(v => v.Saved).ToList();
    }

    /// <summary>
    /// Modification : fichier ouvert en exclusivité (on attend qu'un autre poste ait fini), relu, copié comme version,
    /// modifié par <paramref name="change"/> (qui renvoie les entrées du journal), réécrit. Rien n'est écrit si
    /// <paramref name="change"/> ne renvoie aucune modification.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Pas le droit d'écrire (droits du partage).</exception>
    /// <exception cref="IOException">Fichier inaccessible, ou occupé trop longtemps par un autre poste.</exception>
    /// <param name="whenUnreadable">Contenu de départ si le fichier est illisible (restauration seulement) ; sinon l'erreur remonte.</param>
    private void Update(Func<ServerListFile, List<SharedChange>> change, string who, Func<ServerListFile>? whenUnreadable = null)
    {
        using var stream = OpenExclusive();
        var bytes = ReadAll(stream);
        ServerListFile file;
        bool readable = true;
        try
        {
            file = Parse(bytes);
        }
        catch (InvalidDataException) when (whenUnreadable is not null)
        {
            file = whenUnreadable();
            readable = false;
        }

        if (!file.IsShared)
        {
            throw new InvalidDataException(CoreStrings.ServerListNotShared);
        }

        var changes = change(file);
        if (changes.Count == 0)
        {
            Content = file;
            Error = null;
            return;
        }

        if (readable)
        {
            SaveVersion(file.Revision, file.Saved, bytes);
        }

        file.Revision++;
        file.Saved = DateTime.UtcNow;
        foreach (var entry in changes)
        {
            entry.Revision = file.Revision;
            entry.At = file.Saved;
            entry.By = who;
        }

        file.Changes.AddRange(changes);
        if (file.Changes.Count > ServerListFile.MaxChanges)
        {
            file.Changes.RemoveRange(0, file.Changes.Count - ServerListFile.MaxChanges);
        }

        var json = Encoding.UTF8.GetBytes(file.ToJson());
        try
        {
            Rewrite(stream, json);
        }
        catch (IOException)
        {
            // Écriture interrompue (partage plein, réseau coupé) : remettre la liste telle qu'elle était.
            try
            {
                Rewrite(stream, bytes);
            }
            catch (IOException e)
            {
                Diagnostics.DebugLog.Write("shared", $"{Path} non remise en état après un échec d'écriture : {e.Message}");
            }

            throw;
        }

        Content = file;
        Error = null;
        PruneVersions();
    }

    private static void Rewrite(FileStream stream, byte[] bytes)
    {
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private FileStream OpenExclusive()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) when (attempt < LockAttempts && IsSharingViolation(e))
            {
                Thread.Sleep(LockDelay);
            }
            catch (IOException e) when (IsSharingViolation(e))
            {
                throw new IOException(CoreStrings.ServerListBusy, e);
            }
        }
    }

    /// <summary>Lecture partagée ; réessaie pendant qu'un autre poste écrit la liste.</summary>
    private byte[] ReadShared()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return ReadAll(stream);
            }
            catch (IOException e) when (attempt < LockAttempts && IsSharingViolation(e))
            {
                Thread.Sleep(LockDelay);
            }
        }
    }

    private static byte[] ReadAll(FileStream stream)
    {
        if (stream.Length > ServerListFile.MaxBytes)
        {
            throw new InvalidDataException(CoreStrings.ServerListInvalid);
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Liste lue, avec ou sans BOM UTF-8 : modifiée à la main, elle en a souvent un (PowerShell 5.1, option « UTF-8 avec
    /// BOM » des éditeurs).
    /// </summary>
    private static ServerListFile Parse(byte[] bytes)
    {
        var json = bytes.AsSpan();
        return ServerListFile.Parse(Encoding.UTF8.GetString(json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json));
    }

    // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) sous Windows ; ailleurs, message du système.
    private static bool IsSharingViolation(IOException e) =>
        (e.HResult & 0xFFFF) is 32 or 33 || e.HResult == 11 || e.Message.Contains("being used", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copie de la révision <paramref name="revision"/>, nommée avec sa date d'enregistrement (UTC).</summary>
    private void SaveVersion(int revision, DateTime saved, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(VersionsDirectory);
            var stamp = (saved == default ? DateTime.UtcNow : saved.ToUniversalTime()).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.r{revision:D5}.{stamp}.json";
            File.WriteAllBytes(System.IO.Path.Combine(VersionsDirectory, name), bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sans dossier de versions (droits), la modification se fait quand même ; le journal reste dans la liste.
            Diagnostics.DebugLog.Write("shared", $"Version r{revision} de {Path} non enregistrée : {e.Message}");
        }
    }

    private void PruneVersions()
    {
        try
        {
            foreach (var old in Versions().Skip(MaxVersions))
            {
                File.Delete(old.Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DebugLog.Write("shared", $"Anciennes versions de {Path} non supprimées : {e.Message}");
        }
    }

    private static ServerEntry Copy(ServerEntry s) => new()
    {
        AccountId = s.AccountId,
        Name = s.Name,
        Folder = SessionFolders.Normalize(s.Folder),
        Mode = s.Mode,
        Component = s.Component,
        RemoteMachine = s.RemoteMachine,
        Reason = s.Reason,
        StartDirectory = s.StartDirectory,
        Tag = ServerTagRules.NormalizeName(s.Tag),
        Address = s.Address,
        UserName = s.UserName,
        PlatformId = s.PlatformId,
        SafeName = s.SafeName,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
