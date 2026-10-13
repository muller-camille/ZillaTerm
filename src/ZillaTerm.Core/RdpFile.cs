using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core;

/// <summary>Validation du fichier RDP renvoyé par <c>PSMConnect</c>.</summary>
public static class RdpFile
{
    /// <summary>
    /// Renvoie le contenu à écrire dans le fichier .rdp. Selon la version, le PVWA renvoie le fichier brut
    /// ou une chaîne JSON qui le contient ; on refuse tout le reste (page d'erreur, données PSM Gateway...).
    /// </summary>
    public static byte[] FromPsmConnectResponse(byte[] body, string? mediaType)
    {
        var text = Decode(body).Trim();
        if (text.StartsWith('"'))
        {
            try
            {
                text = JsonSerializer.Deserialize<string>(text) ?? "";
                body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
            }
            catch (JsonException)
            {
                text = "";
            }
        }
        else if (text.StartsWith('{'))
        {
            var hint = text.Contains("PSMGW", StringComparison.OrdinalIgnoreCase)
                ? CoreStrings.RdpGatewayInstead
                : string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpUnexpected, "");
            throw new PvwaException(HttpStatusCode.OK, null, hint);
        }

        if (!text.Contains("full address", StringComparison.OrdinalIgnoreCase))
        {
            var kind = mediaType is null ? "" : $" ({mediaType})";
            throw new PvwaException(HttpStatusCode.OK, null, string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpUnexpected, kind));
        }

        return body;
    }

    private static string Decode(byte[] body)
    {
        using var reader = new StreamReader(new MemoryStream(body), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
