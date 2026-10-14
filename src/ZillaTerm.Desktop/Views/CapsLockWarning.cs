using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;

namespace ZillaTerm.App.Views;

/// <summary>
/// Affiche un avertissement tant que Verr. Maj est activé pendant la saisie d'un mot de passe ; annoncé par les lecteurs
/// d'écran quand il apparaît (<see cref="AnnouncedText"/>). Avalonia ne donne pas l'état de Verr. Maj : il est lu sous
/// Windows (GetKeyState) et sous X11 (indicateurs du clavier) ; ailleurs, déduit de la frappe (majuscule sans Maj).
/// </summary>
internal static class CapsLockWarning
{
    public static void Attach(AnnouncedText warning, params TextBox[] boxes)
    {
        bool? guessed = null;

        void Update()
        {
            bool show = boxes.Any(b => b.IsFocused) && (IsOn() ?? guessed ?? false);
            warning.IsVisible = show;
        }

        foreach (var box in boxes)
        {
            box.GotFocus += (_, _) => Update();
            box.LostFocus += (_, _) => Update();
            // Après la touche : l'état de Verr. Maj est alors à jour.
            box.KeyUp += (_, _) => Update();
            box.AddHandler(InputElement.TextInputEvent, (_, e) =>
            {
                if (e.Text is { Length: 1 } text && char.IsLetter(text[0]) && char.ToUpperInvariant(text[0]) != char.ToLowerInvariant(text[0]))
                {
                    // Sans Maj, une majuscule dit Verr. Maj activé (avec Maj, une minuscule aussi).
                    guessed = char.IsUpper(text[0]) != Shift;
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            box.KeyDown += (_, e) => Shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        }
    }

    private static bool Shift { get; set; }

    /// <summary>État de Verr. Maj lu auprès du système ; null s'il ne le donne pas.</summary>
    internal static bool? IsOn()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return (GetKeyState(0x14) & 1) != 0;
            }

            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
            {
                var display = XOpenDisplay(IntPtr.Zero);
                if (display == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    // XkbUseCoreKbd = 0x100 ; indicateur 0 : Verr. Maj.
                    return XkbGetIndicatorState(display, 0x100, out uint state) == 0 ? (state & 1) != 0 : null;
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Bibliothèque absente (Wayland sans XWayland) : déduit de la frappe.
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XkbGetIndicatorState(IntPtr display, uint device, out uint state);
}
