using System.Runtime.InteropServices;
using Avalonia.Threading;
using ZillaTerm.Core.Diagnostics;
using static ZillaTerm.App.Services.X11Interop;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Emplacements des sessions Bureau à distance sous X11 (ou XWayland) : une connexion à l'écran X propre à ZillaTerm
/// (celle d'Avalonia reste à Avalonia) et un fil qui en lit les événements sans jamais attendre l'interface. Le client
/// FreeRDP crée sa fenêtre dans une fenêtre d'accueil (<see cref="X11Slot.Parking"/>, hors de l'écran, toujours
/// affichée : FreeRDP attend que sa fenêtre soit visible avant de continuer), puis ZillaTerm la place dans l'emplacement
/// de l'onglet ; une fenêtre que FreeRDP recréerait (reconnexion, selon sa version) arrive de même, onglet affiché ou non.
/// </summary>
/// <remarks>
/// <para>
/// Un clic dans la session donne le clavier au client FreeRDP, comme sous Windows : bouton saisi sur l'emplacement
/// (XGrabButton synchrone), focus donné à sa fenêtre, puis le clic lui est rejoué (ReplayPointer). Fait sur le fil des
/// événements, tout de suite : la souris de l'écran reste bloquée jusque-là.
/// </para>
/// <para>
/// Les autres fenêtres que le client intégré ouvre sur le bureau (xfreerdp : « Reconnecting… Close this window to
/// cancel » pendant une reconnexion automatique) vont dans la fenêtre d'accueil, hors de l'écran : l'onglet dit lui-même
/// où en est la reconnexion. Elles sont reconnues à leur identifiant, que le serveur X attribue par connexion : même plage
/// que la fenêtre du client.
/// </para>
/// </remarks>
internal static class X11SlotHost
{
    private static readonly Lock Gate = new();

    // Emplacements par fenêtre (emplacement et fenêtre d'accueil).
    private static readonly Dictionary<nint, X11Slot> Slots = [];
    private static nint _display;
    private static nint _root;

    // Bits des identifiants X propres à une connexion (resource_id_mask du serveur) ; zéro s'il est illisible.
    private static nint _resourceMask;

