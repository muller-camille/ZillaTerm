using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Tmds.DBus.Protocol;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Services.Secrets;

/// <summary>
/// Équivalent de DPAPI hors de Windows, lié au compte de l'utilisateur par le trousseau de son bureau (API D-Bus Secret
/// Service : GNOME Keyring, KWallet récent, KeePassXC…). Une clé aléatoire de 256 bits par usage (l'entropie distingue
/// les usages) est créée au premier besoin et gardée dans le trousseau, jamais sur disque ; les données sont chiffrées en
/// AES-256-GCM avec elle.
/// </summary>
/// <remarks>
/// <para>
/// Format d'un bloc protégé : 1 octet de version (1), nonce aléatoire (12 octets), étiquette GCM (16 octets), données
/// chiffrées. Données associées : l'octet de version puis l'entropie (un bloc d'un usage ne se déchiffre pas avec un
/// autre). Un bloc altéré, tronqué ou d'une autre clé est refusé (<see cref="CryptographicException"/>, comme DPAPI).
/// </para>
/// <para>
/// Échec fermé : sans trousseau, trousseau verrouillé et déverrouillage refusé, ou clé introuvable au moment de relire
/// des données, <see cref="SecretProtectorUnavailableException"/> ; jamais de repli vers un fichier, une clé fixe ou
/// l'absence de chiffrement. La clé n'est recréée que pour protéger (<see cref="Protect"/>), jamais pour relire.
/// </para>
/// <para>
/// La clé lue reste en mémoire (tableau épinglé) le temps du processus, effacée par <see cref="Dispose"/> ou à la
/// fermeture. Le premier usage lit le trousseau, ce qui peut montrer sa fenêtre de déverrouillage : l'interface appelle
/// <see cref="PrepareAsync"/> avant (rien ne bloque alors son fil, et un refus se signale là où elle sait le montrer) ;
/// sinon <see cref="Protect"/> et <see cref="Unprotect"/> attendent le trousseau sur un autre fil. Le journal
/// d'intervention, qui protège ses blocs depuis un minuteur, ne s'attend qu'aux erreurs de disque : sa clé doit être lue
/// avant de commencer ou de reprendre un enregistrement.
/// </para>
/// </remarks>
internal sealed class KeyringProtector : ISecretProtector, IDisposable
{
    public const byte FormatVersion = 1;
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HeaderSize = 1 + NonceSize + TagSize;

    /// <summary>Attribut des éléments du trousseau créés par ZillaTerm.</summary>
    public const string ApplicationName = "ZillaTerm";

    /// <summary>Délai maximal de chaque appel au trousseau (démarrage du service compris).</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Délai laissé à l'utilisateur pour répondre à la fenêtre de déverrouillage du trousseau.</summary>
    public static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromMinutes(2);

    // Une lecture ou création de clé à la fois par usage, dans tout le processus : deux protections du même usage ne
    // créent pas chacune leur clé.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    private readonly byte[] _associatedData;
    private readonly Dictionary<string, string> _attributes;
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _callTimeout;
    private readonly TimeSpan _promptTimeout;
    private readonly object _keyLock = new();
    private byte[]? _key;
    private bool _disposed;

    /// <param name="entropy">Distingue les usages (constante publique de l'usage, comme pour DPAPI).</param>
    public KeyringProtector(byte[] entropy)
        : this(entropy, DefaultCallTimeout, DefaultPromptTimeout)
    {
    }

