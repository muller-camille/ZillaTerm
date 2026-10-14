using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// « À propos » : version, liens du projet, licence, dossier des paramètres, et recherche d'une nouvelle version
/// (téléchargement vérifié par SHA-256 dans le dossier Téléchargements ; l'exécutable n'est jamais remplacé ici).
/// </summary>
/// <remarks>
/// La version publiée sur GitHub est une archive pour Windows : sous Linux et macOS, la nouvelle version est seulement
/// signalée, avec le lien de sa page (ni téléchargement ni remplacement d'exécutable, qui n'y auraient pas de sens).
/// </remarks>
public partial class AboutWindow : DialogWindow
{
    private const string LogoUri = "avares://ZillaTerm/Assets/ZillaTerm.ico";

    private readonly AppSettings _settings = new();
    private readonly Action? _saveSettings;
    private CancellationTokenSource? _download;
    private string? _downloaded;
    private IDisposable? _updateColor;
    private double _logoScaling;

    public AboutWindow() => InitializeComponent();

    public AboutWindow(AppSettings settings, Action saveSettings)
        : this()
    {
        _settings = settings;
        _saveSettings = saveSettings;
        ShowLogo(RenderScaling);
        VersionText.Text = Text.Format(Strings.AboutVersion, UpdateChecker.CurrentVersion);
        LicenseText.Text = Strings.AboutLicense;
        SystemText.Text = Text.Format(Strings.AboutSystem, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture);
        FolderText.Text = Text.Format(Strings.AboutSettingsFolder, SettingsFolder);
        AutoCheckText.Text = settings.CheckForUpdates ? Strings.AboutAutoCheckOn : Strings.AboutAutoCheckOff;
        // Hors Windows : pas d'archive à télécharger ni d'exécutable à remplacer ; la note qui le décrit ne s'applique pas.
        DownloadButton.IsVisible = CanDownload;
        UpdateNoteText.IsVisible = CanDownload;
        if (UpdateService.Available is { } known)
        {
            ShowUpdate(known);
        }

        // Écran à une autre échelle : une autre image du .ico.
        Opened += (_, _) => ShowLogo(RenderScaling);
        ScalingChanged += (_, _) => ShowLogo(RenderScaling);
        Closed += (_, _) => _download?.Cancel();
    }

    /// <summary>
    /// Téléchargement de la nouvelle version : l'archive publiée est celle de Windows (« -win-x64.zip »), qui n'a pas de
    /// sens sous Linux ni macOS ; là, seul le lien de la page de la version est proposé.
    /// </summary>
    private static bool CanDownload => OperatingSystem.IsWindows();

