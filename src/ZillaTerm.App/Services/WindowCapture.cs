using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ZillaTerm.App.Services;

/// <summary>Fenêtre de Bureau à distance ouverte sur le poste (Connexion Bureau à distance, mstsc), pour la capturer.</summary>
public sealed record RemoteDesktopWindow(IntPtr Handle, string Title);

/// <summary>Pixels d'une capture : BGRA 32 bits, de haut en bas, <see cref="Width"/> × 4 octets par ligne, au début de
/// <see cref="Bgra"/> (qui peut être plus long).</summary>
public readonly record struct ScreenPixels(byte[] Bgra, int Width, int Height);

/// <summary>
/// Capture d'une fenêtre en PNG avec PrintWindow (contenu rendu par DirectX compris), même cachée par une autre fenêtre ;
/// pour le journal d'intervention.
/// </summary>
public static class WindowCapture
{
    private const uint PwRenderFullContent = 0x2;

    // Classe de la fenêtre principale de Connexion Bureau à distance (mstsc), qui ouvre les sessions PSM.
    private const string MstscWindowClass = "TscShellContainerClass";

    /// <summary>Fenêtres visibles de Connexion Bureau à distance (sessions PSM, ouvertes avec mstsc).</summary>
    public static IReadOnlyList<RemoteDesktopWindow> RemoteDesktopWindows()
    {
        var windows = new List<RemoteDesktopWindow>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && !IsIconic(handle) && ClassName(handle) == MstscWindowClass)
            {
                windows.Add(new RemoteDesktopWindow(handle, Title(handle)));
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>Image PNG de la fenêtre, ou null si elle n'existe plus ou ne peut pas être dessinée.</summary>
    public static byte[]? CapturePng(IntPtr handle)
    {
        using var bitmap = CaptureBitmap(handle);
        return bitmap is null ? null : Png(bitmap);
    }

    /// <summary>
    /// Image PNG d'une fenêtre enfant (contrôle Bureau à distance dans un onglet) : sa partie de la fenêtre principale,
    /// telle qu'elle est composée à l'écran. Null si elle n'est pas visible.
    /// </summary>
    public static byte[]? CaptureChildPng(IntPtr child)
    {
        if (!ChildArea(child, out var root, out var area))
        {
            return null;
        }

        using var whole = CaptureBitmap(root);
        if (whole is null || Crop(whole, area) is not { } crop)
        {
            return null;
        }

        using var part = whole.Clone(crop, whole.PixelFormat);
        return Png(part);
    }

    /// <summary>
    /// Pixels d'une fenêtre enfant, comme <see cref="CaptureChildPng"/>, pour l'enregistrement de l'écran des sessions
    /// Bureau à distance ; null si elle n'est pas visible ou si la fenêtre principale est réduite. <paramref name="buffer"/>
    /// sert s'il est assez grand (une capture par seconde n'alloue pas une image à chaque fois).
    /// </summary>
    public static ScreenPixels? CaptureChildPixels(IntPtr child, byte[]? buffer)
    {
        if (!ChildArea(child, out var root, out var area) || IsIconic(root))
        {
            return null;
        }

        using var whole = CaptureBitmap(root);
        if (whole is null || Crop(whole, area) is not { } crop)
        {
            return null;
        }

        int row = crop.Width * 4;
        int length = row * crop.Height;
        if (buffer is null || buffer.Length < length)
        {
            buffer = new byte[length];
        }

        var data = whole.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < crop.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), buffer, y * row, row);
            }
        }
        finally
        {
            whole.UnlockBits(data);
        }

        return new ScreenPixels(buffer, crop.Width, crop.Height);
    }

    /// <summary>Image de la fenêtre (32 bits), ou null si elle n'existe plus ou ne peut pas être dessinée.</summary>
    private static Bitmap? CaptureBitmap(IntPtr handle)
    {
        if (!IsWindow(handle) || !GetWindowRect(handle, out var rect))
        {
            return null;
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        bool drawn = false;
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            IntPtr dc = graphics.GetHdc();
            try
            {
                drawn = PrintWindow(handle, dc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }
        finally
        {
            if (!drawn)
            {
                bitmap.Dispose();
            }
        }

        return drawn ? bitmap : null;
    }

    /// <summary>Fenêtre principale d'une fenêtre enfant visible, et la place de l'enfant dans celle-ci.</summary>
    private static bool ChildArea(IntPtr child, out IntPtr root, out Rectangle area)
    {
        root = GetAncestor(child, GaRoot);
        area = default;
        if (root == IntPtr.Zero || !IsWindowVisible(child) || !GetWindowRect(child, out var rect) || !GetWindowRect(root, out var window))
        {
            return false;
        }

        area = new Rectangle(rect.Left - window.Left, rect.Top - window.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }

    /// <summary>Partie de <paramref name="area"/> dans l'image ; null si elle est vide.</summary>
    private static Rectangle? Crop(Bitmap whole, Rectangle area)
    {
        var crop = Rectangle.Intersect(area, new Rectangle(0, 0, whole.Width, whole.Height));
        return crop.Width > 0 && crop.Height > 0 ? crop : null;
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private const uint GaRoot = 2;

    private static string ClassName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static string Title(IntPtr handle)
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
}
