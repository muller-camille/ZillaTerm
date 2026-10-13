using System.Net;
using System.Net.Http;
using System.Net.Security;
using ZillaTerm.Core;

namespace ZillaTerm.App.Services;

/// <summary>
/// Accès à GitHub pour la recherche de nouvelle version : proxy du système avec le compte Windows, délai court.
/// Aucune requête n'est faite sans action de l'utilisateur ou l'option « au démarrage » des Paramètres.
/// </summary>
internal static class UpdateService
{
    private static HttpClient? _http;

    public static HttpClient Http => _http ??= Create();

    /// <summary>Dernière version trouvée plus récente que celle-ci (pour la barre d'état et « À propos »).</summary>
    public static UpdateInfo? Available { get; set; }

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Certificat révoqué refusé ; serveur de révocation injoignable : connexion acceptée (voir TlsRevocation).
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                    TlsRevocation.Validate((sender as SslStream)?.TargetHostName, certificate, chain, errors),
            },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ZillaTerm/{UpdateChecker.CurrentVersion}");
        return http;
    }
}
