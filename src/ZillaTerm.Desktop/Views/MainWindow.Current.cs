using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>Onglet « Mes serveurs » : serveurs de travail de l'utilisateur, rangés en dossiers, chacun avec sa configuration.</summary>
public partial class MainWindow
{
    private const string SavedDragFormat = "ZillaTerm.SavedItem";
    private const string AccountDragFormat = "ZillaTerm.Account";

    private readonly HashSet<string> _collapsedFolders = new(StringComparer.OrdinalIgnoreCase);
    private bool _savedTreeFiltered;
    private Point _dragStart;
    private object? _dragCandidate;
    // Appui qui peut commencer un glisser-déposer (Avalonia part de cet événement).
    private PointerPressedEventArgs? _dragPress;

    /// <summary>Formats du glisser-déposer interne : compte de « Disponibles », élément de « Mes serveurs ».</summary>
    private static readonly DataFormat<PvwaAccount> AccountDrag = DataFormat.CreateInProcessFormat<PvwaAccount>(AccountDragFormat);

    private static readonly DataFormat<object> SavedDrag = DataFormat.CreateInProcessFormat<object>(SavedDragFormat);

    private string PvwaHost => _client?.BaseUri.Host ?? "";

    private void RefreshSaved()
    {
        RememberSavedExpansion();

        // Coffres KeePass en tête ; serveurs CyberArk ensuite (pas en accès d'urgence).
        var filter = SavedSearchBox.Text?.Trim() ?? "";
        bool filtered = filter.Length > 0;
        var items = KeePassNodes(filtered ? filter : null);
        if (!IsOffline)
        {
            items.AddRange(SharedNodes(filtered ? filter : null));
            items.AddRange(Children(SessionLibrary.BuildTree(_settings, PvwaHost, filter), expandAll: filtered));
        }

        SavedTree.ItemsSource = items;
        _savedTreeFiltered = filtered;
        NoSavedText.Text = filtered ? Text.Format(Strings.SavedSearchNoMatch, filter)
            : IsOffline ? Strings.NoKeePassHelp
            : Strings.NoSavedHelp;
        NoSavedText.IsVisible = items.Count == 0;
    }

    /// <summary>
    /// Mémorise les dossiers repliés par l'utilisateur, sauf pendant une recherche : les dossiers des résultats sont
    /// alors tous dépliés, ce qui n'est pas un choix de l'utilisateur.
    /// </summary>
    private void RememberSavedExpansion()
    {
        if (!_savedTreeFiltered && SavedTree.ItemsSource is IEnumerable<object> previous)
        {
            RememberExpansion(previous);
            RememberSharedExpansion(previous);
        }
    }

    private List<object> Children(SessionFolderNode node, bool expandAll)
    {
        var items = new List<object>();
        foreach (var folder in node.Folders)
        {
            bool expanded = expandAll || !_collapsedFolders.Contains(folder.Path);
            items.Add(new SavedFolderNode(folder.Path, Children(folder, expandAll), expanded) { Count = folder.TotalSessions });
        }

        foreach (var session in node.Sessions)
        {
            items.Add(new SavedSessionNode(session, _byId.GetValueOrDefault(session.AccountId), _accountsLoaded)
            {
                IsMarked = _savedMarks.Contains(session),
                Tag = ServerTagView.Resolve(_settings.ServerTags, session.Tag),
            });
        }

        return items;
    }

    private void RememberExpansion(IEnumerable<object> nodes)
    {
        foreach (var node in nodes.Where(n => n is KeePassFolderNode or KeePassGroupNode))
        {
            RememberKeePassExpansion(node);
        }

        foreach (var folder in nodes.OfType<SavedFolderNode>())
        {
            if (folder.IsExpanded)
            {
                _collapsedFolders.Remove(folder.Path);
            }
            else
            {
                _collapsedFolders.Add(folder.Path);
            }

            RememberExpansion(folder.Children);
        }
    }

    private void Expand(string folder)
    {
        for (var path = SessionFolders.Normalize(folder); path.Length > 0; path = SessionFolders.Parent(path))
        {
            _collapsedFolders.Remove(path);
        }
    }

    private void SaveAndRefreshSaved()
    {
        RememberSavedExpansion();
        SaveSettings();
        RefreshSaved();
    }

    // ===================== Recherche =====================

