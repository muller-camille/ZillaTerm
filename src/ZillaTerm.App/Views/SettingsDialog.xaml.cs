using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Terminal;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Rdp;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly LocalSecretStore? _store;
    private readonly string _pvwaHost;
    private readonly HashSet<string> _forgottenKeys = new(StringComparer.Ordinal);
    private readonly ObservableCollection<PsmpRow> _psmpRows = [];
    private readonly ObservableCollection<ComponentRow> _componentRows = [];
    private readonly ObservableCollection<TagRow> _tagRows = [];
    private readonly ObservableCollection<FolderRow> _folderRows = [];

    /// <summary>Fichiers d'environnement importés ici, appliqués à « Enregistrer » dans cet ordre (« Annuler » les abandonne).</summary>
    private readonly List<(string Path, EnvironmentFile File)> _imports = [];

    /// <summary>Réglages affichés : ceux en vigueur, ou une copie où un environnement importé a été appliqué.</summary>
    private AppSettings _shown;

    /// <summary>Fichiers d'environnement de l'équipe lus à l'ouverture (pastilles « Équipe »).</summary>
    private List<(string Path, EnvironmentProfile Profile)> _teamFiles = [];

    /// <param name="store">Coffre local des mots de passe maîtres KeePass, géré depuis cette fenêtre.</param>
    /// <param name="pvwaHost">PVWA de la session, nom donné à un environnement exporté.</param>
    /// <param name="saveSettings">Enregistre les réglages en vigueur (page À propos : date de la dernière recherche d'une nouvelle version).</param>
    public SettingsDialog(AppSettings settings, LocalSecretStore? store = null, string pvwaHost = "", Action? saveSettings = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => FitToScreen();
        _settings = settings;
        _shown = settings;
        _store = store;
        _pvwaHost = pvwaHost;
        LanguageBox.DisplayMemberPath = "Value";
        LanguageBox.SelectedValuePath = "Key";
        LanguageBox.ItemsSource = new[] { new KeyValuePair<string, string>("", CoreStrings.LanguageSystem) }
            .Concat(UiLanguage.Supported.Select(code => new KeyValuePair<string, string>(code, UiLanguage.NativeName(code))))
            .ToList();
        AppThemeBox.DisplayMemberPath = "Value";
        AppThemeBox.SelectedValuePath = "Key";
        AppThemeBox.ItemsSource = new[]
        {
            new KeyValuePair<AppTheme, string>(AppTheme.System, Strings.AppThemeSystem),
            new KeyValuePair<AppTheme, string>(AppTheme.Light, Strings.AppThemeLight),
            new KeyValuePair<AppTheme, string>(AppTheme.Dark, Strings.AppThemeDark),
        };
        ThemeBox.ItemsSource = TerminalTheme.All;
        WindowsComponentBox.ItemsSource = AccountClassifier.CommonComponents;
        PsmpGrid.ItemsSource = _psmpRows;
        ComponentGrid.ItemsSource = _componentRows;
        TagGrid.ItemsSource = _tagRows;
        FolderGrid.ItemsSource = _folderRows;
        _tagRows.CollectionChanged += (_, _) => UpdateAddTagButton();
        AboutView.Initialize(settings, saveSettings);
        UpdateStore();
        ShowValues(settings);
        UpdateAddTagButton();
        // F1 : la section du guide de la page affichée.
        CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Help, (_, _) => OpenGuide()));
        Loaded += (_, _) =>
        {
            (Pages.SelectedItem == SharingPage ? (Control)SessionSharingBox : LanguageBox).Focus();
            _ = LoadTeamBadgesAsync();
        };
    }

    /// <summary>Remplit les pages avec ces réglages (à l'ouverture, puis après l'import d'un environnement).</summary>
    private void ShowValues(AppSettings settings)
    {
        _shown = settings;
        KeepAliveBox.IsChecked = settings.KeepPvwaSessionAlive;
        KeepAccountListCopyBox.IsChecked = settings.KeepAccountListCopy;
        UpdateCheckBox.IsChecked = settings.CheckForUpdates;
        CentralFileBox.Text = settings.EnvironmentFile;
        DebugLogBox.IsChecked = settings.DebugLogEnabled;
        LanguageBox.SelectedValue = UiLanguage.Normalize(settings.Language);
        AppThemeBox.SelectedValue = settings.Theme;
        PsmpBox.Text = settings.PsmpAddress;
        PortBox.Text = settings.PsmpPort.ToString(CultureInfo.InvariantCulture);
        _psmpRows.Clear();
        foreach (var psmp in settings.PsmpServers)
        {
            AddPsmpRow(PsmpRow.From(psmp));
        }

        UpdatePsmpTest();
        SshInAppBox.IsChecked = settings.SshInApp;
        PsmDesktopInTabBox.IsChecked = settings.PsmDesktopInTab;
        PsmDynamicResolutionBox.IsChecked = settings.PsmDynamicResolution;
        PauseRdpDisplayBox.IsChecked = settings.PauseRdpDisplay;
        (settings.DriveSharing() switch
        {
            DriveSharing.None => DrivesNoneRadio,
            DriveSharing.Folders => DrivesFoldersRadio,
            _ => DrivesAllRadio,
        }).IsChecked = true;
        _folderRows.Clear();
        foreach (var folder in settings.PsmSharedFolders)
        {
            _folderRows.Add(new FolderRow { Name = folder.Name, Path = folder.Path });
        }
        FollowBox.IsChecked = settings.FollowTerminalFolder;
        X11DisplayBox.Text = settings.X11Display;
        SessionSharingBox.IsChecked = settings.SessionSharing;
        ShareDirectBox.IsChecked = settings.ShareDirect;
        SharePortBox.Text = settings.SharePort.ToString(CultureInfo.InvariantCulture);
        ShareRelayBox.Text = settings.ShareRelayFolder;
        DutyTextBox.Text = settings.DutyText;
        DutyFileBox.Text = settings.DutyTextFile;
        RemindersBox.Text = InterventionReminder.Format(settings.InterventionReminders).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        EditorBox.Text = settings.TextEditor;
        CompareToolBox.Text = settings.CompareTool;
        ThemeBox.SelectedItem = TerminalTheme.Find(settings.TerminalTheme);
        FontSizeBox.Text = settings.TerminalFontSize.ToString(CultureInfo.CurrentCulture);
        RightClickBox.IsChecked = settings.TerminalRightClickPastes;
        ConfirmPasteBox.IsChecked = settings.ConfirmMultiLinePaste;
        ConfirmCloseBox.IsChecked = settings.ConfirmCloseSession;
        CompareArgsBox.Text = settings.CompareToolArguments;
        (settings.PreferredUploadProtocol == TransferProtocol.Scp ? ScpRadio : SftpRadio).IsChecked = true;
        ArchiveBox.IsChecked = settings.OfferArchive;
        ArchiveThresholdBox.Text = settings.ArchiveThreshold.ToString(CultureInfo.InvariantCulture);
        ShowHiddenBox.IsChecked = settings.ShowHiddenFiles;
        foreach (var (box, column) in ColumnBoxes())
        {
            box.IsChecked = !settings.HiddenFileColumns.Contains(column);
        }

        TailSessionBox.IsChecked = settings.TailIndependentSession;
        TailColorsBox.IsChecked = settings.TailLevelColors;
        TailHighlightBox.Text = settings.TailHighlights;
        TailAlertBox.Text = settings.TailAlerts;
        TailNotifyBox.IsChecked = settings.TailAlertNotify;
        ShowHostKeys();
        WindowsComponentBox.Text = settings.WindowsComponent;
        _componentRows.Clear();
        foreach (var (platform, component) in settings.ComponentByPlatform.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            _componentRows.Add(new ComponentRow { Platform = platform, Component = component });
        }

        _tagRows.Clear();
        foreach (var tag in settings.ServerTags)
        {
            _tagRows.Add(new TagRow(tag.Name, tag.Name, tag.Color));
        }
    }

    /// <summary>Cases des colonnes facultatives de l'onglet Fichiers (la colonne Nom reste toujours).</summary>
    private (CheckBox Box, RemoteSortColumn Column)[] ColumnBoxes() =>
    [
        (SizeColumnBox, RemoteSortColumn.Size),
        (ModifiedColumnBox, RemoteSortColumn.Modified),
        (PermissionsColumnBox, RemoteSortColumn.Permissions),
        (OwnerColumnBox, RemoteSortColumn.Owner),
        (GroupColumnBox, RemoteSortColumn.Group),
    ];

    /// <summary>Un environnement a été importé ici (et appliqué à l'enregistrement).</summary>
    internal bool ImportedEnvironment => _imports.Count > 0;

    /// <summary>Fichiers d'environnement importés ici (pour ne pas reproposer aussitôt le même comme fichier central).</summary>
    internal IEnumerable<string> ImportedPaths => _imports.Select(i => i.Path);

    /// <summary>Ouvre la page « Partage » (partage demandé alors qu'il est désactivé).</summary>
    internal void ShowSharingPage() => Pages.SelectedItem = SharingPage;

    /// <summary>Page « À propos » (lien d'une nouvelle version dans la barre d'état).</summary>
    internal void ShowAboutPage() => Pages.SelectedItem = AboutPage;

    /// <summary>
    /// Clé d'hôte acceptée : « hôte:port », type de clé (ou X.509 pour un certificat), empreinte SHA-256, et sa clé
    /// dans les réglages (un serveur peut avoir une clé de chaque type).
    /// </summary>
    internal sealed record HostKeyRow(string Server, string Algorithm, string Fingerprint, string Entry);

    /// <summary>Clés affichées (celles choisies pour l'oubli disparaissent de la liste, l'oubli se fait à l'enregistrement).</summary>
    internal IReadOnlyList<HostKeyRow> HostKeyRows { get; private set; } = [];

    private void ShowHostKeys()
    {
        HostKeyRows = _shown.KnownHosts
            .Where(kv => !_forgottenKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                int space = kv.Value.IndexOf(' ');
                return new HostKeyRow(KnownHosts.Server(kv.Key), space > 0 ? kv.Value[..space] : "", space > 0 ? kv.Value[(space + 1)..] : kv.Value,
                    kv.Key);
            })
            .ToList();
        FilterHostKeys();
        HostKeysText.Text = _forgottenKeys.Count > 0 ? Strings.HostKeysForgotten
            : HostKeyRows.Count == 0 ? Strings.NoHostKeys
            : Strings.HostKeysHelp;
        RestoreKeysButton.Visibility = _forgottenKeys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHostKeySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ForgetKeysButton.IsEnabled = HostKeysGrid.SelectedItems.Count > 0;

    private void OnHostKeysFilter(object sender, TextChangedEventArgs e) => FilterHostKeys();

    /// <summary>Clés affichées dont le serveur ou l'empreinte contient le texte du filtre (toutes sans filtre).</summary>
    private void FilterHostKeys()
    {
        var filter = HostKeysFilterBox.Text.Trim();
        HostKeysGrid.ItemsSource = filter.Length == 0 ? HostKeyRows
            : HostKeyRows.Where(r => r.Server.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || r.Fingerprint.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Hauteur maximale : la zone de travail de l'écran où s'ouvre la fenêtre (celui de la fenêtre principale, qui peut
    /// être un second écran moins haut que l'écran principal) ; au-delà, le contenu défile.
    /// </summary>
    private void FitToScreen()
    {
        var anchor = Owner ?? this;
        var handle = new System.Windows.Interop.WindowInteropHelper(anchor).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            return;
        }

        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        MaxHeight = target.TransformFromDevice.Transform(new Point(0, area.Height)).Y;
    }

    /// <summary>Clés choisies retirées de la liste ; elles sont oubliées à l'enregistrement (« Annuler » les garde).</summary>
    private void OnForgetHostKeys(object sender, RoutedEventArgs e) => ForgetSelectedKeys();

    /// <summary>Clés retirées remises dans la liste : rien ne sera oublié à l'enregistrement.</summary>
    private void OnRestoreHostKeys(object sender, RoutedEventArgs e)
    {
        RestoreForgottenKeys();
        HostKeysGrid.Focus();
    }

    internal void RestoreForgottenKeys()
    {
        _forgottenKeys.Clear();
        ShowHostKeys();
    }

    internal void ForgetSelectedKeys()
    {
        foreach (var row in HostKeysGrid.SelectedItems.OfType<HostKeyRow>().ToList())
        {
            _forgottenKeys.Add(row.Entry);
        }

        ShowHostKeys();
    }

    /// <summary>À l'enregistrement : les clés choisies sont oubliées des réglages.</summary>
    internal void ApplyForgottenKeys() => ApplyForgottenKeys(_settings);

    private void ApplyForgottenKeys(AppSettings target)
    {
        foreach (var key in _forgottenKeys)
        {
            target.KnownHosts.Remove(key);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ApplyAll())
        {
            DialogResult = true;
        }
    }

    /// <summary>
    /// Vérifie d'abord tout sur une copie : une valeur refusée ne laisse rien d'appliqué à moitié. Puis les environnements
    /// importés et les valeurs affichées passent dans les réglages en vigueur.
    /// </summary>
    /// <returns>Faux après avoir montré la première valeur refusée (rien n'est appliqué).</returns>
    internal bool ApplyAll()
    {
        if (Pending() is null)
        {
            return false;
        }

        foreach (var import in _imports)
        {
            ApplyImport(import, _settings);
        }

        // Mêmes valeurs que sur la copie : déjà vérifiées.
        ReadInto(_settings);
        return true;
    }

    /// <summary>
    /// Réglages tels qu'ils seraient enregistrés (environnements importés, puis valeurs affichées), sur une copie ; null
    /// après avoir montré la première valeur refusée.
    /// </summary>
    private AppSettings? Pending()
    {
        var copy = _settings.Copy();
        foreach (var import in _imports)
        {
            ApplyImport(import, copy);
        }

        return ReadInto(copy) ? copy : null;
    }

    /// <summary>Environnement accepté à l'import : appliqué, et noté comme proposé (il n'est pas reproposé au démarrage).</summary>
    private static void ApplyImport((string Path, EnvironmentFile File) import, AppSettings target)
    {
        import.File.Profile.ApplyTo(target);
        target.EnvironmentFileHashes[import.Path.ToLowerInvariant()] = import.File.Sha256;
    }

    /// <summary>Valeurs affichées, vérifiées, écrites dans <paramref name="target"/> ; faux après avoir montré la première refusée.</summary>
    private bool ReadInto(AppSettings target)
    {
        PsmpGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ComponentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TagGrid.CommitEdit(DataGridEditingUnit.Row, true);
        FolderGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var host = PsmpBox.Text.Trim();
        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowError(Strings.InvalidPsmpAddress, PsmpBox);
            return false;
        }

        if (ParsePort(PortBox.Text) is not { } port)
        {
            ShowError(Strings.InvalidPort, PortBox);
            return false;
        }

        if (ReadPsmpServers(host) is not { } psmpServers)
        {
            return false;
        }

        var centralFile = CentralFileBox.Text.Trim().Trim('"');
        if (centralFile.Length > 0 && !EnvironmentProfile.IsFullPath(centralFile))
        {
            ShowError(Strings.InvalidCentralFile, CentralFileBox);
            return false;
        }

        var windowsComponent = WindowsComponentBox.Text.Trim();
        if (windowsComponent.Length > 0 && !AppSettings.IsValidComponentName(windowsComponent))
        {
            ShowError(Strings.InvalidComponentName, WindowsComponentBox);
            return false;
        }

        if (ReadComponents() is not { } componentByPlatform)
        {
            return false;
        }

        if (!int.TryParse(ArchiveThresholdBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) || threshold < 2)
        {
            ShowError(Strings.InvalidArchiveThreshold, ArchiveThresholdBox);
            return false;
        }

        if (!double.TryParse(FontSizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var fontSize)
            || fontSize < TerminalAppearance.MinFontSize || fontSize > TerminalAppearance.MaxFontSize)
        {
            ShowError(Text.Format(Strings.InvalidFontSize, TerminalAppearance.MinFontSize, TerminalAppearance.MaxFontSize), FontSizeBox);
            return false;
        }

        if (!EnvironmentProfile.IsValidDutyText(DutyTextBox.Text))
        {
            ShowError(Strings.InvalidDutyText, DutyTextBox);
            return false;
        }

        if (!InterventionReminder.TryParse(RemindersBox.Text, out var reminders, out int badReminder))
        {
            ShowError(Text.Format(Strings.InvalidReminder, badReminder), RemindersBox);
            return false;
        }

        if (ReadTags() is not { } tags)
        {
            return false;
        }

        if (ReadFolders() is not { } folders)
        {
            return false;
        }

        var dutyFile = DutyFileBox.Text.Trim();
        if (dutyFile.Length > 0 && !DutyReminderFile.IsValidPath(dutyFile))
        {
            ShowError(Strings.InvalidDutyFile, DutyFileBox);
            return false;
        }

        var x11Display = X11DisplayBox.Text.Trim();
        if (x11Display.Length == 0)
        {
            x11Display = X11Display.Default;
        }

        if (!X11Display.TryParse(x11Display, out _))
        {
            ShowError(Strings.X11DisplayInvalid, X11DisplayBox);
            return false;
        }

        var sharePortText = SharePortBox.Text.Trim();
        int sharePort = 0;
        if (sharePortText.Length > 0
            && (!int.TryParse(sharePortText, NumberStyles.None, CultureInfo.InvariantCulture, out sharePort) || !AppSettings.IsSharePort(sharePort)))
        {
            ShowError(Strings.InvalidSharePort, SharePortBox);
            return false;
        }

        var shareRelay = ShareRelayBox.Text.Trim().Trim('"');
        if (shareRelay.Length > 0 && !Core.Sharing.ShareInvitation.IsRelayFolder(shareRelay))
        {
            ShowError(Strings.InvalidShareRelay, ShareRelayBox);
            return false;
        }

        if (SessionSharingBox.IsChecked == true && ShareDirectBox.IsChecked != true && shareRelay.Length == 0)
        {
            ShowError(Strings.ShareNoRoute, ShareDirectBox);
            return false;
        }

        var compareArgs = CompareArgsBox.Text.Trim();
        if (compareArgs.Length == 0)
        {
            compareArgs = AppSettings.DefaultCompareArguments;
        }

        if (!compareArgs.Contains("{0}", StringComparison.Ordinal) || !compareArgs.Contains("{1}", StringComparison.Ordinal)
            || !IsValidFormat(compareArgs))
        {
            ShowError(Strings.InvalidCompareArguments, CompareArgsBox);
            return false;
        }

        target.Language = LanguageBox.SelectedValue as string ?? "";
        target.Theme = AppThemeBox.SelectedValue as AppTheme? ?? AppTheme.System;
        target.PsmpAddress = host;
        target.PsmpPort = port;
        target.PsmpServers = psmpServers;
        target.WindowsComponent = windowsComponent;
        target.EnvironmentFile = centralFile;
        target.SshInApp = SshInAppBox.IsChecked == true;
        target.PsmDesktopInTab = PsmDesktopInTabBox.IsChecked == true;
        target.PsmDynamicResolution = PsmDynamicResolutionBox.IsChecked == true;
        target.PauseRdpDisplay = PauseRdpDisplayBox.IsChecked == true;
        target.PsmRedirectDrives = DrivesNoneRadio.IsChecked != true;
        target.PsmShareFoldersOnly = DrivesFoldersRadio.IsChecked == true;
        target.PsmSharedFolders = folders;
        target.FollowTerminalFolder = FollowBox.IsChecked == true;
        target.X11Display = x11Display;
        target.SessionSharing = SessionSharingBox.IsChecked == true;
        target.ShareDirect = ShareDirectBox.IsChecked == true;
        target.SharePort = sharePort;
        target.ShareRelayFolder = shareRelay;
        target.DutyText = EnvironmentProfile.NormalizeDutyText(DutyTextBox.Text);
        target.DutyTextFile = dutyFile;
        target.InterventionReminders = reminders;
        target.ReplaceServerTags(tags.Tags, tags.Renamed);
        target.KeepPvwaSessionAlive = KeepAliveBox.IsChecked == true;
        target.KeepAccountListCopy = KeepAccountListCopyBox.IsChecked == true;
        target.CheckForUpdates = UpdateCheckBox.IsChecked == true;
        target.DebugLogEnabled = DebugLogBox.IsChecked == true;
        target.PreferredUploadProtocol = ScpRadio.IsChecked == true ? TransferProtocol.Scp : TransferProtocol.Sftp;
        target.OfferArchive = ArchiveBox.IsChecked == true;
        target.ArchiveThreshold = threshold;
        target.TailIndependentSession = TailSessionBox.IsChecked == true;
        target.TextEditor = EditorBox.Text.Trim().Trim('"');
        target.CompareTool = CompareToolBox.Text.Trim().Trim('"');
        target.TerminalTheme = (ThemeBox.SelectedItem as TerminalTheme ?? TerminalTheme.Campbell).Id;
        target.TerminalFontSize = fontSize;
        target.TerminalRightClickPastes = RightClickBox.IsChecked == true;
        target.ConfirmMultiLinePaste = ConfirmPasteBox.IsChecked == true;
        target.ConfirmCloseSession = ConfirmCloseBox.IsChecked == true;
        target.CompareToolArguments = compareArgs;
        target.ShowHiddenFiles = ShowHiddenBox.IsChecked == true;
        target.HiddenFileColumns = ColumnBoxes().Where(c => c.Box.IsChecked != true).Select(c => c.Column).ToList();
        target.TailLevelColors = TailColorsBox.IsChecked == true;
        target.TailHighlights = TailHighlightBox.Text.Trim();
        target.TailAlerts = TailAlertBox.Text.Trim();
        target.TailAlertNotify = TailNotifyBox.IsChecked == true;
        ApplyForgottenKeys(target);
        target.ComponentByPlatform = componentByPlatform;
        return true;
    }

    private void OnBrowseEditor(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Strings.TextEditorDialogTitle, Filter = Strings.ProgramsFilter };
        if (dialog.ShowDialog(this) == true)
        {
            EditorBox.Text = dialog.FileName;
        }
    }

    private void OnBrowseCompareTool(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Strings.CompareToolLabel.Replace("_", "").TrimEnd(':', ' '), Filter = Strings.ProgramsFilter };
        if (dialog.ShowDialog(this) == true)
        {
            CompareToolBox.Text = dialog.FileName;
        }
    }

    /// <summary>Arguments utilisables avec string.Format (accolades équilibrées, au plus {0} et {1}).</summary>
    private static bool IsValidFormat(string template)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, template, "a", "b");
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void OnBrowseCentralFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = EnvironmentImport.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            CentralFileBox.Text = dialog.FileName;
        }
    }

    /// <summary>Ouvre l'Explorateur sur le fichier du journal, pour le relire ou le joindre à un message.</summary>
    private void OnShowDebugLog(object sender, RoutedEventArgs e)
    {
        var path = DebugLog.FilePath ?? DebugLog.DefaultPath;
        if (!File.Exists(path))
        {
            ShowError(Strings.DebugLogMissing, DebugLogBox);
            return;
        }

        try
        {
            WindowsExplorer.ShowFile(path);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            ShowError(ex.Message, DebugLogShowButton);
        }
    }

    // ===================== Environnement de l'équipe : importer, exporter =====================

    /// <summary>
    /// Fichier d'environnement choisi : ses changements par rapport aux valeurs affichées sont montrés ; acceptés, ils sont
    /// repris dans les pages et appliqués à « Enregistrer », comme le reste de la fenêtre.
    /// </summary>
    private void OnImportEnvironment(object sender, RoutedEventArgs e)
    {
        if (Pending() is not { } shown)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = EnvironmentImport.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true && EnvironmentImport.Review(this, shown, dialog.FileName) is { } file)
        {
            TakeImport(dialog.FileName, file);
        }
    }

    /// <summary>Environnement accepté : repris dans les pages par-dessus les valeurs affichées, appliqué à l'enregistrement.</summary>
    internal void TakeImport(string path, EnvironmentFile file)
    {
        if (Pending() is not { } shown)
        {
            return;
        }

        var import = (Path: path.Trim(), File: file);
        _imports.Add(import);
        ApplyImport(import, shown);
        ShowValues(shown);
        ShowTeamBadges();
        ErrorText.Visibility = Visibility.Collapsed;
        EnvironmentText.Text = Text.Format(Strings.SettingsEnvImported, Path.GetFileName(import.Path));
    }

    /// <summary>Exporte les valeurs affichées (vérifiées) : ni mot de passe ni donnée personnelle.</summary>
    private void OnExportEnvironment(object sender, RoutedEventArgs e)
    {
        if (Pending() is not { } shown)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = EnvironmentProfile.FileName, Filter = EnvironmentImport.FileFilter, DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            EnvironmentProfile.FromSettings(shown, _pvwaHost).Save(dialog.FileName);
            ErrorText.Visibility = Visibility.Collapsed;
            EnvironmentText.Text = Text.Format(Strings.EnvExported, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(Text.Format(Strings.EnvExportFailed, ex.Message), ExportEnvButton);
        }
    }

    // ===================== Guide en ligne =====================

    private void OnGuide(object sender, RoutedEventArgs e) => OpenGuide();

    /// <summary>Section du guide de la page affichée, dans la langue de l'interface (navigateur par défaut).</summary>
    private void OpenGuide()
    {
        var page = (Pages.SelectedItem as TabItem)?.Tag as string ?? "general";
        var url = GuideLink.Url(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, UpdateChecker.CurrentVersion, GuideLink.SettingsPrefix + page);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorText.Text = Text.Format(Strings.SettingsGuideFailed, ex.Message);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    // ===================== Pastilles « Équipe » =====================

    /// <summary>
    /// Montre la pastille « Équipe » des réglages que définit le fichier d'environnement posé à côté de l'exécutable ou
    /// le fichier central, avec la valeur en vigueur. Fichiers lus en arrière-plan : le fichier central peut être sur un
    /// partage lent ou absent.
    /// </summary>
    private async Task LoadTeamBadgesAsync()
    {
        string[] paths = [EnvironmentImport.LocalFile, _settings.EnvironmentFile.Trim()];
        _teamFiles = await Task.Run(() => LoadTeamFiles(paths));
        ShowTeamBadges();
    }

    /// <summary>Pastilles d'après les réglages affichés (ceux en vigueur, ou après l'import d'un environnement).</summary>
    private void ShowTeamBadges()
    {
        var applied = TeamSettings(_teamFiles, _shown);
        foreach (var badge in TeamBadges(Pages))
        {
            if (applied.TryGetValue(badge.Setting, out var path))
            {
                var tip = Text.Format(Strings.TeamBadgeTip, path);
                badge.ToolTip = tip;
                System.Windows.Automation.AutomationProperties.SetHelpText(badge, tip);
                badge.Visibility = Visibility.Visible;
            }
            else
            {
                badge.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Fichiers d'environnement lisibles parmi ceux-ci ; un fichier absent, illisible ou refusé est ignoré.</summary>
    private static List<(string Path, EnvironmentProfile Profile)> LoadTeamFiles(IEnumerable<string> paths)
    {
        var files = new List<(string, EnvironmentProfile)>();
        foreach (var path in paths.Where(p => p.Length > 0))
        {
            try
            {
                if (File.Exists(path))
                {
                    files.Add((path, EnvironmentProfile.Load(path).Profile));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                           or EnvironmentFileException)
            {
                DebugLog.Write("env", $"Fichier d'environnement non lu pour les Paramètres : {path} ({ex.Message})");
            }
        }

        return files;
    }

    /// <summary>
    /// Réglages que ces fichiers définissent et dont la valeur est celle en vigueur, avec le fichier en cause (le dernier
    /// l'emporte, comme au démarrage où le fichier central est proposé après celui de l'exécutable). Un réglage refusé à
    /// l'import, ou changé depuis, n'a pas de pastille.
    /// </summary>
    internal static Dictionary<EnvironmentSetting, string> TeamSettings(IEnumerable<(string Path, EnvironmentProfile Profile)> files,
        AppSettings settings)
    {
        // Le port du partage direct est sur la ligne de la case « Partage direct » : une pastille pour les deux.
        static EnvironmentSetting Row(EnvironmentSetting setting) =>
            setting == EnvironmentSetting.SharePort ? EnvironmentSetting.ShareDirect : setting;

        var applied = new Dictionary<EnvironmentSetting, string>();
        foreach (var (path, profile) in files)
        {
            var differs = profile.Diff(settings).Select(c => Row(c.Setting)).ToHashSet();
            foreach (var setting in profile.DefinedSettings().Select(Row).Where(s => !differs.Contains(s)))
            {
                applied[setting] = path;
            }
        }

        return applied;
    }

    private static IEnumerable<TeamBadge> TeamBadges(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is TeamBadge badge)
            {
                yield return badge;
            }

            foreach (var inner in TeamBadges(child))
            {
                yield return inner;
            }
        }
    }

    private void OnBrowseShareRelay(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.SettingsShareRelayPick };
        if (dialog.ShowDialog(this) == true)
        {
            ShareRelayBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseDutyFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Strings.TerminalSaveFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            DutyFileBox.Text = dialog.FileName;
        }
    }

    // ===================== PSMP par domaine =====================

    /// <summary>
    /// Ligne du tableau des PSMP. Le domaine suit l'adresse (psmp.paris.corp.com → paris.corp.com) tant qu'il n'a pas été
    /// changé à la main.
    /// </summary>
    internal sealed class PsmpRow : INotifyPropertyChanged
    {
        private string _address = "";
        private string _port = "22";
        private string _domain = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Address
        {
            get => _address;
            set
            {
                bool follow = _domain.Length == 0 || _domain == PsmpRouting.DomainOf(_address);
                _address = value ?? "";
                Changed(nameof(Address));
                if (follow)
                {
                    _domain = PsmpRouting.DomainOf(_address);
                    Changed(nameof(Domain));
                }
            }
        }

        public string Port
        {
            get => _port;
            set
            {
                _port = value ?? "";
                Changed(nameof(Port));
            }
        }

        public string Domain
        {
            get => _domain;
            set
            {
                _domain = value ?? "";
                Changed(nameof(Domain));
            }
        }

        public static PsmpRow From(PsmpServer psmp)
        {
            var row = new PsmpRow { Address = psmp.Address, Port = psmp.Port.ToString(CultureInfo.InvariantCulture) };
            if (psmp.Domain.Length > 0)
            {
                row.Domain = psmp.Domain;
            }

            return row;
        }

        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal IList<PsmpRow> PsmpRows => _psmpRows;

    private void AddPsmpRow(PsmpRow row)
    {
        row.PropertyChanged += (_, _) => UpdatePsmpTest();
        _psmpRows.Add(row);
    }

    /// <summary>Nouvelle ligne, prête à la saisie de l'adresse.</summary>
    private void OnAddPsmp(object sender, RoutedEventArgs e)
    {
        PsmpGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var row = new PsmpRow();
        AddPsmpRow(row);
        PsmpGrid.SelectedItem = row;
        PsmpGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            PsmpGrid.CurrentCell = new DataGridCellInfo(row, PsmpGrid.Columns[0]);
            PsmpGrid.Focus();
            PsmpGrid.BeginEdit();
        });
    }

    private void OnRemovePsmp(object sender, RoutedEventArgs e)
    {
        if (PsmpGrid.SelectedItem is PsmpRow row)
        {
            PsmpGrid.CancelEdit(DataGridEditingUnit.Row);
            _psmpRows.Remove(row);
            UpdatePsmpTest();
        }
    }

    private void OnPsmpSelected(object sender, SelectionChangedEventArgs e) => RemovePsmpButton.IsEnabled = PsmpGrid.SelectedItem is PsmpRow;

    private void OnPsmpChanged(object sender, TextChangedEventArgs e) => UpdatePsmpTest();

    /// <summary>PSMP qu'utiliserait le serveur saisi, d'après les valeurs affichées (pas encore enregistrées).</summary>
    private void UpdatePsmpTest()
    {
        var server = PsmpTestBox.Text.Trim();
        if (server.Length == 0)
        {
            PsmpTestText.Text = Strings.PsmpTestPrompt;
            return;
        }

        var preview = new AppSettings
        {
            PsmpAddress = PsmpBox.Text.Trim(),
            PsmpPort = ParsePort(PortBox.Text) ?? 22,
            PsmpServers = _psmpRows.Select(r => new PsmpServer { Address = r.Address, Port = ParsePort(r.Port) ?? 22, Domain = r.Domain }).ToList(),
        };
        PsmpTestText.Text = PsmpRouting.Resolve(preview, server) switch
        {
            null => Strings.PsmpTestNone,
            { Domain: { } domain } psmp => Text.Format(Strings.PsmpTestMatch, psmp.Host, psmp.Port, domain),
            var psmp => Text.Format(Strings.PsmpTestFallback, psmp.Host, psmp.Port),
        };
    }

    private static int? ParsePort(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    /// <summary>
    /// PSMP du tableau, vérifiés (lignes vides ignorées) ; null après avoir montré la première erreur. Deux PSMP pour le
    /// même domaine sont refusés : le second ne servirait jamais.
    /// </summary>
    internal List<PsmpServer>? ReadPsmpServers(string defaultHost)
    {
        var servers = new List<PsmpServer>();
        var domains = new PsmpDomainCheck(defaultHost);
        for (int i = 0; i < _psmpRows.Count; i++)
        {
            var row = _psmpRows[i];
            var address = row.Address.Trim();
            if (address.Length == 0 && PsmpRouting.NormalizeDomain(row.Domain).Length == 0)
            {
                continue;
            }

            int line = i + 1;
            string? error = null;
            var port = ParsePort(row.Port);
            var deduced = PsmpRouting.DomainOf(address);
            string domain = "";
            if (Uri.CheckHostName(address) == UriHostNameType.Unknown)
            {
                error = Text.Format(Strings.InvalidPsmpRowAddress, line);
            }
            else if (port is null)
            {
                error = Text.Format(Strings.InvalidPsmpRowPort, line);
            }
            else
            {
                error = domains.Check(address, row.Domain, out domain) switch
                {
                    PsmpDomainProblem.Missing => Text.Format(Strings.PsmpRowDomainMissing, line),
                    PsmpDomainProblem.Invalid => Text.Format(Strings.InvalidPsmpRowDomain, line),
                    PsmpDomainProblem.Duplicate => Text.Format(Strings.PsmpDuplicateDomain, domain),
                    _ => null,
                };
            }

            if (error is not null)
            {
                PsmpGrid.SelectedItem = row;
                PsmpGrid.ScrollIntoView(row);
                ShowError(error, PsmpGrid);
                return null;
            }

            // Domaine de l'adresse : non enregistré, il suivra l'adresse si on la modifie dans le fichier.
            servers.Add(new PsmpServer { Address = address, Port = port!.Value, Domain = domain == deduced ? "" : domain });
        }

        return servers;
    }

    // ===================== Dossiers partagés =====================

    /// <summary>Ligne du tableau des dossiers partagés (« Ces dossiers seulement ») : nom vu dans la session et chemin.</summary>
    internal sealed class FolderRow : INotifyPropertyChanged
    {
        private string _name = "";
        private string _path = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public string Path
        {
            get => _path;
            set
            {
                _path = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
            }
        }
    }

    internal IList<FolderRow> FolderRows => _folderRows;

    /// <summary>
    /// Dossier choisi dans l'Explorateur : nom proposé d'après le dossier (caractères acceptés seulement, sans doublon),
    /// modifiable dans le tableau.
    /// </summary>
    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        FolderGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var row = new FolderRow { Name = FolderName(dialog.FolderName), Path = dialog.FolderName };
        _folderRows.Add(row);
        FolderGrid.SelectedItem = row;
        FolderGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            FolderGrid.CurrentCell = new DataGridCellInfo(row, FolderGrid.Columns[0]);
            FolderGrid.Focus();
        });
    }

    /// <summary>Nom proposé pour un dossier : son nom réduit aux caractères acceptés, différent de ceux du tableau.</summary>
    internal string FolderName(string path)
    {
        // Dernier élément du chemin : le dossier, le partage (« \\fs01\Partage ») ou la lettre du lecteur (« C:\ »).
        var leaf = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd(':') ?? "";
        var name = new string([.. leaf.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]).Trim('.', '-', '_');
        if (name.Length == 0)
        {
            name = "Dossier";
        }

        name = name[..Math.Min(name.Length, SharedFolderRules.MaxNameLength - 3)];
        var unique = name;
        for (int i = 2; _folderRows.Any(r => string.Equals(r.Name.Trim(), unique, StringComparison.OrdinalIgnoreCase)); i++)
        {
            unique = string.Create(CultureInfo.InvariantCulture, $"{name}{i}");
        }

        return unique;
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is FolderRow row)
        {
            FolderGrid.CancelEdit(DataGridEditingUnit.Row);
            _folderRows.Remove(row);
        }
    }

    private void OnFolderSelected(object sender, SelectionChangedEventArgs e) => RemoveFolderButton.IsEnabled = FolderGrid.SelectedItem is FolderRow;

    /// <summary>
    /// Dossiers du tableau, vérifiés (lignes vides ignorées) ; null après avoir montré la première erreur. « Ces dossiers
    /// seulement » sans dossier est refusé : rien ne serait partagé, « Aucun » le dit mieux.
    /// </summary>
    internal List<SharedFolder>? ReadFolders()
    {
        var folders = new List<SharedFolder>();
        for (int i = 0; i < _folderRows.Count; i++)
        {
            var row = _folderRows[i];
            var name = row.Name.Trim();
            var path = row.Path.Trim().Trim('"');
            if (name.Length == 0 && path.Length == 0)
            {
                continue;
            }

            string? error = !SharedFolderRules.IsValidName(name) ? Text.Format(Strings.InvalidFolderName, i + 1, SharedFolderRules.MaxNameLength)
                : !SharedFolderRules.IsValidPath(path) ? Text.Format(Strings.InvalidFolderPath, i + 1)
                : folders.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ? Text.Format(Strings.DuplicateFolderName, name)
                : folders.Count == SharedFolderRules.MaxFolders ? Text.Format(Strings.TooManyFolders, SharedFolderRules.MaxFolders)
                : null;
            if (error is not null)
            {
                FolderGrid.SelectedItem = row;
                FolderGrid.ScrollIntoView(row);
                ShowError(error, FolderGrid);
                return null;
            }

            folders.Add(new SharedFolder(name, path));
        }

        if (DrivesFoldersRadio.IsChecked == true && folders.Count == 0)
        {
            ShowError(Strings.FoldersEmpty, DrivesFoldersRadio);
            return null;
        }

        return folders;
    }

    // ===================== Étiquettes des serveurs =====================

    /// <summary>Couleur proposée pour une étiquette : son code, son nom et sa pastille.</summary>
    internal sealed record ColorChoice(string Hex, string Name, Brush Brush);

    /// <summary>Couleurs proposées, dans l'ordre de <see cref="ServerTagRules.Palette"/>.</summary>
    private static IReadOnlyList<ColorChoice> PaletteChoices() =>
    [
        .. ServerTagRules.Palette.Select((hex, i) => new ColorChoice(hex, i switch
        {
            0 => Strings.ColorRed,
            1 => Strings.ColorPink,
            2 => Strings.ColorPurple,
            3 => Strings.ColorIndigo,
            4 => Strings.ColorBlue,
            5 => Strings.ColorTeal,
            6 => Strings.ColorGreen,
            7 => Strings.ColorLightGreen,
            8 => Strings.ColorYellow,
            9 => Strings.ColorOrange,
            10 => Strings.ColorBrown,
            _ => Strings.ColorGray,
        }, ServerTagView.Background(new ServerTag("", hex)))),
    ];

    /// <summary>
    /// Ligne du tableau des étiquettes : nom, couleur, et aperçu de la pastille ; <see cref="Original"/> est le nom à
    /// l'ouverture, pour que les serveurs suivent un renommage.
    /// </summary>
    internal sealed class TagRow : INotifyPropertyChanged
    {
        private string _name;
        private string _color;

        public TagRow(string? original, string name, string color)
        {
            Original = original;
            _name = name;
            _color = color.ToUpperInvariant();
            var palette = PaletteChoices();
            // Couleur venue d'un fichier d'environnement, hors de la palette : proposée aussi, pour ne pas la perdre.
            Colors = palette.Any(c => c.Hex == _color)
                ? palette
                : [.. palette, new ColorChoice(_color, Text.Format(Strings.ColorOther, _color), ServerTagView.Background(new ServerTag("", _color)))];
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string? Original { get; }

        public IReadOnlyList<ColorChoice> Colors { get; }

        public string Name
        {
            get => _name;
            set
            {
                _name = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public string Color
        {
            get => _color;
            set
            {
                _color = value ?? ServerTagRules.NeutralColor;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            }
        }

        public Brush Background => ServerTagView.Background(new ServerTag(_name, _color));

        public Brush Foreground => ServerTagView.Foreground(new ServerTag(_name, _color));

        /// <summary>Nom accessible de la liste des couleurs : l'étiquette et sa couleur.</summary>
        public string ColorName => $"{_name}, {Colors.FirstOrDefault(c => c.Hex == _color)?.Name}";
    }

    internal IList<TagRow> TagRows => _tagRows;

    /// <summary>Nouvelle étiquette : un nom libre et une couleur de la palette encore inutilisée, prête à être renommée.</summary>
    private void OnAddTag(object sender, RoutedEventArgs e)
    {
        TagGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var name = Strings.ServerTagNewName;
        for (int i = 2; _tagRows.Any(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{Strings.ServerTagNewName} {i}";
        }

        var color = ServerTagRules.Palette.FirstOrDefault(p => _tagRows.All(r => r.Color != p)) ?? ServerTagRules.NeutralColor;
        var row = new TagRow(null, name, color);
        _tagRows.Add(row);
        TagGrid.SelectedItem = row;
        TagGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            TagGrid.CurrentCell = new DataGridCellInfo(row, TagGrid.Columns[1]);
            TagGrid.Focus();
            TagGrid.BeginEdit();
        });
    }

    private void OnRemoveTag(object sender, RoutedEventArgs e)
    {
        if (TagGrid.SelectedItem is TagRow row)
        {
            TagGrid.CancelEdit(DataGridEditingUnit.Row);
            _tagRows.Remove(row);
        }
    }

    private void OnTagSelected(object sender, SelectionChangedEventArgs e) => RemoveTagButton.IsEnabled = TagGrid.SelectedItem is TagRow;

    /// <summary>« Ajouter une étiquette » grisé au nombre maximal d'étiquettes, avec la raison en infobulle.</summary>
    private void UpdateAddTagButton()
    {
        AddTagButton.IsEnabled = _tagRows.Count < ServerTagRules.MaxTags;
        AddTagButton.ToolTip = AddTagButton.IsEnabled ? null : Strings.ServerTagLimitTip;
    }

    /// <summary>
    /// Étiquettes du tableau, vérifiées, et les renommages (ancien nom → nouveau) que les serveurs suivront ; null après
    /// avoir montré la première erreur (nom vide, trop long, invisible ou en double).
    /// </summary>
    internal (List<ServerTag> Tags, Dictionary<string, string> Renamed)? ReadTags()
    {
        var tags = new List<ServerTag>();
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _tagRows)
        {
            var name = (row.Name ?? "").Trim();
            // Au-delà du nombre maximal, l'étiquette serait effacée sans un mot à l'enregistrement : refusée ici.
            if (!ServerTagRules.IsValidName(name) || ServerTagRules.Find(tags, name) is not null || tags.Count == ServerTagRules.MaxTags)
            {
                TagGrid.SelectedItem = row;
                TagGrid.ScrollIntoView(row);
                ShowError(Strings.InvalidServerTag, TagGrid);
                return null;
            }

            tags.Add(new ServerTag(name, ServerTagRules.IsValidColor(row.Color) ? row.Color : ServerTagRules.NeutralColor));
            if (row.Original is { } original && !string.Equals(original, name, StringComparison.Ordinal))
            {
                renamed[original] = name;
            }
        }

        return (tags, renamed);
    }

    // ===================== Composant par plateforme =====================

    /// <summary>Ligne du tableau des composants : ID de plateforme du PVWA (WinDomain, UnixSSH…) et composant PSM.</summary>
    internal sealed class ComponentRow
    {
        public string Platform { get; set; } = "";

        public string Component { get; set; } = "";
    }

    internal IList<ComponentRow> ComponentRows => _componentRows;

    /// <summary>Nouvelle ligne, prête à la saisie de la plateforme.</summary>
    private void OnAddComponent(object sender, RoutedEventArgs e)
    {
        ComponentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var row = new ComponentRow();
        _componentRows.Add(row);
        ComponentGrid.SelectedItem = row;
        ComponentGrid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            ComponentGrid.CurrentCell = new DataGridCellInfo(row, ComponentGrid.Columns[0]);
            ComponentGrid.Focus();
            ComponentGrid.BeginEdit();
        });
    }

    private void OnRemoveComponent(object sender, RoutedEventArgs e)
    {
        if (ComponentGrid.SelectedItem is ComponentRow row)
        {
            ComponentGrid.CancelEdit(DataGridEditingUnit.Row);
            _componentRows.Remove(row);
        }
    }

    private void OnComponentSelected(object sender, SelectionChangedEventArgs e) =>
        RemoveComponentButton.IsEnabled = ComponentGrid.SelectedItem is ComponentRow;

    /// <summary>
    /// Composants du tableau, vérifiés (lignes vides ignorées) ; null après avoir montré la première erreur. Une plateforme
    /// en double est refusée : une seule ligne servirait.
    /// </summary>
    internal Dictionary<string, string>? ReadComponents()
    {
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _componentRows.Count; i++)
        {
            var row = _componentRows[i];
            var platform = (row.Platform ?? "").Trim();
            var component = (row.Component ?? "").Trim();
            if (platform.Length == 0 && component.Length == 0)
            {
                continue;
            }

            int line = i + 1;
            string? error = null;
            if (platform.Length is 0 or > 100 || platform.Any(char.IsControl))
            {
                error = Text.Format(Strings.ComponentRowPlatformMissing, line);
            }
            else if (!AppSettings.IsValidComponentName(component))
            {
                error = Text.Format(Strings.InvalidComponentRow, line);
            }
            else if (!components.TryAdd(platform, component))
            {
                error = Text.Format(Strings.ComponentDuplicatePlatform, platform);
            }

            if (error is not null)
            {
                ComponentGrid.SelectedItem = row;
                ComponentGrid.ScrollIntoView(row);
                ShowError(error, ComponentGrid);
                return null;
            }
        }

        return new Dictionary<string, string>(components, StringComparer.Ordinal);
    }

    /// <summary>Erreur en pied de fenêtre ; la page du champ en cause s'affiche et le champ prend le focus.</summary>
    private void ShowError(string message, System.Windows.Controls.Control field)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        for (DependencyObject? node = field; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node)
                                                                         ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TabItem page)
            {
                Pages.SelectedItem = page;
                break;
            }
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            field.Focus();
            (field as System.Windows.Controls.TextBox)?.SelectAll();
        });
    }

    // ===================== Coffre local (actions immédiates) =====================

    private void UpdateStore()
    {
        bool exists = _store?.Exists == true;
        bool unlocked = _store?.IsUnlocked == true;
        StoreText.Text = !exists ? Strings.LocalStoreStateNone
            : unlocked ? Text.Format(Strings.LocalStoreStateUnlocked, _store!.Ids.Count)
            : Strings.LocalStoreStateLocked;
        StoreCreateButton.Visibility = _store is not null && !exists ? Visibility.Visible : Visibility.Collapsed;
        StoreUnlockButton.Visibility = exists && !unlocked ? Visibility.Visible : Visibility.Collapsed;
        StoreChangeButton.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        StoreDeleteButton.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStoreCreate(object sender, RoutedEventArgs e) => ShowStoreDialog(LocalStoreDialog.Mode.Create);

    private void OnStoreUnlock(object sender, RoutedEventArgs e) => ShowStoreDialog(LocalStoreDialog.Mode.Unlock);

    private void OnStoreChange(object sender, RoutedEventArgs e) => ShowStoreDialog(LocalStoreDialog.Mode.ChangePassword);

    /// <summary>Coffres KeePass dont le mot de passe maître reste mémorisé dans le coffre local.</summary>
    internal static IReadOnlyList<string> RememberedFolderIds(AppSettings settings) =>
        [.. settings.KeePassFolders.Where(f => f.RememberPassword).Select(f => f.Id)];

    private void ShowStoreDialog(LocalStoreDialog.Mode mode)
    {
        if (_store is not null)
        {
            new LocalStoreDialog(_store, mode, RememberedFolderIds(_settings), offerLater: false) { Owner = this }.ShowDialog();
            UpdateStore();
        }
    }

    private void OnStoreDelete(object sender, RoutedEventArgs e)
    {
        if (_store is null || !ConfirmDialog.Destructive(this, Strings.LocalStoreTitle, Strings.LocalStoreDeleteHeading,
                Strings.LocalStoreDeleteAction, bullets: [Strings.LocalStoreDeleteForget, Strings.LocalStoreDeleteKeePass, Strings.LocalStoreDeleteNow]))
        {
            return;
        }

        try
        {
            _store.Delete();
            foreach (var folder in _settings.KeePassFolders)
            {
                folder.RememberPassword = false;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, Strings.LocalStoreTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateStore();
    }
}
