using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Membres d'un safe et leurs droits, lus sur le PVWA (droit « View Safe Members » nécessaire) : qui peut ajouter des
/// comptes, et le détail des droits du membre sélectionné. Ajout, modification des droits et retrait de membres quand
/// <see cref="SafeMemberActions"/> est fourni (droit « Gérer les membres du safe » nécessaire côté PVWA).
/// </summary>
public partial class SafeMembersDialog : DialogWindow
{
    /// <summary>Droits affichés en colonnes ; les autres figurent dans le détail du membre sélectionné.</summary>
    private static readonly string[] Columns =
    [
        nameof(SafePermissions.ListAccounts),
        nameof(SafePermissions.UseAccounts),
        nameof(SafePermissions.RetrieveAccounts),
        nameof(SafePermissions.AddAccounts),
        nameof(SafePermissions.UpdateAccountContent),
        nameof(SafePermissions.UpdateAccountProperties),
        nameof(SafePermissions.DeleteAccounts),
        nameof(SafePermissions.ManageSafeMembers),
        nameof(SafePermissions.AccessWithoutConfirmation),
    ];

    private readonly string _safe = "";
    private readonly Func<CancellationToken, Task<List<SafeMember>>> _load = _ => Task.FromResult(new List<SafeMember>());
    private readonly SafeMemberActions? _actions;
    private readonly CancellationTokenSource _closing = new();
    private List<SafeMemberRow> _rows = [];

    public SafeMembersDialog() => InitializeComponent();

