using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Création d'un compte dans un safe (<c>POST /API/Accounts</c>) ou modification d'un compte
/// (<c>PATCH /API/Accounts/{id}</c>), avec les droits de la session. À la création, le mot de passe est lu dans les
/// champs masqués sans passer par une chaîne, envoyé une fois puis effacé de la mémoire ; la modification ne touche ni
/// au safe ni au mot de passe.
/// </summary>
public partial class AccountDialog : Window
{
    private readonly Func<NewAccount, CancellationToken, Task<PvwaAccount>>? _create;
    private readonly PvwaAccount? _original;
    private readonly Func<IReadOnlyList<PatchOperation>, CancellationToken, Task<PvwaAccount>>? _update;
    private readonly CancelOnce _closing = new();
    private bool _busy;

    /// <param name="safes">Safes proposés (ceux des comptes visibles) ; la saisie reste libre.</param>
    /// <param name="platforms">Plateformes proposées (celles des comptes visibles) ; la saisie reste libre.</param>
    public AccountDialog(IEnumerable<string> safes, IEnumerable<string> platforms, string safe, string? platform, string? logonDomain,
        Func<NewAccount, CancellationToken, Task<PvwaAccount>> create)
        : this(platforms)
    {
        _create = create;
        SafeBox.ItemsSource = safes.ToList();
        SafeBox.Text = safe;
        PlatformBox.Text = platform ?? "";
        DomainBox.Text = logonDomain ?? "";
        Loaded += (_, _) => (PlatformBox.Text.Length == 0 ? (Control)PlatformBox : AddressBox).Focus();
    }

    /// <summary>Modification de <paramref name="account"/> : seuls les champs changés sont envoyés.</summary>
    public AccountDialog(PvwaAccount account, IEnumerable<string> platforms,
        Func<IReadOnlyList<PatchOperation>, CancellationToken, Task<PvwaAccount>> update)
        : this(platforms)
    {
        _original = account;
        _update = update;
        Title = HeadingText.Text = Strings.EditAccountTitle;
        IntroText.Text = Strings.EditAccountIntro;
        CreateButton.Content = Strings.Save;
        SafeBox.Text = account.SafeName ?? "";
        SafeBox.IsEnabled = false;
        PasswordLabel.Visibility = PasswordBox.Visibility = ConfirmLabel.Visibility = ConfirmBox.Visibility = Visibility.Collapsed;
        PlatformBox.Text = account.PlatformId ?? "";
        AddressBox.Text = account.Address ?? "";
        UserBox.Text = account.UserName ?? "";
        DomainBox.Text = account.LogonDomain;
        NameBox.Text = account.Name ?? "";
        MachinesBox.Text = account.RemoteMachines;
        CpmBox.IsChecked = account.SecretManagement?.AutomaticManagementEnabled ?? true;
        ReasonBox.Text = account.SecretManagement?.ManualManagementReason ?? "";
        Loaded += (_, _) => AddressBox.Focus();
    }

    private AccountDialog(IEnumerable<string> platforms)
    {
        InitializeComponent();
        // Branché ici et non dans le XAML : au chargement, WPF coche la case avant que le champ du motif existe.
        CpmBox.Checked += OnCpmChanged;
        CpmBox.Unchecked += OnCpmChanged;
        PlatformBox.ItemsSource = platforms.ToList();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            PasswordBox.Clear();
            ConfirmBox.Clear();
        };
    }

    /// <summary>Compte tel que le PVWA l'a enregistré, quand <see cref="Window.DialogResult"/> vaut <c>true</c>.</summary>
    public PvwaAccount? Saved { get; private set; }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Pas de fermeture par la croix pendant l'envoi : le compte serait peut-être créé sans que la liste le montre.
        e.Cancel = _busy && DialogResult is null;
        base.OnClosing(e);
    }

    private void OnCpmChanged(object sender, RoutedEventArgs e) => ReasonBox.IsEnabled = CpmBox.IsChecked != true;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var safe = SafeBox.Text.Trim();
        var platform = PlatformBox.Text.Trim();
        var address = AddressBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (safe.Length == 0 || platform.Length == 0 || address.Length == 0 || user.Length == 0)
        {
            ShowError(Strings.AddAccountRequired);
            // Curseur sur le premier champ obligatoire vide.
            (safe.Length == 0 ? SafeBox : platform.Length == 0 ? PlatformBox : address.Length == 0 ? (System.Windows.Controls.Control)AddressBox : UserBox).Focus();
            return;
        }

        if (_original is not null)
        {
            await SaveChangesAsync(platform, address, user);
            return;
        }

        var secret = SecretInput.Read(PasswordBox);
        var confirm = SecretInput.Read(ConfirmBox);
        try
        {
            if (!secret.AsSpan().SequenceEqual(confirm))
            {
                ShowError(Strings.AddAccountMismatch);
                ConfirmBox.Clear();
                ConfirmBox.Focus();
                return;
            }

            var account = new NewAccount
            {
                SafeName = safe,
                PlatformId = platform,
                Address = address,
                UserName = user,
                Name = NameBox.Text,
                LogonDomain = DomainBox.Text,
                Secret = secret,
                AutomaticManagement = CpmBox.IsChecked == true,
                ManualManagementReason = ReasonBox.Text,
                RemoteMachines = MachinesBox.Text,
            };
            SetBusy(true);
            Saved = await _create!(account, _closing.Token);
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
            ShowError(Text.Format(Strings.AddAccountForbidden, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.AddAccountFailed, ErrorText.Describe(ex)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secret.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(confirm.AsSpan()));
            SetBusy(false);
        }
    }

    private async Task SaveChangesAsync(string platform, string address, string user)
    {
        var edit = new AccountEdit(NameBox.Text, address, user, platform, DomainBox.Text, MachinesBox.Text,
            CpmBox.IsChecked == true, ReasonBox.Text);
        var operations = AccountChanges.Diff(_original!, edit);
        if (operations.Count == 0)
        {
            DialogResult = false;
            return;
        }

        try
        {
            SetBusy(true);
            Saved = await _update!(operations, _closing.Token);
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
            ShowError(Text.Format(Strings.EditAccountForbidden, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.EditAccountFailed, ErrorText.Describe(ex)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CreateButton.IsEnabled = !busy;
        // Pendant l'envoi au PVWA, ni « Annuler » ni Échap : la demande est partie, son résultat doit être connu.
        CancelButton.IsEnabled = !busy;
        StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ErrorMessage.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }
}
