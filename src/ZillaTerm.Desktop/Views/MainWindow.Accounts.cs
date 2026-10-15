using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Net;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Actions sur les comptes CyberArk, avec les droits de la session : ajout, modification, suppression, opérations du
/// CPM et copie du mot de passe.
/// </summary>
public partial class MainWindow
{
    /// <summary>Durée de vie d'un mot de passe copié dans le presse-papiers.</summary>
    private static readonly TimeSpan PasswordClipboardDelay = TimeSpan.FromSeconds(20);

    private SecureClipboard? _passwordClipboard;
    private DispatcherTimer? _passwordCountdown;

    private static string AccountLabel(PvwaAccount account) => $"{account.UserName}@{account.Address}";

    /// <summary>Valeurs proposées dans les listes (safes, plateformes) : celles des comptes visibles, triées, sans doublon.</summary>
    private List<string> KnownValues(Func<PvwaAccount, string?> selector) =>
        _accounts.Select(selector).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private Task ShowAccountErrorAsync(string message) =>
        ConfirmDialog.ShowMessageAsync(this, message, ConfirmKind.Warning);

    // ===================== Ajout =====================

    private async void OnAddAccount(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.CommandParameter is string safe)
        {
            await AddAccountAsync(safe, _current);
        }
    }

    /// <summary>
    /// Crée un compte dans le safe (droit « Add accounts » nécessaire), puis recharge la liste des comptes. Le compte
    /// cliqué sert de modèle : sa plateforme et son domaine de connexion sont proposés.
    /// </summary>
    private async Task AddAccountAsync(string safe, PvwaAccount? template)
    {
        if (_client is not { } client)
        {
            return;
        }

        var platform = template?.PlatformId ?? MostCommonPlatform(safe);
        var dialog = new AccountDialog(KnownValues(a => a.SafeName), KnownValues(a => a.PlatformId), safe, platform, template?.LogonDomain,
            client.AddAccountAsync) ;
        if (!await dialog.ShowDialogAsync(this) || dialog.Saved is not { } created)
        {
            if (dialog.SessionExpired)
            {
                await OnSessionExpiredAsync();
            }

            return;
        }

        await ReloadAndSelectAsync(created.Id);
        ActionStatus(AccountLabel(created), Text.Format(Strings.AccountCreated, AccountLabel(created), created.SafeName));
    }

    /// <summary>Plateforme la plus courante du safe (proposée quand on part du safe et non d'un compte).</summary>
    private string? MostCommonPlatform(string? safe) => _accounts
        .Where(a => string.Equals(a.SafeName, safe, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.PlatformId))
        .GroupBy(a => a.PlatformId!, StringComparer.OrdinalIgnoreCase)
        .MaxBy(g => g.Count())?.Key;

    // ===================== Import CSV =====================

    /// <summary>
    /// Import de comptes depuis un fichier CSV ; le safe et la plateforme du compte (ou du safe) cliqué servent de
    /// valeurs par défaut. La liste est rechargée si des comptes ont été créés.
    /// </summary>
    private async void OnImportAccounts(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client)
        {
            return;
        }

        // Clic droit : safe cliqué ; barre d'outils : safe du compte sélectionné, s'il y en a un.
        var safe = (sender as MenuItem)?.CommandParameter as string ?? _current?.SafeName;
        var platform = _current?.PlatformId ?? MostCommonPlatform(safe);
        var choose = new ImportAccountsDialog(KnownValues(a => a.SafeName), KnownValues(a => a.PlatformId), safe, platform) ;
        if (!await choose.ShowDialogAsync(this) || choose.Confirmed is not { } import)
        {
            return;
        }

        var progress = new ImportProgressDialog(import, client.AddAccountAsync);
        try
        {
            await progress.ShowDialogAsync(this);
        }
        finally
        {
            // Mots de passe pas encore envoyés (import arrêté) : effacés même si la fenêtre n'a pas pu s'ouvrir.
            import.Clear();
        }

        if (progress.SessionExpired)
        {
            await OnSessionExpiredAsync();
            return;
        }

        if (progress.Created > 0)
        {
            await LoadAccountsAsync();
            ActionStatus("", Text.Format(Strings.AccountsImported, progress.Created));
        }
    }

    /// <summary>Recharge les comptes puis désigne <paramref name="accountId"/> comme cible des actions.</summary>
    private async Task ReloadAndSelectAsync(string accountId)
    {
        await LoadAccountsAsync();
        if (_byId.TryGetValue(accountId, out var account))
        {
            SetCurrent(account);
        }
    }

    // ===================== Modification et suppression =====================

    private async void OnEditAccount(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _current is not { } account)
        {
            return;
        }

        var dialog = new AccountDialog(account, KnownValues(a => a.PlatformId),
            (operations, ct) => client.UpdateAccountAsync(account.Id, operations, ct)) ;
        if (!await dialog.ShowDialogAsync(this) || dialog.Saved is not { } saved)
        {
            if (dialog.SessionExpired)
            {
                await OnSessionExpiredAsync();
            }

            return;
        }

        await ReloadAndSelectAsync(account.Id);
        ActionStatus(AccountLabel(saved), Text.Format(Strings.AccountUpdated, AccountLabel(saved)));
    }

    private async void OnDeleteAccount(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _current is not { } account)
        {
            return;
        }

        var label = AccountLabel(account);
        // Action la plus lourde du produit : conséquences détaillées, et case à cocher avant que le bouton s'active.
        if (!await ConfirmDialog.DestructiveAsync(this, Strings.AccountDeleteTitle, Text.Format(Strings.AccountDeleteHeading, label),
                Strings.AccountDeleteTitle, subject: Text.Format(Strings.SafeSubject, account.SafeName),
                bullets: [Strings.AccountDeleteForEveryone, Strings.AccountDeletePassword, Strings.AccountDeleteNoUndo],
                acknowledge: Strings.AccountDeleteAck))
        {
            return;
        }

        try
        {
            await client.DeleteAccountAsync(account.Id, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            await OnSessionExpiredAsync();
            return;
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await ShowAccountErrorAsync(Text.Format(Strings.DeleteAccountForbidden, ex.Message));
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await ShowAccountErrorAsync(Text.Format(Strings.DeleteAccountFailed, ErrorText.Describe(ex)));
            return;
        }

        SetCurrent(null);
        await LoadAccountsAsync();
        ActionStatus(label, Text.Format(Strings.AccountDeleted, label, account.SafeName));
    }

    // ===================== Opérations du CPM =====================

    private void OnCpmVerify(object? sender, RoutedEventArgs e) => _ = RunCpmActionAsync(CpmAction.Verify);

    private void OnCpmChange(object? sender, RoutedEventArgs e) => _ = RunCpmActionAsync(CpmAction.Change);

    private void OnCpmReconcile(object? sender, RoutedEventArgs e) => _ = RunCpmActionAsync(CpmAction.Reconcile);

    /// <summary>
    /// Demande au CPM de vérifier, changer ou réconcilier le mot de passe du compte sélectionné (confirmation pour
    /// changer et réconcilier). Le CPM traite la demande ensuite : l'état se met à jour au prochain chargement (F5).
    /// </summary>
    private async Task RunCpmActionAsync(CpmAction action)
    {
        if (_client is not { } client)
        {
            return;
        }

        if (_current is not { } account)
        {
            if (_currentSaved is { } saved)
            {
                SetStatus(Text.Format(Strings.SavedAccountGone, saved.Name), isError: true);
            }

            return;
        }

        var label = AccountLabel(account);
        var confirm = action switch
        {
            CpmAction.Change => (Heading: Strings.CpmChangeHeading, Message: Strings.CpmChangeMessage, Action: Strings.CpmChangeAction),
            CpmAction.Reconcile => (Strings.CpmReconcileHeading, Strings.CpmReconcileMessage, Strings.CpmReconcileAction),
            _ => default((string Heading, string Message, string Action)?),
        };
        if (confirm is { } c && !await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
            {
                Title = Strings.CpmTitle,
                Heading = Text.Format(c.Heading, label),
                Subject = Text.Format(Strings.SafeSubject, account.SafeName),
                Message = c.Message,
                Kind = ConfirmKind.Warning,
                Actions = [c.Action],
            }))
        {
            return;
        }

        try
        {
            await client.RunCpmActionAsync(account.Id, action, _lifetime.Token);
            var name = action switch
            {
                CpmAction.Change => Strings.CpmActionChange,
                CpmAction.Reconcile => Strings.CpmActionReconcile,
                _ => Strings.CpmActionVerify,
            };
            ActionStatus(label, Text.Format(Strings.CpmRequested, label, name));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Fenêtre en cours de fermeture.
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            await OnSessionExpiredAsync();
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await ShowAccountErrorAsync(Text.Format(Strings.CpmForbidden, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await ShowAccountErrorAsync(Text.Format(Strings.CpmFailed, ErrorText.Describe(ex)));
        }
    }

    // ===================== Copie du mot de passe =====================

    /// <summary>
    /// Récupère le mot de passe du compte sélectionné (motif et ticket demandés) et le copie dans le presse-papiers
    /// pour <see cref="PasswordClipboardDelay"/>, sans l'afficher.
    /// </summary>
    private async void OnCopyPassword(object? sender, RoutedEventArgs e)
    {
        if (_client is not { } client)
        {
            return;
        }

        if (_current is not { } account)
        {
            if (_currentSaved is { } saved)
            {
                SetStatus(Text.Format(Strings.SavedAccountGone, saved.Name), isError: true);
            }

            return;
        }

        var label = AccountLabel(account);
        _passwordClipboard ??= new SecureClipboard(this, PasswordClipboardDelay, () =>
        {
            _passwordCountdown?.Stop();
            SetStatus(Strings.PasswordClipboardCleared);
        });
        var reason = _currentSaved?.AccountId == account.Id ? _currentSaved.Reason : null;
        var dialog = new RetrievePasswordDialog(label, PasswordClipboardDelay, reason,
            (options, ct) => client.RetrievePasswordAsync(account.Id, options, ct), _passwordClipboard.CopyAsync, account.SafeName);
        if (await dialog.ShowDialogAsync(this))
        {
            InterventionRecord(Core.Interventions.InterventionKind.Action, label, Strings.InterventionPasswordCopied);
            StartPasswordCountdown(label);
        }
        else if (dialog.SessionExpired)
        {
            await OnSessionExpiredAsync();
        }
    }

    /// <summary>Barre d'état : secondes restantes avant l'effacement du mot de passe copié.</summary>
    private void StartPasswordCountdown(string label)
    {
        var clearsAt = DateTime.UtcNow + PasswordClipboardDelay;
        void Show() => SetStatus(Text.Format(Strings.PasswordCopied, label,
            Math.Max(0, (int)Math.Ceiling((clearsAt - DateTime.UtcNow).TotalSeconds))));
        _passwordCountdown?.Stop();
        _passwordCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _passwordCountdown.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= clearsAt)
            {
                _passwordCountdown?.Stop();
                return;
            }

            Show();
        };
        Show();
        _passwordCountdown.Start();
    }

    /// <summary>Efface du presse-papiers un mot de passe copié (déconnexion, fermeture, verrouillage de Windows).</summary>
    private void ClearPasswordClipboard() => _passwordClipboard?.Clear();
}
