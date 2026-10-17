using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZillaTerm.App.Views;

/// <summary>
/// Place une fenêtre sur le bon écran, avec plusieurs écrans : fenêtre détachée à l'endroit où l'onglet a été lâché,
/// fenêtre ramenée sur l'écran de la fenêtre principale. Les positions sont en pixels de l'écran visé, une fois la
/// fenêtre créée : converties avec l'échelle de la fenêtre principale, elles seraient fausses sur un écran d'une autre
/// échelle (100 % d'un côté, 150 % de l'autre).
/// </summary>
internal static class ScreenPlacement
{
    /// <param name="devicePoint">Point lâché, en pixels de l'écran.</param>
    /// <param name="dx">Décalage horizontal, en pixels indépendants (mis à l'échelle de l'écran visé).</param>
    /// <param name="dy">Décalage vertical, en pixels indépendants.</param>
    public static void OpenAt(Window window, Point devicePoint, double dx, double dy)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SourceInitialized += (_, _) =>
        {
            var monitor = MonitorFromPoint(new NativePoint((int)devicePoint.X, (int)devicePoint.Y), MonitorDefaultToNearest);
            double scale = GetDpiForMonitor(monitor, MonitorDpiEffective, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                (int)Math.Round(devicePoint.X + dx * scale), (int)Math.Round(devicePoint.Y + dy * scale), 0, 0,
                NoSize | NoZOrder | NoActivate);
        };
    }

    /// <summary>
    /// Centre la fenêtre sur <paramref name="owner"/>, en pixels de l'écran, entière dans la zone de travail de l'écran
    /// de <paramref name="owner"/> autant que sa taille le permet. Pour une fenêtre déjà affichée (sur un autre écran que
    /// celui de la fenêtre principale, avec plusieurs écrans) ; Windows la remet à l'échelle de l'écran d'arrivée.
    /// </summary>
    public static void CenterOn(Window window, Window owner)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle, ownerHandle = new WindowInteropHelper(owner).Handle;
        if (handle == IntPtr.Zero || ownerHandle == IntPtr.Zero || !GetWindowRect(handle, out var rect)
            || !GetWindowRect(ownerHandle, out var ownerRect))
        {
            return;
        }

        var area = System.Windows.Forms.Screen.FromHandle(ownerHandle).WorkingArea;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int x = ownerRect.Left + ((ownerRect.Right - ownerRect.Left - width) / 2);
        int y = ownerRect.Top + ((ownerRect.Bottom - ownerRect.Top - height) / 2);
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    /// <summary>
    /// Vrai si la fenêtre est visible sur le même écran que <paramref name="other"/> (celui qui porte la plus grande part
    /// de chacune) ; faux aussi si elle n'est sur aucun écran (écran débranché, position hors de tous les écrans).
    /// </summary>
    public static bool SameScreen(Window window, Window other)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle, otherHandle = new WindowInteropHelper(other).Handle;
        if (handle == IntPtr.Zero || otherHandle == IntPtr.Zero)
        {
            return true;
        }

        return MonitorFromWindow(handle, MonitorDefaultToNull) != IntPtr.Zero
               && System.Windows.Forms.Screen.FromHandle(handle).DeviceName == System.Windows.Forms.Screen.FromHandle(otherHandle).DeviceName;
    }

    /// <summary>Écran et rectangle (pixels de l'écran) d'une fenêtre, pour le journal de débogage.</summary>
    public static string Describe(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect))
        {
            return "pas encore créée";
        }

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        return $"{screen.DeviceName}{(screen.Primary ? " (principal)" : "")} {screen.Bounds.Width}×{screen.Bounds.Height}, "
               + $"fenêtre à {rect.Left},{rect.Top} ({rect.Right - rect.Left}×{rect.Bottom - rect.Top})";
    }

    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiEffective = 0;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