    /// <summary>Connexion de ZillaTerm à l'écran X, ouverte au premier emplacement ; zéro si elle est impossible.</summary>
    public static nint Display
    {
        get
        {
            lock (Gate)
            {
                if (_display == IntPtr.Zero && Usable)
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    if (_display != IntPtr.Zero)
                    {
                        WmProtocols = XInternAtom(_display, "WM_PROTOCOLS", 0);
                        WmDeleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", 0);
                        // resource_mask de la structure Display (Xlib.h : private4, 64 bits) : bits bas contigus.
                        var mask = Marshal.ReadIntPtr(_display, 48);
                        _resourceMask = mask > 0xFFFF && (mask & (mask + 1)) == 0 ? mask : IntPtr.Zero;
                        _root = XDefaultRootWindow(_display);
                        if (_resourceMask != IntPtr.Zero)
                        {
                            XSelectInput(_display, _root, (nint)SubstructureNotifyMask);
                        }

                        new Thread(ReadEvents) { IsBackground = true, Name = "Bureau à distance X11" }.Start();
                    }
                }

                return _display;
            }
        }
    }

    public static nint WmProtocols { get; private set; }

    public static nint WmDeleteWindow { get; private set; }

    /// <summary>
    /// Crée l'emplacement, fenêtre fille de <paramref name="parent"/> (fenêtre d'Avalonia), et sa fenêtre d'accueil.
    /// Visuel et profondeur par défaut de l'écran, ceux de la fenêtre de FreeRDP : la capture de l'emplacement la contient.
    /// </summary>
    /// <exception cref="InvalidOperationException">Pas de connexion à l'écran X.</exception>
    public static X11Slot Create(nint parent, RdpSlot owner)
    {
        var display = Display;
        if (display == IntPtr.Zero)
        {
            throw new InvalidOperationException("XOpenDisplay");
        }

        int screen = XDefaultScreen(display);
        int depth = XDefaultDepth(display, screen);
        var attributes = new XSetWindowAttributes
        {
            // Fond de la zone de la session (#0C0C0C), noir sur un écran de moins de 24 bits.
            BackgroundPixel = depth >= 24 ? 0x0C0C0C : XBlackPixel(display, screen),
            BorderPixel = 0,
            Colormap = XDefaultColormap(display, screen),
        };
        var visual = XDefaultVisual(display, screen);
        var window = XCreateWindow(display, parent, 0, 0, 1, 1, 0, depth, InputOutput, visual,
            CWBackPixel | CWBorderPixel | CWColormap, ref attributes);
        // Hors de l'écran, sans gestionnaire de fenêtres (override-redirect), 1 × 1 : jamais vue.
        attributes.OverrideRedirect = 1;
        var parking = XCreateWindow(display, XDefaultRootWindow(display), -100, -100, 1, 1, 0, depth, InputOutput, visual,
            CWBackPixel | CWBorderPixel | CWColormap | CWOverrideRedirect, ref attributes);
        var slot = new X11Slot(display, window, parking, owner);
        lock (Gate)
        {
            Slots[window] = slot;
            Slots[parking] = slot;
        }

        XSelectInput(display, window, (nint)(StructureNotifyMask | SubstructureNotifyMask));
        XSelectInput(display, parking, (nint)SubstructureNotifyMask);
        XMapWindow(display, parking);
        XSync(display, 0);
        DebugLog.Write("rdp", $"Emplacement X11 0x{window:X} (accueil 0x{parking:X}) dans la fenêtre 0x{parent:X}");
        return slot;
    }

    public static void Remove(X11Slot slot)
    {
        lock (Gate)
        {
            Slots.Remove(slot.Window);
            Slots.Remove(slot.Parking);
        }
    }

    private static X11Slot? Find(nint window)
    {
        lock (Gate)
        {
            return Slots.GetValueOrDefault(window);
        }
    }

    /// <summary>Emplacement dont le client a aussi créé <paramref name="window"/> (même connexion au serveur X).</summary>
    private static X11Slot? FindByClient(nint window)
    {
        lock (Gate)
        {
            return Slots.Values.FirstOrDefault(s => s.Child != IntPtr.Zero && (s.Child & ~_resourceMask) == (window & ~_resourceMask));
        }
    }

    /// <summary>Emplacement où se trouve la fenêtre du client <paramref name="child"/>.</summary>
    private static X11Slot? FindByChild(nint child)
    {
        lock (Gate)
        {
            return child == IntPtr.Zero ? null : Slots.Values.FirstOrDefault(s => s.Child == child);
        }
    }

    /// <summary>
    /// Fil des événements : XNextEvent l'attend sans garder la connexion (les autres fils l'utilisent pendant ce temps,
    /// sans lui prendre ses événements).
    /// </summary>
    private static void ReadEvents()
    {
        var display = _display;
        while (true)
        {
            XNextEvent(display, out var e);
            try
            {
                Handle(display, e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DebugLog.Write("rdp", "Événement X11 de l'emplacement non traité", ex);
            }
        }
    }

    private static void Handle(nint display, in XEvent e)
    {
        switch (e.Type)
        {
            case ButtonPress:
                // Clic dans la session (bouton saisi sur l'emplacement) : le clavier va au client, puis le clic lui est
                // rejoué. Toujours rejoué, même si l'emplacement n'existe plus : la souris de l'écran attend.
                if (Find(e.Window) is { Child: var child } && child != IntPtr.Zero)
                {
                    XSetInputFocus(display, child, RevertToParent, e.Time);
                }

                XAllowEvents(display, ReplayPointer, e.Time);
                XFlush(display);
                break;
            case CreateNotify when e.Window == _root && _root != IntPtr.Zero && FindByClient(e.Window2) is { } slot:
                // Autre fenêtre du client intégré, sur le bureau (dialogue de reconnexion) : hors de l'écran.
                slot.Hide(e.Window2);
                break;
            case CreateNotify when Find(e.Window) is { } slot && e.Window == slot.Parking:
                // Fenêtre créée directement dans la fenêtre d'accueil.
                slot.OnArrived(e.Window2);
                break;
            case ReparentNotify when Find(e.Window) is { } slot:
                if (e.Window == slot.Parking && e.Window3 == slot.Parking)
                {
                    // Fenêtre de FreeRDP, créée à la racine puis placée dans la fenêtre d'accueil (/parent-window).
                    slot.OnArrived(e.Window2);
                }
                else if (e.Window == slot.Window && e.Window3 != slot.Window)
                {
                    slot.OnGone(e.Window2);
                }

                break;
            case DestroyNotify when Find(e.Window) is { } slot && e.Window2 != slot.Window && e.Window2 != slot.Parking:
                slot.OnGone(e.Window2);
                break;
            case ConfigureNotify when e.Window == e.Window2 && Find(e.Window) is { } slot && e.Window == slot.Window:
                slot.OnResized(e.Width, e.Height);
                break;
            case FocusIn or FocusOut when FindByChild(e.Window) is { } slot:
                slot.OnChildFocus(e.Type == FocusIn);
                break;
        }
    }
}

