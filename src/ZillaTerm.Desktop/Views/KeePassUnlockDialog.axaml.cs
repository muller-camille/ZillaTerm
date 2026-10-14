using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Views;

/// <summary>
/// Déverrouillage d'un coffre KeePass : mot de passe maître et/ou fichier clé, et option pour garder le mot de passe
/// dans le coffre local. La dérivation de clé se fait en arrière-plan (elle peut prendre quelques secondes).
/// </summary>
/// <remarks>
/// Le mot de passe maître est lu puis effacé du champ dès le clic sur « Déverrouiller » (Avalonia le garde dans une
/// chaîne .NET, sans SecureString) : après un échec, il est à retaper. L'option « Mémoriser » n'apparaît que là où le
/// coffre local peut être protégé pour le compte de l'utilisateur (<see cref="DpapiProtector.IsAvailable"/>).
/// </remarks>
public partial class KeePassUnlockDialog : DialogWindow
{
    private readonly KeePassFolder _folder = new();
    private readonly KeePassManager? _manager;
    private readonly Func<Task<bool>> _ensureLocalStore = () => Task.FromResult(false);
    private CancellationTokenSource? _unlocking;
    private bool _closed;

    public KeePassUnlockDialog() => InitializeComponent();

    /// <param name="ensureLocalStore">Déverrouille ou crée le coffre local ; faux si l'utilisateur renonce.</param>
    internal KeePassUnlockDialog(KeePassFolder folder, KeePassManager manager, Func<Task<bool>> ensureLocalStore, string? message = null)
        : this()
    {
        CapsLockWarning.Attach(CapsLockText, PasswordBox);
        _folder = folder;
        _manager = manager;
        _ensureLocalStore = ensureLocalStore;
        NameText.Text = folder.DisplayName;
        PathText.Text = folder.FilePath;
        ToolTip.SetTip(PathText, folder.FilePath);
        KeyFileBox.Text = folder.KeyFilePath ?? "";
        RememberBox.IsChecked = folder.RememberPassword;
        if (!folder.UsesPassword)
        {
            PasswordLabel.IsVisible = PasswordBox.IsVisible = RememberBox.IsVisible = RememberHint.IsVisible = false;
        }
        else if (!DpapiProtector.IsAvailable)
        {
            // Coffre local non protégeable sur ce système (Linux, macOS : trousseau non relié) : rien n'est proposé à mémoriser.
            RememberBox.IsVisible = RememberHint.IsVisible = false;
        }

        if (message is not null)
        {
            ShowMessage(message, error: true);
            // Écrit avant l'ouverture : annoncé une fois la fenêtre affichée.
            Opened += (_, _) => MessageText.Announce();
        }

        Opened += (_, _) =>
        {
            if (folder.UsesPassword)
            {
                FocusLater(PasswordBox);
            }
            else
            {
                FocusLater(UnlockButton);
            }
        };
    }

    /// <summary>« Mémoriser » était coché mais le coffre local n'a pas été ouvert : le mot de passe n'est pas gardé.</summary>
    public bool NotRemembered { get; private set; }

    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(this, Strings.AllFilesFilter, folder: KeePassFolderDialog.FolderOf(KeyFileBox.Text));
        if (path is not null)
        {
            KeyFileBox.Text = path;
        }
    }

    private async void OnUnlock(object? sender, RoutedEventArgs e)
    {
        if (_manager is null || _unlocking is not null)
        {
            return;
        }

        var keyFile = (KeyFileBox.Text ?? "").Trim().Trim('"');
        if (keyFile.Length > 0 && !File.Exists(keyFile))
        {
            ShowMessage(Text.Format(Strings.KeePassFileMissing, keyFile), error: true);
            return;
        }

        if (!_folder.UsesPassword && keyFile.Length == 0)
        {
            ShowMessage(Strings.KeePassNeedsKey, error: true);
            return;
        }

        // Lu en UTF-8 puis effacé du champ ; le tableau est effacé à la fin, ouvert ou non.
        var password = SecretInput.ReadUtf8(PasswordBox, clear: true);
        bool remember = _folder.UsesPassword && DpapiProtector.IsAvailable && RememberBox.IsChecked == true;
        SetBusy(true);
        _unlocking = new CancellationTokenSource();
        try
        {
            await _manager.UnlockAsync(_folder, password, keyFile.Length > 0 ? keyFile : null, _unlocking.Token);
            if (_closed)
            {
                // Fenêtre fermée pendant la dérivation de clé : l'utilisateur a renoncé, le coffre ne reste pas ouvert.
                _manager.Lock(_folder.Id);
                return;
            }

            _folder.KeyFilePath = keyFile.Length > 0 ? Path.GetFullPath(keyFile) : null;
            await SaveRememberedAsync(password, remember);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
        }
        catch (Exception ex) when (ex is KeePassException or IOException or UnauthorizedAccessException)
        {
            SetBusy(false);
            ShowMessage(ex.Message, error: true);
            if (PasswordBox.IsVisible)
            {
                FocusLater(PasswordBox);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            _unlocking?.Dispose();
            _unlocking = null;
        }
    }

    /// <summary>Garde ou oublie le mot de passe maître dans le coffre local, selon la case.</summary>
    private async Task SaveRememberedAsync(byte[] password, bool remember)
    {
        var store = _manager!.Store;
        if (remember && password.Length > 0 && await _ensureLocalStore())
        {
            store.Set(_folder.Id, password);
            _folder.RememberPassword = true;
        }
        else if (remember && password.Length > 0)
        {
            // Coffre local refusé (création ou déverrouillage annulé) : la base est ouverte, mais rien n'est mémorisé.
            NotRemembered = true;
        }
        else if (!remember)
        {
            if (store.IsUnlocked)
            {
                store.Remove(_folder.Id);
            }

            _folder.RememberPassword = false;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _unlocking?.Cancel();

    /// <summary>Fermer la fenêtre (croix, Échap) pendant le déverrouillage l'annule aussi.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _unlocking?.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PasswordBox.Clear();
        base.OnClosed(e);
    }

    private void SetBusy(bool busy)
    {
        Form.IsEnabled = !busy;
        UnlockButton.IsEnabled = !busy;
        Progress.IsVisible = busy;
        if (busy)
        {
            ShowMessage(Strings.KeePassUnlocking, error: false);
        }
        else
        {
            MessageText.IsVisible = false;
        }
    }

    private void ShowMessage(string text, bool error)
    {
        MessageText.Text = text;
        MessageText[!TextBlock.ForegroundProperty] = MessageText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
        MessageText.IsVisible = true;
    }
}
