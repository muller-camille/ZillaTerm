using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Copie du mot de passe d'un compte : motif et ticket éventuels, récupération sur le PVWA puis copie dans le
/// presse-papiers sécurisé. Le mot de passe n'est jamais affiché ni converti en chaîne, et il est effacé de la mémoire
/// dès qu'il est copié.
/// </summary>
public partial class RetrievePasswordDialog : Window
{
    private readonly Func<RetrieveOptions, CancellationToken, Task<char[]>> _retrieve;
    private readonly Func<char[], bool> _copy;
    private readonly CancelOnce _closing = new();
    private bool _busy;

    /// <param name="copy">Copie dans le presse-papiers ; faux s'il est occupé par une autre application.</param>
    /// <param name="safe">Safe du compte, affiché sous son nom.</param>
    public RetrievePasswordDialog(string account, TimeSpan clipboardDelay, string? reason,
        Func<RetrieveOptions, CancellationToken, Task<char[]>> retrieve, Func<char[], bool> copy, string? safe = null)
    {
        InitializeComponent();
        _retrieve = retrieve;
        _copy = copy;
        HeadingText.Text = Text.Format(Strings.RetrieveHeading, account);
        if (!string.IsNullOrWhiteSpace(safe))
        {
            SafeText.Text = Text.Format(Strings.SafeSubject, safe);
            SafeText.Visibility = Visibility.Visible;
        }

        ClipboardText.Text = Text.Format(Strings.RetrieveClipboard, (int)clipboardDelay.TotalSeconds);
        ReasonBox.Text = reason ?? "";
        Loaded += (_, _) => ReasonBox.Focus();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _busy && DialogResult is null;
        base.OnClosing(e);
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        char[]? secret = null;
        try
        {
            SetBusy(true);
            secret = await _retrieve(new RetrieveOptions
            {
                Reason = ReasonBox.Text,
                TicketingSystemName = TicketSystemBox.Text,
                TicketId = TicketIdBox.Text,
            }, _closing.Token);
            if (!_copy(secret))
            {
                ShowError(Strings.ClipboardBusy);
                return;
            }

            DialogResult = true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Fenêtre fermée.
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            SessionExpired = true;
            DialogResult = false;
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            ShowError(Text.Format(Strings.RetrieveForbidden, ex.Message));
            // Refus souvent dû à un motif exigé : le curseur y va.
            ReasonBox.Focus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.RetrieveFailed, ErrorText.Describe(ex)));
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secret.AsSpan()));
            }

            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CopyButton.IsEnabled = !busy;
        // Pendant l'envoi au PVWA, ni « Annuler » ni Échap : la demande est partie, son résultat doit être connu.
        CancelButton.IsEnabled = !busy;
        StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
