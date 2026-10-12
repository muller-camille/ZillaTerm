using System.Globalization;
using System.Text;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ZillaTerm.Core.Ssh;

/// <summary>Interactions avec l'utilisateur, appelées depuis les threads de SSH.NET (à marshaler vers l'UI).</summary>
public interface ISshInteraction
{
    /// <summary>Vérifie la clé d'hôte du serveur ; renvoie vrai pour poursuivre la connexion.</summary>
    bool CheckHostKey(string host, int port, string algorithm, string sha256Fingerprint);

    /// <summary>Question posée par le serveur (mot de passe, code MFA...) ; null si l'utilisateur annule.</summary>
    SshAnswer? Prompt(SshQuestion question);

    /// <summary>Types de clé d'hôte déjà acceptés pour ce serveur (voir <see cref="KnownHosts.KeyType"/>).</summary>
    IReadOnlyCollection<string> KnownHostKeyTypes(string host, int port) => [];
}

/// <summary>Question posée par le serveur pendant l'authentification.</summary>
/// <param name="Refused">Réponse précédente refusée : le dire, avec le numéro de l'essai ; null au premier essai.</param>
/// <param name="OfferShare">
/// Mot de passe demandé pendant l'ouverture d'un groupe de sessions : proposer de le réutiliser pour les autres.
/// </param>
public sealed record SshQuestion(string Instruction, string Prompt, bool Echo, string? Refused = null, bool OfferShare = false);

/// <summary>Réponse de l'utilisateur ; <paramref name="Share"/> : à réutiliser pour les autres sessions du groupe.</summary>
public sealed record SshAnswer(string Text, bool Share = false);

/// <summary>
/// Ouvre les connexions SSH, SFTP et SCP : vers une cible via le PSM for SSH (identifiant
/// <c>coffre@compte[#domaine]@cible</c>, chaque connexion est une session PSMP distincte), ou directement vers un
/// serveur (accès d'urgence avec un mot de passe venant d'un coffre KeePass).
/// </summary>
public sealed class SshConnector
{
    private readonly ISshInteraction _ui;
    private readonly Func<CancellationToken, Task<PrivateKeyFile?>>? _key;
    private readonly Func<string?>? _password;
    private readonly SshAnswerCache _answers = new();
    private readonly SshAnswerCache? _group;
    private int _leftGroup;

    /// <summary>
    /// Essais d'authentification par connexion : chaque mot de passe refusé compte pour le verrouillage du compte
    /// (CyberArk, annuaire), on s'arrête donc avant le seuil habituel.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <param name="key">
    /// Clé SSH « MFA caching » fournie par le PVWA, demandée au début de chaque connexion (la demande peut prendre du
    /// temps : l'onglet est déjà ouvert et montre la progression) ; null si elle n'est pas disponible.
    /// </param>
    /// <param name="password">
    /// Mot de passe connu (coffre KeePass), lu au moment de chaque connexion ; s'il est refusé, il est demandé.
    /// </param>
    /// <param name="group">
    /// Sessions ouvertes ensemble (dossier, vue parallèle) : le mot de passe donné pour l'une peut, si l'utilisateur le
    /// choisit, servir aux autres ; oublié dès qu'elles sont toutes authentifiées.
    /// </param>
    public SshConnector(string host, int port, string login, ISshInteraction ui, Func<CancellationToken, Task<PrivateKeyFile?>>? key = null,
        Func<string?>? password = null, string? addressWhat = null, SshAnswerCache? group = null)
    {
        PsmpTarget.Validate(host, addressWhat ?? CoreStrings.PsmpAddressWhat);
        Host = host;
        Port = port;
        Login = login;
        _ui = ui;
        _key = key;
        _password = password;
        _group = group;
        _group?.Join();
    }

    public string Host { get; }

    public int Port { get; }

    public string Login { get; }

    /// <summary>Bannière d'authentification envoyée par le serveur (avertissement d'enregistrement...).</summary>
    public string? Banner { get; private set; }