    /// <param name="actions">Ajout, modification et retrait de membres ; null = consultation seule.</param>
    public SafeMembersDialog(string safeName, Func<CancellationToken, Task<List<SafeMember>>> load, SafeMemberActions? actions = null)
        : this()
    {
        _safe = safeName;
        _load = load;
        _actions = actions;
        HeadingText.Text = Text.Format(Strings.SafeMembersHeading, safeName);
        BuildColumns();
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>Le PVWA a répondu que la session avait expiré : la fenêtre principale doit se déconnecter.</summary>
    public bool SessionExpired { get; private set; }

    private void BuildColumns()
    {
        // Texte « ✓ » plutôt que des cases : la copie (Ctrl+C) donne « ✓ » et pas « True ».
        var check = new FuncValueConverter<bool, string>(granted => granted ? "✓" : "");
        // En-tête sur plusieurs lignes ; le texte reste une chaîne pour que Ctrl+C copie le libellé.
        var wrapped = new FuncDataTemplate<string>((text, _) =>
        {
            var header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(header, text);
            return header;
        });
        MembersGrid.Columns.Add(new DataGridTextColumn
        {
            Header = Strings.SafeMemberColumn, Binding = GridBinding.OneWay(nameof(SafeMemberRow.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star),
        });
        MembersGrid.Columns.Add(new DataGridTextColumn { Header = Strings.SafeMemberTypeColumn, Binding = GridBinding.OneWay(nameof(SafeMemberRow.Type)) });
        MembersGrid.Columns.Add(new DataGridTextColumn { Header = Strings.SafeMemberUntilColumn, Binding = GridBinding.OneWay(nameof(SafeMemberRow.Until)) });
        foreach (var name in Columns)
        {
            var column = new DataGridTextColumn
            {
                Header = SafePermissionText.Label(name),
                HeaderTemplate = wrapped,
                Binding = GridBinding.OneWay($"{nameof(SafeMemberRow.Permissions)}.{name}", check),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 60,
            };
            column.CellStyleClasses.Add("Check");
            MembersGrid.Columns.Add(column);
        }
    }

    // Chargement ou retrait en cours : Ajouter, Modifier et Retirer sont grisés (deux opérations se croiseraient, sur un
    // tableau peut-être périmé). Seul le dernier chargement lancé est affiché.
    private int _loads;
    private bool _loading;
    private bool _removing;

    private void UpdateManageButtons() => ManagePanel.IsEnabled = !_loading && !_removing;

    internal async Task LoadAsync()
    {
        int load = ++_loads;
        _loading = true;
        UpdateManageButtons();
        ErrorMessage.IsVisible = RetryButton.IsVisible = false;
        StatePanel.IsVisible = LoadingBar.IsVisible = true;
        StatusText.Text = Strings.SafeMembersLoading;
        StatusText.IsVisible = true;
        try
        {
            var members = await _load(_closing.Token);
            if (load != _loads)
            {
                // Un chargement plus récent a été lancé entre-temps : c'est lui qui s'affiche.
                return;
            }

            _rows = members
                .OrderBy(m => m.IsGroup ? 0 : 1)
                .ThenBy(m => m.MemberName, StringComparer.OrdinalIgnoreCase)
                .Select(m => new SafeMemberRow(m))
                .ToList();
            ShowRows();
        }
        catch (Exception ex) when (load != _loads && ex is not OutOfMemoryException)
        {
            // Erreur d'un chargement remplacé par un plus récent : sans objet.
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Fenêtre fermée pendant le chargement.
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            SessionExpired = true;
            Close();
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            ShowError(Strings.SafeMembersForbidden);
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            ShowError(Strings.SafeMembersNotFound);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(Text.Format(Strings.SafeMembersFailed, ErrorText.Describe(ex)));
        }
        finally
        {
            if (load == _loads)
            {
                _loading = false;
                UpdateManageButtons();
            }
        }
    }

    private void ShowRows()
    {
        var canAdd = _rows.Where(r => r.Permissions.AddAccounts).ToList();
        StatusText.Text = Text.Format(Strings.SafeMembersCount, _rows.Count);
        CanAddText.Text = canAdd.Count == 0
            ? Strings.SafeMembersNobodyCanAdd
            : Text.Format(Strings.SafeMembersCanAdd, string.Join(", ", canAdd.Select(r => r.Member.IsGroup ? r.Name + Strings.SafeMembersGroupSuffix : r.Name)));
        SummaryPanel.IsVisible = true;
        MembersGrid.IsVisible = HintText.IsVisible = true;
        StatePanel.IsVisible = false;
        ManagePanel.IsVisible = _actions is not null;
        ApplyFilter();
    }

    private void ApplyFilter() =>
        MembersGrid.ItemsSource = OnlyAddBox.IsChecked == true ? _rows.Where(r => r.Permissions.AddAccounts).ToList() : _rows;

    /// <summary>Erreur au centre, avec « Réessayer » ; l'astuce de copie du tableau disparaît avec lui.</summary>
    private void ShowError(string message)
    {
        StatusText.IsVisible = LoadingBar.IsVisible = false;
        MembersGrid.IsVisible = HintText.IsVisible = SummaryPanel.IsVisible = false;
        StatePanel.IsVisible = true;
        ErrorMessage.Text = message;
        ErrorMessage.IsVisible = RetryButton.IsVisible = true;
        FocusLater(RetryButton);
    }

    private async void OnRetry(object? sender, RoutedEventArgs e) => await LoadAsync();

    private void OnOnlyAddClick(object? sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>Une ligne sur deux sur un fond teinté (le tableau d'Avalonia n'a pas d'AlternatingRowBackground).</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e) => e.Row.Classes.Set("Alternate", e.Row.Index % 2 == 1);

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Utilisateurs prédéfinis du coffre (Administrator, Auditors...) : gérés par le coffre lui-même.
        bool editable = MembersGrid.SelectedItems.Count == 1 && MembersGrid.SelectedItem is SafeMemberRow { Member.IsPredefinedUser: false };
        EditButton.IsEnabled = RemoveButton.IsEnabled = editable;
        if (MembersGrid.SelectedItem is not SafeMemberRow row)
        {
            RightsText.Text = "";
            return;
        }

        var granted = row.Permissions.Granted().Select(SafePermissionText.Label).ToList();
        RightsText.Text = Text.Format(Strings.SafeMembersRights, row.Name, granted.Count == 0 ? Strings.SafeMembersNoRight : string.Join(", ", granted));
    }

    private void OnGridDoubleClick(object? sender, TappedEventArgs e)
    {
        // Double-clic sur une ligne (pas sur un en-tête) ; bouton grisé pendant un chargement : rien.
        if (EditButton.IsEffectivelyEnabled && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null)
        {
            OnEditMember(sender, e);
        }
    }

    private async void OnAddMember(object? sender, RoutedEventArgs e)
    {
        if (_actions is not null)
        {
            await ShowMemberDialogAsync(new SafeMemberDialog(_safe, _actions.Add));
        }
    }

    private async void OnEditMember(object? sender, RoutedEventArgs e)
    {
        if (_actions is not null && MembersGrid.SelectedItem is SafeMemberRow row)
        {
            await ShowMemberDialogAsync(new SafeMemberDialog(_safe, row.Member, _actions.Update));
        }
    }

    private async Task ShowMemberDialogAsync(SafeMemberDialog dialog)
    {
        if (await dialog.ShowDialogAsync(this))
        {
            _ = LoadAsync();
        }
        else if (dialog.SessionExpired)
        {
            SessionExpired = true;
            Close();
        }
    }

    private async void OnRemoveMember(object? sender, RoutedEventArgs e)
    {
        if (_actions is null || MembersGrid.SelectedItem is not SafeMemberRow row
            || !await ConfirmDialog.DestructiveAsync(this, Strings.SafeMembersTitle, Text.Format(Strings.SafeMemberRemoveHeading, row.Name),
                Strings.SafeMemberRemoveAction, subject: Text.Format(Strings.SafeSubject, _safe), message: Strings.SafeMemberRemoveMessage))
        {
            return;
        }

        _removing = true;
        UpdateManageButtons();
        try
        {
            await _actions.Remove(row.Name, _closing.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Fenêtre fermée.
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            SessionExpired = true;
            Close();
        }
        catch (PvwaException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.SafeMemberForbidden, ex.Message), ConfirmKind.Warning, Strings.SafeMembersTitle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await ConfirmDialog.ShowMessageAsync(this, Text.Format(Strings.SafeMemberRemoveFailed, ErrorText.Describe(ex)), ConfirmKind.Warning,
                Strings.SafeMembersTitle);
        }
        finally
        {
            _removing = false;
            UpdateManageButtons();
        }
    }

    /// <summary>Ligne du tableau : membre, type et fin d'appartenance lisibles.</summary>
    public sealed class SafeMemberRow(SafeMember member)
    {
        public SafeMember Member { get; } = member;

        public string Name => Member.MemberName;

        public string Type => Member.IsGroup ? Strings.SafeMemberGroup : Strings.SafeMemberUser;

        public string Until => Member.Expires?.ToString("d", System.Globalization.CultureInfo.CurrentCulture) ?? "";

        public SafePermissions Permissions => Member.Permissions;
    }
}

/// <summary>Opérations de gestion des membres d'un safe (droit « Gérer les membres du safe »).</summary>
public sealed record SafeMemberActions(
    Func<SafeMemberChange, CancellationToken, Task> Add,
    Func<SafeMemberChange, CancellationToken, Task> Update,
    Func<string, CancellationToken, Task> Remove);
