using System.Diagnostics;
using System.IO;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App.Services;

/// <summary>Ouvre les sessions : fichier RDP PSM dans mstsc, SSH via PSMP dans Windows Terminal ou une console.</summary>
/// <remarks>
/// <para>
/// Linux : SSH dans le premier terminal trouvé (x-terminal-emulator, gnome-terminal, konsole, xterm) ; le fichier RDP
/// avec FreeRDP (xfreerdp…), dans un terminal s'il y en a un, pour que FreeRDP puisse y poser ses questions (certificat
/// du serveur, mot de passe). macOS : SSH dans Terminal, le fichier RDP avec l'application associée aux fichiers .rdp.
/// </para>
/// <para>
/// Programmes lancés par leur chemin complet, arguments passés un par un (ArgumentList), sans interpréteur de commandes ;
/// le mot de passe et le jeton PSM ne sont jamais sur une ligne de commande : ils restent dans le fichier .rdp, privé
/// (<see cref="PrivateTemp"/>) et effacé une minute après le lancement.
/// </para>
/// </remarks>
internal sealed class SessionLauncher
{
    // Le fichier RDP contient un jeton de connexion à usage unique : on le supprime dès que mstsc l'a lu.
    private static readonly TimeSpan RdpFileLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Clients FreeRDP, dans l'ordre de préférence (FreeRDP 3 d'abord ; noms selon les distributions).</summary>
    private static readonly string[] FreeRdpClients = ["xfreerdp3", "xfreerdp", "sdl-freerdp3", "sdl-freerdp"];

    /// <summary>Terminaux essayés sous Linux, dans l'ordre.</summary>
    internal static readonly string[] Terminals = ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"];

    private static string RdpDirectory => PrivateTemp.Root;

    public void LaunchRdp(byte[] rdpFile, string label)
    {
        // Programme cherché avant d'écrire le fichier : sans client, le jeton n'est écrit nulle part.
        var start = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? null : RdpClientLinux();
        Directory.CreateDirectory(RdpDirectory);
        var path = Path.Combine(RdpDirectory, $"{SafeFileName(label)}-{Guid.NewGuid():N}.rdp");
        WritePrivate(path, rdpFile);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Chemin complet : un « mstsc.exe » posé dans le dossier de l'application ou le dossier courant recevrait sinon
                // le fichier .rdp et son jeton PSM.
                Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe")) { ArgumentList = { path }, UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                StartMacRdp(path);
            }
            else
            {
                Start(start!(path));
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        _ = DeleteLaterAsync(path);
    }

    /// <summary>
    /// Lance <c>ssh -l &lt;login&gt; -p &lt;port&gt; &lt;psmp&gt;</c>, dans un nouvel onglet Windows Terminal si disponible.
    /// </summary>
    public void LaunchSsh(string login, string psmpHost, int port, string title)
    {
        PsmpTarget.Validate(login, CoreStrings.SshLoginWhat, allowSpaces: true);
        PsmpTarget.Validate(psmpHost, CoreStrings.PsmpAddressWhat);
        Start(SshStartInfo(login, psmpHost, port, title, FindOnPath));
    }

    /// <summary>
    /// Commande qui ouvre la session SSH (séparée du lancement pour être vérifiée sans rien lancer).
    /// <paramref name="find"/> : recherche d'un programme dans le PATH (chemin complet ou null).
    /// </summary>
    internal static ProcessStartInfo SshStartInfo(string login, string psmpHost, int port, string title, Func<string, string?> find)
    {
        string[] sshArgs = ["-l", login, "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), psmpHost];
        if (!OperatingSystem.IsWindows())
        {
            // « -- » : une adresse commençant par « - » (fichier d'environnement piégé) ne peut pas devenir une option de ssh.
            string[] unix = [.. sshArgs[..^1], "--", psmpHost];
            return OperatingSystem.IsMacOS() ? MacTerminal(["/usr/bin/ssh", .. unix]) : LinuxTerminal(find, title, ["ssh", .. unix]);
        }

        var ssh = FindSsh() ?? throw new FileNotFoundException(Strings.SshClientNotFound);

        // Identifiant avec espace : ssh.exe est lancé directement, sans passer par wt.exe, pour que l'argument
        // arrive entier à ssh quelle que soit la façon dont Windows Terminal recompose sa ligne de commande.
        var terminal = login.Contains(' ') ? null : FindWindowsTerminal();
        ProcessStartInfo info;
        if (terminal is not null)
        {
            // « ; » sépare les commandes de wt.exe : on l'exclut du titre (les autres arguments sont validés).
            info = new ProcessStartInfo(terminal) { UseShellExecute = false };
            foreach (var arg in (string[])["-w", "0", "new-tab", "--title", title.Replace(";", " "), ssh, .. sshArgs])
            {
                info.ArgumentList.Add(arg);
            }
        }
        else
        {
            // Application console lancée depuis une application graphique : Windows lui ouvre sa propre fenêtre.
            info = new ProcessStartInfo(ssh) { UseShellExecute = false };
            foreach (var arg in sshArgs)
            {
                info.ArgumentList.Add(arg);
            }
        }

        return info;
    }

    /// <summary>
    /// Linux : <paramref name="command"/> (programme puis arguments) dans le premier terminal trouvé. Chaque terminal reçoit
    /// la commande comme une suite d'arguments, jamais comme une ligne à interpréter : « -e » suivi de plusieurs arguments
    /// (x-terminal-emulator, konsole, xterm), « -- » pour gnome-terminal.
    /// </summary>
    /// <exception cref="FileNotFoundException">Programme de la commande ou terminal introuvable.</exception>
    internal static ProcessStartInfo LinuxTerminal(Func<string, string?> find, string? title, IReadOnlyList<string> command)
    {
        var program = (Path.IsPathRooted(command[0]) ? command[0] : find(command[0]))
                      ?? throw new FileNotFoundException(Text.Format("{0} : {1}", command[0], Strings.PathNotFound));
        var terminal = Terminals.Select(t => (Name: t, Path: find(t))).FirstOrDefault(t => t.Path is not null);
        if (terminal.Path is null)
        {
            throw new FileNotFoundException(Text.Format("{0} : {1}", string.Join(", ", Terminals), Strings.PathNotFound));
        }

        var info = new ProcessStartInfo(terminal.Path) { UseShellExecute = false };
        List<string> args = terminal.Name switch
        {
            "gnome-terminal" => [.. TitleArgs("--title"), "--", program],
            "konsole" => ["-e", program],
            // x-terminal-emulator (Debian, Ubuntu : « -T titre », « -e commande arguments… ») et xterm.
            _ => [.. TitleArgs("-T"), "-e", program],
        };
        args.AddRange(command.Skip(1));
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return info;

        IEnumerable<string> TitleArgs(string option) => string.IsNullOrWhiteSpace(title) ? [] : [option, title];
    }

    /// <summary>
    /// macOS : <paramref name="command"/> dans une nouvelle fenêtre de Terminal. Terminal exécute une ligne de commande :
    /// chaque argument y est protégé par AppleScript (« quoted form of »), qui le reçoit tel quel d'osascript.
    /// </summary>
    internal static ProcessStartInfo MacTerminal(IReadOnlyList<string> command)
    {
        var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
        foreach (var arg in (string[])
                 [
                     "-e", "on run argv",
                     "-e", "set cmd to \"exec\"",
                     "-e", "repeat with a in argv",
                     "-e", "set cmd to cmd & \" \" & quoted form of (a as text)",
                     "-e", "end repeat",
                     "-e", "tell application \"Terminal\" to do script cmd",
                     "-e", "tell application \"Terminal\" to activate",
                     "-e", "end run",
                     .. command,
                 ])
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    /// <summary>
    /// Linux : ouverture du fichier .rdp par FreeRDP (fichier en seul argument), dans un terminal s'il y en a un (questions
    /// de FreeRDP : certificat du serveur, mot de passe), sinon directement.
    /// </summary>
    /// <exception cref="FileNotFoundException">Aucun client FreeRDP installé.</exception>
    internal static Func<string, ProcessStartInfo> RdpClientLinux(Func<string, string?>? find = null)
    {
        find ??= FindOnPath;
        var client = FreeRdpClients.Select(find).FirstOrDefault(p => p is not null)
                     ?? throw new FileNotFoundException(Text.Format(Strings.FreeRdpStartFailed, Text.Format("xfreerdp : {0}", Strings.PathNotFound)));
        bool terminal = Terminals.Any(t => find(t) is not null);
        return path =>
        {
            if (terminal)
            {
                return LinuxTerminal(find, null, [client, path]);
            }

            var info = new ProcessStartInfo(client) { UseShellExecute = false };
            info.ArgumentList.Add(path);
            return info;
        };
    }

    /// <summary>macOS : le fichier .rdp ouvert par l'application qui lui est associée ; erreur si aucune ne l'est.</summary>
    private static void StartMacRdp(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { path }, UseShellExecute = false })
                            ?? throw new FileNotFoundException(Text.Format(Strings.FreeRdpStartFailed, "open"));
        if (process.WaitForExit(TimeSpan.FromSeconds(10)) && process.ExitCode != 0)
        {
            throw new FileNotFoundException(Text.Format(Strings.FreeRdpStartFailed, Text.Format(".rdp : {0}", Strings.PathNotFound)));
        }
    }

    /// <summary>Supprime les fichiers RDP qui n'auraient pas encore été effacés.</summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(RdpDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(RdpDirectory, "*.rdp"))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Fichier lisible par l'utilisateur seul (0600 hors de Windows, en plus du dossier réservé).</summary>
    private static void WritePrivate(string path, byte[] content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, content);
            return;
        }

        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(content);
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }

    private static string? FindSsh()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : FindOnPath("ssh.exe");
    }

    private static string? FindWindowsTerminal()
    {
        // wt.exe est un alias d'exécution d'application placé dans %LOCALAPPDATA%\Microsoft\WindowsApps.
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(alias) ? alias : FindOnPath("wt.exe");
    }

    /// <summary>
    /// Chemin complet d'un programme du PATH ; les dossiers relatifs du PATH (« . ») sont ignorés : jamais un programme du
    /// dossier courant (que Process.Start essaierait avant le PATH sous Linux et macOS).
    /// </summary>
    internal static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => dir.Trim())
            .Where(Path.IsPathRooted)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);

    private static async Task DeleteLaterAsync(string path)
    {
        await Task.Delay(RdpFileLifetime).ConfigureAwait(false);
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Nom de fichier en ASCII simple (lettres, chiffres, « . », « - », « _ ») : sûr pour tout système de fichiers.</summary>
    private static string SafeFileName(string label)
    {
        var clean = new string(label.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.');
        return clean.Length > 60 ? clean[..60] : clean;
    }
}
