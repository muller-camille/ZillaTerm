using System.Runtime.InteropServices;

namespace ZillaTerm.App.Services;

/// <summary>
/// Xlib (libX11) pour le Bureau à distance dans un onglet sous Linux, sous X11 ou XWayland : emplacement de la session,
/// fenêtre du client FreeRDP qui y est intégrée, captures de l'écran de la session. Structures et constantes de Xlib.h et
/// X.h pour les systèmes 64 bits (types XID et Time sur 8 octets) ; Avalonia appelle XInitThreads avant tout le reste :
/// une connexion peut servir à plusieurs fils.
/// </summary>
internal static class X11Interop
{
    private const string Lib = "libX11.so.6";

    // Types d'événements (X.h).
    public const int ButtonPress = 4;
    public const int FocusIn = 9;
    public const int FocusOut = 10;
    public const int CreateNotify = 16;
    public const int DestroyNotify = 17;
    public const int ReparentNotify = 21;
    public const int ConfigureNotify = 22;
    public const int ClientMessage = 33;

    // Masques d'événements.
    public const long ButtonPressMask = 1L << 2;
    public const long StructureNotifyMask = 1L << 17;
    public const long SubstructureNotifyMask = 1L << 19;
    public const long FocusChangeMask = 1L << 21;

    // Attributs d'une fenêtre (XSetWindowAttributes).
    public const nuint CWBackPixel = 1 << 1;
    public const nuint CWBorderPixel = 1 << 3;
    public const nuint CWOverrideRedirect = 1 << 9;
    public const nuint CWColormap = 1 << 13;

    public const uint InputOutput = 1;
    public const int GrabModeSync = 0;
    public const int GrabModeAsync = 1;
    public const int ReplayPointer = 2;
    public const int RevertToParent = 2;
    public const uint AnyModifier = 1 << 15;
    public const int ZPixmap = 2;
    public const int IsViewable = 2;
    public const int LsbFirst = 0;
    public const nint CurrentTime = 0;
    public const nint XaCardinal = 6;
    public const nint XaString = 31;

