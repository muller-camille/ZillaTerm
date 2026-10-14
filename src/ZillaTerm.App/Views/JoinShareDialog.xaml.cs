using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>
/// Rejoindre une session partagée : l'invitation collée, la connexion (directe, sinon par le dossier relais), le code de
/// vérification à donner à la personne qui partage, puis l'attente de son accord. Fermer la fenêtre retire la demande.
/// </summary>
public partial class JoinShareDialog : Window
{
    private readonly ShareJoin _join;
    private CancellationTokenSource? _connecting;
    private ShareViewer? _viewer;
    private bool _closed;

    internal JoinShareDialog(ShareJoin join, string? invitation = null)
    {
        InitializeComponent();
        _join = join;
        if (invitation is not null)
        {
            InvitationBox.Text = invitation;
        }

        Loaded += (_, _) => InvitationBox.Focus();
        Closed += (_, _) =>
        {
            _closed = true;
            _connecting?.Cancel();
            if (Joined is null && _viewer is { } viewer)
            {
                // Demande retirée : la personne qui partage la voit disparaître.
                _ = viewer.DisposeAsync();
            }
        };
    }

    /// <summary>Session acceptée ; null si la demande n'a pas abouti.</summary>
    internal WatchedShare? Joined { get; private set; }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (!ShareInvitation.TryParse(InvitationBox.Text, out var invitation))
        {
            ShowError(Strings.JoinInvitationInvalid);
            InvitationBox.Focus();
            return;
        }

        if (invitation.ExpiresAt <= DateTime.UtcNow)
        {
            ShowError(Describe(ShareFailure.Expired));
            return;
        }

        ErrorMessage.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Collapsed;
        WaitingPanel.Visibility = Visibility.Visible;
        CodePanel.Visibility = Visibility.Collapsed;
        JoinButton.IsEnabled = false;
        WaitingText.Text = Strings.JoinConnecting;
        _connecting = new CancellationTokenSource();
        try
        {
            _viewer = await ShareViewer.ConnectAsync(invitation, _join, _connecting.Token);
            if (_closed)
            {
                await _viewer.DisposeAsync();
                return;
            }

            DebugLog.Write("partage", $"Demande envoyée au partage {invitation.IdText} ({(_viewer.IsRelay ? "dossier relais" : "connexion directe")}).");
            WaitingText.Text = Strings.JoinWaiting;
            CodeText.Text = _viewer.VerificationCode;
            RouteText.Text = _viewer.IsRelay ? Strings.JoinRouteRelay : Text.Format(Strings.JoinRouteDirect, _viewer.Route);
            CodePanel.Visibility = Visibility.Visible;
            var info = await _viewer.Accepted;
            if (_closed)
            {
                return;
            }

            Joined = new WatchedShare(_viewer, info, invitation);
            DialogResult = true;
        }
        catch (ShareException ex) when (!_closed)
        {
            DebugLog.Write("partage", "Session partagée non rejointe", ex);
            if (_viewer is { } viewer)
            {
                _viewer = null;
                await viewer.DisposeAsync();
            }

            InputPanel.Visibility = Visibility.Visible;
            WaitingPanel.Visibility = Visibility.Collapsed;
            JoinButton.IsEnabled = true;
            ShowError(Describe(ex.Failure));
        }
        catch (Exception ex) when (_closed && ex is ShareException or OperationCanceledException)
        {
            // Fenêtre fermée pendant la connexion ou l'attente : demande retirée.
        }
    }

    internal static string Describe(ShareFailure failure) => failure switch
    {
        ShareFailure.Unreachable => Strings.JoinUnreachable,
        ShareFailure.Rejected => Strings.JoinRejected,
        ShareFailure.Expired => Strings.JoinExpired,
        ShareFailure.NotFound => Strings.JoinNotFound,
        ShareFailure.Full => Strings.JoinFull,
        _ => Strings.JoinRefused,
    };

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }
}
