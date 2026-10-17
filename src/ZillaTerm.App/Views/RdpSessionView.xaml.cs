using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Views;

/// <summary>
/// Contenu d'un onglet Bureau à distance : barre d'état, contrôle Bureau à distance, et message quand la session
/// n'est pas affichée (connexion en cours, fin de session, erreur).
/// </summary>
public partial class RdpSessionView : UserControl
{
    internal RdpSessionView(RdpSession session)
    {
        InitializeComponent();
        Session = session;
        Body.Children.Insert(0, session.Host);
        // Écran de la session gardé pendant l'enregistrement d'une intervention, tant que l'onglet est affiché.
        _screen = new RdpScreenRecorder(session, Services.InterventionRecorder.Current);
        _ = session.Closed.ContinueWith(_ => _screen.Dispose(), TaskScheduler.Default);
        IsVisibleChanged += (_, _) => ShowScreen();
        // Affichage en pause, poste inactif : le premier geste sur l'onglet le reprend tout de suite.
        PreviewMouseMove += (_, _) => Wake();
        PreviewMouseDown += (_, _) => Wake();
        PreviewKeyDown += (_, _) => Wake();
        session.StateChanged += Update;
        Update();
    }

    /// <summary>Geste sur l'onglet pendant une pause pour inactivité : l'affichage est à reprendre sans attendre.</summary>
    internal event Action? WakeRequested;

    /// <summary>Bouton « Reconnecter » de la barre de l'onglet : la fenêtre principale demande confirmation si la session est ouverte.</summary>
    internal event Action? ReconnectRequested;

    private void Wake()
    {
        if (Session.DisplayPause == RdpDisplayPauseReason.Idle)
        {
            WakeRequested?.Invoke();
        }
    }

    private readonly RdpScreenRecorder _screen;

    internal RdpSession Session { get; }

    /// <summary>Cadre de la couleur de l'étiquette du serveur (aucun sans étiquette).</summary>
    internal void ShowTag(Core.ServerTag? tag) => ServerTagView.Frame(TagFrame, tag);

    /// <summary>
    /// Intervention en cours d'enregistrement : le bouton « Capture » est proposé et l'indicateur dit que l'écran est
    /// gardé (toutes les 10 s au-delà de 100 Mo de journal).
    /// </summary>
    internal bool InterventionRecording
    {
        get => _interventionRecording;
        set
        {
            _interventionRecording = value;
            Update();
        }
    }

    private bool _interventionRecording;

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var png = Services.WindowCapture.CaptureChildPng(Session.Host.SlotHandle);
        if (png is null)
        {
            StatusLine.Text = Strings.InterventionCaptureFailed;
            return;
        }

