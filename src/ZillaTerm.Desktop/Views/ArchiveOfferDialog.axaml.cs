using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Dépôt d'un grand nombre de fichiers : propose de les envoyer dans une seule archive .tar.gz (.tar si gzip manque sur
/// le serveur), ou un par un. Fermée sans choisir : rien n'est envoyé.
/// </summary>
public partial class ArchiveOfferDialog : DialogWindow
{
    public ArchiveOfferDialog() => InitializeComponent();

    public ArchiveOfferDialog(int files, long bytes, string destination, bool compressed = true)
        : this()
    {
        MessageText.Text = Text.Format(Strings.ArchiveOfferText, files, RemotePath.FormatSize(bytes), destination, compressed ? ".tar.gz" : ".tar")
            + (compressed ? "" : "\n\n" + Strings.ArchiveOfferNoGzip);
        ArchiveButton.Content = Text.Format(Strings.ArchiveOfferArchive, compressed ? "._tar.gz" : "._tar");
    }

    /// <summary>Envoyer une archive (sinon les fichiers un par un).</summary>
    public bool UseArchive { get; private set; }

    /// <summary>Ne plus proposer l'archive (option des Paramètres décochée).</summary>
    public bool DontOfferAgain => NeverBox.IsChecked == true;

    private void OnArchive(object? sender, RoutedEventArgs e)
    {
        UseArchive = true;
        DialogResult = true;
    }

    private void OnFiles(object? sender, RoutedEventArgs e) => DialogResult = true;
}