    public Task<SshClient> ConnectShellAsync(CancellationToken ct) => ConnectAsync(info => new SshClient(info), ct);

    public Task<SftpClient> ConnectSftpAsync(CancellationToken ct) => ConnectAsync(info => new SftpClient(info), ct);

    /// <summary>
    /// SCP : les chemins sont passés à une commande « scp -t » exécutée par le shell de la cible ;
    /// ils sont donc protégés entre apostrophes (ShellQuote) pour empêcher toute injection de commande.
    /// </summary>
    public Task<ScpClient> ConnectScpAsync(CancellationToken ct) =>
        ConnectAsync(info => new ScpClient(info, RemotePathTransformation.ShellQuote), ct, "SCP");

    /// <summary>
    /// Explorateur de fichiers : connexion SFTP maintenue (voir <see cref="KeepAlive"/>), SCP ouvert à la demande pour
    /// les envois qui le demandent.
    /// </summary>
    public async Task<IRemoteFiles> OpenFileBrowserAsync(CancellationToken ct)
    {
        var sftp = await ConnectSftpAsync(ct).ConfigureAwait(false);
        KeepAlive(sftp);
        return new RemoteFileBrowser(sftp, ConnectScpAsync);
    }

    /// <summary>Intervalle entre deux demandes de maintien de la connexion.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>Demandes de maintien laissées sans réponse, au plus, avant que la connexion soit tenue pour perdue.</summary>
    public const int KeepAliveCountMax = 4;

    /// <summary>
    /// Maintien d'une connexion ouverte (comme ServerAliveInterval et ServerAliveCountMax d'OpenSSH) : une demande à
    /// laquelle le serveur doit répondre toutes les 15 s. Sans rien recevoir du serveur pendant quatre intervalles, la
    /// connexion est tenue pour perdue (poste en veille, changement de réseau ou de VPN, pare-feu qui l'a oubliée) :
    /// erreur de la connexion au bout d'une minute environ, au lieu d'un terminal ou d'une liste de fichiers figés
    /// jusqu'à ce que le réseau abandonne.
    /// </summary>
    public static void KeepAlive(BaseClient client)
    {
        client.KeepAliveCountMax = KeepAliveCountMax;
        client.KeepAliveInterval = KeepAliveInterval;
    }

    /// <param name="purpose">Nom de la connexion dans le journal (par défaut, son type).</param>
    private async Task<T> ConnectAsync<T>(Func<ConnectionInfo, T> create, CancellationToken ct, string? purpose = null)
        where T : BaseClient
    {
        purpose ??= typeof(T).Name;
        PrivateKeyFile? key = null;
        try
        {
            key = _key is null ? null : await _key(ct).ConfigureAwait(false);
            return await ConnectWithKeyAsync(create, key, purpose, ct).ConfigureAwait(false);
        }
        finally
        {
            // La clé MFA n'est gardée que le temps de cette connexion.
            key?.Dispose();
            // Première authentification de cette session terminée : elle ne compte plus parmi celles qui s'ouvrent.
            if (Interlocked.Exchange(ref _leftGroup, 1) == 0)
            {
                _group?.Leave();
            }
        }
    }

