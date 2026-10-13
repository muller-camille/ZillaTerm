using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Migration;

namespace ZillaTerm.App.Views;

/// <summary>
/// Reprise dans « Mes serveurs » des sessions d'un autre logiciel (PuTTY, KiTTY, WinSCP, mRemoteNG, RDCMan, SecureCRT,
/// OpenSSH, fichiers .rdp ou .mxtsessions) : lecture de la source, rapprochement de chaque session avec un compte du
/// PVWA, aperçu, import, puis résultat de chaque serveur (importé ou non, et pourquoi), exportable en CSV. Aucun mot de
/// passe n'est lu ; chaque serveur importé s'ouvre via PSM ou le PSMP.
/// </summary>
public partial class SessionImportWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _pvwaHost;
    private readonly SessionMatcher _matcher;
    private readonly Action<SessionImport> _imported;
    private readonly CheckBox _allBox;
    private SessionImport? _import;
    private List<SessionImportRow> _rows = [];
    private string _source = "";
    private bool _reading;
    // Dossier saisi appliqué après une pause dans la frappe : chaque changement recalcule toutes les sessions.
    private readonly System.Windows.Threading.DispatcherTimer _folderTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _includingAll;

    /// <param name="imported">Appelé après l'import (enregistrer les préférences, rafraîchir « Mes serveurs »).</param>
    public SessionImportWindow(AppSettings settings, string pvwaHost, IEnumerable<PvwaAccount> accounts, KnownDomains domains,
        Action<SessionImport> imported)
    {
        InitializeComponent();
        _settings = settings;
        _pvwaHost = pvwaHost;
        _matcher = new SessionMatcher(accounts, domains);
        _imported = imported;
        _allBox = new CheckBox
        {
            IsChecked = true,
            IsEnabled = false,
            ToolTip = Strings.SessionImportAllTip,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(_allBox, Strings.SessionImportAllTip);
        _allBox.Click += OnIncludeAll;
        IncludeColumn.Header = _allBox;
        SourceBox.ItemsSource = Sources;
        SourceBox.SelectedIndex = 0;
        FolderBox.Text = Strings.SessionImportDefaultFolder;
        _folderTimer.Tick += (_, _) => ApplyFolder();
        Closed += (_, _) => _folderTimer.Stop();
    }

    /// <summary>Source proposée dans la liste.</summary>
    internal sealed record SourceChoice(ImportSourceKind Kind, string Label);

    /// <summary>Sources, dans la langue de l'interface au moment de l'ouverture.</summary>
    internal static IReadOnlyList<SourceChoice> Sources =>
    [
        new(ImportSourceKind.PuttyRegistry, Strings.SourcePuttyRegistry),
        new(ImportSourceKind.KittyRegistry, Strings.SourceKittyRegistry),
        new(ImportSourceKind.KittyFolder, Strings.SourceKittyFolder),
        new(ImportSourceKind.WinScpRegistry, Strings.SourceWinScpRegistry),
        new(ImportSourceKind.WinScpIni, Strings.SourceWinScpIni),
        new(ImportSourceKind.RegFile, Strings.SourceRegFile),
        new(ImportSourceKind.MxtSessions, Strings.SourceMxtSessions),
        new(ImportSourceKind.MRemoteNg, Strings.SourceMRemoteNg),
        new(ImportSourceKind.RdcMan, Strings.SourceRdcMan),
        new(ImportSourceKind.SecureCrtFolder, Strings.SourceSecureCrtFolder),
        new(ImportSourceKind.SecureCrtXml, Strings.SourceSecureCrtXml),
        new(ImportSourceKind.OpenSsh, Strings.SourceOpenSsh),
        new(ImportSourceKind.RdpFolder, Strings.SourceRdpFolder),
    ];

    internal SessionImport? Import => _import;

    internal IReadOnlyList<SessionImportRow> Rows => _rows;

    private SourceChoice Selected => (SourceChoice)SourceBox.SelectedItem;

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        var kind = Selected.Kind;
        SourceHint.Text = SessionSources.IsRegistry(kind) ? Strings.SessionImportHintRegistry
            : SessionSources.IsFolder(kind) ? Strings.SessionImportHintFolder
            : Strings.SessionImportHintFile;
    }

    private async void OnRead(object sender, RoutedEventArgs e)
    {
        if (_reading)
        {
            return;
        }

        var choice = Selected;
        string? path = null;
        if (!SessionSources.IsRegistry(choice.Kind))
        {
            path = Pick(choice);
            if (path is null)
            {
                return;
            }
        }

        _reading = true;
        ReadButton.IsEnabled = ImportButton.IsEnabled = false;
        ErrorMessage.Visibility = Visibility.Collapsed;
        StatusText.Text = Strings.SessionImportReading;
        try
        {
            var sessions = await Task.Run(() => path is null ? RegistrySessions.Read(choice.Kind) : SessionSources.Read(choice.Kind, path));
            Load(sessions, path ?? choice.Label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Security.SecurityException or ArgumentException)
        {
            StatusText.Text = "";
            ErrorMessage.Text = Text.Format(Strings.SessionImportReadFailed, path ?? choice.Label, ex.Message);
            ErrorMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            _reading = false;
            ReadButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    /// <summary>Fichier ou dossier à lire, dans le dossier habituel du logiciel s'il existe.</summary>
    private string? Pick(SourceChoice choice)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var title = choice.Label;
        if (SessionSources.IsFolder(choice.Kind))
        {
            var folder = new OpenFolderDialog { Title = title };
            if (choice.Kind == ImportSourceKind.SecureCrtFolder && Directory.Exists(Path.Combine(appData, "VanDyke", "Config")))
            {
                folder.InitialDirectory = Path.Combine(appData, "VanDyke", "Config");
            }

            return folder.ShowDialog(this) == true ? folder.FolderName : null;
        }

        var (pattern, directory) = choice.Kind switch
        {
            ImportSourceKind.RegFile => ("*.reg", null),
            ImportSourceKind.WinScpIni => ("*.ini", appData),
            ImportSourceKind.MxtSessions => ("*.mxtsessions;*.ini", null),
            ImportSourceKind.MRemoteNg => ("*.xml", Path.Combine(appData, "mRemoteNG")),
            ImportSourceKind.RdcMan => ("*.rdg", null),
            ImportSourceKind.SecureCrtXml => ("*.xml", null),
            _ => ("", Path.Combine(profile, ".ssh")),
        };
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = (pattern.Length > 0 ? $"{choice.Label} ({pattern})|{pattern}|" : "") + Strings.SessionImportAllFiles + " (*.*)|*.*",
            CheckFileExists = true,
        };
        if (directory is not null && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (choice.Kind == ImportSourceKind.OpenSsh && File.Exists(Path.Combine(profile, ".ssh", "config")))
        {
            dialog.FileName = "config";
        }

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>Sessions lues : rapprochement avec les comptes du PVWA et aperçu.</summary>
    internal void Load(IReadOnlyList<ImportedSession> sessions, string source)
    {
        _source = source;
        _import = new SessionImport(_settings, _pvwaHost, _matcher, sessions, FolderBox.Text);
        _folderTimer.Stop();
        List<TagChoice> tags =
        [
            .. _settings.ServerTags.Select(t => new TagChoice(t.Name, t.Name, ServerTagView.Background(t), ServerTagView.Foreground(t))),
            new TagChoice("", Strings.TagNoneItem, null, null),
        ];
        _rows = _import.Items.Select(i => new SessionImportRow(_import, i, OnRowIncludeChanged, OnRowChosen, tags)).ToList();
        RowsGrid.ItemsSource = _rows;
        ApplyFilter();
        FolderBox.IsEnabled = true;
        StatusText.Text = sessions.Count == 0 ? "" : Text.Format(Strings.SessionImportReadFrom, sessions.Count, source);
        Refresh();
    }

    private void OnFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (_import is { Applied: false })
        {
            _folderTimer.Stop();
            _folderTimer.Start();
        }
    }

    /// <summary>Dossier saisi appliqué aux sessions (sans attendre la fin de la pause avant un import ou un export).</summary>
    private void ApplyFolder()
    {
        _folderTimer.Stop();
        if (_import is { Applied: false } && _import.RootFolder != SessionFolders.Normalize(FolderBox.Text))
        {
            _import.RootFolder = FolderBox.Text;
            Refresh();
        }
    }

    /// <summary>
    /// Case de l'en-tête : coche les lignes affichées, ou les décoche si elles le sont toutes. « Seulement les problèmes »
    /// cache des sessions prêtes : elles gardent leur choix.
    /// </summary>
    private void OnIncludeAll(object sender, RoutedEventArgs e)
    {
        var rows = VisibleRows().Where(r => r.CanInclude).ToList();
        bool include = !rows.All(r => r.Include);
        // Boutons mis à jour une fois à la fin, pas pour chacune des milliers de lignes.
        _includingAll = true;
        try
        {
            foreach (var row in rows)
            {
                row.Include = include;
            }
        }
        finally
        {
            _includingAll = false;
        }

        UpdateButtons();
    }

    /// <summary>Lignes affichées (filtre « Seulement les problèmes » compris).</summary>
    private IEnumerable<SessionImportRow> VisibleRows() => CollectionViewSource.GetDefaultView(_rows).Cast<SessionImportRow>();

    /// <summary>
    /// Case de l'en-tête d'après les lignes affichées qui peuvent être cochées : cochée si toutes le sont, vide si aucune,
    /// entre les deux sinon (les sessions « à vérifier » ne sont pas cochées d'office ; un clic coche alors tout).
    /// </summary>
    private void UpdateAllBox()
    {
        if (_import is not { Applied: false })
        {
            // Avant la lecture, ou import fait : plus rien à cocher, la case reste telle quelle.
            _allBox.IsEnabled = false;
            return;
        }

        var rows = VisibleRows().Where(r => r.CanInclude).ToList();
        int included = rows.Count(r => r.Include);
        _allBox.IsChecked = included == 0 ? false : included == rows.Count ? true : null;
        _allBox.IsEnabled = rows.Count > 0;
    }

    /// <summary>Une session cochée ou décochée : seuls les boutons et la case de l'en-tête en dépendent (pas les états ni le bilan).</summary>
    private void OnRowIncludeChanged()
    {
        if (!_includingAll)
        {
            UpdateButtons();
        }
    }

    /// <summary>Autre compte choisi : seule cette session change d'état, puis le bilan.</summary>
    private void OnRowChosen(SessionImportRow row)
    {
        row.Refresh();
        UpdateSummary();
        UpdateButtons();
    }

    private void OnOnlyProblems(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>« Seulement les problèmes » : sessions non importées ou à vérifier.</summary>
    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.Filter = ProblemsBox.IsChecked == true
            ? row => ((SessionImportRow)row).State is not (ImportState.Ready or ImportState.Imported)
            : null;
        UpdateAllBox();
    }

    /// <summary>Ajoute à « Mes serveurs » les sessions cochées et prêtes, puis affiche le résultat de chacune.</summary>
    internal void OnImport(object sender, RoutedEventArgs e)
    {
        if (_import is null || _import.Applied)
        {
            return;
        }

        ApplyFolder();
        _import.Apply();
        _imported(_import);
        FolderBox.IsEnabled = false;
        Refresh();
        ApplyFilter();
        StatusText.Text = Strings.SessionImportExportHint;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.SessionImportExport.Replace("_", ""),
            Filter = Strings.ExportFilter,
            FileName = $"{Strings.SessionImportFileName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            WriteResult(dialog.FileName);
            ErrorMessage.Visibility = Visibility.Collapsed;
            StatusText.Text = Text.Format(Strings.SessionImportExported, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage.Text = Text.Format(Strings.ExportFailed, ex.Message);
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Serveurs sans compte dans le PVWA : fichier au format de « Importer des comptes (CSV) », à compléter puis à
    /// importer pour créer leurs comptes dans un safe.
    /// </summary>
    private void OnMissingAccounts(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.SessionImportMissingAccounts.Replace("_", ""),
            Filter = Strings.ExportFilter,
            FileName = $"{Strings.SessionImportMissingAccountsFileName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            int count = WriteMissingAccounts(dialog.FileName);
            ErrorMessage.Visibility = Visibility.Collapsed;
            StatusText.Text = Text.Format(Strings.SessionImportMissingAccountsSaved, count, dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage.Text = Text.Format(Strings.ExportFailed, ex.Message);
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Comptes à créer en CSV (BOM UTF-8 pour Excel, séparateur de la région Windows) ; renvoie leur nombre.</summary>
    internal int WriteMissingAccounts(string path)
    {
        ApplyFolder();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return _import?.WriteMissingAccounts(writer, CsvExporter.DefaultSeparator(CultureInfo.CurrentCulture)) ?? 0;
    }

    /// <summary>Résultat de chaque session en CSV (BOM UTF-8 pour Excel, séparateur de la région Windows).</summary>
    internal void WriteResult(string path)
    {
        ApplyFolder();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _import?.WriteCsv(writer, CsvExporter.DefaultSeparator(CultureInfo.CurrentCulture));
    }

    /// <summary>États, bilan et boutons à jour (après lecture, changement de dossier ou de compte, import).</summary>
    private void Refresh()
    {
        foreach (var row in _rows)
        {
            row.Refresh();
        }

        UpdateSummary();
        UpdateButtons();
    }

    private void UpdateSummary()
    {
        if (_import is null)
        {
            return;
        }

        SummaryText.Text = _import.Items.Count == 0 ? Text.Format(Strings.SessionImportNone, _source)
            : _import.Applied ? Text.Format(Strings.SessionImportResult, _import.Count(ImportState.Imported),
                _import.Items.Count - _import.Count(ImportState.Imported))
            : Text.Format(Strings.SessionImportSummary, _import.Count(ImportState.Ready), _import.Count(ImportState.Check),
                _import.Count(ImportState.NoAccount), _import.Count(ImportState.Unsupported), _import.Count(ImportState.AlreadyPresent));
    }

    private void UpdateButtons()
    {
        bool pending = _import is { Applied: false };
        int selected = pending ? _import!.Items.Count(i => i.CanImport && i.Include) : 0;
        ImportButton.IsEnabled = !_reading && selected > 0;
        // Nombre de sessions qui seront ajoutées : celles « à vérifier » ne sont pas cochées d'office.
        ImportButton.Content = selected > 0 ? Text.Format(Strings.SessionImportButtonCount, selected) : Strings.SessionImportButton;
        ExportButton.IsEnabled = !_reading && _import is { Items.Count: > 0 };
        MissingAccountsButton.IsEnabled = !_reading && _import is { MissingAccounts: > 0 };
        ProblemsBox.IsEnabled = _import is { Items.Count: > 0 };
        UpdateAllBox();
    }

    /// <summary>Étiquette proposée dans la colonne « Étiquette » (<see cref="Value"/> vide : aucune).</summary>
    internal sealed record TagChoice(string Value, string Text, System.Windows.Media.Brush? Background, System.Windows.Media.Brush? Foreground);

    /// <summary>Ligne de l'aperçu, puis du résultat.</summary>
    internal sealed class SessionImportRow(SessionImport import, ImportItem item, Action includeChanged, Action<SessionImportRow> chosen,
        IReadOnlyList<TagChoice> tagChoices)
        : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ImportItem Item => item;

        /// <summary>Coché : importé, ou à importer. Une session non importable n'est jamais cochée.</summary>
        public bool Include
        {
            get => item.Include && (item.CanImport || item.State == ImportState.Imported);
            set
            {
                if (item.Include != value)
                {
                    item.Include = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
                    includeChanged();
                }
            }
        }

        public bool CanInclude => item.CanImport && !import.Applied;

        public string Folder => item.Session.Folder;

        public string Name => item.Session.Name;

        public string Protocol => item.ProtocolText;

        public string Server => item.ServerText;

        public string User => item.Session.DisplayUser;

        public IReadOnlyList<ImportCandidate> Candidates => item.Candidates;

        /// <summary>Compte choisi ; un autre peut l'être tant que l'import n'a pas eu lieu.</summary>
        public ImportCandidate? Chosen
        {
            get => item.Chosen;
            set
            {
                if (value is not null && !ReferenceEquals(value, item.Chosen))
                {
                    import.Choose(item, value);
                    chosen(this);
                }
            }
        }

        public bool CanChoose => item.Candidates.Count > 1 && !import.Applied
                                 && item.State is not (ImportState.NoAccount or ImportState.Unsupported);

        public bool ShowsAccountText => !CanChoose;

        public string Account => item.State is ImportState.NoAccount or ImportState.Unsupported ? "" : item.Chosen?.Display ?? "";

        public string Connection => item.ConnectionText;

        public IReadOnlyList<TagChoice> TagChoices => tagChoices;

        /// <summary>Étiquette du serveur importé (vide : aucune) ; devinée, puis choisie si on la change.</summary>
        public string Tag
        {
            get => item.Tag ?? "";
            set
            {
                if (!string.Equals(value ?? "", item.Tag ?? "", StringComparison.Ordinal))
                {
                    import.SetTag(item, string.IsNullOrEmpty(value) ? null : value);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tag)));
                }
            }
        }

        /// <summary>Nom accessible de la liste des étiquettes : la session et son étiquette.</summary>
        public string TagName => item.Tag is { } tag ? $"{Name}, {tag}" : Name;

        public bool CanTag => item.CanImport && !import.Applied;

        public ImportState State => item.State;

        public string Result => item.StateText;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
