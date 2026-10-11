using System.Globalization;
using System.Text;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Message du client FreeRDP de ZillaTerm sur sa sortie standard (« zillaterm:… », fork muller-camille/FreeRDP, client/Windows/wf_zillaterm.h). Les
/// autres lignes ne sont pas des messages.
/// </summary>
public abstract record FreeRdpMessage
{
    private const string Prefix = "zillaterm:";

    /// <summary>Message de la ligne, ou null (autre ligne, ou message mal formé).</summary>
    public static FreeRdpMessage? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = line[Prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            ["connected", var window] when long.TryParse(window, NumberStyles.None, CultureInfo.InvariantCulture, out var handle) =>
                new FreeRdpConnected(handle),
            ["certificate", var flags, var port, var pem, var host] => Certificate(flags, port, pem, host),
            ["reconnecting", var attempt] when int.TryParse(attempt, NumberStyles.None, CultureInfo.InvariantCulture, out var number) =>
                new FreeRdpReconnecting(number),
            ["ended", var code, ..] when uint.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var value) =>
                new FreeRdpEnded(value, parts.Length > 2 ? Name(parts[2]) : ""),
            _ => null,
        };
    }

    private static FreeRdpCertificateRequest? Certificate(string flags, string port, string pem, string host)
    {
        if (!uint.TryParse(flags, NumberStyles.None, CultureInfo.InvariantCulture, out var flagValue)
            || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var portValue)
            || portValue is < 1 or > 65535)
        {
            return null;
        }

        try
        {
            return new FreeRdpCertificateRequest(flagValue, portValue, Encoding.ASCII.GetString(Convert.FromBase64String(pem)), host);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Nom FREERDP_ERROR_… : lettres, chiffres et soulignés, 80 au plus (rien d'autre ne passe dans un message).
    private static string Name(string text) =>
        new(text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(80).ToArray());
}

/// <summary>Session ouverte ; fenêtre de FreeRDP, enfant de celle de l'onglet.</summary>
public sealed record FreeRdpConnected(long Window) : FreeRdpMessage;

/// <summary>
/// Certificat TLS présenté pendant la connexion (serveur, redirection ou passerelle) : FreeRDP attend « accept » ou
/// « deny » sur son entrée standard.
/// </summary>
/// <param name="Pem">Chaîne de certificats du serveur, au format PEM.</param>
public sealed record FreeRdpCertificateRequest(uint Flags, int Port, string Pem, string Host) : FreeRdpMessage
{
    /// <summary>Certificat de la passerelle Bureau à distance (VERIFY_CERT_FLAG_GATEWAY).</summary>
    public bool IsGateway => (Flags & 0x20) != 0;

    /// <summary>Serveur vers lequel la connexion a été redirigée (VERIFY_CERT_FLAG_REDIRECT).</summary>
    public bool IsRedirect => (Flags & 0x10) != 0;
}

/// <summary>Fin du client : code de la dernière erreur (0 si aucune) et son nom FREERDP_ERROR_….</summary>
public sealed record FreeRdpEnded(uint Code, string Name) : FreeRdpMessage;

/// <summary>Connexion coupée : tentative de reconnexion automatique (la première est 1).</summary>
public sealed record FreeRdpReconnecting(int Attempt) : FreeRdpMessage;
