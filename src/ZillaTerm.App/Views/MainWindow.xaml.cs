using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Rdp;
using ZillaTerm.Core.Ssh;
using Microsoft.Win32;

namespace ZillaTerm.App.Views;

/// <summary>
/// Fenêtre principale : barre d'outils, arbre des sessions à gauche,
/// onglet Accueil, connexion PSM ou SSH (PSMP) sur double-clic.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Client du PVWA ; null en accès d'urgence (sans CyberArk, coffres KeePass seulement).</summary>
    private readonly PvwaClient? _client;
    private readonly AppSettings _settings;
    private readonly string _sessionUser;
    private readonly string _vaultUser;
    private readonly AuthMethod _authMethod;
    private readonly SessionLauncher _launcher = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _savedSearchDebounce;
    // Dernière erreur de chargement des comptes, montrée dans la liste avec « Réessayer » ; null après un chargement réussi.
    private string? _loadError;
    private readonly CancellationTokenSource _lifetime = new();
    private List<PvwaAccount> _accounts = [];

    /// <summary>Comptes qui répondent à la recherche de « Disponibles » (affichés, comptés, exportés).</summary>
    private List<PvwaAccount> _shown = [];
    private Dictionary<string, PvwaAccount> _byId = [];

    /// <summary>Domaines connus (au-dessus des serveurs des comptes, du PVWA, du poste) : reconnaît les comptes de domaine.</summary>
    private KnownDomains _domains = KnownDomains.From([], WorkstationDomains());
    private string _query = "";
    private PvwaAccount? _current;
    private SavedSession? _currentSaved;
    private bool _loading;

    /// <summary>Dernier chargement des comptes en échec : les connexions récentes ne restent pas grisées.</summary>
    private bool _loadFailed;

    // Comptes du PVWA chargés au moins une fois : avant, un compte absent n'est pas encore un compte disparu.
    private bool _accountsLoaded;
    private bool _connecting;
    private bool _loggedOff;

    // Nettoyage de fermeture terminé : la fenêtre peut se fermer pour de bon.
    private bool _closeConfirmed;

    /// <param name="client">Client du PVWA connecté ; null pour l'accès d'urgence sans CyberArk.</param>
    /// <param name="authMethod">Méthode de la connexion, reprise pour se reconnecter si la session PVWA expire.</param>
    internal MainWindow(PvwaClient? client, AppSettings settings, string sessionUser, string vaultUser, KeePassManager keePass,
        AuthMethod authMethod = AuthMethod.CyberArk)
    {
        InitializeComponent();
        _client = client;
        _settings = settings;
        _sessionUser = sessionUser;
        _vaultUser = vaultUser;
        _authMethod = authMethod;
        _keePass = keePass;
        Title = client is null ? Strings.EmergencyTitle : $"ZillaTerm — {client.BaseUri.Host}";
        SessionText.Text = client is null ? Strings.EmergencySession : $"{sessionUser} @ {client.BaseUri.Host}";
        if (client is null)
        {
            // Accès d'urgence : pas de session CyberArk à fermer, le bouton ramène à l'identification.
            LogoutLabel.Text = Strings.ToolLeaveEmergency;
            LogoutButton.ToolTip = Strings.ToolLeaveEmergencyTip;
            System.Windows.Automation.AutomationProperties.SetName(LogoutButton, Strings.ToolLeaveEmergency);
        }

        UpdateDebugLogIndicator();
        InitIntervention();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };
        _savedSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _savedSearchDebounce.Tick += (_, _) =>
        {
            _savedSearchDebounce.Stop();
            RefreshSaved();
        };

        GroupByBox.DisplayMemberPath = "Key";
        GroupByBox.SelectedValuePath = "Value";
        GroupByBox.ItemsSource = new[]
        {
            new KeyValuePair<string, GroupBy>(CoreStrings.ColumnSafe, GroupBy.Safe),
            new KeyValuePair<string, GroupBy>(CoreStrings.ColumnPlatform, GroupBy.Platform),
            new KeyValuePair<string, GroupBy>(Strings.GroupByKind, GroupBy.Kind),
        };
        GroupByBox.SelectedValue = settings.GroupBy;

        _psmpUi = new SshInteraction(this, settings, SaveSettings);
        _directUi = new SshInteraction(this, settings, SaveSettings, direct: true);
        _keePass.Changed += OnKeePassChanged;
        SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
        Closed += (_, _) =>
        {
            CloseAllVncSessions();
            _keePass.Changed -= OnKeePassChanged;
            SystemEvents.SessionSwitch -= OnWindowsSessionSwitch;
        };

        CompareWindow.CleanTemporaryFiles();
        ZillaTerm.App.Terminal.TerminalAppearance.Apply(settings.TerminalTheme, settings.TerminalFontSize, settings.TerminalRightClickPastes);
        FilesPanel.Initialize(settings, SaveSettings);
        FilesPanel.OpenSessions = () => MainTabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<RemoteSession>().ToList();
        FilesPanel.TagOf = TagOf;
        FilesPanel.ShowTerminalRequested += ShowTerminal;
        FilesPanel.TransfersChanged += UpdateFilesBadge;
        FilesPanel.WidenRequested += WidenSidePanel;
        if (IsOffline)
        {
            // Accès d'urgence : ni comptes CyberArk ni PSM, seulement les coffres KeePass de « Mes serveurs ».
            AvailableTab.Visibility = Visibility.Collapsed;
            QuickPanel.Visibility = HomeLists.Visibility = NewFolderButton.Visibility = Visibility.Collapsed;
            // Boutons propres à CyberArk masqués plutôt que grisés : ils ne serviraient jamais dans ce mode.
            SshButton.Visibility = AdvancedButton.Visibility = AddCurrentButton.Visibility = RefreshButton.Visibility = Visibility.Collapsed;
            // Menus « Importer » et « Partager » de « Mes serveurs » aussi ; la base KeePass s'ajoute alors par son bouton.
            ImportMenuButton.Visibility = ShareMenuButton.Visibility = SharedSeparator.Visibility = Visibility.Collapsed;
            AddKeePassButton.Visibility = Visibility.Visible;
            SideTabs.SelectedItem = CurrentTab;
            CountText.Text = "";
        }
        else
        {
            RefreshRecent();
            StartSharedLists();
        }

        RefreshSaved();
        UpdateWelcome();
        UpdateActions();
        UpdateFilesTabVisibility();
        // « Parallèle » n'est actif qu'avec au moins une session SSH ouverte, l'onglet Fichiers n'est là qu'avec une
        // session SSH ou de fichiers.
        ((System.Collections.Specialized.INotifyCollectionChanged)MainTabs.Items).CollectionChanged += (_, _) =>
        {
            UpdateActions();
            UpdateFilesTabVisibility();
        };
        StartKeepAlive();
        RestoreLayout();
        Loaded += async (_, _) =>
        {
            SetUpTabStrip();
            // Prêt à taper : la connexion rapide (ou, en accès d'urgence, la liste des coffres KeePass).
            if (IsOffline)
            {
                SavedTree.Focus();
            }
            else
            {
                QuickBox.Focus();
            }

            _ = CheckForUpdateAsync();
            if (!IsOffline)
            {
                await LoadAccountsAsync();
            }
        };
    }

    /// <summary>
    /// Option des Paramètres (désactivée par défaut) : une recherche de nouvelle version par jour au plus, discrète :
    /// un lien dans la barre d'état si une version plus récente existe ; aucune erreur affichée.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (UpdateService.Available is { } known)
        {
            ShowUpdateLink(known);
            return;
        }

        if (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromDays(1))
        {
            return;
        }

        try
        {
            var latest = await UpdateChecker.CheckAsync(UpdateService.Http, _lifetime.Token);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            SaveSettings();
            if (UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentVersion))
            {
                UpdateService.Available = latest;
                ShowUpdateLink(latest);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("update", "Recherche de nouvelle version impossible", ex);
        }
    }

    private void ShowUpdateLink(UpdateInfo info)
    {
        UpdateLinkText.Text = Text.Format(Strings.UpdateAvailableLink, info.Version);
        UpdateLink.Visibility = Visibility.Visible;
    }

    private void OnUpdateLink(object sender, RoutedEventArgs e) => ShowAbout();

    private void OnAbout(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout()
    {
        new AboutWindow(_settings, SaveSettings) { Owner = this }.ShowDialog();
        if (UpdateService.Available is { } info && UpdateChecker.IsNewer(info.Version, UpdateChecker.CurrentVersion))
        {
            ShowUpdateLink(info);
        }
    }

    /// <summary>Accès d'urgence, sans connexion à CyberArk.</summary>
    private bool IsOffline => _client is null;

    /// <summary>Client du PVWA, pour les actions qui n'existent qu'avec CyberArk.</summary>
    private PvwaClient Client => _client ?? throw new InvalidOperationException(Strings.EmergencyWelcome);

    /// <summary>Vrai si la fenêtre a été fermée pour revenir à l'écran de connexion.</summary>
    public bool LogoutRequested { get; private set; }

    /// <summary>
    /// Domaines du poste de travail : nom DNS de l'utilisateur et de la machine, nom NetBIOS du domaine (sauf sur un poste
    /// hors domaine, où c'est le nom de la machine).
    /// </summary>
    private static IEnumerable<string?> WorkstationDomains()
    {
        string? machineDomain = null;
        try
        {
            machineDomain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }

        var netbios = Environment.UserDomainName;
        return [Environment.GetEnvironmentVariable("USERDNSDOMAIN"), machineDomain,
            string.Equals(netbios, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? null : netbios];
    }

    /// <summary>Un PSMP est configuré (par défaut ou pour un domaine) : SSH et SFTP sont proposés.</summary>
    private bool HasPsmp => PsmpRouting.Any(_settings);

    // ===================== Chargement et filtre =====================

    private async Task LoadAccountsAsync()
    {
        if (_loading || IsOffline)
        {
            return;
        }

        _loading = true;
        _loadFailed = false;
        RefreshRecent();
        CommandManager.InvalidateRequerySuggested();
        LoadProgress.Value = 0;
        LoadProgress.IsIndeterminate = true;
        LoadProgress.Visibility = Visibility.Visible;
        CountText.Text = Strings.LoadingAccounts;
        _loadError = null;
        UpdateAvailableState();

        bool reload = false;
        var progress = new Progress<(int Loaded, int Total)>(p =>
        {
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Maximum = Math.Max(p.Total, 1);
            LoadProgress.Value = p.Loaded;
            CountText.Text = Text.Format(Strings.LoadingAccountsProgress, p.Loaded, p.Total);
        });

        try
        {
            var accounts = await Client.GetAccountsAsync(progress, _lifetime.Token);
            accounts.Sort((a, b) =>
            {
                int c = StringComparer.OrdinalIgnoreCase.Compare(a.Address, b.Address);
                return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.UserName, b.UserName);
            });
            _accounts = accounts;
            _domains = KnownDomains.From(accounts.Select(a => a.Address).Append(PvwaHost), WorkstationDomains(),
                accounts.Where(AccountClassifier.TargetsServer).Select(a => a.Address).Append(PvwaHost));
            _byId = [];
            foreach (var a in accounts)
            {
                _byId.TryAdd(a.Id, a);
            }

            _current = null;
            _accountsLoaded = true;
            ApplyFilter();
            SessionLibrary.MigrateFavorites(_settings, _byId, Client.BaseUri.Host);
            SaveSettings();
            RefreshSaved();
            RefreshRecent();
            RefreshQuickResults();
            UpdateWelcome();
            UpdateActions();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Fenêtre en cours de fermeture.
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            reload = Reconnect(userAction: true);
            if (!reload)
            {
                // Plus tard : la liste le dit, « Réessayer » (ou F5) propose à nouveau de se reconnecter.
                CountText.Text = Strings.LoadFailedShort;
                _loadFailed = true;
                RefreshRecent();
                _loadError = Strings.KeepAliveExpired;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CountText.Text = Strings.LoadFailedShort;
            // Un clic sur une connexion récente expliquera alors que les comptes n'ont pas pu être chargés.
            _loadFailed = true;
            RefreshRecent();
            // Dit sur place, dans la liste, avec « Réessayer » ; et dans la barre d'état si l'on est ailleurs.
            _loadError = ErrorText.Describe(ex);
            SetStatus(Text.Format(Strings.AvailableLoadFailed, _loadError), isError: true);
        }
        finally
        {
            _loading = false;
            LoadProgress.Visibility = Visibility.Collapsed;
            CommandManager.InvalidateRequerySuggested();
            UpdateAvailableState();
            UpdateCountVisibility();
        }

        if (reload)
        {
            await LoadAccountsAsync();
        }
    }

    private void ApplyFilter()
    {
        _query = SearchBox.Text;
        _shown = _accounts.Where(a => AccountFilter.Matches(a, _query)).ToList();
        RebuildTree();
        UpdateCount();
        UpdateAvailableState();
    }

    private void RebuildTree()
    {
        var groups = AccountGrouping.Group(_shown, _settings.GroupBy);
        bool expand = _query.Trim().Length > 0 || groups.Count == 1;
        // Comptes du même nom dans un dossier : ce qui les distingue ; pendant une recherche, le champ caché qui correspond.
        SessionTree.ItemsSource = groups
            .Select(g =>
            {
                var distinctions = AccountGrouping.Distinctions(g.Accounts);
                return new FolderNode(g.Name, g.Accounts.Select(a => new AccountNode(a, distinctions.GetValueOrDefault(a), _query, g.Name)).ToList(), expand);
            })
            .ToList();
    }

    private void UpdateCount()
    {
        int shown = _shown.Count;
        CountText.Text = shown == _accounts.Count
            ? Text.Format(Strings.AccountCount, _accounts.Count)
            : Text.Format(Strings.AccountCountFiltered, shown, _accounts.Count);
    }

    /// <summary>Message à la place de la liste vide : chargement, échec, aucun compte, aucun résultat pour le filtre.</summary>
    private void UpdateAvailableState()
    {
        string? text = null;
        string? action = null;
        if (_loading && _accounts.Count == 0)
        {
            text = Strings.LoadingAccounts;
        }
        else if (_loadError is not null && _accounts.Count == 0)
        {
            text = Text.Format(Strings.AvailableLoadFailed, _loadError);
            action = Strings.Retry;
        }
        else if (_accountsLoaded && _accounts.Count == 0)
        {
            text = Strings.AvailableNone;
        }
        else if (_shown.Count == 0 && _query.Trim().Length > 0)
        {
            text = Text.Format(Strings.AvailableNoMatch, _query.Trim());
            action = Strings.ClearFilter;
        }

        AvailableState.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        AvailableStateText.Text = text ?? "";
        AvailableStateButton.Content = action;
        AvailableStateButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnAvailableStateAction(object sender, RoutedEventArgs e)
    {
        if (_loadError is not null && _accounts.Count == 0)
        {
            await LoadAccountsAsync();
        }
        else
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }
    }

    /// <summary>Le nombre de comptes ne s'affiche qu'avec la liste des comptes (ou pendant leur chargement).</summary>
    private void UpdateCountVisibility() =>
        CountText.Visibility = AvailableTab.IsSelected || _loading ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateWelcome()
    {
        WelcomeText.Text = _client is null ? Strings.EmergencyWelcome : Text.Format(Strings.Welcome, _client.BaseUri.Host, _sessionUser, _accounts.Count);
    }

    /// <summary>
    /// Connexions récentes : grisées tant que les comptes du PVWA se chargent (un clic ne pourrait que répondre, à
    /// tort, que le compte n'existe plus). Après un échec, elles redeviennent cliquables pour en donner la raison.
    /// </summary>
    private void RefreshRecent()
    {
        bool usable = _accountsLoaded || _loadFailed;
        // Seulement celles de ce PVWA : ailleurs, le même ID de compte désigne un autre compte.
        var recent = _settings.Recent.Where(r => r.IsForHost(PvwaHost)).ToList();
        RecentList.ItemsSource = recent;
        NoRecentText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RecentList.IsEnabled = usable;
        RecentList.Opacity = usable ? 1 : 0.45;
        RecentLoadingText.Visibility = usable || recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Compte introuvable : comptes pas encore chargés, chargement échoué, ou compte vraiment disparu.</summary>
    private string MissingAccountText(string gone) =>
        _accountsLoaded ? gone : _loading ? Strings.AccountsStillLoading : Strings.AccountsNotLoaded;

    private void RefreshQuickResults()
    {
        var text = QuickBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            QuickResultsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var matches = _accounts.Where(a => AccountFilter.Matches(a, text)).ToList();
        var results = matches.Take(QuickMaxResults).ToList();
        QuickResults.ItemsSource = results;
        QuickResults.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickInfo.Text = !_accountsLoaded && _accounts.Count == 0 ? (_loading ? Strings.LoadingAccounts : Strings.LoadFailedShort)
            : results.Count == 0 ? Text.Format(Strings.AvailableNoMatch, text.Trim())
            : matches.Count > results.Count ? Text.Format(Strings.QuickFirstResults, results.Count, matches.Count)
            : "";
        QuickInfo.Visibility = QuickInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickResultsPanel.Visibility = Visibility.Visible;
        if (results.Count > 0)
        {
            QuickResults.SelectedIndex = 0;
        }
    }

    private const int QuickMaxResults = 50;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter)
        {
            _searchDebounce.Stop();
            ApplyFilter();
            FocusFirstTreeAccount();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
        }
    }

    private void FocusFirstTreeAccount()
    {
        SessionTree.UpdateLayout();
        if (SessionTree.ItemsSource is IReadOnlyList<FolderNode> { Count: > 0 } folders
            && SessionTree.ItemContainerGenerator.ContainerFromItem(folders[0]) is TreeViewItem folder)
        {
            folder.IsExpanded = true;
            folder.UpdateLayout();
            if (folder.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
            {
                first.IsSelected = true;
                first.Focus();
                return;
            }

            folder.Focus();
        }
    }

    private void OnGroupByChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupByBox.SelectedValue is GroupBy groupBy && groupBy != _settings.GroupBy)
        {
            _settings.GroupBy = groupBy;
            SaveSettings();
            RebuildTree();
        }
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        // Dans l'onglet Fichiers, Ctrl+F filtre le dossier affiché.
        if (SideTabs.SelectedItem == FilesTab && FilesPanel.FocusFilter())
        {
            return;
        }

        // Dans « Mes serveurs », Ctrl+F cherche parmi les serveurs courants ; ailleurs, parmi tous les comptes.
        var box = SideTabs.SelectedItem == CurrentTab ? SavedSearchBox : SearchBox;
        if (box == SearchBox)
        {
            SideTabs.SelectedIndex = 0;
        }

        box.Focus();
        box.SelectAll();
    }

    private void CanRefresh(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !_loading && !IsOffline;

    private async void OnRefresh(object sender, ExecutedRoutedEventArgs e)
    {
        if (_sharedLists.Count > 0)
        {
            _ = ReloadSharedAsync(_sharedLists.ToList());
        }

        await LoadAccountsAsync();
    }

    // ===================== Sélection =====================

    /// <summary>
    /// Cible du bouton « Connecter » : la dernière sélection faite, compte CyberArk ou entrée KeePass (jamais les deux,
    /// pour qu'une entrée KeePass choisie plus tôt ne prenne pas la place du compte qu'on vient de sélectionner).
    /// </summary>
    private void SetCurrent(PvwaAccount? account, SavedSession? saved = null, KeePassEntryNode? keePass = null)
    {
        _current = account;
        _currentSaved = saved;
        _currentKeePass = keePass;
        UpdateActions();
    }

    private void SetCurrentFrom(object? item)
    {
        switch (item)
        {
            case KeePassEntryNode entry:
                SetCurrent(null, keePass: entry);
                break;
            case SavedSessionNode node:
                SetCurrent(node.Account, node.Session);
                break;
            case SharedServerNode node:
                SetCurrent(node.Account, node.Session);
                break;
            case AccountNode node:
                SetCurrent(node.Account);
                break;
            case PvwaAccount account:
                SetCurrent(account);
                break;
            default:
                SetCurrent(null);
                break;
        }
    }

    /// <summary>
    /// Revenir dans une liste redonne à sa sélection le rôle de cible, même si elle n'a pas changé (cliquer sur
    /// l'élément déjà sélectionné ne déclenche pas SelectionChanged).
    /// </summary>
    private void OnSelectorFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            SetCurrentFrom(sender switch
            {
                TreeView tree => tree.SelectedItem,
                System.Windows.Controls.Primitives.Selector selector => selector.SelectedItem,
                _ => null,
            });
        }
    }

    /// <summary>Une entrée KeePass n'est plus la cible une fois « Mes serveurs » quitté : elle n'est plus visible.</summary>
    private void OnSideTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SideTabs))
        {
            return;
        }

        if (_currentKeePass is not null && !CurrentTab.IsSelected)
        {
            SetCurrent(null);
        }

        UpdateCountVisibility();
        // Un onglet de gauche choisi (Ctrl+1/2/3, clic…) se montre, même panneau replié ; pas l'onglet Fichiers choisi à
        // l'ouverture d'une session SSH (SelectSideTab sans expand).
        if (IsLoaded && SidePanelCollapsed && !_quietSideSelection)
        {
            SetSidePanelCollapsed(false);
        }

        UpdateFilesBadge();
    }

    private void UpdateActions()
    {
        bool has = _current is not null && !_connecting;
        ConnectButton.IsEnabled = has || _currentKeePass is not null;
        AdvancedButton.IsEnabled = has;
        AddCurrentButton.IsEnabled = _current is not null && _currentSaved is null;
        SshButton.IsEnabled = has && HasPsmp;
        // Infobulles visibles sur un bouton grisé : elles disent ce qui manque.
        string selectFirst = _connecting ? Strings.ToolBusyConnecting : IsOffline ? Strings.ToolSelectKeePassEntry : Strings.ToolSelectAccount;
        ConnectButton.ToolTip = ConnectButton.IsEnabled ? Strings.ToolConnectTip : WithReason(Strings.ToolConnectTip, selectFirst);
        AdvancedButton.ToolTip = has ? Strings.ToolAdvancedTip : WithReason(Strings.ToolAdvancedTip, selectFirst);
        AddCurrentButton.ToolTip = AddCurrentButton.IsEnabled ? Strings.ToolAddToMyServersTip
            : WithReason(Strings.ToolAddToMyServersTip, _currentSaved is not null ? Strings.ToolAlreadyInMyServers : Strings.ToolSelectAccount);
        SshButton.ToolTip = !HasPsmp ? Strings.SetPsmpAddress : has ? Strings.ToolSshTip : WithReason(Strings.ToolSshTip, selectFirst);
        bool anySsh = MainTabs.Items.OfType<TabItem>().Any(t => t.Tag is SshSession);
        ParallelButton.IsEnabled = anySsh;
        ParallelButton.ToolTip = anySsh ? Strings.ToolParallelTip : WithReason(Strings.ToolParallelTip, Strings.ParallelNoSession);
    }

    /// <summary>Infobulle d'un bouton grisé : ce qu'il fait, puis ce qui manque pour qu'il soit actif.</summary>
    private static string WithReason(string tip, string reason) => tip + "\n" + reason;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => SetCurrentFrom(e.NewValue);

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Résultats de la connexion rapide : leur premier élément, choisi d'office, n'est la cible que si l'on s'y
        // trouve ; un rechargement des comptes (F5) ne doit pas faire viser en silence un autre compte.
        if (sender is ListBox { SelectedItem: PvwaAccount account } list
            && (list != QuickResults || QuickBox.IsKeyboardFocusWithin || QuickResults.IsKeyboardFocusWithin))
        {
            SetCurrent(account);
        }
    }

    private void OnTreeItemRightClick(object sender, MouseButtonEventArgs e)
    {
        // Comme dans l'explorateur : le clic droit sélectionne l'élément avant d'ouvrir le menu.
        if (sender is TreeViewItem item)
        {
            if (item.DataContext is SavedSessionNode { IsMarked: false } or SavedFolderNode && _savedMarks.Count > 0)
            {
                // Clic droit hors des serveurs choisis : la sélection de plusieurs serveurs est abandonnée.
                ClearSavedMarks();
            }

            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    // ===================== Ouverture des sessions =====================

    private void OnTreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: AccountNode node })
        {
            e.Handled = true;
            _ = ConnectAsync(node.Account, DefaultRequest(node.Account, null));
        }
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SessionTree.SelectedItem is AccountNode node)
        {
            e.Handled = true;
            _ = ConnectAsync(node.Account, DefaultRequest(node.Account, null));
        }
    }

    private void OnAccountItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PvwaAccount account })
        {
            e.Handled = true;
            _ = ConnectAsync(account, DefaultRequest(account, null));
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: PvwaAccount account })
        {
            e.Handled = true;
            _ = ConnectAsync(account, DefaultRequest(account, null));
        }
    }

    private void OnQuickChanged(object sender, TextChangedEventArgs e) => RefreshQuickResults();

    private void OnQuickKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when QuickResults.SelectedItem is PvwaAccount account:
                e.Handled = true;
                _ = ConnectAsync(account, DefaultRequest(account, null));
                break;
            case Key.Down when QuickResults.Items.Count > 0:
                e.Handled = true;
                QuickResults.SelectedIndex = Math.Min(QuickResults.SelectedIndex + 1, QuickResults.Items.Count - 1);
                QuickResults.ScrollIntoView(QuickResults.SelectedItem);
                break;
            case Key.Up when QuickResults.Items.Count > 0:
                e.Handled = true;
                QuickResults.SelectedIndex = Math.Max(QuickResults.SelectedIndex - 1, 0);
                QuickResults.ScrollIntoView(QuickResults.SelectedItem);
                break;
            case Key.Escape:
                QuickBox.Clear();
                break;
        }
    }

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e) => ConnectRecent();

    private void OnRecentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConnectRecent();
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            OnRemoveRecent(sender, e);
        }
    }

    private void OnConnectRecent(object sender, RoutedEventArgs e) => ConnectRecent();

    /// <summary>Retire la connexion choisie de la liste des récentes (rien n'est fermé ni supprimé ailleurs).</summary>
    private void OnRemoveRecent(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentSession recent && _settings.Recent.Remove(recent))
        {
            SaveSettings();
            RefreshRecent();
        }
    }

    private void OnRecentMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Clic droit dans la zone vide de la liste : pas de menu (le clic droit sur une ligne la sélectionne).
        if (ItemUnder<ListBoxItem>(e.OriginalSource) is not { DataContext: RecentSession recent } || RecentList.SelectedItem != recent)
        {
            e.Handled = true;
        }
    }

    private void OnRecentMenuOpened(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentSession recent)
        {
            ((ContextMenu)sender).IsOpen = false;
            return;
        }

        // Compte d'un autre PVWA, supprimé, ou accès d'urgence sans CyberArk : rien à ajouter.
        var account = recent.IsForHost(PvwaHost) ? _byId.GetValueOrDefault(recent.AccountId) : null;
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>().Where(i => i.Tag as string == "addcurrent"))
        {
            BuildAddToCurrentMenu(item, account is null ? null : folder => AddToCurrent(account, recent, folder));
            item.IsEnabled = account is not null;
            item.ToolTip = account is null ? Text.Format(Strings.AccountGone, recent.Label) : null;
        }
    }

    private void ConnectRecent()
    {
        if (RecentList.SelectedItem is not RecentSession recent)
        {
            return;
        }

        if (!recent.IsForHost(PvwaHost) || !_byId.TryGetValue(recent.AccountId, out var account))
        {
            SetStatus(MissingAccountText(Text.Format(Strings.AccountGone, recent.Label)), isError: true);
            return;
        }

        SetCurrent(account);
        var mode = SessionLibrary.RecentMode(recent.Mode);
        var request = mode == ConnectMode.Psm
            ? DefaultRequest(account, ConnectMode.Psm) with { Component = recent.Mode, RemoteMachine = recent.RemoteMachine }
            : DefaultRequest(account, mode) with { RemoteMachine = recent.RemoteMachine };
        _ = ConnectAsync(account, request, showDialog: false);
    }

    private void OnConnectDefault(object sender, RoutedEventArgs e)
    {
        if (_currentKeePass is { } entry)
        {
            _ = ConnectKeePassAsync(entry, null);
        }
        else if (_currentSaved is { } saved)
        {
            ConnectSaved(saved, advanced: false);
        }
        else if (_current is { } account)
        {
            _ = ConnectAsync(account, DefaultRequest(account, null));
        }
    }

    private void OnConnectPsm(object sender, RoutedEventArgs e)
    {
        if (_current is { } account)
        {
            _ = ConnectAsync(account, DefaultRequest(account, ConnectMode.Psm));
        }
    }

    private void OnConnectSsh(object sender, RoutedEventArgs e)
    {
        if (_current is { } account)
        {
            _ = ConnectAsync(account, DefaultRequest(account, ConnectMode.Ssh));
        }
    }

    private void OnConnectSftp(object sender, RoutedEventArgs e)
    {
        if (_current is { } account)
        {
            _ = ConnectAsync(account, DefaultRequest(account, ConnectMode.Sftp));
        }
    }

    private void OnConnectAdvanced(object sender, RoutedEventArgs e)
    {
        if (_currentSaved is { } saved)
        {
            ConnectSaved(saved, advanced: true);
        }
        else if (_current is { } account)
        {
            _ = ConnectAsync(account, DefaultRequest(account, null), showDialog: true);
        }
    }

    /// <summary>
    /// Requête par défaut, d'après la plateforme (si le PSMP est renseigné) : fichiers seuls en SFTP pour une plateforme
    /// « SFTP », SSH via le PSMP pour une plateforme « SSH » ou Unix, sinon PSM avec le composant mémorisé.
    /// « Connexion avancée » permet d'en choisir un autre.
    /// </summary>
    private ConnectRequest DefaultRequest(PvwaAccount account, ConnectMode? mode)
    {
        var effective = mode ?? AccountClassifier.DefaultMode(account, HasPsmp);
        var machines = AccountClassifier.RemoteMachineList(account);
        return new ConnectRequest(effective, _settings.ResolveComponent(account), machines.Count == 1 ? machines[0] : null);
    }

    /// <summary>
    /// Ouvre la session. La fenêtre « Connexion avancée » s'affiche si elle est demandée, s'il faut choisir
    /// la machine cible, ou si le PVWA refuse la demande (motif exigé, composant inconnu...) pour corriger et réessayer.
    /// </summary>
    private async Task ConnectAsync(PvwaAccount account, ConnectRequest request, bool showDialog = false, SavedSession? saved = null)
    {
        if (_connecting || _loggedOff)
        {
            return;
        }

        if (request.Mode != ConnectMode.Psm && !HasPsmp)
        {
            SetStatus(request.Mode == ConnectMode.Sftp ? Strings.SftpUnavailable : Strings.SshUnavailable, isError: true);
            return;
        }

        // Compte de domaine sans serveur choisi : on demande lequel, avec l'option de le garder dans « Mes serveurs ».
        string? keepFolder = null;
        bool needsMachine = AccountClassifier.NeedsRemoteMachine(account, _domains);
        if (!showDialog && needsMachine && string.IsNullOrWhiteSpace(request.RemoteMachine))
        {
            if (AskServer(account, adding: false) is not { } choice)
            {
                SetStatus("");
                return;
            }

            request = request with { RemoteMachine = choice.Server.Length > 0 ? choice.Server : null };
            keepFolder = choice.Keep ? choice.Folder : null;
            showDialog = choice.Advanced;
        }

        string? error = null;
        bool componentError = false;
        _connecting = true;
        UpdateActions();
        try
        {
            while (true)
            {
                if (showDialog)
                {
                    var dialog = new ConnectDialog(account, request, _settings, _vaultUser, error, componentError, needsMachine)
                    {
                        Owner = this,
                    };
                    if (dialog.ShowDialog() != true || dialog.Result is null)
                    {
                        SetStatus("");
                        return;
                    }

                    request = dialog.Result;
                    if (request.RememberComponent)
                    {
                        _settings.RememberComponent(account.PlatformId, request.Component);
                        SaveSettings();
                    }
                }

                try
                {
                    // Rien d'ouvert (aucun PSMP pour ce serveur) : rien à garder dans « Mes serveurs ».
                    if (await LaunchAsync(account, request, saved) && keepFolder is not null && request.RemoteMachine is { } machine
                        && SessionLibrary.FindSession(_settings, account, PvwaHost, machine) is null)
                    {
                        ShowAddedToCurrent(SessionLibrary.AddConnection(_settings, account, PvwaHost, keepFolder, request.Mode,
                            request.Component, machine));
                    }

                    return;
                }
                catch (PvwaException ex) when (ex.IsUnauthorized)
                {
                    if (!Reconnect(userAction: true))
                    {
                        return;
                    }

                    // Session rouverte : la même connexion est relancée.
                    continue;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is PvwaException or ArgumentException or HttpRequestException
                                               or TaskCanceledException or IOException or UnauthorizedAccessException
                                               or Win32Exception or InvalidOperationException)
                {
                    // Composant absent de la plateforme du compte (il peut porter un autre nom, par ex. WIN-PSM) :
                    // le dialogue le dit et invite à saisir le bon.
                    componentError = ex is PvwaException { IsUnknownComponent: true };
                    error = componentError
                        ? Text.Format(Strings.PsmComponentUnknown, request.Component, account.PlatformId)
                        : ErrorText.Describe(ex);
                    SetStatus(Text.Format(Strings.ConnectFailed, error), isError: true);
                    showDialog = true;
                }
            }
        }
        finally
        {
            _connecting = false;
            UpdateActions();
        }
    }

    /// <returns>Faux si rien n'a été ouvert (aucun PSMP pour ce serveur, erreur affichée).</returns>
    private async Task<bool> LaunchAsync(PvwaAccount account, ConnectRequest request, SavedSession? saved)
    {
        var target = string.IsNullOrWhiteSpace(request.RemoteMachine) ? account.Address : request.RemoteMachine.Trim();
        var label = $"{account.UserName}@{target}";
        if (request.Mode == ConnectMode.Psm)
        {
            SetStatus(Text.Format(Strings.PsmOpening, label, request.Component));
            LoadProgress.IsIndeterminate = true;
            LoadProgress.Visibility = Visibility.Visible;
            var options = new PsmConnectOptions
            {
                ConnectionComponent = request.Component,
                Reason = request.Reason,
                TicketingSystemName = request.TicketingSystem,
                TicketId = request.TicketId,
                RemoteMachine = request.RemoteMachine,
            };
            byte[] rdp;
            DebugLog.Write("psm", $"Demande de connexion PSM pour {label} (compte {account.Id}, composant {request.Component}, machine cible « {request.RemoteMachine} »)");
            try
            {
                rdp = await Client.PsmConnectAsync(account.Id, options, _lifetime.Token);
            }
            finally
            {
                LoadProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
            }

            LogRdpFile(rdp);
            AddRecent(account, label, request.Component, request.RemoteMachine);
            if (PsmTabSettings(rdp, label, out var mstscReason) is { } settings)
            {
                await OpenPsmTabAsync(account, request, options, saved, label, rdp, settings);
            }
            else
            {
                // Session PSM dans Connexion Bureau à distance (mstsc), en fenêtre(s) à part : le fichier du PVWA tel quel.
                _launcher.LaunchRdp(rdp, label);
                SetStatus(mstscReason ?? Text.Format(Strings.PsmStarted, label, request.Component));
                InterventionRecord(Core.Interventions.InterventionKind.Connection, label, InterventionPsmText(request, inTab: false));
            }
        }
        else
        {
            // SSH et SFTP : PSMP du domaine du serveur, sinon PSMP par défaut.
            var psmp = PsmpRouting.Resolve(_settings, target);
            if (psmp is null)
            {
                SetStatus(Text.Format(Strings.PsmpNoRoute, target), isError: true);
                return false;
            }

            DebugLog.Write("ssh", $"{label} : PSMP {psmp.Host}:{psmp.Port} ({(psmp.Domain is null ? "repli" : $"domaine {psmp.Domain}")})");
            var login = PsmpTarget.BuildLogin(_vaultUser, account, request.RemoteMachine);
            Func<Task> duplicate = () => ConnectAsync(account, request, saved: saved);
            if (request.Mode == ConnectMode.Sftp)
            {
                // Fichiers seuls : une session PSMP SFTP, sans terminal, toujours dans l'application.
                OpenPsmpFilesTab(account, psmp, login, label, saved, duplicate, request);
                AddRecent(account, label, RecentModes.Sftp, request.RemoteMachine);
            }
            else if (_settings.SshInApp)
            {
                OpenSshTab(account, psmp, login, label, saved, duplicate, request);
                AddRecent(account, label, RecentModes.Ssh, request.RemoteMachine);
            }
            else
            {
                _launcher.LaunchSsh(login, psmp.Host, psmp.Port, label);
                SetStatus(Text.Format(Strings.SshStarted, label, psmp.Host));
                InterventionRecord(Core.Interventions.InterventionKind.Connection, label, Text.Format(Strings.InterventionSshExternal, psmp.Host));
                AddRecent(account, label, RecentModes.Ssh, request.RemoteMachine);
            }
        }

        return true;
    }

    /// <summary>
    /// Session PSM (bureau Windows) dans un onglet, avec le contrôle Bureau à distance, à la taille de l'onglet ; Windows
    /// vérifie le certificat du PSM comme avec mstsc (voir <see cref="RdpConnectionSettings.ForPsmTab"/>). Chaque
    /// reconnexion demande un nouveau fichier au PVWA (le jeton d'une session PSM ne sert qu'une fois). Contrôle
    /// inutilisable à la première connexion : le jeton n'a pas servi, le fichier est confié à mstsc.
    /// </summary>
    private async Task OpenPsmTabAsync(PvwaAccount account, ConnectRequest request, PsmConnectOptions options, SavedSession? saved,
        string label, byte[] rdp, RdpConnectionSettings settings)
    {
        SetStatus(Text.Format(Strings.PsmStarted, label, request.Component));
        InterventionRecord(Core.Interventions.InterventionKind.Connection, label, InterventionPsmText(request, inTab: true));
        byte[]? first = rdp;
        var session = await OpenRdpTabAsync(label, async ct =>
        {
            var file = first;
            first = null;
            var next = settings;
            if (file is null)
            {
                DebugLog.Write("psm", $"Nouvelle demande de connexion PSM pour {label} (reconnexion)");
                file = await Client.PsmConnectAsync(account.Id, options, ct);
                LogRdpFile(file);
                next = RdpConnectionSettings.FromRdpFile(file);
                if (next.IsRemoteApp)
                {
                    throw new InvalidOperationException(Strings.PsmTabRemoteAppNow);
                }

                next = next.ForPsmTab();
            }

            return new RdpConnectionRequest(next, null);
        }, () => ConnectAsync(account, request, saved: saved), TagOf(account, saved, request.RemoteMachine), settings.Server);

        if (session.ControlFailed && session.ConnectedAt is null && MainTabs.Items.OfType<TabItem>().Any(t => t.Tag == session))
        {
            // Contrôle Bureau à distance inutilisable sur ce poste : rien n'a été envoyé au PSM, mstsc prend le relais.
            DebugLog.Write("psm", $"Contrôle Bureau à distance inutilisable ({session.Error}) : session PSM ouverte avec mstsc.");
            var error = session.Error;
            await RemoveRdpTabAsync(session);
            _launcher.LaunchRdp(rdp, label);
            SetStatus(Text.Format(Strings.PsmTabFallback, label, error));
            InterventionRecord(Core.Interventions.InterventionKind.Connection, label, InterventionPsmText(request, inTab: false));
        }
    }

    /// <summary>Fichier .rdp renvoyé par le PVWA, dans le journal de débogage (jetons et signature masqués).</summary>
    private static void LogRdpFile(byte[] rdp)
    {
        if (DebugLog.Enabled)
        {
            DebugLog.Write("psm", $"Fichier .rdp reçu du PVWA ({rdp.Length} octets) :\n{DebugLog.DescribeRdpFile(RdpConnectionSettings.ReadFile(rdp))}");
        }
    }

    private void AddRecent(PvwaAccount account, string label, string mode, string? remoteMachine)
    {
        _settings.AddRecent(new RecentSession
        {
            AccountId = account.Id,
            PvwaHost = PvwaHost,
            Label = label,
            Mode = mode,
            RemoteMachine = remoteMachine,
            When = DateTime.Now,
        });
        SaveSettings();
        RefreshRecent();
    }

    /// <summary>
    /// Message de la barre d'état. Une erreur reste jusqu'au message suivant ; un message ordinaire s'efface après
    /// <see cref="StatusLifetime"/> pour ne pas rester hors de propos (« Session ouverte » longtemps après).
    /// </summary>
    private void SetStatus(string message, bool isError = false)
    {
        _statusClear ??= CreateStatusClearTimer();
        _statusClear.Stop();
        if (!isError && message.Length > 0)
        {
            _statusClear.Start();
        }

        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "StatusErrorForeground" : "StatusOkForeground");
        // Annoncé par les lecteurs d'écran (zone « polie » de la barre d'état).
        if (message.Length > 0 && System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusText) is { } peer)
        {
            peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
    }

    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(10);
    private DispatcherTimer? _statusClear;

    private DispatcherTimer CreateStatusClearTimer()
    {
        var timer = new DispatcherTimer { Interval = StatusLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            StatusText.Text = "";
        };
        return timer;
    }

    // ===================== Clavier =====================

    /// <summary>
    /// Raccourcis réservés à l'application, interceptés avant le terminal (qui enverrait sinon Tab au serveur) :
    /// Ctrl+Tab et Ctrl+Maj+Tab changent d'onglet, Ctrl+F4 et Ctrl+Maj+W ferment l'onglet de session, Ctrl+1/2/3 mènent
    /// aux onglets de gauche (depuis une session aussi : F6 reste aux applications du serveur). La touche Menu (ou Maj+F10)
    /// sur un onglet ouvre son menu.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        if (ctrl && !alt && key == Key.Tab)
        {
            SelectAdjacentTab(shift ? -1 : 1);
            e.Handled = true;
        }
        else if (ctrl && !alt && ((key == Key.F4 && !shift) || (key == Key.W && shift)))
        {
            if (MainTabs.SelectedItem is TabItem { Tag: not null } tab)
            {
                CloseSessionTab(tab);
            }

            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && key is Key.D1 or Key.NumPad1 or Key.D2 or Key.NumPad2 or Key.D3 or Key.NumPad3)
        {
            if ((key is Key.D1 or Key.NumPad1 && IsOffline) || (key is Key.D3 or Key.NumPad3 && FilesTab.Visibility != Visibility.Visible))
            {
                return;
            }

            ShowSideTab(key is Key.D1 or Key.NumPad1 ? AvailableTab : key is Key.D2 or Key.NumPad2 ? CurrentTab : FilesTab);
            e.Handled = true;
        }
        else if ((key == Key.Apps || (shift && key == Key.F10)) && Keyboard.FocusedElement is TabItem { Header: FrameworkElement { ContextMenu: { } menu } header })
        {
            menu.PlacementTarget = header;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Raccourcis de la fenêtre, quand le terminal ne les a pas pris : Ctrl+K connexion rapide, Ctrl+virgule Paramètres,
    /// Ctrl+B replie ou déplie le panneau de gauche, F6 bascule entre le panneau de gauche et la session.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.K:
                    FocusQuickConnect();
                    e.Handled = true;
                    break;
                case Key.OemComma:
                    OnSettings(this, e);
                    e.Handled = true;
                    break;
                case Key.B:
                    ToggleSidePanel();
                    e.Handled = true;
                    break;
            }
        }
        else if (mods == ModifierKeys.None && key == Key.F6)
        {
            ToggleFocusArea();
            e.Handled = true;
        }
    }

    /// <summary>Onglet de la zone principale suivant (<paramref name="step"/> = 1) ou précédent (-1), en boucle.</summary>
    private void SelectAdjacentTab(int step)
    {
        var tabs = MainTabs.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).ToList();
        if (tabs.Count < 2)
        {
            return;
        }

        int index = MainTabs.SelectedItem is TabItem current ? tabs.IndexOf(current) : -1;
        MainTabs.SelectedItem = tabs[((index + step) % tabs.Count + tabs.Count) % tabs.Count];
    }

    private void FocusQuickConnect()
    {
        if (IsOffline)
        {
            ShowSideTab(CurrentTab);
            return;
        }

        MainTabs.SelectedItem = HomeTab;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            QuickBox.Focus();
            QuickBox.SelectAll();
        });
    }

    /// <summary>Affiche un onglet de gauche et place le curseur dans son champ principal.</summary>
    private void ShowSideTab(TabItem tab)
    {
        SetSidePanelCollapsed(false);
        SideTabs.SelectedItem = tab;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (tab == AvailableTab)
            {
                SearchBox.Focus();
            }
            else if (tab == CurrentTab)
            {
                SavedSearchBox.Focus();
            }
            else
            {
                FilesPanel.FocusList();
            }
        });
    }

    /// <summary>F6 : du panneau de gauche à la session affichée, et retour.</summary>
    private void ToggleFocusArea()
    {
        if (SideTabs.IsKeyboardFocusWithin)
        {
            FocusCurrentSession();
        }
        else
        {
            ShowSideTab((TabItem)SideTabs.SelectedItem);
        }
    }

    // ===================== Menu contextuel, favoris, copie =====================

    private void OnAccountMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var account = _current;
        // Clic droit sur un safe (« Disponibles » groupé par safe) : seulement ses membres ; sur un autre dossier : rien.
        var safe = account is not null ? account.SafeName : SafeOfFolder(menu.PlacementTarget);
        if (account is null && safe is null)
        {
            menu.IsOpen = false;
            return;
        }

        foreach (var element in menu.Items.OfType<FrameworkElement>())
        {
            element.Visibility = account is null && element.Tag as string is not ("safemembers" or "addaccount" or "importaccounts")
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // Action du double-clic en gras : PSM, SSH ou fichiers seuls selon la plateforme.
        var defaultTag = account is null ? null : AccountClassifier.DefaultMode(account, HasPsmp) switch
        {
            ConnectMode.Ssh => "ssh",
            ConnectMode.Sftp => "sftp",
            _ => "psm",
        };
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Tag as string is "psm" or "ssh" or "sftp"))
        {
            item.FontWeight = item.Tag as string == defaultTag ? FontWeights.SemiBold : FontWeights.Normal;
        }

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "addcurrent" when account is not null:
                    BuildAddToCurrentMenu(item, folder => AddToCurrent(account, folder));
                    break;
                case "ssh":
                    item.IsEnabled = HasPsmp;
                    item.ToolTip = HasPsmp ? null : Strings.SetPsmpAddress;
                    break;
                case "sftp":
                    item.IsEnabled = HasPsmp;
                    item.ToolTip = HasPsmp ? Strings.MenuConnectSftpTip : Strings.SetPsmpAddress;
                    break;
                case "safemembers":
                    SetSafeMenuItem(item, safe, Strings.MenuSafeMembersOf, Strings.MenuSafeMembers);
                    break;
                case "addaccount":
                    SetSafeMenuItem(item, safe, Strings.MenuAddAccountTo, Strings.MenuAddAccount);
                    break;
                case "importaccounts":
                    item.CommandParameter = safe;
                    item.IsEnabled = _client is not null;
                    break;
            }
        }
    }

    /// <summary>Entrée de menu qui porte sur un safe : son nom dans le libellé, désactivée sans safe ou sans CyberArk.</summary>
    private void SetSafeMenuItem(MenuItem item, string? safe, string format, string fallback)
    {
        bool available = _client is not null && !string.IsNullOrWhiteSpace(safe);
        // TextBlock : un « _ » dans le nom du safe n'est pas un raccourci clavier.
        item.Header = new TextBlock { Text = available ? Text.Format(format, safe) : fallback };
        item.CommandParameter = safe;
        item.IsEnabled = available;
    }

    /// <summary>Safe d'un dossier de l'arbre « Disponibles » quand les comptes sont groupés par safe, sinon null.</summary>
    private string? SafeOfFolder(UIElement? target) =>
        _settings.GroupBy == GroupBy.Safe && target is TreeViewItem { DataContext: FolderNode folder }
            && folder.Children.FirstOrDefault()?.Account.SafeName is { } safe && !string.IsNullOrWhiteSpace(safe)
            ? safe
            : null;

    private void OnShowSafeMembers(object sender, RoutedEventArgs e) => ShowSafeMembers((sender as MenuItem)?.CommandParameter as string);

    /// <summary>Membres du safe et leurs droits, lus sur le PVWA (droit « View Safe Members » nécessaire).</summary>
    private void ShowSafeMembers(string? safeName)
    {
        if (_client is not { } client || string.IsNullOrWhiteSpace(safeName))
        {
            return;
        }

        var actions = new SafeMemberActions(
            (member, ct) => client.AddSafeMemberAsync(safeName, member, ct),
            (member, ct) => client.UpdateSafeMemberAsync(safeName, member, ct),
            (name, ct) => client.RemoveSafeMemberAsync(safeName, name, ct));
        var dialog = new SafeMembersDialog(safeName, ct => client.GetSafeMembersAsync(safeName, ct), actions) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SessionExpired)
        {
            OnSessionExpired();
        }
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e) => CopyCurrent(a => a.Address ?? "");

    private void OnCopyUser(object sender, RoutedEventArgs e) => CopyCurrent(a => a.UserName ?? "");

    private void OnCopyDomainUser(object sender, RoutedEventArgs e) =>
        CopyCurrent(a => a.LogonDomain.Length > 0 ? $"{a.LogonDomain}\\{a.UserName}" : a.UserName ?? "");

    private void CopyCurrent(Func<PvwaAccount, string> selector)
    {
        if (_current is { } account)
        {
            Clipboard.SetText(selector(account));
        }
    }

    // ===================== Barre d'outils =====================

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var rows = _shown;
        if (rows.Count == 0)
        {
            MessageBox.Show(this, Strings.NothingToExport, "ZillaTerm", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Strings.ExportTitle,
            Filter = Strings.ExportFilter,
            FileName = $"{Strings.ExportFileName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // BOM UTF-8 pour qu'Excel détecte correctement les accents.
            using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            CsvExporter.Write(writer, rows);
            SetStatus(Text.Format(Strings.Exported, rows.Count, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Text.Format(Strings.ExportFailed, ex.Message), "ZillaTerm", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var language = _settings.Language;
        var centralFile = _settings.EnvironmentFile;
        var accepted = new SettingsDialog(_settings, _keePass.Store) { Owner = this }.ShowDialog() == true;
        // Coffre local supprimé ou créé depuis les paramètres : l'arbre « Mes serveurs » peut changer.
        RefreshSaved();
        if (accepted)
        {
            SaveSettings();
            ZillaTerm.App.Terminal.TerminalAppearance.Apply(_settings.TerminalTheme, _settings.TerminalFontSize, _settings.TerminalRightClickPastes);
            Palette.Choose(Application.Current, _settings.Theme);
            UpdateActions();
            StartKeepAlive();
            SetStatus(_settings.Language == language ? Strings.SettingsSaved : Strings.SettingsSavedLanguage);
            // Nouveau fichier central : proposé tout de suite plutôt qu'au prochain démarrage.
            if (_settings.EnvironmentFile.Length > 0 && !string.Equals(_settings.EnvironmentFile, centralFile, StringComparison.OrdinalIgnoreCase))
            {
                OfferEnvironment(_settings.EnvironmentFile);
            }
        }
    }

    // ===================== Environnement partagé =====================

    /// <summary>Importe un fichier d'environnement : changements montrés puis appliqués après confirmation.</summary>
    private void OnImportEnvironment(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = EnvironmentImport.FileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            OfferEnvironment(dialog.FileName);
        }
    }

    private void OfferEnvironment(string path)
    {
        var pvwa = _settings.PvwaUrl;
        if (!EnvironmentImport.Offer(this, _settings, path, automatic: false))
        {
            return;
        }

        UpdateActions();
        StartKeepAlive();
        RefreshSaved();
        SetStatus(string.Equals(pvwa, _settings.PvwaUrl, StringComparison.Ordinal) ? Strings.EnvApplied : Strings.EnvAppliedNextLogin);
    }

    /// <summary>
    /// Exporte l'environnement de l'équipe (PVWA, PSMP, composants, listes partagées, clés des PSMP) : jamais de mot de
    /// passe ni de donnée personnelle.
    /// </summary>
    private void OnExportEnvironment(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = EnvironmentProfile.FileName, Filter = EnvironmentImport.FileFilter, DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            EnvironmentProfile.FromSettings(_settings, PvwaHost).Save(dialog.FileName);
            SetStatus(Text.Format(Strings.EnvExported, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Text.Format(Strings.EnvExportFailed, ex.Message), isError: true);
        }
    }

    /// <summary>
    /// Bouton Historique : envois et téléchargements de l'onglet Fichiers, même sans session ; seulement les échecs et
    /// transferts non vérifiés quand un échec est signalé (« ! ») et pas encore vu.
    /// </summary>
    private void OnTransferHistory(object sender, RoutedEventArgs e)
    {
        bool problems = FilesPanel.UnseenProblems > 0;
        // L'historique montre les transferts en échec : ils sont vus.
        FilesPanel.MarkTransfersSeen();
        UpdateFilesBadge();
        FilesPanel.ShowHistory(problemsOnly: problems);
    }

    /// <summary>Affiche le terminal d'une session : son onglet, la vue parallèle ou sa fenêtre séparée.</summary>
    private void ShowTerminal(SshSession session)
    {
        if (_detached.TryGetValue(session, out var window))
        {
            window.Activate();
            window.View.FocusTerminal();
        }
        else if (_parallel?.ViewOf(session) is { } inParallel)
        {
            ShowParallel();
            inParallel.FocusTerminal();
        }
        else if (TabOf(session) is { } tab)
        {
            MainTabs.SelectedItem = tab;
            (tab.Content as SshSessionView)?.FocusTerminal();
        }
    }

    /// <summary>Bouton Paramètres : menu sous le bouton (paramètres, journal de débogage).</summary>
    private void OnSettingsMenu(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("SettingsMenu");
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSettingsMenuOpened(object sender, RoutedEventArgs e)
    {
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>().Where(i => i.Tag as string == "debuglog"))
        {
            item.IsChecked = _settings.DebugLogEnabled;
        }

        // Accès d'urgence : pas de compte du PVWA auquel rattacher des sessions importées.
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>().Where(i => i.Tag as string == "sessionimport"))
        {
            item.Visibility = IsOffline ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnToggleDebugLog(object sender, RoutedEventArgs e)
    {
        _settings.DebugLogEnabled = ((MenuItem)sender).IsChecked;
        AppDebugLog.Apply(_settings);
        SaveSettings();
        UpdateDebugLogIndicator();
        SetStatus(DebugLog.FilePath is { } path ? Text.Format(Strings.DebugLogStarted, path) : Strings.DebugLogStopped);
    }

    /// <summary>Ouvre l'Explorateur sur le fichier du journal, pour le relire ou le joindre à un message.</summary>
    private void OnShowDebugLog(object sender, RoutedEventArgs e)
    {
        var path = DebugLog.FilePath ?? DebugLog.DefaultPath;
        if (!File.Exists(path))
        {
            SetStatus(Strings.DebugLogMissing, isError: true);
            return;
        }

        try
        {
            WindowsExplorer.ShowFile(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    /// <summary>Barre d'état : rappel visible tant que le journal de débogage est actif.</summary>
    private void UpdateDebugLogIndicator()
    {
        DebugLogText.Visibility = DebugLog.Enabled ? Visibility.Visible : Visibility.Collapsed;
        DebugLogText.ToolTip = DebugLog.FilePath;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(AppSettings.DefaultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(Text.Format(Strings.PreferencesNotSaved, e.Message), isError: true);
        }
    }

    /// <summary>Action CyberArk refusée, session PVWA expirée : proposition de se reconnecter, sans rien fermer.</summary>
    private void OnSessionExpired() => Reconnect(userAction: true);

    /// <summary>
    /// Session PVWA expirée : fenêtre de reconnexion (même adresse, même utilisateur, même méthode). Les onglets, les
    /// sessions et les transferts en cours restent en place ; seul le jeton du PVWA est renouvelé. Renvoie vrai si la
    /// session est rouverte ; « Plus tard » laisse l'application telle quelle, sans CyberArk jusqu'à la prochaine action.
    /// </summary>
    /// <param name="userAction">
    /// Vrai si l'expiration vient d'une action de l'utilisateur (curseur dans le mot de passe) ; faux quand la fenêtre
    /// s'ouvre d'elle-même (maintien de session).
    /// </param>
    private bool Reconnect(bool userAction)
    {
        if (_client is null || _loggedOff || _reconnecting)
        {
            return false;
        }

        _reconnecting = true;
        _reconnectWhenActive = false;
        _keepAlive?.Stop();
        try
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            var dialog = new ReconnectDialog(_client, _authMethod, _vaultUser, _sessionUser, focusPassword: userAction) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                SetStatus(Strings.KeepAliveExpired, isError: true);
                return false;
            }

            DebugLog.Write("login", "Session PVWA rouverte depuis la fenêtre principale.");
            ForgetMfaRetryDelay();
            StartKeepAlive();
            SetStatus(Strings.ReconnectDone);
            return true;
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        LogoutRequested = true;
        InterventionRecord(Core.Interventions.InterventionKind.Action, "", Strings.InterventionLoggedOut);
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            // Fermeture finale, une fois tout nettoyé.
            return;
        }

        if (_loggedOff)
        {
            // Nettoyage en cours (déconnexion du PVWA, transferts, Bureau à distance) : une deuxième demande de fermeture
            // (croix, Alt+F4, session expirée) ne doit pas le couper.
            e.Cancel = true;
            return;
        }

        if (e.Cancel)
        {
            return;
        }

        if (!ConfirmCloseAll())
        {
            // Fichiers modifiés non renvoyés, sessions Bureau à distance ouvertes ou transferts en cours : l'utilisateur
            // garde la fenêtre.
            e.Cancel = true;
            LogoutRequested = false;
            return;
        }

        RememberLayout();
        SaveSettings();
        // Fermeture de la session PVWA avant de quitter (au plus 5 s d'attente).
        e.Cancel = true;
        _loggedOff = true;
        // Dernier essai, plus insistant : aucun minuteur ne réessaiera après la fermeture.
        _passwordClipboard?.Dispose();
        IsEnabled = false;
        // Transferts annulés d'abord : le fichier interrompu est supprimé tant que la connexion est ouverte.
        await FilesPanel.CancelTransfersAsync(null);
        _lifetime.Cancel();
        _searchDebounce.Stop();
        _savedSearchDebounce.Stop();
        try
        {
            if (_client is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (_mfaKeyIssued)
                {
                    try
                    {
                        await _client.RevokeMfaCachingSshKeyAsync(timeout.Token);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Clé non retirée : elle expirera d'elle-même.
                    }
                }

                await _client.LogoffAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Au pire, la session expirera d'elle-même côté PVWA.
        }
        finally
        {
            _mfaKey = null;
            SshAnswerCache.ForgetAll();
            CloseAllSshSessions();
            // Les coffres KeePass ouverts se referment avec la fenêtre, et le coffre local avec eux : sinon « Accès d'urgence »,
            // sur l'écran de connexion, rouvrirait les coffres retenus sans aucun mot de passe.
            _keePass.LockAll(localStore: true);
            _launcher.Cleanup();
            _client?.Dispose();
            _lifetime.Dispose();
        }

        // Au plus 3 s ; une session bloquée est ensuite sortie de la fenêtre, qui peut alors être détruite sans attendre
        // son thread (déconnexion : l'écran de connexion revient).
        bool released = await CloseAllRdpSessionsAsync();
        if (!LogoutRequested && (!released || RdpSession.ControlWindowsLeft))
        {
            // Quitter : la fenêtre d'un contrôle bloqué (de cette fenêtre ou d'avant une déconnexion) serait détruite
            // avec l'application, en attendant son thread. Tout le reste est déjà fermé (session PVWA, coffres) : on
            // quitte directement.
            App.ExitNow();
        }

        _closeConfirmed = true;
        Close();
    }
}
