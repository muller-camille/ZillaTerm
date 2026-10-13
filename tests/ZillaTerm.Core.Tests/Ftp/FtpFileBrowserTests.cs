using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZillaTerm.Core.Ftp;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Tests.Ssh;

namespace ZillaTerm.Core.Tests.Ftp;

/// <summary>
/// Connexion FTP / FTPS contre un faux serveur : jamais de repli en clair sans accord, TLS exigé en FTPES, certificat
/// refusé avant tout envoi de l'identifiant, et pas de commande injectée par un nom de fichier.
/// </summary>
public sealed class FtpFileBrowserTests
{
    /// <summary>
    /// Faux serveur FTP : réponses minimales, TLS (AUTH TLS) seulement si un certificat est donné, listes de dossiers
    /// (format <c>ls -l</c>) par une connexion de données passive.
    /// </summary>
    private sealed class FakeFtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2? _certificate;
        private readonly CancellationTokenSource _stop = new();
        private TcpListener? _data;

        public FakeFtp(X509Certificate2? certificate = null)
        {
            _certificate = certificate;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Commandes reçues, toutes connexions confondues (en clair ou après TLS).</summary>
        public ConcurrentQueue<string> Commands { get; } = new();

        public int Connections;

        /// <summary>AUTH TLS refusé alors que le serveur a un certificat (intermédiaire qui retire TLS).</summary>
        public bool TlsRemoved { get; set; }

        public bool Received(string verb) =>
            Commands.Any(c => c.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase) || c.Equals(verb, StringComparison.OrdinalIgnoreCase));

        /// <summary>Contenu des dossiers (lignes au format <c>ls -l</c>), par chemin.</summary>
        public Dictionary<string, string[]> Listings { get; } = [];

        /// <summary>Contenu des fichiers, par chemin : à télécharger, ou reçus.</summary>
        public ConcurrentDictionary<string, byte[]> Files { get; } = new();

        /// <summary>Dossier d'une commande LIST (« LIST -a /chemin »).</summary>
        private static string ListedPath(string line)
        {
            var argument = line.Length > 5 ? line[5..] : "";
            return argument.StartsWith("-a ", StringComparison.Ordinal) ? argument[3..] : argument;
        }

        private Task SendListingAsync(Stream stream, string line) =>
            SendDataAsync(stream, Listings.TryGetValue(ListedPath(line), out var entries)
                ? Encoding.UTF8.GetBytes(string.Concat(entries.Select(e => e + "\r\n")))
                : null);

        /// <summary>Reçoit un fichier par la connexion de données ouverte par PASV.</summary>
        private async Task ReceiveAsync(Stream stream, string path)
        {
            var listener = Interlocked.Exchange(ref _data, null);
            if (listener is null)
            {
                await WriteAsync(stream, "425 Use PASV first.");
                return;
            }

            await WriteAsync(stream, "150 Ok to send data.");
            using (var data = await listener.AcceptTcpClientAsync())
            {
                listener.Stop();
                using var received = new MemoryStream();
                await data.GetStream().CopyToAsync(received);
                Files[path] = received.ToArray();
            }

            await WriteAsync(stream, "226 Transfer complete.");
        }

