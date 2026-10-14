using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.App;

public partial class App : Application
{
    private AppSettings _settings = new();
    private CultureInfo _systemCulture = CultureInfo.CurrentUICulture;

    // Une seule instance par session : deux instances écraseraient l'une l'autre leurs réglages, leur coffre local et les
    // fichiers en cours de modification. Même nom que l'application WPF : elles ne tournent pas en même temps.
    private Mutex? _instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Release();
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                DebugLog.Write("app", "Exception non gérée", e.Exception);
                e.Handled = true;
                _ = ConfirmDialog.ShowMessageAsync(null, ErrorText.Describe(e.Exception), ConfirmKind.Danger);
            };
            Dispatcher.UIThread.Post(() => _ = StartAsync(desktop));
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
        UiLanguage.Apply(UiLanguage.Resolve(_settings.Language, _systemCulture));
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

    /// <summary>Affiche l'écran de connexion puis, en cas de succès, la fenêtre principale.</summary>
    private async Task StartSessionAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        while (true)
        {
            // Langue choisie dans les préférences (ou celle du système) : appliquée à chaque retour à l'écran de connexion.
            UiLanguage.Apply(UiLanguage.Resolve(_settings.Language, _systemCulture));
            var login = new LoginWindow(_settings);
            bool ok = await login.ShowDialogAsync(null) && (login.Client is not null || login.EmergencyRequested);
            if (login.LanguageChanged)
            {
                // Langue changée depuis l'écran de connexion : on le rouvre dans la nouvelle langue.
                continue;
            }

            if (!ok)
            {
                desktop.Shutdown();
                return;
            }

            // Client null : accès d'urgence, sans CyberArk (coffres KeePass seulement).
            var main = new MainWindow(login.EmergencyRequested ? null : login.Client, _settings, login.SessionUser, login.VaultUser, login.Method);
            desktop.MainWindow = main;
            var closed = new TaskCompletionSource();
            main.Closed += (_, _) => closed.TrySetResult();
            main.Show();
            await closed.Task;
            if (!main.LogoutRequested)
            {
                desktop.Shutdown();
                return;
            }
        }
    }

    private void Release()
    {
        DebugLog.Write("app", "Fermeture de l'application.");
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        _instance = null;
    }
}
