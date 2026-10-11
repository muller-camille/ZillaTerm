namespace ZillaTerm.Core.Rdp;

/// <summary>Fin d'une session FreeRDP, d'après le code de sa dernière erreur (FREERDP_ERROR_*, include/freerdp/error.h).</summary>
public enum FreeRdpEnd
{
    /// <summary>Fin normale : fermée dans ZillaTerm, déconnexion ou fermeture de session par l'utilisateur.</summary>
    Normal,

    /// <summary>Session reprise depuis une autre connexion.</summary>
    TakenOver,

    /// <summary>Fermée par le serveur : inactivité, ouverture de session trop longue.</summary>
    Timeout,

    /// <summary>Connexion refusée par le serveur (droits, licence, politique).</summary>
    Denied,

    /// <summary>Nom du serveur introuvable.</summary>
    NameNotFound,

    /// <summary>Serveur injoignable ou connexion coupée.</summary>
    Unreachable,

    /// <summary>Connexion sécurisée (TLS) impossible, ou certificat refusé.</summary>
    Tls,

    /// <summary>Pas de sécurité commune (authentification réseau, TLS).</summary>
    Negotiation,

    /// <summary>Identifiant ou mot de passe refusé, ou absent.</summary>
    Authentication,

    /// <summary>Compte bloqué, expiré, désactivé, mot de passe expiré ou à changer.</summary>
    Account,

    /// <summary>Accès refusé (ouverture de session non autorisée sur ce serveur).</summary>
    AccessDenied,

    /// <summary>Autre erreur : son nom FreeRDP est montré.</summary>
    Other,
}

public static class FreeRdpExit
{
    private const uint ErrInfo = 0x0001_0000;
    private const uint Connect = 0x0002_0000;

    // Noms des codes de connexion (ERRCONNECT_*, include/freerdp/error.h), dans l'ordre de leur valeur.
    private static readonly string[] ConnectNames =
    [
        "SUCCESS", "PRE_CONNECT_FAILED", "CONNECT_UNDEFINED", "POST_CONNECT_FAILED", "DNS_ERROR", "DNS_NAME_NOT_FOUND",
        "CONNECT_FAILED", "MCS_CONNECT_INITIAL_ERROR", "TLS_CONNECT_FAILED", "AUTHENTICATION_FAILED",
        "INSUFFICIENT_PRIVILEGES", "CONNECT_CANCELLED", "SECURITY_NEGO_CONNECT_FAILED", "CONNECT_TRANSPORT_FAILED",
        "PASSWORD_EXPIRED", "PASSWORD_CERTAINLY_EXPIRED", "CLIENT_REVOKED", "KDC_UNREACHABLE", "ACCOUNT_DISABLED",
        "PASSWORD_MUST_CHANGE", "LOGON_FAILURE", "WRONG_PASSWORD", "ACCESS_DENIED", "ACCOUNT_RESTRICTION",
        "ACCOUNT_LOCKED_OUT", "ACCOUNT_EXPIRED", "LOGON_TYPE_NOT_GRANTED", "NO_OR_MISSING_CREDENTIALS",
        "ACTIVATION_TIMEOUT", "TARGET_BOOTING", "HYBRID_REQUIRED_BY_SERVER",
    ];

    /// <summary>
    /// Code de fin du client FreeRDP (code de sortie du processus : sa dernière erreur), ou null pour un autre code
    /// (plantage, arrêt forcé).
    /// </summary>
    public static uint? FromExitCode(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        uint type = code >> 16;
        return code == 0 || (type is 1 or 2 && (code & 0xFFFF) <= 0x7FFF) ? code : null;
    }

    /// <summary>Nom FreeRDP d'un code (ERRCONNECT_… ; ERRINFO_ et code pour les autres).</summary>
    public static string Name(uint code) => (code >> 16) switch
    {
        2 when (code & 0xFFFF) < (uint)ConnectNames.Length => "ERRCONNECT_" + ConnectNames[code & 0xFFFF],
        1 => FormattableString.Invariant($"ERRINFO_0x{code & 0xFFFF:X4}"),
        _ => "",
    };

    /// <summary>Classe le code de fin d'une session ; vrai pour une fin normale.</summary>
    public static (bool Normal, FreeRdpEnd End) Classify(uint code)
    {
        var end = code switch
        {
            0 => FreeRdpEnd.Normal,
            // ERRINFO_RPC_INITIATED_DISCONNECT, _LOGOFF, _DISCONNECT_BY_USER, ERRINFO_LOGOFF_BY_USER
            ErrInfo | 0x01 or ErrInfo | 0x02 or ErrInfo | 0x0B or ErrInfo | 0x0C => FreeRdpEnd.Normal,
            // Abandon demandé par ZillaTerm (onglet fermé, Déconnecter).
            Connect | 0x0B => FreeRdpEnd.Normal,
            ErrInfo | 0x03 or ErrInfo | 0x04 => FreeRdpEnd.Timeout,
            ErrInfo | 0x05 => FreeRdpEnd.TakenOver,
            ErrInfo | 0x07 or ErrInfo | 0x09 or ErrInfo | 0x0A => FreeRdpEnd.Denied,
            Connect | 0x04 or Connect | 0x05 => FreeRdpEnd.NameNotFound,
            Connect | 0x06 or Connect | 0x0D => FreeRdpEnd.Unreachable,
            Connect | 0x08 => FreeRdpEnd.Tls,
            Connect | 0x0C or Connect | 0x1E => FreeRdpEnd.Negotiation,
            Connect | 0x09 or Connect | 0x14 or Connect | 0x15 or Connect | 0x1B => FreeRdpEnd.Authentication,
            Connect | 0x0E or Connect | 0x0F or Connect | 0x12 or Connect | 0x13 or Connect | 0x17 or Connect | 0x18
                or Connect | 0x19 => FreeRdpEnd.Account,
            Connect | 0x0A or Connect | 0x16 or Connect | 0x1A => FreeRdpEnd.AccessDenied,
            _ => FreeRdpEnd.Other,
        };
        return (end == FreeRdpEnd.Normal, end);
    }
}
