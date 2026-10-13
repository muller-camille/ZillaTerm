using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.Core;

/// <summary>
/// Révocation des certificats des serveurs HTTPS (PVWA, GitHub pour les mises à jour), vérifiée en « échec permissif ».
/// La validation habituelle (certificat approuvé par Windows, nom du serveur) reste la première juge ; si elle accepte,
/// la révocation est vérifiée en ligne (liste de révocation ou OCSP indiquées par le certificat). Un certificat révoqué
/// est refusé. Un serveur de révocation injoignable (réseau fermé, proxy) ne bloque pas la connexion : c'est noté dans le
/// journal de débogage. Le bloquer couperait l'accès au PVWA de tout un réseau sans accès aux listes de révocation, sans
/// protéger d'un intermédiaire qui peut lui-même rendre ces listes injoignables.
/// </summary>
public static class TlsRevocation
{
    /// <summary>Attente au plus du serveur de révocation pour une connexion (Windows garde ensuite ses réponses en cache).</summary>
    internal static readonly TimeSpan RetrievalTimeout = TimeSpan.FromSeconds(5);

    private const X509ChainStatusFlags Unknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    /// <summary>
    /// Rappel de validation du certificat d'un serveur HTTPS. <paramref name="errors"/> est le verdict habituel de .NET :
    /// une erreur refuse le certificat, comme sans ce rappel.
    /// </summary>
    /// <param name="server">Serveur, pour le journal de débogage.</param>
    public static bool Validate(string? server, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None)
        {
            return false;
        }

        if (certificate is not X509Certificate2 leaf)
        {
            return true;
        }

        X509ChainStatusFlags status = X509ChainStatusFlags.NoError;
        try
        {
            using var check = new X509Chain();
            check.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            check.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            check.ChainPolicy.UrlRetrievalTimeout = RetrievalTimeout;
            // Certificats intermédiaires envoyés par le serveur : la chaîne se reconstruit sans les télécharger.
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    check.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            check.Build(leaf);
            foreach (var element in check.ChainElements)
            {
                foreach (var s in element.ChainElementStatus)
                {
                    status |= s.Status;
                }
            }

            foreach (var s in check.ChainStatus)
            {
                status |= s.Status;
            }
        }
        catch (CryptographicException e)
        {
            DebugLog.Write("tls", $"{server} : révocation non vérifiée ({e.Message})");
            return true;
        }

        return Accept(server, status);
    }

    /// <summary>Verdict sur l'état de révocation d'une chaîne déjà acceptée par la validation habituelle.</summary>
    internal static bool Accept(string? server, X509ChainStatusFlags status)
    {
        if (status.HasFlag(X509ChainStatusFlags.Revoked))
        {
            DebugLog.Write("tls", $"{server} : certificat révoqué, connexion refusée.");
            return false;
        }

        if ((status & Unknown) != 0)
        {
            DebugLog.Write("tls", $"{server} : révocation non vérifiée (serveur de révocation injoignable), connexion acceptée.");
        }

        return true;
    }
}
