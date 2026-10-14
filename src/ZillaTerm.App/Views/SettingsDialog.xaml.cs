using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Terminal;
using ZillaTerm.Core;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly LocalSecretStore? _store;
    private readonly HashSet<string> _forgottenKeys = new(StringComparer.Ordinal);
    private readonly ObservableCollection<PsmpRow> _psmpRows = [];
    private readonly ObservableCollection<ComponentRow> _componentRows = [];
    private readonly ObservableCollection<TagRow> _tagRows = [];

    /// <param name="store">Coffre local des mots de passe maîtres KeePass, géré depuis cette fenêtre.</param>
    public SettingsDialog(AppSettings settings, LocalSecretStore? store = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => FitToScreen();
        _settings = settings;
        _store = store;
        KeepAliveBox.IsChecked = settings.KeepPvwaSessionAlive;
        UpdateCheckBox.IsChecked = settings.CheckForUpdates;
        CentralFileBox.Text = settings.EnvironmentFile;
        UpdateStore();
        LanguageBox.DisplayMemberPath = "Value";
        LanguageBox.SelectedValuePath = "Key";
        LanguageBox.ItemsSource = new[] { new KeyValuePair<string, string>("", CoreStrings.LanguageSystem) }
            .Concat(UiLanguage.Supported.Select(code => new KeyValuePair<string, string>(code, UiLanguage.NativeName(code))))
            .ToList();
        LanguageBox.SelectedValue = UiLanguage.Normalize(settings.Language);
        AppThemeBox.DisplayMemberPath = "Value";
        AppThemeBox.SelectedValuePath = "Key";
        AppThemeBox.ItemsSource = new[]
        {
            new KeyValuePair<AppTheme, string>(AppTheme.System, Strings.AppThemeSystem),
            new KeyValuePair<AppTheme, string>(AppTheme.Light, Strings.AppThemeLight),
            new KeyValuePair<AppTheme, string>(AppTheme.Dark, Strings.AppThemeDark),
        };
        AppThemeBox.SelectedValue = settings.Theme;
        PsmpBox.Text = settings.PsmpAddress;
        PortBox.Text = settings.PsmpPort.ToString(CultureInfo.InvariantCulture);
        foreach (var psmp in settings.PsmpServers)
        {
            AddPsmpRow(PsmpRow.From(psmp));
        }

        PsmpGrid.ItemsSource = _psmpRows;
        UpdatePsmpTest();
        SshInAppBox.IsChecked = settings.SshInApp;
        PsmDesktopInTabBox.IsChecked = settings.PsmDesktopInTab;
        PsmDynamicResolutionBox.IsChecked = settings.PsmDynamicResolution;
        PsmRedirectDrivesBox.IsChecked = settings.PsmRedirectDrives;
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
        ThemeBox.ItemsSource = TerminalTheme.All;
        ThemeBox.SelectedItem = TerminalTheme.Find(settings.TerminalTheme);
        FontSizeBox.Text = settings.TerminalFontSize.ToString(CultureInfo.CurrentCulture);
        RightClickBox.IsChecked = settings.TerminalRightClickPastes;
        ConfirmPasteBox.IsChecked = settings.ConfirmMultiLinePaste;
        ConfirmCloseBox.IsChecked = settings.ConfirmCloseSession;
        CompareArgsBox.Text = settings.CompareToolArguments;
        (settings.PreferredUploadProtocol == TransferProtocol.Scp ? ScpRadio : SftpRadio).IsChecked = true;
        ArchiveBox.IsChecked = settings.OfferArchive;
        ArchiveThresholdBox.Text = settings.ArchiveThreshold.ToString(CultureInfo.InvariantCulture);
        TailSessionBox.IsChecked = settings.TailIndependentSession;
        ShowHostKeys();
        WindowsComponentBox.ItemsSource = AccountClassifier.CommonComponents;
        WindowsComponentBox.Text = settings.WindowsComponent;
        foreach (var (platform, component) in settings.ComponentByPlatform.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            _componentRows.Add(new ComponentRow { Platform = platform, Component = component });
        }

        ComponentGrid.ItemsSource = _componentRows;
        foreach (var tag in settings.ServerTags)
        {
            _tagRows.Add(new TagRow(tag.Name, tag.Name, tag.Color));
        }

        TagGrid.ItemsSource = _tagRows;
        _tagRows.CollectionChanged += (_, _) => UpdateAddTagButton();
        UpdateAddTagButton();
        Loaded += (_, _) => (Pages.SelectedItem == SharingPage ? (Control)SessionSharingBox : LanguageBox).Focus();
    }

    /// <summary>Ouvre la page « Partage » (partage demandé alors qu'il est désactivé).</summary>
    internal void ShowSharingPage() => Pages.SelectedItem = SharingPage;

    /// <summary>
    /// Clé d'hôte acceptée : « hôte:port », type de clé (ou X.509 pour un certificat), empreinte SHA-256, et sa clé
    /// dans les réglages (un serveur peut avoir une clé de chaque type).
    /// </summary>
    internal sealed record HostKeyRow(string Server, string Algorithm, string Fingerprint, string Entry);

    /// <summary>Clés affichées (celles choisies pour l'oubli disparaissent de la liste, l'oubli se fait à l'enregistrement).</summary>
    internal IReadOnlyList<HostKeyRow> HostKeyRows { get; private set; } = [];

    private void ShowHostKeys()
    {
        HostKeyRows = _settings.KnownHosts
            .Where(kv => !_forgottenKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                int space = kv.Value.IndexOf(' ');
                return new HostKeyRow(KnownHosts.Server(kv.Key), space > 0 ? kv.Value[..space] : "", space > 0 ? kv.Value[(space + 1)..] : kv.Value,
                    kv.Key);
            })
            .ToList();
        HostKeysGrid.ItemsSource = HostKeyRows;
        HostKeysText.Text = _forgottenKeys.Count > 0 ? Strings.HostKeysForgotten
            : HostKeyRows.Count == 0 ? Strings.NoHostKeys
            : Strings.HostKeysHelp;
    }

    private void OnHostKeySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ForgetKeysButton.IsEnabled = HostKeysGrid.SelectedItems.Count > 0;

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

    internal void ForgetSelectedKeys()
    {
        foreach (var row in HostKeysGrid.SelectedItems.OfType<HostKeyRow>().ToList())
        {
            _forgottenKeys.Add(row.Entry);
        }

        ShowHostKeys();
    }

    /// <summary>À l'enregistrement : les clés choisies sont oubliées des réglages.</summary>
    internal void ApplyForgottenKeys()
    {
        foreach (var key in _forgottenKeys)
        {
            _settings.KnownHosts.Remove(key);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        PsmpGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ComponentGrid.CommitEdit(DataGridEditingUnit.Row, true);
        TagGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var host = PsmpBox.Text.Trim();
        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowError(Strings.InvalidPsmpAddress, PsmpBox);
            return;
        }

        if (ParsePort(PortBox.Text) is not { } port)
        {
            ShowError(Strings.InvalidPort, PortBox);
            return;
        }

        if (ReadPsmpServers(host) is not { } psmpServers)
        {
            return;
        }

        var centralFile = CentralFileBox.Text.Trim().Trim('"');
        if (centralFile.Length > 0 && !EnvironmentProfile.IsFullPath(centralFile))
        {
            ShowError(Strings.InvalidCentralFile, CentralFileBox);
            return;
        }

        var windowsComponent = WindowsComponentBox.Text.Trim();
        if (windowsComponent.Length > 0 && !AppSettings.IsValidComponentName(windowsComponent))
        {
            ShowError(Strings.InvalidComponentName, WindowsComponentBox);
            return;
        }

        if (ReadComponents() is not { } componentByPlatform)
        {
            return;
        }

        if (!int.TryParse(ArchiveThresholdBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var threshold) || threshold < 2)
        {
            ShowError(Strings.InvalidArchiveThreshold, ArchiveThresholdBox);
            return;
        }

        if (!double.TryParse(FontSizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var fontSize)
            || fontSize < TerminalAppearance.MinFontSize || fontSize > TerminalAppearance.MaxFontSize)
        {
            ShowError(Text.Format(Strings.InvalidFontSize, TerminalAppearance.MinFontSize, TerminalAppearance.MaxFontSize), FontSizeBox);
            return;
        }

        if (!EnvironmentProfile.IsValidDutyText(DutyTextBox.Text))
        {
            ShowError(Strings.InvalidDutyText, DutyTextBox);
            return;
        }

        if (!InterventionReminder.TryParse(RemindersBox.Text, out var reminders, out int badReminder))
        {
            ShowError(Text.Format(Strings.InvalidReminder, badReminder), RemindersBox);
            return;
        }

        if (ReadTags() is not { } tags)
        {
            return;
        }

        var dutyFile = DutyFileBox.Text.Trim();
        if (dutyFile.Length > 0 && !DutyReminderFile.IsValidPath(dutyFile))
        {
            ShowError(Strings.InvalidDutyFile, DutyFileBox);
            return;
        }

        var x11Display = X11DisplayBox.Text.Trim();
        if (x11Display.Length == 0)
        {
            x11Display = X11Display.Default;
        }

        if (!X11Display.TryParse(x11Display, out _))
        {
            ShowError(Strings.X11DisplayInvalid, X11DisplayBox);
            return;
        }

        var sharePortText = SharePortBox.Text.Trim();
        int sharePort = 0;
        if (sharePortText.Length > 0
            && (!int.TryParse(sharePortText, NumberStyles.None, CultureInfo.InvariantCulture, out sharePort) || !AppSettings.IsSharePort(sharePort)))
        {
            ShowError(Strings.InvalidSharePort, SharePortBox);
            return;
        }

        var shareRelay = ShareRelayBox.Text.Trim().Trim('"');
        if (shareRelay.Length > 0 && !Core.Sharing.ShareInvitation.IsRelayFolder(shareRelay))
        {
            ShowError(Strings.InvalidShareRelay, ShareRelayBox);
            return;
        }

        if (SessionSharingBox.IsChecked == true && ShareDirectBox.IsChecked != true && shareRelay.Length == 0)
        {
            ShowError(Strings.ShareNoRoute, ShareDirectBox);
            return;
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
            return;
        }

        _settings.Language = LanguageBox.SelectedValue as string ?? "";
        _settings.Theme = AppThemeBox.SelectedValue as AppTheme? ?? AppTheme.System;
        _settings.PsmpAddress = host;
        _settings.PsmpPort = port;
        _settings.PsmpServers = psmpServers;
        _settings.WindowsComponent = windowsComponent;
        _settings.EnvironmentFile = centralFile;
        _settings.SshInApp = SshInAppBox.IsChecked == true;
        _settings.PsmDesktopInTab = PsmDesktopInTabBox.IsChecked == true;
        _settings.PsmDynamicResolution = PsmDynamicResolutionBox.IsChecked == true;
        _settings.PsmRedirectDrives = PsmRedirectDrivesBox.IsChecked == true;
        _settings.FollowTerminalFolder = FollowBox.IsChecked == true;
        _settings.X11Display = x11Display;
        _settings.SessionSharing = SessionSharingBox.IsChecked == true;
        _settings.ShareDirect = ShareDirectBox.IsChecked == true;
        _settings.SharePort = sharePort;
        _settings.ShareRelayFolder = shareRelay;
        _settings.DutyText = EnvironmentProfile.NormalizeDutyText(DutyTextBox.Text);
        _settings.DutyTextFile = dutyFile;
        _settings.InterventionReminders = reminders;
        _settings.ReplaceServerTags(tags.Tags, tags.Renamed);
        _settings.KeepPvwaSessionAlive = KeepAliveBox.IsChecked == true;
        _settings.CheckForUpdates = UpdateCheckBox.IsChecked == true;
        _settings.PreferredUploadProtocol = ScpRadio.IsChecked == true ? TransferProtocol.Scp : TransferProtocol.Sftp;
        _settings.OfferArchive = ArchiveBox.IsChecked == true;
        _settings.ArchiveThreshold = threshold;
        _settings.TailIndependentSession = TailSessionBox.IsChecked == true;
        _settings.TextEditor = EditorBox.Text.Trim().Trim('"');
        _settings.CompareTool = CompareToolBox.Text.Trim().Trim('"');
        _settings.TerminalTheme = (ThemeBox.SelectedItem as TerminalTheme ?? TerminalTheme.Campbell).Id;
        _settings.TerminalFontSize = fontSize;
        _settings.TerminalRightClickPastes = RightClickBox.IsChecked == true;
        _settings.ConfirmMultiLinePaste = ConfirmPasteBox.IsChecked == true;
        _settings.ConfirmCloseSession = ConfirmCloseBox.IsChecked == true;
        _settings.CompareToolArguments = compareArgs;
        ApplyForgottenKeys();
        _settings.ComponentByPlatform = componentByPlatform;

        DialogResult = true;
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

    /// <summary>Erreur en pied de fenêtre ; la page du champ en cause s'affiche et le champ prend le focus.</summary>
    private void OnBrowseCentralFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = EnvironmentImport.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            CentralFileBox.Text = dialog.FileName;
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
