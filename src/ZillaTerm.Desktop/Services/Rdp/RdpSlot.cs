using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Emplacement de la session dans l'onglet : fenêtre native fille de la fenêtre principale, qu'Avalonia place, affiche et
/// masque (<see cref="NativeControlHost"/>) sans jamais attendre le client Bureau à distance. Celui-ci y crée sa fenêtre
/// (Windows : wfreerdp, « /parent-window » ; Linux : xfreerdp, dans la fenêtre d'accueil de l'emplacement, voir
/// <see cref="X11SlotHost"/>) ; ZillaTerm l'adapte à sa taille, et la ferme avant de libérer l'emplacement.
/// </summary>
/// <remarks>
/// Rien d'Avalonia ne peut s'afficher par-dessus cette fenêtre : l'onglet la masque quand il montre autre chose
/// (progression, fin de session). macOS : pas d'emplacement natif utilisable (le client FreeRDP n'y est pas intégré).
/// </remarks>
internal sealed class RdpSlot : NativeControlHost
{
    private const string Win32ClassName = "ZillaTermRdpSlot";

    // Fenêtres d'emplacement Windows, par handle (procédure de fenêtre commune).
    private static readonly Dictionary<IntPtr, RdpSlot> Win32Slots = [];
    private static readonly WndProc Win32Proc = Win32WindowProc;
    private static ushort _win32Class;

    private IntPtr _handle;
    private X11Slot? _x11;
    private bool _focusOnChildClick;
    private bool _listensToClipboard;
    private bool _closed;
    private int _focusRetries;
    private TopLevel? _topLevel;
    private Control? _area;
    private (int Width, int Height) _lastPixels;

    /// <summary>La taille de l'emplacement a changé (thread de l'interface).</summary>
    public event Action? PixelSizeChanged;

    /// <summary>
    /// L'emplacement (Windows) ou la fenêtre du client dans l'emplacement (Linux) a reçu (vrai) ou perdu (faux) le focus
    /// clavier.
    /// </summary>
    public event Action<bool>? FocusChanged;

    /// <summary>Molette reçue par l'emplacement (message, wParam, lParam), quand <see cref="FocusOnChildClick"/> (Windows).</summary>
    public event Action<int, IntPtr, IntPtr>? ChildMouseWheel;

    /// <summary>
    /// Fenêtre créée dans l'emplacement (Windows : WM_PARENTNOTIFY, pendant sa création ; Linux : arrivée dans la fenêtre
    /// d'accueil). Le client FreeRDP crée la sienne une fois la connexion ouverte, et de nouveau après une reconnexion
    /// sous Linux.
    /// </summary>
    public event Action<IntPtr>? ChildCreated;

    /// <summary>Fenêtre du client détruite ou retirée de l'emplacement (Linux : reconnexion, fin du client).</summary>
    public event Action<IntPtr>? ChildGone;

    /// <summary>
    /// Le presse-papiers de Windows a changé (WM_CLIPBOARDUPDATE), tant que <see cref="ListenToClipboard"/> est actif :
    /// journal de débogage des sessions FreeRDP (copier-coller entre le poste et la session).
    /// </summary>
    public event Action? ClipboardChanged;

    /// <summary>
    /// Fenêtre d'un autre processus dans l'emplacement (client FreeRDP) : un clic dedans lui donne le focus clavier
    /// (Windows : à l'emplacement, d'où FreeRDP prend le clavier ; Linux : directement à sa fenêtre) ; sous Windows, la
    /// molette lui est transmise (<see cref="ChildMouseWheel"/>).
    /// </summary>
    public bool FocusOnChildClick
    {
        get => _focusOnChildClick;
        set
        {
            _focusOnChildClick = value;
            _x11?.GrabButtons(value);
        }
    }

    /// <summary>Fenêtre de l'emplacement (parent des fenêtres du client), ou zéro tant qu'elle n'existe pas.</summary>
    public IntPtr SlotHandle => _x11?.Window ?? _handle;

    /// <summary>
    /// Fenêtre où le client crée la sienne (« /parent-window ») : l'emplacement sous Windows, sa fenêtre d'accueil sous
    /// Linux ; zéro tant qu'elle n'existe pas.
    /// </summary>
    public IntPtr ClientParent => _x11?.Parking ?? _handle;

    /// <summary>Vrai si l'emplacement est une fenêtre X11 (Linux).</summary>
    public bool IsX11 => _x11 is not null;

