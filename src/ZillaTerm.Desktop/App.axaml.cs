using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
    private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private KeePassManager? _keePass;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // Une seule instance par session : deux instances écraseraient l'une l'autre leurs réglages, leur coffre local et les
    // fichiers en cours de modification. Même nom que l'application WPF : elles ne tournent pas en même temps.
    private Mutex? _instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Release();
            // Fin de la session du poste (déconnexion, arrêt) : nettoyage immédiat, sans question (voir EndWithWindowsSession).
            desktop.ShutdownRequested += (_, _) => EndWithSystemSession(desktop);
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                DebugLog.Write("app", "Exception non gérée", e.Exception);
                e.Handled = true;
                _ = ConfirmDialog.ShowMessageAsync(null, ErrorText.Describe(e.Exception), ConfirmKind.Danger);
            };
            Dispatcher.UIThread.Post(() => _ = RunAsync(desktop));
        }
    }

    /// <summary>
    /// Démarrage et sessions successives ; une erreur imprévue (fenêtre qui ne s'ouvre pas) est notée, dite, puis
    /// l'application s'arrête : sans fenêtre, elle resterait lancée sans rien montrer.
    /// </summary>
    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await StartAsync(desktop);
        }
        catch (Exception ex)
        {
            DebugLog.Write("app", "Erreur au démarrage ou à l'ouverture d'une fenêtre", ex);
            await ConfirmDialog.ShowMessageAsync(null, ErrorText.Describe(ex), ConfirmKind.Danger);
            Quit(desktop);
        }
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _instance = new Mutex(initiallyOwned: true, @"Local\CyberArkTerm.Instance", out bool first);
        if (!first)
        {
            _instance.Dispose();
            _instance = null;
            await ConfirmDialog.ShowMessageAsync(null, Strings.AlreadyRunning);
            desktop.Shutdown();
            return;
        }

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
        // Coffre local des mots de passe maîtres : DPAPI sous Windows, trousseau du bureau sous Linux.
        _keePass = new KeePassManager(Services.Secrets.SecretProtectors.CreateForLocalStore());
        // Verrouillage du poste : coffres verrouillés, maintien de la session PVWA suspendu, partage arrêté.
        SessionLock.Start();
        ApplyLanguage(UiLanguage.Resolve(_settings.Language, _systemCulture));
        if (_settings.SetAsideFile is { } aside)
        {
            await ConfirmDialog.ShowMessageAsync(null,
                Text.Format(_settings.RestoredFromBackup ? Strings.SettingsSetAsideRestored : Strings.SettingsSetAsideReset, aside), ConfirmKind.Warning);
        }
        else if (_settings.RestoredFromBackup)
        {
            // settings.json absent (supprimé pour repartir de zéro, remplacement interrompu) : sauvegarde reprise, et dit.
            await ConfirmDialog.ShowMessageAsync(null, Text.Format(Strings.SettingsMissingRestored, Path.GetDirectoryName(AppSettings.DefaultPath)));
        }

        // Environnement de l'équipe : fichier à côté de l'exécutable et fichier central, proposés s'ils ont changé.
        await EnvironmentImport.OfferAtStartupAsync(_settings);
        await StartSessionAsync(desktop);
    }

    /// <summary>
    /// Langue de l'interface, pour la suite de cette méthode et pour tout le fil de l'interface. La langue suit le contexte
    /// d'exécution : posée seulement ici (méthode asynchrone), elle serait défaite dès que la méthode rend la main, et
    /// les clics traités ensuite ouvriraient leurs fenêtres dans la langue du système. Une opération synchrone du fil
    /// de l'interface la pose donc aussi (Avalonia garde la langue qu'une telle opération change).
    /// </summary>
    private static void ApplyLanguage(CultureInfo culture)
    {
        UiLanguage.Apply(culture);
        Dispatcher.UIThread.Post(() => UiLanguage.Apply(culture), DispatcherPriority.Send);
    }

    /// <summary>Affiche l'écran de connexion puis, en cas de succès, la fenêtre principale.</summary>
    private async Task StartSessionAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        while (true)
        {
            // Langue choisie dans les préférences (ou celle du système) : appliquée à chaque retour à l'écran de connexion.
            ApplyLanguage(UiLanguage.Resolve(_settings.Language, _systemCulture));
            var login = new LoginWindow(_settings);
            bool ok = await login.ShowDialogAsync(null) && (login.Client is not null || login.EmergencyRequested);
            if (login.LanguageChanged)
            {
                // Langue changée depuis l'écran de connexion : on le rouvre dans la nouvelle langue.
                continue;
            }

            if (!ok)
            {
                Quit(desktop);
                return;
            }

            // Client null : accès d'urgence, sans CyberArk (coffres KeePass seulement).
            var main = new MainWindow(login.EmergencyRequested ? null : login.Client, _settings, login.SessionUser, login.VaultUser, _keePass!,
                login.Method);
            desktop.MainWindow = main;
            var closed = new TaskCompletionSource();
            main.Closed += (_, _) => closed.TrySetResult();
            main.Show();
            await closed.Task;
            if (!main.LogoutRequested)
            {
                Quit(desktop);
                return;
            }
        }
    }

    /// <summary>
    /// Fin de la session du poste : le système peut arrêter le processus sans attendre la fermeture ordonnée (questions,
    /// suites asynchrones). Le nettoyage essentiel est donc fait ici, de façon synchrone, puis celui de la fermeture.
    /// </summary>
    private void EndWithSystemSession(IClassicDesktopStyleApplicationLifetime desktop)
    {
        DebugLog.Write("app", "Fin de la session du poste.");
        foreach (var main in desktop.Windows.OfType<MainWindow>().ToList())
        {
            main.EndWithWindowsSession();
        }

        if (RdpSession.ControlWindowsLeft)
        {
            ExitNow();
        }

        Release();
    }

    /// <summary>
    /// Fin immédiate, après le nettoyage : une session Bureau à distance bloquée garde une fenêtre de son client que
    /// l'arrêt normal attendrait (l'application resterait figée).
    /// </summary>
    internal static void ExitNow()
    {
        DebugLog.Write("app", "Session Bureau à distance bloquée à la fermeture : arrêt immédiat de l'application.");
        (Current as App)?.Release();
        Environment.Exit(0);
    }

    /// <summary>Fin de l'application ; immédiate si une session Bureau à distance fermée est encore bloquée.</summary>
    private static void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (RdpSession.ControlWindowsLeft)
        {
            ExitNow();
        }

        desktop.Shutdown();
    }

    private void Release()
    {
        DebugLog.Write("app", "Fermeture de l'application.");
        // Intervention en cours d'enregistrement : sa fin est écrite avant que ZillaTerm s'arrête.
        InterventionRecorder.Current.Stop(Strings.InterventionStoppedAtExit);
        SessionLock.Stop();
        _keePass?.Dispose();
        _keePass = null;
        if (_instance is not null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
            _instance = null;
        }
    }
}