    /// <summary>Vrai si Xlib peut servir ici : Linux 64 bits, écran X (DISPLAY : X11, ou XWayland sous Wayland).</summary>
    public static bool Usable => OperatingSystem.IsLinux() && IntPtr.Size == 8
                                 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>XEvent (union de 192 octets) : seuls les champs lus ici, à leur place sur 64 bits.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    public struct XEvent
    {
        [FieldOffset(0)]
        public int Type;

        /// <summary>
        /// xany.window ; xbutton.window ; xfocus.window ; xclient.window ; « event » de xdestroywindow, xreparent,
        /// xconfigure ; « parent » de xcreatewindow.
        /// </summary>
        [FieldOffset(32)]
        public nint Window;

        /// <summary>« window » de xcreatewindow, xdestroywindow, xreparent, xconfigure ; xclient.message_type.</summary>
        [FieldOffset(40)]
        public nint Window2;

        /// <summary>xfocus.mode.</summary>
        [FieldOffset(40)]
        public int FocusMode;

        /// <summary>xreparent.parent ; xbutton.subwindow.</summary>
        [FieldOffset(48)]
        public nint Window3;

        /// <summary>xclient.format.</summary>
        [FieldOffset(48)]
        public int Format;

        /// <summary>xbutton.time ; xclient.data.l[0].</summary>
        [FieldOffset(56)]
        public nint Time;

        /// <summary>Largeur de xconfigure et xcreatewindow.</summary>
        [FieldOffset(56)]
        public int Width;

        /// <summary>Hauteur de xconfigure et xcreatewindow.</summary>
        [FieldOffset(60)]
        public int Height;

        /// <summary>xclient.data.l[1].</summary>
        [FieldOffset(64)]
        public nint Data1;
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct XSetWindowAttributes
    {
        [FieldOffset(8)]
        public nuint BackgroundPixel;

        [FieldOffset(24)]
        public nuint BorderPixel;

        [FieldOffset(88)]
        public int OverrideRedirect;

        [FieldOffset(96)]
        public nint Colormap;
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    public struct XWindowAttributes
    {
        [FieldOffset(0)]
        public int X;

        [FieldOffset(4)]
        public int Y;

        [FieldOffset(8)]
        public int Width;

        [FieldOffset(12)]
        public int Height;

        [FieldOffset(20)]
        public int Depth;

        [FieldOffset(92)]
        public int MapState;

        [FieldOffset(120)]
        public int OverrideRedirect;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClassHint
    {
        public nint Name;
        public nint Class;
    }

    // XImage : champs lus par leur place (Xlib.h, 64 bits).
    public const int ImageWidth = 0;
    public const int ImageHeight = 4;
    public const int ImageData = 16;
    public const int ImageByteOrder = 24;
    public const int ImageBytesPerLine = 44;
    public const int ImageBitsPerPixel = 48;
    public const int ImageDestroy = 96;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DestroyImage(nint image);

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport(Lib)]
    public static extern nint XOpenDisplay(nint name);

    [DllImport(Lib)]
    public static extern int XCloseDisplay(nint display);

    [DllImport(Lib)]
    public static extern int XNextEvent(nint display, out XEvent e);

    [DllImport(Lib)]
    public static extern int XFlush(nint display);

    [DllImport(Lib)]
    public static extern int XSync(nint display, int discard);

    [DllImport(Lib)]
    public static extern int XDefaultScreen(nint display);

    [DllImport(Lib)]
    public static extern nint XDefaultRootWindow(nint display);

    [DllImport(Lib)]
    public static extern nint XDefaultVisual(nint display, int screen);

    [DllImport(Lib)]
    public static extern int XDefaultDepth(nint display, int screen);

    [DllImport(Lib)]
    public static extern nint XDefaultColormap(nint display, int screen);

    [DllImport(Lib)]
    public static extern nuint XBlackPixel(nint display, int screen);

    [DllImport(Lib)]
    public static extern nint XCreateWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint windowClass, nint visual, nuint valueMask, ref XSetWindowAttributes attributes);

    [DllImport(Lib)]
    public static extern int XDestroyWindow(nint display, nint window);

    [DllImport(Lib)]
    public static extern int XMapWindow(nint display, nint window);

    [DllImport(Lib)]
    public static extern int XSelectInput(nint display, nint window, nint eventMask);

    [DllImport(Lib)]
    public static extern int XReparentWindow(nint display, nint window, nint parent, int x, int y);

    [DllImport(Lib)]
    public static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);

