using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Historique des transferts de l'onglet Fichiers : date, sens, serveur, élément, destination, nombre de fichiers et
/// résultat, filtrable par sens ou réduit aux échecs et transferts non vérifiés. Pour un transfert : ses sommes de
/// contrôle, et le dossier d'un téléchargement.
/// </summary>
public partial class TransferHistoryDialog : DialogWindow
{
    /// <summary>Choix du filtre « Échecs et non vérifiés ».</summary>
    internal const int ProblemsFilter = 3;

    /// <summary>Texte venant du serveur, caractères invisibles remplacés par « � » (voir <see cref="RemoteEntry.DisplayName"/>).</summary>
    internal static readonly IValueConverter VisibleConverter =
        new FuncValueConverter<string?, string>(text => text is null ? "" : RemoteEntry.Visible(text));

    /// <summary>Sens d'un transfert : « ⬆ envoyé » ou « ⬇ téléchargé ».</summary>
    internal static readonly IValueConverter DirectionConverter =
        new FuncValueConverter<bool, string>(upload => upload ? Strings.ChecksUploaded : Strings.ChecksDownloaded);

    private readonly TransferHistory _history = new();
    private readonly Action _save = () => { };

    public TransferHistoryDialog() => InitializeComponent();

    /// <param name="save">Enregistre l'historique (après « Effacer »).</param>
    /// <param name="problemsOnly">Ouvert pour un échec signalé : seulement les échecs et transferts non vérifiés.</param>
    public TransferHistoryDialog(TransferHistory history, Action save, bool problemsOnly = false)
        : this()
    {
        _history = history;
        _save = save;
        BuildColumns();
        if (problemsOnly)
        {
            FilterBox.SelectedIndex = ProblemsFilter;
        }

        // Branché après le chargement : la sélection initiale du filtre ne doit pas remplir une grille sans colonnes.
        FilterBox.SelectionChanged += (_, _) => ShowRecords();
        RecordsGrid.SelectionChanged += (_, _) => UpdateButtons();
        RecordsGrid.LoadingRow += (_, e) => e.Row.Classes.Set("Problem", e.Row.DataContext is TransferRecord record && HasProblem(record));
        ShowRecords();
    }

    /// <summary>Texte de la colonne « Résultat » (le même que dans la file des transferts).</summary>
    public static string Result(TransferRecord record) => record.State switch
    {
        TransferState.Done when record.Different > 0 => Text.Format(Strings.QueueStateDifferent, record.Different),
        TransferState.Done when record.Files.Count(f => f.Unverified) is > 0 and var unverified =>
            Text.Format(Strings.QueueStateUnverified, unverified, Transferred(record)),
        TransferState.Done when Transferred(record) > 0 => Text.Format(Strings.QueueStateVerified, record.Identical, Transferred(record)),
        TransferState.Done => Strings.QueueStateDone,
        TransferState.Failed => "✗ " + record.Error,
        _ => Strings.QueueStateCancelled,
    };

    /// <summary>Transfert en échec ou fichier différent de l'original : ligne en rouge.</summary>
    public static bool HasProblem(TransferRecord record) => record.State == TransferState.Failed || record.Different > 0;

    /// <summary>Filtre « Échecs et non vérifiés » : en échec, fichier différent, ou fichier transféré mais pas relu.</summary>
    public static bool NeedsReview(TransferRecord record) =>
        HasProblem(record) || (record.State == TransferState.Done && record.Files.Any(f => f.Unverified));

    /// <summary>Fichiers transférés (liens vers des dossiers, non suivis, à part).</summary>
    private static int Transferred(TransferRecord record) => record.Files.Count(f => !f.Skipped);

