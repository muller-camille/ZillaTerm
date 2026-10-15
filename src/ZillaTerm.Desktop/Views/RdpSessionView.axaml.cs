using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Rdp;

namespace ZillaTerm.App.Views;

/// <summary>
/// Contenu d'un onglet Bureau à distance : barre d'état, fenêtre du client Bureau à distance, et message quand la session
/// n'est pas affichée (connexion en cours, fin de session, erreur).
/// </summary>
public partial class RdpSessionView : UserControl
{
    /// <summary>Pour le concepteur d'Avalonia seulement.</summary>
    public RdpSessionView()
    {
        InitializeComponent();
        Session = null!;
        _screen = null!;
    }

    internal RdpSessionView(RdpSession session)
    {
        InitializeComponent();
        Session = session;
        Body.Children.Insert(0, session.Host);
        // Écran de la session gardé pendant l'enregistrement d'une intervention, tant que l'onglet est affiché.
        _screen = new RdpScreenRecorder(session, Services.InterventionRecorder.Current);
        _ = session.Closed.ContinueWith(_ => _screen.Dispose(), TaskScheduler.Default);
        session.StateChanged += Update;
        Update();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && Session is not null)
        {
            if (!IsVisible)
            {
                // Linux : le clavier revient à ZillaTerm s'il était à la session de l'onglet quitté.
                Session.Host.ReleaseKeyboard();
            }

            ShowScreen();
        }
    }

    private void OnCapture(object? sender, RoutedEventArgs e)
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
        CaptureButton.IsVisible = _interventionRecording && session.ShowsDesktop;
        ScreenRecorded.IsVisible = CaptureButton.IsVisible;
        ScreenRecorded.Text = Services.InterventionRecorder.Current.ScreenThrottled ? Strings.RdpScreenThrottled : Strings.RdpScreenRecorded;
        ShowScreen();
        Overlay.IsVisible = !session.ShowsDesktop;
        FullScreenButton.IsEnabled = session.IsConnected && session.CanFullScreen;
        // Client FreeRDP : pas de plein écran ; le bouton grisé dit pourquoi, et quoi faire à la place.
        var fullScreenTip = session.UsesFreeRdp ? Strings.RdpFullScreenFreeRdpTip : Strings.RdpFullScreenTip;
        ToolTip.SetTip(FullScreenButton, fullScreenTip);
        AutomationProperties.SetHelpText(FullScreenButton, fullScreenTip);
        ToolTip.SetShowOnDisabled(FullScreenButton, session.UsesFreeRdp);
        DisconnectButton.IsEnabled = session.HasControl;
        var server = session.Server.Length > 0 ? session.Server : session.Label;
        switch (session.State)
        {
            case RdpSessionState.Connecting:
                StatusLine.Text = Text.Format(Strings.RdpConnecting, server);
                OverlayText.Text = Text.Format(Strings.RdpConnecting, session.Label);
                OverlayDetail.Text = session.Step ?? "";
                OverlayProgress.IsVisible = true;
                ReconnectButton.IsVisible = false;
                break;
            case RdpSessionState.Connected when session.IsReconnecting:
                // Connexion perdue : FreeRDP se reconnecte de lui-même ; la session, masquée, reprend avec le réseau.
                StatusLine.Text = Text.Format(Strings.RdpReconnectingStatus, session.Label);
                OverlayText.Text = Strings.RdpReconnecting;
                OverlayDetail.Text = session.ReconnectAttempt > 0
                    ? Text.Format(Strings.RdpReconnectAttempt, session.ReconnectAttempt, session.ReconnectAttempts)
                    : Strings.RdpReconnectWaiting;
                OverlayProgress.IsVisible = true;
                ReconnectButton.IsVisible = false;
                break;
            case RdpSessionState.Connected:
                StatusLine.Text = Text.Format(Strings.RdpConnected, session.Label, server);
                OverlayProgress.IsVisible = false;
                break;
            default:
                var failed = session.State == RdpSessionState.Failed;
                StatusLine.Text = failed ? Strings.ConnectionImpossible : Strings.SessionEnded;
                OverlayText.Text = StatusLine.Text;
                OverlayDetail.Text = session.Error ?? "";
                OverlayProgress.IsVisible = false;
                ReconnectButton.IsVisible = true;
                break;
        }

        if (session.IsAuthenticationWarningOpen)
        {
            StatusLine.Text = Text.Format(Strings.RdpCertificateWarningOpen, server);
        }

        if (session.IsNotResponding)
        {
            // Barre de l'onglet (Avalonia) : la fenêtre du client reste à sa place, rien ne s'affiche par-dessus.
            StatusLine.Text = Text.Format(Strings.RdpNotResponding, session.Label);
        }

        if (session.IsNotResponding || session.IsAuthenticationWarningOpen || session.IsReconnecting)
        {
            StatusLine[!TextBlock.ForegroundProperty] = StatusLine.GetResourceObservable(session.IsReconnecting ? "WarningBrush" : "ErrorBrush").ToBinding();
            StatusLine.FontWeight = FontWeight.SemiBold;
        }
        else
        {
            StatusLine.ClearValue(TextBlock.ForegroundProperty);
            StatusLine.ClearValue(TextBlock.FontWeightProperty);
        }
    }

    /// <summary>Bureau affiché dans l'onglet visible : son écran est capturé (s'il y a un enregistrement en cours ou un partage).</summary>
    private void ShowScreen()
    {
        bool showing = IsVisible && Session.ShowsDesktop;
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

    private void OnFullScreen(object? sender, RoutedEventArgs e) => Session.EnterFullScreen();

    private void OnDisconnect(object? sender, RoutedEventArgs e) => Session.Disconnect();

    private async void OnReconnect(object? sender, RoutedEventArgs e) => await Session.ConnectAsync();
}