        /// <summary>Envoie <paramref name="content"/> par la connexion de données ouverte par PASV (550 sans contenu).</summary>
        private async Task SendDataAsync(Stream stream, byte[]? content)
        {
            var listener = Interlocked.Exchange(ref _data, null);
            if (listener is null || content is null)
            {
                listener?.Stop();
                await WriteAsync(stream, "550 No such file or directory.");
                return;
            }

            await WriteAsync(stream, "150 Opening data connection.");
            using (var data = await listener.AcceptTcpClientAsync())
            {
                listener.Stop();
                await data.GetStream().WriteAsync(content);
            }

            await WriteAsync(stream, "226 Transfer complete.");
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref Connections);
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var owned = client;
            Stream stream = client.GetStream();
            try
            {
                await WriteAsync(stream, "220 fake FTP");
                while (await ReadLineAsync(stream) is { } line)
                {
                    Commands.Enqueue(line);
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "AUTH" when _certificate is null || TlsRemoved:
                            await WriteAsync(stream, "500 AUTH not understood.");
                            break;
                        case "AUTH":
                            await WriteAsync(stream, "234 Proceed with negotiation.");
                            var ssl = new SslStream(stream);
                            await ssl.AuthenticateAsServerAsync(_certificate);
                            stream = ssl;
                            break;
                        case "USER":
                            await WriteAsync(stream, "331 Please specify the password.");
                            break;
                        case "PASS":
                            await WriteAsync(stream, "230 Login successful.");
                            break;
                        case "FEAT":
                            await WriteAsync(stream, "211-Features:\r\n SIZE\r\n MDTM\r\n211 End");
                            break;
                        case "PWD":
                            await WriteAsync(stream, "257 \"/home/fake\" is the current directory");
                            break;
                        case "SYST":
                            await WriteAsync(stream, "215 UNIX Type: L8");
                            break;
                        case "MKD":
                            await WriteAsync(stream, "257 \"" + line[4..] + "\" created");
                            break;
                        case "SIZE":
                            await WriteAsync(stream, line.Contains("taken", StringComparison.Ordinal) ? "213 12" : "550 No such file.");
                            break;
                        case "CWD":
                            await WriteAsync(stream, line[4..] == "/home/fake" || Listings.ContainsKey(line[4..]) ? "250 OK" : "550 Failed to change directory.");
                            break;
                        case "RNFR":
                            await WriteAsync(stream, "350 Ready for RNTO.");
                            break;
                        case "PASV":
                            var data = new TcpListener(IPAddress.Loopback, 0);
                            data.Start();
                            Interlocked.Exchange(ref _data, data)?.Stop();
                            int port = ((IPEndPoint)data.LocalEndpoint).Port;
                            await WriteAsync(stream, $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "LIST":
                            await SendListingAsync(stream, line);
                            break;
                        case "RETR":
                            await SendDataAsync(stream, Files.GetValueOrDefault(line[5..]));
                            break;
                        case "STOR":
                            await ReceiveAsync(stream, line[5..]);
                            break;
                        case "QUIT":
                            await WriteAsync(stream, "221 Goodbye.");
                            return;
                        default:
                            await WriteAsync(stream, "200 OK");
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Client parti (certificat refusé, fin du test).
            }
        }

        private static async Task WriteAsync(Stream stream, string reply)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\r\n"));
            await stream.FlushAsync();
        }

        private static async Task<string?> ReadLineAsync(Stream stream)
        {
            var line = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0)
                {
                    return null;
                }

                if (one[0] == '\n')
                {
                    return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                }

