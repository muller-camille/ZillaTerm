using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ZillaTerm.App.Services;

/// <summary>
/// Dossier temporaire de l'application (copies en cours d'édition, fichiers .rdp et leur jeton PSM, archives à envoyer,
/// fichiers glissés vers l'Explorateur), réservé à l'utilisateur Windows : « %TEMP%\ZillaTerm » s'il lui appartient
/// (ses droits sont alors limités à lui seul), sinon « %LOCALAPPDATA%\ZillaTerm\Temp ». Un dossier créé d'avance par
/// un autre utilisateur (TEMP pointant vers un dossier partagé) n'est jamais utilisé.
/// </summary>
internal static class PrivateTemp
{
    private static readonly Lazy<string> RootPath = new(Prepare);

    public static string Root => RootPath.Value;

    public static string Combine(string name) => Path.Combine(Root, name);

    private static string Prepare()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ZillaTerm");
        if (TryRestrict(temp))
        {
            return temp;
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZillaTerm", "Temp");
        TryRestrict(local);
        return local;
    }

    /// <summary>
    /// Dossiers temporaires de CyberArkTerm (ancien nom de l'application) : ce qui a plus d'un jour est effacé (copies en
    /// cours d'édition, fichiers .rdp, glissements, archives), comme l'ancienne version le faisait à son démarrage ; le
    /// dossier vide est retiré. Seulement s'il appartient à l'utilisateur.
    /// </summary>
    public static void CleanupLegacy()
    {
        var name = Core.AppSettings.LegacyName;
        foreach (var root in new[]
                 {
                     Path.Combine(Path.GetTempPath(), name),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name, "Temp"),
                 })
        {
            try
            {
                if (!Directory.Exists(root) || !OwnedByMe(root))
                {
                    continue;
                }

                var limit = DateTime.UtcNow.AddDays(-1);
                foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                             .OrderByDescending(e => e.FullName.Length))
                {
                    if (entry is FileInfo file && file.LastWriteTimeUtc < limit)
                    {
                        file.Delete();
                    }
                    else if (entry is DirectoryInfo dir && !dir.EnumerateFileSystemInfos().Any())
                    {
                        dir.Delete();
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Core.Diagnostics.DebugLog.Write("temp", $"Ancien dossier temporaire {root} : {e.Message}");
            }
        }
    }

    private static bool OwnedByMe(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } me
               && new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) == me;
    }

    /// <summary>
    /// Crée le dossier s'il manque ; vrai s'il appartient à l'utilisateur et que ses droits ont été réservés à lui seul
    /// (les fichiers et dossiers qu'il contient en héritent).
    /// </summary>
    private static bool TryRestrict(string path)
    {
        try
        {
            var directory = Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var me = identity.User;
            if (me is null || directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) != me)
            {
                return false;
            }

            var restricted = new DirectorySecurity();
            restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            restricted.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(restricted);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Core.Diagnostics.DebugLog.Write("temp", $"Droits du dossier temporaire {path} non réservés : {e.Message}");
            return false;
        }
    }
}
