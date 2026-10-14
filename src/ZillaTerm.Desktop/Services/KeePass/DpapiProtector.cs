using System.Security.Cryptography;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Services.KeePass;

/// <summary>
/// DPAPI (compte Windows courant) : le fichier du coffre local est inutilisable sur un autre compte ou poste.
/// <see cref="ProtectedData"/> vient du paquet System.Security.Cryptography.ProtectedData (Windows seulement).
/// </summary>
/// <remarks>
/// Sous Linux et macOS, cette protection passerait par le trousseau du système, pas encore relié : <see cref="IsAvailable"/>
/// est faux, et les fonctions qui en ont besoin (mémoriser un mot de passe maître KeePass) n'apparaissent pas. Rien n'est
/// alors écrit sans cette protection : <see cref="Protect"/> et <see cref="Unprotect"/> refusent.
/// </remarks>
internal sealed class DpapiProtector : ISecretProtector
{
    // Ancien nom de l'application gardé : les secrets déjà protégés doivent rester lisibles.
    private static readonly byte[] LocalStoreEntropy = "CyberArkTerm.LocalSecretStore.v1"u8.ToArray();

    private readonly byte[] _entropy;

    /// <summary>Protection du coffre local.</summary>
    public DpapiProtector()
        : this(LocalStoreEntropy)
    {
    }

    /// <param name="entropy">Distingue les usages : un bloc d'un usage ne se déchiffre pas avec un autre.</param>
    public DpapiProtector(byte[] entropy) => _entropy = entropy;

    /// <summary>Protection disponible sur ce système (Windows).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(data, _entropy, DataProtectionScope.CurrentUser)
        : throw Unavailable();

    public byte[] Unprotect(byte[] data) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(data, _entropy, DataProtectionScope.CurrentUser)
        : throw Unavailable();

    private static PlatformNotSupportedException Unavailable() =>
        new("Protection DPAPI disponible sous Windows seulement : trousseau du système non relié sous Linux et macOS.");
}