    private void BuildColumns()
    {
        // Texte tronqué (« … ») et complet dans l'infobulle (style de la fenêtre) : serveur, élément et destination peuvent
        // être longs. Colonne étroite : assez large pour ses textes possibles (texts), en entier dans chaque langue.
        void Add(string header, BindingBase binding, double width = 1, DataGridLengthUnitType unit = DataGridLengthUnitType.Star,
            string? sortBy = null, params string[] texts)
        {
            var column = new DataGridTextColumn { Header = header, Binding = binding, Width = new DataGridLength(width, unit) };
            if (sortBy is not null)
            {
                column.SortMemberPath = sortBy;
            }

            if (texts.Length > 0)
            {
                column.MinWidth = FitWidth(RecordsGrid, texts.Append(header));
            }

            RecordsGrid.Columns.Add(column);
        }

        // Date la plus large possible dans le format de la langue (jour et mois à deux chiffres, heure de l'après-midi).
        var widestTime = new DateTime(2026, 12, 28, 22, 58, 0).ToString("g", CultureInfo.CurrentCulture);
        Add(Strings.HistoryColTime, Shown(nameof(TransferRecord.Time),
                new FuncValueConverter<DateTime, string>(time => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))),
            1, DataGridLengthUnitType.Auto, null, widestTime);
        Add(Strings.ChecksColDirection, Shown(nameof(TransferRecord.Upload), DirectionConverter), 1, DataGridLengthUnitType.Auto, null,
            Strings.ChecksUploaded, Strings.ChecksDownloaded);
        Add(Strings.HistoryColServer, Shown(nameof(TransferRecord.Server)));
        // Noms et dossiers du serveur : caractères invisibles remplacés par « � » (historique ancien compris).
        Add(Strings.HistoryColItem, Shown(nameof(TransferRecord.Label), VisibleConverter), 1.2);
        Add(Strings.HistoryColDestination, Shown(nameof(TransferRecord.Destination), VisibleConverter), 1.5);
        Add(Strings.HistoryColFiles, Shown(nameof(TransferRecord.FileCount)), 1, DataGridLengthUnitType.Auto, null, "0000");
        // Téléchargements : toujours par la connexion SFTP de l'onglet.
        var protocol = Shown(nameof(TransferRecord.Protocol));
        protocol.TargetNullValue = "SFTP";
        Add(Strings.HistoryColProtocol, protocol, 1, DataGridLengthUnitType.Auto, null, "SFTP");
        // Tri du résultat : par état du transfert (son texte se calcule).
        Add(Strings.ChecksColResult, Shown(".", new FuncValueConverter<TransferRecord, string>(r => r is null ? "" : Result(r))), 1.5,
            DataGridLengthUnitType.Star, nameof(TransferRecord.State));
    }

    /// <summary>
    /// Liaison d'une colonne, à sens unique : le DataGrid d'Avalonia lie ses colonnes dans les deux sens par défaut, et
    /// réécrirait dans l'historique la valeur par défaut d'une colonne convertie (date, sens).
    /// </summary>
    internal static Binding Shown(string path, IValueConverter? converter = null) =>
        new(path) { Mode = BindingMode.OneWay, Converter = converter };

    /// <summary>
    /// Largeur d'une colonne étroite pour montrer en entier ses textes possibles (titre compris), dans la police de la
    /// grille, en gras (lignes en échec), avec la marge d'une cellule : lisible dans chacune des trois langues.
    /// </summary>
    internal static double FitWidth(DataGrid grid, IEnumerable<string> texts)
    {
        // Marges du texte d'une cellule et d'un en-tête (avec la place de la flèche de tri).
        const double CellPadding = 40;
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, FontWeight.SemiBold);
        return CellPadding + texts.Max(text => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface,
            grid.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace);
    }

    private void ShowRecords()
    {
        var records = FilterBox.SelectedIndex switch
        {
            1 => _history.Records.Where(r => r.Upload),
            2 => _history.Records.Where(r => !r.Upload),
            ProblemsFilter => _history.Records.Where(NeedsReview),
            _ => _history.Records,
        };
        var shown = records.ToList();
        RecordsGrid.ItemsSource = shown;
        EmptyText.Text = _history.Records.Count == 0 ? Strings.HistoryEmpty : Strings.HistoryNoMatch;
        EmptyText.IsVisible = shown.Count == 0;
        ClearButton.IsEnabled = _history.Records.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var record = RecordsGrid.SelectedItem as TransferRecord;
        ChecksButton.IsEnabled = record is { Files.Count: > 0 };
        OpenFolderButton.IsEnabled = record is { Upload: false } && Directory.Exists(record.Destination);
    }

    private async void OnChecks(object? sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is TransferRecord { Files.Count: > 0 } record)
        {
            await new TransferChecksDialog(record.Files).ShowDialogAsync(this);
        }
    }

    private void OnRowDoubleClick(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null)
        {
            OnChecks(sender, e);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        // Seulement un dossier existant, passé en argument au gestionnaire de fichiers (jamais exécuté comme un programme).
        if (RecordsGrid.SelectedItem is not TransferRecord { Upload: false } record || !Directory.Exists(record.Destination))
        {
            return;
        }

        try
        {
            OpenFolder(record.Destination);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            await ConfirmDialog.ShowMessageAsync(this, ex.Message, ConfirmKind.Warning, Strings.HistoryTitle);
        }
    }

    /// <summary>
    /// Ouvre le dossier dans le gestionnaire de fichiers du système : l'Explorateur du dossier de Windows (jamais un
    /// « explorer.exe » posé à côté de l'application), xdg-open sous Linux, open sous macOS. Le chemin, complet, sans
    /// caractère de contrôle, est un argument du programme (sans interpréteur de commandes).
    /// </summary>
    /// <exception cref="ArgumentException">Chemin relatif, ou contenant un guillemet ou un caractère de contrôle.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Le gestionnaire de fichiers n'a pas pu être lancé.</exception>
    internal static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Any(c => c == '"' || char.IsControl(c)) || !Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException($"Chemin refusé pour le gestionnaire de fichiers : {folder}", nameof(folder));
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            // L'Explorateur lit lui-même sa ligne de commande : le chemin lui est passé entre guillemets (sans séparateur
            // final, qui suivi d'un guillemet en ferait un caractère du chemin).
            start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{path}\"");
        }
        else
        {
            // Chemin complet : il commence par « / », jamais pris pour une option.
            start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open");
            start.ArgumentList.Add(path);
        }

        start.UseShellExecute = false;
        Process.Start(start)?.Dispose();
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.DestructiveAsync(this, Strings.HistoryTitle, Strings.HistoryClearHeading, Strings.HistoryClearAction,
                subject: Text.Format(Strings.HistoryClearCount, _history.Records.Count), message: Strings.HistoryClearMessage))
        {
            return;
        }

        _history.Clear();
        _save();
        ShowRecords();
    }
}
