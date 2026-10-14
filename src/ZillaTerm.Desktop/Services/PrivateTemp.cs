using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Services;

/// <summary>
/// Dossier temporaire de l'application (copies en cours d'édition, fichiers .rdp et leur jeton PSM, archives à envoyer,
/// fichiers glissés vers l'Explorateur), réservé à l'utilisateur Windows : « %TEMP%\ZillaTerm » s'il lui appartient
/// (ses droits sont alors limités à lui seul), sinon « %LOCALAPPDATA%\ZillaTerm\Temp ». Un dossier créé d'avance par
/// un autre utilisateur (TEMP pointant vers un dossier partagé) n'est jamais utilisé.
/// </summary>
/// <remarks>
/// Linux et macOS : « $TMPDIR/ZillaTerm-&lt;uid&gt; » (/tmp est commun à tous les utilisateurs), sinon
/// « ~/.local/share/ZillaTerm/Temp ». Le dossier doit être un vrai dossier (jamais un lien symbolique, qui n'est pas
/// suivi), appartenir à l'utilisateur et n'être ouvert qu'à lui (droits 0700) : c'est vérifié à chaque usage, et un dossier
/// qui ne l'est pas est refusé (<see cref="UnauthorizedAccessException"/>) plutôt que d'y écrire.
/// </remarks>
internal static class PrivateTemp
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly Lazy<string> RootPath = new(Prepare);

    /// <summary>Dossier réservé ; hors de Windows, revérifié (et recréé s'il a disparu) à chaque usage.</summary>
    /// <exception cref="UnauthorizedAccessException">Hors de Windows : dossier qui n'est plus réservé à l'utilisateur.</exception>
    public static string Root
    {
        get
        {
            var root = RootPath.Value;
            if (!OperatingSystem.IsWindows() && (!Path.IsPathRooted(root) || Inspect(root) != Ownership.Private) && !TryRestrict(root))
            {
                throw new UnauthorizedAccessException($"{root} : {Strings.PermissionDenied}");
            }

            return root;
        }
    }

    public static string Combine(string name) => Path.Combine(Root, name);

    private static string Prepare()
    {
        var temp = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "ZillaTerm" : $"ZillaTerm-{UnixUser.EffectiveId}");
        if (TryRestrict(temp))
        {
            return temp;
        }

        var local = Path.Combine(LocalApplicationData, "ZillaTerm", "Temp");
        TryRestrict(local);
        return local;
    }

    /// <summary>
    /// « %LOCALAPPDATA% », « ~/.local/share » : même s'il n'existe pas encore (sinon .NET rend un chemin vide, et le dossier
    /// serait cherché dans le dossier courant).
    /// </summary>
    private static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

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
                     Path.Combine(LocalApplicationData, name, "Temp"),
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
            // Lien symbolique ou dossier d'un autre : on n'y touche pas (rien n'est effacé à travers un lien).
            return Inspect(path) is Ownership.Private or Ownership.Open;
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
            if (!OperatingSystem.IsWindows())
            {
                return TryRestrictUnix(path);
            }

            var directory = Directory.CreateDirectory(path);
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

    /// <summary>
    /// Linux, macOS : dossier créé avec les droits 0700 s'il manque. Existant, il doit être à l'utilisateur, ne pas être un
    /// lien symbolique, et personne d'autre ne doit avoir pu y écrire ; lisible par d'autres (créé avec un umask 022), ses
    /// droits sont ramenés à 0700.
    /// </summary>
    private static bool TryRestrictUnix(string path)
    {
        if (OperatingSystem.IsWindows() || !Path.IsPathRooted(path))
        {
            // Chemin relatif (dossier de base inconnu) : jamais le dossier courant.
            return false;
        }

        if (Inspect(path) == Ownership.Missing)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            // Droits 0700 dès la création (le umask ne peut qu'en retirer). Un lien symbolique posé entre-temps à ce nom
            // n'est pas suivi : il est vu, et refusé, juste après.
            Directory.CreateDirectory(path, OwnerOnly);
        }

        var state = Inspect(path);
        if (state == Ownership.Open)
        {
            File.SetUnixFileMode(path, OwnerOnly);
            state = Inspect(path);
        }

        if (state != Ownership.Private)
        {
            Core.Diagnostics.DebugLog.Write("temp", $"Dossier temporaire {path} refusé : {state}");
        }

        return state == Ownership.Private;
    }

    /// <summary>État d'un dossier hors de Windows, lu sans suivre de lien symbolique.</summary>
    internal enum Ownership
    {
        /// <summary>N'existe pas.</summary>
        Missing,

        /// <summary>Dossier de l'utilisateur, fermé aux autres (0700 ou moins).</summary>
        Private,

        /// <summary>Dossier de l'utilisateur lisible ou traversable par d'autres, mais où eux seuls n'ont pas pu écrire.</summary>
        Open,

        /// <summary>Dossier de l'utilisateur où d'autres peuvent écrire : refusé (ils ont pu y déposer un lien).</summary>
        Writable,

        /// <summary>Lien symbolique, fichier ou autre : refusé.</summary>
        NotDirectory,

        /// <summary>Appartient à un autre utilisateur : refusé.</summary>
        OtherOwner,
    }

    /// <summary>Propriétaire, type et droits de <paramref name="path"/> (lstat : un lien symbolique n'est pas suivi).</summary>
    internal static Ownership Inspect(string path)
    {
        var status = UnixUser.LStat(path);
        if (status is null)
        {
            return Ownership.Missing;
        }

        var (mode, uid) = status.Value;
        if ((mode & UnixUser.TypeMask) != UnixUser.TypeDirectory)
        {
            return Ownership.NotDirectory;
        }

        if (uid != UnixUser.EffectiveId)
        {
            return Ownership.OtherOwner;
        }

        // Droits d'écriture du groupe ou des autres (0022) ; lecture ou traversée seulement (0055).
        return (mode & 0x12) != 0 ? Ownership.Writable : (mode & 0x3F) != 0 ? Ownership.Open : Ownership.Private;
    }

    /// <summary>
    /// Utilisateur et état des fichiers sous Linux et macOS, par la bibliothèque native de .NET (System.Native) : sa
    /// structure de résultat est la même sur tous les systèmes, contrairement à celle de stat.
    /// </summary>
    private static class UnixUser
    {
        public const int TypeMask = 0xF000;
        public const int TypeDirectory = 0x4000;

        private const int NoSuchFile = 2;
        private const int NotADirectory = 20;

        public static uint EffectiveId => GetEUid();

        /// <summary>Type et droits (st_mode), propriétaire ; null si le chemin n'existe pas.</summary>
        /// <exception cref="IOException">Lecture impossible (droits du dossier parent…).</exception>
        public static (int Mode, uint Uid)? LStat(string path)
        {
            if (LStat(path, out var status) == 0)
            {
                return (status.Mode, status.Uid);
            }

            int error = Marshal.GetLastPInvokeError();
            return error is NoSuchFile or NotADirectory ? null : throw new IOException($"{path} : lstat ({error})");
        }

        // FileStatus de System.Native : Flags, Mode, Uid en tête (le reste — taille, dates, inode — n'est pas lu). Taille
        // réservée bien au-delà de la sienne (120 octets en .NET 10) : la bibliothèque y écrit toute sa structure.
        [StructLayout(LayoutKind.Explicit, Size = 512)]
        private struct FileStatus
        {
            [FieldOffset(4)]
            public int Mode;

            [FieldOffset(8)]
            public uint Uid;
        }

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

        [DllImport("libSystem.Native", EntryPoint = "SystemNative_GetEUid")]
        private static extern uint GetEUid();
    }
}
