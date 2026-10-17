using System.Text.RegularExpressions;

namespace ZillaTerm.Core.Rdp;

/// <summary>Disques du poste partagés avec les sessions PSM Windows (voir <see cref="AppSettings.DriveSharing"/>).</summary>
public enum DriveSharing
{
    /// <summary>Aucun disque.</summary>
    None,

    /// <summary>Tous les disques du poste.</summary>
    AllDrives,

    /// <summary>
    /// Seulement les dossiers choisis (<see cref="AppSettings.PsmSharedFolders"/>), avec le client FreeRDP de ZillaTerm ;
    /// aucun disque avec Connexion Bureau à distance ou le contrôle Bureau à distance de Windows, qui ne savent partager
    /// que des disques entiers.
    /// </summary>
    Folders,
}

/// <summary>
/// Dossier du poste partagé avec les sessions PSM Windows à la place de tous les disques : nom vu dans la session
/// (<c>\\tsclient\Nom</c>) et chemin sur le poste, complet ou commençant par une variable d'environnement
/// (<c>%USERPROFILE%\Partage</c>), remplacée à chaque connexion.
/// </summary>
public sealed record SharedFolder(string Name, string Path);

/// <summary>Raison du refus d'une liste de dossiers partagés.</summary>
public enum SharedFolderProblem
{
    InvalidName,
    InvalidPath,
    DuplicateName,
    TooMany,
}

/// <summary>Dossiers partagés : vérification des noms et des chemins, et dossiers à partager à la connexion.</summary>
public static partial class SharedFolderRules
{
    /// <summary>Dossiers gardés au plus.</summary>
    public const int MaxFolders = 16;

    /// <summary>Longueur maximale d'un nom.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Longueur maximale d'un chemin.</summary>
    public const int MaxPathLength = 260;

    /// <summary>
    /// Nom acceptable : lettres sans accent, chiffres, « . », « - » et « _ », en commençant par une lettre ou un chiffre.
    /// Le serveur reçoit le nom en ASCII et FreeRDP remplace les espaces et les séparateurs : le nom vu dans la session
    /// est celui saisi.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length is > 0 and <= MaxNameLength && NamePattern().IsMatch(trimmed);
    }

    /// <summary>
    /// Chemin acceptable : complet (<c>C:\…</c>, partage <c>\\serveur\partage\…</c>) ou commençant par une variable
    /// d'environnement (<c>%USERPROFILE%\…</c>) ; jamais relatif, jamais un chemin de périphérique (<c>\\?\</c>,
    /// <c>\\.\</c>), sans caractère interdit dans un chemin Windows ni « , » ou guillemet (ils couperaient l'option du
    /// client FreeRDP).
    /// </summary>
    public static bool IsValidPath(string? path)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length is 0 or > MaxPathLength || !HasPlainCharacters(trimmed))
        {
            return false;
        }

        if (VariablePrefix().Match(trimmed) is { Success: true } variable)
        {
            var rest = trimmed[variable.Length..];
            return (rest.Length == 0 || rest[0] is '\\' or '/') && !rest.Contains(':', StringComparison.Ordinal);
        }

        return IsFullPath(trimmed);
    }

    /// <summary>Première erreur de la liste (nom, chemin, doublon de nom sans tenir compte des majuscules, nombre), ou null.</summary>
    public static (SharedFolderProblem Problem, string Detail)? Check(IReadOnlyList<SharedFolder?> folders)
    {
        if (folders.Count > MaxFolders)
        {
            return (SharedFolderProblem.TooMany, folders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (folder is null || !IsValidName(folder.Name))
            {
                return (SharedFolderProblem.InvalidName, Short(folder?.Name));
            }

            if (!IsValidPath(folder.Path))
            {
                return (SharedFolderProblem.InvalidPath, $"{folder.Name.Trim()} : {Short(folder.Path)}");
            }

            if (!names.Add(folder.Name.Trim()))
            {
                return (SharedFolderProblem.DuplicateName, folder.Name.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// Dossiers des réglages ou d'un fichier, nettoyés : noms et chemins valides seulement, sans espaces autour, sans
    /// doublon de nom, <see cref="MaxFolders"/> au plus.
    /// </summary>
    public static List<SharedFolder> Sanitize(IEnumerable<SharedFolder?>? folders)
    {
        var result = new List<SharedFolder>();
        foreach (var folder in folders ?? [])
        {
            if (folder is null || !IsValidName(folder.Name) || !IsValidPath(folder.Path)
                || result.Any(f => string.Equals(f.Name, folder.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new SharedFolder(folder.Name.Trim(), folder.Path.Trim()));
            if (result.Count == MaxFolders)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Dossiers à partager à une connexion : variables d'environnement remplacées (<paramref name="expand"/>), chemin
    /// complet et présent sur le poste (<paramref name="exists"/>). Les autres (dossier absent, variable inconnue, chemin
    /// devenu invalide une fois la variable remplacée) ne sont pas partagés, et rendus à part pour le dire.
    /// </summary>
    public static (List<SharedFolder> Shared, List<SharedFolder> Missing) Resolve(IEnumerable<SharedFolder> folders,
        Func<string, string> expand, Func<string, bool> exists)
    {
        var shared = new List<SharedFolder>();
        var missing = new List<SharedFolder>();
        foreach (var folder in Sanitize(folders))
        {
            var path = expand(folder.Path).Trim();
            if (path.Length <= MaxPathLength && HasPlainCharacters(path) && IsFullPath(path) && exists(path))
            {
                shared.Add(folder with { Path = path });
            }
            else
            {
                missing.Add(folder);
            }
        }

        return (shared, missing);
    }

    /// <summary>Dossiers sur une ligne : « Nom (chemin), … ».</summary>
    public static string Describe(IEnumerable<SharedFolder> folders) => string.Join(", ", folders.Select(f => $"{f.Name} ({f.Path})"));

    /// <summary>Serveur du premier dossier sur un partage réseau (<c>\\serveur\…</c>), ou null.</summary>
    public static string? NetworkServer(IEnumerable<SharedFolder> folders) =>
        folders.Select(f => EnvironmentProfile.NetworkServer(f.Path)).FirstOrDefault(s => s is not null);

    /// <summary>
    /// Chemin Windows complet : lecteur (<c>C:\</c>, racine comprise) ou partage <c>\\serveur\partage</c> ; jamais un chemin
    /// de périphérique (<c>\\?\</c>, <c>\\.\</c>) ni de flux (« : » ailleurs qu'après la lettre du lecteur).
    /// </summary>
    private static bool IsFullPath(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        {
            return path.IndexOf(':', 2) < 0;
        }

        if (path.Length < 5 || !path.StartsWith(@"\\", StringComparison.Ordinal) || path[2] is '\\' or '/' or '?' or '.'
            || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        // Serveur et partage, chacun non vide.
        var share = path.IndexOfAny(['\\', '/'], 2);
        return share > 2 && share < path.Length - 1 && path[share + 1] is not ('\\' or '/');
    }

    /// <summary>Ni caractère de contrôle, ni caractère interdit dans un chemin Windows, ni « , » ou guillemet.</summary>
    private static bool HasPlainCharacters(string path) =>
        path.AsSpan().IndexOfAny(",\"<>|?*") < 0 && !path.Any(char.IsControl);

    private static string Short(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^%[A-Za-z_][A-Za-z0-9_()]*%")]
    private static partial Regex VariablePrefix();
}
