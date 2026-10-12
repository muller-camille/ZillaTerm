using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;
using Microsoft.Win32;

namespace ZillaTerm.App.Views;

/// <summary>
/// Onglet latéral « Fichiers » (navigateur SFTP) : parcourt le serveur de la session SSH
/// active, dépose des fichiers par glisser-déposer (SCP ou SFTP), supprime, télécharge, et peut suivre
/// le dossier courant du terminal.
/// </summary>
public partial class FileBrowserPanel : UserControl
{
    private AppSettings _settings = new();
    private Action _saveSettings = () => { };
    private RemoteSession? _session;
    private IRemoteFiles? _browser;
    private int _generation;
    // Filtre du dossier affiché (null : tout est affiché) et dossier auquel il s'applique.
    private FileNameFilter? _filter;
    private string? _shownDirectory;
    private bool _resettingFilter;
    // Lettres tapées dans la liste (sélection par le début du nom).
    private readonly TypeAhead _typeAhead = new();

    /// <summary>
    /// Opérations en cours (renommage, suppression, droits, glisser vers l'Explorateur), par connexion : elles ne bloquent
    /// les autres que sur le même serveur.
    /// </summary>
    private readonly Dictionary<IRemoteFiles, int> _busy = new(ReferenceEqualityComparer.Instance);

    public FileBrowserPanel()
    {
        InitializeComponent();
        InitializeQueue();
        FileList.SelectionChanged += (_, _) => UpdateSelectionButtons();
        FileList.SizeChanged += (_, _) => FitNameColumn();
        // F5 partout dans l'onglet (liste, chemin, filtre, boutons) : relire le dossier, et non recharger les comptes du
        // PVWA (raccourci de la fenêtre, que la commande atteindrait sinon). Aussi dans une fenêtre détachée.
        CommandBindings.Add(new CommandBinding(NavigationCommands.Refresh, (_, e) =>
        {
            _ = RefreshAsync();
            e.Handled = true;
        }));
        InputBindings.Add(new KeyBinding(NavigationCommands.Refresh, Key.F5, ModifierKeys.None));
        ShowMessage(Strings.NoSshSessionHelp, retry: false);
        HeaderText.Text = Strings.NoSshSession;
        UpdateToolbar();
    }

    /// <param name="history">Historique des transferts ; par défaut celui du profil Windows (les tests en donnent un en mémoire).</param>
    public void Initialize(AppSettings settings, Action saveSettings, TransferHistory? history = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _history = history ?? TransferHistory.Load(TransferHistory.DefaultPath);
        HiddenBox.IsChecked = settings.ShowHiddenFiles;
        ShowSortArrow();
        FitNameColumn();
    }

    /// <summary>Étiquette (PROD, QA…) du serveur d'une session, montrée dans l'en-tête du panneau.</summary>
    public Func<RemoteSession, ServerTag?> TagOf { get; set; } = _ => null;

    /// <summary>Associe le panneau à la session de l'onglet actif (ou à aucune).</summary>
    public void Attach(RemoteSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        if (_session is not null)
        {
            if (_session is SshSession previous)
            {
                previous.TerminalDirectoryChanged -= OnTerminalDirectory;
            }

            _session.StateChanged -= OnSessionStateChanged;
        }

        _session = session;
        _browser = null;
        _generation++;
        UpdateStopChmodButton();
        FileList.ItemsSource = null;
        ResetFilter();
        PathBox.Text = "";
        StatusText.Text = "";
        UpdateExtractPanel();
        if (session is null)
        {
            UpdateTailFilesButton();
            HeaderTag.Visibility = Visibility.Collapsed;
            HeaderText.Text = Strings.NoSshSession;
            ShowMessage(Strings.NoSshSessionHelp, retry: false);
            UpdateToolbar();
            return;
        }

        HeaderText.Text = session.Label;
        var tag = TagOf(session);
        HeaderTag.Content = tag is null ? null : ServerTagView.Chip(tag);
        HeaderTag.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateTailFilesButton();
        // Session de fichiers seuls (SFTP, FTP) : pas de terminal à suivre.
        var ssh = session as SshSession;
        FollowBox.Visibility = ssh is not null ? Visibility.Visible : Visibility.Collapsed;
        FollowBox.IsEnabled = ssh?.CanFollowTerminal == true;
        FollowBox.IsChecked = ssh is { CanFollowTerminal: true, FollowTerminal: true };
        FollowBox.ToolTip = ssh?.CanFollowTerminal == true
            ? Strings.FollowTip
            : Strings.FollowDisabledTip;
        if (ssh is not null)
        {
            ssh.TerminalDirectoryChanged += OnTerminalDirectory;
        }

        session.StateChanged += OnSessionStateChanged;
        _ = OpenAsync();
    }

    // ===================== Connexion et navigation =====================

    private async Task OpenAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        int generation = _generation;
        if (session.State != RemoteSessionState.Connected)
        {
            ShowMessage(session.State == RemoteSessionState.Connecting ? ConnectingText(session)
                : session is SshSession ? Strings.SshClosedBrowse
                : Strings.FilesClosedBrowse, retry: false);
            UpdateToolbar();
            return;
        }

