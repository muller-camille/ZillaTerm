using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App.Views;

public partial class LoginWindow : DialogWindow
{
    private readonly AppSettings _settings = new();
    private PvwaClient? _pending;
    private AuthMethod _pendingMethod;
    private bool _closed;
    private bool _loaded;

    public LoginWindow() => InitializeComponent();

    public LoginWindow(AppSettings settings)
        : this()
    {
        CapsLockWarning.Attach(CapsLockText, PasswordBox, ChallengeBox);
        _settings = settings;

        MethodBox.ItemsSource = Enum.GetValues<AuthMethod>();
        MethodBox.SelectedItem = settings.AuthMethod;
        UrlBox.Text = settings.PvwaUrl;
        UserBox.Text = settings.UserName;

        // Langue affichée : celle en cours (choisie, ou à défaut celle du système).
        LanguageBox.DisplayMemberBinding = new Binding(nameof(KeyValuePair<string, string>.Value));
        LanguageBox.SelectedValueBinding = new Binding(nameof(KeyValuePair<string, string>.Key));
        LanguageBox.ItemsSource = UiLanguage.Supported
            .Select(code => new KeyValuePair<string, string>(code, UiLanguage.NativeName(code)))
            .ToList();
        LanguageBox.SelectedValue = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        Opened += (_, _) =>
        {
            _loaded = true;
            Activate();
            FocusFirstField();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            // Fenêtre fermée sans succès : on libère le client à moitié authentifié.
            if (Client is null)
            {
                _pending?.Dispose();
            }
        };
        // Pendant la question RADIUS, Échap fait « Retour » au lieu de « Quitter », qui fermerait ZillaTerm.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Client authentifié, disponible quand le dialogue est accepté.</summary>
    public PvwaClient? Client { get; private set; }

    /// <summary>Nom affiché de l'utilisateur connecté.</summary>
    public string SessionUser { get; private set; } = "";

    /// <summary>Nom d'utilisateur du coffre (utilisé pour se connecter au PSMP).</summary>
    public string VaultUser { get; private set; } = "";

    /// <summary>Méthode d'authentification de la session ouverte (reprise pour se reconnecter après expiration).</summary>
    public AuthMethod Method { get; private set; }

    /// <summary>Vrai si la fenêtre a été fermée pour être rouverte dans une autre langue.</summary>
    public bool LanguageChanged { get; private set; }

    /// <summary>Vrai si l'utilisateur a choisi l'accès d'urgence (coffres KeePass, sans CyberArk).</summary>
    public bool EmergencyRequested { get; private set; }

    /// <summary>Environnement fourni par l'équipe : changements montrés puis appliqués ; l'adresse du PVWA est reprise.</summary>
    private async void OnImportEnvironment(object? sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(this, EnvironmentImport.FileFilter);
        if (path is not null && await EnvironmentImport.OfferAsync(this, _settings, path, automatic: false))
        {
            UrlBox.Text = _settings.PvwaUrl;
            MethodBox.SelectedItem = _settings.AuthMethod;
            FocusFirstField();
        }
    }

    private void OnEmergencyClick(object? sender, RoutedEventArgs e)
    {
        EmergencyRequested = true;
        DialogResult = true;
    }

    private AuthMethod SelectedMethod => MethodBox.SelectedItem is AuthMethod m ? m : AuthMethod.CyberArk;

    private void FocusFirstField()
    {
        if (string.IsNullOrEmpty(UrlBox.Text))
        {
            FocusLater(UrlBox);
        }
        else if (UserBox.IsEnabled && string.IsNullOrEmpty(UserBox.Text))
        {
            FocusLater(UserBox);
        }
        else if (PasswordBox.IsEnabled)
        {
            FocusLater(PasswordBox);
        }
        else
        {
            FocusLater(LoginButton);
        }
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || LanguageBox.SelectedValue is not string code
            || code == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        {
            return;
        }

        // Saisies conservées (sauf le mot de passe) : la fenêtre est reconstruite dans la nouvelle langue.
        _settings.Language = code;
        _settings.PvwaUrl = (UrlBox.Text ?? "").Trim();
        _settings.AuthMethod = SelectedMethod;
        if (SelectedMethod != AuthMethod.Windows)
        {
            _settings.UserName = (UserBox.Text ?? "").Trim();
        }

        TrySaveSettings();
        LanguageChanged = true;
        Close();
    }

    private void OnMethodChanged(object? sender, SelectionChangedEventArgs e)
    {
        bool needsCredentials = SelectedMethod != AuthMethod.Windows;
        UserBox.IsEnabled = needsCredentials;
        PasswordBox.IsEnabled = needsCredentials;
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        bool answeringChallenge = ChallengePanel.IsVisible;
        var method = answeringChallenge ? _pendingMethod : SelectedMethod;
        string userName = (UserBox.Text ?? "").Trim();
        // Effacé après l'envoi ; le champ est vidé dès la lecture.
        var password = SecretInput.Read(answeringChallenge ? ChallengeBox : PasswordBox, clear: answeringChallenge);

        if (!answeringChallenge && method != AuthMethod.Windows && (userName.Length == 0 || password.Length == 0))
        {
            SecretInput.Clear(password);
            ShowError(Strings.LoginMissingCredentials);
            return;
        }

        SetBusy(true);
        try
        {
            if (!answeringChallenge)
            {
                _pending?.Dispose();
                _pending = null;
                _pending = PvwaClient.Create(UrlBox.Text ?? "", method);
                _pendingMethod = method;
            }

            DebugLog.Write("login", $"Connexion à {_pending!.BaseUri} (méthode {method}{(answeringChallenge ? ", réponse au challenge RADIUS" : "")})");
            await _pending!.LogonAsync(method, userName, password);
            DebugLog.Write("login", "Session PVWA ouverte.");
            if (_closed)
            {
                // Fenêtre fermée pendant l'authentification.
                return;
            }

            SaveSettings(method, userName);
            Client = _pending;
            SessionUser = method == AuthMethod.Windows ? Environment.UserDomainName + "\\" + Environment.UserName : userName;
            VaultUser = method == AuthMethod.Windows ? Environment.UserName : userName;
            Method = method;
            PasswordBox.Clear();
            DialogResult = true;
        }
        catch (PvwaException ex) when (!_closed && ex.IsRadiusChallenge)
        {
            DebugLog.Write("login", "Le PVWA demande une réponse au challenge RADIUS.");
            ShowChallenge(ex.ServerMessage ?? Strings.LoginRadiusPrompt);
        }
        catch (Exception ex) when (!_closed && ex is not OutOfMemoryException)
        {
            DebugLog.Write("login", "Échec de la connexion au PVWA", ex);
            ShowError(ErrorText.Describe(ex));
            if (answeringChallenge)
            {
                // Le challenge a échoué : on repart d'une authentification complète.
                ShowCredentials();
            }

            PasswordBox.Clear();
            FocusLater(PasswordBox);
        }
        catch (Exception ex) when (_closed && ex is not OutOfMemoryException)
        {
            // Fenêtre fermée pendant une authentification lente (accès d'urgence, Quitter) : la requête a été annulée
            // avec elle, rien à signaler par-dessus la fenêtre suivante.
            DebugLog.Write("login", "Connexion au PVWA abandonnée (fenêtre fermée)", ex);
        }
        finally
        {
            SecretInput.Clear(password);
            ChallengeBox.Clear();
            SetBusy(false);
        }
    }

    internal void ShowChallenge(string prompt)
    {
        StatusText.IsVisible = false;
        CredentialsPanel.IsVisible = false;
        ChallengePanel.IsVisible = true;
        ChallengeText.Text = prompt;
        PasswordBox.Clear();
        BackButton.IsVisible = true;
        FocusLater(ChallengeBox);
    }

    private void ShowCredentials()
    {
        ChallengePanel.IsVisible = false;
        CredentialsPanel.IsVisible = true;
        BackButton.IsVisible = false;
    }

    /// <summary>
    /// Pendant la question RADIUS, Échap fait « Retour » au lieu de « Quitter », qui fermerait ZillaTerm. « Retour » n'est
    /// pas le bouton d'annulation de la fenêtre : celui-ci la fermerait à chaque clic.
    /// </summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && EscapeGoesBack())
        {
            e.Handled = true;
        }
    }