    /// <param name="callTimeout">Délai maximal de chaque appel au trousseau.</param>
    /// <param name="promptTimeout">Délai laissé pour répondre à la fenêtre de déverrouillage.</param>
    public KeyringProtector(byte[] entropy, TimeSpan callTimeout, TimeSpan promptTimeout)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        Purpose = PurposeOf(entropy);
        _associatedData = [FormatVersion, .. entropy];
        _attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application"] = ApplicationName,
            ["purpose"] = Purpose,
        };
        _gate = Gates.GetOrAdd(Purpose, _ => new SemaphoreSlim(1, 1));
        _callTimeout = callTimeout;
        _promptTimeout = promptTimeout;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Nom de l'usage dans le trousseau (attribut « purpose »), tiré de l'entropie.</summary>
    public string Purpose { get; }

    /// <summary>Libellé de l'élément du trousseau.</summary>
    public string Label => $"{ApplicationName} ({Purpose})";

    /// <summary>La clé est lue : protéger et relire ne passent plus par le trousseau.</summary>
    public bool HasKey
    {
        get
        {
            lock (_keyLock)
            {
                return _key is not null;
            }
        }
    }

    /// <summary>
    /// Nom d'usage stable : l'entropie elle-même si c'est un identifiant (lettres, chiffres, « . », « - », « _ »), sinon
    /// une empreinte SHA-256 qui ne la révèle pas.
    /// </summary>
    public static string PurposeOf(byte[] entropy)
    {
        bool identifier = entropy.Length is > 0 and <= 64
            && entropy.All(b => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'.' or (byte)'-' or (byte)'_');
        return identifier ? Encoding.ASCII.GetString(entropy) : "sha256-" + Convert.ToHexStringLower(SHA256.HashData(entropy))[..32];
    }

    /// <summary>
    /// Lit la clé de cet usage dans le trousseau (déverrouillage avec sa fenêtre au besoin, sans bloquer l'appelant), et
    /// la crée si elle n'existe pas et que <paramref name="create"/> le permet.
    /// </summary>
    /// <param name="create">
    /// Faux s'il y a déjà des données protégées à relire : une clé introuvable est alors une clé perdue.
    /// </param>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, verrouillé, muet, ou clé perdue.</exception>
    public async Task PrepareAsync(bool create, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasKey)
        {
            return;
        }

        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (HasKey)
            {
                return;
            }

            var key = await ReadKeyAsync(create, cancellation).ConfigureAwait(false);
            lock (_keyLock)
            {
                if (_disposed)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _key = key;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var aes = CreateCipher(create: true);
        var result = new byte[HeaderSize + data.Length];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, data, result.AsSpan(HeaderSize), result.AsSpan(1 + NonceSize, TagSize), _associatedData);
        return result;
    }

    /// <exception cref="CryptographicException">Bloc altéré, tronqué, d'un autre usage ou d'une autre clé.</exception>
    public byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        // Bloc mal formé : refusé sans même lire le trousseau.
        if (data.Length < HeaderSize || data[0] != FormatVersion)
        {
            throw new CryptographicException("Invalid protected data.");
        }

        using var aes = CreateCipher(create: false);
        var plain = new byte[data.Length - HeaderSize];
        try
        {
            aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(HeaderSize), data.AsSpan(1 + NonceSize, TagSize), plain, _associatedData);
            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_keyLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearKey();
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }

    /// <summary>Exception à montrer pour cette raison (message traduit).</summary>
    internal static SecretProtectorUnavailableException Unavailable(SecretProtectorFailure failure, Exception? inner, string? detail = null) =>
        new(failure, failure switch
        {
            SecretProtectorFailure.NotSupported => Strings.KeyringMacUnsupported,
            SecretProtectorFailure.NoKeyring => Strings.KeyringUnavailable,
            SecretProtectorFailure.UnlockRefused => Strings.KeyringUnlockRefused,
            SecretProtectorFailure.NoResponse => Strings.KeyringNoResponse,
            SecretProtectorFailure.KeyLost => Strings.KeyringKeyLost,
            _ => Text.Format(Strings.KeyringFailed, detail ?? inner?.Message ?? "?"),
        }, inner);

    /// <summary>Erreur du bus ou du trousseau traduite en raison montrable (aucun secret dans ces messages).</summary>
    internal static SecretProtectorUnavailableException Translate(Exception e) => e switch
    {
        SecretProtectorUnavailableException u => u,
        TimeoutException => Unavailable(SecretProtectorFailure.NoResponse, e),
        DBusErrorReplyException { ErrorName: "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner"
            or "org.freedesktop.DBus.Error.Spawn.ChildExited" or "org.freedesktop.DBus.Error.Spawn.ExecFailed" } => Unavailable(SecretProtectorFailure.NoKeyring, e),
        DBusErrorReplyException { ErrorName: "org.freedesktop.DBus.Error.NoReply" or "org.freedesktop.DBus.Error.Timeout" } => Unavailable(SecretProtectorFailure.NoResponse, e),
        DBusErrorReplyException { ErrorName: "org.freedesktop.Secret.Error.IsLocked" } => Unavailable(SecretProtectorFailure.UnlockRefused, e),
        DBusErrorReplyException r => Unavailable(SecretProtectorFailure.Failed, e, r.ErrorName),
        DBusConnectionException => Unavailable(SecretProtectorFailure.NoKeyring, e),
        _ => Unavailable(SecretProtectorFailure.Failed, e, e.GetType().Name),
    };

    /// <summary>AES-GCM avec la clé de cet usage ; la lit (ou la crée) d'abord dans le trousseau au besoin.</summary>
    private AesGcm CreateCipher(bool create)
    {
        while (true)
        {
            lock (_keyLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_key is { } key)
                {
                    // AesGcm garde sa propre copie de la clé : Dispose peut effacer la nôtre pendant le chiffrement.
                    return new AesGcm(key, TagSize);
                }
            }

            // Trousseau lu sur un autre fil : rien à reprendre sur le fil appelant (fil de l'interface), pas d'interblocage.
            Task.Run(() => PrepareAsync(create)).GetAwaiter().GetResult();
        }
    }

    private async Task<byte[]> ReadKeyAsync(bool create, CancellationToken cancellation)
    {
        try
        {
            await using var client = await SecretServiceClient.ConnectAsync(_callTimeout, _promptTimeout, cancellation).ConfigureAwait(false);
            if (!await client.IsServiceRegisteredAsync(cancellation).ConfigureAwait(false))
            {
                throw Unavailable(SecretProtectorFailure.NoKeyring, null);
            }

            await client.OpenSessionAsync(cancellation).ConfigureAwait(false);
            var item = await FindItemAsync(client, cancellation).ConfigureAwait(false);
            bool created = false;
            if (item is null)
            {
                if (!create)
                {
                    DebugLog.Write("secrets", $"Trousseau : clé « {Purpose} » introuvable, données existantes illisibles (clé non recréée).");
                    throw Unavailable(SecretProtectorFailure.KeyLost, null);
                }

                item = await CreateItemAsync(client, cancellation).ConfigureAwait(false);
                created = true;
            }

            // Relue même juste après sa création : rien n'est chiffré avec une clé que le trousseau n'aurait pas gardée.
            var key = await ReadSecretAsync(client, item, cancellation).ConfigureAwait(false);
            DebugLog.Write("secrets", $"Trousseau : clé « {Purpose} » {(created ? "créée" : "lue")} ({item}, transport {(client.IsEncrypted ? "chiffré" : "en clair sur le bus de session")}).");
            return key;
        }
        catch (Exception e) when (e is not OperationCanceledException and not ObjectDisposedException)
        {
            var unavailable = Translate(e);
            if (ReferenceEquals(unavailable, e))
            {
                DebugLog.Write("secrets", $"Trousseau : clé « {Purpose} » inaccessible ({unavailable.Failure}).");
            }
            else
            {
                DebugLog.Write("secrets", $"Trousseau : clé « {Purpose} » inaccessible ({unavailable.Failure}).", e);
            }

            throw unavailable;
        }
    }

    /// <summary>
    /// Élément de la clé de cet usage, déverrouillé ; null s'il n'y en a pas. Plusieurs (deux créations simultanées) : le
    /// plus ancien, puis le premier chemin, pour que tous les processus retiennent le même.
    /// </summary>
    private async Task<string?> FindItemAsync(SecretServiceClient client, CancellationToken cancellation)
    {
        var (unlocked, locked) = await client.SearchItemsAsync(_attributes, cancellation).ConfigureAwait(false);
        if (unlocked.Length + locked.Length == 0)
        {
            // Trousseau dont les éléments ne se voient qu'une fois ouvert (base KeePassXC verrouillée) : le trousseau par
            // défaut est déverrouillé avant de conclure que la clé n'existe pas.
            var collection = await client.ReadAliasAsync("default", cancellation).ConfigureAwait(false);
            if (collection != SecretServiceClient.NoObject
                && await client.IsLockedAsync(collection, SecretServiceClient.CollectionInterface, cancellation).ConfigureAwait(false))
            {
                await client.UnlockAsync([collection], cancellation).ConfigureAwait(false);
                (unlocked, locked) = await client.SearchItemsAsync(_attributes, cancellation).ConfigureAwait(false);
            }
        }

        string[] all = [.. unlocked, .. locked];
        if (all.Length == 0)
        {
            return null;
        }

        string chosen = all[0];
        if (all.Length > 1)
        {
            var created = new List<(ulong Created, string Path)>();
            foreach (var path in all)
            {
                created.Add((await client.GetCreatedAsync(path, cancellation).ConfigureAwait(false), path));
            }

            chosen = created.OrderBy(c => c.Created).ThenBy(c => c.Path, StringComparer.Ordinal).First().Path;
            DebugLog.Write("secrets", $"Trousseau : {all.Length} clés « {Purpose} », la plus ancienne est gardée ({chosen}).");
        }

        if (!unlocked.Contains(chosen, StringComparer.Ordinal))
        {
            var now = await client.UnlockAsync([chosen], cancellation).ConfigureAwait(false);
            if (!now.Contains(chosen, StringComparer.Ordinal))
            {
                throw Unavailable(SecretProtectorFailure.UnlockRefused, null);
            }
        }

        return chosen;
    }

    /// <summary>Crée la clé de cet usage dans le trousseau par défaut (créé s'il n'existe pas) ; rend l'élément retenu.</summary>
    private async Task<string> CreateItemAsync(SecretServiceClient client, CancellationToken cancellation)
    {
        var collection = await client.ReadAliasAsync("default", cancellation).ConfigureAwait(false);
        if (collection == SecretServiceClient.NoObject)
        {
            // Pas de trousseau par défaut : créé comme le fait libsecret (le trousseau demande son mot de passe).
            collection = await client.CreateCollectionAsync("Default keyring", "default", cancellation).ConfigureAwait(false);
            if (collection == SecretServiceClient.NoObject)
            {
                throw Unavailable(SecretProtectorFailure.UnlockRefused, null);
            }
        }

        if (await client.IsLockedAsync(collection, SecretServiceClient.CollectionInterface, cancellation).ConfigureAwait(false))
        {
            var now = await client.UnlockAsync([collection], cancellation).ConfigureAwait(false);
            if (!now.Contains(collection, StringComparer.Ordinal))
            {
                throw Unavailable(SecretProtectorFailure.UnlockRefused, null);
            }
        }

        // Le secret de l'élément : la clé en base64 (texte), dans des tableaux épinglés effacés aussitôt envoyée.
        var key = GC.AllocateArray<byte>(KeySize, pinned: true);
        var encoded = GC.AllocateArray<byte>(Base64.GetMaxEncodedToUtf8Length(KeySize), pinned: true);
        string item;
        try
        {
            RandomNumberGenerator.Fill(key);
            Base64.EncodeToUtf8(key, encoded, out _, out _);
            item = await client.CreateItemAsync(collection, Label, _attributes, encoded, cancellation).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(encoded);
        }

        if (item == SecretServiceClient.NoObject)
        {
            throw Unavailable(SecretProtectorFailure.UnlockRefused, null);
        }

        // Une autre création simultanée (autre processus) : tous retiennent le même élément.
        return await FindItemAsync(client, cancellation).ConfigureAwait(false) ?? item;
    }

    /// <summary>Clé (32 octets) de cet élément, dans un tableau épinglé.</summary>
    private static async Task<byte[]> ReadSecretAsync(SecretServiceClient client, string item, CancellationToken cancellation)
    {
        var secret = await client.GetSecretAsync(item, cancellation).ConfigureAwait(false);
        var key = GC.AllocateArray<byte>(KeySize, pinned: true);
        try
        {
            // Base64 sans espace ni retour à la ligne, 32 octets ; toute autre valeur (élément modifié à la main) est
            // refusée, jamais remplacée.
            if (Base64.DecodeFromUtf8(secret, key, out int consumed, out int written) != System.Buffers.OperationStatus.Done
                || consumed != secret.Length || written != KeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw Unavailable(SecretProtectorFailure.Failed, null, "invalid key");
            }

            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        lock (_keyLock)
        {
            ClearKey();
        }
    }

    private void ClearKey()
    {
        if (_key is { } key)
        {
            CryptographicOperations.ZeroMemory(key);
            _key = null;
        }
    }
}
