using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ZillaTerm.App.Services;

/// <summary>
/// Temps écoulé depuis la dernière action au clavier ou à la souris : sur tout le poste sous Windows (GetLastInputInfo)
/// et sous X11 (extension MIT-SCREEN-SAVER, libXss) ; à défaut, dans les fenêtres de ZillaTerm seulement, ce qui est plus
/// prudent (une session n'est jamais maintenue pour un poste inactif).
/// </summary>
internal static class UserActivity
{
    private static long _lastInput = Environment.TickCount64;
    private static bool _xssMissing;

    /// <summary>Suit la frappe et la souris dans <paramref name="window"/> (repli quand le système ne donne rien).</summary>
    public static void Track(Window window)
    {
        void Touch(object? sender, RoutedEventArgs e) => _lastInput = Environment.TickCount64;
        window.AddHandler(InputElement.KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static TimeSpan Idle => SystemIdle() ?? TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastInput);

    private static TimeSpan? SystemIdle()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
                return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : null;
            }

            if (OperatingSystem.IsLinux() && !_xssMissing && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
            {
                return X11Idle();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // libXss absente (ou Wayland sans XWayland) : activité suivie dans les fenêtres de ZillaTerm.
            _xssMissing = true;
        }

        return null;
    }

    private static TimeSpan? X11Idle()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var info = XScreenSaverAllocInfo();
            if (info == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (XScreenSaverQueryInfo(display, XDefaultRootWindow(display), info) == 0)
                {
                    return null;
                }

                return TimeSpan.FromMilliseconds(Marshal.PtrToStructure<XScreenSaverInfo>(info).Idle);
            }
            finally
            {
                XFree(info);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo
    {
        public nuint Window;
        public int State;
        public int Kind;
        public nuint TilOrSince;
        public nuint Idle;
        public nuint EventMask;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libXss.so.1")]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport("libXss.so.1")]
    private static extern int XScreenSaverQueryInfo(IntPtr display, nuint drawable, IntPtr info);
}
