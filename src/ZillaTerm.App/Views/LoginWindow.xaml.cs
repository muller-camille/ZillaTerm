using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App.Views;

public partial class LoginWindow : Window
{
    private readonly AppSettings _settings;
    private PvwaClient? _pending;
    private AuthMethod _pendingMethod;
    private bool _closed;

    public LoginWindow(AppSettings settings)
    {
        InitializeComponent();
        CapsLockWarning.Attach(CapsLockText, PasswordBox, ChallengeBox);
        _settings = settings;

        MethodBox.ItemsSource = Enum.GetValues<AuthMethod>();
        MethodBox.SelectedItem = settings.AuthMethod;
        UrlBox.Text = settings.PvwaUrl;
        UserBox.Text = settings.UserName;

        // Langue affichée : celle en cours (choisie, ou à défaut celle de Windows).
        LanguageBox.DisplayMemberPath = "Value";
        LanguageBox.SelectedValuePath = "Key";
        LanguageBox.ItemsSource = UiLanguage.Supported
            .Select(code => new KeyValuePair<string, string>(code, UiLanguage.NativeName(code)))
            .ToList();
        LanguageBox.SelectedValue = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        Loaded += (_, _) =>
        {
            FocusFirstField();
            // Fenêtre rouverte après un changement de langue : elle doit reprendre le clavier.
            Activate();
        };
        // Le focus clavier n'est effectif qu'une fois la fenêtre active : on le replace à la première activation.
        EventHandler? firstActivation = null;
        firstActivation = (_, _) =>
        {
            Activated -= firstActivation;
            Dispatcher.BeginInvoke(FocusFirstField, System.Windows.Threading.DispatcherPriority.Input);
        };
        Activated += firstActivation;
        Closed += (_, _) =>
        {
            _closed = true;
            // Fenêtre fermée sans succès : on libère le client à moitié authentifié.
            if (Client is null)
            {
                _pending?.Dispose();
            }
        };
    }

    /// <summary>Client authentifié, disponible quand <see cref="Window.DialogResult"/> vaut <c>true</c>.</summary>
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
    private void OnImportEnvironment(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = EnvironmentImport.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true && EnvironmentImport.Offer(this, _settings, dialog.FileName, automatic: false))
        {
            UrlBox.Text = _settings.PvwaUrl;
            MethodBox.SelectedItem = _settings.AuthMethod;
            FocusFirstField();
        }
    }

    private void OnEmergencyClick(object sender, RoutedEventArgs e)
    {
        EmergencyRequested = true;
        DialogResult = true;
    }

    private AuthMethod SelectedMethod => MethodBox.SelectedItem is AuthMethod m ? m : AuthMethod.CyberArk;

    private void FocusFirstField()
    {
        if (UrlBox.Text.Length == 0)
        {
            UrlBox.Focus();
        }
        else if (UserBox.IsEnabled && UserBox.Text.Length == 0)
        {
            UserBox.Focus();
        }
        else if (PasswordBox.IsEnabled)
        {
            PasswordBox.Focus();
        }
        else
        {
            LoginButton.Focus();
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedValue is not string code
            || code == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        {
            return;
        }

        // Saisies conservées (sauf le mot de passe) : la fenêtre est reconstruite dans la nouvelle langue.
        _settings.Language = code;
        _settings.PvwaUrl = UrlBox.Text.Trim();
        _settings.AuthMethod = SelectedMethod;
        if (SelectedMethod != AuthMethod.Windows)
        {
            _settings.UserName = UserBox.Text.Trim();
        }

        TrySaveSettings();
        LanguageChanged = true;
        Close();
    }

    private void OnMethodChanged(object sender, SelectionChangedEventArgs e)
    {
        bool needsCredentials = SelectedMethod != AuthMethod.Windows;
        UserBox.IsEnabled = needsCredentials;
        PasswordBox.IsEnabled = needsCredentials;
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        bool answeringChallenge = ChallengePanel.Visibility == Visibility.Visible;
        var method = answeringChallenge ? _pendingMethod : SelectedMethod;
        string userName = UserBox.Text.Trim();
        // Lu sans chaîne .NET, effacé après l'envoi.
        var password = SecretInput.Read(answeringChallenge ? ChallengeBox : PasswordBox);

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
                _pending = PvwaClient.Create(UrlBox.Text, method);
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
            PasswordBox.Focus();
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
        StatusText.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Collapsed;
        ChallengePanel.Visibility = Visibility.Visible;
        ChallengeText.Text = prompt;
        PasswordBox.Clear();
        BackButton.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => ChallengeBox.Focus());
    }

    private void ShowCredentials()
    {
        ChallengePanel.Visibility = Visibility.Collapsed;
        CredentialsPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Pendant la question RADIUS, Échap fait « Retour » au lieu de « Quitter », qui fermerait ZillaTerm. « Retour » n'est
    /// pas le bouton d'annulation de la fenêtre (IsCancel) : WPF fermerait alors la fenêtre à chaque clic.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && EscapeGoesBack())
        {
            e.Handled = true;
        }
    }

    /// <summary>Échap pendant la question RADIUS : retour aux identifiants (ignoré pendant l'envoi de la réponse).</summary>
    /// <returns>Vrai si la touche est prise ici (question affichée).</returns>
    internal bool EscapeGoesBack()
    {
        if (ChallengePanel.Visibility != Visibility.Visible)
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
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _pending?.Dispose();
        _pending = null;
        ChallengeBox.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        ShowCredentials();
        SetBusy(false);
        FocusFirstField();
    }

    private void ShowError(string message)
    {
        // ErrorLine : annoncée par les lecteurs d'écran.
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        LoginButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        LanguageBox.IsEnabled = !busy && ChallengePanel.Visibility != Visibility.Visible;
        CredentialsPanel.IsEnabled = !busy;
        ChallengePanel.IsEnabled = !busy;
        if (busy)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveSettings(AuthMethod method, string userName)
    {
        _settings.PvwaUrl = UrlBox.Text.Trim();
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