    /// <summary>Pourquoi l'emplacement n'a pas pu être créé (le client ne peut pas s'y intégrer), ou null.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Taille de l'emplacement en pixels de l'écran : celle de la zone de l'onglet qui le contient, même quand il est
    /// masqué (connexion en cours) ; sans cela, le bureau distant démarrerait à la taille minimale (200 × 200).
    /// </summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            var area = _area ?? (Parent as Control) ?? this;
            var size = area.Bounds.Size;
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return size.Width <= 0 || size.Height <= 0 ? (0, 0) : ((int)(size.Width * scale), (int)(size.Height * scale));
        }
    }

    /// <summary>Suit (vrai) ou non les changements du presse-papiers (<see cref="ClipboardChanged"/>, Windows).</summary>
    public void ListenToClipboard(bool listen)
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero || listen == _listensToClipboard)
        {
            return;
        }

        _listensToClipboard = listen ? AddClipboardFormatListener(_handle) : !RemoveClipboardFormatListener(_handle);
    }

    // ---- Fenêtre du client dans l'emplacement.

    /// <summary>
    /// Vrai si <paramref name="window"/> est une fenêtre du client <paramref name="processId"/> créée dans l'emplacement
    /// (Windows : fille de l'emplacement ; Linux : dans sa fenêtre d'accueil ou l'emplacement).
    /// </summary>
    public bool IsClientWindow(IntPtr window, int processId)
    {
        if (_x11 is { } x11)
        {
            return x11.IsClientWindow(window, processId);
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var owner);
        return GetParent(window) == _handle && owner == processId;
    }

    /// <summary>Linux : fenêtre que le client avait ouverte sur le bureau (dialogue), mise hors de l'écran par ZillaTerm.</summary>
    public bool IsHiddenClientDialog(IntPtr window) => _x11?.WasHidden(window) == true;

    /// <summary>Linux : place la fenêtre du client dans l'emplacement, à sa taille. Windows : déjà fille de l'emplacement.</summary>
    public void Adopt(IntPtr window) => _x11?.Adopt(window, PixelSize);

    /// <summary>Nouvelle taille de la fenêtre du client (Windows : message WM_SIZE, que le client FreeRDP de ZillaTerm suit).</summary>
    public void ResizeClient(IntPtr window, (int Width, int Height) pixels)
    {
        if (_x11 is { } x11)
        {
            x11.Resize(window, pixels);
        }
        else if (OperatingSystem.IsWindows())
        {
            PostMessage(window, WmSize, IntPtr.Zero, new IntPtr((pixels.Height << 16) | (pixels.Width & 0xFFFF)));
        }
    }

    /// <summary>
    /// Windows : FreeRDP prend le clavier (son crochet clavier vérifie que l'emplacement a le focus) ou le laisse
    /// (« lParam = 1 » : message de la fenêtre parente, voir wf_client_thread). Linux : rien, le focus est à sa fenêtre.
    /// </summary>
    public void TellClientFocus(IntPtr window, bool focused)
    {
        if (_x11 is null && OperatingSystem.IsWindows())
        {
            PostMessage(window, focused ? WmSetFocus : WmKillFocus, IntPtr.Zero, new IntPtr(1));
        }
    }

    /// <summary>Molette transmise au client (Windows).</summary>
    public void ForwardWheel(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if (_x11 is null && OperatingSystem.IsWindows())
        {
            PostMessage(window, message, wParam, lParam);
        }
    }

    /// <summary>Demande au client de fermer sa fenêtre (il se déconnecte) ; faux si la demande n'a pas pu partir.</summary>
    public bool CloseClient(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (_x11 is { } x11)
        {
            return x11.Close(window);
        }

        return OperatingSystem.IsWindows() && PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Vrai si l'emplacement (ou la fenêtre du client sous Linux) a le focus clavier.</summary>
    public bool HasFocus => _x11?.HasFocus ?? (OperatingSystem.IsWindows() && _handle != IntPtr.Zero && GetFocus() == _handle);

    /// <summary>
    /// Focus clavier à l'emplacement (Windows : FreeRDP en est prévenu et prend le clavier) ou à la fenêtre du client
    /// (Linux ; réessayé tant que l'onglet tout juste affiché n'est pas encore à l'écran).
    /// </summary>
    public void FocusClient()
    {
        if (_closed)
        {
            return;
        }

        if (_x11 is { } x11)
        {
            if (x11.FocusChild())
            {
                _focusRetries = 0;
            }
            else if (x11.Child != IntPtr.Zero && _focusRetries++ < 10)
            {
                DispatcherTimer.RunOnce(FocusClient, TimeSpan.FromMilliseconds(50), DispatcherPriority.Background);
            }
            else
            {
                _focusRetries = 0;
            }
        }
        else if (OperatingSystem.IsWindows() && _handle != IntPtr.Zero)
        {
            SetFocus(_handle);
        }
    }

    /// <summary>Linux : clavier rendu à la fenêtre de ZillaTerm s'il est au client (onglet masqué, clic ailleurs).</summary>
    public void ReleaseKeyboard()
    {
        if (_x11 is { } x11 && _topLevel?.TryGetPlatformHandle() is { HandleDescriptor: "XID", Handle: var topLevel })
        {
            x11.ReleaseFocus(topLevel);
        }
    }

    /// <summary>Plus d'événements : la session est fermée ; la fenêtre native part avec la vue (fin de l'onglet).</summary>
    public void Close()
    {
        _closed = true;
        FocusOnChildClick = false;
        ListenToClipboard(false);
    }

    // ---- Événements de la fenêtre native, sur le thread de l'interface.

    internal void RaiseChildCreated(IntPtr window)
    {
        if (!_closed)
        {
            ChildCreated?.Invoke(window);
        }
    }

    internal void RaiseChildGone(IntPtr window)
    {
        if (!_closed)
        {
            ChildGone?.Invoke(window);
        }
    }

    internal void RaiseFocusChanged(bool focused)
    {
        if (!_closed)
        {
            FocusChanged?.Invoke(focused);
        }
    }

    /// <summary>Fenêtre de l'emplacement redimensionnée (Linux : ConfigureNotify ; Windows : WM_SIZE).</summary>
    internal void RaiseNativeResized(int width, int height)
    {
        if (!_closed && width > 1 && height > 1)
        {
            NotifyPixelSize();
        }
    }

    private void NotifyPixelSize()
    {
        var pixels = PixelSize;
        if (pixels != _lastPixels && pixels.Width > 0 && pixels.Height > 0)
        {
            _lastPixels = pixels;
            PixelSizeChanged?.Invoke();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        // Clic n'importe où dans la fenêtre de ZillaTerm (hors de la session) : le clavier revient à ZillaTerm (Linux).
        _topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _area = Parent as Control;
        if (_area is not null)
        {
            _area.SizeChanged += OnAreaSizeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseKeyboard();
        _topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _topLevel = null;
        if (_area is not null)
        {
            _area.SizeChanged -= OnAreaSizeChanged;
            _area = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnAreaSizeChanged(object? sender, SizeChangedEventArgs e) => NotifyPixelSize();

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e) => ReleaseKeyboard();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            if (parent.HandleDescriptor == "XID" && X11Interop.Usable)
            {
                _x11 = X11SlotHost.Create(parent.Handle, this);
                _x11.GrabButtons(_focusOnChildClick);
                return new PlatformHandle(_x11.Window, "XID");
            }

            if (OperatingSystem.IsWindows() && parent.HandleDescriptor == "HWND")
            {
                _handle = CreateWin32(parent.Handle);
                return new PlatformHandle(_handle, "HWND");
            }

            // Wayland natif, macOS : pas de fenêtre où un client Bureau à distance puisse s'intégrer.
            Error = $"{parent.HandleDescriptor} : {Localization.Strings.PathNotFound}";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            DebugLog.Write("rdp", "Emplacement Bureau à distance non créé", ex);
            Error = ex.Message;
        }

        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_x11 is { } x11 && control.Handle == x11.Window)
        {
            x11.Destroy();
            _x11 = null;
            return;
        }

        if (_handle != IntPtr.Zero && control.Handle == _handle && OperatingSystem.IsWindows())
        {
            ListenToClipboard(false);
            Win32Slots.Remove(_handle);
            DestroyWindow(_handle);
            _handle = IntPtr.Zero;
            return;
        }

        base.DestroyNativeControlCore(control);
    }

    // ---- Windows : fenêtre noire du thread de l'interface ; signale ses changements de taille.

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WmSize = 0x0005;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmClose = 0x0010;
    private const int WmParentNotify = 0x0210;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHorizontalWheel = 0x020E;
    private const int WmClipboardUpdate = 0x031D;
    private const int BlackBrush = 4;

    private IntPtr CreateWin32(IntPtr parent)
    {
        var instance = GetModuleHandle(null);
        if (_win32Class == 0)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WindowProc = Marshal.GetFunctionPointerForDelegate(Win32Proc),
                Instance = instance,
                Background = GetStockObject(BlackBrush),
                ClassName = Win32ClassName,
            };
            _win32Class = RegisterClassEx(ref windowClass);
            if (_win32Class == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        var handle = CreateWindowEx(0, Win32ClassName, "", WsChild | WsVisible | WsClipChildren | WsClipSiblings, 0, 0, 1, 1, parent,
            IntPtr.Zero, instance, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        Win32Slots[handle] = this;
        return handle;
    }

    private static IntPtr Win32WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        if (!Win32Slots.TryGetValue(hwnd, out var slot))
        {
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        if (message == WmClipboardUpdate)
        {
            slot.ClipboardChanged?.Invoke();
            return IntPtr.Zero;
        }

        // Fenêtre enfant créée (WM_CREATE) : lParam est son handle.
        if (message == WmParentNotify && (wParam.ToInt64() & 0xFFFF) == 0x0001)
        {
            slot.RaiseChildCreated(lParam);
        }

        if (slot._focusOnChildClick)
        {
            switch (message)
            {
                // Bouton de la souris enfoncé dans la fenêtre enfant (gauche, droit, milieu, X).
                case WmParentNotify when (wParam.ToInt64() & 0xFFFF) is 0x0201 or 0x0204 or 0x0207 or 0x020B:
                    SetFocus(hwnd);
                    break;
                case WmMouseWheel or WmMouseHorizontalWheel:
                    slot.ChildMouseWheel?.Invoke(message, wParam, lParam);
                    return IntPtr.Zero;
            }
        }

        var result = DefWindowProc(hwnd, message, wParam, lParam);
        switch (message)
        {
            case WmSize:
                slot.RaiseNativeResized((int)(lParam.ToInt64() & 0xFFFF), (int)((lParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case WmSetFocus:
                slot.RaiseFocusChanged(true);
                break;
            case WmKillFocus:
                slot.RaiseFocusChanged(false);
                break;
        }

        return result;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width,
        int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
