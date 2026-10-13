using System.Text;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.Core;

/// <summary>
/// « Mark-of-the-Web » : flux Zone.Identifier (zone Internet, ZoneId=3) écrit sur un fichier venu d'un serveur ou
/// d'Internet, pour que Windows et les logiciels bureautiques le traitent comme téléchargé (SmartScreen avant de lancer
/// un exécutable, macros bloquées, mode protégé) et non comme un fichier créé sur le poste. Sans effet hors de Windows,
/// et sans erreur sur un disque sans flux de données alternatifs (FAT, exFAT, certains partages).
/// </summary>
public static class MarkOfTheWeb
{
    /// <summary>Zone Internet (URLZONE_INTERNET).</summary>
    public const int InternetZone = 3;

    private const string Stream = ":Zone.Identifier";

    /// <summary>Marque <paramref name="path"/> ; vrai si la marque a été écrite.</summary>
    /// <param name="hostUrl">
    /// Adresse publique (https) d'où vient le fichier, notée comme le fait un navigateur. Jamais celle d'un serveur
    /// interne : SmartScreen peut l'envoyer à Microsoft avec l'empreinte du fichier.
    /// </param>
    public static bool Apply(string path, Uri? hostUrl = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var content = new StringBuilder("[ZoneTransfer]\r\nZoneId=").Append(InternetZone).Append("\r\n");
        if (hostUrl is { IsAbsoluteUri: true } && hostUrl.Scheme == Uri.UriSchemeHttps)
        {
            // AbsoluteUri : caractères de contrôle et non ASCII échappés, rien ne peut ajouter de ligne au fichier.
            content.Append("HostUrl=").Append(hostUrl.AbsoluteUri).Append("\r\n");
        }

        try
        {
            File.WriteAllText(path + Stream, content.ToString(), Encoding.ASCII);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            DebugLog.Write("files", $"Mark-of-the-Web non écrit sur {path} : {e.Message}");
            return false;
        }
    }

    /// <summary>Contenu du flux Zone.Identifier de <paramref name="path"/> ; null s'il n'y en a pas, ou hors de Windows.</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path + Stream, Encoding.ASCII);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
