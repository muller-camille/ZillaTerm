using System.IO;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Ssh;
using Microsoft.Win32;

namespace ZillaTerm.App.Views;

/// <summary>
/// Avec quoi comparer un fichier du serveur : le même chemin (ou un autre) sur un serveur dont une session SSH est
/// ouverte, ou un fichier de ce poste.
/// </summary>
public partial class CompareDialog : Window
{
    private readonly RemoteSession _current;
    private readonly string _path;

    /// <param name="label">Fichier comparé, par ex. « root@srv01 : /etc/app.conf ».</param>
    /// <param name="sessions">Sessions SSH ouvertes ; la première autre que <paramref name="current"/> est proposée.</param>
    public CompareDialog(string label, string path, IReadOnlyList<RemoteSession> sessions, RemoteSession current)
    {
        InitializeComponent();
        _current = current;
        _path = path;
        // Chemin venant du serveur : caractères invisibles remplacés par « � ».
        IntroText.Text = Text.Format(Strings.CompareIntro, RemoteEntry.Visible(label));
        var connected = sessions.Where(s => s.State == RemoteSessionState.Connected).ToList();
        SessionBox.ItemsSource = connected;
        var other = connected.FirstOrDefault(s => !ReferenceEquals(s, current));
        SessionBox.SelectedItem = other ?? current;
        RemotePathBox.Text = path;
        // Aucun autre serveur ouvert : le même chemin donnerait le même fichier, un fichier de ce poste est proposé.
        if (other is null)
        {
            LocalRadio.IsChecked = true;
        }

        SessionBox.SelectionChanged += (_, _) => UpdateSameFile();
        RemotePathBox.TextChanged += (_, _) => UpdateSameFile();
        ServerRadio.Checked += (_, _) => UpdateSameFile();
        LocalRadio.Checked += (_, _) => UpdateSameFile();
        UpdateSameFile();
        Loaded += (_, _) => (other is null ? LocalPathBox : (UIElement)SessionBox).Focus();
    }

    /// <summary>Même serveur et même chemin que le fichier comparé : rien à comparer.</summary>
    private bool SameFile => ServerRadio.IsChecked == true && ReferenceEquals(Session, _current) && RemoteFile == _path;

    /// <summary>Le même fichier des deux côtés est signalé tout de suite, et « Comparer » grisé.</summary>
    private void UpdateSameFile()
    {
        bool same = SameFile;
        CompareButton.IsEnabled = !same;
        if (same)
        {
            ShowError(Strings.CompareSameFile);
        }
        else if (ErrorText.Text == Strings.CompareSameFile)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Serveur choisi (null pour un fichier de ce poste).</summary>
    public RemoteSession? Session => ServerRadio.IsChecked == true ? SessionBox.SelectedItem as RemoteSession : null;

    public string RemoteFile => RemotePathBox.Text.Trim();

    public string LocalFile => LocalPathBox.Text.Trim().Trim('"');

    /// <summary>
    /// Explorateur du serveur choisi, ouvert sur le chemin saisi (le même par défaut) : le fichier choisi remplace le
    /// chemin. La connexion SFTP du serveur est ouverte si son onglet Fichiers ne l'a pas encore fait.
    /// </summary>
    private async void OnBrowseServer(object sender, RoutedEventArgs e)
    {
        if (SessionBox.SelectedItem is not RemoteSession session)
        {
            ShowError(Strings.CompareNoServer);
            return;
        }

        IRemoteFiles browser;
        BrowseServerButton.IsEnabled = false;
        try
        {
            browser = await session.GetBrowserAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.SftpFailed, global::ZillaTerm.App.ErrorText.Describe(ex)));
            return;
        }
        finally
        {
            BrowseServerButton.IsEnabled = true;
        }

        if (!IsLoaded)
        {
            return;
        }

        var picker = new RemoteFileDialog(session.Label, RemotePathBox.Text, browser.HomeDirectory, browser.BrowseAsync) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPath is { } path)
        {
            RemotePathBox.Text = path;
            ServerRadio.IsChecked = true;
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Strings.CompareLocal.Replace("_", "") };
        if (dialog.ShowDialog(this) == true)
        {
            LocalPathBox.Text = dialog.FileName;
            LocalRadio.IsChecked = true;
        }
    }

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        string? error = ServerRadio.IsChecked == true
            ? Session is null ? Strings.CompareNoServer : RemoteFile.Length == 0 ? Strings.CompareNoPath : SameFile ? Strings.CompareSameFile : null
            : !File.Exists(LocalFile) ? Strings.CompareNoLocalFile : null;
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        DialogResult = true;
    }
}
