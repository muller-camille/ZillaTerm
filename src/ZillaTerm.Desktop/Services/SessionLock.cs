using System.Runtime.Versioning;
using Avalonia.Threading;
using ZillaTerm.App.Services.Sharing;

namespace ZillaTerm.App.Services;

/// <summary>
/// Verrouillage et déverrouillage de la session du poste : coffres KeePass verrouillés, presse-papiers du mot de passe
/// vidé, maintien de la session PVWA suspendu. Sous Windows, les événements de session (SystemEvents) ; sous Linux,
/// logind et l'économiseur d'écran du bureau par D-Bus (voir <see cref="SessionLockWatch"/>). Ailleurs (macOS, Linux
/// sans D-Bus), rien n'est signalé : <see cref="Supported"/> le dit.
/// </summary>
internal static class SessionLock
{
    private static bool _started;
    private static SessionLockWatch? _watch;

    /// <summary>Session verrouillée (ou poste mis en veille). Levé sur le fil de l'interface.</summary>
    public static event Action? Locked;

    /// <summary>
    /// Session verrouillée, levé tout de suite sur le fil qui l'apprend (D-Bus, événements de session de Windows) : pour
    /// ce qui doit s'arrêter sans attendre le fil de l'interface, occupé ou figé (partages de session).
    /// </summary>
    public static event Action? LockedNow;

    /// <summary>Session déverrouillée. Levé sur le fil de l'interface.</summary>
    public static event Action? Unlocked;

    /// <summary>Le verrouillage du poste est suivi (Windows, ou Linux avec logind joignable).</summary>
    public static bool Supported => OperatingSystem.IsWindows() || _watch is { IsWatching: true };

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
        else
        {
            _watch = SessionLockWatch.Start(_ => Raise(locked: true), _ => Raise(locked: false));
        }
    }

    /// <summary>Arrête le suivi (fermeture de l'application).</summary>
    public static void Stop()
    {
        _watch?.Dispose();
        _watch = null;
    }

    private static void Raise(bool locked)
    {
        if (locked)
        {
            LockedNow?.Invoke();
        }

        Dispatcher.UIThread.Post(() => (locked ? Locked : Unlocked)?.Invoke());
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindows()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            // Comme dans ZillaTerm.App : déconnexion de la console ou d'une session à distance = verrouillage.
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            {
                Raise(locked: true);
            }
            else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
                     or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            {
                Raise(locked: false);
            }
        };
    }
}
