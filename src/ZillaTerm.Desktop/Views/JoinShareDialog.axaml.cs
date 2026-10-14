using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>
/// Rejoindre une session partagée : l'invitation collée, la connexion (directe, sinon par le dossier relais), le code de
/// vérification à donner à la personne qui partage, puis l'attente de son accord. Fermer la fenêtre retire la demande.
/// </summary>
/// <remarks>
/// La session acceptée est rendue en ses trois parties (<see cref="Joined"/>) : la fenêtre principale en fait la session
/// regardée (<c>WatchedShare</c> de <c>ShareService</c>, pas encore porté) et lui ouvre un onglet.
/// </remarks>
public partial class JoinShareDialog : DialogWindow
{
    private readonly ShareJoin? _join;
    private CancellationTokenSource? _connecting;
    private ShareViewer? _viewer;
    private bool _closed;

    public JoinShareDialog() => InitializeComponent();

    internal JoinShareDialog(ShareJoin join, string? invitation = null)
        : this()
    {
        _join = join;
        if (invitation is not null)
        {
            InvitationBox.Text = invitation;
        }

        Opened += (_, _) => FocusLater(InvitationBox);
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

    /// <summary>Session acceptée (connexion, ce qu'elle montre, invitation utilisée) ; null si la demande n'a pas abouti.</summary>
    internal (IShareConnection Viewer, ShareInfo Info, ShareInvitation Invitation)? Joined { get; private set; }

    private async void OnJoin(object? sender, RoutedEventArgs e)
    {
        if (_join is null)
        {
            return;
        }

        if (!ShareInvitation.TryParse(InvitationBox.Text ?? "", out var invitation))
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

        ErrorMessage.IsVisible = false;
        InputPanel.IsVisible = false;
        WaitingPanel.IsVisible = true;
        CodePanel.IsVisible = false;
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
            CodePanel.IsVisible = true;
            var info = await _viewer.Accepted;
            if (_closed)
            {
                return;
            }

            Joined = (_viewer, info, invitation);
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

            InputPanel.IsVisible = true;
            WaitingPanel.IsVisible = false;
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
        ErrorMessage.IsVisible = true;
    }
}