    /// <summary>Échap pendant la question RADIUS : retour aux identifiants (ignoré pendant l'envoi de la réponse).</summary>
    /// <returns>Vrai si la touche est prise ici (question affichée).</returns>
    internal bool EscapeGoesBack()
    {
        if (!ChallengePanel.IsVisible)
        {
            return false;
        }

        if (BackButton.IsEnabled)
        {
            OnBackClick(BackButton, new RoutedEventArgs());
        }

        return true;
    }

    /// <summary>
    /// Question RADIUS laissée sans réponse : retour aux identifiants (méthode ou identifiant à corriger), sans réponse
    /// fausse qui compterait comme un échec. La tentative en cours est abandonnée ; « S'identifier » en recommence une.
    /// </summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        _pending?.Dispose();
        _pending = null;
        ChallengeBox.Clear();
        StatusText.IsVisible = false;
        ShowCredentials();
        SetBusy(false);
        FocusFirstField();
    }

    private void ShowError(string message)
    {
        // ErrorLine : annoncée par les lecteurs d'écran.
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.Opacity = busy ? 1 : 0;
        LoginButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        LanguageBox.IsEnabled = !busy && !ChallengePanel.IsVisible;
        CredentialsPanel.IsEnabled = !busy;
        ChallengePanel.IsEnabled = !busy;
        if (busy)
        {
            StatusText.IsVisible = false;
        }
    }

    private void SaveSettings(AuthMethod method, string userName)
    {
        _settings.PvwaUrl = (UrlBox.Text ?? "").Trim();
        _settings.AuthMethod = method;
        if (method != AuthMethod.Windows)
        {
            _settings.UserName = userName;
        }

        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save(AppSettings.DefaultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Préférences non enregistrées : sans conséquence pour la session en cours.
        }
    }
}
