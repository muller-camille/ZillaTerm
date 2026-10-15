using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZillaTerm.Core.Diagnostics;
using static ZillaTerm.App.Services.X11Interop;

namespace ZillaTerm.App.Services;

/// <summary>Fenêtre de Bureau à distance ouverte sur le poste (Connexion Bureau à distance, mstsc ; xfreerdp sous Linux), pour la capturer.</summary>
public sealed record RemoteDesktopWindow(IntPtr Handle, string Title);

/// <summary>Pixels d'une capture : BGRA 32 bits, de haut en bas, <see cref="Width"/> × 4 octets par ligne, au début de
/// <see cref="Bgra"/> (qui peut être plus long).</summary>
public readonly record struct ScreenPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// Capture d'une fenêtre en PNG, pour le journal d'intervention et le partage d'une session Bureau à distance.
/// Windows : PrintWindow (contenu rendu par DirectX compris), même cachée par une autre fenêtre. Linux (X11, XWayland) :
/// XGetImage de la fenêtre, sur une connexion à l'écran X réservée aux captures (fil de travail) ; une partie cachée par
/// une autre fenêtre peut être noire sans compositeur. macOS : rien (aucune session n'y est intégrée).
/// </summary>
public static class WindowCapture
{
    private const uint PwRenderFullContent = 0x2;

    // Classe de la fenêtre principale de Connexion Bureau à distance (mstsc), qui ouvre les sessions PSM.
    private const string MstscWindowClass = "TscShellContainerClass";

    // Connexion à l'écran X des captures (une seule à la fois).
    private static readonly Lock X11Gate = new();
    private static nint _x11Display;

    /// <summary>
    /// Fenêtres visibles de Connexion Bureau à distance (sessions PSM, ouvertes avec mstsc) ; sous Linux, celles du client
    /// FreeRDP du système (WM_CLASS xfreerdp, sessions ouvertes hors de l'application).
    /// </summary>
    public static IReadOnlyList<RemoteDesktopWindow> RemoteDesktopWindows()
    {
        var windows = new List<RemoteDesktopWindow>();
        if (OperatingSystem.IsWindows())
        {
            EnumWindows((handle, _) =>
            {
                if (IsWindowVisible(handle) && !IsIconic(handle) && ClassName(handle) == MstscWindowClass)
                {
                    windows.Add(new RemoteDesktopWindow(handle, WindowText(handle)));
                }

                return true;
            }, IntPtr.Zero);
        }
        else if (Usable)
        {
            WithX11(display =>
            {
                // Fenêtres de premier niveau, et celles qu'un gestionnaire de fenêtres a placées dans son cadre.
                foreach (var top in Children(display, XDefaultRootWindow(display)))
                {
                    foreach (var window in (nint[])[top, .. Children(display, top)])
                    {
                        if (IsViewableWindow(display, window, out _, out _)
                            && ClassHint(display, window) is { } hint && hint.Name.Contains("freerdp", StringComparison.OrdinalIgnoreCase))
                        {
                            windows.Add(new RemoteDesktopWindow(window, X11Interop.Title(display, window)));
                        }
                    }
                }

                return true;
            });
        }

        return windows;
    }

    /// <summary>Image PNG de la fenêtre, ou null si elle n'existe plus ou ne peut pas être dessinée.</summary>
    public static byte[]? CapturePng(IntPtr handle)
    {
        var pixels = OperatingSystem.IsWindows() ? CaptureWin32(handle, null) : CaptureX11(handle, null);
        return pixels is { } image ? Png(image) : null;
    }

    /// <summary>
    /// Image PNG d'une fenêtre enfant (session Bureau à distance dans un onglet) : sa partie de la fenêtre principale,
    /// telle qu'elle est composée à l'écran. Null si elle n'est pas visible.
    /// </summary>
    public static byte[]? CaptureChildPng(IntPtr child) => CaptureChildPixels(child, null) is { } pixels ? Png(pixels) : null;

    /// <summary>
    /// Pixels d'une fenêtre enfant, comme <see cref="CaptureChildPng"/>, pour l'enregistrement de l'écran des sessions
    /// Bureau à distance et leur partage ; null si elle n'est pas visible ou si la fenêtre principale est réduite.
    /// <paramref name="buffer"/> sert s'il est assez grand (une capture par seconde n'alloue pas une image à chaque fois).
    /// </summary>
    public static ScreenPixels? CaptureChildPixels(IntPtr child, byte[]? buffer)
    {
        if (child == IntPtr.Zero)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            // L'emplacement contient la fenêtre du client (même profondeur) : son image est celle de la session.
            return CaptureX11(child, buffer);
        }

        if (!ChildArea(child, out var root, out var area) || IsIconic(root) || CaptureWin32(root, null) is not { } whole)
        {
            return null;
        }