    private async Task<T> ConnectWithKeyAsync<T>(Func<ConnectionInfo, T> create, PrivateKeyFile? key, string purpose, CancellationToken ct)
        where T : BaseClient
    {
        var knownTypes = _ui.KnownHostKeyTypes(Host, Port);
        // 1er essai : clé MFA (ou mot de passe connu) + keyboard-interactive. Ensuite, selon les méthodes que le
        // serveur annonce dans son refus : keyboard-interactive à nouveau (mauvais mot de passe) ou « password ».
        bool usePassword = false;
        int refusals = 0;
        for (int attempt = 0; ; attempt++)
        {
            bool cancelled = false;
            // Après un refus, la première question de l'essai le dit (et non en silence, ce qui pousserait à retaper
            // le même mot de passe jusqu'au verrouillage du compte).
            string? refused = refusals == 0 ? null
                : string.Format(CultureInfo.CurrentCulture, CoreStrings.AuthenticationRefusedRetry, refusals + 1, MaxAttempts);
            var known = attempt == 0 ? _password?.Invoke() : null;
            // Un mot de passe ou un code a été envoyé : un refus compte alors pour le verrouillage du compte.
            bool answered = known is not null;
            var methods = new List<AuthenticationMethod>();
            if (key is not null && attempt == 0)
            {
                methods.Add(new PrivateKeyAuthenticationMethod(Login, key));
            }

            if (known is not null)
            {
                methods.Add(new PasswordAuthenticationMethod(Login, known));
            }

            if (usePassword)
            {
                var password = Answer(string.Format(CultureInfo.CurrentCulture, CoreStrings.PasswordPromptTitle, Host),
                        string.Format(CultureInfo.CurrentCulture, CoreStrings.PasswordPrompt, Login), echo: false, refused)
                    ?? throw new OperationCanceledException(CoreStrings.AuthenticationCancelled);
                answered = true;
                methods.Add(new PasswordAuthenticationMethod(Login, password));
            }
            else
            {
                var interactive = new KeyboardInteractiveAuthenticationMethod(Login);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts)
                    {
                        // La question seulement, jamais la réponse.
                        DebugLog.Write("ssh", $"{Host} : question du serveur « {prompt.Request.Trim()} » (saisie affichée {prompt.IsEchoed})");
                        var answer = known is not null && !prompt.IsEchoed && IsPasswordPrompt(prompt.Request)
                            ? known
                            : Answer(e.Instruction, prompt.Request, prompt.IsEchoed, refused);
                        refused = null;
                        if (answer is null)
                        {
                            cancelled = true;
                            answer = "";
                        }
                        else
                        {
                            answered = true;
                        }

                        prompt.Response = answer;
                    }
                };
                methods.Add(interactive);
            }

            var info = new ConnectionInfo(Host, Port, Login, [.. methods])
            {
                Timeout = TimeSpan.FromSeconds(30),
                Encoding = Encoding.UTF8,
            };
            info.AuthenticationBanner += (_, e) => Banner = e.BannerMessage;
            PreferKnownHostKeys(info.HostKeyAlgorithms, knownTypes);
            if (!Curve25519Supported.Value)
            {
                // Système sans Curve25519 (anciens Windows, Wine) : on laisse le serveur choisir ECDH NIST ou DH.
                foreach (var name in info.KeyExchangeAlgorithms.Keys.Where(k => k.Contains("25519", StringComparison.Ordinal)).ToList())
                {
                    info.KeyExchangeAlgorithms.Remove(name);
                }
            }

