using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;
using FluentFTP;
using FluentFTP.Exceptions;

namespace ZillaTerm.Core.Ftp;

/// <summary>Chiffrement d'une connexion FTP.</summary>
public enum FtpSecurity
{
    /// <summary>ftp:// : TLS explicite si le serveur le propose ; sinon en clair, seulement si l'utilisateur l'accepte.</summary>
    Opportunistic,

    /// <summary>ftpes:// : TLS explicite (AUTH TLS) exigé.</summary>
    Explicit,

    /// <summary>ftps:// : TLS implicite (dès la connexion, port 990).</summary>
    Implicit,
}

/// <summary>Certificat d'un serveur FTPS que le système n'approuve pas, montré à l'utilisateur.</summary>
/// <param name="Sha256">Empreinte SHA-256 du certificat (hexadécimal), à épingler.</param>
/// <param name="Problem">Ce que le système lui reproche (nom, chaîne, dates).</param>
public sealed record FtpCertificate(string Subject, string Issuer, string Sha256, DateTime NotBefore, DateTime NotAfter, string Problem);

/// <summary>Paramètres et questions d'une connexion FTP (appelées depuis un autre fil).</summary>
public sealed class FtpConnection
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string UserName { get; init; }

    /// <summary>Mot de passe, lu au moment de la connexion.</summary>
    public required Func<string?> Password { get; init; }

    public required FtpSecurity Security { get; init; }

    /// <summary>Certificat non approuvé par le système : vrai pour continuer (l'appelant peut l'épingler).</summary>
    public required Func<FtpCertificate, bool> TrustCertificate { get; init; }

    /// <summary>Serveur sans TLS (ftp://) : vrai pour continuer en clair (mot de passe et fichiers lisibles sur le réseau).</summary>
    public required Func<bool> AllowCleartext { get; init; }

    /// <summary>
    /// Ce serveur a déjà chiffré une connexion (<see cref="FtpTlsMemory"/>) : s'il ne propose plus TLS, c'est
    /// <see cref="AllowTlsRemoved"/> qui décide, jamais la simple question <see cref="AllowCleartext"/>.
    /// </summary>
    public Func<bool> TlsSeenBefore { get; init; } = () => false;

    /// <summary>Serveur déjà vu avec TLS qui ne le propose plus (interception possible) : vrai pour continuer en clair.</summary>
    public Func<bool> AllowTlsRemoved { get; init; } = () => false;
}

/// <summary>
/// Fichiers d'un serveur FTP ou FTPS (accès d'urgence, entrées KeePass ftp://, ftpes://, ftps://) pour l'onglet
/// Fichiers : navigation, envois et téléchargements vérifiés par SHA-256 (le fichier envoyé est relu), lecture et
/// écriture, droits (SITE CHMOD). Les opérations sont sérialisées sur la connexion de contrôle.
/// </summary>
public sealed class FtpFileBrowser : IRemoteFiles
{
    private readonly AsyncFtpClient _client;
    private readonly PriorityGate _gate = new();

    private FtpFileBrowser(AsyncFtpClient client, string home, bool encrypted)
    {
        _client = client;
        HomeDirectory = home;
        CurrentDirectory = home;
        IsEncrypted = encrypted;
    }

    public string HomeDirectory { get; }

    public string CurrentDirectory { get; private set; }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Connexion chiffrée par TLS (contrôle et données).</summary>
    public bool IsEncrypted { get; }

    public bool ChoosesUploadProtocol => false;

    public TransferProtocol UploadProtocol => IsEncrypted ? TransferProtocol.Ftps : TransferProtocol.Ftp;

