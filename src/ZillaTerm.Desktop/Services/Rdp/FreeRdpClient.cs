using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>Le client FreeRDP n'a pas pu être préparé ou lancé.</summary>
internal sealed class FreeRdpStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client Bureau à distance FreeRDP intégré aux onglets.
/// </summary>
/// <remarks>
/// <para>
/// Windows : celui de ZillaTerm (wfreerdp, release du fork, tools/freerdp-download.sh), embarqué dans l'application,
/// copié au premier usage dans un dossier réservé à l'utilisateur (« %LOCALAPPDATA%\ZillaTerm\FreeRDP\&lt;empreinte&gt; »),
/// vérifié (SHA-256) avant chaque lancement et gardé ouvert en lecture seule pendant celui-ci : il ne peut pas être
/// remplacé entre la vérification et le lancement. Ses processus sont dans un objet de travail fermé avec l'application :
/// aucun ne lui survit.
/// </para>
/// <para>
/// Linux (X11, ou XWayland) : le client FreeRDP 3 du système (xfreerdp3, ou xfreerdp en version 3), qui doit savoir lire
/// toutes ses options sur son entrée standard (« /args-from:stdin ») et n'accepter que des empreintes données
/// (« /cert:deny,fingerprint:… ») ; sinon il n'est pas utilisé (<see cref="UnavailableReason"/>) : rien de secret ne passe
/// jamais par sa ligne de commande. Lancé dans sa propre session (setsid : aucun terminal où il pourrait poser une
/// question), arrêté avec ZillaTerm (setpriv --pdeathsig), journal lu ligne par ligne (stdbuf). macOS : pas de client
/// intégré.
/// </para>
/// </remarks>
internal static partial class FreeRdpClient
{
    private const string ResourceName = "ZillaTerm.FreeRdp.wfreerdp.exe";

    /// <summary>Seul argument de la ligne de commande du client sous Linux : tout le reste arrive sur son entrée standard.</summary>
    public const string ArgumentsFromStdin = "/args-from:stdin";

