using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Ajout d'un membre à un safe ou modification de ses droits, avec les droits de la session (« Gérer les membres du
/// safe »). Les 22 droits sont regroupés comme dans le PVWA.
/// </summary>
public partial class SafeMemberDialog : Window
{
    /// <summary>Droits par groupe, par leur nom dans l'API (titres lus à chaque ouverture : la langue peut changer).</summary>
    private static (string Title, string[] Permissions)[] Groups() =>
    [
        (Strings.SafeMemberGroupAccess, [nameof(SafePermissions.ListAccounts), nameof(SafePermissions.UseAccounts), nameof(SafePermissions.RetrieveAccounts)]),
        (Strings.SafeMemberGroupAccounts,
        [
            nameof(SafePermissions.AddAccounts), nameof(SafePermissions.UpdateAccountContent), nameof(SafePermissions.UpdateAccountProperties),
            nameof(SafePermissions.InitiateCPMAccountManagementOperations), nameof(SafePermissions.SpecifyNextAccountContent),
            nameof(SafePermissions.RenameAccounts), nameof(SafePermissions.DeleteAccounts), nameof(SafePermissions.UnlockAccounts),
        ]),
        (Strings.SafeMemberGroupSafe,
        [
            nameof(SafePermissions.ManageSafe), nameof(SafePermissions.ManageSafeMembers), nameof(SafePermissions.BackupSafe),
            nameof(SafePermissions.ViewAuditLog), nameof(SafePermissions.ViewSafeMembers),
        ]),
        (Strings.SafeMemberGroupWorkflow,
        [
            nameof(SafePermissions.RequestsAuthorizationLevel1), nameof(SafePermissions.RequestsAuthorizationLevel2),
            nameof(SafePermissions.AccessWithoutConfirmation),
        ]),
        (Strings.SafeMemberGroupFolders,
            [nameof(SafePermissions.CreateFolders), nameof(SafePermissions.DeleteFolders), nameof(SafePermissions.MoveAccountsAndFolders)]),
    ];

    private readonly Dictionary<string, CheckBox> _boxes = [];
    private readonly HashSet<string> _before;
    private readonly bool _editing;
    private readonly Func<SafeMemberChange, CancellationToken, Task> _save;
    private bool _settingProfile;
    private readonly CancelOnce _closing = new();
    private bool _busy;

    /// <summary>Ajout d'un membre : profil « Utilisateur des comptes » au départ (lister, se connecter, sans les mots de passe).</summary>
    public SafeMemberDialog(string safe, Func<SafeMemberChange, CancellationToken, Task> save)
        : this(save, ProfilePermissions(SafeProfile.AccountUser), editing: false)
    {
        HeadingText.Text = Text.Format(Strings.SafeMemberAddTitle, safe);
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Modification des droits de <paramref name="member"/> (le nom, le type et l'annuaire ne changent pas).</summary>
    public SafeMemberDialog(string safe, SafeMember member, Func<SafeMemberChange, CancellationToken, Task> save)
        : this(save, member.Permissions, editing: true)
    {
        HeadingText.Text = Text.Format(Strings.SafeMemberEditTitle, member.MemberName, safe);
        NameBox.Text = member.MemberName;
        NameBox.IsReadOnly = true;
        TypeBox.SelectedIndex = member.IsGroup ? 1 : 0;
        TypeBox.IsEnabled = false;
        SearchInLabel.Visibility = SearchInBox.Visibility = Visibility.Collapsed;
        UntilBox.SelectedDate = member.Expires?.Date;
    }

    private SafeMemberDialog(Func<SafeMemberChange, CancellationToken, Task> save, SafePermissions permissions, bool editing)
    {
        InitializeComponent();
        _save = save;
        _editing = editing;
        _before = permissions.Granted().ToHashSet();
        ProfileBox.ItemsSource = SafeProfiles.Choices.Append(SafeProfile.Custom)
            .Select(p => new KeyValuePair<SafeProfile, string>(p, ProfileText(p))).ToList();
        ProfileBox.DisplayMemberPath = "Value";
        ProfileBox.SelectedValuePath = "Key";
        TypeBox.ItemsSource = new[] { Strings.SafeMemberUser, Strings.SafeMemberGroup };
        TypeBox.SelectedIndex = 0;
        var granted = permissions.All().ToDictionary(p => p.Name, p => p.Granted);
        var groups = Groups();
        for (int i = 0; i < groups.Length; i++)
        {
            var (title, names) = groups[i];
            var panel = new StackPanel();
            foreach (var name in names)
            {
                var box = new CheckBox { Content = SafePermissionText.Label(name), IsChecked = granted[name], Margin = new Thickness(0, 2, 0, 2) };
                if (SafeProfiles.Sensitive.Contains(name))
                {
                    // Droit sensible : marqué, avec son explication.
                    var mark = new System.Windows.Documents.Run("⚠ ");
                    mark.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "WarningBrush");
                    box.Content = new TextBlock { Inlines = { mark, new System.Windows.Documents.Run(SafePermissionText.Label(name)) } };
                    box.ToolTip = Strings.SafeMemberSensitiveTip;
                    System.Windows.Automation.AutomationProperties.SetName(box, SafePermissionText.Label(name));
                    System.Windows.Automation.AutomationProperties.SetHelpText(box, Strings.SafeMemberSensitiveTip);
                }

                box.Checked += OnRightChanged;
                box.Unchecked += OnRightChanged;
                _boxes[name] = box;
                panel.Children.Add(box);
            }

            (i < 2 ? LeftGroups : RightGroups).Children.Add(
                new GroupBox { Header = title, Content = panel, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6, 4, 6, 4) });
        }

        Closed += (_, _) => _closing.Cancel();
        ShowChanges();
    }

