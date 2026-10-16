using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>Avancement d'un transfert : fichier en cours (nom, rang), octets transférés depuis le début.</summary>
public readonly record struct TransferStep(string Name, int Index, long Bytes);

/// <summary>
/// Progression d'un transfert qui doit se terminer avant de continuer (téléchargement au dépôt dans l'Explorateur) :
/// la fenêtre reste ouverte pendant le travail, qu'« Annuler » interrompt.
/// </summary>
public partial class TransferDialog : Window
{
    private readonly Func<IProgress<TransferStep>, CancellationToken, Task> _work;
    private readonly CancelOnce _cancel = new();
    private readonly long _totalBytes;
    private readonly int _totalFiles;
    private bool _closed;

    public TransferDialog(string title, int totalFiles, long totalBytes, Func<IProgress<TransferStep>, CancellationToken, Task> work)
    {
        InitializeComponent();
        Title = title;
        _totalFiles = totalFiles;
        _totalBytes = totalBytes;
        _work = work;
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>Erreur du transfert, ou null (réussi ou annulé).</summary>
    public Exception? Error { get; private set; }

    private async Task RunAsync()
    {
        var progress = new Progress<TransferStep>(step =>
        {
            ItemText.Text = Text.Format(Strings.Downloading, step.Name);
            Bar.Value = _totalBytes > 0 ? 100.0 * step.Bytes / _totalBytes : 100.0 * step.Index / Math.Max(_totalFiles, 1);
            SummaryText.Text = $"{step.Index + 1} / {_totalFiles} · {RemotePath.FormatSize(step.Bytes)} / {RemotePath.FormatSize(_totalBytes)}";
        });
        bool ok = false;
        try
        {
            await _work(progress, _cancel.Token);
            ok = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex;
        }

        // Fenêtre déjà fermée (Annuler, Échap, croix) : le résultat est « annulé ».
        if (!_closed)
        {
            DialogResult = ok;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel.Cancel();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cancel.Cancel();
        base.OnClosed(e);
    }
}
