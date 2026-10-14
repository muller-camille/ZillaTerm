using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Views;

/// <summary>
/// Session PVWA expirée : nouvelle connexion sur le même client, avec la même adresse, le même utilisateur et la même
/// méthode qu'à l'ouverture (aucun des trois ne peut changer ici). La fenêtre principale, ses onglets, ses sessions et
/// ses transferts restent en place ; seul le jeton du PVWA est renouvelé. Challenge RADIUS pris en charge.
/// </summary>
public partial class ReconnectDialog : DialogWindow
{
    private readonly PvwaClient? _client;
    private readonly AuthMethod _method;
    private readonly string _userName = "";
    private CancellationTokenSource? _logon;
    private bool _challenge;
    private bool _closed;

    public ReconnectDialog() => InitializeComponent();

    /// <param name="userName">Utilisateur de la connexion d'origine (ignoré pour la méthode Windows).</param>
    /// <param name="shownUser">Utilisateur affiché.</param>
    /// <param name="focusPassword">
    /// Curseur dans le mot de passe ; faux quand la fenêtre s'ouvre d'elle-même (maintien de session) : ce que
    /// l'utilisateur tapait ailleurs (terminal) ne doit pas partir comme mot de passe vers le PVWA.
    /// </param>
    internal ReconnectDialog(PvwaClient client, AuthMethod method, string userName, string shownUser, bool focusPassword)
        : this()
    {
        CapsLockWarning.Attach(CapsLockText, PasswordBox, ChallengeBox);
        _client = client;
        _method = method;
        _userName = userName;
        WhoText.Text = Text.Format(Strings.ReconnectWho, shownUser, client.BaseUri.Host, method);
        if (method == AuthMethod.Windows)
        {
            CredentialsPanel.IsVisible = false;
        }

        Opened += (_, _) =>
        {
            if (focusPassword && method != AuthMethod.Windows)
            {
                FocusLater(PasswordBox);
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _logon?.Cancel();
            // Rien ne reste dans les champs après la fermeture (« Plus tard », Échap, croix).
            PasswordBox.Clear();
            ChallengeBox.Clear();
        };
    }

    private async void OnReconnect(object? sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }

        // Champ vidé dès la lecture (Avalonia n'a pas de SecureString) ; copie effacée après l'envoi.
        var password = SecretInput.Read(_challenge ? ChallengeBox : PasswordBox, clear: true);
        if (_method != AuthMethod.Windows && password.Length == 0)
        {
            // Rien n'est envoyé au PVWA sans saisie (Entrée tapée par erreur) : pas de tentative perdue.
            ShowError(_challenge ? Strings.ReconnectAnswerMissing : Strings.ReconnectPasswordMissing);
            FocusLater(_challenge ? ChallengeBox : PasswordBox);
            return;
        }

        SetBusy(true);
        _logon = new CancellationTokenSource();
        try
        {
            DebugLog.Write("login", $"Reconnexion à {_client.BaseUri} (méthode {_method}{(_challenge ? ", réponse au challenge RADIUS" : "")})");
            await _client.LogonAsync(_method, _userName, password, _logon.Token);
            DebugLog.Write("login", "Session PVWA rouverte.");
            if (!_closed)
            {
                DialogResult = true;
            }
        }
        catch (PvwaException ex) when (!_closed && ex.IsRadiusChallenge)
        {
            DebugLog.Write("login", "Le PVWA demande une réponse au challenge RADIUS.");
            ShowChallenge(ex.ServerMessage ?? Strings.LoginRadiusPrompt);
        }
        catch (Exception ex) when (!_closed && ex is not OutOfMemoryException)
        {
            DebugLog.Write("login", "Échec de la reconnexion au PVWA", ex);
            ShowError(ErrorText.Describe(ex));
            if (_challenge)
            {
                // Le challenge a échoué : on repart du mot de passe.
                _challenge = false;
                ChallengePanel.IsVisible = false;
                CredentialsPanel.IsVisible = true;
            }

            PasswordBox.Clear();
            FocusLater(PasswordBox);
        }
        catch (Exception ex) when (_closed && ex is not OutOfMemoryException)
        {
            // Fenêtre fermée pendant la demande (annulée) : plus rien à afficher.
        }
        finally
        {
            SecretInput.Clear(password);
            ChallengeBox.Clear();
            if (!_closed)
            {
                SetBusy(false);
            }
        }
    }

    private void ShowChallenge(string prompt)
    {
        _challenge = true;
        ErrorMessage.IsVisible = false;
        CredentialsPanel.IsVisible = false;
        ChallengePanel.IsVisible = true;
        ChallengeText.Text = prompt;
        PasswordBox.Clear();
        FocusLater(ChallengeBox);
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.Opacity = busy ? 1 : 0;
        ReconnectButton.IsEnabled = !busy;
        CredentialsPanel.IsEnabled = !busy;
        ChallengePanel.IsEnabled = !busy;
        if (busy)
        {
            ErrorMessage.IsVisible = false;
        }
    }
}
