using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Migration;
using Microsoft.Win32;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglet « Mes serveurs » : export et import de la liste, et listes partagées (fichiers JSON sur un partage réseau, que
/// chacun complète ou élague ; chaque modification est historisée avec son auteur et une copie de la version précédente).
/// </summary>
public partial class MainWindow
{
    private const string SharedFileFilterExtension = "*.json";

    private readonly List<SharedServerList> _sharedLists = [];
    private readonly Dictionary<SharedServerList, FileSystemWatcher> _sharedWatchers = [];
    private bool _sharedStopped;
    private readonly HashSet<string> _collapsedShared = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SharedServerList> _sharedChanged = [];

    // Serveur d'une liste partagée, par copie de connexion (la connexion passe par ConnectSaved). Jamais vidée : une
    // copie encore sélectionnée après une relecture de la liste reste reconnue comme partagée.
    private readonly ConditionalWeakTable<SavedSession, SharedServerNode> _sharedSessions = new();

    // Machines cibles venues d'une liste partagée déjà acceptées pendant cette session de ZillaTerm.
    private readonly HashSet<string> _acceptedSharedTargets = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _sharedReload;
    // Listes dont la surveillance sera retentée (partage injoignable, coupure réseau ou VPN).
    private readonly HashSet<SharedServerList> _sharedWatchRetries = [];

    /// <summary>Délai avant de retenter la surveillance d'un partage perdu.</summary>
    private static readonly TimeSpan SharedWatchRetry = TimeSpan.FromMinutes(1);

    private string SharedWho => SharedServerList.Who(_sessionUser);

    // ===================== Export et import de « Mes serveurs » =====================

    private void OnExportMyServers(object sender, RoutedEventArgs e)
    {
        var file = ServerListFile.Export(_settings, PvwaHost);
        if (file.Servers.Count == 0 && file.Folders.Count == 0)
        {
            MessageBox.Show(this, Strings.NoServersToExport, Strings.MyServers, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Strings.ExportServersTitle,
            Filter = Strings.ServerListFilter,
            FileName = $"{Strings.ExportServersFileName}-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, file.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus(Text.Format(Strings.ServersExported, file.Servers.Count, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Text.Format(Strings.ExportFailed, ex.Message), Strings.MyServers, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportMyServers(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Strings.ImportServersTitle, Filter = Strings.ServerListFilter };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ServerListFile file;
        try
        {
            file = ReadServerFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, Text.Format(Strings.ImportServersFailed, ex.Message), Strings.MyServers, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Fichier d'un autre PVWA : ses ID de comptes y désignent d'autres comptes que sur celui-ci.
        if (file.Pvwa.Length > 0 && !string.Equals(file.Pvwa, PvwaHost, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Text.Format(Strings.ImportServersOtherPvwa, file.Pvwa, PvwaHost), Strings.MyServers,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var plan = ServerImport.Plan(_settings, PvwaHost, file);
        if (plan.Added.Count == 0 && plan.NewFolders.Count == 0)
        {
            MessageBox.Show(this, Text.Format(Strings.ImportServersNothing, plan.Duplicates), Strings.MyServers,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var details = DescribeImport(plan);
        if (!ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.ImportServersTitle,
                Heading = Text.Format(Strings.ImportServersConfirm, plan.Added.Count, Path.GetFileName(dialog.FileName)),
                Message = details.Length > 0 ? details : null,
                Kind = plan.WithTargetMachine.Any() ? ConfirmKind.Warning : ConfirmKind.Question,
                Actions = [Strings.ImportServersAction],
                DefaultAction = 0,
            }))
        {
            return;
        }

        int added = plan.Apply(_settings, PvwaHost);
        foreach (var server in plan.Added)
        {
            Expand(server.Folder);
        }

        SaveAndRefreshSaved();
        SetStatus(Text.Format(Strings.ServersImported, added, plan.Duplicates));
    }

    /// <summary>
    /// Sessions d'un autre logiciel (PuTTY, WinSCP, mRemoteNG...) reprises dans « Mes serveurs » avec le compte du PVWA de
    /// chaque serveur : il faut la liste des comptes.
    /// </summary>
    private void OnImportSessions(object sender, RoutedEventArgs e)
    {
        if (IsOffline)
        {
            return;
        }

        if (!_accountsLoaded)
        {
            MessageBox.Show(this, _loading ? Strings.AccountsStillLoading : Strings.AccountsNotLoaded, Strings.SessionImportTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new SessionImportWindow(_settings, PvwaHost, _accounts, _domains, import =>
        {
            foreach (var created in import.Items.Select(i => i.Created).OfType<SavedSession>())
            {
                Expand(created.Folder);
            }

            SaveAndRefreshSaved();
            int added = import.Count(ImportState.Imported);
            SetStatus(Text.Format(Strings.SessionImportDone, added));
            if (added > 0)
            {
                SideTabs.SelectedItem = CurrentTab;
            }
        })
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    /// <summary>Détail avant import : serveurs ignorés, dossiers créés, machines cibles.</summary>
    private static string DescribeImport(ServerImport plan)
    {
        var text = new StringBuilder();
        if (plan.Duplicates > 0)
        {
            text.Append(Text.Format(Strings.ImportServersDuplicates, plan.Duplicates));
        }

        if (plan.NewFolders.Count > 0)
        {
            text.Append(text.Length > 0 ? "\n\n" : "").Append(Text.Format(Strings.ImportServersFolders, Abridged(plan.NewFolders, 8, ", ")));
        }

        var targets = plan.WithTargetMachine.Select(s => $"  {s.Name} — {s.RemoteMachine}").ToList();
        if (targets.Count > 0)
        {
            text.Append(text.Length > 0 ? "\n\n" : "").Append(Strings.ImportServersTargets).Append('\n').Append(Abridged(targets, 8, "\n"));
        }

        return text.ToString();
    }

    private static string Abridged(IReadOnlyList<string> items, int max, string separator) =>
        string.Join(separator, items.Take(max)) + (items.Count > max ? separator + Text.Format(Strings.AndMore, items.Count - max) : "");

    private static ServerListFile ReadServerFile(string path)
    {
        if (new FileInfo(path).Length > ServerListFile.MaxBytes)
        {
            throw new InvalidDataException(CoreStrings.ServerListInvalid);
        }

        return ServerListFile.Parse(File.ReadAllText(path));
    }

    // ===================== Listes partagées : ouverture, création, fermeture =====================

    /// <summary>Listes partagées enregistrées : lues et surveillées en arrière-plan (partage réseau parfois lent).</summary>
    private void StartSharedLists()
    {
        foreach (var path in _settings.SharedLists.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            try
            {
                _sharedLists.Add(new SharedServerList(path));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                DebugLog.Write("shared", $"Liste partagée ignorée ({path}) : {e.Message}");
            }
        }

        foreach (var list in _sharedLists)
        {
            _ = WatchSharedAsync(list);
        }

        if (_sharedLists.Count > 0)
        {
            _ = ReloadSharedAsync(_sharedLists.ToList());
        }

        Closed += (_, _) => StopWatchingSharedLists();
    }

    internal void StopWatchingSharedLists()
    {
        // Fenêtre fermée : une liste encore en chargement, ou dont la surveillance se met en place sur un partage lent,
        // ne s'ajoute plus après coup (son surveillant garderait la fenêtre fermée en vie).
        _sharedStopped = true;
        _sharedLists.Clear();
        foreach (var watcher in _sharedWatchers.Values)
        {
            watcher.Dispose();
        }

        _sharedWatchers.Clear();
        _sharedReload?.Stop();
    }

    /// <summary>Menus « Importer » et « Partager » de l'en-tête de « Mes serveurs », ouverts sous leur bouton.</summary>
    private void OnHeaderMenu(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).ContextMenu is { } menu)
        {
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void OnOpenSharedList(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Strings.OpenSharedListTitle, Filter = Strings.ServerListFilter };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (_sharedLists.FirstOrDefault(l => string.Equals(l.Path, Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            SetStatus(Text.Format(Strings.SharedListAlreadyOpen, open.Name));
            return;
        }

        var list = new SharedServerList(dialog.FileName);
        SetStatus(Text.Format(Strings.SharedListLoading, list.Path));
        await Task.Run(list.Load);
        if (_sharedStopped)
        {
            return;
        }

        if (list.Content is not { } content)
        {
            SetStatus("");
            MessageBox.Show(this, Text.Format(Strings.SharedListOpenFailed, list.Path, list.Error), Strings.SharedListsTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!content.IsShared)
        {
            SetStatus("");
            MessageBox.Show(this, CoreStrings.ServerListNotShared, Strings.SharedListsTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddSharedList(list);
        SetStatus(Text.Format(Strings.SharedListOpened, list.Name, content.Servers.Count));
        if (content.Pvwa.Length > 0 && !string.Equals(content.Pvwa, PvwaHost, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Text.Format(Strings.SharedListOtherPvwaWarning, list.Name, content.Pvwa, PvwaHost), Strings.SharedListsTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnCreateSharedList(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Strings.CreateSharedListTitle,
            Filter = Strings.ServerListFilter,
            FileName = Strings.SharedListDefaultFileName + ".json",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (File.Exists(dialog.FileName))
        {
            MessageBox.Show(this, Text.Format(Strings.SharedListExists, dialog.FileName), Strings.SharedListsTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var naming = new InputDialog(Strings.CreateSharedListTitle, Strings.SharedListNameLabel, Path.GetFileNameWithoutExtension(dialog.FileName))
        {
            Owner = this,
        };
        if (naming.ShowDialog() != true)
        {
            return;
        }

        var (path, name, pvwa, who) = (dialog.FileName, naming.Value.Trim(), PvwaHost, SharedWho);
        try
        {
            var list = await Task.Run(() => SharedServerList.Create(path, name, pvwa, who));
            if (_sharedStopped)
            {
                return;
            }

            AddSharedList(list);
            SetStatus(Text.Format(Strings.SharedListCreated, list.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Text.Format(Strings.SharedListCreateFailed, path, ex.Message), Strings.SharedListsTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddSharedList(SharedServerList list)
    {
        _sharedLists.Add(list);
        _settings.SharedLists.Add(list.Path);
        _collapsedShared.Remove(SharedKey(list, null));
        _ = WatchSharedAsync(list);
        SaveAndRefreshSaved();
    }

    private void OnCloseSharedList(object sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is not SharedListNode node)
        {
            return;
        }

        _sharedLists.Remove(node.List);
        _settings.SharedLists.RemoveAll(p => string.Equals(p, node.List.Path, StringComparison.OrdinalIgnoreCase));
        if (_sharedWatchers.Remove(node.List, out var watcher))
        {
            watcher.Dispose();
        }

        SaveAndRefreshSaved();
        SetStatus(Text.Format(Strings.SharedListClosed, node.Name, node.List.Path));
    }

    private void OnShowSharedListFile(object sender, RoutedEventArgs e)
    {
        if (SharedListOf(SavedTree.SelectedItem) is { } list)
        {
            try
            {
                Services.WindowsExplorer.ShowFile(list.Path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
            {
                SetStatus(ex.Message, isError: true);
            }
        }
    }

    // ===================== Lecture et surveillance =====================

    private async void OnRefreshSharedList(object sender, RoutedEventArgs e)
    {
        if (SharedListOf(SavedTree.SelectedItem) is { } list)
        {
            await ReloadSharedAsync([list], force: true);
            SetStatus(list.Error is null ? Text.Format(Strings.SharedListRefreshed, list.Name) : list.Error, isError: list.Error is not null);
        }
    }

    /// <summary>Relit les listes (hors du fil de l'interface) ; l'arbre n'est reconstruit que si l'une a changé.</summary>
    private async Task ReloadSharedAsync(IReadOnlyCollection<SharedServerList> lists, bool force = false)
    {
        var before = lists.Select(l => (l.Content?.Revision, l.Error)).ToList();
        await Task.WhenAll(lists.Select(l => Task.Run(l.Load)));
        var after = lists.Select(l => (l.Content?.Revision, l.Error)).ToList();
        if (!_sharedStopped && (force || !before.SequenceEqual(after)))
        {
            RefreshSaved();
        }
    }

    private async Task WatchSharedAsync(SharedServerList list)
    {
        // Le partage peut être lent ou injoignable : le surveillant est créé hors du fil de l'interface.
        var watcher = await Task.Run(() =>
        {
            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(list.Path)!, Path.GetFileName(list.Path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                w.EnableRaisingEvents = true;
                return w;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                DebugLog.Write("shared", $"Surveillance de {list.Path} impossible : {e.Message}");
                return null;
            }
        });
        if (watcher is null)
        {
            RetrySharedWatch(list);
            return;
        }

        if (_sharedStopped || !_sharedLists.Contains(list) || _sharedWatchers.ContainsKey(list))
        {
            watcher.Dispose();
            return;
        }

        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => QueueSharedReload(list));
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => QueueSharedReload(list));
        // Partage ou VPN coupé : le surveillant s'arrête en signalant une erreur ; sans reprise, les changements des
        // collègues n'apparaîtraient plus jusqu'au redémarrage.
        watcher.Error += (_, e) => Dispatcher.BeginInvoke(() => OnSharedWatchLost(list, watcher, e.GetException()));
        _sharedWatchers[list] = watcher;
    }

    private void OnSharedWatchLost(SharedServerList list, FileSystemWatcher watcher, Exception error)
    {
        DebugLog.Write("shared", $"Surveillance de {list.Path} interrompue : {error.Message}");
        if (_sharedWatchers.TryGetValue(list, out var current) && ReferenceEquals(current, watcher))
        {
            _sharedWatchers.Remove(list);
        }

        watcher.Dispose();
        RetrySharedWatch(list);
    }

    /// <summary>
    /// Nouvel essai de surveillance un peu plus tard, tant que la liste est ouverte ; une fois repris, la liste est relue
    /// pour rattraper les changements faits pendant la coupure.
    /// </summary>
    private void RetrySharedWatch(SharedServerList list)
    {
        if (_sharedStopped || !_sharedLists.Contains(list) || !_sharedWatchRetries.Add(list))
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = SharedWatchRetry };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            _sharedWatchRetries.Remove(list);
            if (_sharedStopped || !_sharedLists.Contains(list) || _sharedWatchers.ContainsKey(list))
            {
                return;
            }

            await WatchSharedAsync(list);
            if (_sharedWatchers.ContainsKey(list))
            {
                QueueSharedReload(list);
            }
        };
        timer.Start();
    }

    /// <summary>Modification signalée par le partage : relecture groupée un instant après (une écriture = plusieurs signaux).</summary>
    private void QueueSharedReload(SharedServerList list)
    {
        if (!_sharedLists.Contains(list))
        {
            return;
        }

        _sharedChanged.Add(list);
        if (_sharedReload is null)
        {
            _sharedReload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _sharedReload.Tick += async (_, _) =>
            {
                _sharedReload.Stop();
                var lists = _sharedChanged.Where(_sharedLists.Contains).ToList();
                _sharedChanged.Clear();
                await ReloadSharedAsync(lists);
            };
        }

        _sharedReload.Stop();
        _sharedReload.Start();
    }

    // ===================== Arbre =====================

    private static string SharedKey(SharedServerList list, string? folder) => list.Path + "\n" + (folder ?? "");

    /// <summary>Nœuds des listes partagées ; pendant une recherche, seules les listes qui ont un résultat restent.</summary>
    private List<object> SharedNodes(string? filter)
    {
        var nodes = new List<object>();
        bool filtered = filter is not null;
        foreach (var list in _sharedLists)
        {
            var content = list.Content;
            var children = new List<object>();
            if (content is not null)
            {
                // Les ID de comptes ne valent que pour le PVWA de la liste : ceux d'une liste d'un autre PVWA ne sont pas
                // cherchés dans les comptes de celui-ci.
                var pvwa = content.Pvwa.Length > 0 ? content.Pvwa : PvwaHost;
                var entries = new Dictionary<SavedSession, ServerEntry>(ReferenceEqualityComparer.Instance);
                foreach (var entry in content.Servers)
                {
                    entries[entry.ToSession(pvwa, keepId: true)] = entry;
                }

                var tree = SessionLibrary.BuildTree(content.AllFolders(), entries.Keys, filter);
                if (filtered && tree.TotalSessions == 0)
                {
                    continue;
                }

                children = SharedChildren(list, tree, entries, filtered);
            }
            else if (filtered)
            {
                continue;
            }

            bool expanded = filtered || !_collapsedShared.Contains(SharedKey(list, null));
            nodes.Add(new SharedListNode(list, content, children, expanded, PvwaHost));
        }

        return nodes;
    }

    private List<object> SharedChildren(SharedServerList list, SessionFolderNode node, Dictionary<SavedSession, ServerEntry> entries, bool expandAll)
    {
        var items = new List<object>();
        foreach (var folder in node.Folders)
        {
            bool expanded = expandAll || !_collapsedShared.Contains(SharedKey(list, folder.Path));
            items.Add(new SharedFolderNode(list, folder.Path, SharedChildren(list, folder, entries, expandAll), expanded) { Count = folder.TotalSessions });
        }

        foreach (var session in node.Sessions)
        {
            var account = SessionLibrary.IsForHost(session, PvwaHost) ? _byId.GetValueOrDefault(session.AccountId) : null;
            var server = new SharedServerNode(list, entries[session], session, account, _accountsLoaded)
            {
                Tag = ServerTagView.Resolve(_settings.ServerTags, session.Tag),
            };
            _sharedSessions.AddOrUpdate(session, server);
            items.Add(server);
        }

        return items;
    }

    private void RememberSharedExpansion(IEnumerable<object> nodes)
    {
        foreach (var node in nodes)
        {
            var (key, expanded, children) = node switch
            {
                SharedListNode list => (SharedKey(list.List, null), list.IsExpanded, list.Children),
                SharedFolderNode folder => (SharedKey(folder.List, folder.Path), folder.IsExpanded, folder.Children),
                _ => (null, false, null),
            };
            if (key is null)
            {
                continue;
            }

            if (expanded)
            {
                _collapsedShared.Remove(key);
            }
            else
            {
                _collapsedShared.Add(key);
            }

            RememberSharedExpansion(children!);
        }
    }

    private static SharedServerList? SharedListOf(object? node) => node switch
    {
        SharedListNode list => list.List,
        SharedFolderNode folder => folder.List,
        SharedServerNode server => server.List,
        _ => null,
    };

    /// <summary>
    /// Serveurs retirés de la liste avec un nœud : le serveur, ou tous ceux du dossier et de ses sous-dossiers, y compris
    /// ceux que la recherche masque (comme dans « Mes serveurs ») ; l'arbre d'une recherche n'en montre qu'une partie.
    /// </summary>
    internal static List<ServerEntry> SharedRemovalEntries(object node) => node switch
    {
        SharedFolderNode folder when folder.List.Content is { } content =>
            content.Servers.Where(s => !string.IsNullOrEmpty(s.Folder) && SessionFolders.IsWithin(s.Folder, folder.Path)).ToList(),
        _ => SharedServers(node).Select(s => s.Entry).ToList(),
    };

    /// <summary>Serveurs d'un nœud de liste partagée (dossier : sous-dossiers compris).</summary>
    private static List<SharedServerNode> SharedServers(object node) => node switch
    {
        SharedServerNode server => [server],
        SharedListNode list => list.Children.SelectMany(SharedServers).ToList(),
        SharedFolderNode folder => folder.Children.SelectMany(SharedServers).ToList(),
        _ => [],
    };

    // ===================== Modifications =====================

    /// <summary>
    /// Modification d'une liste partagée hors du fil de l'interface (partage réseau, attente qu'un autre poste ait fini
    /// d'écrire) ; null en cas d'échec, expliqué à l'utilisateur.
    /// </summary>
    private async Task<int?> ChangeSharedAsync(SharedServerList list, Func<SharedServerList, int> change)
    {
        SetStatus(Text.Format(Strings.SharedListSaving, list.Name));
        try
        {
            int result = await Task.Run(() => change(list));
            RefreshSaved();
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("");
            var reason = e is UnauthorizedAccessException ? Strings.SharedListReadOnly : e.Message;
            MessageBox.Show(this, Text.Format(Strings.SharedListChangeFailed, list.Name, reason), Strings.SharedListsTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>
    /// Ajoute des serveurs de « Mes serveurs » (ou un compte) à une liste partagée. Le dossier de chacun est donné par
    /// <paramref name="folderOf"/> ; le motif reste personnel.
    /// </summary>
    private async Task ShareAsync(SharedServerList list, IReadOnlyCollection<SavedSession> sessions, Func<SavedSession, string> folderOf)
    {
        if (sessions.Count == 0 || RefuseOtherPvwa(list))
        {
            return;
        }

        var entries = sessions.Select(s =>
        {
            var entry = ServerEntry.From(s);
            entry.Folder = SessionFolders.Normalize(folderOf(s));
            return entry;
        }).ToList();
        var who = SharedWho;
        if (await ChangeSharedAsync(list, l => l.Add(entries, who)) is { } added)
        {
            if (entries.Select(e => e.Folder).FirstOrDefault(f => f.Length > 0) is { } folder)
            {
                ExpandShared(list, folder);
                RefreshSaved();
            }

            SetStatus(Text.Format(Strings.SharedServersAdded, added, list.Name, entries.Count - added));
        }
    }

    private void ExpandShared(SharedServerList list, string folder)
    {
        _collapsedShared.Remove(SharedKey(list, null));
        for (var path = SessionFolders.Normalize(folder); path.Length > 0; path = SessionFolders.Parent(path))
        {
            _collapsedShared.Remove(SharedKey(list, path));
        }
    }

    /// <summary>Sous-menu « Partager dans une liste » : une entrée par liste partagée lisible.</summary>
    private void BuildShareMenu(MenuItem parent, Func<IReadOnlyCollection<SavedSession>> servers)
    {
        parent.Items.Clear();
        var lists = _sharedLists.Where(l => l.Content?.IsShared == true).ToList();
        parent.IsEnabled = lists.Count > 0 && !IsOffline;
        parent.ToolTip = lists.Count == 0 ? Strings.ShareNoListTip : null;
        foreach (var list in lists)
        {
            parent.Items.Add(MenuEntry(list.Name, () => _ = ShareAsync(list, servers(), s => s.Folder)));
        }
    }

    /// <summary>
    /// Serveur, dossier de « Mes serveurs » ou compte de « Disponibles » déposé sur une liste partagée : ajouté (après
    /// confirmation) dans le dossier visé de la liste.
    /// </summary>
    private async Task DropOnSharedAsync(SharedServerList list, object target, IDataObject data)
    {
        var folder = target switch
        {
            SharedFolderNode f => f.Path,
            SharedServerNode s => s.Session.Folder,
            _ => "",
        };
        List<SavedSession> sessions;
        Func<SavedSession, string> folderOf;
        switch (data.GetData(AccountDragFormat) ?? data.GetData(SavedDragFormat))
        {
            case PvwaAccount account:
                sessions = [SavedSession.FromAccount(account, PvwaHost, folder)];
                folderOf = _ => folder;
                break;
            case SavedSessionNode node:
                sessions = [node.Session];
                folderOf = _ => folder;
                break;
            case SavedFolderNode dropped:
                sessions = FolderServers(dropped);
                var destination = SessionFolders.Combine(folder, dropped.Name);
                folderOf = s => SessionFolders.Rebase(s.Folder, dropped.Path, destination);
                break;
            default:
                return;
        }

        if (sessions.Count == 0 || list.Content?.IsShared != true)
        {
            return;
        }

        // Les autres utilisateurs de la liste verront ces serveurs : Entrée annule.
        if (ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.SharedListsTitle,
                Heading = Text.Format(Strings.ShareDropHeading, list.Name),
                Message = Strings.ShareDropMessage,
                Items = sessions.Select(s => s.Name).ToList(),
                Actions = [Strings.ShareDropAction],
            }))
        {
            await ShareAsync(list, sessions, folderOf);
        }
    }

    private async void OnRemoveFromSharedList(object sender, RoutedEventArgs e) => await RemoveSharedAsync(SavedTree.SelectedItem);

    private async Task RemoveSharedAsync(object? node)
    {
        if (SharedListOf(node) is not { } list || node is SharedListNode)
        {
            return;
        }

        var shown = SharedServers(node!);
        var entries = SharedRemovalEntries(node!);
        if (entries.Count == 0)
        {
            return;
        }

        int hidden = Math.Max(0, entries.Count - shown.Count);
        var heading = node is SharedFolderNode folder
            ? Text.Format(Strings.SharedRemoveFolderHeading, folder.Path, list.Name)
            : Text.Format(Strings.SharedRemoveHeading, shown[0].Title, list.Name);
        if (!ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.SharedListsTitle,
                Heading = heading,
                Message = hidden > 0
                    ? Text.Format(Strings.SharedRemoveHiddenBySearch, hidden) + " " + Strings.SharedRemoveMessage
                    : Strings.SharedRemoveMessage,
                Items = node is SharedFolderNode ? entries.Select(s => s.ToSession(PvwaHost).Name).ToList() : [],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.ActionRemove],
                DangerAction = 0,
            }))
        {
            return;
        }

        var ids = entries.Select(s => s.Id).ToList();
        var who = SharedWho;
        if (await ChangeSharedAsync(list, l => l.Remove(ids, who)) is { } removed)
        {
            SetStatus(Text.Format(Strings.SharedServersRemoved, removed, list.Name));
        }
    }

    private void OnCopySharedToMyServers(object sender, RoutedEventArgs e)
    {
        if (SavedTree.SelectedItem is not { } node || SharedServers(node) is not { Count: > 0 } servers || RefuseOtherPvwa(servers[0].List))
        {
            return;
        }

        var file = new ServerListFile { Format = ServerListFile.ExportFormat, Servers = servers.Select(s => s.Entry).ToList() };
        var plan = ServerImport.Plan(_settings, PvwaHost, file);
        if (plan.Added.Count == 0)
        {
            SetStatus(Text.Format(Strings.CopiedToMyServersNone, plan.Duplicates));
            return;
        }

        // Machine cible choisie dans la liste et non autorisée par CyberArk : confirmée avant la copie, car la copie ne la
        // signalera plus à la connexion.
        var targets = plan.Added
            .Where(s => !string.IsNullOrWhiteSpace(s.RemoteMachine) && !IsAllowedTarget(s.AccountId, s.RemoteMachine!))
            .Select(s => $"  {s.Name} — {s.RemoteMachine!.Trim()}")
            .ToList();
        if (targets.Count > 0 && !ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.SharedListsTitle,
                Heading = Strings.SharedCopyTargetsHeading,
                Subject = Text.Format(Strings.SharedListSubject, servers[0].List.Name),
                Message = Strings.SharedCopyTargetsMessage,
                Items = targets.Select(t => t.Trim()).ToList(),
                Kind = ConfirmKind.Warning,
                Actions = [Strings.SharedCopyAnyway],
                DangerAction = 0,
            }))
        {
            return;
        }

        plan.Apply(_settings, PvwaHost);
        foreach (var server in plan.Added)
        {
            Expand(server.Folder);
        }

        SaveAndRefreshSaved();
        SetStatus(Text.Format(Strings.CopiedToMyServers, plan.Added.Count, plan.Duplicates));
    }

    private async void OnSharedHistory(object sender, RoutedEventArgs e)
    {
        if (SharedListOf(SavedTree.SelectedItem) is not { } list)
        {
            return;
        }

        var who = SharedWho;
        var dialog = new SharedHistoryDialog(list, version => ChangeSharedAsync(list, l =>
        {
            l.Restore(version, who);
            return 0;
        })) { Owner = this };
        dialog.ShowDialog();
        await ReloadSharedAsync([list]);
    }

    // ===================== Connexion =====================

    /// <summary>
    /// Liste créée pour un autre PVWA : ses ID de comptes ne désignent pas les mêmes comptes ici, on n'y ajoute rien et on
    /// n'en copie rien (message dans la barre d'état).
    /// </summary>
    private bool RefuseOtherPvwa(SharedServerList list)
    {
        if (list.Content is not { Pvwa.Length: > 0 } content || string.Equals(content.Pvwa, PvwaHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        SetStatus(Text.Format(Strings.SharedOtherPvwaChange, list.Name, content.Pvwa, PvwaHost), isError: true);
        return true;
    }

    /// <summary>Machine cible autorisée par CyberArk pour le compte (machines du compte).</summary>
    private bool IsAllowedTarget(string accountId, string machine) =>
        _byId.TryGetValue(accountId, out var account)
        && AccountClassifier.RemoteMachineList(account).Contains(machine.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Une machine cible venue d'une liste partagée (et non de CyberArk) est confirmée avant la première connexion :
    /// quiconque peut écrire la liste peut la changer, et la session ouvre le compte de domaine sur cette machine.
    /// </summary>
    private bool ConfirmSharedTarget(SavedSession saved, PvwaAccount account)
    {
        if (!_sharedSessions.TryGetValue(saved, out var node) || string.IsNullOrWhiteSpace(saved.RemoteMachine))
        {
            return true;
        }

        var machine = saved.RemoteMachine.Trim();
        var key = $"{account.Id}\n{machine}";
        if (_acceptedSharedTargets.Contains(key)
            || AccountClassifier.RemoteMachineList(account).Contains(machine, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.SharedListsTitle,
                Heading = Text.Format(Strings.SharedTargetHeading, AccountLabel(account), machine),
                Subject = Text.Format(Strings.SharedTargetSubject, node.Title, node.List.Name),
                Message = Strings.SharedTargetMessage,
                Kind = ConfirmKind.Warning,
                Actions = [Strings.ConnectAnyway],
                DangerAction = 0,
            }))
        {
            return false;
        }

        _acceptedSharedTargets.Add(key);
        return true;
    }

    private void OnSharedListMenuOpened(object sender, RoutedEventArgs e)
    {
        var node = SavedTree.SelectedItem as SharedListNode;
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>().Where(i => i.Tag as string == "readable"))
        {
            item.IsEnabled = node?.IsReadable == true && node.Children.Count > 0;
        }
    }
}