    /// <summary>Connexion, TLS négocié selon <see cref="FtpConnection.Security"/>, puis identification.</summary>
    /// <exception cref="FtpRefusedException">Certificat ou connexion en clair refusés par l'utilisateur.</exception>
    public static async Task<FtpFileBrowser> ConnectAsync(FtpConnection connection, CancellationToken ct)
    {
        var mode = connection.Security == FtpSecurity.Implicit ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        try
        {
            return await OpenAsync(connection, mode, ct).ConfigureAwait(false);
        }
        catch (FtpSecurityNotAvailableException) when (connection.Security == FtpSecurity.Opportunistic)
        {
            // Serveur déjà vu avec TLS : le retrait de TLS (qu'un intermédiaire obtient en effaçant AUTH TLS) est signalé.
            bool seen = connection.TlsSeenBefore();
            Diagnostics.DebugLog.Write("ftp", $"{connection.Host}:{connection.Port} ne propose pas TLS{(seen ? " (déjà vu avec TLS)" : "")}");
            if (seen ? !connection.AllowTlsRemoved() : !connection.AllowCleartext())
            {
                throw new FtpRefusedException(seen ? CoreStrings.FtpTlsRemoved : CoreStrings.FtpCleartextDeclined);
            }

            return await OpenAsync(connection, FtpEncryptionMode.None, ct).ConfigureAwait(false);
        }
        catch (FtpSecurityNotAvailableException)
        {
            throw new FtpRefusedException(CoreStrings.FtpTlsRequired);
        }
    }

    private static async Task<FtpFileBrowser> OpenAsync(FtpConnection connection, FtpEncryptionMode mode, CancellationToken ct)
    {
        var config = new FtpConfig
        {
            EncryptionMode = mode,
            DataConnectionEncryption = true,
            ValidateAnyCertificate = false,
            // Mode passif sans suivre l'adresse donnée par le serveur dans sa réponse PASV : la connexion de données
            // va toujours vers le serveur lui-même (un serveur malveillant ne peut pas viser une autre machine du
            // réseau). En IPv6, EPSV, qui ne donne qu'un port.
            DataConnectionType = FtpDataConnectionType.PASVEX,
            ConnectTimeout = 30_000,
            ReadTimeout = 30_000,
            DataConnectionConnectTimeout = 30_000,
            DataConnectionReadTimeout = 60_000,
            SocketKeepAlive = true,
            Noop = true,
            TimeConversion = FtpDate.LocalTime,
            ClientTimeZone = TimeZoneInfo.Local,
        };
        var client = new AsyncFtpClient(connection.Host, connection.UserName, connection.Password() ?? "", connection.Port, config, null)
        {
            // Noms en UTF-8 même quand FEAT n'annonce pas UTF8 (la bibliothèque restait alors en ASCII : « é » devenait
            // « ? », à la lecture comme à l'envoi, qui créait un autre fichier). OPTS UTF8 ON est envoyé après la connexion.
            Encoding = Encoding.UTF8,
        };
        string? accepted = null;
        bool refused = false;
        client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }

            using var certificate = new X509Certificate2(e.Certificate);
            var sha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            // Connexions de données : le certificat déjà accepté pour cette connexion l'est sans nouvelle question.
            if (sha256 == accepted)
            {
                e.Accept = true;
                return;
            }

