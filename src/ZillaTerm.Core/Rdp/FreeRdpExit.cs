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
