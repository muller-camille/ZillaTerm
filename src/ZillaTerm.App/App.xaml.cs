using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App;

public partial class App : Application
{
    private AppSettings _settings = new();
    private KeePassManager? _keePass;
    private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

    // Une seule instance par session Windows : deux instances écraseraient l'une l'autre leurs réglages, leur coffre local
    // et les fichiers en cours de modification (nettoyage du dossier temporaire au démarrage). Nom gardé de CyberArkTerm :
    // l'ancienne version et ZillaTerm ne tournent pas en même temps.
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        _instance = new Mutex(initiallyOwned: true, @"Local\CyberArkTerm.Instance", out bool first);
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            MessageBox.Show(Strings.AlreadyRunning, "ZillaTerm", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Même icône pour toutes les fenêtres (connexion, fenêtre principale, dialogues).
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/ZillaTerm.ico", UriKind.Absolute));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { Icon: null } window)
            {
                window.Icon = icon;
            }
        }));
        // Boutons et cases à libellé AccessText : un nom pour les lecteurs d'écran.
        AccessibleName.Follow();
        _systemCulture = CultureInfo.CurrentUICulture;
        // Premier démarrage sous le nom ZillaTerm : réglages repris du dossier de CyberArkTerm.
        string? imported = null;
        try
        {
            if (AppSettings.ImportLegacyFolder(AppSettings.LegacyDirectory, Path.GetDirectoryName(AppSettings.DefaultPath)!))
            {
                imported = $"Réglages repris de {AppSettings.LegacyDirectory}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            imported = $"Réglages de {AppSettings.LegacyDirectory} non repris (nouvel essai au prochain démarrage) : {ex.Message}";
        }

        _settings = AppSettings.Load(AppSettings.DefaultPath);
        // Thème (clair, sombre ou celui de Windows) et contraste élevé de Windows, avant la première fenêtre.
        Palette.Follow(this, _settings.Theme);
        AppDebugLog.Apply(_settings);
        if (imported is not null)
        {
            DebugLog.Write("app", imported);
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                DebugLog.Write("app", "Exception non gérée (fin de l'application)", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => DebugLog.Write("app", "Exception d'une tâche non observée", args.Exception);
        RemoteEditor.CleanupStale();
        FileBrowserPanel.CleanupDragFolders();
        PrivateTemp.CleanupLegacy();
        _keePass = new KeePassManager();
        UiLanguage.Apply(UiLanguage.Resolve(_settings.Language, _systemCulture));
        if (_settings.SetAsideFile is { } aside)
        {
            MessageBox.Show(Text.Format(_settings.RestoredFromBackup ? Strings.SettingsSetAsideRestored : Strings.SettingsSetAsideReset, aside),
                "ZillaTerm", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Environnement de l'équipe : fichier à côté de l'exécutable et fichier central, proposés s'ils ont changé.
        EnvironmentImport.OfferAtStartup(_settings);
        StartSession();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DebugLog.Write("app", "Fermeture de l'application.");
        Release();
        base.OnExit(e);
    }

    /// <summary>
    /// Fin immédiate, après le nettoyage de <see cref="OnExit"/> : une session Bureau à distance bloquée garde une
    /// fenêtre de son contrôle que l'arrêt normal détruirait en attendant son thread (l'application resterait figée).
    /// </summary>
    internal static void ExitNow()
    {
        DebugLog.Write("app", "Session Bureau à distance bloquée à la fermeture : arrêt immédiat de l'application.");
        (Current as App)?.Release();
        Environment.Exit(0);
    }

    /// <summary>Fin de l'application ; immédiate si une session Bureau à distance fermée est encore bloquée.</summary>
    private void Quit()
    {
        if (RdpSession.ControlWindowsLeft)
        {
            ExitNow();
        }

        Shutdown();
    }

    private void Release()
    {
        // Intervention en cours d'enregistrement : sa fin est écrite avant que ZillaTerm s'arrête.
        InterventionRecorder.Current.Stop(Strings.InterventionStoppedAtExit);
        _keePass?.Dispose();
        _keePass = null;
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        _instance = null;
    }

    /// <summary>Affiche l'écran de connexion puis, en cas de succès, la liste des comptes.</summary>
    private void StartSession()
    {
        // Langue choisie dans les préférences (ou celle de Windows) : appliquée à chaque retour à l'écran de connexion.
        UiLanguage.Apply(UiLanguage.Resolve(_settings.Language, _systemCulture));
        var login = new LoginWindow(_settings);
        bool ok = login.ShowDialog() == true && (login.Client is not null || login.EmergencyRequested);
        if (login.LanguageChanged)
        {
            // Langue changée depuis l'écran de connexion : on le rouvre dans la nouvelle langue.
            StartSession();
            return;
        }

        if (!ok)
        {
            Quit();
            return;
        }

        // Client null : accès d'urgence, sans CyberArk (coffres KeePass seulement).
        var main = new MainWindow(login.EmergencyRequested ? null : login.Client, _settings, login.SessionUser, login.VaultUser, _keePass!,
            login.Method);
        MainWindow = main;
        main.Closed += (_, _) =>
        {
            if (main.LogoutRequested)
            {
                StartSession();
            }
            else
            {
                Quit();
            }
        };
        main.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DebugLog.Write("app", "Exception non gérée", e.Exception);
        MessageBox.Show(ErrorText.Describe(e.Exception), "ZillaTerm", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
