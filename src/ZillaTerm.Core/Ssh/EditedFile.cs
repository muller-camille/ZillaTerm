using System.Security.Cryptography;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Fichier du serveur ouvert dans l'éditeur de texte de l'utilisateur : surveille la copie locale et signale
/// (<see cref="Saved"/>) chaque enregistrement dont le contenu diffère du dernier vu et de celui du serveur.
/// </summary>
public sealed class EditedFile : IDisposable
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(400);
    private const int MaxReadAttempts = 10;

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private string _seenHash;
    private string _sentHash;
    // Dernière empreinte lue de la copie locale, avec sa taille et sa date relevées juste avant la lecture : tant qu'elles
    // n'ont pas changé, la copie n'est pas relue (fermeture d'un onglet ou de l'application, sur le fil de l'interface).
    private string? _localHash;
    private (long Length, DateTime WriteTime) _localStamp;
    private int _readAttempts;
    private bool _disposed;

    /// <param name="remoteWriteTime">Date de modification du fichier sur le serveur à l'ouverture.</param>
    /// <param name="remoteLength">Taille du fichier sur le serveur à l'ouverture.</param>
    /// <param name="delay">Attente après la dernière écriture avant de lire le fichier (les éditeurs écrivent en plusieurs fois).</param>
    /// <remarks>Lit et hache toute la copie locale : hors du fil de l'interface pour un gros fichier.</remarks>
    public EditedFile(string remotePath, string localPath, DateTime remoteWriteTime, long remoteLength, TimeSpan? delay = null)
    {
        RemotePath = remotePath;
        LocalPath = localPath;
        RemoteWriteTime = remoteWriteTime;
        RemoteLength = remoteLength;
        _delay = delay ?? DefaultDelay;
        _sentHash = _seenHash = ReadLocalHash();
        _timer = new Timer(_ => OnQuiet());
        // On surveille le dossier : beaucoup d'éditeurs enregistrent dans un fichier temporaire puis le renomment.
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Enregistrement dans l'éditeur avec un contenu nouveau (levé sur un thread du pool).</summary>
    public event Action<EditedFile>? Saved;

    /// <summary>Chemin complet du fichier sur le serveur.</summary>
    public string RemotePath { get; }

    /// <summary>Copie locale ouverte dans l'éditeur.</summary>
    public string LocalPath { get; }

    public string Name => Ssh.RemotePath.Name(RemotePath);

    /// <summary>Date de modification sur le serveur lors de l'ouverture ou du dernier renvoi.</summary>
    public DateTime RemoteWriteTime { get; private set; }

    /// <summary>Taille sur le serveur lors de l'ouverture ou du dernier renvoi.</summary>
    public long RemoteLength { get; private set; }

    /// <summary>Vrai si la copie locale diffère du contenu présent sur le serveur.</summary>
    /// <remarks>
    /// Rapide quand la copie n'a pas changé depuis sa dernière lecture (taille et date) : la surveillance la relit après
    /// chaque enregistrement. Sinon (enregistrement de l'instant), elle est relue : <see cref="HasUnsentChangesAsync"/> le
    /// fait hors du fil de l'interface.
    /// </remarks>
    public bool HasUnsentChanges
    {
        get
        {
            try
            {
                var hash = CurrentLocalHash();
                lock (_lock)
                {
                    return hash != _sentHash;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary><see cref="HasUnsentChanges"/>, la copie relue (si besoin) et hachée hors du fil appelant.</summary>
    public Task<bool> HasUnsentChangesAsync() => Task.Run(() => HasUnsentChanges);

    /// <summary>
    /// Un renvoi a été coupé pendant l'écriture : le fichier du serveur est peut-être incomplet, et c'est nous qui
    /// l'avons modifié (le prochain renvoi ne doit pas le signaler comme changé par quelqu'un d'autre).
    /// </summary>
    public bool WriteInterrupted { get; private set; }

    /// <summary>Le contenu <paramref name="content"/> vient d'être écrit sur le serveur.</summary>
    public void MarkSent(byte[] content, DateTime remoteWriteTime, long remoteLength)
    {
        var hash = Hash(content);
        lock (_lock)
        {
            _sentHash = hash;
            _seenHash = hash;
            RemoteWriteTime = remoteWriteTime;
            RemoteLength = remoteLength;
            WriteInterrupted = false;
        }
    }

    public void MarkWriteInterrupted()
    {
        lock (_lock)
        {
            WriteInterrupted = true;
        }
    }

    /// <summary>
    /// Remplace la copie locale par <paramref name="freshCopy"/>, nouvelle copie du fichier du serveur (changé depuis
    /// l'ouverture), déplacée sur la copie locale : ce contenu devient celui du serveur, sans être signalé comme enregistré.
    /// </summary>
    /// <param name="freshCopy">Copie vérifiée, dans le même dossier que <see cref="LocalPath"/>.</param>
    /// <param name="remoteWriteTime">Date de modification du fichier sur le serveur, relevée avant la copie.</param>
    /// <param name="remoteLength">Taille du fichier sur le serveur, relevée avant la copie.</param>
    /// <remarks>Lit et hache toute la copie : hors du fil de l'interface pour un gros fichier.</remarks>
    public void Refresh(string freshCopy, DateTime remoteWriteTime, long remoteLength)
    {
        string hash;
        using (var stream = new FileStream(freshCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }

        string seen, sent;
        DateTime time;
        long length;
        bool interrupted;
        lock (_lock)
        {
            (seen, sent, time, length, interrupted) = (_seenHash, _sentHash, RemoteWriteTime, RemoteLength, WriteInterrupted);
            // Avant le déplacement : la surveillance, qui le verra, ne le signale pas comme un enregistrement.
            _sentHash = _seenHash = hash;
            RemoteWriteTime = remoteWriteTime;
            RemoteLength = remoteLength;
            WriteInterrupted = false;
        }

        try
        {
            // Remplacement d'un seul coup : l'éditeur qui a la copie ouverte ne lit jamais un contenu à moitié écrit.
            File.Move(freshCopy, LocalPath, overwrite: true);
        }
        catch
        {
            // Copie verrouillée par l'éditeur : elle reste telle quelle, avec ses références.
            lock (_lock)
            {
                (_seenHash, _sentHash, RemoteWriteTime, RemoteLength, WriteInterrupted) = (seen, sent, time, length, interrupted);
            }

            throw;
        }
    }

    // Fichiers qui ne se lisent pas dans un éditeur de texte : archives, images, exécutables, documents bureautiques...
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gz", ".tgz", ".bz2", ".tbz", ".xz", ".txz", ".zst", ".z", ".lz", ".lzma", ".zip", ".7z", ".rar", ".tar", ".cpio",
        ".jar", ".war", ".ear", ".rpm", ".deb", ".apk", ".iso", ".img", ".dmg", ".vmdk", ".qcow2",
        ".exe", ".dll", ".so", ".o", ".a", ".lib", ".bin", ".class", ".pyc", ".msi",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".mp3", ".mp4", ".avi", ".mkv", ".mov", ".wav", ".flac",
        ".db", ".sqlite", ".dmp", ".dbf", ".mdb", ".p12", ".pfx", ".jks", ".keystore", ".der", ".kdbx",
    };

    /// <summary>
    /// Vrai pour un fichier à ne pas ouvrir dans un éditeur de texte d'un double-clic (archive, image, exécutable...) :
    /// son extension le dit binaire. Les autres (sans extension, .conf, .log, .sh, .yml...) s'ouvrent dans l'éditeur.
    /// </summary>
    public static bool LooksBinary(string remoteName) => BinaryExtensions.Contains(Path.GetExtension(remoteName));

    /// <summary>Début du fichier lu pour savoir s'il est du texte (<see cref="LooksBinary(ReadOnlySpan{byte})"/>).</summary>
    public const int SniffLength = 8000;

    /// <summary>
    /// Vrai si le début du fichier contient un octet nul : exécutable ELF, bibliothèque, fichier core, données... (même
    /// règle que git). Un fichier texte, en UTF-8 ou en Latin-1, n'en contient pas.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head) => head[..Math.Min(head.Length, SniffLength)].Contains((byte)0);

    /// <summary>
    /// Nom de la copie locale : celui du fichier distant, limité aux caractères sûrs pour Windows et pour la
    /// ligne de commande de l'éditeur (lettres, chiffres, espace, « . », « - », « _ »).
    /// </summary>
    public static string LocalCopyName(string remoteName)
    {
        var chars = remoteName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_').ToArray();
        var name = new string(chars).Trim(' ', '.');
        if (name.Length == 0)
        {
            return "fichier";
        }

        // Nom réservé de Windows (« nul », « con.txt », « COM1 ») : préfixé, sinon la copie irait vers un périphérique.
        return WindowsFileName.Sanitize(name.Length > 120 ? name[..120] : name);
    }

    /// <summary>Lit un fichier même s'il est encore ouvert par l'éditeur.</summary>
    public static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _watcher.Dispose();
        _timer.Dispose();
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>Taille et date de la copie locale.</summary>
    private (long Length, DateTime WriteTime) LocalStamp()
    {
        var info = new FileInfo(LocalPath);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Empreinte de la copie locale, lue par morceaux (pas tout le fichier en mémoire) et gardée avec sa taille et sa
    /// date relevées avant la lecture : un changement pendant la lecture rend la date différente, la copie sera relue.
    /// </summary>
    private string ReadLocalHash()
    {
        var stamp = LocalStamp();
        string hash;
        using (var stream = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920,
                   FileOptions.SequentialScan))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }

        lock (_lock)
        {
            _localHash = hash;
            _localStamp = stamp;
        }

        return hash;
    }

    /// <summary>Empreinte de la copie locale : celle déjà lue si sa taille et sa date n'ont pas changé, sinon relue.</summary>
    private string CurrentLocalHash()
    {
        var stamp = LocalStamp();
        lock (_lock)
        {
            if (_localHash is { } known && stamp == _localStamp)
            {
                return known;
            }
        }

        return ReadLocalHash();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs r && !string.Equals(r.FullPath, LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_lock)
        {
            if (!_disposed)
            {
                _readAttempts = 0;
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnQuiet()
    {
        string hash;
        try
        {
            hash = ReadLocalHash();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fichier encore verrouillé ou en cours de remplacement par l'éditeur : on réessaie un peu plus tard.
            lock (_lock)
            {
                if (!_disposed && ++_readAttempts < MaxReadAttempts)
                {
                    _timer.Change(_delay, Timeout.InfiniteTimeSpan);
                }
            }

            return;
        }

        lock (_lock)
        {
            if (_disposed || hash == _seenHash)
            {
                return;
            }

            _seenHash = hash;
            if (hash == _sentHash)
            {
                // Retour au contenu du serveur : rien à renvoyer.
                return;
            }
        }

        Saved?.Invoke(this);
    }
}
