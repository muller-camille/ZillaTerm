using Microsoft.Win32;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Certificat d'un serveur Bureau à distance approuvé pour le contrôle de Windows, comme le fait la case « Ne plus
/// demander de confirmation pour les connexions à cet ordinateur » de Connexion Bureau à distance : empreinte SHA-1
/// (« CertHash ») sous HKCU\Software\Microsoft\Terminal Server Client\Servers\serveur. Le contrôle reconnaît alors
/// ce certificat-là sans afficher son avertissement ; un autre certificat reste signalé, ou refusé.
/// </summary>
internal static class RdpClientTrust
{
    private const string ServersKey = @"Software\Microsoft\Terminal Server Client\Servers";
    private const string CertHash = "CertHash";

    /// <summary>Mémorise le certificat (empreinte SHA-1 en hexadécimal) pour ce serveur.</summary>
    /// <exception cref="ArgumentException">Nom de serveur ou empreinte incorrects.</exception>
    public static void Remember(string server, int port, string sha1)
    {
        var hash = Hash(sha1);
        foreach (var name in KeyNames(server, port))
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{ServersKey}\{name}");
            key.SetValue(CertHash, hash, RegistryValueKind.Binary);
        }
    }

    /// <summary>Vrai si ce certificat est celui mémorisé pour ce serveur.</summary>
    public static bool IsRemembered(string server, int port, string sha1)
    {
        var hash = Hash(sha1);
        return KeyNames(server, port).All(name =>
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ServersKey}\{name}");
            return key?.GetValue(CertHash) is byte[] value && value.AsSpan().SequenceEqual(hash);
        });
    }

    /// <summary>Oublie le certificat mémorisé pour ce serveur (tests).</summary>
    internal static void Forget(string server, int port)
    {
        foreach (var name in KeyNames(server, port))
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ServersKey}\{name}", writable: true);
            key?.DeleteValue(CertHash, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Clés du serveur : son nom tel que donné au contrôle, et « nom:port » pour un autre port que 3389 (les deux formes
    /// selon la version de Windows).
    /// </summary>
    internal static IReadOnlyList<string> KeyNames(string server, int port)
    {
        server = server.Trim();
        if (server.Length == 0 || server.IndexOfAny(['\\', '/', '\0']) >= 0)
        {
            throw new ArgumentException("Nom de serveur incorrect", nameof(server));
        }

        return port is Core.Rdp.RdpConnectionSettings.DefaultPort or <= 0 ? [server] : [server, $"{server}:{port}"];
    }

    private static byte[] Hash(string sha1)
    {
        var hash = Convert.FromHexString(sha1);
        return hash.Length == 20 ? hash : throw new ArgumentException("Empreinte SHA-1 attendue", nameof(sha1));
    }
}
