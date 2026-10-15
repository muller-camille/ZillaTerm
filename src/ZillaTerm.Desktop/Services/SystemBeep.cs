using System.Runtime.InteropServices;

namespace ZillaTerm.App.Services;

/// <summary>
/// Son d'alerte du système (sonnerie d'un serveur VNC), à la place de <c>SystemSounds.Beep</c> de WPF : son « par
/// défaut » de Windows, cloche du serveur X sous Linux (réglée par le bureau, souvent muette), NSBeep sous macOS.
/// </summary>
internal static class SystemBeep
{
    // Connexion au serveur X ouverte au premier son, gardée ensuite (une par application).
    private static nint _display;

    /// <summary>Joue le son ; sans son disponible (pas de serveur X, bibliothèque absente), ne fait rien.</summary>
    public static void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBeep(0);
            }
            else if (OperatingSystem.IsMacOS())
            {
                NSBeep();
            }
            else if (OperatingSystem.IsLinux())
            {
                if (_display == 0)
                {
                    _display = XOpenDisplay(0);
                }

                if (_display != 0)
                {
                    XBell(_display, 0);
                    XFlush(_display);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pas de son sur ce poste.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
    private static extern void NSBeep();

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(nint name);

    [DllImport("libX11.so.6")]
    private static extern int XBell(nint display, int percent);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);
}