    /// <summary>
    /// Recherche dans « Mes serveurs » : appliquée quand la frappe marque une pause (comme dans « Disponibles »), pour ne
    /// pas reconstruire l'arbre à chaque lettre quand il y a beaucoup de serveurs ; tout de suite quand le champ est vidé.
    /// </summary>
    private void OnSavedSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _savedSearchDebounce.Stop();
        if (string.IsNullOrEmpty(SavedSearchBox.Text))
        {
            RefreshSaved();
        }
        else
        {
            _savedSearchDebounce.Start();
        }
    }

    /// <summary>Recherche en attente appliquée maintenant (Entrée, flèche vers le bas).</summary>
    private void FlushSavedSearch()
    {
        if (_savedSearchDebounce.IsEnabled)
        {
            _savedSearchDebounce.Stop();
            RefreshSaved();
        }
    }

    private void OnSavedSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter)
        {
            FlushSavedSearch();
            // Comme dans « Disponibles » : premier résultat sélectionné, Entrée une seconde fois pour se connecter.
            SavedTree.UpdateLayout();
            if (FirstSavedLeaf(SavedTree) is { } item)
            {
                SavedTree.SelectedItem = item.DataContext;
                item.Focus(NavigationMethod.Directional);
                item.BringIntoView();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SavedSearchBox.Text))
        {
            SavedSearchBox.Clear();
            e.Handled = true;
        }
    }

    /// <summary>Premier serveur ou entrée KeePass visible, dans les dossiers dépliés seulement.</summary>
    private static TreeViewItem? FirstSavedLeaf(ItemsControl parent)
    {
        foreach (var data in parent.Items)
        {
            if (data is null || parent.ContainerFromItem(data) is not TreeViewItem item)
            {
                continue;
            }

            if (data is SavedSessionNode or SharedServerNode or KeePassEntryNode)
            {
                return item;
            }

            if (item.IsExpanded && item.ItemCount > 0)
            {
                item.UpdateLayout();
                if (FirstSavedLeaf(item) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    // ===================== Sélection et connexion =====================

    private void OnSavedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = SavedTree.SelectedItem;
        // Modifier et Supprimer (en-tête) n'agissent que sur un serveur ou un dossier de « Mes serveurs ».
        EditSavedButton.IsEnabled = DeleteSavedButton.IsEnabled = selected is SavedSessionNode or SavedFolderNode;
        // Arbre reconstruit en arrière-plan (liste partagée modifiée par un collègue, Paramètres, verrouillage du
        // poste) : sa sélection disparaît, mais la cible choisie dans une autre liste reste.
        if (selected is not null || SavedTree.IsKeyboardFocusWithin)
        {
            SetCurrentFrom(selected);
        }
    }

    private void OnSavedItemDoubleClick(object? sender, TappedEventArgs e)
    {
        // Seul l'élément double-cliqué compte (pas le dossier qui le contient).
        if (ItemUnder<TreeViewItem>(e.Source) is not { IsSelected: true } item)
        {
            return;
        }

        if (item.DataContext is SavedSessionNode node)
        {
            e.Handled = true;
            ConnectSaved(node.Session, advanced: false);
        }
        else if (item.DataContext is SharedServerNode shared)
        {
            e.Handled = true;
            ConnectSaved(shared.Session, advanced: false);
        }
        else if (item.DataContext is KeePassEntryNode or KeePassHintNode or KeePassFolderNode { IsUnlocked: false })
        {
            e.Handled = true;
            ActivateKeePassNode(item.DataContext);
        }
    }

    private void OnSavedKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SavedTree.SelectedItem is SavedSessionNode node:
                e.Handled = true;
                ConnectSaved(node.Session, advanced: false);
                break;
            case Key.Enter when SavedTree.SelectedItem is SharedServerNode shared:
                e.Handled = true;
                ConnectSaved(shared.Session, advanced: false);
                break;
            case Key.Delete when SavedTree.SelectedItem is SharedServerNode or SharedFolderNode:
                e.Handled = true;
                _ = RemoveSharedAsync(SavedTree.SelectedItem);
                break;
            case Key.F2 or Key.Delete when SavedTree.SelectedItem is SharedListNode:
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = ActivateKeePassNode(SavedTree.SelectedItem);
                break;
            case Key.F2 when SavedTree.SelectedItem is KeePassEntryNode entry:
                e.Handled = true;
                _ = EditKeePassEntryAsync(entry);
                break;
            case Key.Delete when SavedTree.SelectedItem is KeePassEntryNode entry:
                e.Handled = true;
                _ = DeleteKeePassEntryAsync(entry);
                break;
            case Key.F2:
                e.Handled = true;
                _ = RenameSelectedSavedAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                _ = DeleteSelectedSavedAsync();
                break;
            case Key.Escape when _savedMarks.Count > 0:
                e.Handled = true;
                ClearSavedMarks();
                break;
        }
    }

    /// <summary>Ouvre le serveur avec sa configuration (mode, composant, machine, motif, dossier SFTP).</summary>
    private void ConnectSaved(SavedSession saved, bool advanced) => _ = ConnectSavedAsync(saved, advanced);

    private async Task ConnectSavedAsync(SavedSession saved, bool advanced)
    {
        // Serveur d'une liste partagée créée pour un autre PVWA : son ID de compte ne vaut pas ici.
        if (!SessionLibrary.IsForHost(saved, PvwaHost))
        {
            SetStatus(Text.Format(Strings.SharedOtherPvwaConnect, saved.Name, saved.PvwaHost, PvwaHost), isError: true);
            return;
        }

        if (!_byId.TryGetValue(saved.AccountId, out var account))
        {
            SetStatus(MissingAccountText(Text.Format(Strings.SavedAccountGone, saved.Name)), isError: true);
            return;
        }

        if (!await ConfirmSharedTargetAsync(saved, account))
        {
            return;
        }

        await ConnectAsync(account, SavedRequest(saved, account), showDialog: advanced, saved: saved);
    }

    private void OnSafeMembersOfSelectedSaved(object? sender, RoutedEventArgs e)
    {
        switch (SavedTree.SelectedItem)
        {
            case SavedSessionNode node:
                _ = ShowSafeMembersAsync(node.Account?.SafeName ?? node.Session.SafeName);
                break;
            case SharedServerNode shared:
                _ = ShowSafeMembersAsync(shared.Account?.SafeName ?? shared.Session.SafeName);
                break;
        }
    }

    private void OnConnectSelectedSaved(object? sender, RoutedEventArgs e)
    {
        if (SelectedSavedSession() is { } session)
        {
            ConnectSaved(session, advanced: false);
        }
    }

    private void OnConnectSelectedSavedAdvanced(object? sender, RoutedEventArgs e)
    {
        if (SelectedSavedSession() is { } session)
        {
            ConnectSaved(session, advanced: true);
        }
    }

    /// <summary>Serveur sélectionné, de « Mes serveurs » ou d'une liste partagée.</summary>
    private SavedSession? SelectedSavedSession() => SavedTree.SelectedItem switch
    {
        SavedSessionNode node => node.Session,
        SharedServerNode shared => shared.Session,
        _ => null,
    };

    // ===================== Ajout depuis « Disponibles » =====================

    private void OnAddToCurrent(object? sender, RoutedEventArgs e)
    {
        if (_current is { } account && _currentSaved is null)
        {
            _ = AddToCurrentAsync(account, SelectedSavedFolder() ?? _settings.KeepChosenServerFolder, chooseFolder: true);
        }
    }

    /// <summary>« Ajouter à Mes serveurs » du menu d'un compte : fenêtre du dossier et de l'étiquette.</summary>
    private void OnAddAccountToCurrent(object? sender, RoutedEventArgs e)
    {
        if (_current is { } account)
        {
            _ = AddToCurrentAsync(account, _settings.KeepChosenServerFolder, chooseFolder: true);
        }
    }

    private string? SelectedSavedFolder() => SavedTree.SelectedItem switch
    {
        SavedFolderNode folder => folder.Path,
        SavedSessionNode node => node.Session.Folder,
        _ => null,
    };

    /// <summary>
    /// Compte ajouté à « Mes serveurs » : une fenêtre demande le dossier et l'étiquette, et pour un compte de domaine le
    /// serveur (facultatif : sans serveur, il le sera à chaque connexion). Glissé sur un dossier
    /// (<paramref name="chooseFolder"/> faux), un compte d'un serveur est ajouté sans fenêtre s'il n'y a pas d'étiquette à
    /// choisir.
    /// </summary>
    private async Task AddToCurrentAsync(PvwaAccount account, string folder, bool chooseFolder)
    {
        bool askServer = AccountClassifier.NeedsRemoteMachine(account, _domains);
        if (!askServer && !chooseFolder && _settings.ServerTags.Count == 0)
        {
            ShowAddedToCurrent(SessionLibrary.AddSession(_settings, account, PvwaHost, folder, HasPsmp), null);
            return;
        }

        var accountName = $"{account.UserName}@{account.Address}";
        if (await AskAddAsync(account, folder, askServer ? null : accountName,
            server => server.Length > 0 ? ($"{account.UserName}@{server}", server) : (accountName, null)) is not { } choice)
        {
            return;
        }

        // Composant non figé : celui retenu pour la plateforme au moment de la connexion, comme sans serveur.
        ShowAddedToCurrent(askServer && choice.Server.Length > 0
            ? SessionLibrary.AddConnection(_settings, account, PvwaHost, choice.Folder, AccountClassifier.DefaultMode(account, HasPsmp),
                null, choice.Server)
            : SessionLibrary.AddSession(_settings, account, PvwaHost, choice.Folder, HasPsmp), choice.ChosenTag);
    }

    /// <summary>
    /// Fenêtre « Ajouter à « Mes serveurs » » : dossier (le dernier choisi proposé) et étiquette (devinée d'après le nom,
    /// l'adresse, le safe ou le dossier), et serveur si <paramref name="name"/> est null ; null si elle est annulée.
    /// </summary>
    /// <param name="target">Nom et machine cible du serveur ajouté, d'après le serveur saisi (vide sans question).</param>
    private async Task<ServerPromptDialog?> AskAddAsync(PvwaAccount account, string folder, string? name, Func<string, (string Name, string? Machine)> target)
    {
        var dialog = new ServerPromptDialog(account, name is null ? SessionLibrary.KnownMachines(_settings, account, PvwaHost) : [],
            _settings.SessionFolderList.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), _ => false, keep: false, folder,
            adding: true, AccountClassifier.IsDomainAccount(account, _domains), _settings.ServerTags,
            (server, inFolder) => GuessTag(account, target(server), inFolder), name);
        if (!await dialog.ShowDialogAsync(this))
        {
            return null;
        }

        RememberFolder(dialog.Folder);
        return dialog;
    }

    /// <summary>
    /// Fenêtre « Choisir le serveur » d'un compte de domaine à la connexion ; null si elle est annulée. Le choix « Garder
    /// dans « Mes serveurs » » et le dossier sont mémorisés ; l'étiquette du serveur gardé y est choisie aussi.
    /// </summary>
    private async Task<ServerPromptDialog?> AskServerAsync(PvwaAccount account)
    {
        var dialog = new ServerPromptDialog(account, SessionLibrary.KnownMachines(_settings, account, PvwaHost),
            _settings.SessionFolderList.OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            server => SessionLibrary.FindSession(_settings, account, PvwaHost, server) is not null,
            _settings.KeepChosenServer, _settings.KeepChosenServerFolder, adding: false, AccountClassifier.IsDomainAccount(account, _domains),
            _settings.ServerTags, (server, folder) => GuessTag(account, ($"{account.UserName}@{server}", server), folder));
        if (!await dialog.ShowDialogAsync(this))
        {
            return null;
        }

        if (dialog.KeepChoice != _settings.KeepChosenServer)
        {
            _settings.KeepChosenServer = dialog.KeepChoice;
            SaveSettings();
        }

        if (dialog.Keep)
        {
            RememberFolder(dialog.Folder);
        }

        return dialog;
    }

    /// <summary>Dernier dossier choisi pour ajouter un serveur : proposé la fois suivante.</summary>
    private void RememberFolder(string folder)
    {
        if (folder != _settings.KeepChosenServerFolder)
        {
            _settings.KeepChosenServerFolder = folder;
            SaveSettings();
        }
    }

    /// <summary>Étiquette (PROD, QA, DEV…) devinée d'après le nom, la machine cible, l'adresse, le safe ou le dossier.</summary>
    private string? GuessTag(PvwaAccount account, (string Name, string? Machine) target, string folder) =>
        ServerTagGuess.ForServer(_settings.ServerTags, target.Name, target.Machine, account.Address, account.SafeName, folder)?.Name;

    /// <summary>Connexion récente ajoutée à « Mes serveurs » avec son mode, son composant et sa machine cible.</summary>
    private async Task AddToCurrentAsync(PvwaAccount account, RecentSession recent)
    {
        var machine = string.IsNullOrWhiteSpace(recent.RemoteMachine) ? null : recent.RemoteMachine.Trim();
        var name = string.IsNullOrWhiteSpace(recent.Label) ? $"{account.UserName}@{account.Address}" : recent.Label.Trim();
        if (await AskAddAsync(account, _settings.KeepChosenServerFolder, name, _ => (name, machine)) is { } choice)
        {
            ShowAddedToCurrent(SessionLibrary.AddFromRecent(_settings, account, recent, PvwaHost, choice.Folder), choice.ChosenTag);
        }
    }

    /// <summary>Session ouverte ajoutée à « Mes serveurs » avec son type, son composant et sa machine cible.</summary>
    private async Task AddToCurrentAsync(PvwaAccount account, ConnectRequest request)
    {
        var name = request.RemoteMachine is { } machine ? $"{account.UserName}@{machine}" : $"{account.UserName}@{account.Address}";
        if (await AskAddAsync(account, _settings.KeepChosenServerFolder, name, _ => (name, request.RemoteMachine)) is { } choice)
        {
            ShowAddedToCurrent(SessionLibrary.AddConnection(_settings, account, PvwaHost, choice.Folder, request.Mode, request.Component,
                request.RemoteMachine), choice.ChosenTag);
        }
    }

    private void ShowAddedToCurrent(SavedSession session, string? tag)
    {
        session.Tag = tag;
        Expand(session.Folder);
        SaveAndRefreshSaved();
        SetStatus(session.Folder.Length > 0
            ? Text.Format(Strings.AddedToMyServersFolder, session.Name, session.Folder)
            : Text.Format(Strings.AddedToMyServers, session.Name));
    }

    private static MenuItem MenuEntry(string text, Action action)
    {
        // TextBlock plutôt que texte brut : un « _ » dans un nom de dossier n'est pas un raccourci clavier.
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        item.Click += (_, _) => action();
        return item;
    }

    // ===================== Dossiers =====================

    /// <summary>Nouveau dossier dans le dossier sélectionné (menu « Nouveau sous-dossier », bouton, menu de la liste).</summary>
    private void OnNewFolder(object? sender, RoutedEventArgs e) =>
        _ = CreateFolderAsync(SavedTree.SelectedItem is SavedFolderNode folder ? folder.Path : "");

    private async Task CreateFolderAsync(string parent)
    {
        if (await AskFolderNameAsync(parent.Length == 0 ? Strings.NewFolder : Text.Format(Strings.NewFolderIn, parent), parent, "") is { } path)
        {
            SessionLibrary.AddFolder(_settings, path);
            Expand(path);
            SaveAndRefreshSaved();
        }
    }

    /// <summary>Demande un nom de dossier sous <paramref name="parent"/> ; renvoie le chemin complet.</summary>
    private async Task<string?> AskFolderNameAsync(string title, string parent, string initial, string? except = null)
    {
        var dialog = new InputDialog(title, Strings.FolderNameLabel, initial, value =>
        {
            if (value.Contains('/') || value.Contains('\\'))
            {
                return Strings.FolderNameNoSlashes;
            }

            var path = SessionFolders.Combine(parent, value);
            bool exists = _settings.SessionFolderList.Contains(path, StringComparer.OrdinalIgnoreCase);
            return exists && !string.Equals(path, except, StringComparison.OrdinalIgnoreCase) ? Strings.FolderExists : null;
        });
        return await dialog.ShowDialogAsync(this) ? SessionFolders.Combine(parent, dialog.Value) : null;
    }

    // ===================== Propriétés, renommage, suppression =====================

    private async void OnEditSelectedSaved(object? sender, RoutedEventArgs e)
    {
        switch (SavedTree.SelectedItem)
        {
            case SavedSessionNode node:
                var dialog = new SessionPropertiesDialog(node.Session, node.Account, _settings.SessionFolderList, HasPsmp,
                    _settings.KnownComponents(node.Account?.PlatformId ?? node.Session.PlatformId),
                    _settings.X11Servers.Contains(node.Session.Id), _settings.ServerTags);
                if (await dialog.ShowDialogAsync(this))
                {
                    // Transfert X11 : réglage personnel, hors du serveur (jamais exporté ni partagé).
                    _settings.X11Servers.Remove(node.Session.Id);
                    if (dialog.X11Forwarding)
                    {
                        _settings.X11Servers.Add(node.Session.Id);
                    }

                    SessionLibrary.AddFolder(_settings, node.Session.Folder);
                    Expand(node.Session.Folder);
                    SaveAndRefreshSaved();
                    SetStatus(Text.Format(Strings.SavedSettingsSaved, node.Session.Name));
                }

                break;
            case SavedFolderNode:
                await RenameSelectedSavedAsync();
                break;
        }
    }

    private void OnRenameSelectedSaved(object? sender, RoutedEventArgs e) => _ = RenameSelectedSavedAsync();

    private async Task RenameSelectedSavedAsync()
    {
        switch (SavedTree.SelectedItem)
        {
            case SavedSessionNode node:
                var dialog = new InputDialog(Strings.RenameTitle, Strings.DisplayNameLabel, node.Session.Name);
                if (await dialog.ShowDialogAsync(this))
                {
                    node.Session.Name = dialog.Value;
                    SaveAndRefreshSaved();
                }

                break;
            case SavedFolderNode folder:
                if (await AskFolderNameAsync(Strings.RenameFolderTitle, SessionFolders.Parent(folder.Path), folder.Name, except: folder.Path) is { } path)
                {
                    // Dossiers repliés : ils le restent sous leur nouveau nom (sous-dossiers compris).
                    RememberSavedExpansion();
                    SessionLibrary.RenameFolder(_settings, folder.Path, SessionFolders.Name(path));
                    foreach (var collapsed in _collapsedFolders.Where(p => SessionFolders.IsWithin(p, folder.Path)).ToList())
                    {
                        _collapsedFolders.Remove(collapsed);
                        _collapsedFolders.Add(SessionFolders.Rebase(collapsed, folder.Path, path));
                    }

                    SaveSettings();
                    RefreshSaved();
                }

                break;
        }
    }

    private void OnDeleteSelectedSaved(object? sender, RoutedEventArgs e) => _ = DeleteSelectedSavedAsync();

    /// <summary>
    /// Menu de « Mes serveurs » : celui de l'élément cliqué (sélectionné par le clic droit), choisi à chaque ouverture
    /// selon sa nature ; dans le vide, celui de la liste (nouveau dossier, base KeePass).
    /// </summary>
    private void OnSavedTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var item = ItemUnder<TreeViewItem>(e.Source);
        var key = item is null ? "SavedBackgroundMenu" : SavedMenuKey(item.DataContext);
        if (key is null || !this.TryFindResource(key, out var resource) || resource is not ContextMenu menu)
        {
            return;
        }

        Control target = item ?? SavedTree;
        menu.PlacementTarget = target;
        menu.Open(target);
    }

    private static string? SavedMenuKey(object? node) => node switch
    {
        KeePassFolderNode or KeePassHintNode => "KeePassFolderMenu",
        KeePassGroupNode => "KeePassGroupMenu",
        KeePassEntryNode => "KeePassEntryMenu",
        SharedListNode => "SharedListMenu",
        SharedFolderNode => "SharedFolderMenu",
        SharedServerNode => "SharedServerMenu",
        SavedFolderNode => "SavedFolderMenu",
        SavedSessionNode => "SavedSessionMenu",
        _ => null,
    };

    private async Task DeleteSelectedSavedAsync()
    {
        switch (SavedTree.SelectedItem)
        {
            case SavedSessionNode node:
                if (await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
                    {
                        Title = Strings.MyServers,
                        Heading = Text.Format(Strings.RemoveSavedHeading, node.Session.Name),
                        Message = Strings.RemoveSavedMessage,
                        Actions = [Strings.ActionRemove],
                    }))
                {
                    _settings.Sessions.Remove(node.Session);
                    SaveAndRefreshSaved();
                }

                break;
            case SavedFolderNode folder:
                // Tous les serveurs du dossier, y compris ceux que la recherche masque.
                int count = SessionLibrary.CountInFolder(_settings, folder.Path, PvwaHost);
                if (await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
                    {
                        Title = Strings.MyServers,
                        Heading = Text.Format(Strings.DeleteFolderHeading, folder.Path),
                        Message = count == 0 ? null : Text.Format(Strings.DeleteFolderMessage, count),
                        Kind = count == 0 ? ConfirmKind.Question : ConfirmKind.Warning,
                        Actions = [Strings.DeleteFolderAction],
                        DangerAction = count == 0 ? -1 : 0,
                    }))
                {
                    SessionLibrary.DeleteFolder(_settings, folder.Path, PvwaHost);
                    SaveAndRefreshSaved();
                }

                break;
            case SharedServerNode or SharedFolderNode:
                _ = RemoveSharedAsync(SavedTree.SelectedItem);
                break;
        }
    }

    // ===================== Glisser-déposer =====================

    /// <summary>Appui sur un élément : point de départ d'un éventuel glisser (comptes de « Disponibles », « Mes serveurs »).</summary>
    private void OnDragSourcePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragCandidate = ItemUnder<TreeViewItem>(e.Source)?.DataContext;
        _dragPress = e;
    }

    private async void OnAvailableDragMove(object? sender, PointerEventArgs e)
    {
        if (ShouldStartDrag(e) && _dragCandidate is AccountNode node && _dragPress is { } press)
        {
            _dragCandidate = null;
            _dragPress = null;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(AccountDrag, node.Account));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }
    }

    private async void OnSavedDragMove(object? sender, PointerEventArgs e)
    {
        if (ShouldStartDrag(e) && _dragCandidate is SavedSessionNode or SavedFolderNode && _dragPress is { } press)
        {
            var item = _dragCandidate!;
            _dragCandidate = null;
            _dragPress = null;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(SavedDrag, item));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
    }

    private bool ShouldStartDrag(PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dragCandidate is null)
        {
            return false;
        }

        // Même seuil que Windows (4 pixels).
        var delta = e.GetPosition(this) - _dragStart;
        return Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4;
    }

    /// <summary>Un compte glissé depuis « Disponibles » sur « Mes serveurs » ouvre cet onglet.</summary>
    private void OnCurrentTabDragEnter(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(AccountDrag))
        {
            SideTabs.SelectedItem = CurrentTab;
        }
    }

    private void OnSavedDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(AccountDrag) ? DragDropEffects.Copy
            : e.DataTransfer.Contains(SavedDrag) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSavedDrop(object? sender, DragEventArgs e)
    {
        var over = ItemUnder<TreeViewItem>(e.Source)?.DataContext;
        if (SharedListOf(over) is { } sharedList)
        {
            await DropOnSharedAsync(sharedList, over!, e.DataTransfer);
            return;
        }

        var target = over switch
        {
            SavedFolderNode folder => folder.Path,
            SavedSessionNode node => node.Session.Folder,
            _ => "",
        };

        if (e.DataTransfer.TryGetValue(AccountDrag) is { } account)
        {
            await AddToCurrentAsync(account, target, chooseFolder: false);
            return;
        }

        switch (e.DataTransfer.TryGetValue(SavedDrag))
        {
            case SavedSessionNode node when !string.Equals(node.Session.Folder, target, StringComparison.OrdinalIgnoreCase):
                SessionLibrary.MoveSession(_settings, node.Session, target);
                break;
            case SavedFolderNode folder:
                var destination = SessionFolders.Combine(target, folder.Name);
                if (string.Equals(destination, folder.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    SessionLibrary.MoveFolder(_settings, folder.Path, destination);
                }
                catch (ArgumentException ex)
                {
                    SetStatus(ex.Message, isError: true);
                    return;
                }

                break;
            default:
                return;
        }

        Expand(target);
        SaveAndRefreshSaved();
    }

    /// <summary>Glisser-déposer de « Disponibles » vers « Mes serveurs », et dans « Mes serveurs ».</summary>
    private void InitDragDrop()
    {
        SessionTree.AddHandler(PointerPressedEvent, OnDragSourcePressed, RoutingStrategies.Tunnel);
        SessionTree.AddHandler(PointerMovedEvent, OnAvailableDragMove, RoutingStrategies.Tunnel);
        SavedTree.AddHandler(PointerMovedEvent, OnSavedDragMove, RoutingStrategies.Tunnel);
        SavedTree.AddHandler(DragDrop.DragOverEvent, OnSavedDragOver);
        SavedTree.AddHandler(DragDrop.DropEvent, OnSavedDrop);
        CurrentTab.AddHandler(DragDrop.DragEnterEvent, OnCurrentTabDragEnter);
    }
}
