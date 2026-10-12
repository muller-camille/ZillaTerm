using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Emplacement de la session dans l'onglet : fenêtre enfant du thread de l'interface, que WPF place, affiche et
/// masque sans jamais attendre un autre thread. Le thread de chaque connexion (<see cref="RdpThread"/>) y crée la
/// fenêtre du contrôle Bureau à distance, l'adapte à sa taille, et l'en retire avant de libérer le contrôle.
/// </summary>
internal sealed class RdpSlot : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private SlotWindow? _window;

    /// <summary>La taille de l'emplacement a changé (thread de l'interface).</summary>
    public event Action? PixelSizeChanged;

    /// <summary>L'emplacement a reçu (vrai) ou perdu (faux) le focus clavier de Windows.</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>Molette reçue par l'emplacement (message, wParam, lParam), quand <see cref="FocusOnChildClick"/>.</summary>
    public event Action<int, IntPtr, IntPtr>? ChildMouseWheel;

    /// <summary>
    /// Fenêtre créée dans l'emplacement (WM_PARENTNOTIFY) : son handle, pendant sa création. Le client FreeRDP crée la
    /// sienne une fois la connexion ouverte.
    /// </summary>
    public event Action<IntPtr>? ChildCreated;

    /// <summary>
    /// Le presse-papiers de Windows a changé (WM_CLIPBOARDUPDATE), tant que <see cref="ListenToClipboard"/> est actif :
    /// journal de débogage des sessions FreeRDP (copier-coller entre le poste et la session).
    /// </summary>
    public event Action? ClipboardChanged;

    /// <summary>
    /// Fenêtre d'un autre processus dans l'emplacement (client FreeRDP) : un clic dedans donne le focus clavier à
    /// l'emplacement, d'où FreeRDP prend le clavier ; la molette lui est transmise (<see cref="ChildMouseWheel"/>).
    /// </summary>
    public bool FocusOnChildClick { get; set; }

    /// <summary>Fenêtre de l'emplacement (parent des fenêtres du contrôle), ou zéro tant qu'elle n'existe pas.</summary>
    public IntPtr SlotHandle => _window?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// Taille de l'emplacement, en pixels de l'écran : celle de sa fenêtre, ou celle que la mise en page lui donne tant
    /// que WPF n'a pas encore placé la fenêtre (emplacement tout juste affiché, juste avant la création du contrôle).
    /// Sans cela, le bureau distant démarrerait à la taille minimale (200 × 200).
    /// </summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            if (IsVisible && _window is { Handle: var handle } && handle != IntPtr.Zero && GetClientRect(handle, out var rect)
                && rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0)
            {
                return (rect.Right - rect.Left, rect.Bottom - rect.Top);
            }

            return LayoutPixelSize();
        }
    }

    /// <summary>Taille donnée par la mise en page de WPF, convertie en pixels de l'écran ; (0, 0) hors de la fenêtre.</summary>
    private (int Width, int Height) LayoutPixelSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            return (0, 0);
        }

        var toDevice = target.TransformToDevice;
        return ((int)Math.Round(ActualWidth * toDevice.M11), (int)Math.Round(ActualHeight * toDevice.M22));
    }

    /// <summary>Suit (vrai) ou non les changements du presse-papiers (<see cref="ClipboardChanged"/>).</summary>
    public void ListenToClipboard(bool listen)
    {
        if (_window is not { Handle: var handle } || handle == IntPtr.Zero || listen == _window.ListensToClipboard)
        {
            return;
        }

        _window.ListensToClipboard = listen
            ? AddClipboardFormatListener(handle)
            : !RemoveClipboardFormatListener(handle);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _window = new SlotWindow(this, hwndParent.Handle);
        return new HandleRef(this, _window.Handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ListenToClipboard(false);
        _window?.DestroyHandle();
        _window = null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Fenêtre noire du thread de l'interface ; signale ses changements de taille.</summary>
    private sealed class SlotWindow : System.Windows.Forms.NativeWindow
    {
        private const int WmSize = 0x0005;
        private const int WmSetFocus = 0x0007;
        private const int WmKillFocus = 0x0008;
        private const int WmEraseBackground = 0x0014;
        private const int WmParentNotify = 0x0210;
        private const int WmMouseWheel = 0x020A;
        private const int WmMouseHorizontalWheel = 0x020E;
        private const int WmClipboardUpdate = 0x031D;
        private const int BlackBrush = 4;

        private readonly RdpSlot _owner;

        /// <summary>Inscrite auprès de Windows pour les changements du presse-papiers.</summary>
        public bool ListensToClipboard { get; set; }

        public SlotWindow(RdpSlot owner, IntPtr parent)
        {
            _owner = owner;
            CreateHandle(new System.Windows.Forms.CreateParams
            {
                Parent = parent,
                Style = WsChild | WsClipChildren | WsClipSiblings,
            });
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WmEraseBackground && GetClientRect(m.HWnd, out var rect))
            {
                FillRect(m.WParam, ref rect, GetStockObject(BlackBrush));
                m.Result = 1;
                return;
            }

            if (m.Msg == WmClipboardUpdate)
            {
                _owner.ClipboardChanged?.Invoke();
                m.Result = IntPtr.Zero;
                return;
            }

            // Fenêtre enfant créée (WM_CREATE) : lParam est son handle.
            if (m.Msg == WmParentNotify && (m.WParam.ToInt64() & 0xFFFF) == 0x0001)
            {
                _owner.ChildCreated?.Invoke(m.LParam);
            }

            if (_owner.FocusOnChildClick)
            {
                switch (m.Msg)
                {
                    // Bouton de la souris enfoncé dans la fenêtre enfant (gauche, droit, milieu, X).
                    case WmParentNotify when (m.WParam.ToInt64() & 0xFFFF) is 0x0201 or 0x0204 or 0x0207 or 0x020B:
                        SetFocus(m.HWnd);
                        break;
                    case WmMouseWheel or WmMouseHorizontalWheel:
                        _owner.ChildMouseWheel?.Invoke(m.Msg, m.WParam, m.LParam);
                        m.Result = IntPtr.Zero;
                        return;
                }
            }

            base.WndProc(ref m);
            switch (m.Msg)
            {
                case WmSize:
                    _owner.PixelSizeChanged?.Invoke();
                    break;
                case WmSetFocus:
                    _owner.FocusChanged?.Invoke(true);
                    break;
                case WmKillFocus:
                    _owner.FocusChanged?.Invoke(false);
                    break;
            }
        }
    }
}