        // Partie de la fenêtre principale occupée par l'enfant.
        int left = Math.Max(area.Left, 0);
        int top = Math.Max(area.Top, 0);
        int right = Math.Min(area.Right, whole.Width);
        int bottom = Math.Min(area.Bottom, whole.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        int width = right - left;
        int height = bottom - top;
        int row = width * 4;
        if (buffer is null || buffer.Length < row * height)
        {
            buffer = new byte[row * height];
        }

        for (int y = 0; y < height; y++)
        {
            Buffer.BlockCopy(whole.Bgra, ((top + y) * whole.Width + left) * 4, buffer, y * row, row);
        }

        return new ScreenPixels(buffer, width, height);
    }

    private static byte[] Png(ScreenPixels pixels)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            int row = pixels.Width * 4;
            for (int y = 0; y < pixels.Height; y++)
            {
                Marshal.Copy(pixels.Bgra, y * row, frame.Address + (y * frame.RowBytes), row);
            }
        }

        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        return png.ToArray();
    }

    // ---- Linux : XGetImage.

    /// <summary>Exécute <paramref name="action"/> avec la connexion des captures ; null si l'écran X est inaccessible.</summary>
    private static T? WithX11<T>(Func<nint, T?> action)
    {
        try
        {
            lock (X11Gate)
            {
                if (_x11Display == IntPtr.Zero)
                {
                    _x11Display = XOpenDisplay(IntPtr.Zero);
                }

                return _x11Display == IntPtr.Zero ? default : action(_x11Display);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            DebugLog.Write("intervention", "Capture X11 impossible", e);
            return default;
        }
    }

    /// <summary>Pixels d'une fenêtre X visible (BGRA, opaques) ; null si elle n'est pas affichée ou d'un format inattendu.</summary>
    private static ScreenPixels? CaptureX11(IntPtr window, byte[]? buffer)
    {
        if (!Usable)
        {
            return null;
        }

        return WithX11<ScreenPixels?>(display =>
        {
            if (!IsViewableWindow(display, window, out int width, out int height) || width > 16384 || height > 16384)
            {
                return null;
            }

            var image = XGetImage(display, window, 0, 0, (uint)width, (uint)height, nuint.MaxValue, ZPixmap);
            if (image == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // Écran en couleurs vraies, 32 bits par pixel, petit-boutiste : B, V, R, (inutilisé).
                int stride = Marshal.ReadInt32(image, ImageBytesPerLine);
                var data = Marshal.ReadIntPtr(image, ImageData);
                if (Marshal.ReadInt32(image, ImageBitsPerPixel) != 32 || Marshal.ReadInt32(image, ImageByteOrder) != LsbFirst
                    || data == IntPtr.Zero || stride < width * 4)
                {
                    return null;
                }

                int row = width * 4;
                if (buffer is null || buffer.Length < row * height)
                {
                    buffer = new byte[row * height];
                }

                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data + (y * stride), buffer, y * row, row);
                }

                for (int i = 3; i < row * height; i += 4)
                {
                    buffer[i] = 0xFF;
                }

                return new ScreenPixels(buffer, width, height);
            }
            finally
            {
                Destroy(image);
            }
        });
    }

    // ---- Windows : PrintWindow dans une image GDI.

    /// <summary>Pixels de la fenêtre (BGRA, opaques), ou null si elle n'existe plus ou ne peut pas être dessinée.</summary>
    private static ScreenPixels? CaptureWin32(IntPtr handle, byte[]? buffer)
    {
        if (!OperatingSystem.IsWindows() || !IsWindow(handle) || !GetWindowRect(handle, out var rect))
        {
            return null;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            // Hauteur négative : lignes de haut en bas.
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            var previous = SelectObject(dc, bitmap);
            bool drawn = PrintWindow(handle, dc, PwRenderFullContent);
            SelectObject(dc, previous);
            if (!drawn)
            {
                return null;
            }

            int length = width * height * 4;
            if (buffer is null || buffer.Length < length)
            {
                buffer = new byte[length];
            }

            Marshal.Copy(bits, buffer, 0, length);
            for (int i = 3; i < length; i += 4)
            {
                buffer[i] = 0xFF;
            }

            return new ScreenPixels(buffer, width, height);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Fenêtre principale d'une fenêtre enfant visible, et la place de l'enfant dans celle-ci.</summary>
    private static bool ChildArea(IntPtr child, out IntPtr root, out Rect area)
    {
        root = GetAncestor(child, GaRoot);
        area = default;
        if (root == IntPtr.Zero || !IsWindowVisible(child) || !GetWindowRect(child, out var rect) || !GetWindowRect(root, out var window))
        {
            return false;
        }

        area = new Rect
        {
            Left = rect.Left - window.Left,
            Top = rect.Top - window.Top,
            Right = rect.Right - window.Left,
            Bottom = rect.Bottom - window.Top,
        };
        return true;
    }

    private const uint GaRoot = 2;

    private static string ClassName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static string WindowText(IntPtr handle)
    {
        var title = new StringBuilder(512);
        return GetWindowText(handle, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr handle, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);
}