    /// <summary>
    /// Logo : l'image du .ico la plus petite qui couvre la taille affichée (pixels réels, selon l'échelle de l'écran).
    /// La première image du fichier, de 16 px, agrandie à 56, serait floue.
    /// </summary>
    private void ShowLogo(double scaling)
    {
        if (scaling == _logoScaling && LogoImage.Source is not null)
        {
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri(LogoUri));
            using var file = new MemoryStream();
            stream.CopyTo(file);
            var bytes = file.ToArray();
            var frame = IconFrame(bytes, LogoImage.Width * scaling);
            var previous = LogoImage.Source as IDisposable;
            LogoImage.Source = new Bitmap(new MemoryStream(frame ?? bytes));
            previous?.Dispose();
            _logoScaling = scaling;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            LogoImage.IsVisible = false;
        }
    }

    /// <summary>
    /// Image PNG du .ico la plus petite d'au moins <paramref name="needed"/> pixels de large (à défaut, la plus grande) ;
    /// null si le fichier n'en contient pas (le .ico entier est alors décodé tel quel).
    /// </summary>
    internal static byte[]? IconFrame(byte[] ico, double needed)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var frames = new List<(int Width, int Offset, int Length)>();
        for (int i = 0; i < count && 6 + (i + 1) * 16 <= ico.Length; i++)
        {
            var entry = ico.AsSpan(6 + i * 16, 16);
            int width = entry[0] == 0 ? 256 : entry[0];
            long length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (offset + length <= ico.Length && ico.AsSpan((int)offset, (int)length).StartsWith(png))
            {
                frames.Add((width, (int)offset, (int)length));
            }
        }

        if (frames.Count == 0)
        {
            return null;
        }

        var covering = frames.Where(f => f.Width >= needed).ToList();
        var (_, start, size) = covering.Count > 0 ? covering.MinBy(f => f.Width) : frames.MaxBy(f => f.Width);
        return ico.AsSpan(start, size).ToArray();
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

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateText.Text = Strings.AboutChecking;
        SetUpdateColor(null);
        try
        {
            var latest = await UpdateChecker.CheckAsync(UpdateService.Http, CancellationToken.None);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _saveSettings?.Invoke();
            if (UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentVersion))
            {
                UpdateService.Available = latest;
                ShowUpdate(latest);
            }
            else
            {
                UpdateText.Text = Text.Format(Strings.AboutUpToDate, UpdateChecker.CurrentVersion);
                UpdatePanel.IsVisible = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateText.Text = Text.Format(Strings.AboutCheckFailed, ErrorText.Describe(ex));
            SetUpdateColor("ErrorBrush");
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
        SetUpdateColor("SuccessBrush");
        UpdatePanel.IsVisible = true;
        DownloadButton.IsEnabled = info.PackageUrl is not null && info.SumsUrl is not null;
    }

    /// <summary>Couleur du thème pour l'état de la recherche (null : couleur du texte), qui suit le changement de thème.</summary>
    private void SetUpdateColor(string? key)
    {
        _updateColor?.Dispose();
        _updateColor = key is null ? null : UpdateText.Bind(TextBlock.ForegroundProperty, UpdateText.GetResourceObservable(key));
    }

    /// <summary>Archive téléchargée dans Téléchargements puis comparée à SHA256SUMS.txt ; gardée seulement si identique.</summary>
    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (!CanDownload || UpdateService.Available is not { } info)
        {
            return;
        }

        DownloadButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.IsVisible = true;
        UpdateText.Text = Text.Format(Strings.AboutDownloading, info.PackageName);
        SetUpdateColor(null);
        _download = new CancellationTokenSource();
        try
        {
            _downloaded = await UpdateChecker.DownloadAsync(UpdateService.Http, info, DownloadsFolder,
                new Progress<double>(value => DownloadProgress.Value = value), _download.Token);
            UpdateText.Text = Text.Format(Strings.AboutDownloaded, _downloaded);
            SetUpdateColor("SuccessBrush");
            ShowFileButton.IsVisible = true;
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            // Fenêtre fermée pendant le téléchargement : le fichier partiel a été supprimé.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdateText.Text = Text.Format(Strings.AboutDownloadFailed, ErrorText.Describe(ex));
            SetUpdateColor("ErrorBrush");
            DownloadButton.IsEnabled = true;
        }
        finally
        {
            DownloadProgress.IsVisible = false;
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnShowFile(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsWindows() && _downloaded is not null && File.Exists(_downloaded))
        {
            await ShowInExplorerAsync(() => WindowsExplorer.ShowFile(_downloaded));
        }
    }

    private async void OnProject(object? sender, RoutedEventArgs e) => await OpenUrlAsync(UpdateChecker.ProjectUrl);

    private async void OnReleases(object? sender, RoutedEventArgs e) => await OpenUrlAsync(UpdateChecker.ReleasesUrl);

    private async void OnReleasePage(object? sender, RoutedEventArgs e) => await OpenUrlAsync(UpdateService.Available?.PageUrl ?? UpdateChecker.ReleasesUrl);

    private async void OnOpenSettingsFolder(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(SettingsFolder))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            await ShowInExplorerAsync(() => WindowsExplorer.OpenFolder(SettingsFolder));
        }
        else
        {
            // Gestionnaire de fichiers du bureau (xdg-open sous Linux, Finder sous macOS).
            await StartAsync(SettingsFolder);
        }
    }

    /// <summary>Navigateur par défaut, seulement pour les adresses du projet.</summary>
    private async Task OpenUrlAsync(string url)
    {
        if (url.StartsWith(UpdateChecker.ProjectUrl, StringComparison.Ordinal))
        {
            await StartAsync(url);
        }
    }

    private async Task ShowInExplorerAsync(Action show)
    {
        try
        {
            show();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            await ConfirmDialog.ShowMessageAsync(this, ex.Message, ConfirmKind.Danger, Title);
        }
    }

    /// <summary>
    /// Ouvre une adresse ou un dossier avec l'application par défaut du système (le shell de Windows, xdg-open sous Linux,
    /// open sous macOS).
    /// </summary>
    private async Task StartAsync(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ConfirmDialog.ShowMessageAsync(this, ex.Message, ConfirmKind.Danger, Title);
        }
    }
}