    // Variables qu'un client FreeRDP lirait : journal détaillé ou écrit dans un fichier (WLOG_*), configuration
    // d'OpenSSL et ses autorités de certification, proxy HTTP (la connexion au PSM doit être directe, comme avec mstsc).
    private static readonly string[] HiddenVariablePrefixes = ["WLOG_", "OPENSSL_", "SSL_CERT_", "FREERDP_"];
    private static readonly string[] HiddenVariables = ["http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    private static readonly Lazy<IntPtr> Job = new(CreateJob);
    private static readonly Lazy<LinuxClient> Linux = new(ProbeLinux);

    /// <summary>
    /// Vrai si un client FreeRDP peut s'ouvrir dans un onglet : Windows, l'application embarque le sien (compilation avec
    /// local-packages/freerdp) ; Linux, le client du système convient (<see cref="UnavailableReason"/> sinon).
    /// </summary>
    public static bool IsAvailable =>
        OperatingSystem.IsWindows() ? typeof(FreeRdpClient).Assembly.GetManifestResourceInfo(ResourceName) is not null
        : OperatingSystem.IsLinux() && Linux.Value.Path is not null;

    /// <summary>Pourquoi aucun client FreeRDP n'est intégrable (journal de débogage), ou null.</summary>
    public static string? UnavailableReason =>
        OperatingSystem.IsWindows() ? (IsAvailable ? null : "wfreerdp.exe non embarqué (local-packages/freerdp absent à la compilation)")
        : OperatingSystem.IsLinux() ? Linux.Value.Problem
        : "macOS : pas de client Bureau à distance intégrable dans un onglet";

    /// <summary>
    /// Vrai si une application distante (RemoteApp) s'ouvre dans l'onglet : fork de ZillaTerm sous Windows. Le client
    /// X11 du système ouvre ses fenêtres à part, sur le bureau : la session va alors au client externe.
    /// </summary>
    public static bool EmbedsRemoteApp => OperatingSystem.IsWindows();

    /// <summary>
    /// Vrai si le client applique l'empreinte épinglée à l'ancienne sécurité RDP (sans TLS) : correctifs du fork de
    /// ZillaTerm (Windows). Le client du système ne le fait pas : un serveur sans TLS y est refusé.
    /// </summary>
    public static bool PinsLegacySecurity => OperatingSystem.IsWindows();

    /// <summary>
    /// Dossier de configuration donné au client (« /tune:FreeRDP_ConfigPath ») : à ZillaTerm, réservé à l'utilisateur,
    /// sans certificat ni clé connus du client (seules les empreintes passées par ZillaTerm sont acceptées).
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            var folder = Path.Combine(LocalApplicationData, "ZillaTerm", "FreeRDP-config");
            Restrict(folder);
            return folder;
        }
    }

    /// <summary>
    /// Lance le client avec ces arguments (rien de secret), entrée et sorties redirigées, sans fenêtre de console ; Windows :
    /// dans l'objet de travail. Linux : <paramref name="arguments"/> doit être <see cref="ArgumentsFromStdin"/> seul, et
    /// l'appel se fait sur le thread de l'interface (signal envoyé au client quand le thread qui l'a lancé s'arrête).
    /// </summary>
    /// <exception cref="FreeRdpStartException">Client absent, copie impossible, empreinte fausse ou lancement refusé
    /// (stratégie AppLocker…).</exception>
    public static Process Start(IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            using var executable = Open();
            return Run(executable.Name, [], arguments, Path.GetDirectoryName(executable.Name)!);
        }

        if (Linux.Value is not { Path: { } client } linux)
        {
            throw new FreeRdpStartException(UnavailableReason ?? "xfreerdp");
        }

        // Ligne de commande visible de tous les processus du poste (ps, /proc) : jamais autre chose que l'option qui fait
        // lire les autres sur l'entrée standard.
        if (arguments is not [ArgumentsFromStdin])
        {
            throw new FreeRdpStartException("/args-from:stdin");
        }

        List<string> wrappers = [];
        if (linux.Setsid is { } setsid)
        {
            wrappers.Add(setsid);
        }

        if (linux.Setpriv is { } setpriv)
        {
            wrappers.AddRange([setpriv, "--pdeathsig", "TERM", "--"]);
        }

        if (linux.Stdbuf is { } stdbuf)
        {
            // Journal du client ligne par ligne sur sa sortie (un tube : sinon par blocs de 4 Ko) : la reconnexion
            // automatique se lit au moment où elle a lieu (le client du fork, lui, vide sa sortie à chaque message).
            wrappers.AddRange([stdbuf, "-oL", "-eL"]);
        }

        // setsid, setpriv et stdbuf remplacent leur processus par le suivant : le processus lancé est celui du client.
        List<string> command = [.. wrappers, client, .. arguments];
        string program = command[0];
        command.RemoveAt(0);
        var config = ConfigPath;
        var noAuthorities = Path.Combine(config, "no-ca");
        Restrict(noAuthorities);
        return Run(program, [], command, config, process =>
        {
            // Aucune autorité de certification du système : seules les empreintes acceptées par ZillaTerm ouvrent la
            // connexion (comme le client Windows de ZillaTerm), jamais un certificat que le système approuverait seul.
            process.Environment["SSL_CERT_FILE"] = Path.Combine(noAuthorities, "none.pem");
            process.Environment["SSL_CERT_DIR"] = noAuthorities;
            process.Environment["SSL_CERT_URI"] = noAuthorities;
        });
    }

    private static Process Run(string program, IReadOnlyList<string> prefix, IReadOnlyList<string> arguments, string directory,
        Action<ProcessStartInfo>? environment = null)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = directory,
        };
        foreach (var argument in prefix.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        HideVariables(info);
        environment?.Invoke(info);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new FreeRdpStartException("Process.Start");
        }
        catch (Win32Exception e)
        {
            throw new FreeRdpStartException(e.Message, e);
        }

        if (OperatingSystem.IsWindows() && Job.Value != IntPtr.Zero && !AssignProcessToJobObject(Job.Value, process.Handle))
        {
            DebugLog.Write("freerdp", $"Processus {process.Id} hors de l'objet de travail : {Marshal.GetLastPInvokeErrorMessage()}");
        }

        return process;
    }

    private static void HideVariables(ProcessStartInfo info)
    {
        foreach (var name in info.Environment.Keys.ToList())
        {
            if (HiddenVariablePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || HiddenVariables.Any(v => name.Equals(v, StringComparison.OrdinalIgnoreCase)))
            {
                info.Environment.Remove(name);
            }
        }
    }

    /// <summary>« %LOCALAPPDATA% », « ~/.local/share » : même s'il n'existe pas encore.</summary>
    private static string LocalApplicationData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    // ---- Linux : client FreeRDP 3 du système.

    /// <param name="Path">Client utilisable, ou null.</param>
    /// <param name="Problem">Pourquoi aucun client n'est utilisable, ou null.</param>
    private sealed record LinuxClient(string? Path, string? Problem, string? Setsid = null, string? Setpriv = null, string? Stdbuf = null);

    /// <summary>
    /// Client X11 de FreeRDP 3 trouvé dans le PATH (xfreerdp3, sinon xfreerdp), vérifié une fois : version 3 au moins,
    /// options « /args-from » (entrée standard), « /parent-window » et empreintes de « /cert ». Sinon, refusé avec la raison.
    /// </summary>
    private static LinuxClient ProbeLinux()
    {
        if (!X11Interop.Usable)
        {
            return Refuse("pas d'écran X11 (DISPLAY absent : session Wayland sans XWayland) : client Bureau à distance non intégrable");
        }

        string? problem = null;
        foreach (var name in (string[])["xfreerdp3", "xfreerdp"])
        {
            if (SessionLauncher.FindOnPath(name) is not { } path)
            {
                continue;
            }

            var version = Output(path, "/version");
            var match = version is null ? null : VersionLine().Match(version);
            if (match is not { Success: true } || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            {
                problem = $"{path} : version de FreeRDP illisible";
                continue;
            }

            if (major < 3)
            {
                problem = $"{path} : FreeRDP {match.Groups[0].Value.Trim()}, version 3 requise";
                continue;
            }

            var help = Output(path, "/help") ?? "";
            if (!help.Contains("/args-from:", StringComparison.Ordinal) || !help.Contains("stdin", StringComparison.Ordinal))
            {
                // Sans lecture des options sur l'entrée standard, mot de passe et jeton PSM seraient sur la ligne de commande.
                problem = $"{path} : pas d'option /args-from (entrée standard) : refusé, les identifiants seraient sur la ligne de commande";
                continue;
            }

            if (!help.Contains("/parent-window:", StringComparison.Ordinal) || !help.Contains("fingerprint:", StringComparison.Ordinal))
            {
                problem = $"{path} : pas d'option /parent-window ou d'empreintes de certificat (/cert:fingerprint) : refusé";
                continue;
            }

            var setpriv = SessionLauncher.FindOnPath("setpriv");
            if (setpriv is not null && Output(setpriv, "--help")?.Contains("--pdeathsig", StringComparison.Ordinal) != true)
            {
                setpriv = null;
            }

            DebugLog.Write("freerdp", $"Client FreeRDP du système : {path}, {match.Value.Trim()}"
                + (setpriv is null ? " ; setpriv --pdeathsig absent : le client ne s'arrêtera pas de lui-même si ZillaTerm s'arrête brutalement" : ""));
            return new LinuxClient(path, null, SessionLauncher.FindOnPath("setsid"), setpriv, SessionLauncher.FindOnPath("stdbuf"));
        }

        return Refuse(problem ?? "xfreerdp3 : " + Localization.Strings.PathNotFound);

        static LinuxClient Refuse(string why)
        {
            DebugLog.Write("freerdp", $"Pas de client FreeRDP dans les onglets : {why}");
            return new LinuxClient(null, why);
        }
    }

    /// <summary>Sortie d'un programme lancé avec un argument (5 s au plus), ou null.</summary>
    private static string? Output(string program, string argument)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { argument },
            };
            HideVariables(info);
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return output.Wait(TimeSpan.FromSeconds(1)) ? output.Result : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            DebugLog.Write("freerdp", $"{program} {argument} : {e.Message}");
            return null;
        }
    }

    [GeneratedRegex(@"FreeRDP version (\d+)\.\d+[^\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    // ---- Windows : copie vérifiée du client embarqué.

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
        var root = Path.Combine(LocalApplicationData, "ZillaTerm", "FreeRDP");
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
    /// Crée le dossier s'il manque et le réserve à l'utilisateur. Windows : droits réservés à l'utilisateur (fichiers
    /// compris) ; son propriétaire peut être le groupe Administrateurs (compte administrateur), d'où aucune condition
    /// dessus : l'empreinte du client est de toute façon vérifiée avant chaque lancement. Linux, macOS : droits 0700,
    /// dossier de l'utilisateur et jamais un lien symbolique, sinon refusé.
    /// </summary>
    private static void Restrict(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            try
            {
                if (PrivateTemp.Inspect(folder) == PrivateTemp.Ownership.Missing)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                    Directory.CreateDirectory(folder, ownerOnly);
                }

                if (PrivateTemp.Inspect(folder) == PrivateTemp.Ownership.Open)
                {
                    File.SetUnixFileMode(folder, ownerOnly);
                }

                if (PrivateTemp.Inspect(folder) is var state and not PrivateTemp.Ownership.Private)
                {
                    throw new FreeRdpStartException($"{folder} : {state}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new FreeRdpStartException(e.Message, e);
            }

            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

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