/// <summary>
/// Un emplacement X11 (<see cref="X11SlotHost"/>) : la fenêtre de l'onglet, sa fenêtre d'accueil et la fenêtre du client
/// FreeRDP qui y est placée. Ses événements arrivent sur le fil X11 et sont transmis à <see cref="RdpSlot"/> sur le fil
/// de l'interface.
/// </summary>
internal sealed class X11Slot(nint display, nint window, nint parking, RdpSlot owner)
{
    // Boutons de la souris saisis sur l'emplacement : gauche, milieu, droit (la molette va directement au client).
    private static readonly uint[] Buttons = [1, 2, 3];

    private bool _grabbed;
    private volatile bool _closed;
    private nint _child;

    // Fenêtres du client retirées du bureau (dialogues).
    private readonly HashSet<nint> _hidden = [];

    public nint Display { get; } = display;

    public nint Window { get; } = window;

    /// <summary>Fenêtre d'accueil donnée au client FreeRDP (« /parent-window ») : hors de l'écran, toujours affichée.</summary>
    public nint Parking { get; } = parking;

    /// <summary>Fenêtre du client placée dans l'emplacement ; zéro sinon (lue aussi par le fil X11).</summary>
    public nint Child
    {
        get => Volatile.Read(ref _child);
        private set => Volatile.Write(ref _child, value);
    }

