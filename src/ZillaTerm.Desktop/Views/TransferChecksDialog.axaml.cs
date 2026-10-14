using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Vérification des fichiers d'un transfert : pour chacun, taille, somme SHA-256 sur ce poste et sur le serveur, et
/// résultat (identique, différent, non vérifié). Les sommes peuvent être copiées au format de <c>sha256sum -c</c>.
/// </summary>
public partial class TransferChecksDialog : DialogWindow
{
    private readonly IReadOnlyList<TransferCheck> _checks = [];

    public TransferChecksDialog() => InitializeComponent();

    public TransferChecksDialog(IReadOnlyList<TransferCheck> checks)
        : this()
    {
        _checks = checks;
        HeadingText.Text = Heading(checks);
        HeadingText[!TextBlock.ForegroundProperty] = HeadingText.GetResourceObservable(
            checks.Any(c => c.Failed || (c.Verified && !c.Matches)) ? "ErrorBrush"
            : checks.Any(c => c.Unverified || c.Skipped) ? "WarningBrush"
            : "SuccessBrush").ToBinding();
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
        var mono = this.TryFindResource("MonospaceFont", out var font) && font is FontFamily family ? family : FontFamily.Default;
        // Liaisons à sens unique (TransferHistoryDialog.Shown) : le DataGrid d'Avalonia lie ses colonnes dans les deux sens.
        // Sens en entier dans chaque langue (« ⬇ téléchargé »), pas seulement la largeur de son titre.
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColDirection,
            Binding = TransferHistoryDialog.Shown(nameof(TransferCheck.Upload), TransferHistoryDialog.DirectionConverter),
            Width = DataGridLength.Auto,
            MinWidth = TransferHistoryDialog.FitWidth(ChecksGrid, [Strings.ChecksColDirection, Strings.ChecksUploaded, Strings.ChecksDownloaded]),
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            // Chemin du serveur : caractères invisibles remplacés par « � ». À sa largeur (160 au moins) plutôt qu'en étoile
            // comme sous WPF : les sommes entières dépassent la largeur de la fenêtre, et avec une colonne en étoile le
            // DataGrid d'Avalonia ne fait plus défiler jusqu'à la dernière colonne (« Résultat »).
            Header = Strings.ChecksColFile,
            Binding = TransferHistoryDialog.Shown(nameof(TransferCheck.RemotePath), TransferHistoryDialog.VisibleConverter),
            Width = DataGridLength.Auto,
            MinWidth = 160,
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColSize,
            Binding = TransferHistoryDialog.Shown(".", new FuncValueConverter<TransferCheck, string>(Size)),
            // Tri par la taille elle-même, pas par son texte.
            SortMemberPath = nameof(TransferCheck.LocalLength),
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColLocal, Binding = TransferHistoryDialog.Shown(nameof(TransferCheck.LocalHash)), FontFamily = mono,
        });
        ChecksGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.ChecksColRemote, Binding = TransferHistoryDialog.Shown(nameof(TransferCheck.RemoteHash)), FontFamily = mono,
        });
        ChecksGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Strings.ChecksColResult,
            CanUserSort = false,
            CellTemplate = new FuncDataTemplate<TransferCheck>((check, _) =>
            {
                var text = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Classes = { "ResultText" } };
                if (check is not null)
                {
                    text.Text = Result(check);
                    text.Classes.Set("Identical", check.Matches);
                    text.Classes.Set("Different", !check.Matches && check.Verified);
                }

                return text;
            }),
        });
    }

    private static string Size(TransferCheck? check) =>
        check is null ? "" : RemotePath.FormatSize(Math.Max(check.LocalLength, check.RemoteLength));

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        // Somme de l'original seulement pour les fichiers vérifiés ; une ligne par fichier, fin de ligne Unix.
        var lines = _checks.Where(c => c.Verified).Select(c => c.ToSha256SumLine()).ToList();
        try
        {
            if (Clipboard is not { } clipboard)
            {
                CopiedText.Text = Strings.ClipboardBusy;
                return;
            }

            await clipboard.SetTextAsync(string.Join("\n", lines) + "\n");
            CopiedText.Text = Text.Format(Strings.ChecksCopied, lines.Count);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or TimeoutException)
        {
            CopiedText.Text = Strings.ClipboardBusy;
        }
    }
}