            e.Accept = !refused && connection.TrustCertificate(new FtpCertificate(certificate.Subject, certificate.Issuer, sha256,
                certificate.NotBefore, certificate.NotAfter, Describe(e.PolicyErrors)));
            if (e.Accept)
            {
                accepted = sha256;
            }
            else
            {
                refused = true;
            }
        };

        try
        {
            await client.Connect(ct).ConfigureAwait(false);
            var home = RemotePath.Normalize(await client.GetWorkingDirectory(ct).ConfigureAwait(false));
            Diagnostics.DebugLog.Write("ftp", $"{connection.Host}:{connection.Port} connecté ({(client.IsEncrypted ? "TLS" : "en clair")}), dossier {home}");
            return new FtpFileBrowser(client, home, client.IsEncrypted);
        }
        catch (Exception e) when (refused && e is not OperationCanceledException)
        {
            client.Dispose();
            throw new FtpRefusedException(CoreStrings.FtpCertificateDeclined);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static string Describe(SslPolicyErrors errors)
    {
        var problems = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            problems.Add(CoreStrings.CertificateNameMismatch);
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            problems.Add(CoreStrings.CertificateUntrusted);
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            problems.Add(CoreStrings.CertificateMissing);
        }

        return string.Join(" ; ", problems);
    }

    // ===================== Navigation =====================

    public async Task<List<RemoteEntry>> ListAsync(string path, bool showHidden, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var target = path.StartsWith('/') ? path : RemotePath.Combine(CurrentDirectory, path);
        // CWD puis PWD : le chemin tel que le serveur le donne (liens résolus selon le serveur).
        await _client.SetWorkingDirectory(Checked(target), ct).ConfigureAwait(false);
        var directory = RemotePath.Normalize(await _client.GetWorkingDirectory(ct).ConfigureAwait(false));
        var entries = await ReadDirectoryAsync(directory, directory, showHidden, ct).ConfigureAwait(false);
        CurrentDirectory = directory;
        return entries;
    }

    public async Task<List<RemoteEntry>> BrowseAsync(string directory, bool showHidden, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await ReadDirectoryAsync(RemotePath.Normalize(directory), CurrentDirectory, showHidden, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Liens résolus au plus par dossier lu : chacun coûte un aller-retour avec le serveur. Au-delà (/usr/lib, /etc/alternatives...),
    /// un lien s'affiche comme un fichier ; l'ouvrir entre dans le dossier s'il en est un.
    /// </summary>
    internal const int MaxResolvedLinks = 40;

    /// <summary>Contenu d'un dossier ; le serveur revient ensuite dans <paramref name="workingDirectory"/>.</summary>
    private async Task<List<RemoteEntry>> ReadDirectoryAsync(string directory, string workingDirectory, bool showHidden, CancellationToken ct)
    {
        var entries = new List<RemoteEntry>();
        int links = 0;
        bool moved = false;
        foreach (var item in await _client.GetListing(Checked(directory), FtpListOption.AllFiles, ct).ConfigureAwait(false))
        {
            if (item.Name is "." or ".." || (!showHidden && item.Name.StartsWith('.')))
            {
                continue;
            }

            bool isDirectory = item.Type == FtpObjectType.Directory;
            if (item.Type == FtpObjectType.Link && !item.Name.Any(char.IsControl) && links++ < MaxResolvedLinks)
            {
                // Un seul CWD par lien (accepté seulement vers un dossier), et un seul retour à la fin.
                var reply = await _client.Execute("CWD " + RemotePath.Combine(directory, item.Name), ct).ConfigureAwait(false);
                isDirectory = reply.Success;
                moved |= reply.Success;
            }

            entries.Add(ToEntry(directory, item, isDirectory));
        }

        if (moved)
        {
            await _client.SetWorkingDirectory(Checked(workingDirectory), ct).ConfigureAwait(false);
        }

        return RemoteEntry.Sort(entries);
    }

    private async Task<bool> IsDirectoryAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _client.DirectoryExists(path, ct).ConfigureAwait(false);
        }
        catch (FtpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Droits donnés par la bibliothèque, écrits « à l'octal » en décimal (640 pour rw-r-----), en bits ; 0 s'ils ne
    /// sont pas connus.
    /// </summary>
    internal static int ModeOf(int chmod) =>
        chmod <= 0 ? 0 : chmod.ToString(CultureInfo.InvariantCulture).All(c => c is >= '0' and <= '7')
            ? Convert.ToInt32(chmod.ToString(CultureInfo.InvariantCulture), 8) & 0xFFF
            : 0;

    private static RemoteEntry ToEntry(string directory, FtpListItem item, bool isDirectory)
    {
        bool isLink = item.Type == FtpObjectType.Link;
        int mode = ModeOf(item.Chmod);
        return new RemoteEntry(
            item.Name,
            RemotePath.Combine(directory, item.Name),
            isDirectory,
            isLink,
            isDirectory ? 0 : Math.Max(0, item.Size),
            item.Modified == DateTime.MinValue ? default : item.Modified,
            mode > 0 ? RemoteEntry.FormatPermissions(isDirectory, isLink, mode) : "",
            RemoteEntry.CleanName(item.RawOwner),
            RemoteEntry.CleanName(item.RawGroup));
    }

    public async Task DeleteAsync(RemoteEntry entry, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        if (entry.IsDirectory && !entry.IsSymbolicLink)
        {
            // RMD : dossier vide seulement (la suppression récursive de la bibliothèque viderait le dossier).
            await CommandAsync("RMD " + Checked(entry.FullPath), ct).ConfigureAwait(false);
        }
        else
        {
            await _client.DeleteFile(Checked(entry.FullPath), ct).ConfigureAwait(false);
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        await CommandAsync("MKD " + Checked(path), ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await ExistsCoreAsync(path, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(RemoteEntry entry, string newPath, CancellationToken ct)
    {
        var path = entry.FullPath;
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        // Jamais d'écrasement : RNTO remplace la cible sur la plupart des serveurs FTP.
        if (await ExistsCoreAsync(newPath, ct).ConfigureAwait(false))
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RenameTargetExists, newPath));
        }

        await CommandAsync("RNFR " + Checked(path), ct).ConfigureAwait(false);
        await CommandAsync("RNTO " + Checked(newPath), ct).ConfigureAwait(false);
    }

    private async Task<bool> ExistsCoreAsync(string path, CancellationToken ct) =>
        await _client.FileExists(Checked(path), ct).ConfigureAwait(false) || await IsDirectoryAsync(path, ct).ConfigureAwait(false);

    /// <summary>Commande FTP simple ; une réponse d'erreur devient une exception avec le message du serveur.</summary>
    private async Task CommandAsync(string command, CancellationToken ct)
    {
        var reply = await _client.Execute(command, ct).ConfigureAwait(false);
        if (!reply.Success)
        {
            throw new IOException(string.IsNullOrWhiteSpace(reply.ErrorMessage) ? $"{reply.Code} {reply.Message}" : reply.ErrorMessage);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Un retour à la ligne dans un chemin ajouterait une commande FTP : refusé. Un nom que l'UTF-8 ne représente pas
    /// (caractère Unicode incomplet, possible dans un nom Windows) l'est aussi, au lieu d'être changé en un autre nom.
    /// </summary>
    private static string Checked(string path)
    {
        if (path.Any(char.IsControl))
        {
            throw new ArgumentException(CoreStrings.ControlCharacterInPath);
        }

        try
        {
            StrictUtf8.GetByteCount(path);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpNameNotEncodable, RemoteEntry.Visible(path)));
        }

        return path;
    }

    /// <summary>Chemin affiché dans un message : caractères de contrôle remplacés par « ? ».</summary>
    private static string Printable(string path) => string.Concat(path.Select(c => char.IsControl(c) ? '?' : c));

    // ===================== Envois =====================

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
                await UploadDirectoryAsync(localPath, remote, checks, links, progress, background, ct).ConfigureAwait(false);
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
            await UploadFileAsync(localPath, remote, checks, progress, background, ct).ConfigureAwait(false);
        }
    }

    private async Task UploadDirectoryAsync(string localDirectory, string remoteDirectory, ICollection<TransferCheck> checks,
        List<TransferCheck> links, IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
        {
            if (!await IsDirectoryAsync(Checked(remoteDirectory), ct).ConfigureAwait(false))
            {
                await CommandAsync("MKD " + remoteDirectory, ct).ConfigureAwait(false);
            }
        }

        var directory = new DirectoryInfo(localDirectory);
        foreach (var file in LocalTree.Files(directory))
        {
            await UploadFileAsync(file.FullName, RemotePath.Combine(remoteDirectory, file.Name), checks, progress, background, ct)
                .ConfigureAwait(false);
        }

        // Liens symboliques et jonctions vers des dossiers : pas suivis (boucle, dossier interdit), notés dans le bilan.
        links.AddRange(LocalTree.Links(directory).Select(link => TransferCheck.SkippedLink(link, RemotePath.Combine(remoteDirectory, link.Name))));
        foreach (var sub in LocalTree.Directories(directory))
        {
            await UploadDirectoryAsync(sub.FullName, RemotePath.Combine(remoteDirectory, sub.Name), checks, links, progress, background, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Un fichier : envoi en hachant le fichier local, puis relecture du fichier arrivé sur le serveur pour comparer les
    /// sommes. Annulé pendant l'envoi : le fichier incomplet est supprimé du serveur (seulement s'il a pu être modifié,
    /// voir <see cref="MayHaveWrittenAsync"/>).
    /// </summary>
    private async Task UploadFileAsync(string localPath, string remotePath, ICollection<TransferCheck> checks,
        IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = Path.GetFileName(localPath);
        var protocol = UploadProtocol;
        bool started = false;
        bool contentSent = false;
        (byte[] Hash, long Length) sent;
        try
        {
            using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
            {
                Checked(remotePath);
                started = true;
                try
                {
                    sent = await SendAsync(localPath, remotePath, name, () => contentSent = true, progress, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException && NeedsRecovery(e))
                {
                    await ReconnectAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (started && await MayHaveWrittenAsync(name, remotePath, contentSent).ConfigureAwait(false))
            {
                checks.Add((await DiscardRemoteAsync(name, localPath, remotePath).ConfigureAwait(false)) with { Protocol = protocol });
            }

            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            checks.Add(new TransferCheck(name, localPath, remotePath, -1, [], -1, [], e.Message) { Upload = true, Failed = true, Protocol = protocol });
            throw;
        }

        try
        {
            using (await _gate.EnterAsync(background, ct).ConfigureAwait(false))
            {
                try
                {
                    var check = await CheckRemoteAsync(name, localPath, remotePath, sent.Length, sent.Hash, progress, ct).ConfigureAwait(false);
                    checks.Add(check with { Protocol = protocol });
                }
                catch (Exception e) when (e is not OutOfMemoryException && NeedsRecovery(e))
                {
                    await ReconnectAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            checks.Add(new TransferCheck(name, localPath, remotePath, -1, [], -1, [], CoreStrings.TransferCheckCancelled)
                { Upload = true, Protocol = protocol });
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Envoyé, mais pas relu (connexion perdue…) : il est sur le serveur, non vérifié, et l'historique le montre.
            checks.Add(new TransferCheck(name, localPath, remotePath, sent.Length, sent.Hash, -1, [], e.Message)
                { Upload = true, Protocol = protocol });
            throw;
        }
    }

    /// <summary>
    /// Envoi annulé : vrai si le fichier du serveur a pu être modifié, et doit donc être supprimé. Sans aucun contenu
    /// parti (annulé avant que le serveur accepte STOR, ou juste après), un fichier non vide est resté tel quel : il est
    /// gardé. Vide, il vient de l'envoi (créé ou vidé par STOR) : il est supprimé.
    /// </summary>
    private async Task<bool> MayHaveWrittenAsync(string name, string remotePath, bool contentSent)
    {
        if (contentSent)
        {
            return true;
        }

        try
        {
            using (await _gate.EnterAsync(background: false, CancellationToken.None).ConfigureAwait(false))
            {
                if (await _client.GetFileSize(remotePath, -1, CancellationToken.None).ConfigureAwait(false) > 0)
                {
                    Diagnostics.DebugLog.Write("ftp", $"Envoi de {name} annulé avant tout contenu : le fichier du serveur est resté tel quel");
                    return false;
                }
            }
        }
        catch (Exception e) when (e is FtpException or IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            // État inconnu : la suppression est tentée, et son échec noté.
        }

        return true;
    }

    /// <summary>
    /// Échec en plein transfert (coupure, délai dépassé, transfert incomplet) : la réponse tardive du serveur fausserait la
    /// commande suivante, la connexion est refaite. Un refus propre du serveur (code d'erreur) ne la dérègle pas.
    /// </summary>
    private static bool NeedsRecovery(Exception e) => e is not (FtpCommandException or ArgumentException);

    /// <summary>
    /// Après un transfert interrompu, la réponse du serveur à ce transfert peut arriver plus tard et passer pour celle de
    /// la commande suivante : la connexion de contrôle est refaite (même identification, certificat déjà accepté).
    /// </summary>
    /// <remarks>
    /// Appelée en gardant la main sur la connexion (<c>_gate</c>) : une commande en attente ne doit pas passer avant, sur
    /// la connexion désynchronisée.
    /// </remarks>
    private async Task ReconnectAsync()
    {
        try
        {
            await _client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FtpException or IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
        {
            // Déjà coupée.
        }

        try
        {
            await _client.Connect(CancellationToken.None).ConfigureAwait(false);
            if (CurrentDirectory.Length > 0)
            {
                await _client.SetWorkingDirectory(CurrentDirectory, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is FtpException or IOException or ObjectDisposedException or TimeoutException or InvalidOperationException
                                      or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            // Reconnexion impossible pour l'instant : la bibliothèque réessaiera à la commande suivante.
            Diagnostics.DebugLog.Write("ftp", "Reconnexion après un transfert interrompu impossible", e);
        }
    }

    /// <param name="contentRead">Appelé dès que du contenu est lu du fichier local pour partir vers le serveur.</param>
    private async Task<(byte[] Hash, long Length)> SendAsync(string localPath, string remotePath, string name, Action contentRead,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await using var file = File.OpenRead(localPath);
        using var hashing = new HashingStream(file);
        long total = file.Length;
        var protocol = UploadProtocol;
        progress?.Report(new TransferProgress(name, 0, total, Protocol: protocol));
        var report = progress is null ? null : new Progress<FtpProgress>(
            p => progress.Report(new TransferProgress(name, p.TransferredBytes, total, Protocol: protocol)));
        FtpStatus status;
        try
        {
            status = await _client.UploadStream(hashing, remotePath, FtpRemoteExists.OverwriteInPlace, false, report, ct).ConfigureAwait(false);
        }
        finally
        {
            if (hashing.Count > 0)
            {
                contentRead();
            }
        }

        if (status != FtpStatus.Success)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpTransferFailed, remotePath));
        }

        return (hashing.GetHash(), hashing.Count);
    }

    /// <summary>Relit le fichier arrivé sur le serveur et le compare au fichier local.</summary>
    private async Task<TransferCheck> CheckRemoteAsync(string name, string localPath, string remotePath, long localLength, byte[] localHash,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new TransferProgress(name, 0, localLength, Verifying: true));
        using var sink = new HashingStream(null);
        var report = progress is null ? null : new Progress<FtpProgress>(
            p => progress.Report(new TransferProgress(name, p.TransferredBytes, localLength, Verifying: true)));
        try
        {
            if (!await _client.DownloadStream(sink, remotePath, 0, report, ct).ConfigureAwait(false))
            {
                throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpTransferFailed, remotePath));
            }
        }
        catch (Exception e) when (e is FtpException or IOException && _client.IsConnected && !ct.IsCancellationRequested)
        {
            // Fichier envoyé mais illisible par le compte (serveur en écriture seule…) : envoyé, non vérifié.
            return new TransferCheck(name, localPath, remotePath, localLength, localHash, -1, [], e.Message) { Upload = true };
        }

        return new TransferCheck(name, localPath, remotePath, localLength, localHash, sink.Count, sink.GetHash()) { Upload = true };
    }

    private async Task<TransferCheck> DiscardRemoteAsync(string name, string localPath, string remotePath)
    {
        string detail;
        try
        {
            using (await _gate.EnterAsync(background: false, CancellationToken.None).ConfigureAwait(false))
            {
                if (await _client.FileExists(remotePath, CancellationToken.None).ConfigureAwait(false))
                {
                    await _client.DeleteFile(remotePath, CancellationToken.None).ConfigureAwait(false);
                }
            }

            detail = CoreStrings.TransferIncompleteDeleted;
        }
        catch (Exception e) when (e is FtpException or IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            detail = string.Format(CultureInfo.CurrentCulture, CoreStrings.TransferIncompleteNotDeleted, e.Message);
        }

        return new TransferCheck(name, localPath, remotePath, -1, [], -1, [], detail) { Upload = true, Interrupted = true };
    }

    // ===================== Téléchargements et lectures =====================

    public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        DownloadAsync(entry, localPath, null, progress, background: false, ct);

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
                started = true;
                try
                {
                    await using (var file = TransferCheck.CreatePartial(partial))
                    {
                        created = true;
                        using var hashing = new HashingStream(file);
                        var report = progress is null ? null : new Progress<FtpProgress>(
                            p => progress.Report(new TransferProgress(entry.Name, p.TransferredBytes, entry.Length)));
                        if (!await _client.DownloadStream(hashing, Checked(entry.FullPath), 0, report, ct).ConfigureAwait(false))
                        {
                            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpTransferFailed, entry.FullPath));
                        }

                        remoteHash = hashing.GetHash();
                        received = hashing.Count;
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException && NeedsRecovery(e))
                {
                    await ReconnectAsync().ConfigureAwait(false);
                    throw;
                }
            }

            // Copie complète : elle ne remplace le fichier local existant que maintenant.
            File.Move(partial, localPath, overwrite: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException && started)
        {
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
            check = new TransferCheck(entry.Name, localPath, entry.FullPath, -1, [], received, remoteHash, e.Message);
        }

        checks?.Add(check);
        return check;
    }

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

    public ITailSource TailSource(string path) => new FtpTailSource(this, path);

    private sealed class FtpTailSource(FtpFileBrowser browser, string path) : ITailSource
    {
        public Task<long> GetSizeAsync(CancellationToken ct) => browser.GetSizeAsync(path, ct);

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => browser.ReadAsync(path, offset, count, ct);
    }

    public async Task<long> GetSizeAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await SizeAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<long> SizeAsync(string path, CancellationToken ct)
    {
        long size = await _client.GetFileSize(Checked(path), -1, ct).ConfigureAwait(false);
        return size >= 0 ? size : throw new FileNotFoundException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpNoSuchFile, path));
    }

    /// <summary>
    /// Lit au plus <paramref name="count"/> octets à partir de <paramref name="offset"/> (REST puis RETR), puis ferme le
    /// transfert et lit la réponse du serveur (226, ou 426 pour un transfert arrêté avant la fin) : non lue, elle
    /// passerait pour la réponse de la commande suivante.
    /// </summary>
    public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var buffer = new byte[count];
        int read = 0;
        var stream = await _client.OpenRead(Checked(path), FtpDataType.Binary, offset, false, ct).ConfigureAwait(false);
        try
        {
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            await _client.GetReply(CancellationToken.None).ConfigureAwait(false);
        }

        return read == count ? buffer : buffer[..read];
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        long size = await SizeAsync(path, ct).ConfigureAwait(false);
        if (size > maxBytes)
        {
            throw new FileTooLargeException(path, size, maxBytes);
        }

        using var content = new BoundedStream(path, maxBytes);
        bool done;
        try
        {
            done = await _client.DownloadStream(content, path, 0, null, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException && (NeedsRecovery(e) || content.TooLarge is not null))
        {
            // Transfert interrompu : connexion refaite avant toute autre commande.
            await ReconnectAsync().ConfigureAwait(false);
            if (content.TooLarge is { } tooLarge)
            {
                throw tooLarge;
            }

            throw;
        }

        if (!done)
        {
            // La bibliothèque rend « échec » sans exception, y compris quand le fichier a grossi au-delà de la limite
            // pendant la lecture (exception de BoundedStream avalée).
            await ReconnectAsync().ConfigureAwait(false);
            throw content.TooLarge ?? new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpTransferFailed, path));
        }

        return content.ToArray();
    }

    public async Task<(DateTime LastWriteTime, long Length)> GetStatAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        return await StatAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<(DateTime LastWriteTime, long Length)> StatAsync(string path, CancellationToken ct)
    {
        long size = await SizeAsync(path, ct).ConfigureAwait(false);
        var modified = await _client.GetModifiedTime(path, ct).ConfigureAwait(false);
        return (modified == DateTime.MinValue ? default : modified, size);
    }

    /// <summary>Remplace le contenu du fichier (STOR) ; renvoie sa nouvelle date et sa taille.</summary>
    /// <exception cref="IOException">Le serveur n'a pas reçu tout le contenu.</exception>
    public async Task<(DateTime LastWriteTime, long Length)> WriteFileAsync(string remotePath, byte[] content, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(content, writable: false);
        var status = await _client.UploadStream(stream, Checked(remotePath), FtpRemoteExists.OverwriteInPlace, false, null, ct).ConfigureAwait(false);
        if (status != FtpStatus.Success)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.FtpTransferFailed, remotePath));
        }

        var stat = await StatAsync(remotePath, ct).ConfigureAwait(false);
        if (stat.Length != content.Length)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CoreStrings.WriteIncomplete, stat.Length, content.Length));
        }

        return stat;
    }

    // ===================== Droits =====================

    public async Task<int?> GetModeAsync(string path, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        int mode = ModeOf(await _client.GetChmod(Checked(path), ct).ConfigureAwait(false));
        return mode > 0 ? mode : null;
    }

    /// <summary>SITE CHMOD sur l'élément et, si demandé, sur son contenu (liens non suivis).</summary>
    public async Task<PermissionsResult> SetPermissionsAsync(string path, PermissionChange change, bool recursive,
        bool executeOnlyIfAlready, IProgress<int>? progress, CancellationToken ct)
    {
        using var entered = await _gate.EnterAsync(background: false, ct).ConfigureAwait(false);
        var result = new PermissionsResult();
        int current = ModeOf(await _client.GetChmod(Checked(path), ct).ConfigureAwait(false));
        if (current == 0 && !change.CoversAllRwx)
        {
            // Droits non donnés par le serveur : ajouter ou retirer un bit partirait de 000.
            result.Errors.Add($"{path} : {CoreStrings.PermissionsUnknown}");
            return result;
        }

        await ChmodAsync(path, change.Apply(current), ct).ConfigureAwait(false);
        result.Changed++;
        progress?.Report(result.Changed);
        if (recursive && await IsDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            await ApplyToContentsAsync(path, change, executeOnlyIfAlready, result, progress, ct).ConfigureAwait(false);
        }

        return result;
    }

    private Task ChmodAsync(string path, int mode, CancellationToken ct) =>
        CommandAsync(string.Format(CultureInfo.InvariantCulture, "SITE CHMOD {0} {1}", Convert.ToString(mode, 8).PadLeft(3, '0'), Checked(path)), ct);

    private async Task ApplyToContentsAsync(string directory, PermissionChange change, bool executeOnlyIfAlready, PermissionsResult result,
        IProgress<int>? progress, CancellationToken ct)
    {
        FtpListItem[] items;
        try
        {
            items = await _client.GetListing(Checked(directory), FtpListOption.AllFiles, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FtpException or IOException)
        {
            result.Errors.Add($"{directory} : {e.Message}");
            return;
        }

        foreach (var item in items.Where(i => i.Name is not ("." or "..") && i.Type != FtpObjectType.Link))
        {
            ct.ThrowIfCancellationRequested();
            var path = RemotePath.Combine(directory, item.Name);
            if (path.Any(char.IsControl))
            {
                // Nom qui ajouterait une commande FTP : l'élément (et son contenu) est laissé tel quel, noté en erreur, et
                // les suivants sont traités.
                result.Errors.Add($"{Printable(path)} : {CoreStrings.ControlCharacterInPath}");
                continue;
            }

            int itemMode = ModeOf(item.Chmod);
            bool isDirectory = item.Type == FtpObjectType.Directory;
            try
            {
                if (itemMode == 0 && !change.CoversAllRwx)
                {
                    result.Errors.Add($"{path} : {CoreStrings.PermissionsUnknown}");
                }
                else
                {
                    await ChmodAsync(path, change.ApplyToContent(itemMode, isDirectory, executeOnlyIfAlready), ct).ConfigureAwait(false);
                    result.Changed++;
                    progress?.Report(result.Changed);
                }
            }
            catch (Exception e) when (e is FtpException or IOException)
            {
                result.Errors.Add($"{path} : {e.Message}");
            }

            if (isDirectory)
            {
                await ApplyToContentsAsync(path, change, executeOnlyIfAlready, result, progress, ct).ConfigureAwait(false);
            }
        }
    }

    // ===================== Arborescence =====================

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
        foreach (var item in await _client.GetListing(Checked(entry.FullPath), FtpListOption.AllFiles, ct).ConfigureAwait(false))
        {
            if (item.Name is "." or "..")
            {
                continue;
            }

            if (item.Type != FtpObjectType.Link)
            {
                children.Add(ToEntry(entry.FullPath, item, item.Type == FtpObjectType.Directory));
                continue;
            }

            // Lien vers un dossier : pas suivi. Lien vers un fichier : téléchargé, avec la taille de sa cible.
            var full = Checked(RemotePath.Combine(entry.FullPath, item.Name));
            if (!await IsDirectoryAsync(full, ct).ConfigureAwait(false))
            {
                long size = await _client.GetFileSize(full, -1, ct).ConfigureAwait(false);
                if (size >= 0)
                {
                    children.Add(ToEntry(entry.FullPath, item, false) with { Length = size });
                }
            }
        }

        foreach (var child in RemoteEntry.Sort(children))
        {
            await AddTreeAsync(child, [.. path, child.Name], items, maxItems, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Mémoire bornée : un fichier qui grossit pendant la lecture n'occupe pas plus que prévu.</summary>
    private sealed class BoundedStream(string path, long maxBytes) : MemoryStream
    {
        /// <summary>Limite dépassée (gardée : la bibliothèque avale l'exception levée pendant le transfert).</summary>
        public FileTooLargeException? TooLarge { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        private void Check(int count)
        {
            if (Length + count > maxBytes)
            {
                throw TooLarge = new FileTooLargeException(path, Length + count, maxBytes);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Connexion FTP abandonnée par l'utilisateur (certificat ou connexion en clair refusés).</summary>
public sealed class FtpRefusedException(string message) : Exception(message);