        ShowMessage(Strings.SftpOpening, retry: false);
        try
        {
            var browser = await session.GetBrowserAsync();
            if (generation != _generation)
            {
                return;
            }

            _browser = browser;
            HideMessage();
            var start = session.BrowserDirectory
                ?? session.Saved?.StartDirectory
                ?? (session is SshSession { FollowTerminal: true } ssh ? ssh.TerminalDirectory : null)
                ?? browser.HomeDirectory;
            await NavigateAsync(start);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                ShowMessage(Text.Format(Strings.SftpFailed, ErrorText.Describe(ex)), retry: true);
            }
        }
        finally
        {
            UpdateToolbar();
        }
    }

    /// <summary>Affiche le dossier ; faux s'il n'a pas pu être lu.</summary>
    /// <param name="quiet">
    /// Relecture après un transfert : la barre d'état garde le bilan des transferts (seule une erreur la remplace).
    /// </param>
    /// <param name="silent">Pas de message d'erreur (l'appelant affiche le sien) ; une connexion perdue reste signalée.</param>
    private async Task<bool> NavigateAsync(string path, bool quiet = false, bool silent = false)
    {
        var browser = _browser;
        if (browser is null)
        {
            return false;
        }

        int generation = _generation;
        if (!quiet)
        {
            SetStatus(Text.Format(Strings.Reading, path));
        }

        try
        {
            var entries = await browser.ListAsync(path, _settings.ShowHiddenFiles, CancellationToken.None);
            if (generation != _generation)
            {
                return false;
            }

            var directory = browser.CurrentDirectory;
            var items = new List<RemoteEntry>(entries.Count + 1);
            if (directory != "/")
            {
                items.Add(RemoteEntry.ParentLink(directory));
            }

            items.AddRange(RemoteEntry.Sort(entries, _settings.FileSortColumn, _settings.FileSortDescending));
            if (directory != _shownDirectory)
            {
                // Autre dossier : son contenu s'affiche en entier.
                ResetFilter();
            }

            _shownDirectory = directory;
            ShowEntries(items);
            PathBox.Text = directory;
            if (_session is not null)
            {
                _session.BrowserDirectory = directory;
            }

            if (!quiet)
            {
                ShowListSummary();
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != _generation)
            {
                return false;
            }

            PathBox.Text = browser.CurrentDirectory;
            if (!browser.IsConnected)
            {
                _browser = null;
                // Connexion remplacée entre-temps par une autre fonction de la session (envoi, comparaison, édition…) :
                // le panneau reprend la nouvelle au lieu de rester sur l'ancienne, fermée.
                if (_session?.OpenedBrowser is { } current && !ReferenceEquals(current, browser))
                {
                    _browser = current;
                    return await NavigateAsync(path, quiet, silent);
                }

                ShowMessage(Text.Format(Strings.SftpLost, ErrorText.Describe(ex)), retry: true);
            }
            else if (!silent)
            {
                SetStatus(Text.Format(Strings.CannotOpen, path, Describe(ex)), error: true);
            }

            return false;
        }
    }

    private Task RefreshAsync() => _browser is null ? OpenAsync() : NavigateAsync(_browser.CurrentDirectory);

    private void OnTerminalDirectory(string directory)
    {
        if (FollowBox.IsChecked == true && _browser is not null && directory != _browser.CurrentDirectory)
        {
            _ = NavigateAsync(directory);
        }
    }

    private void OnSessionStateChanged()
    {
        if (_session is null)
        {
            return;
        }

        UpdateExtractPanel();

        // Session de fichiers seuls en reconnexion ou fermée : sa connexion est remplacée, le panneau attend la nouvelle.
        if (_session is FilesSession && _session.State != RemoteSessionState.Connected && _browser is not null)
        {
            _browser = null;
        }

        if (_session.State == RemoteSessionState.Connected && _browser is null)
        {
            _ = OpenAsync();
        }
        else if (_session.State is RemoteSessionState.Failed or RemoteSessionState.Closed && _browser is null)
        {
            ShowMessage(NotConnectedText(_session), retry: false);
        }
        else if (_session.State == RemoteSessionState.Connecting && _browser is null)
        {
            ShowMessage(ConnectingText(_session), retry: false);
        }

        UpdateToolbar();
    }

    private static string NotConnectedText(RemoteSession session) =>
        session is SshSession ? Strings.SshNotConnectedBrowse : Strings.FilesClosedBrowse;

    private static string ConnectingText(RemoteSession session) =>
        session is FilesSession files ? Text.Format(Strings.FilesConnectingBrowse, files.Protocol) : Strings.SshConnecting;

    // ===================== Actions =====================

    private void OnParent(object sender, RoutedEventArgs e)
    {
        if (_browser is not null)
        {
            _ = NavigateAsync(RemotePath.Parent(_browser.CurrentDirectory));
        }
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_browser is not null)
        {
            _ = NavigateAsync(_browser.HomeDirectory);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnRetry(object sender, RoutedEventArgs e) => _ = OpenAsync();

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _browser is not null && PathBox.Text.Trim().Length > 0)
        {
            e.Handled = true;
            _ = NavigateAsync(PathBox.Text.Trim());
        }
    }

    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        if (_session is not SshSession session)
        {
            return;
        }

        session.FollowTerminal = FollowBox.IsChecked == true;
        if (session.FollowTerminal)
        {
            // (Ré)installe le suivi dans le shell courant : utile après « sudo -i » ou si l'installation automatique
            // n'a pas pu se faire (sans effet en double si le suivi est déjà actif).
            session.InstallFolderTracking();
            if (session.TerminalDirectory is { } dir)
            {
                _ = NavigateAsync(dir);
            }
        }
    }

    // ===================== Colonnes =====================

    private const double MinNameWidth = 130;

    /// <summary>Largeur de l'indicateur « +2 » des colonnes masquées faute de place.</summary>
    private const double MarkerWidth = 30;

    /// <summary>Largeur de chaque colonne facultative quand elle est affichée (celle choisie en glissant le bord de l'en-tête).</summary>
    private readonly Dictionary<RemoteSortColumn, double> _columnWidths = new()
    {
        [RemoteSortColumn.Size] = 76,
        [RemoteSortColumn.Modified] = 112,
        [RemoteSortColumn.Permissions] = 84,
        [RemoteSortColumn.Owner] = 90,
        [RemoteSortColumn.Group] = 90,
    };

    /// <summary>Colonnes cochées mais masquées faute de place, dans l'ordre des colonnes.</summary>
    private List<RemoteSortColumn> _crowded = [];

    /// <summary>Le panneau doit être élargi de tant de pixels (colonnes masquées faute de place) : la fenêtre s'en charge.</summary>
    public event Action<double>? WidenRequested;

    /// <summary>Colonnes que l'on peut masquer (toutes sauf « Nom »), dans l'ordre d'affichage.</summary>
    private (GridViewColumn Column, RemoteSortColumn Kind, string Text)[] OptionalColumns() =>
    [
        (SizeColumn, RemoteSortColumn.Size, Strings.ColumnSize),
        (ModifiedColumn, RemoteSortColumn.Modified, Strings.ColumnModified),
        (PermissionsColumn, RemoteSortColumn.Permissions, Strings.ColumnPermissions),
        (OwnerColumn, RemoteSortColumn.Owner, Strings.ColumnOwner),
        (GroupColumn, RemoteSortColumn.Group, Strings.ColumnGroup),
    ];

    private bool IsChosen(RemoteSortColumn kind) => !_settings.HiddenFileColumns.Contains(kind);

    /// <summary>Pixels qui manquent au panneau pour afficher toutes les colonnes cochées (0 si elles tiennent).</summary>
    internal double MissingWidth =>
        _crowded.Count == 0 || FileList.ActualWidth <= 0 ? 0
        : Math.Ceiling(MinNameWidth + OptionalColumns().Where(c => IsChosen(c.Kind)).Sum(c => _columnWidths[c.Kind])
                       + SystemParameters.VerticalScrollBarWidth + 8 - FileList.ActualWidth);

    /// <summary>
    /// Colonne « Nom » élastique : elle prend la largeur laissée par les autres colonnes (panneau élargi ou rétréci).
    /// Les colonnes décochées (menu de l'en-tête) sont masquées. Panneau trop étroit : les colonnes « Groupe »,
    /// « Propriétaire » puis « Droits » sont masquées, dans cet ordre (elles reviennent en élargissant), plutôt que
    /// coupées ; l'indicateur « +n » au bout de l'en-tête le signale.
    /// </summary>
    private void FitNameColumn()
    {
        foreach (var (column, kind, _) in OptionalColumns())
        {
            if (column.ActualWidth > 0)
            {
                _columnWidths[kind] = column.ActualWidth;
            }
        }

        double available = FileList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
        if (available <= 0)
        {
            return;
        }

        var (shown, crowded) = LayOutColumns(available);
        if (crowded.Count > 0)
        {
            // Place de l'indicateur prise sur les colonnes : une de plus peut ne plus tenir.
            (shown, crowded) = LayOutColumns(available - MarkerWidth);
        }

        foreach (var (column, kind, _) in OptionalColumns())
        {
            column.Width = shown.Contains(kind) ? _columnWidths[kind] : 0;
        }

        _crowded = crowded;
        MoreColumn.Width = crowded.Count > 0 ? MarkerWidth : 0;
        MoreColumnText.Text = "+" + crowded.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var tip = crowded.Count == 0 ? "" : Text.Format(Strings.FilesColumnsHiddenTip,
            string.Join(", ", OptionalColumns().Where(c => crowded.Contains(c.Kind)).Select(c => c.Text)));
        MoreColumnText.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(MoreColumnText, tip);
        NameColumn.Width = Math.Max(MinNameWidth,
            available - shown.Sum(k => _columnWidths[k]) - (crowded.Count > 0 ? MarkerWidth : 0));
    }

    /// <summary>
    /// Colonnes affichées dans <paramref name="available"/> pixels : « Taille » et « Modifié » si elles sont cochées,
    /// puis « Droits », « Propriétaire » et « Groupe » tant qu'il reste la place (les suivantes sont alors masquées).
    /// </summary>
    private (List<RemoteSortColumn> Shown, List<RemoteSortColumn> Crowded) LayOutColumns(double available)
    {
        var shown = new List<RemoteSortColumn>();
        var crowded = new List<RemoteSortColumn>();
        double used = MinNameWidth;
        foreach (var (_, kind, _) in OptionalColumns().Where(c => IsChosen(c.Kind)))
        {
            double width = _columnWidths[kind];
            if (kind is RemoteSortColumn.Size or RemoteSortColumn.Modified || (crowded.Count == 0 && used + width <= available))
            {
                shown.Add(kind);
                used += width;
            }
            else
            {
                crowded.Add(kind);
            }
        }

        return (shown, crowded);
    }

    /// <summary>
    /// Menu des colonnes (clic droit sur l'en-tête, ou clic sur « +n ») : une case par colonne facultative, gardée dans
    /// les réglages ; une colonne cochée mais sans la place est signalée, et « Élargir le panneau » la fait apparaître.
    /// </summary>
    private void OnColumnsMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        var items = new List<(MenuItem Item, RemoteSortColumn Kind)>();
        var widen = new MenuItem { Header = Strings.FilesColumnsWiden };
        // Colonnes sans la place, après chaque case cochée ou décochée (le menu reste ouvert).
        void Mark()
        {
            foreach (var (item, kind) in items)
            {
                item.InputGestureText = _crowded.Contains(kind) ? Strings.FilesColumnNoRoom : "";
            }

            widen.IsEnabled = _crowded.Count > 0;
        }

        foreach (var (_, kind, text) in OptionalColumns())
        {
            var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = IsChosen(kind), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                ShowColumn(kind, item.IsChecked);
                Mark();
            };
            items.Add((item, kind));
            menu.Items.Add(item);
        }

        if (WidenRequested is not null)
        {
            widen.Click += (_, _) => WidenRequested?.Invoke(MissingWidth);
            menu.Items.Add(new Separator());
            menu.Items.Add(widen);
        }

        Mark();
    }

    /// <summary>Ouvert sous « +n » : le clic droit suivant l'ouvre de nouveau sous le pointeur.</summary>
    private void OnColumnsMenuClosed(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.ClearValue(ContextMenu.PlacementProperty);
        menu.ClearValue(ContextMenu.PlacementTargetProperty);
    }

    /// <summary>Affiche ou masque une colonne (menu de l'en-tête) ; le choix est gardé dans les réglages.</summary>
    internal void ShowColumn(RemoteSortColumn kind, bool show)
    {
        if (kind == RemoteSortColumn.Name || show == IsChosen(kind))
        {
            return;
        }

        if (show)
        {
            _settings.HiddenFileColumns.Remove(kind);
        }
        else
        {
            _settings.HiddenFileColumns.Add(kind);
        }

        _saveSettings();
        FitNameColumn();
    }

    // ===================== Tri (clic sur un en-tête de colonne) =====================

    /// <summary>
    /// Tri par la colonne cliquée, puis dans l'autre sens au clic suivant (la taille et la date commencent par les plus
    /// grandes et les plus récentes). Dossiers toujours en tête ; le tri est gardé d'un dossier et d'une session à l'autre.
    /// </summary>
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column, Role: not GridViewColumnHeaderRole.Padding } header)
        {
            return;
        }

        if (column == MoreColumn)
        {
            // « +n » : le menu des colonnes, sous l'indicateur.
            var menu = (ContextMenu)FindResource("ColumnsMenu");
            menu.PlacementTarget = header;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            return;
        }

        if (SortColumns().FirstOrDefault(c => c.Column == column) is not { Column: not null } clicked)
        {
            return;
        }

        var sort = clicked.Sort;
        SortBy(sort, sort == _settings.FileSortColumn
            ? !_settings.FileSortDescending
            : sort is RemoteSortColumn.Size or RemoteSortColumn.Modified);
    }

    internal void SortBy(RemoteSortColumn column, bool descending)
    {
        _settings.FileSortColumn = column;
        _settings.FileSortDescending = descending;
        _saveSettings();
        ShowSortArrow();
        if (FileList.ItemsSource is not IEnumerable<RemoteEntry> shown)
        {
            return;
        }

        var selected = FileList.SelectedItems.Cast<RemoteEntry>().ToList();
        var entries = shown.ToList();
        ShowEntries(entries.Where(e => e.IsParentLink)
            .Concat(RemoteEntry.Sort(entries.Where(e => !e.IsParentLink), column, descending))
            .ToList());
        foreach (var entry in selected)
        {
            FileList.SelectedItems.Add(entry);
        }

        if (selected.Count > 0)
        {
            FileList.ScrollIntoView(selected[0]);
        }
    }

    /// <summary>Colonnes triables : colonne de la liste, titre, tri correspondant.</summary>
    private (GridViewColumn Column, string Text, RemoteSortColumn Sort)[] SortColumns() =>
    [
        (NameColumn, Core.Localization.CoreStrings.ColumnName, RemoteSortColumn.Name),
        (SizeColumn, Strings.ColumnSize, RemoteSortColumn.Size),
        (ModifiedColumn, Strings.ColumnModified, RemoteSortColumn.Modified),
        (PermissionsColumn, Strings.ColumnPermissions, RemoteSortColumn.Permissions),
        (OwnerColumn, Strings.ColumnOwner, RemoteSortColumn.Owner),
        (GroupColumn, Strings.ColumnGroup, RemoteSortColumn.Group),
    ];

    /// <summary>
    /// Triangle vers le haut (croissant) ou vers le bas (décroissant) dans l'en-tête de la colonne de tri, dessiné pour
    /// ne pas dépendre de la police. Le sens est aussi dans <c>Tag</c> de l'en-tête.
    /// </summary>
    private void ShowSortArrow()
    {
        foreach (var (column, text, sort) in SortColumns())
        {
            if (sort != _settings.FileSortColumn)
            {
                column.Header = text;
                continue;
            }

            bool descending = _settings.FileSortDescending;
            var arrow = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(descending ? "M0,0 L8,0 L4,4.5 Z" : "M0,4.5 L8,4.5 L4,0 Z"),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MutedBrush");
            column.Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Tag = descending,
                Children = { new TextBlock { Text = text }, arrow },
            };
        }
    }

    /// <summary>Contenu du dossier (avec « .. » en tête), affiché à travers le filtre de nom.</summary>
    private void ShowEntries(List<RemoteEntry> items)
    {
        FileList.ItemsSource = items;
        ApplyFilter();
    }

    /// <summary>« .. » reste toujours affiché, pour remonter même quand rien ne répond au filtre.</summary>
    private void ApplyFilter()
    {
        if (FileList.ItemsSource is null)
        {
            return;
        }

        var filter = _filter;
        CollectionViewSource.GetDefaultView(FileList.ItemsSource).Filter = filter is null
            ? null
            : item => item is RemoteEntry entry && (entry.IsParentLink || filter.Matches(entry.Name));
    }

    /// <summary>Bilan du dossier dans la barre d'état ; avec un filtre, combien d'éléments il laisse affichés.</summary>
    private void ShowListSummary()
    {
        if (FileList.ItemsSource is not IEnumerable<RemoteEntry> shown)
        {
            return;
        }

        var entries = shown.Where(e => !e.IsParentLink).ToList();
        if (_filter is { } filter)
        {
            SetStatus(Text.Format(Strings.FilesFilterSummary, entries.Count(e => filter.Matches(e.Name)), entries.Count));
            return;
        }

        int folders = entries.Count(e => e.IsDirectory);
        SetStatus(Text.Format(Strings.FolderSummary, folders, entries.Count - folders));
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_resettingFilter)
        {
            return;
        }

        _filter = FileNameFilter.Parse(FilterBox.Text);
        ApplyFilter();
        ShowListSummary();
    }

    /// <summary>Filtre vidé sans message : changement de dossier ou de session.</summary>
    private void ResetFilter()
    {
        _resettingFilter = true;
        try
        {
            FilterBox.Clear();
        }
        finally
        {
            _resettingFilter = false;
        }

        _filter = null;
        ApplyFilter();
    }

    /// <summary>Échap vide le filtre ; Entrée ou ↓ passe au premier élément affiché.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0)
        {
            FilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down && FileList.Items.Count > 0)
        {
            var first = FileList.Items.OfType<RemoteEntry>().FirstOrDefault(i => !i.IsParentLink) ?? FileList.Items[0];
            SelectAndFocus(first);
            e.Handled = true;
        }
    }

    /// <summary>Sélectionne seulement <paramref name="item"/>, l'amène à l'écran et lui donne le clavier.</summary>
    private void SelectAndFocus(object item)
    {
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item);
        FileList.UpdateLayout();
        (FileList.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem)?.Focus();
    }

    /// <summary>Ctrl+F dans l'onglet Fichiers : curseur dans le filtre du dossier ; faux sans dossier affiché.</summary>
    public bool FocusFilter()
    {
        if (!FilterBox.IsEnabled || !FilterBox.IsVisible)
        {
            return false;
        }

        FilterBox.Focus();
        FilterBox.SelectAll();
        return true;
    }

    private void OnHiddenChanged(object sender, RoutedEventArgs e)
    {
        _settings.ShowHiddenFiles = HiddenBox.IsChecked == true;
        _saveSettings();
        _ = RefreshAsync();
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: RemoteEntry entry })
        {
            e.Handled = true;
            Open(entry);
        }
    }

    private void OnOpenSelected(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is RemoteEntry entry)
        {
            Open(entry);
        }
    }

    private void Open(RemoteEntry entry) => _ = OpenEntryAsync(entry);

    /// <summary>
    /// Double-clic, Entrée ou « Ouvrir » : un dossier s'affiche, un fichier texte s'ouvre dans l'éditeur (renvoyé au
    /// serveur à l'enregistrement) ; une archive, une image, un exécutable (d'après son nom ou ses premiers octets) est
    /// téléchargé.
    /// </summary>
    private async Task OpenEntryAsync(RemoteEntry entry)
    {
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.FullPath);
            return;
        }

        var browser = _browser;
        if (_session?.Editor is not { } editor || browser is null || EditedFile.LooksBinary(entry.Name))
        {
            RequestDownload([entry]);
            return;
        }

        if (entry.Length > 0 || entry.IsSymbolicLink)
        {
            byte[] head;
            try
            {
                head = await browser.ReadAsync(entry.FullPath, 0, EditedFile.SniffLength, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Autre session affichée entre-temps : rien à faire dans celle-ci. Lien vers un dossier que le serveur FTP
                // n'a pas résolu (dossier aux nombreux liens) : on y entre.
                if (_browser != browser || (entry.IsSymbolicLink && await NavigateAsync(entry.FullPath, silent: true)))
                {
                    return;
                }

                SetStatus(Text.Format(Strings.CannotOpen, entry.DisplayName, Describe(ex)), error: true);
                return;
            }

            if (_browser != browser)
            {
                // Autre session (vue parallèle) ou autre connexion affichée pendant la lecture : le téléchargement
                // prendrait le fichier du même nom sur l'autre serveur.
                return;
            }

            if (EditedFile.LooksBinary(head))
            {
                RequestDownload([entry]);
                return;
            }
        }

        await editor.EditAsync(entry);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FileList.SelectedItem is RemoteEntry entry:
                Open(entry);
                e.Handled = true;
                break;
            case Key.Delete:
                _ = DeleteAsync();
                e.Handled = true;
                break;
            case Key.F4:
                OnEdit(sender, e);
                e.Handled = true;
                break;
            case Key.F2:
                _ = RenameAsync();
                e.Handled = true;
                break;
            case Key.Back:
                OnParent(sender, e);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Lettres tapées dans la liste : sélection de l'élément dont le nom commence ainsi, comme dans l'Explorateur (la même
    /// lettre de nouveau passe au suivant).
    /// </summary>
    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0 || e.Text.Any(char.IsControl))
        {
            return;
        }

        var items = FileList.Items.OfType<RemoteEntry>().ToList();
        // Point de départ : l'élément qui a le clavier (sélection multiple), sinon la sélection.
        var focused = Keyboard.FocusedElement is ListViewItem container
            ? FileList.ItemContainerGenerator.ItemFromContainer(container)
            : FileList.SelectedItem;
        int current = items.FindIndex(i => ReferenceEquals(i, focused));
        int index = _typeAhead.Find(items.ConvertAll(i => i.DisplayName), current, e.Text, DateTime.UtcNow);
        if (index >= 0)
        {
            SelectAndFocus(items[index]);
        }

        e.Handled = true;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        bool files = selected.Count > 0 && selected.All(s => !s.IsDirectory);
        foreach (var item in ((ContextMenu)sender).Items.OfType<MenuItem>())
        {
            if (item.Tag is "tailadd")
            {
                FillTailAddMenu(item, files);
                continue;
            }

            if (item.Tag is "compare")
            {
                UpdateCompareMenu(item, selected);
                continue;
            }

            item.IsEnabled = item.Tag switch
            {
                "open" => selected.Count == 1,
                "edit" => selected is [{ IsDirectory: false }],
                "tail" => files,
                "rename" => selected.Count == 1,
                "download" or "delete" or "copy" or "chmod" => selected.Count > 0,
                _ => _browser is not null,
            };
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count > 0)
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(s => s.FullPath)));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }
    }

    /// <summary>
    /// Vrai si <paramref name="browser"/> est toujours la connexion affichée : une opération longue qui se termine après
    /// un changement de session n'actualise pas l'autre session et n'écrit pas dans sa barre d'état.
    /// </summary>
    private bool StillShowing(IRemoteFiles browser, int generation) => generation == _generation && ReferenceEquals(_browser, browser);

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var browser = _browser;
        int generation = _generation;
        if (browser is null)
        {
            return;
        }

        var dialog = new InputDialog(Strings.NewFolder, Text.Format(Strings.NewRemoteFolderPrompt, browser.CurrentDirectory),
            validate: ValidateName) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await browser.CreateDirectoryAsync(RemotePath.Combine(browser.CurrentDirectory, dialog.Value), CancellationToken.None);
            if (StillShowing(browser, generation))
            {
                await NavigateAsync(browser.CurrentDirectory);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (StillShowing(browser, generation))
            {
                SetStatus(Text.Format(Strings.CreateFailed, Describe(ex)), error: true);
            }
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => _ = DeleteAsync();

    private void OnRename(object sender, RoutedEventArgs e) => _ = RenameAsync();

    /// <summary>
    /// Renomme l'élément choisi dans son dossier (F2). Le nouveau nom ne remplace jamais un élément existant : le
    /// serveur n'est pas interrogé si le nom est pris.
    /// </summary>
    private async Task RenameAsync()
    {
        var browser = _browser;
        int generation = _generation;
        if (browser is null || SelectedEntries() is not [var entry] || RefuseIfBusy(browser))
        {
            return;
        }

        var directory = RemotePath.Parent(entry.FullPath);
        var where = Text.Format(Strings.ServerPath, _session?.Label ?? "", RemoteEntry.Visible(directory));
        var dialog = new InputDialog(Strings.RenameTitle, Text.Format(Strings.RenamePrompt, entry.DisplayName, where), entry.Name, ValidateName)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Value == entry.Name || !StillShowing(browser, generation))
        {
            return;
        }

        var target = RemotePath.Combine(directory, dialog.Value);
        BeginBusy(browser);
        try
        {
            await browser.RenameAsync(entry, target, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (StillShowing(browser, generation))
            {
                SetStatus(Text.Format(Strings.RenameFailed, Describe(ex)), error: true);
            }

            return;
        }
        finally
        {
            EndBusy(browser);
        }

        if (StillShowing(browser, generation) && await NavigateAsync(browser.CurrentDirectory))
        {
            if (FileList.ItemsSource is IEnumerable<RemoteEntry> shown && shown.FirstOrDefault(e => e.FullPath == target) is { } renamed)
            {
                FileList.SelectedItem = renamed;
                FileList.ScrollIntoView(renamed);
            }

            SetStatus(Text.Format(Strings.Renamed, entry.DisplayName, dialog.Value));
        }
    }

    /// <summary>Nom d'un élément dans un dossier du serveur : ni « / », ni caractère de contrôle, ni « . » ou « .. ».</summary>
    internal static string? ValidateName(string name) =>
        name.Contains('/') ? Strings.NameNoSlash
        : name.Any(char.IsControl) ? Strings.NameNoControl
        : name is "." or ".." ? Strings.NameReserved
        : null;

    /// <summary>Le contenu d'un dossier du serveur a changé (fichier renvoyé depuis l'éditeur) : actualisation s'il est affiché.</summary>
    public void OnRemoteChanged(RemoteSession session, string directory)
    {
        if (ReferenceEquals(session, _session) && _browser is { } browser && browser.CurrentDirectory == directory && !_busy.ContainsKey(browser))
        {
            _ = NavigateAsync(directory);
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_session?.Editor is { } editor && _browser is not null && SelectedEntries() is [{ IsDirectory: false } entry])
        {
            await editor.EditAsync(entry);
        }
    }

    private void OnPermissions(object sender, RoutedEventArgs e) => _ = ChangePermissionsAsync();

    /// <summary>Modifications récursives des droits en cours, par connexion (bouton « Arrêter » de la barre d'état).</summary>
    private readonly Dictionary<IRemoteFiles, CancellationTokenSource> _chmodCancels = new(ReferenceEqualityComparer.Instance);

    private async Task ChangePermissionsAsync()
    {
        var browser = _browser;
        int generation = _generation;
        var selected = SelectedEntries();
        if (browser is null || selected.Count == 0 || RefuseIfBusy(browser))
        {
            return;
        }

        var target = selected.Count == 1 ? selected[0].DisplayName : Text.Format(Strings.ItemsCount, selected.Count);
        var modes = new List<int>();
        foreach (var entry in selected)
        {
            if (!entry.IsSymbolicLink)
            {
                modes.Add(UnixPermissions.FromSymbolic(entry.Permissions));
                continue;
            }

            // La liste montre les droits du lien lui-même (lrwxrwxrwx) alors que chmod change ceux de sa cible :
            // partir de 777 rendrait la cible accessible à tous si l'on validait sans rien changer.
            int? targetMode;
            try
            {
                targetMode = await browser.GetModeAsync(entry.FullPath, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetStatus(Text.Format(Strings.PermissionsFailed, Describe(ex)), error: true);
                return;
            }

            if (targetMode is not { } resolved)
            {
                SetStatus(Text.Format(Strings.PermissionsLinkUnknown, entry.DisplayName), error: true);
                return;
            }

            modes.Add(resolved);
        }

        var location = Text.Format(Strings.ServerPath, _session?.Label ?? "", browser.CurrentDirectory);
        var owner = Window.GetWindow(this);
        var dialog = new PermissionsDialog(target, location, modes, selected.Any(s => s.IsDirectory && !s.IsSymbolicLink)) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var change = dialog.Change;
        if (change.Set == 0 && change.Clear == 0)
        {
            SetStatus(Strings.PermissionsNothing);
            return;
        }

        // Droits complets : leur valeur octale (755) ; sinon ce qui change (g+w).
        var what = change.CoversAllRwx ? UnixPermissions.ToOctal(change.Set) : change.ToSymbolic();
        if (dialog.Recursive)
        {
            var bullets = new List<string> { Strings.PermissionsRecursiveLinks };
            if (dialog.ExecuteOnlyIfAlready)
            {
                bullets.Add(Strings.PermissionsRecursiveSmart);
            }

            bullets.Add(Strings.PermissionsRecursiveStop);
            if (!ConfirmDialog.Confirm(owner, new ConfirmRequest
                {
                    Title = Strings.PermissionsTitle,
                    Heading = Text.Format(Strings.PermissionsRecursiveHeading, what, target),
                    Subject = location,
                    Bullets = bullets,
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.PermissionsApply],
                }))
            {
                return;
            }
        }

        if (RefuseIfBusy(browser))
        {
            // Autre opération lancée sur ce serveur pendant la lecture des droits d'un lien.
            return;
        }

        var errors = new List<string>();
        int changed = 0;
        bool stopped = false;
        using var cancel = new CancellationTokenSource();
        if (dialog.Recursive)
        {
            _chmodCancels[browser] = cancel;
            UpdateStopChmodButton();
        }

        BeginBusy(browser);
        try
        {
            foreach (var entry in selected)
            {
                if (StillShowing(browser, generation))
                {
                    SetStatus(Text.Format(Strings.PermissionsApplying, entry.DisplayName));
                }

                int before = changed;
                var progress = new Progress<int>(n =>
                {
                    if (StillShowing(browser, generation))
                    {
                        SetStatus(Text.Format(Strings.PermissionsProgress, before + n));
                    }
                });
                try
                {
                    // Pas de propagation à travers un lien symbolique sélectionné (seule sa cible change de droits).
                    bool recursive = dialog.Recursive && entry.IsDirectory && !entry.IsSymbolicLink;
                    var result = await browser.SetPermissionsAsync(entry.FullPath, change, recursive, dialog.ExecuteOnlyIfAlready,
                        progress, cancel.Token);
                    changed += result.Changed;
                    errors.AddRange(result.Errors);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add(Text.Format(Strings.ItemError, entry.DisplayName, Describe(ex)));
                }
            }
        }
        finally
        {
            EndBusy(browser);
            _chmodCancels.Remove(browser);
            UpdateStopChmodButton();
        }

        if (!StillShowing(browser, generation))
        {
            return;
        }

        await NavigateAsync(browser.CurrentDirectory);
        if (errors.Count > 0)
        {
            SetStatus(Text.Format(Strings.PermissionsFailed, string.Join(" ; ", errors.Take(5))), error: true);
        }
        else if (stopped)
        {
            SetStatus(Text.Format(Strings.PermissionsStopped, changed));
        }
        else
        {
            SetStatus(dialog.Recursive
                ? Text.Format(Strings.PermissionsDoneCount, what, changed)
                : Text.Format(Strings.PermissionsDone, what, target));
        }
    }

    private void OnStopChmod(object sender, RoutedEventArgs e)
    {
        if (_browser is { } browser && _chmodCancels.TryGetValue(browser, out var cancel))
        {
            cancel.Cancel();
        }
    }

    /// <summary>« Arrêter » : seulement pendant une modification récursive des droits sur le serveur affiché.</summary>
    private void UpdateStopChmodButton() =>
        StopChmodButton.Visibility = _browser is { } browser && _chmodCancels.ContainsKey(browser) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Vrai (et la barre d'état le dit) si une opération est déjà en cours sur ce serveur : l'action demandée attend
    /// qu'elle se termine. Les autres serveurs ne sont pas concernés.
    /// </summary>
    internal bool RefuseIfBusy(IRemoteFiles browser)
    {
        if (!_busy.ContainsKey(browser))
        {
            return false;
        }

        SetStatus(_chmodCancels.ContainsKey(browser) ? Strings.FilesBusyPermissions : Strings.FilesBusy, error: true);
        return true;
    }

    internal void BeginBusy(IRemoteFiles browser) => _busy[browser] = _busy.GetValueOrDefault(browser) + 1;

    internal void EndBusy(IRemoteFiles browser)
    {
        if (_busy.GetValueOrDefault(browser) <= 1)
        {
            _busy.Remove(browser);
        }
        else
        {
            _busy[browser]--;
        }
    }

    /// <summary>Curseur dans la liste des fichiers (Ctrl+3, F6) : sur l'élément choisi, sinon le premier.</summary>
    public void FocusList()
    {
        if (FileList.Items.Count == 0)
        {
            FileList.Focus();
            return;
        }

        if (FileList.SelectedIndex < 0)
        {
            FileList.SelectedIndex = 0;
        }

        FileList.ScrollIntoView(FileList.SelectedItem);
        FileList.UpdateLayout();
        if (FileList.ItemContainerGenerator.ContainerFromIndex(FileList.SelectedIndex) is ListViewItem item)
        {
            item.Focus();
        }
        else
        {
            FileList.Focus();
        }
    }

    private async Task DeleteAsync()
    {
        var browser = _browser;
        int generation = _generation;
        var selected = SelectedEntries();
        if (browser is null || selected.Count == 0 || RefuseIfBusy(browser))
        {
            return;
        }

        // Le serveur est nommé : l'onglet Fichiers change de serveur avec l'onglet de session actif.
        static string Name(RemoteEntry entry) => entry.DisplayName + (entry.IsDirectory ? "/" : "");
        if (!ConfirmDialog.Destructive(Window.GetWindow(this), Strings.DeleteTitle,
                selected.Count == 1
                    ? Text.Format(Strings.FileDeleteHeadingOne, Name(selected[0]))
                    : Text.Format(Strings.FileDeleteHeadingMany, selected.Count),
                Strings.ActionDelete,
                subject: Text.Format(Strings.ServerPath, _session?.Label ?? "", browser.CurrentDirectory),
                bullets: [Strings.FileDeleteNoBin, Strings.FileDeleteRules],
                items: selected.Count == 1 ? null : selected.Select(Name).ToList()))
        {
            return;
        }

        var errors = new List<string>();
        BeginBusy(browser);
        try
        {
            foreach (var entry in selected)
            {
                if (StillShowing(browser, generation))
                {
                    SetStatus(Text.Format(Strings.Deleting, entry.DisplayName));
                }

                try
                {
                    await browser.DeleteAsync(entry, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    errors.Add(Text.Format(Strings.ItemError, entry.DisplayName, Describe(ex, entry.IsDirectory)));
                }
            }
        }
        finally
        {
            EndBusy(browser);
        }

        if (!StillShowing(browser, generation))
        {
            return;
        }

        await NavigateAsync(browser.CurrentDirectory);
        if (errors.Count > 0)
        {
            SetStatus(Text.Format(Strings.DeleteIncomplete, string.Join(" ; ", errors)), error: true);
        }
        else
        {
            SetStatus(Text.Format(Strings.Deleted, selected.Count));
        }
    }

    // ===================== Envoi (glisser-déposer) et téléchargement =====================

    /// <summary>Ligne de dossier survolée pendant un dépôt : les fichiers iront dans ce dossier.</summary>
    private ListViewItem? _dropRow;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        bool ok = _browser is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        var row = ok ? FolderRowAt(e) : null;
        SetDropRow(row);
        if (ok && row?.DataContext is RemoteEntry folder)
        {
            // Sur un dossier : la ligne en surbrillance, la destination dans la barre d'état (le voile cacherait la ligne).
            DropHint.Visibility = Visibility.Collapsed;
            SetStatus(Text.Format(Strings.DropIntoFolder, Text.Format(Strings.ServerPath, _session?.Label ?? "", RemoteEntry.Visible(folder.FullPath))));
        }
        else if (ok)
        {
            DropHintText.Text = Text.Format(_queue.ActiveCount > 0 ? Strings.DropHintQueued : Strings.DropHint, Protocol,
                Text.Format(Strings.ServerPath, _session?.Label ?? "", _browser!.CurrentDirectory));
            DropHint.Visibility = Visibility.Visible;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        SetDropRow(null);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        var target = (FolderRowAt(e)?.DataContext as RemoteEntry)?.FullPath;
        SetDropRow(null);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _ = EnqueueUploadAsync(paths, target);
        }
    }

    /// <summary>Ligne de dossier sous le pointeur (dossier parent compris), ou null.</summary>
    private ListViewItem? FolderRowAt(DragEventArgs e)
    {
        DependencyObject? node = FileList.InputHitTest(e.GetPosition(FileList)) as DependencyObject;
        while (node is not null and not ListViewItem && !ReferenceEquals(node, FileList))
        {
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return node is ListViewItem { DataContext: RemoteEntry { IsDirectory: true } } row ? row : null;
    }

    private void SetDropRow(ListViewItem? row)
    {
        if (ReferenceEquals(row, _dropRow))
        {
            return;
        }

        _dropRow?.ClearValue(TagProperty);
        _dropRow = row;
        row?.SetValue(TagProperty, "drop");
    }

    private void OnUploadDialog(object sender, RoutedEventArgs e)
    {
        if (_browser is null)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = Text.Format(Strings.UploadTo, _browser.CurrentDirectory), Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = EnqueueUploadAsync(dialog.FileNames);
        }
    }

    private string Protocol => ProtocolOf(_browser);

    /// <summary>Protocole des envois vers ce serveur : celui des Paramètres (SCP ou SFTP), sinon le seul possible (FTP, FTPS).</summary>
    private string ProtocolOf(IRemoteFiles? browser) =>
        browser is { ChoosesUploadProtocol: false } ? browser.UploadProtocol.Label() : _settings.PreferredUploadProtocol.Label();

    /// <summary>Télécharge les fichiers choisis ; un dossier se télécharge en le glissant vers l'Explorateur (c'est dit).</summary>
    private void OnDownload(object sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Any(s => s.IsDirectory))
        {
            SetStatus(Strings.DownloadFolderHint);
        }

        RequestDownload(selected.Where(s => !s.IsDirectory).ToList());
    }

    /// <summary>Demande où enregistrer, puis met le téléchargement en file.</summary>
    private void RequestDownload(IReadOnlyList<RemoteEntry> files)
    {
        if (_browser is null || files.Count == 0)
        {
            return;
        }

        string folder;
        IReadOnlyList<string> targets;
        if (files.Count == 1)
        {
            // La fenêtre d'enregistrement demande elle-même avant de remplacer un fichier.
            var save = new SaveFileDialog { Title = Strings.DownloadTitle, FileName = WindowsFileName.Sanitize(files[0].Name) };
            if (save.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            targets = [save.FileName];
            folder = Path.GetDirectoryName(save.FileName)!;
        }
        else
        {
            var pick = new OpenFolderDialog { Title = Text.Format(Strings.DownloadManyTitle, files.Count) };
            if (pick.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            folder = pick.FolderName;
            // Noms Windows nettoyés et distincts sans tenir compte de la casse (« Report.txt » et « report.txt »), puis une
            // seule question pour ceux qui existent déjà.
            targets = VirtualFiles.AssignNames(files.Select(f => (IReadOnlyList<string>)[f.Name]).ToList())
                .Select(name => Path.Combine(folder, name)).ToList();
            var existing = targets.Where(File.Exists).Select(t => Path.GetFileName(t)).ToList();
            if (existing.Count > 0 && !ConfirmDialog.Confirm(Window.GetWindow(this), new ConfirmRequest
                {
                    Title = Strings.DownloadTitle,
                    Heading = existing.Count == 1
                        ? Text.Format(Strings.DownloadReplaceHeadingOne, existing[0])
                        : Text.Format(Strings.DownloadReplaceHeadingMany, existing.Count),
                    Subject = folder,
                    Message = Strings.DownloadReplaceMessage,
                    Items = existing.Count == 1 ? [] : existing,
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.ActionReplace],
                    DangerAction = 0,
                }))
            {
                return;
            }
        }

        EnqueueDownload(files, folder, targets);
    }

    // ===================== Vérification des transferts =====================

    /// <summary>
    /// Bilan de la vérification SHA-256 après des transferts, dans la barre d'état ; le détail de chaque transfert est
    /// dans l'historique. Si un fichier diffère de l'original, l'erreur est affichée et le détail s'ouvre de lui-même.
    /// </summary>
    /// <param name="note">Ajouté à la fin du bilan (protocole refusé par le serveur…).</param>
    private void ReportChecks(string done, IReadOnlyList<TransferCheck> checks, bool error = false, string? note = null)
    {
        int different = checks.Count(c => c.Verified && !c.Matches);
        if (different > 0)
        {
            SetStatus(Text.Format(Strings.TransferMismatch, different), error: true);
            new TransferChecksDialog(checks) { Owner = Window.GetWindow(this) }.ShowDialog();
            return;
        }

        int verified = checks.Count(c => c.Matches);
        // Fichiers transférés mais non relus (droits, vérification annulée) ; pas ceux en échec ou interrompus.
        int unverified = checks.Count(c => c.Unverified);
        // Liens vers des dossiers dans un dossier envoyé : pas suivis.
        int skipped = checks.Count(c => c.Skipped);
        var parts = new List<string> { done };
        if (verified > 0)
        {
            parts.Add(checks.Count == 1 ? Text.Format(Strings.TransferVerifiedOne, checks[0].RemoteHash[..12]) : Text.Format(Strings.TransferVerified, verified));
        }

        if (unverified > 0)
        {
            parts.Add(Text.Format(Strings.TransferNotVerified, unverified));
        }

        if (skipped > 0)
        {
            parts.Add(Text.Format(Strings.TransferLinksSkipped, skipped));
        }

        if (note is not null)
        {
            parts.Add(note);
        }

        SetStatus(string.Join(" · ", parts), error);
    }

    // ===================== Utilitaires =====================

    private List<RemoteEntry> SelectedEntries() =>
        FileList.SelectedItems.OfType<RemoteEntry>().Where(e => !e.IsParentLink).ToList();

    private static string Describe(Exception ex, bool directory = false) => ex switch
    {
        Renci.SshNet.Common.SftpPermissionDeniedException => Strings.PermissionDenied,
        Renci.SshNet.Common.SftpPathNotFoundException => Strings.PathNotFound,
        Renci.SshNet.Common.SshException when directory => Strings.DirectoryNotEmpty,
        _ => ErrorText.Describe(ex),
    };

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    private void ShowMessage(string text, bool retry)
    {
        MessageText.Text = text;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;

    private void UpdateToolbar()
    {
        bool ready = _browser is not null;
        UpdateStopChmodButton();
        Toolbar.IsEnabled = ready;
        PathBox.IsEnabled = ready;
        FilterBox.IsEnabled = ready;
        HiddenBox.IsEnabled = ready;
        UpdateSelectionButtons();
    }

    /// <summary>Boutons qui agissent sur la sélection : grisés comme dans le menu, l'infobulle dit ce qu'il faut choisir.</summary>
    private void UpdateSelectionButtons()
    {
        var selected = SelectedEntries();
        string? any = selected.Count > 0 ? null : Strings.FileNeedSelection;
        Enable(DownloadButton, Strings.DownloadTip, any);
        Enable(EditButton, Strings.EditTip, selected is [{ IsDirectory: false }] ? null : Strings.FileNeedOneFile);
        Enable(RenameButton, Strings.RenameTip, selected.Count == 1 ? null : selected.Count == 0 ? Strings.FileNeedSelection : Strings.FileNeedOne);
        Enable(PermissionsButton, Strings.PermissionsTip, any);
        Enable(DeleteButton, Strings.DeleteTip, any);

        static void Enable(Button button, string tip, string? missing)
        {
            button.IsEnabled = missing is null;
            button.ToolTip = missing is null ? tip : tip + "\n" + missing;
        }
    }
}
