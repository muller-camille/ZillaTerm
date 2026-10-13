using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Historique des transferts de l'onglet Fichiers : date, sens, serveur, élément, destination, nombre de fichiers et
/// résultat, filtrable par sens ou réduit aux échecs et transferts non vérifiés. Pour un transfert : ses sommes de
/// contrôle, et le dossier d'un téléchargement.
/// </summary>
public partial class TransferHistoryDialog : Window
{
    /// <summary>Choix du filtre « Échecs et non vérifiés ».</summary>
    internal const int ProblemsFilter = 3;

    private readonly TransferHistory _history;
    private readonly Action _save;

    /// <param name="save">Enregistre l'historique (après « Effacer »).</param>
    /// <param name="problemsOnly">Ouvert pour un échec signalé : seulement les échecs et transferts non vérifiés.</param>
    public TransferHistoryDialog(TransferHistory history, Action save, bool problemsOnly = false)
    {
        InitializeComponent();
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
        // Texte tronqué (« … ») et complet dans l'infobulle : serveur, élément et destination peuvent être longs. Colonne
        // étroite : assez large pour ses textes possibles (texts), en entier dans chaque langue.
        void Add(string header, Binding binding, double width = 1, DataGridLengthUnitType unit = DataGridLengthUnitType.Star,
            params string[] texts)
        {
            var cell = new Style(typeof(TextBlock));
            cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            cell.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { RelativeSource = RelativeSource.Self }));
            var column = new DataGridTextColumn { Header = header, Binding = binding, Width = new DataGridLength(width, unit), ElementStyle = cell };
            if (texts.Length > 0)
            {
                column.MinWidth = FitWidth(RecordsGrid, texts.Append(header));
            }

            RecordsGrid.Columns.Add(column);
        }

        var row = new Style(typeof(DataGridRow));
        var problem = new DataTrigger { Binding = new Binding(".") { Converter = new ProblemConverter() }, Value = true };
        problem.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("ErrorBrush")));
        problem.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        row.Triggers.Add(problem);
        RecordsGrid.RowStyle = row;

        // Date la plus large possible dans le format de la langue (jour et mois à deux chiffres, heure de l'après-midi).
        var widestTime = new DateTime(2026, 12, 28, 22, 58, 0).ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        Add(Strings.HistoryColTime, new Binding(nameof(TransferRecord.Time)) { Converter = new LocalTimeConverter() }, 1, DataGridLengthUnitType.Auto,
            widestTime);
        Add(Strings.ChecksColDirection, new Binding(nameof(TransferRecord.Upload)) { Converter = new DirectionConverter() }, 1, DataGridLengthUnitType.Auto,
            Strings.ChecksUploaded, Strings.ChecksDownloaded);
        Add(Strings.HistoryColServer, new Binding(nameof(TransferRecord.Server)));
        // Noms et dossiers du serveur : caractères invisibles remplacés par « � » (historique ancien compris).
        Add(Strings.HistoryColItem, new Binding(nameof(TransferRecord.Label)) { Converter = new VisibleConverter() }, 1.2);
        Add(Strings.HistoryColDestination, new Binding(nameof(TransferRecord.Destination)) { Converter = new VisibleConverter() }, 1.5);
        Add(Strings.HistoryColFiles, new Binding(nameof(TransferRecord.FileCount)), 1, DataGridLengthUnitType.Auto, "0000");
        // Téléchargements : toujours par la connexion SFTP de l'onglet.
        Add(Strings.HistoryColProtocol, new Binding(nameof(TransferRecord.Protocol)) { TargetNullValue = "SFTP" }, 1, DataGridLengthUnitType.Auto,
            "SFTP");
        Add(Strings.ChecksColResult, new Binding(".") { Converter = new ResultConverter() }, 1.5);
    }

    /// <summary>
    /// Largeur d'une colonne étroite pour montrer en entier ses textes possibles (titre compris), dans la police de la
    /// grille, en gras (lignes en échec), avec la marge d'une cellule : lisible dans chacune des trois langues.
    /// </summary>
    internal static double FitWidth(DataGrid grid, IEnumerable<string> texts)
    {
        const double CellPadding = 16;
        var typeface = new System.Windows.Media.Typeface(grid.FontFamily, grid.FontStyle, FontWeights.SemiBold, grid.FontStretch);
        double pixelsPerDip = System.Windows.Media.VisualTreeHelper.GetDpi(grid).PixelsPerDip;
        return CellPadding + texts.Max(text => new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, grid.FontSize, System.Windows.Media.Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace);
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
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = _history.Records.Count > 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var record = RecordsGrid.SelectedItem as TransferRecord;
        ChecksButton.IsEnabled = record is { Files.Count: > 0 };
        OpenFolderButton.IsEnabled = record is { Upload: false } && Directory.Exists(record.Destination);
    }

    private void OnChecks(object sender, RoutedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is TransferRecord { Files.Count: > 0 } record)
        {
            new TransferChecksDialog(record.Files) { Owner = this }.ShowDialog();
        }
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RecordsGrid, (DependencyObject)e.OriginalSource) is DataGridRow)
        {
            OnChecks(sender, e);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        // Seulement un dossier existant, passé en argument à l'Explorateur (jamais exécuté comme un programme).
        if (RecordsGrid.SelectedItem is not TransferRecord { Upload: false } record || !Directory.Exists(record.Destination))
        {
            return;
        }

        try
        {
            Services.WindowsExplorer.OpenFolder(record.Destination);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, Strings.HistoryTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Destructive(this, Strings.HistoryTitle, Strings.HistoryClearHeading, Strings.HistoryClearAction,
                subject: Text.Format(Strings.HistoryClearCount, _history.Records.Count), message: Strings.HistoryClearMessage))
        {
            return;
        }

        _history.Clear();
        _save();
        ShowRecords();
    }

    private sealed class LocalTimeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is DateTime time ? time.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Texte venant du serveur, caractères invisibles remplacés par « � » (voir <see cref="RemoteEntry.DisplayName"/>).</summary>
    internal sealed class VisibleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is string text ? RemoteEntry.Visible(text) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class DirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is true ? Strings.ChecksUploaded : Strings.ChecksDownloaded;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ProblemConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is TransferRecord record && HasProblem(record);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ResultConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is TransferRecord record ? Result(record) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
