using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

/// <summary>
/// Plusieurs serveurs « Mes serveurs » à la fois : Ctrl+clic (ou Ctrl+Espace) ajoute ou retire un serveur (ou tous ceux
/// d'un dossier), Maj+clic (ou Maj+↑/↓) choisit une suite de serveurs ; clic droit (ou touche Menu) → « Ouvrir en vue
/// parallèle » ou « Se connecter aux N serveurs ». Un dossier s'ouvre aussi d'un coup (clic droit sur le dossier). Seuls
/// les serveurs SSH vont dans la vue parallèle.
/// </summary>
public partial class MainWindow
{
    // Serveurs choisis ; gardés par serveur (et non par nœud) car l'arbre est reconstruit à chaque changement.
    private readonly HashSet<SavedSession> _savedMarks = [];
    private SavedSession? _savedAnchor;

    /// <summary>Serveurs choisis, dans l'ordre de l'arbre.</summary>
    internal IReadOnlyList<SavedSession> MarkedSaved => VisibleSavedNodes(includeCollapsed: true)
        .Select(n => n.Session).Where(_savedMarks.Contains).ToList();

    private void OnSavedMouseDown(object sender, MouseButtonEventArgs e)
    {
        var node = ItemUnder<TreeViewItem>(e.OriginalSource)?.DataContext;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control) && node is SavedSessionNode or SavedFolderNode)
        {
            ToggleMarks(node);
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift) && node is SavedSessionNode target)
        {
            MarkRange(target.Session);
            e.Handled = true;
            return;
        }

        // Clic simple : la sélection de plusieurs serveurs est abandonnée.
        if (node is not null && _savedMarks.Count > 0)
        {
            ClearSavedMarks();
        }

        OnDragSourceMouseDown(sender, e);
    }

    /// <summary>
    /// Au clavier comme à la souris : Ctrl+Espace vaut Ctrl+clic sur l'élément sélectionné ; Maj+↑/↓ déplace la sélection
    /// (comme ↑/↓, qui gardent les serveurs choisis), puis choisit la suite de serveurs depuis celui de départ, comme
    /// Maj+clic.
    /// </summary>
    private void OnSavedPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Space && modifiers == ModifierKeys.Control && SavedTree.SelectedItem is SavedSessionNode or SavedFolderNode)
        {
            ToggleMarks(SavedTree.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && modifiers == ModifierKeys.Shift && SavedTree.SelectedItem is not null)
        {
            // Le TreeView déplace la sélection pendant cette touche : la suite est choisie juste après.
            _savedAnchor ??= (SavedTree.SelectedItem as SavedSessionNode)?.Session;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (SavedTree.SelectedItem is SavedSessionNode target)
                {
                    _savedAnchor ??= target.Session;
                    MarkRange(target.Session);
                }
            });
        }
    }

    /// <summary>Ctrl+clic : le serveur (ou les serveurs du dossier) entre dans la sélection, ou en sort.</summary>
    private void ToggleMarks(object node)
    {
        // Le premier Ctrl+clic garde le serveur déjà sélectionné, comme dans l'Explorateur.
        if (_savedMarks.Count == 0 && SavedTree.SelectedItem is SavedSessionNode current && !ReferenceEquals(current, node))
        {
            _savedMarks.Add(current.Session);
        }

        var sessions = node switch
        {
            SavedSessionNode server => [server.Session],
            SavedFolderNode folder => FolderServers(folder),
            _ => new List<SavedSession>(),
        };
        bool add = sessions.Any(s => !_savedMarks.Contains(s));
        foreach (var session in sessions)
        {
            if (add)
            {
                _savedMarks.Add(session);
            }
            else
            {
                _savedMarks.Remove(session);
            }
        }

        _savedAnchor = node is SavedSessionNode anchor ? anchor.Session : _savedAnchor;
        UpdateSavedMarks();
    }

    /// <summary>Maj+clic : tous les serveurs affichés entre le dernier choisi et celui-ci.</summary>
    private void MarkRange(SavedSession target)
    {
        var visible = VisibleSavedNodes(includeCollapsed: false).Select(n => n.Session).ToList();
        var anchor = _savedAnchor ?? (SavedTree.SelectedItem as SavedSessionNode)?.Session ?? target;
        int from = visible.IndexOf(anchor);
        int to = visible.IndexOf(target);
        if (from < 0)
        {
            from = to;
        }

        _savedMarks.Clear();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to) && i >= 0; i++)
        {
            _savedMarks.Add(visible[i]);
        }

        UpdateSavedMarks();
    }

    private void ClearSavedMarks()
    {
        _savedMarks.Clear();
        _savedAnchor = null;
        UpdateSavedMarks();
    }

    /// <summary>Coches de l'arbre et rappel dans la barre d'état.</summary>
    private void UpdateSavedMarks()
    {
        foreach (var node in VisibleSavedNodes(includeCollapsed: true))
        {
            node.IsMarked = _savedMarks.Contains(node.Session);
        }

        if (_savedMarks.Count > 0)
        {
            SetStatus(Text.Format(Strings.SavedMarkedStatus, _savedMarks.Count));
        }
    }

    /// <summary>Serveurs de l'arbre « Mes serveurs », dans l'ordre affiché (dossiers repliés compris ou non).</summary>
    private IEnumerable<SavedSessionNode> VisibleSavedNodes(bool includeCollapsed)
    {
        IEnumerable<SavedSessionNode> Walk(IEnumerable<object> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case SavedSessionNode server:
                        yield return server;
                        break;
                    case SavedFolderNode folder when includeCollapsed || folder.IsExpanded:
                        foreach (var child in Walk(folder.Children))
                        {
                            yield return child;
                        }

                        break;
                }
            }
        }

        return SavedTree.ItemsSource is IEnumerable<object> roots ? Walk(roots) : [];
    }

    /// <summary>Serveurs d'un dossier et de ses sous-dossiers.</summary>
    private static List<SavedSession> FolderServers(SavedFolderNode folder)
    {
        var servers = new List<SavedSession>();
        foreach (var child in folder.Children)
        {
            if (child is SavedSessionNode server)
            {
                servers.Add(server.Session);
            }
            else if (child is SavedFolderNode sub)
            {
                servers.AddRange(FolderServers(sub));
            }
        }

        return servers;
    }

    /// <summary>Serveurs visés par le menu d'un serveur : la sélection s'il en fait partie, sinon lui seul.</summary>
    private List<SavedSession> MenuServers() =>
        SavedTree.SelectedItem is SavedSessionNode node && _savedMarks.Contains(node.Session) && _savedMarks.Count > 1
            ? MarkedSaved.ToList()
            : SavedTree.SelectedItem is SavedSessionNode single ? [single.Session] : [];

    private void OnSavedSessionMenuOpened(object sender, RoutedEventArgs e)
    {
        var servers = MenuServers();
        bool many = servers.Count > 1;
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
        {
            switch (item.Tag)
            {
                case "parallel":
                    item.Header = many ? Text.Format(Strings.MenuOpenManyInParallel, servers.Count) : Strings.MenuOpenInParallel;
                    item.IsEnabled = servers.Any(s => s.Mode == ConnectMode.Ssh) && HasPsmp && !IsOffline;
                    break;
                case "connectmany":
                    item.Header = Text.Format(Strings.MenuConnectMany, servers.Count);
                    item.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "connect":
                    item.FontWeight = many ? FontWeights.Normal : FontWeights.SemiBold;
                    break;
                case "share":
                    BuildShareMenu(item, () => servers);
                    break;
                case "tag":
                    BuildTagMenu(item, servers);
                    break;
            }
        }
    }

    private void OnSavedFolderMenuOpened(object sender, RoutedEventArgs e)
    {
        var servers = SavedTree.SelectedItem is SavedFolderNode folder ? FolderServers(folder) : [];
        int ssh = servers.Count(s => s.Mode == ConnectMode.Ssh);
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
        {
            switch (item.Tag)
            {
                case "parallel":
                    item.Header = Text.Format(Strings.MenuOpenFolderInParallel, ssh);
                    item.IsEnabled = ssh > 0 && HasPsmp && !IsOffline;
                    break;
                case "connectmany":
                    item.Header = Text.Format(Strings.MenuConnectMany, servers.Count);
                    item.IsEnabled = servers.Count > 0 && !IsOffline;
                    break;
                case "share":
                    BuildShareMenu(item, () => servers);
                    item.IsEnabled &= servers.Count > 0;
                    break;
                case "tag":
                    BuildTagMenu(item, servers);
                    break;
            }
        }
    }

    /// <summary>
    /// Sous-menu « Étiquette » : chaque étiquette (cochée si tous les serveurs l'ont), « Aucune », et une proposition
    /// d'après le nom, l'adresse, le safe et le dossier pour les serveurs qui n'en ont pas encore.
    /// </summary>
    private void BuildTagMenu(MenuItem parent, List<SavedSession> servers)
    {
        parent.Items.Clear();
        parent.IsEnabled = servers.Count > 0;
        foreach (var tag in _settings.ServerTags)
        {
            var item = new MenuItem
            {
                Header = ServerTagView.Chip(tag, 11),
                IsCheckable = false,
                IsChecked = servers.Count > 0 && servers.All(s => string.Equals(s.Tag, tag.Name, StringComparison.OrdinalIgnoreCase)),
            };
            System.Windows.Automation.AutomationProperties.SetName(item, tag.Name);
            item.Click += (_, _) => SetTags(servers, tag.Name);
            parent.Items.Add(item);
        }

        var none = new MenuItem { Header = Strings.MenuTagNone, IsChecked = servers.Count > 0 && servers.All(s => s.Tag is null) };
        none.Click += (_, _) => SetTags(servers, null);
        parent.Items.Add(none);
        var untagged = servers.Where(s => s.Tag is null).ToList();
        if (_settings.ServerTags.Count > 0 && untagged.Count > 0)
        {
            parent.Items.Add(new Separator());
            var guess = new MenuItem { Header = Text.Format(Strings.MenuTagGuess, untagged.Count) };
            guess.Click += (_, _) => GuessTags(untagged);
            parent.Items.Add(guess);
        }
    }

    private void SetTags(List<SavedSession> servers, string? tag)
    {
        foreach (var server in servers)
        {
            server.Tag = tag;
        }

        SaveAndRefreshSaved();
        SetStatus(tag is null ? Text.Format(Strings.TagRemovedStatus, servers.Count) : Text.Format(Strings.TagGivenStatus, tag, servers.Count));
    }

    /// <summary>Étiquette proposée pour chaque serveur qui n'en a pas ; ceux sans proposition restent sans étiquette.</summary>
    private void GuessTags(List<SavedSession> servers)
    {
        int tagged = 0;
        foreach (var server in servers)
        {
            if (ServerTagGuess.ForServer(_settings.ServerTags, server.Name, server.RemoteMachine, server.Address, server.SafeName,
                    server.Folder) is { } tag)
            {
                server.Tag = tag.Name;
                tagged++;
            }
        }

        SaveAndRefreshSaved();
        SetStatus(Text.Format(Strings.TagGuessedStatus, tagged, servers.Count - tagged));
    }

    private async void OnOpenSavedInParallel(object sender, RoutedEventArgs e) => await OpenSavedAsync(MenuServers(), parallel: true);

    private async void OnConnectMarkedSaved(object sender, RoutedEventArgs e) => await OpenSavedAsync(MenuServers(), parallel: false);

    private async void OnOpenFolderInParallel(object sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is SavedFolderNode folder)
        {
            await OpenSavedAsync(FolderServers(folder), parallel: true);
        }
    }

    private async void OnConnectFolder(object sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is SavedFolderNode folder)
        {
            await OpenSavedAsync(FolderServers(folder), parallel: false);
        }
    }

    /// <summary>Requête de connexion d'un serveur « Mes serveurs », avec sa configuration.</summary>
    private ConnectRequest SavedRequest(SavedSession saved, PvwaAccount account)
    {
        var machines = AccountClassifier.RemoteMachineList(account);
        return new ConnectRequest(
            saved.Mode,
            string.IsNullOrWhiteSpace(saved.Component) ? _settings.ResolveComponent(account) : saved.Component,
            saved.RemoteMachine ?? (machines.Count == 1 ? machines[0] : null),
            saved.Reason,
            X11Forwarding: saved.Mode == ConnectMode.Ssh && _settings.X11Servers.Contains(saved.Id));
    }

    /// <summary>
    /// Ouvre plusieurs serveurs l'un après l'autre (chaque connexion est une session PSMP ou PSM distincte, avec ses
    /// questions habituelles). Vers la vue parallèle : les serveurs SSH seulement, dans la limite des places libres.
    /// </summary>
    private async Task OpenSavedAsync(IReadOnlyList<SavedSession> servers, bool parallel)
    {
        if (servers.Count == 0)
        {
            return;
        }

        var chosen = servers.ToList();
        int skipped = 0;
        if (parallel)
        {
            if (!HasPsmp)
            {
                SetStatus(Strings.SshUnavailable, isError: true);
                return;
            }

            chosen = servers.Where(s => s.Mode == ConnectMode.Ssh).ToList();
            skipped = servers.Count - chosen.Count;
            int room = ParallelLayout.MaxSessions - (_parallel?.Sessions.Count ?? 0);
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, Strings.ParallelNoSshServer, Strings.ParallelTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (room <= 0)
            {
                MessageBox.Show(this, Strings.ParallelFull, Strings.ParallelTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (chosen.Count > room)
            {
                var dialog = new ParallelDialog(chosen.Select(s => s.Name).ToList(), Enumerable.Range(0, room).ToList(), room,
                    Text.Format(Strings.ParallelChooseServers, chosen.Count, room), Strings.ParallelOpen) { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                chosen = dialog.SelectedIndexes.Select(i => chosen[i]).ToList();
            }
        }

        var opened = new List<SshSession>();
        int missing = 0;
        // Plusieurs sessions via le PSMP : le mot de passe peut être donné une fois pour toutes (au choix de l'utilisateur,
        // en mémoire, oublié dès qu'elles sont connectées, et au plus tard après 5 minutes).
        var group = chosen.Count > 1 ? new SshAnswerCache() : null;
        _openingGroup = group;
        if (group is not null)
        {
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => group.Clear(), TaskScheduler.Default);
        }

        try
        {
            foreach (var saved in chosen)
            {
                if (_loggedOff)
                {
                    // Session expirée ou fenêtre en cours de fermeture : les serveurs suivants ne s'ouvrent pas.
                    break;
                }

                if (!SessionLibrary.IsForHost(saved, PvwaHost) || !_byId.TryGetValue(saved.AccountId, out var account))
                {
                    missing++;
                    continue;
                }

                if (!ConfirmSharedTarget(saved, account))
                {
                    continue;
                }

                int before = _remoteSessions.Count;
                await ConnectAsync(account, SavedRequest(saved, account), saved: saved);
                // Vue parallèle : terminaux seulement (pas les sessions de fichiers seuls).
                if (_remoteSessions.Count > before && _remoteSessions[^1] is SshSession session)
                {
                    opened.Add(session);
                }
            }
        }
        finally
        {
            _openingGroup = null;
        }

        ClearSavedMarks();
        if (parallel && opened.Count > 0)
        {
            EnsureParallelTab();
            foreach (var session in opened)
            {
                MoveToParallel(session);
            }

            UpdateParallelHeader();
            ShowParallel();
        }

        var status = Text.Format(parallel ? Strings.ParallelOpenedStatus : Strings.SavedOpenedStatus, parallel ? opened.Count : chosen.Count - missing);
        if (skipped > 0)
        {
            status += " " + Text.Format(Strings.ParallelSkippedPsm, skipped);
        }

        if (missing > 0)
        {
            status += " " + Text.Format(Strings.SavedMissingAccounts, missing);
        }

        SetStatus(status, isError: missing > 0);
    }
}
