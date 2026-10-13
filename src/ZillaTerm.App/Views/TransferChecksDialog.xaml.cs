using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Vérification des fichiers d'un transfert : pour chacun, taille, somme SHA-256 sur ce poste et sur le serveur, et
/// résultat (identique, différent, non vérifié). Les sommes peuvent être copiées au format de <c>sha256sum -c</c>.
/// </summary>
public partial class TransferChecksDialog : Window
{
    private readonly IReadOnlyList<TransferCheck> _checks;

    public TransferChecksDialog(IReadOnlyList<TransferCheck> checks)
    {
        InitializeComponent();
        _checks = checks;
        HeadingText.Text = Heading(checks);
        HeadingText.SetResourceReference(TextBlock.ForegroundProperty, checks.Any(c => c.Failed || (c.Verified && !c.Matches)) ? "ErrorBrush"
            : checks.Any(c => c.Unverified || c.Skipped) ? "WarningBrush"
            : "SuccessBrush");
        // Comment les sommes ont été obtenues, selon les sens présents.
        IntroText.Text = string.Join(Environment.NewLine, new[]
        {
            checks.Any(c => c.Upload) ? Strings.ChecksIntroUpload : null,
            checks.Any(c => !c.Upload) ? Strings.ChecksIntroDownload : null,
        }.OfType<string>());
        BuildColumns();
        // Les fichiers à revoir d'abord.
        ChecksGrid.ItemsSource = checks.OrderBy(Rank).ToList();
    }

    /// <summary>
    /// Résumé : tous identiques, ou combien diffèrent, sont en échec, interrompus ou non vérifiés ; et les liens vers des
    /// dossiers non suivis.
    /// </summary>
    public static string Heading(IReadOnlyList<TransferCheck> checks)
    {
        int Count(int rank) => checks.Count(c => Rank(c) == rank);
        int files = checks.Count - Count(5);
        var lines = new List<string>();
        void Add(int count, string format)
        {
            if (count > 0)
            {
                lines.Add(Text.Format(format, count, files));
            }
        }

        Add(Count(0), Strings.ChecksSomeDiffer);
        Add(Count(1), Strings.ChecksSomeFailed);
        Add(Count(2), Strings.ChecksSomeUnverified);
        Add(Count(3), Strings.ChecksSomeInterrupted);
        if (lines.Count == 0 && (files > 0 || Count(5) == 0))
        {
            lines.Add(Text.Format(Strings.ChecksAllOk, files));
        }

        Add(Count(5), Strings.ChecksSomeSkipped);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Texte de la colonne « Résultat ».</summary>
    public static string Result(TransferCheck check) =>
        check.Skipped ? Text.Format(Strings.ChecksSkipped, check.Error)
        : check.Interrupted ? Text.Format(Strings.ChecksInterrupted, check.Error)
        : check.Failed ? Text.Format(Strings.ChecksFailed, check.Error)
        : !check.Verified ? Text.Format(Strings.ChecksUnverified, check.Error)
        : check.Matches ? Strings.ChecksIdentical : Strings.ChecksDifferent;

    /// <summary>Ordre d'affichage : différents, en échec, non vérifiés, interrompus, identiques, liens non suivis.</summary>
    private static int Rank(TransferCheck check) =>
        check.Skipped ? 5 : check.Interrupted ? 3 : check.Failed ? 1 : !check.Verified ? 2 : check.Matches ? 4 : 0;

    private void BuildColumns()
    {
        var hash = (Style)FindResource("HashText");
        // Sens en entier dans chaque langue (« ⬇ téléchargé »), pas seulement la largeur de son titre.
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColDirection, Binding = new Binding(nameof(TransferCheck.Upload)) { Converter = new DirectionConverter() },
            Width = DataGridLength.Auto,
            MinWidth = TransferHistoryDialog.FitWidth(ChecksGrid, [Strings.ChecksColDirection, Strings.ChecksUploaded, Strings.ChecksDownloaded]),
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            // Chemin du serveur : caractères invisibles remplacés par « � ».
            Header = Strings.ChecksColFile,
            Binding = new Binding(nameof(TransferCheck.RemotePath)) { Converter = new TransferHistoryDialog.VisibleConverter() },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 160,
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColSize, Binding = new Binding(".") { Converter = new SizeConverter() },
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn { Header = Strings.ChecksColLocal, Binding = new Binding(nameof(TransferCheck.LocalHash)), ElementStyle = hash });
        ChecksGrid.Columns.Add(new DataGridTextColumn { Header = Strings.ChecksColRemote, Binding = new Binding(nameof(TransferCheck.RemoteHash)), ElementStyle = hash });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColResult, Binding = new Binding(".") { Converter = new ResultConverter() }, ElementStyle = (Style)FindResource("ResultText"),
        });
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        // Somme de l'original seulement pour les fichiers vérifiés ; une ligne par fichier, fin de ligne Unix.
        var lines = _checks.Where(c => c.Verified).Select(c => c.ToSha256SumLine()).ToList();
        try
        {
            Clipboard.SetText(string.Join("\n", lines) + "\n");
            CopiedText.Text = Text.Format(Strings.ChecksCopied, lines.Count);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            CopiedText.Text = Strings.ClipboardBusy;
        }
    }

    private sealed class DirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is true ? Strings.ChecksUploaded : Strings.ChecksDownloaded;

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class SizeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is TransferCheck check ? RemotePath.FormatSize(Math.Max(check.LocalLength, check.RemoteLength)) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class ResultConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is TransferCheck check ? Result(check) : "";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
