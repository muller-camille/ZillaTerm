using System.Globalization;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Migration;

/// <summary>Où lire les sessions d'un autre logiciel.</summary>
public enum ImportSourceKind
{
    /// <summary>Registre Windows de l'utilisateur : sessions PuTTY.</summary>
    PuttyRegistry,

    /// <summary>Registre Windows de l'utilisateur : sessions KiTTY.</summary>
    KittyRegistry,

    /// <summary>Registre Windows de l'utilisateur : sites WinSCP.</summary>
    WinScpRegistry,

    /// <summary>Export du registre (.reg) : sessions PuTTY, KiTTY et sites WinSCP qu'il contient.</summary>
    RegFile,

    /// <summary>Dossier de KiTTY portable (sessions en fichiers).</summary>
    KittyFolder,

    /// <summary>Fichier WinSCP.ini.</summary>
    WinScpIni,

    /// <summary>Fichier .mxtsessions, ou fichier .ini de configuration au même format.</summary>
    MxtSessions,

    /// <summary>Fichier confCons.xml de mRemoteNG.</summary>
    MRemoteNg,

    /// <summary>Fichier .rdg de Remote Desktop Connection Manager.</summary>
    RdcMan,

    /// <summary>Fichier de configuration du client OpenSSH.</summary>
    OpenSsh,

    /// <summary>Dossier de configuration de SecureCRT (sessions en fichiers .ini).</summary>
    SecureCrtFolder,

    /// <summary>Export XML des réglages de SecureCRT.</summary>
    SecureCrtXml,

    /// <summary>Dossier de fichiers .rdp.</summary>
    RdpFolder,

    /// <summary>Dossier des sessions de PuTTY sous Linux et les autres Unix (~/.putty/sessions).</summary>
    PuttyFolder,
}

/// <summary>Lecture des sessions d'une source, quel que soit son format.</summary>
public static class SessionSources
{
    public static bool IsRegistry(ImportSourceKind kind) =>
        kind is ImportSourceKind.PuttyRegistry or ImportSourceKind.KittyRegistry or ImportSourceKind.WinScpRegistry;

    public static bool IsFolder(ImportSourceKind kind) =>
        kind is ImportSourceKind.KittyFolder or ImportSourceKind.SecureCrtFolder or ImportSourceKind.RdpFolder or ImportSourceKind.PuttyFolder;

    /// <summary>Clé du registre de l'utilisateur (sous HKEY_CURRENT_USER) dont les sous-clés sont les sessions.</summary>
    public static string RegistryPath(ImportSourceKind kind) => kind switch
    {
        ImportSourceKind.PuttyRegistry => PuttySessions.PuttyKey,
        ImportSourceKind.KittyRegistry => PuttySessions.KittyKey,
        ImportSourceKind.WinScpRegistry => WinScpSites.RegistryKey,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Seules valeurs du registre à lire pour cette source (jamais un mot de passe).</summary>
    public static IReadOnlyList<string> RegistryValueNames(ImportSourceKind kind) =>
        kind == ImportSourceKind.WinScpRegistry ? WinScpSites.ValueNames : PuttySessions.ValueNames;

    /// <summary>Sessions lues dans le registre (sous-clés de <see cref="RegistryPath"/>).</summary>
    public static List<ImportedSession> FromRegistry(ImportSourceKind kind, IEnumerable<RegKey> keys) =>
        kind == ImportSourceKind.WinScpRegistry ? WinScpSites.FromRegistry(keys) : PuttySessions.FromRegistry(keys);

    /// <summary>Sessions d'un fichier ou d'un dossier.</summary>
    /// <exception cref="InvalidDataException">Fichier illisible, y compris une valeur qu'un format n'attend pas.</exception>
    public static List<ImportedSession> Read(ImportSourceKind kind, string path)
    {
        try
        {
            return ReadCore(kind, path);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Valeur inattendue dans un fichier abîmé ou forgé : une erreur de lecture comme une autre, pas un plantage.
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, CoreStrings.MigrationUnreadable, ex.Message), ex);
        }
    }

    private static List<ImportedSession> ReadCore(ImportSourceKind kind, string path)
    {
        switch (kind)
        {
            case ImportSourceKind.KittyFolder:
                return PuttySessions.FromKittyFolder(path);
            case ImportSourceKind.PuttyFolder:
                return PuttySessions.FromUnixFolder(path);
            case ImportSourceKind.SecureCrtFolder:
                return SecureCrtSessions.FromFolder(path);
            case ImportSourceKind.RdpFolder:
                return RdpFiles.FromFolder(path);
        }

        var text = ImportText.ReadFile(path);
        switch (kind)
        {
            case ImportSourceKind.RegFile:
                var keys = RegFile.Parse(text, PuttySessions.ValueNames.Concat(WinScpSites.ValueNames));
                return [.. PuttySessions.FromRegistry(keys), .. WinScpSites.FromRegistry(keys)];
            case ImportSourceKind.WinScpIni:
                return WinScpSites.FromIni(text);
            case ImportSourceKind.MxtSessions:
                return MxtSessionsFile.Read(text);
            case ImportSourceKind.MRemoteNg:
                return MRemoteNgFile.Read(text);
            case ImportSourceKind.RdcMan:
                return RdcManFile.Read(text);
            case ImportSourceKind.OpenSsh:
                return OpenSshConfig.Read(text);
            case ImportSourceKind.SecureCrtXml:
                return SecureCrtSessions.FromXml(text);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
