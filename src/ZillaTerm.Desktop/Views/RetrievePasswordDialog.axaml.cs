using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Copie du mot de passe d'un compte : motif et ticket éventuels, récupération sur le PVWA puis copie dans le
/// presse-papiers sécurisé. Le mot de passe n'est jamais affiché ni converti en chaîne, et il est effacé de la mémoire
/// dès qu'il est copié.
/// </summary>
public partial class RetrievePasswordDialog : DialogWindow
{
    private readonly Func<RetrieveOptions, CancellationToken, Task<char[]>>? _retrieve;
    private readonly Func<char[], Task<bool>>? _copy;
    private readonly CancellationTokenSource _closing = new();
    private bool _busy;

    public RetrievePasswordDialog() => InitializeComponent();

    /// <param name="copy">
    /// Copie dans le presse-papiers sécurisé (fournie par la fenêtre principale, qui l'efface après le délai) ; faux s'il
    /// est occupé par une autre application. Asynchrone : le presse-papiers d'Avalonia l'est.
    /// </param>
    /// <param name="safe">Safe du compte, affiché sous son nom.</param>
    public RetrievePasswordDialog(string account, TimeSpan clipboardDelay, string? reason,
        Func<RetrieveOptions, CancellationToken, Task<char[]>> retrieve, Func<char[], Task<bool>> copy, string? safe = null)
        : this()
    {
        _retrieve = retrieve;
        _copy = copy;
        HeadingText.Text = Text.Format(Strings.RetrieveHeading, account);
        if (!string.IsNullOrWhiteSpace(safe))
        {
            SafeText.Text = Text.Format(Strings.SafeSubject, safe);
            SafeText.IsVisible = true;
        }

        ClipboardText.Text = Text.Format(PlatformText.RetrieveClipboard, (int)clipboardDelay.TotalSeconds);
        ReasonBox.Text = reason ?? "";
        Opened += (_, _) => FocusLater(ReasonBox);
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Pendant l'envoi au PVWA, la fenêtre reste ouverte (croix, Alt+F4) : la demande est partie, son résultat doit être
        // connu. Elle se ferme seulement par son propre résultat.
        if (_busy && DialogResult is null)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_busy || _retrieve is null || _copy is null)
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
            if (!await _copy(secret))
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
            FocusLater(ReasonBox);
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
        StatusText.IsVisible = busy;
        if (busy)
        {
            ErrorPanel.IsVisible = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorPanel.IsVisible = true;
    }
}
