using System.Runtime.InteropServices;
using System.Text;

namespace ZillaTerm.App.Tests;

/// <summary>Fenêtres, vraie souris et vrai clavier (SendInput), pour les tests de sessions réelles.</summary>
internal static class Win32Input
{
    public readonly record struct Bounds(int Left, int Top, int Width, int Height)
    {
        public (int X, int Y) Center => (Left + (Width / 2), Top + (Height / 2));
    }

    public static Bounds ScreenBounds(IntPtr window)
    {
        GetWindowRect(window, out var r);
        return new Bounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    public static string Title(IntPtr window)
    {
        var text = new StringBuilder(512);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>Fenêtres contenues dans <paramref name="parent"/>, à toute profondeur.</summary>
    public static List<IntPtr> Descendants(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, _) =>
        {
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Fenêtres de premier niveau de ce processus.</summary>
    public static List<IntPtr> ProcessWindows() => TopLevelWindows(Environment.ProcessId);

    /// <summary>Fenêtres de premier niveau du processus <paramref name="pid"/>.</summary>
    public static List<IntPtr> TopLevelWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out int owner);
            if (owner == pid)
            {
                list.Add(h);
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Met <paramref name="window"/> au premier plan : une entrée de ce processus (souris immobile) l'y autorise.
    /// </summary>
    public static void BringToFront(IntPtr window)
    {
        Send(new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0001 } });
        SetForegroundWindow(window);
    }

    /// <summary>Fenêtre qui a le clavier dans le thread de <paramref name="window"/>.</summary>
    public static IntPtr FocusOf(IntPtr window)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) ? info.Focus : IntPtr.Zero;
    }

    public static void Click((int X, int Y) point, bool right = false)
    {
        SetCursorPos(point.X, point.Y);
        Send(
            new Input { Type = 0, Mouse = new MouseInput { Flags = right ? 0x0008u : 0x0002u } },
            new Input { Type = 0, Mouse = new MouseInput { Flags = right ? 0x0010u : 0x0004u } });
    }

    /// <summary>Appuie puis relâche chaque touche (codes virtuels : lettres majuscules, chiffres).</summary>
    public static void Type(string keys)
    {
        foreach (var key in keys)
        {
            Send(
                new Input { Type = 1, Keyboard = new KeyboardInput { Vk = key } },
                new Input { Type = 1, Keyboard = new KeyboardInput { Vk = key, Flags = 0x0002 } });
        }
    }

    private static void Send(params Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public Rect CaretRect;
    }

    private delegate bool EnumProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(int thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc proc, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr param);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
