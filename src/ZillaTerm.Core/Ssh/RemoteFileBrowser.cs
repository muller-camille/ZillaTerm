using System.Globalization;
using ZillaTerm.Core.Localization;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Navigation dans les fichiers du serveur via SFTP (ls, cd, rm, chmod, téléchargement, modification) et dépôt
/// de fichiers par SCP ou SFTP. Les opérations sont sérialisées : une seule à la fois sur la connexion. Chaque fichier
/// envoyé ou téléchargé est vérifié par sa somme SHA-256 des deux côtés (<see cref="TransferCheck"/>).
/// </summary>
public sealed class RemoteFileBrowser : IRemoteFiles
{
    private readonly SftpClient _sftp;
    private readonly Func<CancellationToken, Task<ScpClient>> _scpFactory;
    private readonly PriorityGate _gate = new();

    // Une seule commande SCP à la fois sur la connexion SCP.
    private readonly SemaphoreSlim _scpGate = new(1, 1);
    private ScpClient? _scp;

    // Plus petite taille de fichier refusée en SCP dès l'annonce de sa taille : les fichiers au moins aussi gros partent
    // d'abord en SFTP.
    private long _scpRefusedFrom = long.MaxValue;

    public RemoteFileBrowser(SftpClient sftp, Func<CancellationToken, Task<ScpClient>> scpFactory)
    {
        _sftp = sftp;
        _scpFactory = scpFactory;
        HomeDirectory = sftp.WorkingDirectory;
        CurrentDirectory = HomeDirectory;
    }

    /// <summary>Dossier de connexion (répertoire personnel du compte cible).</summary>
    public string HomeDirectory { get; }

    public string CurrentDirectory { get; private set; }