                line.Add(one[0]);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ftp.fake.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Clé exportée puis relue : SslStream côté serveur (Windows) refuse une clé éphémère.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static FtpConnection Connection(FakeFtp server, FtpSecurity security, Func<bool>? allowCleartext = null,
        Func<FtpCertificate, bool>? trust = null) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        UserName = "alice",
        Password = () => "secret-pw",
        Security = security,
        AllowCleartext = allowCleartext ?? (() => throw new InvalidOperationException("question inattendue")),
        TrustCertificate = trust ?? (_ => throw new InvalidOperationException("question inattendue")),
    };

    [Fact]
    public async Task ServerWithoutTlsIsRefusedWhenTheUserDeclinesCleartext()
    {
        using var server = new FakeFtp();
        int asked = 0;

        var error = await Assert.ThrowsAsync<FtpRefusedException>(() =>
            FtpFileBrowser.ConnectAsync(Connection(server, FtpSecurity.Opportunistic, () => { asked++; return false; }), CancellationToken.None));

        Assert.Equal(CoreStrings.FtpCleartextDeclined, error.Message);
        Assert.Equal(1, asked);
        Assert.True(server.Received("AUTH"));
        // Ni l'identifiant ni le mot de passe ne sont partis en clair.
        Assert.False(server.Received("USER"));
        Assert.False(server.Received("PASS"));
    }

    [Fact]
    public async Task ExplicitTlsNeverFallsBackToCleartext()
    {
        using var server = new FakeFtp();

        var error = await Assert.ThrowsAsync<FtpRefusedException>(() =>
            FtpFileBrowser.ConnectAsync(Connection(server, FtpSecurity.Explicit), CancellationToken.None));

        Assert.Equal(CoreStrings.FtpTlsRequired, error.Message);
        Assert.False(server.Received("USER"));
        Assert.False(server.Received("PASS"));
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task CleartextAcceptedByTheUserConnectsUnencrypted()
    {
        using var server = new FakeFtp();
        int asked = 0;

        using var browser = await FtpFileBrowser.ConnectAsync(
            Connection(server, FtpSecurity.Opportunistic, () => { asked++; return true; }), CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.False(browser.IsEncrypted);
        Assert.Equal(TransferProtocol.Ftp, browser.UploadProtocol);
        Assert.False(browser.ChoosesUploadProtocol);
        Assert.Equal("/home/fake", browser.HomeDirectory);
        Assert.Contains("PASS secret-pw", server.Commands);
    }

    [Fact]
    public async Task ControlCharactersInAPathAreNeverSentToTheServer()
    {
        using var server = new FakeFtp();
        using var browser = await FtpFileBrowser.ConnectAsync(
            Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => browser.CreateDirectoryAsync("/home/fake/a\r\nDELE b", CancellationToken.None));

        Assert.False(server.Received("MKD"));
        Assert.False(server.Received("DELE"));
        await browser.CreateDirectoryAsync("/home/fake/ok", CancellationToken.None);
        Assert.Contains("MKD /home/fake/ok", server.Commands);
    }

    /// <summary>
    /// Droits récursifs : un nom avec un caractère de contrôle (il ajouterait une commande FTP) est noté en erreur, sans
    /// arrêter le reste ; son contenu n'est pas parcouru et le bilan compte les autres éléments.
    /// </summary>
    [Fact]
    public async Task RecursivePermissionsSkipANameWithAControlCharacter()
    {
        using var server = new FakeFtp();
        const string Bell = "\u0007";
        server.Listings["/home/fake/app"] =
        [
            "drwxr-xr-x 2 alice users 4096 Oct 10 08:00 conf",
            $"-rw-r--r-- 1 alice users 12 Oct 10 08:00 bad{Bell}name.txt",
            $"drwxr-xr-x 2 alice users 4096 Oct 10 08:00 bad{Bell}dir",
            "-rw-r--r-- 1 alice users 12 Oct 10 08:00 z.txt",
        ];
        server.Listings["/home/fake/app/conf"] = ["-rw-r--r-- 1 alice users 3 Oct 10 08:00 app.conf"];
        server.Listings[$"/home/fake/app/bad{Bell}dir"] = ["-rw-r--r-- 1 alice users 3 Oct 10 08:00 hidden.txt"];
        using var browser = await FtpFileBrowser.ConnectAsync(
            Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);

        var result = await browser.SetPermissionsAsync("/home/fake/app", PermissionChange.Absolute(0x1ED, includeSpecial: false),
            recursive: true, executeOnlyIfAlready: false, null, CancellationToken.None);

        // Le dossier, conf/, conf/app.conf et z.txt ; les deux noms refusés, chacun une fois, lisibles dans le message.
        Assert.Equal(4, result.Changed);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains($"/home/fake/app/bad?name.txt : {CoreStrings.ControlCharacterInPath}", result.Errors);
        Assert.Contains($"/home/fake/app/bad?dir : {CoreStrings.ControlCharacterInPath}", result.Errors);
        Assert.Contains("SITE CHMOD 755 /home/fake/app/z.txt", server.Commands);
        Assert.Contains("SITE CHMOD 755 /home/fake/app/conf/app.conf", server.Commands);
        Assert.DoesNotContain(server.Commands, c => c.Contains('\u0007', StringComparison.Ordinal));
    }

    /// <summary>
    /// Nom long (jusqu'à 255 caractères, la limite de Windows) : le fichier en cours de téléchargement prend un nom court
    /// dans le même dossier, puis le nom voulu une fois complet ; rien d'autre ne reste dans le dossier.
    /// </summary>
    [Fact]
    public async Task DownloadsAFileWithANameOfMaximumLength()
    {
        using var server = new FakeFtp();
        var name = new string('j', 250) + ".log";
        var content = Encoding.UTF8.GetBytes("ligne 1\nligne 2\n");
        server.Files["/home/fake/" + name] = content;
        var folder = Path.Combine(Path.GetTempPath(), "zt-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var browser = await FtpFileBrowser.ConnectAsync(
                Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);
            var entry = new RemoteEntry(name, "/home/fake/" + name, false, false, content.Length, default, "-rw-r--r--");
            var local = Path.Combine(folder, name);

            var check = await browser.DownloadAsync(entry, local, null, CancellationToken.None);

            Assert.True(check.Matches);
            Assert.Equal(content, File.ReadAllBytes(local));
            Assert.Equal([local], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Serveur qui envoie bien plus que la taille annoncée (2 Kio, plafond 64 Mio de plus) : téléchargement arrêté avec
    /// un message clair, copie incomplète supprimée, rien d'autre sur le disque.
    /// </summary>
    [Fact]
    public async Task DownloadStopsWhenTheServerSendsFarMoreThanAnnounced()
    {
        using var server = new FakeFtp();
        server.Files["/home/fake/app.conf"] = new byte[65 * 1024 * 1024];
        var folder = Path.Combine(Path.GetTempPath(), "zt-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var browser = await FtpFileBrowser.ConnectAsync(
                Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);
            var entry = new RemoteEntry("app.conf", "/home/fake/app.conf", false, false, 2048, default, "-rw-r--r--");
            var checks = new List<TransferCheck>();

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                browser.DownloadAsync(entry, Path.Combine(folder, "app.conf"), checks, null, background: false, CancellationToken.None));

            Assert.Contains(RemotePath.FormatSize(2048), error.Message, StringComparison.Ordinal);
            Assert.True(Assert.Single(checks).Failed);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Envoi d'un dossier : un lien vers un dossier (ici vers le dossier lui-même, une boucle) n'est pas suivi ; il est
    /// noté dans les vérifications, après les fichiers envoyés.
    /// </summary>
    [Fact]
    public async Task FolderUploadDoesNotFollowALinkToAFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "zt-ftp-" + Guid.NewGuid().ToString("N"));
        var site = Path.Combine(folder, "site");
        Directory.CreateDirectory(site);
        try
        {
            File.WriteAllText(Path.Combine(site, "index.html"), "<h1>ok</h1>");
            if (!LocalLinks.TryLinkDirectory(Path.Combine(site, "loop"), site))
            {
                // Windows sans le droit de créer des liens symboliques.
                return;
            }

            using var server = new FakeFtp();
            using var browser = await FtpFileBrowser.ConnectAsync(
                Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);
            var checks = new List<TransferCheck>();

            await browser.UploadAsync(site, "/home/fake", TransferProtocol.Ftp, checks, null, background: false, CancellationToken.None);

            Assert.True(checks[0].Matches);
            Assert.Equal("/home/fake/site/index.html", checks[0].RemotePath);
            var link = Assert.Single(checks, c => c.Skipped);
            Assert.Equal(("loop", "/home/fake/site/loop", false), (link.Name, link.RemotePath, link.Unverified));
            Assert.Same(link, checks[^1]);
            Assert.Equal(2, checks.Count);
            Assert.DoesNotContain(server.Commands, c => c.Contains("loop", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Serveur qui n'annonce pas UTF8 dans FEAT (le faux serveur lit pourtant ses commandes en UTF-8) : les noms restent
    /// en UTF-8 (OPTS UTF8 ON), à la lecture comme à l'envoi, au lieu de devenir « ? » ; un nom que l'UTF-8 ne représente
    /// pas, ou la racine d'un lecteur, sont refusés avant toute commande.
    /// </summary>
    [Fact]
    public async Task NamesStayInUtf8WhenTheServerDoesNotAnnounceIt()
    {
        using var server = new FakeFtp();
        server.Listings["/home/fake"] = ["-rw-r--r-- 1 alice users 3 Oct 10 08:00 Procédure.txt"];
        server.Listings["/home/fake/Données"] = [];
        var folder = Path.Combine(Path.GetTempPath(), "zt-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var local = Path.Combine(folder, "Procèdure.txt");
            File.WriteAllText(local, "abc");
            using var browser = await FtpFileBrowser.ConnectAsync(
                Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);

            Assert.Equal("Procédure.txt", Assert.Single(await browser.ListAsync("/home/fake", true, CancellationToken.None)).Name);
            await browser.ListAsync("/home/fake/Données", true, CancellationToken.None);
            var checks = new List<TransferCheck>();
            await browser.UploadAsync(local, "/home/fake", TransferProtocol.Ftp, checks, null, background: false, CancellationToken.None);

            Assert.Contains("OPTS UTF8 ON", server.Commands);
            Assert.Equal("abc"u8.ToArray(), server.Files["/home/fake/Procèdure.txt"]);
            Assert.True(Assert.Single(checks).Matches);
            Assert.DoesNotContain(server.Commands, c => c.Contains('?', StringComparison.Ordinal));

            await Assert.ThrowsAsync<ArgumentException>(() => browser.CreateDirectoryAsync("/home/fake/a\uD800b", CancellationToken.None));
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    browser.UploadAsync(Path.GetPathRoot(folder)!, "/home/fake", TransferProtocol.Ftp, checks, null, background: false, CancellationToken.None));
            }

            Assert.False(server.Received("MKD"));
            Assert.Single(server.Commands, c => c.StartsWith("STOR ", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task RenameNeverReplacesAnExistingFile()
    {
        using var server = new FakeFtp();
        using var browser = await FtpFileBrowser.ConnectAsync(
            Connection(server, FtpSecurity.Opportunistic, () => true), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => browser.RenameAsync(FileEntry("/home/fake/a.txt"), "/home/fake/taken.txt", CancellationToken.None));
        Assert.False(server.Received("RNFR"));
        Assert.False(server.Received("RNTO"));

        await browser.RenameAsync(FileEntry("/home/fake/a.txt"), "/home/fake/b.txt", CancellationToken.None);
        Assert.Contains("RNFR /home/fake/a.txt", server.Commands);
        Assert.Contains("RNTO /home/fake/b.txt", server.Commands);
    }

    private static RemoteEntry FileEntry(string path) => new(RemotePath.Name(path), path, false, false, 1, default, "");

    [Fact]
    public async Task UntrustedCertificateDeclinedBeforeTheLogin()
    {
        using var certificate = SelfSigned();
        using var server = new FakeFtp(certificate);
        FtpCertificate? shown = null;

        var error = await Assert.ThrowsAsync<FtpRefusedException>(() =>
            FtpFileBrowser.ConnectAsync(Connection(server, FtpSecurity.Explicit, trust: c => { shown = c; return false; }), CancellationToken.None));

        Assert.Equal(CoreStrings.FtpCertificateDeclined, error.Message);
        Assert.NotNull(shown);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), shown.Sha256);
        Assert.Contains("ftp.fake.test", shown.Subject);
        Assert.False(string.IsNullOrWhiteSpace(shown.Problem));
        Assert.False(server.Received("USER"));
        Assert.False(server.Received("PASS"));
    }

    [Fact]
    public async Task TrustedCertificateGivesAnEncryptedSession()
    {
        using var certificate = SelfSigned();
        using var server = new FakeFtp(certificate);
        int asked = 0;

        using var browser = await FtpFileBrowser.ConnectAsync(
            Connection(server, FtpSecurity.Opportunistic, trust: _ => { asked++; return true; }), CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.True(browser.IsEncrypted);
        Assert.Equal(TransferProtocol.Ftps, browser.UploadProtocol);
        Assert.Equal("/home/fake", browser.HomeDirectory);
        // Les transferts aussi sont chiffrés.
        Assert.Contains("PROT P", server.Commands);
    }

    /// <summary>
    /// Serveur vu une fois avec TLS : mémorisé ; s'il se présente ensuite sans TLS (AUTH TLS effacé), ce n'est plus la
    /// simple question « en clair » mais l'alerte de retrait, et sans accord ni l'identifiant ni le mot de passe ne
    /// partent. Le retrait confirmé est oublié : la question habituelle revient.
    /// </summary>
    [Fact]
    public async Task ServerSeenWithTlsIsFlaggedWhenTlsDisappears()
    {
        using var certificate = SelfSigned();
        using var server = new FakeFtp(certificate);
        var settings = new AppSettings();
        int cleartextAsked = 0, removalAsked = 0;
        bool acceptRemoval = false;
        FtpConnection Make() => new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            UserName = "alice",
            Password = () => "secret-pw",
            Security = FtpSecurity.Opportunistic,
            TrustCertificate = _ => true,
            AllowCleartext = () =>
            {
                cleartextAsked++;
                return true;
            },
            TlsSeenBefore = () => FtpTlsMemory.Seen(settings, "127.0.0.1", server.Port),
            AllowTlsRemoved = () =>
            {
                removalAsked++;
                return acceptRemoval;
            },
        };

        using (var first = await FtpFileBrowser.ConnectAsync(Make(), CancellationToken.None))
        {
            Assert.True(first.IsEncrypted);
        }

        Assert.False(FtpTlsMemory.Seen(settings, "127.0.0.1", server.Port));
        Assert.True(FtpTlsMemory.Remember(settings, "127.0.0.1", server.Port));
        Assert.False(FtpTlsMemory.Remember(settings, "127.0.0.1", server.Port));
        Assert.True(FtpTlsMemory.Seen(settings, "127.0.0.1", server.Port));

        server.TlsRemoved = true;
        server.Commands.Clear();
        var error = await Assert.ThrowsAsync<FtpRefusedException>(() => FtpFileBrowser.ConnectAsync(Make(), CancellationToken.None));
        Assert.Equal(CoreStrings.FtpTlsRemoved, error.Message);
        Assert.Equal((0, 1), (cleartextAsked, removalAsked));
        Assert.False(server.Received("USER"));
        Assert.False(server.Received("PASS"));

        acceptRemoval = true;
        using (var accepted = await FtpFileBrowser.ConnectAsync(Make(), CancellationToken.None))
        {
            Assert.False(accepted.IsEncrypted);
        }

        Assert.Equal((0, 2), (cleartextAsked, removalAsked));
        FtpTlsMemory.Forget(settings, "127.0.0.1", server.Port);
        using var later = await FtpFileBrowser.ConnectAsync(Make(), CancellationToken.None);
        Assert.Equal((1, 2), (cleartextAsked, removalAsked));
    }

    /// <summary>Un certificat FTPS épinglé vaut mémoire de TLS (réglages d'avant cette mémoire) ; l'oubli le retire aussi.</summary>
    [Fact]
    public void PinnedCertificateCountsAsTlsSeen()
    {
        var settings = new AppSettings();
        KnownHosts.Remember(settings.KnownHosts, "ftps://FTP01.corp.local", 21, "X.509", "AB12");
        settings.KnownHosts["psmp.corp.local:22"] = "ssh-ed25519 SHA256:AAAA";

        Assert.True(FtpTlsMemory.Seen(settings, "ftp01.corp.local", 21));
        Assert.False(FtpTlsMemory.Seen(settings, "ftp01.corp.local", 2121));

        FtpTlsMemory.Forget(settings, "ftp01.corp.local", 21);
        Assert.False(FtpTlsMemory.Seen(settings, "ftp01.corp.local", 21));
        Assert.Equal(["psmp.corp.local:22"], settings.KnownHosts.Keys);
    }

    [Theory]
    [InlineData(640, 0x1A0)]
    [InlineData(755, 0x1ED)]
    [InlineData(644, 0x1A4)]
    [InlineData(1777, 0x3FF)]
    [InlineData(4755, 0x9ED)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(658, 0)]
    public void ModeOfReadsTheOctalDigits(int chmod, int expected) =>
        Assert.Equal(expected, FtpFileBrowser.ModeOf(chmod));
}
