using System.Globalization;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Parcours d'un dossier de ce poste à envoyer ou à archiver. Les liens symboliques et jonctions vers des dossiers ne
/// sont pas suivis, comme au téléchargement d'un dossier du serveur : une jonction peut boucler (vers un dossier parent)
/// ou mener à un dossier interdit (« Mes documents\Ma musique » des anciens profils Windows). Un lien vers un fichier
/// est envoyé avec le contenu de sa cible. Le dossier déposé lui-même est parcouru, même s'il est un lien.
/// </summary>
public static class LocalTree
{
    // Fichiers cachés et système compris, comme avant (Directory.EnumerateFiles sans options) ; erreurs d'accès signalées.
    private static readonly EnumerationOptions Options = new() { AttributesToSkip = 0, RecurseSubdirectories = false };

    /// <summary>Nom sous lequel un fichier ou un dossier déposé arrive sur le serveur : son nom, sans séparateur final.</summary>
    /// <exception cref="ArgumentException">
    /// Racine d'un lecteur ou d'un partage (« E:\ », « \\serveur\partage ») : sans nom, son contenu irait directement dans
    /// le dossier du serveur, et remplacerait sans question ce qui y porte le même nom.
    /// </exception>
    public static string UploadName(string localPath)
    {
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        return name.Length > 0 ? name : throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, CoreStrings.UploadWholeDrive, localPath));
    }

    /// <summary>Fichiers du dossier, liens vers des fichiers compris.</summary>
    public static IEnumerable<FileInfo> Files(DirectoryInfo directory) => directory.EnumerateFiles("*", Options);

    /// <summary>Sous-dossiers à parcourir : ni lien symbolique ni jonction.</summary>
    public static IEnumerable<DirectoryInfo> Directories(DirectoryInfo directory) =>
        directory.EnumerateDirectories("*", Options).Where(d => !IsLink(d));

    /// <summary>Liens symboliques et jonctions vers des dossiers, qui ne sont pas suivis.</summary>
    public static IEnumerable<DirectoryInfo> Links(DirectoryInfo directory) => directory.EnumerateDirectories("*", Options).Where(IsLink);

    /// <summary>Tous les fichiers sous le dossier, sans suivre les liens vers des dossiers.</summary>
    public static IEnumerable<FileInfo> AllFiles(DirectoryInfo directory) => All(directory).OfType<FileInfo>();

    /// <summary>Fichiers et sous-dossiers sous le dossier (chaque dossier avant son contenu), sans suivre les liens.</summary>
    public static IEnumerable<FileSystemInfo> All(DirectoryInfo directory)
    {
        foreach (var file in Files(directory))
        {
            yield return file;
        }

        foreach (var sub in Directories(directory))
        {
            yield return sub;
            foreach (var entry in All(sub))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Lien symbolique ou jonction (point de montage) : un dossier « à la demande » d'un dossier synchronisé, autre point
    /// d'analyse, reste un dossier ordinaire. Un point d'analyse illisible est traité comme un lien (pas suivi).
    /// </summary>
    public static bool IsLink(FileSystemInfo info)
    {
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
