using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Views;

/// <summary>Coffre local des mots de passe maîtres KeePass : création, déverrouillage, changement de mot de passe.</summary>
/// <remarks>
/// Le coffre est fourni par l'appelant, déjà construit avec sa protection (<see cref="ISecretProtector"/> : DPAPI sous
/// Windows, trousseau du système ailleurs) : la fenêtre n'en crée aucune.
/// </remarks>
public partial class LocalStoreDialog : DialogWindow
{
    public enum Mode
    {
        Create,
        Unlock,
        ChangePassword,
    }

    private readonly LocalSecretStore? _store;
    private readonly Mode _mode;
    private readonly IEnumerable<string>? _remembered;
    // Fenêtre fermée (Alt+F4) pendant le calcul : son résultat ne peut plus être donné.
    private bool _closed;

    public LocalStoreDialog() => InitializeComponent();

    /// <param name="store">Coffre local, construit par l'appelant avec son <see cref="ISecretProtector"/>.</param>
    /// <param name="remembered">
    /// Coffres KeePass dont le mot de passe doit rester mémorisé : au déverrouillage, les autres secrets (coffres retirés
    /// ou « se souvenir » décoché pendant que le coffre local était verrouillé) sont oubliés.
    /// </param>
    /// <param name="offerLater">Déverrouillage demandé en passant (ouverture d'une base KeePass) : « Plus tard » plutôt
    /// qu'« Annuler ».</param>
    public LocalStoreDialog(LocalSecretStore store, Mode mode, IEnumerable<string>? remembered = null, bool offerLater = true)
        : this()
    {
        CapsLockWarning.Attach(CapsLockText, PasswordBox, ConfirmBox);
        _store = store;
        _mode = mode;
        _remembered = remembered;
        IntroText.Text = mode switch
        {
            Mode.Create => PlatformText.LocalStoreCreateIntro,
            Mode.Unlock => Strings.LocalStoreUnlockIntro,
            _ => Strings.LocalStoreChangeIntro,
        };
        OkButton.Content = mode switch
        {
            Mode.Create => Strings.LocalStoreCreateButton,
            Mode.Unlock => Strings.KeePassUnlock,
            _ => Strings.Save,
        };
        if (mode == Mode.Unlock)
        {
            ConfirmLabel.IsVisible = ConfirmBox.IsVisible = MinLengthText.IsVisible = false;
            // Sans la ligne « 8 caractères au moins » : l'espace sous le champ, avant les boutons.
            PasswordBox.Margin = new Avalonia.Thickness(0, 0, 0, 8);
            if (offerLater)
            {
                CancelButton.Content = Strings.LocalStoreLater;
            }
        }
        else
        {
            // Annoncé avant la saisie, pas seulement après une erreur.
            MinLengthText.Text = Text.Format(Strings.LocalStoreMinLength, LocalSecretStore.MinPasswordLength);
        }

        Opened += (_, _) => FocusLater(PasswordBox);
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        // Champs vidés dès la lecture (Avalonia n'a pas de SecureString) ; copie effacée dès que le coffre local en a
        // dérivé sa clé.
        var password = SecretInput.Read(PasswordBox, clear: true);
        var confirm = SecretInput.Read(ConfirmBox, clear: true);
        try
        {
            if (_mode != Mode.Unlock)
            {
                if (password.Length < LocalSecretStore.MinPasswordLength)
                {
                    ShowError(Text.Format(Core.Localization.CoreStrings.LocalStorePasswordTooShort, LocalSecretStore.MinPasswordLength));
                    FocusLater(PasswordBox);
                    return;
                }

                bool same = password.AsSpan().SequenceEqual(confirm);
                if (!same)
                {
                    ShowError(Strings.LocalStoreMismatch);
                    FocusLater(PasswordBox);
                    return;
                }
            }

            SecretInput.Clear(confirm);
            await RunAsync(password);
        }
        finally
        {
            SecretInput.Clear(password);
            SecretInput.Clear(confirm);
        }
    }

    private async Task RunAsync(char[] password)
    {
        if (_store is not { } store)
        {
            return;
        }

        IsEnabled = false;
        Progress.IsVisible = true;
        ErrorText.IsVisible = false;
        try
        {
            // Argon2id (64 Mio) : quelques centaines de millisecondes, hors du thread de l'interface.
            await Task.Run(() =>
            {
                switch (_mode)
                {
                    case Mode.Create:
                        store.Create(password);
                        break;
                    case Mode.Unlock:
                        store.Unlock(password);
                        if (_remembered is not null)
                        {
                            store.RemoveAllExcept(_remembered);
                        }

                        break;
                    default:
                        store.ChangePassword(password);
                        break;
                }
            });
            if (!_closed)
            {
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Session Windows verrouillée pendant le calcul : le coffre local reste verrouillé.
            if (!_closed)
            {
                DialogResult = false;
            }
        }
        catch (Exception ex) when (!_closed && ex is KeePassException or ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException)
        {
            IsEnabled = true;
            Progress.IsVisible = false;
            ShowError(ex.Message);
            // Le champ a été vidé à la lecture : le mot de passe se retape en entier.
            FocusLater(PasswordBox);
        }
        catch (Exception ex) when (_closed && ex is KeePassException or ArgumentException or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            // Fenêtre déjà fermée : plus personne à qui montrer l'erreur ; le coffre local reste dans son état d'avant.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        PasswordBox.Clear();
        ConfirmBox.Clear();
        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
