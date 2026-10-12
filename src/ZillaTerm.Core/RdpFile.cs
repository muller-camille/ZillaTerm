using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Rdp;

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

    /// <summary>
    /// Fichier .rdp qui partage tous les disques du poste avec la session (« drivestoredirect:s:* ») : la ligne existante
    /// est remplacée, sinon ajoutée ; le reste du fichier, son encodage et ses fins de ligne sont gardés. Un fichier signé
    /// est rendu tel quel : la Connexion Bureau à distance vérifie sa signature, qu'un changement invaliderait.
    /// </summary>
    public static byte[] WithAllDrives(byte[] content)
    {
        if (IsSigned(content))
        {
            return content;
        }

        var (encoding, bom) = content switch
        {
            [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
            [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
            [0xEF, 0xBB, 0xBF, ..] => (Encoding.UTF8, 3),
            _ => (Encoding.UTF8, 0),
        };
        var text = encoding.GetString(content, bom, content.Length - bom);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        const string drives = "drivestoredirect:s:*";
        int found = lines.FindIndex(l => l.IndexOf(':') is > 0 and var colon
            && l[..colon].Trim().Equals("drivestoredirect", StringComparison.OrdinalIgnoreCase));
        if (found >= 0)
        {
            lines[found] = drives;
        }
        else
        {
            // Avant la ligne vide qui suit la dernière fin de ligne.
            lines.Insert(lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count, drives);
        }

        return [.. content.AsSpan(0, bom), .. encoding.GetBytes(string.Join(newline, lines))];
    }

    /// <summary>Vrai si le fichier .rdp porte une signature (ligne « signature » non vide).</summary>
    public static bool IsSigned(byte[] content) =>
        RdpConnectionSettings.ReadFile(content).TryGetValue("signature", out var signature) && signature.Trim().Length > 0;

    private static string Decode(byte[] body)
    {
        using var reader = new StreamReader(new MemoryStream(body), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
