using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.Sharing;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglet d'une session partagée par une autre personne : son terminal (à la taille du sien, ajusté à l'onglet) ou son
/// écran (Bureau à distance, VNC), en lecture seule ; la frappe ne part dans la session qu'une fois la main donnée par
/// la personne qui partage, et le dit tant qu'elle l'a.
/// </summary>
public partial class SharedSessionView : UserControl
{
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(4);

    private readonly ConcurrentQueue<ShareMessage> _incoming = new();
    private readonly DispatcherTimer _hintTimer = new() { Interval = HintDuration };
    private int _drainScheduled;
    private TerminalEmulator? _emulator;
    private ScreenTileDecoder? _decoder;
    private WriteableBitmap? _bitmap;

    // Lignes de l'écran reçu (RGB 5-6-5), copiées dans l'image affichée.
    private byte[] _screenBytes = [];
    private bool _hasControl;
    private bool _asked;
    private bool _paused;
    private bool _hint;

    /// <summary>Pour le chargeur XAML d'Avalonia seulement ; une session regardée s'affiche avec l'autre constructeur.</summary>
    public SharedSessionView()
    {
        InitializeComponent();
        Watched = null!;
    }

    internal SharedSessionView(WatchedShare watched)
    {
        InitializeComponent();
        Watched = watched;
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _hint = false;
            ShowNotice();
        };
        bool terminal = watched.Info.Kind == ShareKind.Terminal;
        TerminalBox.IsVisible = terminal;
        Screen.IsVisible = !terminal;
        Terminal.Input += OnInput;
        UpdateControl();
        // Les messages arrivés depuis l'accord (l'écran actuel) sont remis d'abord, dans l'ordre.
        watched.Viewer.MessageReceived += OnMessage;
        watched.Viewer.Ended += reason => Dispatcher.UIThread.Post(() => ShowEnded(reason));
    }

    internal WatchedShare Watched { get; }

    /// <summary>Fin de la session regardée (raison), ou null tant qu'elle continue.</summary>
    internal ShareEndReason? EndReason { get; private set; }

    /// <summary>« Quitter » ou « Fermer l'onglet » : la fenêtre principale ferme l'onglet.</summary>
    internal event Action? LeaveRequested;

    /// <summary>Main reçue (vrai) ou perdue (faux), pour le journal d'intervention.</summary>
    internal event Action<bool>? ControlChanged;

    /// <summary>Collage de plusieurs lignes avec la main : confirmé comme dans ses propres sessions (fenêtre principale).</summary>
    internal Func<TerminalInput, Task<bool>>? PasteGuard { get; set; }

    internal void FocusContent()
    {
        if (Watched.Info.Kind == ShareKind.Terminal)
        {
            Terminal.Focus();
        }
        else
        {
            Focus();
        }
    }

    /// <summary>Onglet affiché (sélectionné, dans une fenêtre visible).</summary>
    private bool IsShown => TopLevel.GetTopLevel(this) is not null && IsEffectivelyVisible;

    // ===================== Messages =====================

    private void OnMessage(ShareMessage message)
    {
        _incoming.Enqueue(message);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            // Un seul passage pour plusieurs messages reçus en rafale.
            Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
        }
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        bool terminalChanged = false, screenChanged = false;
        while (_incoming.TryDequeue(out var message))
        {
            switch (message.Type)
            {
                case ShareMessageType.TerminalReset when message.ReadReset() is var (columns, rows, screen):
                    _emulator = new TerminalEmulator(columns, rows);
                    _emulator.Feed(screen);
                    Terminal.Emulator = _emulator;
                    terminalChanged = true;
                    break;
                case ShareMessageType.TerminalData when _emulator is not null:
                    _emulator.Feed(message.ReadText());
                    terminalChanged = true;
                    break;
                case ShareMessageType.ScreenFrame:
                    _decoder ??= new ScreenTileDecoder();
                    screenChanged |= _decoder.Apply(message.Payload);
                    break;
                case ShareMessageType.Control:
                    bool granted = message.ReadFlag();
                    if (granted != _hasControl)
                    {
                        _hasControl = granted;
                        ControlChanged?.Invoke(granted);
                    }

                    _asked = false;
                    UpdateControl();
                    if (granted && IsShown)
                    {
                        Terminal.Focus();
                    }

                    break;
                case ShareMessageType.Notice:
                    _paused = message.ReadByte() == (byte)ShareNotice.ScreenPaused;
                    ShowNotice();
                    break;
            }
        }

        if (terminalChanged)
        {
            Terminal.Refresh();
        }

        if (screenChanged && _decoder is { Width: > 0, Height: > 0 } decoder)
        {
            ShowScreen(decoder);
        }
    }

    /// <summary>Copie l'écran décodé (RGB 5-6-5, ligne après ligne) dans l'image affichée.</summary>
    private void ShowScreen(ScreenTileDecoder decoder)
    {
        int width = decoder.Width, height = decoder.Height;
        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            var previous = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Rgb565, AlphaFormat.Opaque);
            Screen.Source = _bitmap;
            previous?.Dispose();
        }

        var source = MemoryMarshal.AsBytes(decoder.Pixels.AsSpan(0, width * height));
        if (_screenBytes.Length != source.Length)
        {
            _screenBytes = new byte[source.Length];
        }

        source.CopyTo(_screenBytes);
        using (var target = _bitmap.Lock())
        {
            int row = width * 2;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(_screenBytes, y * row, target.Address + (y * target.RowBytes), row);
            }
        }

        Screen.InvalidateVisual();
    }

    // ===================== Main =====================

    private async void OnInput(TerminalInput input)
    {
        if (EndReason is not null)
        {
            return;
        }

        if (!_hasControl)
        {
            // Lecture seule : rien ne part ; le dire, brièvement.
            if (input.IsTyping)
            {
                _hint = true;
                _hintTimer.Stop();
                _hintTimer.Start();
                ShowNotice();
            }

            return;
        }

        if (input.IsMultiLinePaste && _emulator is { BracketedPaste: false } && PasteGuard is { } guard
            // Après la question : main reprise ou partage fini entre-temps, rien ne part.
            && (!await guard(input) || EndReason is not null || !_hasControl))
        {
            return;
        }

        _ = Watched.Viewer.SendInputAsync(input);
    }

    private async void OnControl(object? sender, RoutedEventArgs e)
    {
        if (EndReason is not null)
        {
            return;
        }

        bool want = !_hasControl && !_asked;
        if (_hasControl)
        {
            _hasControl = false;
            ControlChanged?.Invoke(false);
        }

        _asked = want;
        UpdateControl();
        await Watched.Viewer.RequestControlAsync(want);
    }

    private void UpdateControl()
    {
        bool possible = Watched.Info.ControlAllowed && Watched.Info.Kind == ShareKind.Terminal && EndReason is null;
        ControlButton.IsVisible = possible;
        ControlButton.Content = _hasControl ? Strings.WatchGiveBack : _asked ? Strings.WatchCancelRequest : Strings.WatchAskControl;
        ToolTip.SetTip(ControlButton, _hasControl ? Strings.WatchGiveBackTip : _asked ? null : Strings.WatchAskControlTip);
        ControlBanner.IsVisible = _hasControl;
        ControlText.Text = Text.Format(Strings.WatchHasControl, Watched.Info.Sharer);
        UpdateStatus();
    }

    private void ShowNotice()
    {
        string? text = EndReason is not null ? null
            : _paused ? Text.Format(Strings.WatchScreenPaused, Watched.Info.Sharer)
            : _hint ? (Watched.Info.ControlAllowed && Watched.Info.Kind == ShareKind.Terminal ? Strings.WatchReadOnlyAsk : Strings.WatchReadOnly)
            : null;
        NoticeBanner.IsVisible = text is not null;
        NoticeText.Text = text ?? "";
    }

    private void UpdateStatus()
    {
        var info = Watched.Info;
        var parts = new List<string>
        {
            Text.Format(Strings.WatchSharedBy, info.Sharer, info.Machine),
            info.Label,
            _hasControl ? Strings.WatchModeControl : Strings.WatchModeReadOnly,
            Text.Format(Strings.WatchUntil, info.EndsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)),
            Text.Format(Strings.WatchCode, Watched.Viewer.VerificationCode),
            Watched.Viewer.IsRelay ? Strings.WatchRouteRelay : Strings.WatchRouteDirect,
        };
        StatusLine.Text = string.Join(" · ", parts);
        ToolTip.SetTip(StatusLine, StatusLine.Text);
    }

    // ===================== Fin =====================

    private void ShowEnded(ShareEndReason reason)
    {
        if (EndReason is not null)
        {
            return;
        }

        EndReason = reason;
        if (_hasControl)
        {
            _hasControl = false;
            ControlChanged?.Invoke(false);
        }

        _asked = false;
        _hintTimer.Stop();
        UpdateControl();
        ShowNotice();
        LeaveButton.IsVisible = false;
        Overlay.IsVisible = true;
        OverlayText.Text = EndText(reason, Watched.Info.Sharer);
        OverlayDetail.Text = Strings.WatchEndedDetail;
    }

    internal static string EndText(ShareEndReason reason, string sharer) => reason switch
    {
        ShareEndReason.Stopped => Text.Format(Strings.WatchEndStopped, sharer),
        ShareEndReason.Excluded => Text.Format(Strings.WatchEndExcluded, sharer),
        ShareEndReason.Expired => Strings.WatchEndExpired,
        ShareEndReason.SessionClosed => Strings.WatchEndSessionClosed,
        ShareEndReason.Locked => Text.Format(Strings.WatchEndLocked, sharer),
        ShareEndReason.ApplicationClosed => Text.Format(Strings.WatchEndClosed, sharer),
        ShareEndReason.Lost => Strings.WatchEndLost,
        ShareEndReason.Left => Strings.WatchEndLeft,
        _ => Strings.WatchEndOther,
    };

    private void OnLeave(object? sender, RoutedEventArgs e) => LeaveRequested?.Invoke();
}
