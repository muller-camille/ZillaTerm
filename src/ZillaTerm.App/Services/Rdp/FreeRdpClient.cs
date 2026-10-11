using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>Le client FreeRDP n'a pas pu être préparé ou lancé.</summary>
internal sealed class FreeRdpStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client Bureau à distance FreeRDP de ZillaTerm (wfreerdp, release du fork, tools/freerdp-download.sh) : embarqué dans
/// l'application, copié au premier usage dans un dossier réservé à l'utilisateur
/// (« %LOCALAPPDATA%\ZillaTerm\FreeRDP\&lt;empreinte&gt; »), vérifié (SHA-256) avant chaque lancement et gardé ouvert
/// en lecture seule pendant celui-ci : il ne peut pas être remplacé entre la vérification et le lancement. Ses
/// processus sont dans un objet de travail fermé avec l'application : aucun ne lui survit.
/// </summary>
internal static class FreeRdpClient
{
    private const string ResourceName = "ZillaTerm.FreeRdp.wfreerdp.exe";

    // Variables qu'un client FreeRDP lirait : journal détaillé ou écrit dans un fichier (WLOG_*), configuration
    // d'OpenSSL, proxy HTTP (la connexion au PSM doit être directe, comme avec mstsc).
    private static readonly string[] HiddenVariablePrefixes = ["WLOG_", "OPENSSL_", "SSL_CERT_", "FREERDP_"];
    private static readonly string[] HiddenVariables = ["http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    /// <summary>Vrai si l'application embarque le client FreeRDP (compilation avec local-packages/freerdp).</summary>
    public static bool IsAvailable => typeof(FreeRdpClient).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>
    /// Dossier de configuration donné au client (« /tune:FreeRDP_ConfigPath ») : à ZillaTerm, réservé à l'utilisateur,
    /// sans certificat ni clé connus du client (seules les empreintes passées par ZillaTerm sont acceptées).
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZillaTerm", "FreeRDP-config");
            Restrict(folder);
            return folder;
        }
    }

    /// <summary>
    /// Lance le client avec ces arguments (rien de secret), entrée et sorties redirigées, sans fenêtre de console, dans
    /// l'objet de travail.
    /// </summary>
    /// <exception cref="FreeRdpStartException">Client absent, copie impossible, empreinte fausse ou lancement refusé
    /// (stratégie AppLocker…).</exception>
    public static Process Start(IEnumerable<string> arguments)
    {
        using var executable = Open();
        var info = new ProcessStartInfo(executable.Name, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable.Name)!,
        };
        foreach (var name in info.Environment.Keys.ToList())
        {
            if (HiddenVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || HiddenVariables.Any(v => name.Equals(v, StringComparison.OrdinalIgnoreCase)))
            {
                info.Environment.Remove(name);
            }
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new FreeRdpStartException("Process.Start");
        }
        catch (Win32Exception e)
        {
            throw new FreeRdpStartException(e.Message, e);
        }

        if (Job.Value != IntPtr.Zero && !AssignProcessToJobObject(Job.Value, process.Handle))
        {
            DebugLog.Write("freerdp", $"Processus {process.Id} hors de l'objet de travail : {Marshal.GetLastPInvokeErrorMessage()}");
        }

        return process;
    }

    /// <summary>Copie vérifiée du client, ouverte en lecture partagée seulement (ni écriture, ni suppression).</summary>
    private static FileStream Open()
    {
        byte[] content;
        using (var resource = typeof(FreeRdpClient).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (resource is null)
            {
                throw new FreeRdpStartException("wfreerdp.exe");
            }

            content = new byte[resource.Length];
            resource.ReadExactly(content);
        }

        var hash = SHA256.HashData(content);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZillaTerm", "FreeRDP");
        var folder = Path.Combine(root, Convert.ToHexString(hash, 0, 8));
        var path = Path.Combine(folder, "wfreerdp.exe");
        try
        {
            Restrict(folder);
            if (OpenVerified(path, hash) is { } existing)
            {
                return existing;
            }

            var temporary = Path.Combine(folder, $"wfreerdp-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
            DebugLog.Write("freerdp", $"Client FreeRDP copié dans {folder}");
            RemoveOldCopies(root, folder);
            return OpenVerified(path, hash) ?? throw new FreeRdpStartException(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FreeRdpStartException(e.Message, e);
        }
    }

    /// <summary>
    /// Crée le dossier s'il manque et réserve ses droits à l'utilisateur (fichiers compris). Il est dans son profil ;
    /// son propriétaire peut être le groupe Administrateurs (compte administrateur), d'où aucune condition dessus :
    /// l'empreinte du client est de toute façon vérifiée avant chaque lancement.
    /// </summary>
    private static void Restrict(string folder)
    {
        var directory = Directory.CreateDirectory(folder);
        using var identity = WindowsIdentity.GetCurrent();
        var me = identity.User ?? throw new FreeRdpStartException(folder);
        var restricted = new DirectorySecurity();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(restricted);
    }

    private static FileStream? OpenVerified(string path, byte[] hash)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (SHA256.HashData(stream).AsSpan().SequenceEqual(hash))
        {
            return stream;
        }

        stream.Dispose();
        DebugLog.Write("freerdp", $"Copie du client FreeRDP modifiée, remplacée : {path}");
        return null;
    }

    /// <summary>Copies des versions précédentes (dossiers d'une autre empreinte) : effacées si elles ne servent plus.</summary>
    private static void RemoveOldCopies(string root, string current)
    {
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!string.Equals(folder, current, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Encore lancée par une autre instance de ZillaTerm : on réessaiera.
                }
            }
        }
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            DebugLog.Write("freerdp", $"Objet de travail sans arrêt à la fermeture : {Marshal.GetLastPInvokeErrorMessage()}");
        }

        return job;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
