using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Views;

/// <summary>Ajout d'un coffre KeePass dans « Mes serveurs », ou modification de son emplacement.</summary>
public partial class KeePassFolderDialog : DialogWindow
{
    private readonly KeePassFolder _folder = new();

    public KeePassFolderDialog() => InitializeComponent();

    public KeePassFolderDialog(KeePassFolder folder)
        : this()
    {
        _folder = folder;
        FileBox.Text = folder.FilePath;
        NameBox.Text = folder.Name;
        KeyFileBox.Text = folder.KeyFilePath ?? "";
        UsesPasswordBox.IsChecked = folder.UsesPassword;
        HeadingText.Text = folder.FilePath.Length == 0 ? Strings.KeePassAddHeading : Strings.KeePassEditHeading;
        // La fenêtre et son avertissement s'affichent d'abord ; le choix du fichier vient du bouton « Parcourir ».
        Opened += (_, _) =>
        {
            if (folder.FilePath.Length == 0)
            {
                FocusLater(BrowseFileButton);
            }
            else
            {
                FocusLater(NameBox);
            }
        };
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(this, Strings.KeePassFilesFilter, Strings.KeePassChooseFile, FolderOf(FileBox.Text));
        if (path is not null)
        {
            FileBox.Text = path;
            if ((NameBox.Text ?? "").Trim().Length == 0)
            {
                NameBox.Text = Path.GetFileNameWithoutExtension(path);
            }
        }
    }

    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(this, Strings.AllFilesFilter, Strings.KeePassChooseKeyFile, FolderOf(KeyFileBox.Text));
        if (path is not null)
        {
            KeyFileBox.Text = path;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var file = (FileBox.Text ?? "").Trim().Trim('"');
        var keyFile = (KeyFileBox.Text ?? "").Trim().Trim('"');
        string? error = !File.Exists(file) ? Text.Format(Strings.KeePassFileMissing, file)
            : keyFile.Length > 0 && !File.Exists(keyFile) ? Text.Format(Strings.KeePassFileMissing, keyFile)
            : UsesPasswordBox.IsChecked != true && keyFile.Length == 0 ? Strings.KeePassNeedsKey
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        _folder.FilePath = Path.GetFullPath(file);
        _folder.Name = (NameBox.Text ?? "").Trim();
        _folder.KeyFilePath = keyFile.Length > 0 ? Path.GetFullPath(keyFile) : null;
        _folder.UsesPassword = UsesPasswordBox.IsChecked == true;
        DialogResult = true;
    }

    /// <summary>Dossier du fichier déjà choisi, pour y ouvrir le sélecteur (comme le nom de fichier proposé sous WPF).</summary>
    internal static string? FolderOf(string? path)
    {
        var trimmed = (path ?? "").Trim().Trim('"');
        try
        {
            return trimmed.Length > 0 && Path.IsPathFullyQualified(trimmed) ? Path.GetDirectoryName(trimmed) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
