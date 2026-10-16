using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _hintTimer;
    private int _drainScheduled;
    private TerminalEmulator? _emulator;
    private ScreenTileDecoder? _decoder;
    private WriteableBitmap? _bitmap;
    private bool _hasControl;
    private bool _asked;
    private bool _paused;
    private bool _hint;

    internal SharedSessionView(WatchedShare watched)
    {
        InitializeComponent();
        Watched = watched;
        _hintTimer = new DispatcherTimer { Interval = HintDuration };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _hint = false;
            ShowNotice();
        };
        bool terminal = watched.Info.Kind == ShareKind.Terminal;
        TerminalBox.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        Screen.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        Terminal.Input += OnInput;
        UpdateControl();
        // Les messages arrivés depuis l'accord (l'écran actuel) sont remis d'abord, dans l'ordre.
        watched.Viewer.MessageReceived += OnMessage;
        watched.Viewer.Ended += reason => Dispatcher.BeginInvoke(() => ShowEnded(reason));
    }

    internal WatchedShare Watched { get; }

    /// <summary>Fin de la session regardée (raison), ou null tant qu'elle continue.</summary>
    internal ShareEndReason? EndReason { get; private set; }

    /// <summary>« Quitter » ou « Fermer l'onglet » : la fenêtre principale ferme l'onglet.</summary>
    internal event Action? LeaveRequested;

    /// <summary>Main reçue (vrai) ou perdue (faux), pour le journal d'intervention.</summary>
    internal event Action<bool>? ControlChanged;

    /// <summary>Collage de plusieurs lignes avec la main : confirmé comme dans ses propres sessions (fenêtre principale).</summary>
    internal Func<TerminalInput, bool>? PasteGuard { get; set; }

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

    // ===================== Messages =====================

    private void OnMessage(ShareMessage message)
    {
        _incoming.Enqueue(message);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            // Un seul passage pour plusieurs messages reçus en rafale.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, Drain);
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
                    _decoder ??= new ScreenTileDecoder(ScreenTileEncoder.MaxTilesBetweenKeys);
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
                    if (granted && IsVisible)
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
            if (_bitmap is null || _bitmap.PixelWidth != decoder.Width || _bitmap.PixelHeight != decoder.Height)
            {
                _bitmap = new WriteableBitmap(decoder.Width, decoder.Height, 96, 96, PixelFormats.Bgr565, null);
                Screen.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, decoder.Width, decoder.Height), decoder.Pixels, decoder.Width * 2, 0);
        }
    }

    // ===================== Main =====================

    private void OnInput(TerminalInput input)
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

        if (input.IsMultiLinePaste && _emulator is { BracketedPaste: false } && PasteGuard?.Invoke(input) == false)
        {
            return;
        }

        _ = Watched.Viewer.SendInputAsync(input);
    }

    private async void OnControl(object sender, RoutedEventArgs e)
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
        ControlButton.Visibility = possible ? Visibility.Visible : Visibility.Collapsed;
        ControlButton.Content = _hasControl ? Strings.WatchGiveBack : _asked ? Strings.WatchCancelRequest : Strings.WatchAskControl;
        ControlButton.ToolTip = _hasControl ? Strings.WatchGiveBackTip : _asked ? null : Strings.WatchAskControlTip;
        ControlBanner.Visibility = _hasControl ? Visibility.Visible : Visibility.Collapsed;
        ControlText.Text = Text.Format(Strings.WatchHasControl, Watched.Info.Sharer);
        UpdateStatus();
    }

    private void ShowNotice()
    {
        string? text = EndReason is not null ? null
            : _paused ? Text.Format(Strings.WatchScreenPaused, Watched.Info.Sharer)
            : _hint ? (Watched.Info.ControlAllowed && Watched.Info.Kind == ShareKind.Terminal ? Strings.WatchReadOnlyAsk : Strings.WatchReadOnly)
            : null;
        NoticeBanner.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
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
        StatusLine.ToolTip = StatusLine.Text;
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
        LeaveButton.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
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

    private void OnLeave(object sender, RoutedEventArgs e) => LeaveRequested?.Invoke();
}
