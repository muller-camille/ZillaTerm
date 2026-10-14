using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Services.Secrets;

/// <summary>
/// Protection des secrets liée au compte de l'utilisateur, selon le système : DPAPI sous Windows (comme ZillaTerm.App),
/// trousseau du bureau (Secret Service) sous Linux. macOS n'est pas encore pris en charge : ce qui en dépend reste
/// indisponible, avec un message clair. Jamais de repli vers un fichier, une clé fixe ou l'absence de chiffrement.
/// </summary>
/// <remarks>
/// La création ne touche pas encore au trousseau (rien ne bloque le démarrage) : il est lu au premier usage, ou par
/// <see cref="PrepareAsync"/> que l'interface appelle avant (déverrouillage sans bloquer son fil). Un trousseau absent ou
/// refusé se signale alors par <see cref="SecretProtectorUnavailableException"/> ; <see cref="IsAvailable"/> permet de
/// griser d'avance ce qui en dépend.
/// </remarks>
internal static class SecretProtectors
{
    /// <summary>Délai maximal de la vérification de <see cref="IsAvailable"/>.</summary>
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(5);

    /// <param name="entropy">
    /// Constante publique qui distingue l'usage (« ZillaTerm.InterventionJournal.v1 ») : un bloc d'un usage ne se
    /// déchiffre pas avec un autre ; sous Linux, une clé du trousseau par usage.
    /// </param>
    public static ISecretProtector Create(byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(entropy);
        if (OperatingSystem.IsWindows())
        {
            return new DpapiProtector(entropy);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new UnsupportedProtector();
        }

        return new KeyringProtector(entropy);
    }

    /// <summary>
    /// Protection du coffre local des mots de passe maîtres KeePass : DPAPI avec l'entropie d'origine sous Windows (les
    /// coffres existants restent lisibles), clé « ZillaTerm.LocalSecretStore.v1 » du trousseau ailleurs.
    /// </summary>
    public static ISecretProtector CreateForLocalStore() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : Create("ZillaTerm.LocalSecretStore.v1"u8.ToArray());

    /// <summary>
    /// La protection peut servir sur ce poste ; sinon <paramref name="reason"/> dit pourquoi (texte traduit, à montrer en
    /// infobulle de ce qui est grisé). Sous Linux : bus de session présent et service Secret Service enregistré dessus
    /// (en marche ou démarrable) ; son déverrouillage n'est demandé qu'au premier usage. Peut attendre le bus jusqu'à
    /// 5 secondes : depuis le fil de l'interface, préférer <see cref="CheckAvailabilityAsync"/>.
    /// </summary>
    public static bool IsAvailable(out string? reason)
    {
        reason = Task.Run(() => CheckAvailabilityAsync()).GetAwaiter().GetResult();
        return reason is null;
    }

    /// <summary>Comme <see cref="IsAvailable"/>, sans bloquer : null si la protection peut servir, sinon la raison.</summary>
    public static async Task<string?> CheckAvailabilityAsync(CancellationToken cancellation = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Strings.KeyringMacUnsupported;
        }

        if (SecretServiceClient.SessionBusAddress is null)
        {
            return Strings.KeyringUnavailable;
        }

        try
        {
            return await IsKeyringRegisteredAsync(cancellation).WaitAsync(AvailabilityTimeout, cancellation).ConfigureAwait(false)
                ? null
                : Strings.KeyringUnavailable;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return KeyringProtector.Translate(e).Message;
        }
    }

    private static async Task<bool> IsKeyringRegisteredAsync(CancellationToken cancellation)
    {
        await using var client = await SecretServiceClient.ConnectAsync(AvailabilityTimeout, AvailabilityTimeout, cancellation).ConfigureAwait(false);
        return await client.IsServiceRegisteredAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Lit d'avance la clé de <paramref name="protector"/> (trousseau déverrouillé avec sa fenêtre au besoin), sans bloquer
    /// le fil appelant ; rien à faire avec DPAPI.
    /// </summary>
    /// <param name="create">Faux s'il y a déjà des données à relire : une clé introuvable est alors signalée perdue.</param>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, refusé, muet, clé perdue, ou macOS.</exception>
    public static Task PrepareAsync(ISecretProtector protector, bool create, CancellationToken cancellation = default) => protector switch
    {
        KeyringProtector keyring => keyring.PrepareAsync(create, cancellation),
        UnsupportedProtector => Task.FromException(KeyringProtector.Unavailable(SecretProtectorFailure.NotSupported, null)),
        _ => Task.CompletedTask,
    };

    /// <summary>Système sans protection des secrets (macOS pour l'instant) : tout usage est refusé, avec la raison.</summary>
    private sealed class UnsupportedProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => throw KeyringProtector.Unavailable(SecretProtectorFailure.NotSupported, null);

        public byte[] Unprotect(byte[] data) => throw KeyringProtector.Unavailable(SecretProtectorFailure.NotSupported, null);
    }
}