    /// <summary>
    /// Connexion ouverte ; faux une fois fermée, y compris par une autre fonction de la session qui l'a remplacée (le
    /// client SSH.NET lève alors ObjectDisposedException au lieu de répondre).
    /// </summary>
    public bool IsConnected
    {
        get
        {
            try
            {
                return _sftp.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public bool ChoosesUploadProtocol => true;

    public TransferProtocol UploadProtocol => TransferProtocol.Sftp;

    /// <summary>Liste un dossier (chemin absolu ou relatif au dossier courant) et en fait le dossier courant.</summary>
    public async Task<List<RemoteEntry>> ListAsync(string path, bool showHidden, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var target = path.StartsWith('/') ? path : RemotePath.Combine(CurrentDirectory, path);
        // ChangeDirectory renvoie le chemin canonique (liens symboliques résolus, comme « cd » puis « pwd -P »).
        await _sftp.ChangeDirectoryAsync(target, ct).ConfigureAwait(false);
        var directory = _sftp.WorkingDirectory;
        var entries = await ReadDirectoryAsync(directory, showHidden, ct).ConfigureAwait(false);
        CurrentDirectory = directory;
        return entries;
    }

    /// <summary>
    /// Liste un dossier (chemin absolu) sans en faire le dossier courant : pour choisir un fichier depuis une autre
    /// fenêtre sans déplacer l'onglet Fichiers de ce serveur.
    /// </summary>
    public async Task<List<RemoteEntry>> BrowseAsync(string directory, bool showHidden, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await ReadDirectoryAsync(RemotePath.Normalize(directory), showHidden, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Liens symboliques suivis au plus par dossier lu : chacun coûte deux allers-retours avec le serveur (realpath, puis
    /// lstat), envoyés par paquets de <see cref="LinkBatch"/>. Au-delà (/usr/lib64...), un lien s'affiche comme un
    /// fichier, avec sa propre taille ; l'ouvrir entre dans le dossier s'il en est un.
    /// </summary>
    internal const int MaxResolvedLinks = 400;

    /// <summary>Liens suivis ensemble : leurs demandes partent sans attendre les réponses (SFTP les numérote).</summary>
    private const int LinkBatch = 16;

    /// <summary>
    /// Contenu d'un dossier, trié (dossiers d'abord) ; un lien vers un dossier compte comme un dossier, un lien vers un
    /// fichier a la taille de sa cible (celle du lien ne dit rien du fichier). Annulable (nouvelle navigation).
    /// </summary>
    private async Task<List<RemoteEntry>> ReadDirectoryAsync(string directory, bool showHidden, CancellationToken ct)
    {
        var files = new List<ISftpFile>();
        await foreach (var file in _sftp.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
        {
            if (file.Name is not ("." or "..") && (showHidden || !file.Name.StartsWith('.')))
            {
                files.Add(file);
            }
        }

        // La liste donne les attributs du lien ; on suit le lien pour savoir s'il mène à un dossier, et sa vraie taille.
        var targets = new Dictionary<ISftpFile, SftpFileAttributes>();
        foreach (var batch in files.Where(f => f.IsSymbolicLink).Take(MaxResolvedLinks).Chunk(LinkBatch))
        {
            var attributes = await Task.WhenAll(batch.Select(link => TargetAttributesAsync(link, ct))).ConfigureAwait(false);
            for (int i = 0; i < batch.Length; i++)
            {
                if (attributes[i] is { } target)
                {
                    targets[batch[i]] = target;
                }
            }
        }

        return RemoteEntry.Sort(files.Select(f => !targets.TryGetValue(f, out var target) ? ToEntry(f, f.IsDirectory)
            : target.IsDirectory ? ToEntry(f, true)
            : ToEntry(f, false) with { Length = target.Size }));
    }

    /// <summary>Attributs de la cible d'un lien ; null pour un lien cassé (affiché comme un fichier).</summary>
    private async Task<SftpFileAttributes?> TargetAttributesAsync(ISftpFile link, CancellationToken ct)
    {
        try
        {
            return await _sftp.GetAttributesAsync(link.FullName, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Supprime un fichier (rm), un dossier vide (rmdir) ou un lien symbolique lui-même (jamais sa cible).</summary>
    public async Task DeleteAsync(RemoteEntry entry, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        if (entry.IsSymbolicLink)
        {
            // SSH.NET résout le chemin (realpath) avant de supprimer : sur un lien, c'est sa cible qui partirait.
            var link = await LinkItselfAsync(entry, ct).ConfigureAwait(false);
            await link.DeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        if (entry.IsDirectory)
        {
            await _sftp.DeleteDirectoryAsync(entry.FullPath, ct).ConfigureAwait(false);
        }
        else
        {
            await _sftp.DeleteFileAsync(entry.FullPath, ct).ConfigureAwait(false);
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        await _sftp.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await _sftp.ExistsAsync(path, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(RemoteEntry entry, string newPath, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var directory = RemotePath.Parent(entry.FullPath);
        var listed = await ListByNameAsync(directory, ct).ConfigureAwait(false);
        var target = RemotePath.Parent(newPath) == directory ? listed : await ListByNameAsync(RemotePath.Parent(newPath), ct).ConfigureAwait(false);
        // Jamais d'écrasement : certains serveurs remplaceraient la cible sans rien dire. Nom cherché dans la liste du
        // dossier : ExistsAsync suit les liens, si bien qu'un lien cassé du même nom passerait pour absent.
        if (target.ContainsKey(RemotePath.Name(newPath)) || await _sftp.ExistsAsync(newPath, ct).ConfigureAwait(false))
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RenameTargetExists, newPath));
        }

        // L'élément lui-même, renommé vers le chemin tel quel : RenameFileAsync résoudrait les deux chemins (realpath),
        // renommant la cible d'un lien plutôt que le lien, ou envoyant le fichier vers la cible d'un lien cassé du nouveau
        // nom apparu entre-temps.
        var item = listed.GetValueOrDefault(entry.Name) ?? throw new Renci.SshNet.Common.SftpPathNotFoundException(entry.FullPath);
        await Task.Run(() => item.MoveTo(newPath), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Le lien symbolique lui-même, pris dans la liste de son dossier (voir <see cref="ListByNameAsync"/>).
    /// </summary>
    private async Task<ISftpFile> LinkItselfAsync(RemoteEntry entry, CancellationToken ct) =>
        (await ListByNameAsync(RemotePath.Parent(entry.FullPath), ct).ConfigureAwait(false)).GetValueOrDefault(entry.Name)
        ?? throw new Renci.SshNet.Common.SftpPathNotFoundException(entry.FullPath);

    /// <summary>
    /// Éléments d'un dossier par nom, tels que la liste les donne : le chemin d'un élément listé garde son nom tel quel,
    /// alors que les méthodes de SSH.NET qui prennent un chemin le résolvent (realpath) jusqu'à la cible d'un lien
    /// symbolique ; un lien cassé y figure aussi.
    /// </summary>
    private async Task<Dictionary<string, ISftpFile>> ListByNameAsync(string directory, CancellationToken ct)
    {
        var listed = new Dictionary<string, ISftpFile>(StringComparer.Ordinal);
        await foreach (var file in _sftp.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
        {
            listed[file.Name] = file;
        }

        return listed;
    }

    /// <summary>
    /// Envoie un fichier ou un dossier local (récursivement) dans <paramref name="remoteDirectory"/>, un fichier à la
    /// fois : chaque fichier est envoyé (SCP ou SFTP), puis relu par SFTP pour comparer sa somme SHA-256 à celle du
    /// fichier local. Entre deux fichiers, les demandes interactives (navigation, suppression...) passent avant. Les liens
    /// vers des dossiers ne sont pas suivis (<see cref="LocalTree"/>).
    /// </summary>
    /// <param name="protocol">Protocole préféré ; refusé par le serveur pour un fichier, l'autre prend le relais.</param>
    /// <param name="checks">Reçoit la vérification de chaque fichier traité, y compris celui en échec ou interrompu.</param>
    /// <param name="background">Transfert de la file d'attente : passe après les demandes interactives.</param>
    /// <exception cref="OperationCanceledException">
    /// Annulé : le fichier en cours d'envoi, incomplet, est supprimé du serveur ; les fichiers déjà envoyés restent.
    /// Pendant la vérification, le fichier, complet, reste en place (non vérifié).
    /// </exception>
    public async Task UploadAsync(string localPath, string remoteDirectory, TransferProtocol protocol, ICollection<TransferCheck> checks,
        IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        // Racine d'un lecteur (« E:\ ») refusée : elle serait versée dans le dossier lui-même.
        var name = LocalTree.UploadName(localPath);
        var remote = RemotePath.Combine(remoteDirectory, name);
        if (Directory.Exists(localPath))
        {
            // Liens vers des dossiers, pas suivis : notés à la fin (le rang du fichier en cours ne compte que les fichiers).
            var links = new List<TransferCheck>();
            try
            {
                await UploadDirectoryAsync(localPath, remote, protocol, checks, links, progress, background, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var link in links)
                {
                    checks.Add(link);
                }
            }
        }
        else
        {
            await UploadFileAsync(localPath, remote, protocol, checks, progress, background, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Nombre de fichiers que <see cref="UploadAsync"/> enverra pour ce chemin local (liens vers des dossiers non suivis).</summary>
    public static int CountFiles(string localPath) =>
        Directory.Exists(localPath) ? LocalTree.AllFiles(new DirectoryInfo(localPath)).Count() : 1;

        /// <summary>Suivi d'un fichier du serveur (tail -f) par SFTP : taille, puis lecture de ce qui a été ajouté.</summary>
    public ITailSource TailSource(string path) => new SftpTailSource(this, path);

    /// <summary>Taille actuelle d'un fichier (lien symbolique suivi).</summary>
    /// <exception cref="IOException">Fichier spécial (voir <see cref="EnsureNotSpecial"/>).</exception>
    public async Task<long> GetSizeAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var attributes = await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
        EnsureNotSpecial(path, attributes);
        return attributes.Size;
    }

    /// <summary>Lit au plus <paramref name="count"/> octets d'un fichier à partir de <paramref name="offset"/>.</summary>
    /// <exception cref="IOException">Fichier spécial (voir <see cref="EnsureNotSpecial"/>).</exception>
    public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        // Avant l'ouverture : celle d'un tube nommé bloquerait déjà le serveur SFTP.
        EnsureNotSpecial(path, await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false));
        await using var stream = await _sftp.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == count ? buffer : buffer[..read];
    }

    /// <summary>
    /// Contenu entier d'un fichier, en mémoire (comparaison de fichiers : rien n'est écrit sur le poste).
    /// </summary>
    /// <exception cref="FileTooLargeException">Le fichier dépasse <paramref name="maxBytes"/>.</exception>
    public async Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var attributes = await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
        EnsureNotSpecial(path, attributes);
        if (attributes.Size > maxBytes)
        {
            throw new FileTooLargeException(path, attributes.Size, maxBytes);
        }

        await using var stream = await _sftp.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        using var content = new MemoryStream((int)Math.Max(0, attributes.Size));
        var buffer = new byte[256 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            content.Write(buffer, 0, n);
            if (content.Length > maxBytes)
            {
                // Fichier qui grossit pendant la lecture.
                throw new FileTooLargeException(path, content.Length, maxBytes);
            }
        }

        return content.ToArray();
    }

    private sealed class SftpTailSource(RemoteFileBrowser browser, string path) : ITailSource
    {
        public Task<long> GetSizeAsync(CancellationToken ct) => browser.GetSizeAsync(path, ct);

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => browser.ReadAsync(path, offset, count, ct);
    }

    /// <summary>Date de modification et taille actuelles d'un fichier.</summary>
    public async Task<(DateTime LastWriteTime, long Length)> GetStatAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var attributes = await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
        return (attributes.LastWriteTime, attributes.Size);
    }

    /// <summary>
    /// Remplace le contenu d'un fichier existant par SFTP, sur place : propriétaire, droits, ACL, contexte SELinux,
    /// liens physiques et symboliques sont conservés (un fichier temporaire renommé les perdrait, et SCP recrée le
    /// fichier en 644). Renvoie sa nouvelle date et sa taille.
    /// </summary>
    /// <exception cref="IOException">Le serveur n'a pas reçu tout le contenu.</exception>
    /// <remarks>
    /// Une coupure pendant l'écriture laisse le fichier du serveur incomplet : l'appelant garde le contenu et le renvoie
    /// en entier à la tentative suivante.
    /// </remarks>
    public async Task<(DateTime LastWriteTime, long Length)> WriteFileAsync(string remotePath, byte[] content, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(content, writable: false);
        await _sftp.UploadFileAsync(stream, remotePath, ct).ConfigureAwait(false);
        var attributes = await _sftp.GetAttributesAsync(remotePath, ct).ConfigureAwait(false);
        if (attributes.Size != content.Length)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.WriteIncomplete, attributes.Size, content.Length));
        }

        return (attributes.LastWriteTime, attributes.Size);
    }

    /// <summary>
    /// Droits actuels (12 bits) de <paramref name="path"/>. Pour un lien symbolique, ceux de sa cible, celle que chmod
    /// modifie ; null si le serveur ne donne que ceux du lien.
    /// </summary>
    public async Task<int?> GetModeAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        // SSH.NET résout le chemin (realpath) avant de lire les attributs : on obtient ceux de la cible.
        var attributes = await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
        return attributes.IsSymbolicLink ? null : UnixPermissions.FromAttributes(attributes);
    }

    /// <summary>
    /// Change les droits (chmod) d'un fichier ou d'un dossier, et si <paramref name="recursive"/> de tout son contenu.
    /// Seuls les droits sont envoyés au serveur : taille, dates et propriétaire restent inchangés.
    /// </summary>
    /// <param name="includeSpecial">
    /// Applique aussi les bits spéciaux (setuid, setgid, sticky) à l'élément choisi ; sinon les siens sont conservés.
    /// Le contenu garde toujours ses propres bits spéciaux.
    /// </param>
    /// <param name="executeOnlyIfAlready">
    /// Pour le contenu : les bits x ne sont donnés qu'aux dossiers et aux fichiers déjà exécutables (« X » de chmod),
    /// pour ne pas rendre tous les fichiers exécutables.
    /// </param>
    /// <param name="progress">Nombre d'éléments traités.</param>
    public async Task<PermissionsResult> SetPermissionsAsync(string path, PermissionChange change, bool recursive,
        bool executeOnlyIfAlready, IProgress<int>? progress, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var result = new PermissionsResult();
        var attributes = await _sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
        await ApplyPermissionsAsync(path, attributes, change.Apply(UnixPermissions.FromAttributes(attributes)), ct).ConfigureAwait(false);
        result.Changed++;
        progress?.Report(result.Changed);
        if (recursive && attributes.IsDirectory)
        {
            // Chemin réel du dossier (liens des dossiers parents résolus) : le contenu lu doit s'y trouver.
            var root = (await _sftp.GetAsync(path, ct).ConfigureAwait(false)).FullName;
            await ApplyToContentsAsync(root, change, executeOnlyIfAlready, result, progress, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ApplyToContentsAsync(string directory, PermissionChange change, bool executeOnlyIfAlready, PermissionsResult result,
        IProgress<int>? progress, CancellationToken ct)
    {
        var entries = new List<ISftpFile>();
        try
        {
            await foreach (var file in _sftp.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
            {
                if (!file.FullName.StartsWith(directory.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    // Dossier remplacé par un lien depuis sa lecture : son contenu est ailleurs (/etc…), rien n'y est changé.
                    result.Errors.Add($"{directory} : {CoreStrings.ChangedDuringChmod}");
                    return;
                }

                // Comme « chmod -R », les liens symboliques ne sont pas suivis.
                if (file.Name is not ("." or "..") && !file.IsSymbolicLink)
                {
                    entries.Add(file);
                }
            }
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or InvalidOperationException)
        {
            result.Errors.Add($"{directory} : {e.Message}");
            return;
        }

        foreach (var file in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = change.ApplyToContent(UnixPermissions.FromAttributes(file.Attributes), file.IsDirectory, executeOnlyIfAlready);

            try
            {
                // Élément remplacé par un lien depuis la lecture du dossier : les droits iraient à la cible du lien (le serveur
                // suit les liens), il est laissé tel quel.
                var now = await _sftp.GetAsync(file.FullName, ct).ConfigureAwait(false);
                if (now.FullName != file.FullName || now.IsSymbolicLink || now.IsDirectory != file.IsDirectory)
                {
                    result.Errors.Add($"{file.FullName} : {CoreStrings.ChangedDuringChmod}");
                    continue;
                }

                await ApplyPermissionsAsync(file.FullName, file.Attributes, target, ct).ConfigureAwait(false);
                result.Changed++;
                progress?.Report(result.Changed);
            }
            catch (Exception e) when (e is Renci.SshNet.Common.SshException or InvalidOperationException)
            {
                result.Errors.Add($"{file.FullName} : {e.Message}");
            }

            if (file.IsDirectory)
            {
                await ApplyToContentsAsync(file.FullName, change, executeOnlyIfAlready, result, progress, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Écrit les 12 bits de <paramref name="mode"/> (les bits spéciaux inchangés y sont repris tels quels).</summary>
    private Task ApplyPermissionsAsync(string path, SftpFileAttributes attributes, int mode, CancellationToken ct)
    {
        attributes.OwnerCanRead = (mode & 0x100) != 0;
        attributes.OwnerCanWrite = (mode & 0x80) != 0;
        attributes.OwnerCanExecute = (mode & 0x40) != 0;
        attributes.GroupCanRead = (mode & 0x20) != 0;
        attributes.GroupCanWrite = (mode & 0x10) != 0;
        attributes.GroupCanExecute = (mode & 0x08) != 0;
        attributes.OthersCanRead = (mode & 0x04) != 0;
        attributes.OthersCanWrite = (mode & 0x02) != 0;
        attributes.OthersCanExecute = (mode & 0x01) != 0;
        attributes.IsUIDBitSet = (mode & UnixPermissions.SetUid) != 0;
        attributes.IsGroupIDBitSet = (mode & UnixPermissions.SetGid) != 0;
        attributes.IsStickyBitSet = (mode & UnixPermissions.Sticky) != 0;

        // SSH.NET n'envoie que les attributs modifiés : seuls les droits changent sur le serveur.
        return Task.Run(() => _sftp.SetAttributes(path, attributes), ct);
    }

    /// <summary>
    /// Éléments choisis et, pour les dossiers, tout leur contenu (fichiers cachés compris), chacun avec son chemin
    /// relatif ; un dossier vient avant son contenu. À l'intérieur, les liens vers des dossiers ne sont pas suivis
    /// (boucles, sortie de l'arborescence) ; le dossier courant du navigateur ne change pas.
    /// </summary>
    /// <exception cref="InvalidOperationException">Plus de <paramref name="maxItems"/> éléments.</exception>
    public async Task<List<RemoteTreeItem>> ListTreeAsync(IReadOnlyList<RemoteEntry> roots, int maxItems, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var items = new List<RemoteTreeItem>();
        foreach (var root in roots)
        {
            await AddTreeAsync(root, [root.Name], items, maxItems, ct).ConfigureAwait(false);
        }

        return items;
    }

    private async Task AddTreeAsync(RemoteEntry entry, IReadOnlyList<string> path, List<RemoteTreeItem> items, int maxItems,
        CancellationToken ct)
    {
        if (items.Count >= maxItems)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, CoreStrings.TooManyFiles, maxItems));
        }

        items.Add(new RemoteTreeItem(entry, path));
        if (!entry.IsDirectory)
        {
            return;
        }

        var children = new List<RemoteEntry>();
        await foreach (var file in _sftp.ListDirectoryAsync(entry.FullPath, ct).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            if (SpecialType(file.Attributes) is not null)
            {
                // Périphérique, tube nommé, socket : rien à copier sur le poste (et une lecture qui ne finirait pas).
                continue;
            }

            if (!file.IsSymbolicLink)
            {
                children.Add(ToEntry(file, file.IsDirectory));
                continue;
            }

            try
            {
                // Lien vers un dossier : pas suivi. Lien vers un fichier : téléchargé (le contenu et la taille de la cible).
                var target = await _sftp.GetAttributesAsync(file.FullName, ct).ConfigureAwait(false);
                if (!target.IsDirectory && SpecialType(target) is null)
                {
                    children.Add(ToEntry(file, false) with { Length = target.Size });
                }
            }
            catch (Exception e) when (e is Renci.SshNet.Common.SshException or InvalidOperationException)
            {
                // Lien cassé : rien à télécharger.
            }
        }

        foreach (var child in RemoteEntry.Sort(children))
        {
            await AddTreeAsync(child, [.. path, child.Name], items, maxItems, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Télécharge un fichier en hachant (SHA-256) les données reçues du serveur, puis relit le fichier écrit sur le
    /// disque pour comparer les deux sommes.
    /// </summary>
    public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        DownloadAsync(entry, localPath, null, progress, background: false, ct);

    /// <inheritdoc cref="DownloadAsync(RemoteEntry, string, IProgress{TransferProgress}?, CancellationToken)"/>
    /// <param name="checks">Reçoit la vérification, y compris en cas d'échec ou d'annulation.</param>
    /// <param name="background">Transfert de la file d'attente : passe après les demandes interactives.</param>
    /// <exception cref="OperationCanceledException">
    /// Annulé pendant le téléchargement : le fichier local, incomplet, est supprimé. Pendant la vérification, il reste.
    /// </exception>
    public async Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, ICollection<TransferCheck>? checks,
        IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        byte[] remoteHash;
        long received;
        bool started = false;
        bool created = false;
        var partial = TransferCheck.PartialPath(localPath);
        try
        {
            using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
            {
                // Avant de créer la copie locale : un fichier spécial (ou disparu) est refusé sans rien laisser sur le poste.
                try
                {
                    EnsureNotSpecial(entry.FullPath, await _sftp.GetAttributesAsync(entry.FullPath, ct).ConfigureAwait(false));
                }
                catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
                {
                    checks?.Add(new TransferCheck(entry.Name, localPath, entry.FullPath, -1, [], -1, [], e.Message) { Failed = true });
                    throw;
                }

                started = true;
                await using var file = TransferCheck.CreatePartial(partial);
                created = true;
                using var hashing = new HashingStream(file);
                var report = progress is null ? null : new Progress<Renci.SshNet.DownloadFileProgressReport>(
                    p => progress.Report(new TransferProgress(entry.Name, (long)p.TotalBytesDownloaded, entry.Length)));
                await _sftp.DownloadFileAsync(entry.FullPath, hashing, report, ct).ConfigureAwait(false);
                remoteHash = hashing.GetHash();
                received = hashing.Count;
            }

            // Copie complète : elle ne remplace le fichier local existant que maintenant (un échec en cours de route le laisse
            // intact).
            File.Move(partial, localPath, overwrite: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException && started)
        {
            // Annulé ou en échec en cours de route : la copie, incomplète, est supprimée (rien à supprimer si elle n'a pas pu
            // être créée).
            var detail = created ? DiscardLocal(partial) : null;
            bool cancelled = e is OperationCanceledException && ct.IsCancellationRequested;
            checks?.Add(new TransferCheck(entry.Name, localPath, entry.FullPath, -1, [], -1, [],
                cancelled ? detail ?? e.Message : detail is null ? e.Message : $"{e.Message} ({detail})")
                { Interrupted = cancelled, Failed = !cancelled });
            throw;
        }

        progress?.Report(new TransferProgress(entry.Name, 0, received, Verifying: true));
        TransferCheck check;
        try
        {
            var (localHash, localLength) = await TransferCheck.HashFileAsync(localPath, ct).ConfigureAwait(false);
            check = new TransferCheck(entry.Name, localPath, entry.FullPath, localLength, localHash, received, remoteHash);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            checks?.Add(new TransferCheck(entry.Name, localPath, entry.FullPath, -1, [], received, remoteHash, CoreStrings.TransferCheckCancelled));
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fichier écrit mais illisible ensuite (antivirus, droits) : téléchargé, non vérifié.
            check = new TransferCheck(entry.Name, localPath, entry.FullPath, -1, [], received, remoteHash, e.Message);
        }

        checks?.Add(check);
        return check;
    }

        public void Dispose()
    {
        _sftp.Dispose();
        _scp?.Dispose();
        // _scpGate n'est pas libéré : un envoi SCP arrêté pendant la fermeture le relâche encore après (SemaphoreSlim
        // sans AvailableWaitHandle ne retient aucune ressource).
    }

    private async Task<ScpClient> GetScpAsync(CancellationToken ct)
    {
        if (_scp is { } current && IsUsable(current))
        {
            return current;
        }

        _scp?.Dispose();
        _scp = await _scpFactory(ct).ConfigureAwait(false);
        return _scp;
    }

    private async Task UploadDirectoryAsync(string localDirectory, string remoteDirectory, TransferProtocol protocol,
        ICollection<TransferCheck> checks, List<TransferCheck> links, IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
        {
            await EnsureDirectoryAsync(remoteDirectory, ct).ConfigureAwait(false);
        }

        var directory = new DirectoryInfo(localDirectory);
        foreach (var file in LocalTree.Files(directory))
        {
            await UploadFileAsync(file.FullName, RemotePath.Combine(remoteDirectory, file.Name), protocol, checks, progress, background, ct)
                .ConfigureAwait(false);
        }

        // Liens symboliques et jonctions vers des dossiers : pas suivis (boucle, dossier interdit), notés dans le bilan.
        links.AddRange(LocalTree.Links(directory).Select(link => TransferCheck.SkippedLink(link, RemotePath.Combine(remoteDirectory, link.Name))));
        foreach (var sub in LocalTree.Directories(directory))
        {
            await UploadDirectoryAsync(sub.FullName, RemotePath.Combine(remoteDirectory, sub.Name), protocol, checks, links, progress, background, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Un fichier : envoi, puis vérification. Si le serveur refuse le protocole choisi avant tout contenu (PSMP qui
    /// limite la taille des envois SCP, SFTP en lecture seule…), l'autre protocole prend le relais aussitôt, sans
    /// attente ni question. Annulé pendant l'envoi : le fichier incomplet est supprimé du serveur (seulement s'il a pu
    /// être modifié, pour ne pas effacer un fichier existant resté intact, voir <see cref="MayHaveWrittenAsync"/>). En
    /// échec : noté tel quel, le fichier du serveur pouvant être incomplet. Vérification impossible (connexion perdue…) :
    /// notée « non vérifié » avec l'erreur.
    /// </summary>
    private async Task UploadFileAsync(string localPath, string remotePath, TransferProtocol protocol, ICollection<TransferCheck> checks,
        IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = Path.GetFileName(localPath);
        long length = LocalLength(localPath);
        var used = protocol;
        TransferProtocol? refused = null;
        string? reason = null;
        long refusedFrom = Interlocked.Read(ref _scpRefusedFrom);
        if (protocol == TransferProtocol.Scp && length >= refusedFrom)
        {
            // Un fichier au moins aussi gros a déjà été refusé en SCP à son annonce dans cet onglet : SFTP d'abord, sans
            // attendre un nouveau refus (SCP reste essayé si SFTP est refusé à son tour).
            Diagnostics.DebugLog.Write("files", $"SCP refusé plus tôt dans cet onglet pour un fichier de {refusedFrom} octets : {name} ({length} octets) part en SFTP");
            used = TransferProtocol.Sftp;
            refused = TransferProtocol.Scp;
            reason = string.Format(CultureInfo.CurrentCulture, CoreStrings.ScpRefusedEarlier, RemotePath.FormatSize(refusedFrom));
        }

        var state = new SendState();
        (byte[] Hash, long Length)? sent = null;
        try
        {
            try
            {
                sent = await SendAsync(used, localPath, remotePath, name, length, state, progress, background, ct)
                    .ConfigureAwait(false);
            }
            catch (UploadRefusedException e)
            {
                // Refusé avant tout contenu : rien n'a été écrit sur le serveur, l'autre protocole prend le relais.
                state = new SendState();
                ct.ThrowIfCancellationRequested();
                Diagnostics.DebugLog.Write("files", $"{used.Label()} refusé par le serveur pour {name} ({e.Message}) : envoi en {used.Other().Label()}");
                if (e.AtHeader)
                {
                    RememberScpRefusal(length);
                }

                reason = e.Message;
                refused = used;
                used = used.Other();
                try
                {
                    sent = await SendAsync(used, localPath, remotePath, name, length, state, progress, background, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception second) when (second is not OperationCanceledException && second is not OutOfMemoryException)
                {
                    throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.UploadRefusedThenFailed,
                        refused.Value.Label(), e.Message, used.Label(), second.Message), second);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (state.Started && await MayHaveWrittenAsync(name, remotePath, state).ConfigureAwait(false))
            {
                checks.Add((await DiscardRemoteAsync(name, localPath, remotePath).ConfigureAwait(false))
                    with { Protocol = used, Refused = refused, RefusedReason = reason });
            }

            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            checks.Add(new TransferCheck(name, localPath, remotePath, -1, [], -1, [], e.Message)
                { Upload = true, Failed = true, Protocol = used, Refused = refused, RefusedReason = reason });
            throw;
        }

        // Vérification. Annulée : le fichier, complet, reste sur le serveur, non vérifié.
        try
        {
            // SCP lit le fichier lui-même : il est haché après coup.
            var (localHash, localLength) = sent ?? await TransferCheck.HashFileAsync(localPath, ct).ConfigureAwait(false);
            using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
            {
                var check = await CheckRemoteAsync(name, localPath, remotePath, localLength, localHash, progress, ct).ConfigureAwait(false);
                checks.Add(check with { Protocol = used, Refused = refused, RefusedReason = reason });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            checks.Add(new TransferCheck(name, localPath, remotePath, -1, [], -1, [], CoreStrings.TransferCheckCancelled)
                { Upload = true, Protocol = used, Refused = refused, RefusedReason = reason });
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Envoyé, mais pas relu (connexion SFTP perdue, fichier local devenu illisible…) : il est sur le serveur,
            // non vérifié, et l'historique le montre.
            var (hash, len) = sent ?? ([], -1);
            checks.Add(new TransferCheck(name, localPath, remotePath, len, hash, -1, [], e.Message)
                { Upload = true, Protocol = used, Refused = refused, RefusedReason = reason });
            throw;
        }
    }

    /// <summary>Où en est l'envoi d'un fichier : ce qu'il a pu faire au fichier du serveur s'il est annulé.</summary>
    private sealed class SendState
    {
        /// <summary>L'envoi a pu ouvrir le fichier du serveur (le créer, ou le vider en SFTP).</summary>
        public bool Started { get; set; }

        /// <summary>Du contenu a été lu du fichier local pour partir vers le serveur.</summary>
        public bool ContentSent { get; set; }
    }

    /// <summary>
    /// Envoi annulé : vrai si le fichier du serveur a pu être modifié, et doit donc être supprimé. Sans aucun contenu
    /// parti, un fichier non vide est resté tel quel (SFTP vide le fichier à son ouverture, scp ne le raccourcit qu'à
    /// la fin) : il est gardé. Vide, il vient de l'envoi (créé ou vidé) : il est supprimé.
    /// </summary>
    private async Task<bool> MayHaveWrittenAsync(string name, string remotePath, SendState state)
    {
        if (state.ContentSent)
        {
            return true;
        }

        try
        {
            using (await _gate.EnterAsync(background: false, CancellationToken.None).ConfigureAwait(false))
            {
                var now = await _sftp.GetAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
                if (now.Length > 0)
                {
                    Diagnostics.DebugLog.Write("files", $"Envoi de {name} annulé avant tout contenu : le fichier du serveur est resté tel quel");
                    return false;
                }

                return true;
            }
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            // État inconnu : la suppression est tentée, et son échec noté.
            return true;
        }
    }

    /// <summary>Envoi par l'un des deux protocoles ; SFTP rend la somme du fichier, hachée au fil de l'envoi.</summary>
    /// <param name="state">Ce que l'envoi a pu faire au fichier du serveur (annulé ensuite : il est peut-être supprimé).</param>
    private async Task<(byte[] Hash, long Length)?> SendAsync(TransferProtocol protocol, string localPath, string remotePath, string name,
        long length, SendState state, IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        progress?.Report(new TransferProgress(name, 0, length, Protocol: protocol));
        if (protocol == TransferProtocol.Scp)
        {
            await SendScpAsync(localPath, remotePath, name, state, progress, ct).ConfigureAwait(false);
            return null;
        }

        using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
        {
            state.Started = true;
            return await SendSftpAsync(localPath, remotePath, name, state, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Taille du fichier local ; -1 s'il est illisible (l'envoi le signalera).</summary>
    private static long LocalLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>SCP refusé dès l'annonce de la taille (limite de taille du PSMP probable) : retenu pour cet onglet.</summary>
    private void RememberScpRefusal(long length)
    {
        if (length < 0)
        {
            return;
        }

        long current;
        while (length < (current = Interlocked.Read(ref _scpRefusedFrom))
               && Interlocked.CompareExchange(ref _scpRefusedFrom, length, current) != current)
        {
        }
    }

    /// <summary>
    /// Envoi SFTP, en hachant le fichier local au fil de l'envoi. Refusé par le serveur avant tout contenu (ouverture
    /// du fichier refusée, la connexion restant ouverte) : <see cref="UploadRefusedException"/>.
    /// </summary>
    private async Task<(byte[] Hash, long Length)> SendSftpAsync(string localPath, string remotePath, string name, SendState state,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await using var file = File.OpenRead(localPath);
        using var hashing = new HashingStream(file);
        long total = file.Length;
        var report = progress is null ? null : new Progress<Renci.SshNet.UploadFileProgressReport>(
            p => progress.Report(new TransferProgress(name, (long)p.TotalBytesUploaded, total, Protocol: TransferProtocol.Sftp)));
        try
        {
            await _sftp.UploadFileAsync(hashing, remotePath, report, ct).ConfigureAwait(false);
        }
        catch (Renci.SshNet.Common.SshException e) when (hashing.Count == 0 && _sftp.IsConnected
            && e is not Renci.SshNet.Common.SftpPathNotFoundException && !ct.IsCancellationRequested)
        {
            // Rien n'a été lu du fichier : le serveur a refusé l'ouverture (droits, SFTP en lecture seule…). Dossier
            // absent : SCP échouerait de même, l'erreur reste telle quelle.
            throw new UploadRefusedException(e.Message, e);
        }
        finally
        {
            state.ContentSent |= hashing.Count > 0;
        }

        return (hashing.GetHash(), hashing.Count);
    }

    /// <summary>
    /// Après une annulation, temps laissé à l'envoi SCP pour s'arrêter de lui-même avant de couper la connexion : bien
    /// moins que les 5 s qu'attend la fermeture d'un onglet, pour que le fichier incomplet soit encore supprimé.
    /// </summary>
    internal static TimeSpan ScpStopTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Envoi SCP (connexion SCP dédiée, la connexion SFTP reste libre pendant ce temps). Une annulation arrête l'envoi
    /// à la lecture suivante du fichier : seul son canal se ferme, la connexion SCP reste ouverte pour les envois
    /// suivants (rouvrir une session par le PSMP juste après en avoir coupé une peut être refusé). La connexion n'est
    /// coupée que si l'envoi ne s'arrête pas (serveur muet). Une connexion en échec est oubliée : l'envoi suivant en
    /// ouvre une neuve. Refusé par le serveur avant le contenu (connexion refusée, canal fermé par le PSMP, refus de
    /// scp) : <see cref="UploadRefusedException"/>, rien n'a été écrit.
    /// </summary>
    /// <param name="state">Ce que l'envoi a pu faire au fichier du serveur (annulé ensuite : il est peut-être supprimé).</param>
    private async Task SendScpAsync(string localPath, string remotePath, string name, SendState state,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await _scpGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ScpClient scp;
            try
            {
                scp = await GetScpAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OutOfMemoryException)
            {
                // Connexion SCP refusée (PSMP, authentification abandonnée…).
                Diagnostics.DebugLog.Write("files", $"SCP {name} : connexion SCP impossible ({e.GetType().Name} : {e.Message})");
                throw new UploadRefusedException(e.Message, e);
            }

            await using var file = File.OpenRead(localPath);
            using var source = new CancellableReadStream(file, ct);
            var stop = new ScpStop();
            EventHandler<Renci.SshNet.Common.ScpUploadEventArgs> handler =
                (_, e) => progress?.Report(new TransferProgress(name, e.Uploaded, e.Size, Protocol: TransferProtocol.Scp));
            scp.Uploading += handler;
            try
            {
                state.Started = true;
                // Fil dédié : un envoi qui reste bloqué dans la bibliothèque SSH n'immobilise pas le pool de threads.
                var upload = Task.Factory.StartNew(() => scp.Upload(source, remotePath), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var cut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (ct.Register(() => _ = StopStuckScpAsync(upload, scp, stop, cut)))
                {
                    if (await Task.WhenAny(upload, cut.Task).ConfigureAwait(false) != upload)
                    {
                        // Connexion coupée, mais l'envoi peut rester bloqué dans l'attente d'une réponse du serveur
                        // (la bibliothèque SSH ne s'en libère pas) : il est abandonné, la file continue.
                        _ = upload.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        throw new OperationCanceledException(ct);
                    }

                    await upload.ConfigureAwait(false);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Arrêtée à la lecture suivante : la connexion reste utilisable. Coupée (serveur muet) : oubliée.
                if (!IsUsable(scp))
                {
                    ForgetScp(scp);
                }

                throw new OperationCanceledException(ct);
            }
            catch (Renci.SshNet.Common.ScpException e) when (!source.WasRead)
            {
                // Refus de scp sur le serveur avant le contenu (droits, dossier absent…) : connexion intacte.
                Diagnostics.DebugLog.Write("files", $"SCP {name} : refusé par scp avant le contenu ({e.Message})");
                throw new UploadRefusedException(e.Message, e);
            }
            catch (Renci.SshNet.Common.ScpException)
            {
                throw;
            }
            catch (Renci.SshNet.Common.SshException e) when (!source.WasRead)
            {
                // Canal fermé par le serveur avant le contenu : dès la commande scp, ou à l'annonce du fichier quand sa
                // taille a déjà été lue (la bibliothèque la lit juste avant de l'annoncer).
                ForgetScp(scp);
                bool atHeader = source.LengthRead;
                Diagnostics.DebugLog.Write("files",
                    $"SCP {name} : canal fermé par le serveur {(atHeader ? "à l'annonce du fichier" : "dès la commande scp")} ({e.Message})");
                var message = atHeader
                    ? string.Format(CultureInfo.CurrentCulture, CoreStrings.ScpRefusedHeader, RemotePath.FormatSize(file.Length), e.Message)
                    : string.Format(CultureInfo.CurrentCulture, CoreStrings.ScpRefusedCommand, e.Message);
                throw new UploadRefusedException(message, e, atHeader);
            }
            catch (Renci.SshNet.Common.SshException e)
            {
                Diagnostics.DebugLog.Write("files", $"SCP {name} : échec pendant l'envoi ({e.GetType().Name} : {e.Message})");
                ForgetScp(scp);
                throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.ScpClosedByServer, e.Message), e);
            }
            catch (Exception)
            {
                // Erreur hors SSH (fichier local illisible…) : seul le canal est fermé, la connexion sert encore.
                if (!IsUsable(scp))
                {
                    ForgetScp(scp);
                }

                throw;
            }
            finally
            {
                state.ContentSent |= source.WasRead;
                scp.Uploading -= handler;
                // Coupée par la surveillance juste quand l'envoi se terminait : à ne pas laisser au suivant.
                if (!stop.Finish())
                {
                    ForgetScp(scp);
                }
            }
        }
        finally
        {
            _scpGate.Release();
        }
    }

    /// <summary>
    /// Envoi refusé par le serveur avant tout contenu : rien n'a été écrit, l'autre protocole peut prendre le relais.
    /// </summary>
    /// <param name="atHeader">
    /// Canal SCP fermé par le serveur à l'annonce du fichier (nom et taille), la commande scp ayant été acceptée.
    /// </param>
    private sealed class UploadRefusedException(string message, Exception inner, bool atHeader = false) : IOException(message, inner)
    {
        public bool AtHeader { get; } = atHeader;
    }

    /// <summary>
    /// Fin d'un envoi SCP ou coupure par la surveillance, le premier des deux l'emporte : la connexion n'est jamais coupée
    /// une fois l'envoi terminé (elle peut déjà servir à l'envoi suivant).
    /// </summary>
    private sealed class ScpStop
    {
        private int _state; // 0 : en cours, 1 : terminé, 2 : coupé

        public bool TryCut() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

        /// <summary>Faux si la connexion a été coupée avant la fin.</summary>
        public bool Finish() => Interlocked.CompareExchange(ref _state, 1, 0) != 2;
    }

    /// <summary>
    /// Annulé mais l'envoi ne s'arrête pas (serveur qui ne lit plus ou ne répond plus) : la connexion SCP est coupée et
    /// <paramref name="cut"/> signale de ne plus l'attendre.
    /// </summary>
    private static async Task StopStuckScpAsync(Task upload, ScpClient scp, ScpStop stop, TaskCompletionSource cut)
    {
        if (await Task.WhenAny(upload, Task.Delay(ScpStopTimeout)).ConfigureAwait(false) != upload && stop.TryCut())
        {
            Diagnostics.DebugLog.Write("files", "SCP : l'envoi annulé ne s'arrête pas, connexion SCP coupée");
            AbortScp(scp);
            cut.TrySetResult();
        }
    }

    /// <summary>Connexion SCP en échec ou coupée : fermée, et la prochaine sera neuve.</summary>
    private void ForgetScp(ScpClient scp)
    {
        if (ReferenceEquals(_scp, scp))
        {
            _scp = null;
        }

        AbortScp(scp);
    }

    /// <summary>Connexion SCP encore ouverte (une annulation tombée à la fin d'un envoi a pu la fermer).</summary>
    private static bool IsUsable(ScpClient scp)
    {
        try
        {
            return scp.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static void AbortScp(ScpClient scp)
    {
        try
        {
            scp.Dispose();
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // Connexion déjà coupée.
        }
    }

    /// <summary>
    /// Supprime du serveur le fichier dont l'envoi a été interrompu, seulement s'il est un fichier ordinaire. Un lien
    /// symbolique (fichier remplacé à travers le lien) est gardé, et sa cible, incomplète, aussi : SSH.NET supprimerait la
    /// cible (realpath), peut-être hors du dossier, avec son propriétaire et ses droits, et laisserait le lien cassé.
    /// </summary>
    private async Task<TransferCheck> DiscardRemoteAsync(string name, string localPath, string remotePath)
    {
        string detail;
        try
        {
            using (await _gate.EnterAsync(background: false, CancellationToken.None).ConfigureAwait(false))
            {
                var listed = await ListByNameAsync(RemotePath.Parent(remotePath), CancellationToken.None).ConfigureAwait(false);
                var item = listed.GetValueOrDefault(RemotePath.Name(remotePath));
                if (item is { IsRegularFile: true })
                {
                    // L'élément listé lui-même : son chemin n'est pas résolu.
                    await item.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else if (item is not null)
                {
                    Diagnostics.DebugLog.Write("files", $"Envoi de {name} annulé : {remotePath} n'est pas un fichier ordinaire, rien n'est supprimé");
                    return new TransferCheck(name, localPath, remotePath, -1, [], -1, [],
                        string.Format(CultureInfo.CurrentCulture, CoreStrings.TransferIncompleteLinkKept, RemoteEntry.Visible(await TargetOfAsync(item))))
                        { Upload = true, Interrupted = true };
                }
            }

            detail = CoreStrings.TransferIncompleteDeleted;
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            detail = CoreStrings.TransferIncompleteDeleted;
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException)
        {
            detail = string.Format(CultureInfo.CurrentCulture, CoreStrings.TransferIncompleteNotDeleted, e.Message);
        }

        return new TransferCheck(name, localPath, remotePath, -1, [], -1, [], detail) { Upload = true, Interrupted = true };
    }

    /// <summary>Chemin réel (liens résolus) d'un élément listé ; son propre chemin si le serveur ne le donne pas.</summary>
    private async Task<string> TargetOfAsync(ISftpFile item)
    {
        try
        {
            return (await _sftp.GetAsync(item.FullName, CancellationToken.None).ConfigureAwait(false)).FullName;
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or InvalidOperationException)
        {
            return item.FullName;
        }
    }

    /// <summary>Supprime la copie locale d'un téléchargement interrompu ; renvoie ce qui a été fait.</summary>
    private static string DiscardLocal(string localPath)
    {
        try
        {
            File.Delete(localPath);
            return CoreStrings.TransferIncompleteDeleted;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Format(CultureInfo.CurrentCulture, CoreStrings.TransferIncompleteNotDeleted, e.Message);
        }
    }

    /// <summary>Relit par SFTP le fichier arrivé sur le serveur et le compare au fichier local.</summary>
    private async Task<TransferCheck> CheckRemoteAsync(string name, string localPath, string remotePath, long localLength, byte[] localHash,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new TransferProgress(name, 0, localLength, Verifying: true));
        using var sink = new HashingStream(null);
        var report = progress is null ? null : new Progress<Renci.SshNet.DownloadFileProgressReport>(
            p => progress.Report(new TransferProgress(name, (long)p.TotalBytesDownloaded, localLength, Verifying: true)));
        try
        {
            await _sftp.DownloadFileAsync(remotePath, sink, report, ct).ConfigureAwait(false);
        }
        catch (Renci.SshNet.Common.SshException e) when (_sftp.IsConnected)
        {
            // Fichier envoyé mais illisible par le compte (droits, ACL) : envoyé, non vérifié.
            return new TransferCheck(name, localPath, remotePath, localLength, localHash, -1, [], e.Message) { Upload = true };
        }

        return new TransferCheck(name, localPath, remotePath, localLength, localHash, sink.Count, sink.GetHash()) { Upload = true };
    }

    private async Task EnsureDirectoryAsync(string remote, CancellationToken ct)
    {
        if (!await _sftp.ExistsAsync(remote, ct).ConfigureAwait(false))
        {
            await _sftp.CreateDirectoryAsync(remote, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Propriétaire et groupe : les noms de la ligne « longname » envoyée par le serveur (SFTP version 3), sinon les
    /// numéros UID et GID des attributs.
    /// </summary>
    private static RemoteEntry ToEntry(ISftpFile f, bool isDirectory)
    {
        var names = SftpLongName.Parse((f as SftpFile)?.LongName, f.Length);
        var permissions = RemoteEntry.FormatPermissions(isDirectory, f.IsSymbolicLink, UnixPermissions.FromAttributes(f.Attributes));
        if (SpecialType(f.Attributes) is { } type)
        {
            // Comme ls -l : « crw-rw-rw- » pour /dev/null, « prw-r--r-- » pour un tube nommé.
            permissions = type + permissions[1..];
        }

        return new(
            f.Name,
            f.FullName,
            isDirectory,
            f.IsSymbolicLink,
            f.Length,
            f.LastWriteTime,
            permissions,
            names?.Owner ?? IdText(f.Attributes.UserId),
            names?.Group ?? IdText(f.Attributes.GroupId));
    }

    /// <summary>
    /// Type d'un fichier spécial, comme le premier caractère de ls -l : « c » (périphérique caractère : /dev/zero),
    /// « b » (bloc), « p » (tube nommé), « s » (socket) ; null pour un fichier, un dossier ou un lien.
    /// </summary>
    internal static char? SpecialType(SftpFileAttributes attributes) =>
        attributes.IsCharacterDevice ? 'c' : attributes.IsBlockDevice ? 'b' : attributes.IsNamedPipe ? 'p' : attributes.IsSocket ? 's' : null;

    /// <summary>
    /// Refuse de lire un fichier spécial (lien symbolique suivi) : /dev/zero ne finit jamais, un tube nommé bloque le
    /// serveur SFTP jusqu'à ce qu'un autre programme y écrive.
    /// </summary>
    /// <exception cref="IOException">Fichier spécial.</exception>
    private static void EnsureNotSpecial(string path, SftpFileAttributes attributes)
    {
        if (SpecialType(attributes) is not null)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.SpecialFileRefused, RemoteEntry.Visible(path)));
        }
    }

    /// <summary>
    /// UID ou GID en clair (non signé, comme le serveur l'envoie : 4294967294 et non -2) ; vide si le serveur ne l'a
    /// pas envoyé (-1).
    /// </summary>
    internal static string IdText(int id) => id == -1 ? "" : unchecked((uint)id).ToString(CultureInfo.InvariantCulture);
}