        Services.InterventionRecorder.Current.Record(Core.Interventions.InterventionKind.Screenshot, Session.Label, Text.Format(Strings.InterventionCaptureOf, Session.Label), png);
        StatusLine.Text = Text.Format(Strings.InterventionCaptured, Session.Label);
    }

    private void Update()
    {
        var session = Session;
        bool paused = session.DisplayPause != RdpDisplayPauseReason.None;
        // Pause marquée dans l'enregistrement de l'écran (lecteur du rapport : écran et frise).
        _screen.SetPause(session.DisplayPause switch
        {
            RdpDisplayPauseReason.Idle => Core.Interventions.ScreenPause.Idle,
            RdpDisplayPauseReason.Hidden => Core.Interventions.ScreenPause.Hidden,
            _ => Core.Interventions.ScreenPause.None,
        });
        CaptureButton.Visibility = _interventionRecording && session.ShowsDesktop && !paused ? Visibility.Visible : Visibility.Collapsed;
        ScreenRecorded.Visibility = CaptureButton.Visibility;
        ScreenRecorded.Text = Services.InterventionRecorder.Current.ScreenThrottled ? Strings.RdpScreenThrottled : Strings.RdpScreenRecorded;
        ShowScreen();
        Overlay.Visibility = session.ShowsDesktop && !paused ? Visibility.Collapsed : Visibility.Visible;
        // Message affiché d'abord : le clavier peut alors y aller.
        UpdatePauseFocus(paused);
        FullScreenButton.IsEnabled = session.IsConnected && session.CanFullScreen;
        // Client FreeRDP : pas de plein écran ; le bouton grisé dit pourquoi, et quoi faire à la place.
        var fullScreenTip = session.UsesFreeRdp ? Strings.RdpFullScreenFreeRdpTip : Strings.RdpFullScreenTip;
        FullScreenButton.ToolTip = fullScreenTip;
        AutomationProperties.SetHelpText(FullScreenButton, fullScreenTip);
        ToolTipService.SetShowOnDisabled(FullScreenButton, session.UsesFreeRdp);
        DisconnectButton.IsEnabled = session.HasControl;
        var server = session.Server.Length > 0 ? session.Server : session.Label;
        switch (session.State)
        {
            case RdpSessionState.Connecting:
                StatusLine.Text = Text.Format(Strings.RdpConnecting, server);
                OverlayText.Text = Text.Format(Strings.RdpConnecting, session.Label);
                OverlayDetail.Text = session.Step ?? "";
                OverlayProgress.Visibility = Visibility.Visible;
                ReconnectButton.Visibility = Visibility.Collapsed;
                break;
            case RdpSessionState.Connected when session.IsReconnecting:
                // Connexion perdue : FreeRDP se reconnecte de lui-même ; la session, masquée, reprend avec le réseau.
                StatusLine.Text = Text.Format(Strings.RdpReconnectingStatus, session.Label);
                OverlayText.Text = Strings.RdpReconnecting;
                OverlayDetail.Text = session.ReconnectAttempt > 0
                    ? Text.Format(Strings.RdpReconnectAttempt, session.ReconnectAttempt, session.ReconnectAttempts)
                    : Strings.RdpReconnectWaiting;
                OverlayProgress.Visibility = Visibility.Visible;
                ReconnectButton.Visibility = Visibility.Collapsed;
                break;
            case RdpSessionState.Connected when paused:
                // Session ouverte, image suspendue : l'onglet dit pourquoi et comment la reprendre.
                StatusLine.Text = Text.Format(Strings.RdpPausedStatus, session.Label);
                OverlayText.Text = Strings.RdpPaused;
                OverlayDetail.Text = session.DisplayPause == RdpDisplayPauseReason.Idle
                    ? Text.Format(Strings.RdpPausedIdle, (int)RdpDisplayPause.IdleDelay.TotalMinutes)
                    : Strings.RdpPausedHidden;
                OverlayProgress.Visibility = Visibility.Collapsed;
                ReconnectButton.Visibility = Visibility.Collapsed;
                break;
            case RdpSessionState.Connected:
                StatusLine.Text = Text.Format(Strings.RdpConnected, session.Label, server);
                OverlayProgress.Visibility = Visibility.Collapsed;
                break;
            default:
                var failed = session.State == RdpSessionState.Failed;
                StatusLine.Text = failed ? Strings.ConnectionImpossible : Strings.SessionEnded;
                OverlayText.Text = StatusLine.Text;
                OverlayDetail.Text = session.Error ?? "";
                OverlayProgress.Visibility = Visibility.Collapsed;
                ReconnectButton.Visibility = Visibility.Visible;
                break;
        }

        if (session.IsAuthenticationWarningOpen)
        {
            StatusLine.Text = Text.Format(Strings.RdpCertificateWarningOpen, server);
        }

        if (session.IsNotResponding)
        {
            // Barre de l'onglet (WPF) : la fenêtre du contrôle reste à sa place, rien ne s'affiche par-dessus.
            StatusLine.Text = Text.Format(Strings.RdpNotResponding, session.Label);
        }

        if (session.IsNotResponding || session.IsAuthenticationWarningOpen || session.IsReconnecting)
        {
            StatusLine.SetResourceReference(TextBlock.ForegroundProperty, session.IsReconnecting ? "WarningBrush" : "ErrorBrush");
            StatusLine.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            StatusLine.ClearValue(TextBlock.ForegroundProperty);
            StatusLine.ClearValue(TextBlock.FontWeightProperty);
        }
    }

    /// <summary>
    /// Pause de l'affichage : le clavier quitte la session (le geste qui la reprend n'y est pas envoyé), sur le message de
    /// l'onglet ; à la reprise, il y revient s'il n'est pas allé ailleurs entre-temps.
    /// </summary>
    private void UpdatePauseFocus(bool paused)
    {
        if (paused == _paused)
        {
            return;
        }

        _paused = paused;
        if (paused && (IsKeyboardFocusWithin || Session.Host.HasKeyboardFocus))
        {
            Overlay.Focus();
        }
        else if (!paused && Overlay.IsKeyboardFocused)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, Session.Focus);
        }
    }

    private bool _paused;

    /// <summary>Bureau affiché dans l'onglet visible : son écran est capturé (s'il y a un enregistrement en cours ou un partage).</summary>
    private void ShowScreen()
    {
        bool showing = IsVisible && Session.ShowsDesktop && Session.DisplayPause == RdpDisplayPauseReason.None;
        _screen.Show(showing, Session.Host.SlotHandle);
        Interlocked.Exchange(ref _shownSlot, showing ? Session.Host.SlotHandle : IntPtr.Zero);
    }

    // Emplacement du bureau tant qu'il est affiché dans l'onglet ; zéro sinon.
    private IntPtr _shownSlot;

    /// <summary>
    /// Image du bureau pour une session partagée (fil de travail) ; null s'il n'est pas affiché dans l'onglet (onglet en
    /// arrière-plan, plein écran, session qui ne répond plus) : la personne qui regarde voit l'image figée, et le sait.
    /// </summary>
    internal Services.ScreenPixels? CaptureForShare(byte[]? buffer)
    {
        var slot = Volatile.Read(ref _shownSlot);
        return slot == IntPtr.Zero || Session.IsFullScreen || Session.IsNotResponding ? null : Services.WindowCapture.CaptureChildPixels(slot, buffer);
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => Session.EnterFullScreen();

    private void OnDisconnect(object sender, RoutedEventArgs e) => Session.Disconnect();

    private void OnToolbarReconnect(object sender, RoutedEventArgs e) => ReconnectRequested?.Invoke();

    private async void OnReconnect(object sender, RoutedEventArgs e) => await Session.ConnectAsync();
}