    private static SafePermissions ProfilePermissions(SafeProfile profile)
    {
        var permissions = new SafePermissions();
        foreach (var name in SafeProfiles.Rights(profile))
        {
            permissions.Set(name, true);
        }

        return permissions;
    }

    private static string ProfileText(SafeProfile profile) => profile switch
    {
        SafeProfile.ReadOnly => Strings.SafeProfileReadOnly,
        SafeProfile.AccountUser => Strings.SafeProfileAccountUser,
        SafeProfile.AccountManager => Strings.SafeProfileAccountManager,
        SafeProfile.Full => Strings.SafeProfileFull,
        _ => Strings.SafeProfileCustom,
    };

    private IEnumerable<string> GrantedNow() => _boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key);

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingProfile || ProfileBox.SelectedValue is not SafeProfile profile || profile == SafeProfile.Custom)
        {
            return;
        }

        var rights = SafeProfiles.Rights(profile);
        _settingProfile = true;
        foreach (var (name, box) in _boxes)
        {
            box.IsChecked = rights.Contains(name);
        }

        _settingProfile = false;
        ShowChanges();
    }

    private void OnRightChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingProfile)
        {
            ShowChanges();
        }
    }

    /// <summary>Profil correspondant aux cases ; en modification, cases changées en gras et bilan « +2 / −1 ».</summary>
    private void ShowChanges()
    {
        var now = GrantedNow().ToHashSet();
        _settingProfile = true;
        ProfileBox.SelectedValue = SafeProfiles.Match(now);
        _settingProfile = false;
        if (!_editing)
        {
            return;
        }

        foreach (var (name, box) in _boxes)
        {
            bool changed = now.Contains(name) != _before.Contains(name);
            box.FontWeight = changed ? FontWeights.SemiBold : FontWeights.Normal;
            if (!SafeProfiles.Sensitive.Contains(name))
            {
                box.ToolTip = changed
                    ? Text.Format(Strings.SafeMemberChangedTip, _before.Contains(name) ? Strings.SafeMemberWasGranted : Strings.SafeMemberWasNotGranted)
                    : null;
            }
        }

        int added = now.Count(n => !_before.Contains(n));
        int removed = _before.Count(n => !now.Contains(n));
        ChangesText.Text = Text.Format(Strings.SafeMemberChanges, added, removed);
        ChangesText.Visibility = added + removed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = _busy && DialogResult is null;
        base.OnClosing(e);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (NameBox.Text.Trim().Length == 0)
        {
            ShowError(Strings.SafeMemberNameRequired);
            NameBox.Focus();
            return;
        }

        var permissions = new SafePermissions();
        foreach (var (name, box) in _boxes)
        {
            permissions.Set(name, box.IsChecked == true);
        }

        // Droits sensibles nouvellement accordés : confirmés un par un, « Annuler » par défaut.
        var sensitive = SafeProfiles.SensitiveAdded(_editing ? _before : [], permissions.Granted());
        if (sensitive.Count > 0 && !ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Title,
                Heading = Text.Format(Strings.SafeMemberSensitiveHeading, NameBox.Text.Trim()),
                Message = Strings.SafeMemberSensitiveMessage,
                Bullets = sensitive.Select(SafePermissionText.Label).ToList(),
                Kind = ConfirmKind.Warning,
                Actions = [Strings.SafeMemberSensitiveAction],
            }))
        {
            return;
        }

        var change = new SafeMemberChange
        {
            MemberName = NameBox.Text.Trim(),
            MemberType = TypeBox.SelectedIndex == 1 ? "Group" : "User",
            SearchIn = SearchInBox.Text,
            Expires = UntilBox.SelectedDate,
            Permissions = permissions,
        };
        try
        {
            SetBusy(true);
            await _save(change, _closing.Token);
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
            ShowError(Text.Format(Strings.SafeMemberForbidden, ex.Message));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.SafeMemberFailed, ErrorText.Describe(ex)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
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