    [DllImport(Lib)]
    public static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);

    [DllImport(Lib)]
    public static extern int XFree(nint data);

    [DllImport(Lib)]
    public static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);

    [DllImport(Lib)]
    public static extern int XSetInputFocus(nint display, nint window, int revertTo, nint time);

    [DllImport(Lib)]
    public static extern int XGrabButton(nint display, uint button, uint modifiers, nint grabWindow, int ownerEvents, uint eventMask,
        int pointerMode, int keyboardMode, nint confineTo, nint cursor);

    [DllImport(Lib)]
    public static extern int XUngrabButton(nint display, uint button, uint modifiers, nint grabWindow);

    [DllImport(Lib)]
    public static extern int XAllowEvents(nint display, int mode, nint time);

    [DllImport(Lib, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    public static extern nint XInternAtom(nint display, string name, int onlyIfExists);

    [DllImport(Lib)]
    public static extern int XSendEvent(nint display, nint window, int propagate, nint eventMask, ref XEvent e);

    [DllImport(Lib)]
    public static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

    [DllImport(Lib)]
    public static extern nint XGetImage(nint display, nint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    [DllImport(Lib)]
    public static extern int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete,
        nint requestedType, out nint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out nint data);

    [DllImport(Lib)]
    public static extern int XGetClassHint(nint display, nint window, out XClassHint hint);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static extern int Poll(ref PollFd fds, nuint count, int timeout);

    /// <summary>Libère une image de XGetImage (sa fonction destroy_image, comme la macro XDestroyImage).</summary>
    public static void Destroy(nint image)
    {
        var destroy = Marshal.ReadIntPtr(image, ImageDestroy);
        if (destroy != IntPtr.Zero)
        {
            Marshal.GetDelegateForFunctionPointer<DestroyImage>(destroy)(image);
        }
    }

    /// <summary>Fenêtre visible à l'écran (elle et ses parents affichés), avec sa taille ; faux sinon ou si elle n'existe plus.</summary>
    public static bool IsViewableWindow(nint display, nint window, out int width, out int height)
    {
        width = height = 0;
        if (window == IntPtr.Zero || XGetWindowAttributes(display, window, out var attributes) == 0 || attributes.MapState != IsViewable)
        {
            return false;
        }

        width = attributes.Width;
        height = attributes.Height;
        return width > 0 && height > 0;
    }

    /// <summary>Fenêtres filles de <paramref name="window"/>, de la plus basse à la plus haute ; vide si elle n'existe plus.</summary>
    public static nint[] Children(nint display, nint window)
    {
        if (XQueryTree(display, window, out _, out _, out var children, out uint count) == 0)
        {
            return [];
        }

        try
        {
            var result = new nint[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = Marshal.ReadIntPtr(children, i * IntPtr.Size);
            }

            return result;
        }
        finally
        {
            if (children != IntPtr.Zero)
            {
                XFree(children);
            }
        }
    }

    /// <summary>Fenêtre parente ; zéro si elle n'existe plus.</summary>
    public static nint Parent(nint display, nint window)
    {
        if (XQueryTree(display, window, out _, out var parent, out var children, out _) == 0)
        {
            return IntPtr.Zero;
        }

        if (children != IntPtr.Zero)
        {
            XFree(children);
        }

        return parent;
    }

    /// <summary>Processus déclaré par la fenêtre (_NET_WM_PID), ou null.</summary>
    public static int? ProcessId(nint display, nint window)
    {
        var atom = XInternAtom(display, "_NET_WM_PID", 1);
        if (atom == IntPtr.Zero
            || XGetWindowProperty(display, window, atom, 0, 1, 0, XaCardinal, out _, out int format, out var count, out _, out var data) != 0)
        {
            return null;
        }

        try
        {
            // Format 32 : chaque valeur occupe un « long » de Xlib (8 octets sur 64 bits).
            return data != IntPtr.Zero && format == 32 && count >= 1 ? (int)Marshal.ReadInt64(data) : null;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                XFree(data);
            }
        }
    }

    /// <summary>Classe de la fenêtre (WM_CLASS : nom de l'instance et de la classe), ou null.</summary>
    public static (string Name, string Class)? ClassHint(nint display, nint window)
    {
        if (XGetClassHint(display, window, out var hint) == 0)
        {
            return null;
        }

        try
        {
            return (Marshal.PtrToStringUTF8(hint.Name) ?? "", Marshal.PtrToStringUTF8(hint.Class) ?? "");
        }
        finally
        {
            if (hint.Name != IntPtr.Zero)
            {
                XFree(hint.Name);
            }

            if (hint.Class != IntPtr.Zero)
            {
                XFree(hint.Class);
            }
        }
    }

    /// <summary>Titre de la fenêtre (_NET_WM_NAME en UTF-8, sinon WM_NAME) ; vide s'il n'y en a pas.</summary>
    public static string Title(nint display, nint window)
    {
        var utf8 = XInternAtom(display, "UTF8_STRING", 1);
        var netName = XInternAtom(display, "_NET_WM_NAME", 1);
        if (utf8 != IntPtr.Zero && netName != IntPtr.Zero && Text(display, window, netName, utf8) is { Length: > 0 } title)
        {
            return title;
        }

        var wmName = XInternAtom(display, "WM_NAME", 1);
        return wmName == IntPtr.Zero ? "" : Text(display, window, wmName, XaString) ?? "";
    }

    private static string? Text(nint display, nint window, nint property, nint type)
    {
        if (XGetWindowProperty(display, window, property, 0, 1024, 0, type, out _, out int format, out var count, out _, out var data) != 0)
        {
            return null;
        }

        try
        {
            return data != IntPtr.Zero && format == 8 && count > 0 ? Marshal.PtrToStringUTF8(data, (int)count) : null;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                XFree(data);
            }
        }
    }
}
