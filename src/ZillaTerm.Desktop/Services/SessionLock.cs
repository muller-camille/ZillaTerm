using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Threading;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services;

/// <summary>
/// Verrouillage et déverrouillage de la session du poste : coffres KeePass verrouillés, maintien de la session PVWA
/// suspendu, partage arrêté. Sous Windows, les événements de session (SystemEvents) ; sous Linux, logind (signaux Lock,
/// Unlock et mise en veille de org.freedesktop.login1, lus par <c>gdbus monitor</c>). Sans logind ni gdbus, rien n'est
/// signalé : <see cref="Supported"/> le dit.
/// </summary>
internal static class SessionLock
{
    private static bool _started;
    private static Process? _monitor;

    /// <summary>Session verrouillée (ou poste mis en veille). Levé sur le fil de l'interface.</summary>
    public static event Action? Locked;

    /// <summary>Session déverrouillée. Levé sur le fil de l'interface.</summary>
    public static event Action? Unlocked;

    /// <summary>Le verrouillage du poste est suivi (Windows, ou Linux avec logind et gdbus).</summary>
    public static bool Supported { get; private set; }

    /// <summary>Commence à suivre le verrouillage (une fois, au démarrage de l'application).</summary>
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (OperatingSystem.IsWindows())
        {
            StartWindows();
        }
        else if (OperatingSystem.IsLinux())
        {
            StartLogind();
        }
    }

    /// <summary>Arrête le suivi (fermeture de l'application).</summary>
    public static void Stop()
    {
        try
        {
            if (_monitor is { HasExited: false } monitor)
            {
                monitor.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Processus déjà terminé.
        }

        _monitor?.Dispose();
        _monitor = null;
    }

    private static void Raise(bool locked) => Dispatcher.UIThread.Post(() => (locked ? Locked : Unlocked)?.Invoke());

    [SupportedOSPlatform("windows")]
    private static void StartWindows()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            {
                Raise(locked: true);
            }
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock)
            {
                Raise(locked: false);
            }
        };
        Supported = true;
    }

    /// <summary>
    /// Signaux de logind pour la session de ZillaTerm (trouvée par son processus) : Lock, Unlock, et PrepareForSleep
    /// (veille) traité comme un verrouillage. Les arguments sont passés sans interpréteur de commandes.
    /// </summary>
    private static void StartLogind()
    {
        var session = RunGdbus("call", "--system", "--dest", "org.freedesktop.login1", "--object-path", "/org/freedesktop/login1",
            "--method", "org.freedesktop.login1.Manager.GetSessionByPID", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Réponse : (objectpath '/org/freedesktop/login1/session/_32',)
        var path = session is null ? null : System.Text.RegularExpressions.Regex.Match(session, @"'(/org/freedesktop/login1/session/[^']+)'") is { Success: true } m
            ? m.Groups[1].Value
            : null;
        if (path is null)
        {
            DebugLog.Write("lock", "Session logind introuvable : le verrouillage du poste n'est pas suivi.");
            return;
        }

        try
        {
            var info = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "monitor", "--system", "--dest", "org.freedesktop.login1" })
            {
                info.ArgumentList.Add(arg);
            }

            var monitor = Process.Start(info);
            if (monitor is null)
            {
                return;
            }

            _monitor = monitor;
            monitor.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line)
                {
                    return;
                }

                if (line.StartsWith(path + ":", StringComparison.Ordinal) && line.Contains("org.freedesktop.login1.Session.Lock (", StringComparison.Ordinal))
                {
                    Raise(locked: true);
                }
                else if (line.StartsWith(path + ":", StringComparison.Ordinal) && line.Contains("org.freedesktop.login1.Session.Unlock (", StringComparison.Ordinal))
                {
                    Raise(locked: false);
                }
                else if (line.Contains("org.freedesktop.login1.Manager.PrepareForSleep (true,)", StringComparison.Ordinal))
                {
                    Raise(locked: true);
                }
            };
            monitor.ErrorDataReceived += (_, _) => { };
            monitor.BeginOutputReadLine();
            monitor.BeginErrorReadLine();
            Supported = true;
            DebugLog.Write("lock", $"Verrouillage du poste suivi par logind ({path}).");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DebugLog.Write("lock", "gdbus introuvable : le verrouillage du poste n'est pas suivi", e);
        }
    }

    private static string? RunGdbus(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("gdbus")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(3000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