    /// <summary>Saisit (vrai) ou relâche les boutons de la souris sur l'emplacement : un clic dans la session lui donne le clavier.</summary>
    public void GrabButtons(bool grab)
    {
        if (grab == _grabbed || _closed)
        {
            return;
        }

        _grabbed = grab;
        foreach (var button in Buttons)
        {
            if (grab)
            {
                XGrabButton(Display, button, AnyModifier, Window, 0, (uint)ButtonPressMask, GrabModeSync, GrabModeAsync, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                XUngrabButton(Display, button, AnyModifier, Window);
            }
        }

        XFlush(Display);
    }

    /// <summary>
    /// Fenêtre du client hors de l'onglet (dialogue) placée dans la fenêtre d'accueil, hors de l'écran (fil X11) ; si
    /// c'est une nouvelle fenêtre de sa session, elle y sera reconnue et placée dans l'onglet comme la première.
    /// </summary>
    internal void Hide(nint window)
    {
        if (_closed)
        {
            return;
        }

        lock (_hidden)
        {
            _hidden.Add(window);
        }

        XReparentWindow(Display, window, Parking, 0, 0);
        XFlush(Display);
        DebugLog.Write("rdp", $"Fenêtre 0x{window:X} du client Bureau à distance hors de l'onglet : placée hors de l'écran");
    }

    /// <summary>Vrai si <paramref name="window"/> est une fenêtre du client retirée du bureau (<see cref="Hide"/>).</summary>
    public bool WasHidden(nint window)
    {
        lock (_hidden)
        {
            return _hidden.Contains(window);
        }
    }

    /// <summary>
    /// Place la fenêtre du client dans l'emplacement, à sa taille, et suit son focus clavier. Elle est retirée de la
    /// fenêtre d'accueil : une nouvelle fenêtre (reconnexion) y arrivera de même.
    /// </summary>
    public void Adopt(nint child, (int Width, int Height) pixels)
    {
        if (_closed)
        {
            return;
        }

        Child = child;
        XSelectInput(Display, child, (nint)FocusChangeMask);
        XReparentWindow(Display, child, Window, 0, 0);
        XMoveResizeWindow(Display, child, 0, 0, (uint)Math.Max(pixels.Width, 1), (uint)Math.Max(pixels.Height, 1));
        XFlush(Display);
    }

    public void Resize(nint child, (int Width, int Height) pixels)
    {
        if (!_closed && child == Child && child != IntPtr.Zero)
        {
            XMoveResizeWindow(Display, child, 0, 0, (uint)Math.Max(pixels.Width, 1), (uint)Math.Max(pixels.Height, 1));
            XFlush(Display);
        }
    }

    /// <summary>
    /// Clavier au client. Faux si l'emplacement n'est pas encore visible à l'écran (onglet tout juste affiché) : X le
    /// refuserait ; l'appelant réessaie.
    /// </summary>
    public bool FocusChild()
    {
        if (_closed || Child == IntPtr.Zero || !IsViewableWindow(Display, Child, out _, out _))
        {
            return false;
        }

        XSetInputFocus(Display, Child, RevertToParent, CurrentTime);
        XFlush(Display);
        return true;
    }

    /// <summary>Vrai si le clavier est au client (ou à l'emplacement).</summary>
    public bool HasFocus
    {
        get
        {
            if (_closed)
            {
                return false;
            }

            XGetInputFocus(Display, out var focus, out _);
            return focus != IntPtr.Zero && (focus == Child || focus == Window);
        }
    }

    /// <summary>Clavier rendu à la fenêtre d'Avalonia <paramref name="topLevel"/>, s'il est au client.</summary>
    public void ReleaseFocus(nint topLevel)
    {
        if (topLevel != IntPtr.Zero && HasFocus && IsViewableWindow(Display, topLevel, out _, out _))
        {
            XSetInputFocus(Display, topLevel, RevertToParent, CurrentTime);
            XFlush(Display);
        }
    }

    /// <summary>Demande au client de fermer sa fenêtre (WM_DELETE_WINDOW) : il se déconnecte. Faux si l'envoi échoue.</summary>
    public bool Close(nint child)
    {
        if (_closed || child == IntPtr.Zero || X11SlotHost.WmProtocols == IntPtr.Zero)
        {
            return false;
        }

        var message = new XEvent
        {
            Type = ClientMessage,
            Window = child,
            Window2 = X11SlotHost.WmProtocols,
            Format = 32,
            Time = X11SlotHost.WmDeleteWindow,
            Data1 = CurrentTime,
        };
        bool sent = XSendEvent(Display, child, 0, 0, ref message) != 0;
        XFlush(Display);
        return sent;
    }

    /// <summary>
    /// Vrai si <paramref name="child"/> est bien une fenêtre du processus <paramref name="processId"/> (_NET_WM_PID), dans
    /// la fenêtre d'accueil ou dans l'emplacement.
    /// </summary>
    public bool IsClientWindow(nint child, int processId)
    {
        if (_closed)
        {
            return false;
        }

        var parent = X11Interop.Parent(Display, child);
        return (parent == Parking || parent == Window) && X11Interop.ProcessId(Display, child) == processId;
    }

    /// <summary>Détruit l'emplacement et sa fenêtre d'accueil (et ce qu'elles contiennent encore).</summary>
    public void Destroy()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        X11SlotHost.Remove(this);
        XDestroyWindow(Display, Window);
        XDestroyWindow(Display, Parking);
        XFlush(Display);
        Child = IntPtr.Zero;
    }

    // ---- Fil X11 → fil de l'interface.

    internal void OnArrived(nint child) => Dispatcher.UIThread.Post(() => owner.RaiseChildCreated(child));

    internal void OnGone(nint child)
    {
        Interlocked.CompareExchange(ref _child, IntPtr.Zero, child);

        Dispatcher.UIThread.Post(() => owner.RaiseChildGone(child));
    }

    internal void OnResized(int width, int height) => Dispatcher.UIThread.Post(() => owner.RaiseNativeResized(width, height));

    internal void OnChildFocus(bool focused) => Dispatcher.UIThread.Post(() => owner.RaiseFocusChanged(focused));
}
