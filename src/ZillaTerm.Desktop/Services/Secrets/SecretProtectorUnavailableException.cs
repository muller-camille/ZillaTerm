namespace ZillaTerm.App.Services.Secrets;

/// <summary>Pourquoi la protection des secrets (DPAPI sous Windows, trousseau ailleurs) ne peut pas servir.</summary>
internal enum SecretProtectorFailure
{
    /// <summary>Système pas encore pris en charge (macOS).</summary>
    NotSupported,

    /// <summary>Pas de bus de session D-Bus, ou aucun service Secret Service (org.freedesktop.secrets) dessus.</summary>
    NoKeyring,

    /// <summary>Déverrouillage du trousseau (ou accès à la clé) refusé, ou laissé sans réponse jusqu'au délai.</summary>
    UnlockRefused,

    /// <summary>Le trousseau n'a pas répondu à temps.</summary>
    NoResponse,

    /// <summary>
    /// Clé de cet usage introuvable dans le trousseau alors qu'il fallait relire des données protégées avec elle : elle
    /// n'est pas recréée en silence (les anciennes données seraient illisibles sans le dire).
    /// </summary>
    KeyLost,

    /// <summary>Réponse inattendue ou erreur du trousseau.</summary>
    Failed,
}

/// <summary>
/// La protection des secrets ne peut pas servir : rien n'est chiffré ni déchiffré, jamais de repli vers un fichier, une clé
/// fixe ou l'absence de chiffrement. Le message est traduit et peut être montré tel quel.
/// </summary>
/// <remarks>
/// Ne dérive pas de <see cref="System.Security.Cryptography.CryptographicException"/> : un trousseau absent ou verrouillé ne
/// doit pas passer pour un bloc abîmé (le journal d'intervention et le coffre local traitent ceux-là à part).
/// </remarks>
internal sealed class SecretProtectorUnavailableException : Exception
{
    public SecretProtectorUnavailableException(SecretProtectorFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public SecretProtectorFailure Failure { get; }
}
