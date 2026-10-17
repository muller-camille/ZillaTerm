using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Page « À propos » des Paramètres : version, liens du projet, licence, dossier des paramètres, et recherche d'une
/// nouvelle version (téléchargement vérifié par SHA-256 dans le dossier Téléchargements ; l'exécutable n'est jamais
/// remplacé ici). Un téléchargement en cours s'arrête à la fermeture des Paramètres, pas en changeant de page.
/// </summary>
public partial class AboutPanel : UserControl
{
    private AppSettings _settings = new();
    private Action _saveSettings = () => { };
    private CancellationTokenSource? _download;
    private string? _downloaded;
    private Window? _host;

    public AboutPanel()
    {
        InitializeComponent();
        ShowLogo(VisualTreeHelper.GetDpi(this).PixelsPerDip);
        VersionText.Text = Text.Format(Strings.AboutVersion, UpdateChecker.CurrentVersion);
        LicenseText.Text = Strings.AboutLicense;
        SystemText.Text = Text.Format(Strings.AboutSystem, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture);
        FolderText.Text = Text.Format(Strings.AboutSettingsFolder, SettingsFolder);
        if (UpdateService.Available is { } known)
        {
            ShowUpdate(known);
        }

        Loaded += (_, _) =>
        {
            if (_host is null && Window.GetWindow(this) is { } host)
            {
                _host = host;
                host.Closed += (_, _) => _download?.Cancel();
            }
        };
    }

    /// <param name="saveSettings">Enregistre les réglages (date de la dernière recherche d'une nouvelle version).</param>
    internal void Initialize(AppSettings settings, Action? saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings ?? (() => { });
    }

    /// <summary>Taille du logo à l'écran : sur un écran à une autre échelle, une autre image du .ico.</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ShowLogo(newDpi.PixelsPerDip);
    }

    /// <summary>
    /// Logo : l'image du .ico la plus petite qui couvre la taille affichée (pixels réels, selon l'échelle de l'écran).
    /// BitmapFrame.Create donnait la première image du fichier, de 16 px : agrandie à 56, elle était floue.
    /// </summary>
    private void ShowLogo(double pixelsPerDip)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/ZillaTerm;component/Assets/ZillaTerm.ico", UriKind.Absolute),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            double needed = LogoImage.Width * pixelsPerDip;
            LogoImage.Source = decoder.Frames.Where(f => f.PixelWidth >= needed).MinBy(f => f.PixelWidth)
                ?? decoder.Frames.MaxBy(f => f.PixelWidth);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            LogoImage.Visibility = Visibility.Collapsed;
        }
    }

    private static string SettingsFolder => Path.GetDirectoryName(AppSettings.DefaultPath) ?? "";

    private static string DownloadsFolder
    {
        get
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateText.Text = Strings.AboutChecking;
        UpdateText.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            var latest = await UpdateChecker.CheckAsync(UpdateService.Http, CancellationToken.None);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _saveSettings();
            if (UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentVersion))
            {
                UpdateService.Available = latest;
                ShowUpdate(latest);
            }
            else
            {
                UpdateText.Text = Text.Format(Strings.AboutUpToDate, UpdateChecker.CurrentVersion);
                UpdatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateText.Text = Text.Format(Strings.AboutCheckFailed, ErrorText.Describe(ex));
            UpdateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void ShowUpdate(UpdateInfo info)
    {
        UpdateText.Text = info.Published is { } date
            ? Text.Format(Strings.AboutNewVersionDated, info.Version, date.LocalDateTime.ToString("d"))
            : Text.Format(Strings.AboutNewVersion, info.Version);
        UpdateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessBrush");
        UpdatePanel.Visibility = Visibility.Visible;
        DownloadButton.IsEnabled = info.PackageUrl is not null && info.SumsUrl is not null;
    }

    /// <summary>Archive téléchargée dans Téléchargements puis comparée à SHA256SUMS.txt ; gardée seulement si identique.</summary>
    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } info)
        {
            return;
        }

        DownloadButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        UpdateText.Text = Text.Format(Strings.AboutDownloading, info.PackageName);
        UpdateText.ClearValue(TextBlock.ForegroundProperty);
        _download = new CancellationTokenSource();
        try
        {
            _downloaded = await UpdateChecker.DownloadAsync(UpdateService.Http, info, DownloadsFolder,
                new Progress<double>(value => DownloadProgress.Value = value), _download.Token);
            UpdateText.Text = Text.Format(Strings.AboutDownloaded, _downloaded);
            UpdateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessBrush");
            ShowFileButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            // Paramètres fermés pendant le téléchargement : le fichier partiel a été supprimé.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateText.Text = Text.Format(Strings.AboutDownloadFailed, ErrorText.Describe(ex));
            UpdateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ErrorBrush");
            DownloadButton.IsEnabled = true;
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
        }
    }

    private void OnShowFile(object sender, RoutedEventArgs e)
    {
        if (_downloaded is not null && File.Exists(_downloaded))
        {
            ShowInExplorer(() => WindowsExplorer.ShowFile(_downloaded));
        }
    }

    private void OnProject(object sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.ProjectUrl);

    private void OnReleases(object sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.ReleasesUrl);

    private void OnReleasePage(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.Available?.PageUrl ?? UpdateChecker.ReleasesUrl);

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SettingsFolder))
        {
            ShowInExplorer(() => WindowsExplorer.OpenFolder(SettingsFolder));
        }
    }

    /// <summary>Navigateur par défaut, seulement pour les adresses du projet.</summary>
    private void OpenUrl(string url)
    {
        if (url.StartsWith(UpdateChecker.ProjectUrl, StringComparison.Ordinal))
        {
            Start(url, null);
        }
    }

    private void ShowInExplorer(Action show)
    {
        try
        {
            show();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        if (Window.GetWindow(this) is { } owner)
        {
            MessageBox.Show(owner, message, Strings.AboutTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Start(string file, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments ?? "") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }
}
