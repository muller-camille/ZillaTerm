using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Vnc;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglet VNC : écran distant (ajusté à l'onglet ou en taille réelle), clavier et souris transmis au serveur,
/// presse-papiers texte échangé seulement à la demande (boutons), jamais automatiquement.
/// </summary>
/// <remarks>
/// Le mot de passe VNC n'est pas connu de la vue : la session le demande à la fenêtre principale à chaque connexion
/// (<see cref="VncSession"/>), qui le lit dans le coffre au moment de l'envoyer.
/// </remarks>
public partial class VncSessionView : UserControl
{
    private static readonly uint[] Modifiers =
        [VncKeys.ControlLeft, VncKeys.ControlRight, VncKeys.AltLeft, VncKeys.AltRight];

    private readonly object _dirtyLock = new();
    private readonly HashSet<uint> _pressed = [];
    private readonly Dictionary<Key, uint> _combos = [];
    private RfbClient? _client;
    private WriteableBitmap? _bitmap;
    private VncRect _dirty;
    private bool _renderQueued;
    private byte _buttons;
    private (int X, int Y, byte Buttons) _lastPointer = (-1, -1, 0);
    private string? _remoteText;

    /// <summary>Pour le chargeur XAML d'Avalonia seulement ; une session VNC s'affiche avec l'autre constructeur.</summary>
    public VncSessionView()
    {
        InitializeComponent();
        Session = null!;
    }

    public VncSessionView(VncSession session)
    {
        InitializeComponent();
        Session = session;
        session.StateChanged += () => Dispatcher.UIThread.Post(UpdateState);
        session.ClientChanged += client =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Attach(client);
            }
            else
            {
                Dispatcher.UIThread.Invoke(() => Attach(client));
            }
        };
        // Avant les contrôles (et la navigation au clavier de la fenêtre) : Tab, flèches, Alt+lettre vont au serveur.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        // Onglet affiché (sélectionné, ou retour de la fenêtre) : le clavier va à l'écran distant ; caché : plus rien
        // ne reste enfoncé sur le serveur.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        DetachedFromVisualTree += (_, _) => ReleaseAll();
        UpdateState();
    }

    public VncSession Session { get; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin)
        {
            // Focus perdu : les touches relâchées ailleurs ne seraient jamais envoyées.
            ReleaseAll();
        }
    }

    // ===================== État et image =====================

    private void Attach(RfbClient client)
    {
        _client = client;
        _remoteText = null;
        CopyRemoteButton.IsEnabled = false;
        _pressed.Clear();
        _combos.Clear();
        _buttons = 0;
        client.Updated += rect =>
        {
            if (ReferenceEquals(client, _client))
            {
                QueueRender(rect);
            }
        };
        client.Resized += () =>
        {
            if (ReferenceEquals(client, _client))
            {
                QueueRender(new VncRect(0, 0, int.MaxValue / 2, int.MaxValue / 2));
            }
        };
        client.ClipboardReceived += text => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(client, _client))
            {
                _remoteText = text;
                CopyRemoteButton.IsEnabled = text.Length > 0;
                StatusLine.Text = Text.Format(Strings.VncClipboardReceived, text.Length);
            }
        });
        client.Bell += () => Dispatcher.UIThread.Post(SystemBeep.Play);
        _bitmap = null;
        QueueRender(new VncRect(0, 0, int.MaxValue / 2, int.MaxValue / 2));
    }

    private void QueueRender(VncRect rect)
    {
        lock (_dirtyLock)
        {
            _dirty = _dirty.Union(rect);
            if (_renderQueued)
            {
                return;
            }

            _renderQueued = true;
        }

        Dispatcher.UIThread.Post(Render, DispatcherPriority.Render);
    }

    /// <summary>Copie la zone modifiée de l'image reçue dans l'image affichée (une fois par passage de rendu au plus).</summary>
    private void Render()
    {
        VncRect dirty;
        lock (_dirtyLock)
        {
            dirty = _dirty;
            _dirty = default;
            _renderQueued = false;
        }

        if (_client is not { } client)
        {
            return;
        }

        var fb = client.Framebuffer;
        lock (fb.Sync)
        {
            if (fb.Width <= 0 || fb.Height <= 0)
            {
                return;
            }

            if (_bitmap is null || _bitmap.PixelSize.Width != fb.Width || _bitmap.PixelSize.Height != fb.Height)
            {
                var previous = _bitmap;
                // Pixels du serveur : B, G, R puis un octet inutilisé (opaque).
                _bitmap = new WriteableBitmap(new PixelSize(fb.Width, fb.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                Screen.Source = _bitmap;
                previous?.Dispose();
                dirty = new VncRect(0, 0, fb.Width, fb.Height);
                ApplyFit();
            }

            int x = Math.Clamp(dirty.X, 0, fb.Width), y = Math.Clamp(dirty.Y, 0, fb.Height);
            int w = Math.Min(dirty.X + dirty.Width, fb.Width) - x, h = Math.Min(dirty.Y + dirty.Height, fb.Height) - y;
            if (w > 0 && h > 0)
            {
                using var target = _bitmap.Lock();
                for (int row = y; row < y + h; row++)
                {
                    Marshal.Copy(fb.Pixels, (row * fb.Stride) + (x * 4), target.Address + (row * target.RowBytes) + (x * 4), w * 4);
                }
            }
        }

        Screen.InvalidateVisual();
        if (Session.IsConnected)
        {
            StatusLine.Text = Text.Format(Strings.VncStatus, client.DesktopName, Address, fb.Width, fb.Height, client.ProtocolVersion);
        }
    }

    private string Address => Session.Port == RfbClient.DefaultPort ? Session.Host : $"{Session.Host}:{Session.Port}";

    private void UpdateState()
    {
        var state = Session.State;
        Overlay.IsVisible = state != VncSessionState.Connected;
        ReconnectButton.IsVisible = state is VncSessionState.Failed or VncSessionState.Closed;
        foreach (var button in new Control[] { CtrlAltDelButton, SendClipboardButton })
        {
            button.IsEnabled = state == VncSessionState.Connected;
        }

        switch (state)
        {
            case VncSessionState.Connecting:
                OverlayText.Text = Text.Format(Strings.VncConnecting, Address);
                OverlayDetail.Text = "";
                StatusLine.Text = OverlayText.Text;
                break;
            case VncSessionState.Connected:
                Focus();
                break;
            case VncSessionState.Failed:
                OverlayText.Text = Strings.VncFailed;
                OverlayDetail.Text = Session.Error ?? "";
                StatusLine.Text = OverlayText.Text;
                break;
            case VncSessionState.Closed:
                OverlayText.Text = Strings.VncClosedText;
                OverlayDetail.Text = Session.Error ?? "";
                StatusLine.Text = OverlayText.Text;
                ReleaseAll();
                break;
        }
    }

    private void OnReconnect(object? sender, RoutedEventArgs e) => _ = Session.ConnectAsync();

    private void OnFitChanged(object? sender, RoutedEventArgs e) => ApplyFit();

    /// <summary>Ajusté : l'écran est réduit pour tenir dans l'onglet ; sinon taille réelle, avec défilement.</summary>
    private void ApplyFit()
    {
        bool fit = FitButton.IsChecked == true;
        Screen.Stretch = fit ? Stretch.Uniform : Stretch.None;
        Screen.StretchDirection = StretchDirection.DownOnly;
        Scroller.HorizontalScrollBarVisibility = Scroller.VerticalScrollBarVisibility =
            fit ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    // ===================== Souris =====================

    private bool Map(PointerEventArgs e, out int x, out int y)
    {
        x = y = 0;
        if (_client is not { } client || !Session.IsConnected || Screen.Bounds.Width <= 0 || Screen.Bounds.Height <= 0)
        {
            return false;
        }

        var p = e.GetPosition(Screen);
        var fb = client.Framebuffer;
        x = Math.Clamp((int)(p.X * fb.Width / Screen.Bounds.Width), 0, Math.Max(0, fb.Width - 1));
        y = Math.Clamp((int)(p.Y * fb.Height / Screen.Bounds.Height), 0, Math.Max(0, fb.Height - 1));
        return true;
    }

    private void SendPointer(int x, int y)
    {
        if (_lastPointer != (x, y, _buttons))
        {
            _lastPointer = (x, y, _buttons);
            _client?.SendPointer(x, y, _buttons);
        }
    }

    private void OnMouseMove(object? sender, PointerEventArgs e)
    {
        if (Map(e, out var x, out var y))
        {
            SendPointer(x, y);
        }
    }

    private void OnMouseDown(object? sender, PointerPressedEventArgs e) => OnMouseButton(e);

    private void OnMouseUp(object? sender, PointerReleasedEventArgs e) => OnMouseButton(e);

    /// <summary>Bouton enfoncé ou relâché : l'état des trois boutons part avec la position.</summary>
    private void OnMouseButton(PointerEventArgs e)
    {
        Focus();
        if (!Map(e, out var x, out var y))
        {
            return;
        }

        e.Handled = true;
        var buttons = e.GetCurrentPoint(Screen).Properties;
        _buttons = (byte)((buttons.IsLeftButtonPressed ? 1 : 0)
                          | (buttons.IsMiddleButtonPressed ? 2 : 0)
                          | (buttons.IsRightButtonPressed ? 4 : 0));
        // Bouton tenu : la souris reste à l'écran distant même hors de l'image (glisser), jusqu'au relâchement.
        e.Pointer.Capture(_buttons != 0 ? Screen : null);
        SendPointer(x, y);
    }

    private void OnMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Map(e, out var x, out var y) && e.Delta.Y != 0)
        {
            e.Handled = true;
            // Un cran de molette vaut 1 (120 sous WPF) : au moins un cran par évènement, comme en WPF.
            int delta = (int)Math.Round(e.Delta.Y * 120);
            for (int i = 0; i < Math.Max(1, Math.Abs(delta) / 120); i++)
            {
                _client?.SendWheel(x, y, delta == 0 ? Math.Sign(e.Delta.Y) : delta);
            }
        }
    }

    // ===================== Clavier =====================

    private void Press(uint keysym)
    {
        _pressed.Add(keysym);
        _client?.SendKey(keysym, down: true);
    }

    private void Release(uint keysym)
    {
        _pressed.Remove(keysym);
        _client?.SendKey(keysym, down: false);
    }

    /// <summary>Focus perdu ou session fermée : rien ne doit rester enfoncé sur le serveur.</summary>
    private void ReleaseAll()
    {
        foreach (var keysym in _pressed.ToList())
        {
            Release(keysym);
        }

        _combos.Clear();
        if (_buttons != 0 && _lastPointer.X >= 0)
        {
            _buttons = 0;
            SendPointer(_lastPointer.X, _lastPointer.Y);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Session.IsConnected)
        {
            return;
        }

        var key = e.Key;
        if (Special(key) is { } keysym)
        {
            Press(keysym);
            e.Handled = true;
            return;
        }

        // Ctrl ou Alt + lettre ou chiffre (pas AltGr, qui produit un caractère) : la touche elle-même.
        var modifiers = e.KeyModifiers;
        bool altGr = _pressed.Contains(VncKeys.AltRight) && modifiers.HasFlag(KeyModifiers.Control);
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0 && !altGr && Base(key) is { } baseKey)
        {
            _combos[key] = baseKey;
            Press(baseKey);
            e.Handled = true;
        }

        // Les autres touches arrivent en caractères (TextInput), disposition du clavier de ce poste comprise.
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (!Session.IsConnected)
        {
            return;
        }

        var key = e.Key;
        if (Special(key) is { } keysym)
        {
            Release(keysym);
            e.Handled = true;
        }
        else if (_combos.Remove(key, out var baseKey))
        {
            Release(baseKey);
            e.Handled = true;
        }
    }

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (!Session.IsConnected || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        // Caractère obtenu avec AltGr : Ctrl et Alt sont relâchés le temps de l'envoyer.
        var held = Modifiers.Where(_pressed.Contains).ToList();
        foreach (var keysym in held)
        {
            _client?.SendKey(keysym, down: false);
        }

        foreach (var c in e.Text)
        {
            var keysym = VncKeys.FromChar(c);
            if (keysym != 0)
            {
                _client?.SendKey(keysym, down: true);
                _client?.SendKey(keysym, down: false);
            }
        }

        foreach (var keysym in held)
        {
            _client?.SendKey(keysym, down: true);
        }
    }

    private static uint? Special(Key key) => key switch
    {
        Key.Back => VncKeys.BackSpace,
        Key.Tab => VncKeys.Tab,
        Key.Enter => VncKeys.Return,
        Key.Escape => VncKeys.Escape,
        Key.PageUp => VncKeys.PageUp,
        Key.PageDown => VncKeys.PageDown,
        Key.End => VncKeys.End,
        Key.Home => VncKeys.Home,
        Key.Left => VncKeys.Left,
        Key.Up => VncKeys.Up,
        Key.Right => VncKeys.Right,
        Key.Down => VncKeys.Down,
        Key.Insert => VncKeys.Insert,
        Key.Delete => VncKeys.Delete,
        Key.Pause => VncKeys.Pause,
        Key.Scroll => VncKeys.ScrollLock,
        Key.Snapshot => VncKeys.Print,
        Key.Apps => VncKeys.Menu,
        >= Key.F1 and <= Key.F24 => VncKeys.Function(key - Key.F1 + 1),
        Key.LeftShift => VncKeys.ShiftLeft,
        Key.RightShift => VncKeys.ShiftRight,
        Key.LeftCtrl => VncKeys.ControlLeft,
        Key.RightCtrl => VncKeys.ControlRight,
        Key.LeftAlt => VncKeys.AltLeft,
        Key.RightAlt => VncKeys.AltRight,
        Key.LWin => VncKeys.SuperLeft,
        Key.RWin => VncKeys.SuperRight,
        _ => null,
    };

    private static uint? Base(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 'a' + (uint)(key - Key.A),
        >= Key.D0 and <= Key.D9 => '0' + (uint)(key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => '0' + (uint)(key - Key.NumPad0),
        Key.Space => ' ',
        _ => null,
    };

    // ===================== Barre de la session =====================

    private async void OnCtrlAltDel(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || !Session.IsConnected)
        {
            return;
        }

        // Sur certaines consoles de machines virtuelles, Ctrl+Alt+Suppr redémarre la machine.
        if (!await ConfirmDialog.ConfirmAsync(TopLevel.GetTopLevel(this) as Window, new ConfirmRequest
            {
                Title = Strings.VncCtrlAltDelAction,
                Heading = Text.Format(Strings.VncCtrlAltDelHeading, Session.Label),
                Message = Strings.VncCtrlAltDelMessage,
                Kind = ConfirmKind.Warning,
                Actions = [Strings.VncCtrlAltDelAction],
            }) || !Session.IsConnected || !ReferenceEquals(client, _client))
        {
            return;
        }

        uint[] keys = [VncKeys.ControlLeft, VncKeys.AltLeft, VncKeys.Delete];
        foreach (var k in keys)
        {
            client.SendKey(k, down: true);
        }

        foreach (var k in keys.Reverse())
        {
            client.SendKey(k, down: false);
        }

        Focus();
    }

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    /// <summary>Envoie le texte du presse-papiers de ce poste au serveur (à la demande seulement).</summary>
    private async void OnSendClipboard(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || !Session.IsConnected || Clipboard is not { } clipboard)
        {
            return;
        }

        string text;
        try
        {
            text = await clipboard.TryGetTextAsync() ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Presse-papiers occupé par une autre application : comme s'il était vide.
            text = "";
        }

        if (text.Length == 0)
        {
            StatusLine.Text = Strings.VncClipboardEmpty;
            return;
        }

        // Lecture du presse-papiers asynchrone : la session a pu se fermer ou changer de connexion entre-temps.
        if (!Session.IsConnected || !ReferenceEquals(client, _client))
        {
            return;
        }

        client.SendClipboard(text);
        StatusLine.Text = Text.Format(Strings.VncClipboardSent, text.Length);
        Focus();
    }

    private async void OnCopyRemote(object? sender, RoutedEventArgs e)
    {
        if (_remoteText is { Length: > 0 } text && Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                StatusLine.Text = Text.Format(Strings.VncClipboardCopied, text.Length);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Presse-papiers occupé par une autre application : nouvel essai possible.
            }
        }

        Focus();
    }
}