            var client = create(info);
            bool keyRefused = false;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = _ui.CheckHostKey(Host, Port, e.HostKeyName, e.FingerPrintSHA256);
                keyRefused = !e.CanTrust;
                DebugLog.Write("ssh", $"{Host}:{Port} : clé d'hôte {e.HostKeyName} SHA256:{e.FingerPrintSHA256}, acceptée {e.CanTrust}");
            };
            DebugLog.Write("ssh", $"{Host}:{Port} : connexion {purpose}, essai {attempt + 1}, méthodes {string.Join(", ", methods.Select(m => m.Name))}");
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                var c = client.ConnectionInfo;
                DebugLog.Write("ssh", $"{Host}:{Port} : {purpose} connecté, serveur « {c.ServerVersion} », échange de clés {c.CurrentKeyExchangeAlgorithm}, "
                    + $"chiffrement {c.CurrentServerEncryption}/{c.CurrentClientEncryption}, compression {c.CurrentServerCompressionAlgorithm}");
                return client;
            }
            catch (SshAuthenticationException ex) when (!cancelled && !keyRefused)
            {
                DebugLog.Write("ssh", $"{Host}:{Port} : authentification refusée : {ex.Message}");
                client.Dispose();
                if (answered)
                {
                    // Réponse refusée : oubliée ici et pour le groupe, qui ne doit pas la réessayer sur ses autres sessions.
                    _answers.Clear();
                    _group?.Clear();
                }

                var allowed = ex.Message;
                bool interactive = allowed.Contains("keyboard-interactive", StringComparison.OrdinalIgnoreCase);
                usePassword = !interactive && allowed.Contains("password", StringComparison.OrdinalIgnoreCase);
                if (!interactive && !usePassword)
                {
                    // Ni question ni mot de passe acceptés (clé seule) : un nouvel essai échouerait de la même façon.
                    throw;
                }

                if (answered && ++refusals >= MaxAttempts)
                {
                    throw new SshAuthenticationException(
                        string.Format(CultureInfo.CurrentCulture, CoreStrings.AuthenticationRefusedFinal, MaxAttempts), ex);
                }

                if (attempt >= MaxAttempts)
                {
                    // Garde-fou : refus répétés sans qu'aucune question n'ait été posée.
                    throw;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("ssh", $"{Host}:{Port} : échec de la connexion {purpose}", ex);
                client.Dispose();
                if (keyRefused)
                {
                    // Refus de l'utilisateur (ou clé changée non confirmée) : pas une panne, une annulation.
                    throw new HostKeyRefusedException(CoreStrings.HostKeyRefused);
                }

                if (cancelled)
                {
                    throw new OperationCanceledException(CoreStrings.AuthenticationCancelled);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Types de clé d'hôte déjà acceptés pour ce serveur proposés en premier, comme OpenSSH : un serveur qui a plusieurs
    /// clés présente celle déjà connue plutôt qu'une nouvelle à faire vérifier (l'ordre des autres ne change pas).
    /// </summary>
    internal static void PreferKnownHostKeys<TValue>(IOrderedDictionary<string, TValue> algorithms, IReadOnlyCollection<string> knownTypes)
    {
        int position = 0;
        foreach (var name in algorithms.Keys.ToList())
        {
            if (knownTypes.Contains(KnownHosts.KeyType(name)))
            {
                algorithms.SetPosition(name, position++);
            }
        }
    }

    /// <summary>
    /// Le mot de passe est réutilisé pour les connexions suivantes de la même session (SFTP, SCP) tant qu'il est
    /// accepté, et pour les autres sessions d'un groupe si l'utilisateur l'a choisi ; les codes à usage unique (MFA) sont
    /// toujours redemandés.
    /// </summary>
    private string? Answer(string instruction, string request, bool echo, string? refused)
    {
        bool cacheable = !echo && IsPasswordPrompt(request);
        if (cacheable && _answers.TryGet(request, out var cached))
        {
            return cached;
        }

        if (!cacheable || _group is not { } group)
        {
            var answer = _ui.Prompt(new SshQuestion(instruction, request, echo, refused))?.Text;
            if (answer is not null && cacheable)
            {
                _answers.Set(request, answer);
            }

            return answer;
        }

        // Groupe : une question à la fois ; la réponse partagée par une session précédente sert sans redemander.
        group.Gate.Wait();
        try
        {
            if (group.TryGet(request, out var shared))
            {
                _answers.Set(request, shared);
                return shared;
            }

            var answer = _ui.Prompt(new SshQuestion(instruction, request, echo, refused, OfferShare: true));
            if (answer is not null)
            {
                _answers.Set(request, answer.Text);
                if (answer.Share)
                {
                    group.Set(request, answer.Text);
                }
            }

            return answer?.Text;
        }
        finally
        {
            group.Gate.Release();
        }
    }

    private static readonly Lazy<bool> Curve25519Supported = new(() =>
    {
        try
        {
            using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(
                System.Security.Cryptography.ECCurve.CreateFromFriendlyName("Curve25519"));
            ecdh.ExportParameters(includePrivateParameters: false);
            return true;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    });

    public static bool IsPasswordPrompt(string request) =>
        request.Contains("password", StringComparison.OrdinalIgnoreCase)
        || request.Contains("mot de passe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>La clé d'hôte présentée n'a pas été acceptée : la connexion est annulée avant toute authentification.</summary>
public sealed class HostKeyRefusedException(string message) : OperationCanceledException(message);
