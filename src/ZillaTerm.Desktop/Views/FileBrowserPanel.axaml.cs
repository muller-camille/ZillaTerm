using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

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
    // Lecture de dossier en cours, arrêtée par la navigation suivante.
    private CancellationTokenSource? _listing;
    // Filtre du dossier affiché (null : tout est affiché) et dossier auquel il s'applique.
    private FileNameFilter? _filter;
    private string? _shownDirectory;
    private bool _resettingFilter;
    // Lettres tapées dans la liste (sélection par le début du nom).
    private readonly TypeAhead _typeAhead = new();

    /// <summary>
    /// Contenu du dossier affiché (« .. » en tête), avant le filtre de nom : la liste montre ceux qui passent le filtre
    /// (la vue filtrée de WPF).
    /// </summary>
    private List<RemoteEntry>? _entries;

    // Colonnes de la liste, dans l'ordre (le DataGrid ne nomme pas ses colonnes).
    private readonly DataGridColumn NameColumn;
    private readonly DataGridColumn SizeColumn;
    private readonly DataGridColumn ModifiedColumn;
    private readonly DataGridColumn PermissionsColumn;
    private readonly DataGridColumn OwnerColumn;
    private readonly DataGridColumn GroupColumn;
    private readonly DataGridColumn MoreColumn;

    /// <summary>
    /// Opérations en cours (renommage, suppression, droits, glisser vers le gestionnaire de fichiers), par connexion : elles
    /// ne bloquent les autres que sur le même serveur.
    /// </summary>
    private readonly Dictionary<IRemoteFiles, int> _busy = new(ReferenceEqualityComparer.Instance);

    public FileBrowserPanel()
    {
        InitializeComponent();
        NameColumn = FileList.Columns[0];
        SizeColumn = FileList.Columns[1];
        ModifiedColumn = FileList.Columns[2];
        PermissionsColumn = FileList.Columns[3];
        OwnerColumn = FileList.Columns[4];
        GroupColumn = FileList.Columns[5];
        MoreColumn = FileList.Columns[6];
        InitializeQueue();
        InitializeMenus();
        FileList.SelectionChanged += (_, _) => UpdateSelectionButtons();
        FileList.SizeChanged += (_, _) => FitNameColumn();
        FileList.Sorting += OnColumnHeaderClick;
        // Avant le tableau : Entrée y passerait à la ligne suivante, et le clic dans une sélection la réduirait avant de
        // pouvoir la glisser.
        FileList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        FileList.AddHandler(TextInputEvent, OnListTextInput, RoutingStrategies.Bubble);
        FileList.AddHandler(ContextRequestedEvent, OnListContextRequested, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerPressedEvent, OnListPreviewMouseDown, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerMovedEvent, OnListPreviewMouseMove, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerReleasedEvent, OnListPreviewMouseUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileList.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        FileList.AddHandler(DragDrop.DragOverEvent, OnDragEnter);
        FileList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        FileList.AddHandler(DragDrop.DropEvent, OnDrop);
        PathBox.AddHandler(KeyDownEvent, OnPathKeyDown, RoutingStrategies.Tunnel);
        FilterBox.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        Toolbar.AddHandler(KeyDownEvent, OnToolbarKeyDown, RoutingStrategies.Bubble);
        // F5 partout dans l'onglet (liste, chemin, filtre, boutons) : relire le dossier, et non recharger les comptes du
        // PVWA (raccourci de la fenêtre, que la touche atteindrait sinon). Aussi dans une fenêtre détachée.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
            {
                _ = RefreshAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        ShowMessage(Strings.NoSshSessionHelp, retry: false);
        HeaderText.Text = Strings.NoSshSession;
        UpdateToolbar();
    }

    /// <param name="history">Historique des transferts ; par défaut celui du profil (les tests en donnent un en mémoire).</param>
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

    /// <summary>Fenêtre qui contient le panneau (principale, ou fenêtre détachée) : propriétaire des dialogues.</summary>
    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

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
        _entries = null;
        FileList.ItemsSource = null;
        ResetFilter();
        PathBox.Text = "";
        StatusText.Text = "";
        UpdateExtractPanel();
        if (session is null)
        {
            UpdateTailFilesButton();
            HeaderTag.IsVisible = false;
            HeaderText.Text = Strings.NoSshSession;
            ShowMessage(Strings.NoSshSessionHelp, retry: false);
            UpdateToolbar();
            return;
        }

        HeaderText.Text = session.Label;
        var tag = TagOf(session);
        HeaderTag.Content = tag is null ? null : ServerTagView.Chip(tag);
        HeaderTag.IsVisible = tag is not null;
        UpdateTailFilesButton();
        // Session de fichiers seuls (SFTP, FTP) : pas de terminal à suivre.
        var ssh = session as SshSession;
        FollowBox.IsVisible = ssh is not null;
        FollowBox.IsEnabled = ssh?.CanFollowTerminal == true;
        FollowBox.IsChecked = ssh is { CanFollowTerminal: true, FollowTerminal: true };
        ToolTip.SetTip(FollowBox, ssh?.CanFollowTerminal == true
            ? Strings.FollowTip
            : Strings.FollowDisabledTip);
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
        // Une nouvelle navigation arrête la lecture en cours (dossier aux nombreux liens symboliques, qui tient la
        // connexion) ; une relecture discrète, après un transfert, n'arrête rien et ne s'arrête pas.
        CancellationTokenSource? listing = null;
        if (!quiet)
        {
            SetStatus(Text.Format(Strings.Reading, path));
            _listing?.Cancel();
            listing = _listing = new CancellationTokenSource();
        }

        try
        {
            var entries = await browser.ListAsync(path, _settings.ShowHiddenFiles, listing?.Token ?? CancellationToken.None);
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
                ShowPendingReport();
            }

            return true;
        }
        catch (OperationCanceledException) when (listing is { IsCancellationRequested: true })
        {
            // Remplacée par une autre navigation, qui affiche son propre dossier.
            return false;
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
        finally
        {
            if (listing is not null)
            {
                if (ReferenceEquals(_listing, listing))
                {
                    _listing = null;
                }

                listing.Dispose();
            }
        }
    }

    private Task RefreshAsync() => _browser is null ? OpenAsync() : NavigateAsync(_browser.CurrentDirectory);

    private void OnTerminalDirectory(string directory)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTerminalDirectory(directory));
            return;
        }

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

    private void OnParent(object? sender, RoutedEventArgs e)
    {
        if (_browser is not null)
        {
            _ = NavigateAsync(RemotePath.Parent(_browser.CurrentDirectory));
        }
    }

    private void OnHome(object? sender, RoutedEventArgs e)
    {
        if (_browser is not null)
        {
            _ = NavigateAsync(_browser.HomeDirectory);
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnRetry(object? sender, RoutedEventArgs e) => _ = OpenAsync();

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        var path = (PathBox.Text ?? "").Trim();
        if (e.Key == Key.Enter && _browser is not null && path.Length > 0)
        {
            e.Handled = true;
            _ = NavigateAsync(path);
        }
    }

    /// <summary>Barre d'outils : un seul arrêt de tabulation, les flèches passent d'un bouton à l'autre (en boucle).</summary>
    private void OnToolbarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var buttons = Toolbar.Children.OfType<Button>().Where(b => b.IsEffectivelyEnabled && b.IsVisible).ToList();
        int current = buttons.FindIndex(b => b.IsFocused);
        if (buttons.Count == 0 || current < 0)
        {
            return;
        }

        int next = (current + (e.Key == Key.Right ? 1 : -1) + buttons.Count) % buttons.Count;
        buttons[next].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    private void OnFollowChanged(object? sender, RoutedEventArgs e)
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

    /// <summary>
    /// Place de la barre de défilement verticale, retirée de la largeur de la liste comme sous WPF (largeur réservée même
    /// quand elle n'est pas affichée : les colonnes ne bougent pas quand elle apparaît).
    /// </summary>
    private double ScrollBarWidth =>
        FileList.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == Orientation.Vertical) is { Bounds.Width: > 0 } bar
            ? bar.Bounds.Width
            : 12;

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
    private (DataGridColumn Column, RemoteSortColumn Kind, string Text)[] OptionalColumns() =>
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
        _crowded.Count == 0 || FileList.Bounds.Width <= 0 ? 0
        : Math.Ceiling(MinNameWidth + OptionalColumns().Where(c => IsChosen(c.Kind)).Sum(c => _columnWidths[c.Kind])
                       + ScrollBarWidth + 8 - FileList.Bounds.Width);

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
            if (column.IsVisible && column.ActualWidth > 0)
            {
                _columnWidths[kind] = column.ActualWidth;
            }
        }

        double available = FileList.Bounds.Width - ScrollBarWidth - 8;
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
            // Colonne masquée : retirée de la liste (une largeur nulle laisserait son filet et son bord saisissable).
            column.IsVisible = shown.Contains(kind);
            if (column.IsVisible)
            {
                column.Width = new DataGridLength(_columnWidths[kind]);
            }
        }

        _crowded = crowded;
        MoreColumn.IsVisible = crowded.Count > 0;
        MoreColumn.Width = new DataGridLength(MarkerWidth);
        MoreColumnText.Text = "+" + crowded.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var tip = crowded.Count == 0 ? "" : Text.Format(Strings.FilesColumnsHiddenTip,
            string.Join(", ", OptionalColumns().Where(c => crowded.Contains(c.Kind)).Select(c => c.Text)));
        ToolTip.SetTip(MoreColumnText, tip.Length == 0 ? null : tip);
        AutomationProperties.SetName(MoreColumnText, tip);
        NameColumn.Width = new DataGridLength(Math.Max(MinNameWidth,
            available - shown.Sum(k => _columnWidths[k]) - (crowded.Count > 0 ? MarkerWidth : 0)));
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

    /// <summary>Menu des colonnes (ressource du contrôle).</summary>
    private ContextMenu ColumnsMenu => (ContextMenu)Resources["ColumnsMenu"]!;

    /// <summary>Menu de la liste (ressource du contrôle).</summary>
    private ContextMenu FileMenu => (ContextMenu)Resources["FileMenu"]!;

    /// <summary>
    /// Menu des colonnes (clic droit sur l'en-tête, ou clic sur « +n ») : une case par colonne facultative, gardée dans
    /// les réglages ; une colonne cochée mais sans la place est signalée, et « Élargir le panneau » la fait apparaître.
    /// </summary>
    /// <param name="target">La liste (sous le pointeur, clic droit sur l'en-tête), ou « +n » (sous l'indicateur).</param>
    private void OpenColumnsMenu(Control target, PlacementMode placement)
    {
        // Ouvert par le code, sans être attaché à un contrôle : rempli ici (Avalonia ne lève Opening que pour le menu
        // ouvert par le clic droit sur son contrôle).
        var menu = ColumnsMenu;
        FillColumnsMenu(menu);
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.Open(target);
    }

    private void FillColumnsMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var items = new List<(MenuItem Item, RemoteSortColumn Kind, string Text)>();
        var widen = new MenuItem { Header = Strings.FilesColumnsWiden };
        // Colonnes sans la place, après chaque case cochée ou décochée (le menu reste ouvert).
        void Mark()
        {
            foreach (var (item, kind, text) in items)
            {
                item.Header = MenuHeader.WithGesture(text, _crowded.Contains(kind) ? Strings.FilesColumnNoRoom : null);
            }

            widen.IsEnabled = _crowded.Count > 0;
        }

        foreach (var (_, kind, text) in OptionalColumns())
        {
            var item = new MenuItem { ToggleType = MenuItemToggleType.CheckBox, IsChecked = IsChosen(kind), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                ShowColumn(kind, item.IsChecked);
                Mark();
            };
            items.Add((item, kind, text));
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

    /// <summary>Fermé : placement oublié (chaque ouverture donne le sien).</summary>
    private void OnColumnsMenuClosed(object? sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender!;
        menu.ClearValue(ContextMenu.PlacementProperty);
        menu.ClearValue(ContextMenu.PlacementTargetProperty);
    }

    /// <summary>
    /// Clic droit sur l'en-tête de la liste : le menu des colonnes (celui de la liste sinon, sur les lignes et sous elles).
    /// </summary>
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null)
        {
            return;
        }

        e.Handled = true;
        OpenColumnsMenu(FileList, PlacementMode.Pointer);
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
    /// Le tri du tableau lui-même n'est jamais utilisé (il mêlerait dossiers et fichiers, et « .. »).
    /// </summary>
    private void OnColumnHeaderClick(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (e.Column == MoreColumn)
        {
            // « +n » : le menu des colonnes, sous l'indicateur.
            OpenColumnsMenu(MoreColumnText, PlacementMode.Bottom);
            return;
        }

        if (SortColumns().FirstOrDefault(c => c.Column == e.Column) is not { Column: not null } clicked)
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
        if (_entries is not { } entries)
        {
            return;
        }

        var selected = FileList.SelectedItems.OfType<RemoteEntry>().ToList();
        ShowEntries(entries.Where(e => e.IsParentLink)
            .Concat(RemoteEntry.Sort(entries.Where(e => !e.IsParentLink), column, descending))
            .ToList());
        Reselect(selected);
        if (FileList.SelectedItems.OfType<RemoteEntry>().FirstOrDefault() is { } first)
        {
            FileList.ScrollIntoView(first, null);
        }
    }

    /// <summary>Colonnes triables : colonne de la liste, titre, tri correspondant.</summary>
    private (DataGridColumn Column, string Text, RemoteSortColumn Sort)[] SortColumns() =>
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
            var arrow = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(descending ? "M0,0 L8,0 L4,4.5 Z" : "M0,4.5 L8,4.5 L4,0 Z"),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            arrow[!Avalonia.Controls.Shapes.Shape.FillProperty] = arrow.GetResourceObservable("MutedBrush").ToBinding();
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
        _entries = items;
        ApplyFilter();
    }

    /// <summary>« .. » reste toujours affiché, pour remonter même quand rien ne répond au filtre.</summary>
    private void ApplyFilter()
    {
        if (_entries is not { } entries)
        {
            FileList.ItemsSource = null;
            return;
        }

        var filter = _filter;
        var shown = filter is null ? entries : entries.Where(e => e.IsParentLink || filter.Matches(e.Name)).ToList();
        if (ReferenceEquals(FileList.ItemsSource, shown))
        {
            return;
        }

        // Les éléments choisis qui restent affichés le restent (comme le filtre d'une vue WPF).
        var selected = FileList.SelectedItems.OfType<RemoteEntry>().ToList();
        FileList.ItemsSource = shown;
        Reselect(selected);
    }

    /// <summary>Choisit de nouveau, après une relecture ou un tri, les éléments encore affichés.</summary>
    private void Reselect(IReadOnlyList<RemoteEntry> selected)
    {
        if (selected.Count == 0 || FileList.ItemsSource is not IList<RemoteEntry> shown)
        {
            return;
        }

        foreach (var entry in selected.Where(shown.Contains))
        {
            FileList.SelectedItems.Add(entry);
        }
    }

    /// <summary>Éléments affichés (après le filtre), dans l'ordre de la liste.</summary>
    private IReadOnlyList<RemoteEntry> ShownItems => FileList.ItemsSource as IReadOnlyList<RemoteEntry> ?? [];

    /// <summary>Bilan du dossier dans la barre d'état ; avec un filtre, combien d'éléments il laisse affichés.</summary>
    private void ShowListSummary()
    {
        if (_entries is not { } shown)
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

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_resettingFilter)
        {
            return;
        }

        _filter = FileNameFilter.Parse(FilterBox.Text ?? "");
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
    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(FilterBox.Text))
        {
            FilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down && ShownItems.Count > 0)
        {
            var items = ShownItems;
            var first = items.FirstOrDefault(i => !i.IsParentLink) ?? items[0];
            SelectAndFocus(first);
            e.Handled = true;
        }
    }

    /// <summary>Sélectionne seulement <paramref name="item"/>, l'amène à l'écran et lui donne le clavier.</summary>
    private void SelectAndFocus(RemoteEntry item)
    {
        FileList.SelectedItem = item;
        FileList.ScrollIntoView(item, null);
        FileList.Focus(NavigationMethod.Directional);
    }

    /// <summary>Ctrl+F dans l'onglet Fichiers : curseur dans le filtre du dossier ; faux sans dossier affiché.</summary>
    public bool FocusFilter()
    {
        if (!FilterBox.IsEffectivelyEnabled || !FilterBox.IsEffectivelyVisible)
        {
            return false;
        }

        FilterBox.Focus();
        FilterBox.SelectAll();
        return true;
    }

    private void OnHiddenChanged(object? sender, RoutedEventArgs e)
    {
        _settings.ShowHiddenFiles = HiddenBox.IsChecked == true;
        _saveSettings();
        _ = RefreshAsync();
    }

    /// <summary>Double-clic sur une ligne (pas sur l'en-tête ni la barre de défilement).</summary>
    private void OnItemDoubleClick(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source) is { DataContext: RemoteEntry entry })
        {
            e.Handled = true;
            Open(entry);
        }
    }

    /// <summary>Ligne de la liste qui contient <paramref name="source"/> (élément touché par le pointeur), ou null.</summary>
    private static DataGridRow? RowAt(object? source) => (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);

    private void OnOpenSelected(object? sender, RoutedEventArgs e)
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
            await RequestDownloadAsync([entry]);
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
                await RequestDownloadAsync([entry]);
                return;
            }
        }

        await editor.EditAsync(entry);
    }

    /// <summary>
    /// Touches de la liste, avant le tableau : Entrée ouvre (le tableau passerait à la ligne suivante), Suppr supprime,
    /// F4 édite, F2 renomme, Retour arrière remonte.
    /// </summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

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
    /// lettre de nouveau passe au suivant). Une espace pendant la saisie d'un nom (« rapport final ») en fait partie ; seule,
    /// elle ne cherche rien (voir <see cref="TypeAhead"/>) : le tableau ne s'en sert pas.
    /// </summary>
    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        TypeAheadSelect(e.Text);
        e.Handled = true;
    }

    private void TypeAheadSelect(string text)
    {
        var items = ShownItems;
        // Point de départ : l'élément choisi (le dernier choisi dans une sélection multiple).
        int current = FileList.SelectedItem is RemoteEntry focused ? IndexOf(items, focused) : -1;
        int index = _typeAhead.Find(items.Select(i => i.DisplayName).ToList(), current, text, DateTime.UtcNow);
        if (index >= 0)
        {
            SelectAndFocus(items[index]);
        }
    }

    private static int IndexOf(IReadOnlyList<RemoteEntry> items, RemoteEntry item)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Raccourcis affichés à droite des entrées du menu de la liste (InputGestureText de WPF).</summary>
    private void InitializeMenus()
    {
        foreach (var item in FileMenu.Items.OfType<MenuItem>())
        {
            var gesture = item.Tag switch
            {
                "edit" => "F4",
                "rename" => "F2",
                "refresh" => "F5",
                _ => null,
            };
            if (gesture is not null && item.Header is string text)
            {
                item.Header = MenuHeader.WithGesture(text, gesture);
            }
        }
    }

    private void OnMenuOpened(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selected = SelectedEntries();
        bool files = selected.Count > 0 && selected.All(s => !s.IsDirectory);
        foreach (var item in ((ContextMenu)sender!).Items.OfType<MenuItem>())
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

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Count > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, selected.Select(s => s.FullPath)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Core.Diagnostics.DebugLog.Write("files", $"Chemins non copiés : {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Vrai si <paramref name="browser"/> est toujours la connexion affichée : une opération longue qui se termine après
    /// un changement de session n'actualise pas l'autre session et n'écrit pas dans sa barre d'état.
    /// </summary>
    private bool StillShowing(IRemoteFiles browser, int generation) => generation == _generation && ReferenceEquals(_browser, browser);

    /// <summary>
    /// Connexion, session et dossier affichés quand une action commence, gardés pendant la fenêtre qui suit (choix des
    /// fichiers, nom d'un dossier) : le panneau peut entre-temps passer à une autre session (vue parallèle, terminal
    /// détaché) ou suivre le terminal dans un autre dossier, l'action vise toujours ce que la fenêtre annonçait.
    /// </summary>
    internal sealed record ShownFolder(IRemoteFiles Browser, RemoteSession Session, string Directory)
    {
        /// <summary>La connexion, ou celle qui l'a remplacée dans la même session (reconnexion pendant la fenêtre).</summary>
        public IRemoteFiles Connection => !Browser.IsConnected && Session.OpenedBrowser is { } reopened ? reopened : Browser;
    }

    /// <summary>Connexion, session et dossier affichés ; null sans connexion.</summary>
    internal ShownFolder? CurrentFolder() =>
        _browser is { } browser && _session is { } session ? new ShownFolder(browser, session, browser.CurrentDirectory) : null;

    /// <summary>
    /// Bilans de suppression ou de modification des droits pas encore affichés, par session : une opération terminée
    /// pendant que le panneau montrait une autre session garde son bilan, erreurs comprises, jusqu'au retour sur la sienne.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<RemoteSession, StatusReport> _reports = [];

    private sealed record StatusReport(string Text, bool Error);

    /// <summary>
    /// Bilan d'une opération longue (suppression, droits) : affiché après relecture du dossier si le panneau montre sa
    /// session, même après un aller-retour sur un autre onglet ; sinon à la prochaine lecture d'un dossier de la session.
    /// </summary>
    private void Report(RemoteSession session, string text, bool error)
    {
        _reports.AddOrUpdate(session, new StatusReport(text, error));
        if (ReferenceEquals(_session, session) && _browser is { } browser)
        {
            _ = NavigateAsync(browser.CurrentDirectory);
        }
    }

    /// <summary>Dossier lu : le bilan en attente de la session affichée remplace le résumé du dossier.</summary>
    private void ShowPendingReport()
    {
        if (_session is { } session && _reports.TryGetValue(session, out var report))
        {
            _reports.Remove(session);
            SetStatus(report.Text, report.Error);
        }
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        int generation = _generation;
        if (CurrentFolder() is not (var browser, var session, var directory))
        {
            return;
        }

        // Dossier pris avant la fenêtre : le dossier est créé là où la question l'annonce.
        var where = Text.Format(Strings.ServerPath, session.Label, RemoteEntry.Visible(directory));
        var dialog = new InputDialog(Strings.NewFolder, Text.Format(Strings.NewRemoteFolderPrompt, where), validate: ValidateName);
        if (!await dialog.ShowDialogAsync(OwnerWindow))
        {
            return;
        }

        try
        {
            await browser.CreateDirectoryAsync(RemotePath.Combine(directory, dialog.Value), CancellationToken.None);
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

    private void OnDelete(object? sender, RoutedEventArgs e) => _ = DeleteAsync();

    private void OnRename(object? sender, RoutedEventArgs e) => _ = RenameAsync();

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
        var dialog = new InputDialog(Strings.RenameTitle, Text.Format(Strings.RenamePrompt, entry.DisplayName, where), entry.Name, ValidateName);
        if (!await dialog.ShowDialogAsync(OwnerWindow) || dialog.Value == entry.Name || !StillShowing(browser, generation))
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
            if (ShownItems.FirstOrDefault(e => e.FullPath == target) is { } renamed)
            {
                FileList.SelectedItem = renamed;
                FileList.ScrollIntoView(renamed, null);
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

    private async void OnEdit(object? sender, RoutedEventArgs e)
    {
        if (_session?.Editor is { } editor && _browser is not null && SelectedEntries() is [{ IsDirectory: false } entry])
        {
            await editor.EditAsync(entry);
        }
    }

    private void OnPermissions(object? sender, RoutedEventArgs e) => _ = ChangePermissionsAsync();

    /// <summary>Modifications récursives des droits en cours, par connexion (bouton « Arrêter » de la barre d'état).</summary>
    private readonly Dictionary<IRemoteFiles, CancellationTokenSource> _chmodCancels = new(ReferenceEqualityComparer.Instance);

    private async Task ChangePermissionsAsync()
    {
        var selected = SelectedEntries();
        if (CurrentFolder() is not (var browser, var session, var directory) || selected.Count == 0 || RefuseIfBusy(browser))
        {
            return;
        }

        // Serveur et dossier lus avant l'attente des droits d'un lien : la fenêtre nomme le serveur des éléments choisis,
        // même si le panneau est passé entre-temps à une autre session.
        var location = Text.Format(Strings.ServerPath, session.Label, RemoteEntry.Visible(directory));
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

        var owner = OwnerWindow;
        var dialog = new PermissionsDialog(target, location, modes, selected.Any(s => s.IsDirectory && !s.IsSymbolicLink));
        if (!await dialog.ShowDialogAsync(owner))
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
            if (!await ConfirmDialog.ConfirmAsync(owner, new ConfirmRequest
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
                // Avancement affiché tant que le panneau montre ce serveur (de nouveau après un aller-retour sur un autre onglet).
                if (ReferenceEquals(_browser, browser))
                {
                    SetStatus(Text.Format(Strings.PermissionsApplying, entry.DisplayName));
                }

                int before = changed;
                var progress = new Progress<int>(n =>
                {
                    if (ReferenceEquals(_browser, browser))
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

        Report(session,
            errors.Count > 0 ? Text.Format(Strings.PermissionsFailed, string.Join(" ; ", errors.Take(5)))
            : stopped ? Text.Format(Strings.PermissionsStopped, changed)
            : dialog.Recursive ? Text.Format(Strings.PermissionsDoneCount, what, changed)
            : Text.Format(Strings.PermissionsDone, what, target),
            error: errors.Count > 0);
    }

    private void OnStopChmod(object? sender, RoutedEventArgs e)
    {
        if (_browser is { } browser && _chmodCancels.TryGetValue(browser, out var cancel))
        {
            cancel.Cancel();
        }
    }

    /// <summary>« Arrêter » : seulement pendant une modification récursive des droits sur le serveur affiché.</summary>
    private void UpdateStopChmodButton() =>
        StopChmodButton.IsVisible = _browser is { } browser && _chmodCancels.ContainsKey(browser);

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
        var items = ShownItems;
        if (items.Count == 0)
        {
            FileList.Focus();
            return;
        }

        if (FileList.SelectedIndex < 0)
        {
            FileList.SelectedIndex = 0;
        }

        if (FileList.SelectedItem is { } selected)
        {
            FileList.ScrollIntoView(selected, null);
        }

        FileList.Focus(NavigationMethod.Directional);
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedEntries();
        if (CurrentFolder() is not { } folder || selected.Count == 0 || RefuseIfBusy(folder.Browser))
        {
            return;
        }

        // Le serveur est nommé : l'onglet Fichiers change de serveur avec l'onglet de session actif.
        static string Name(RemoteEntry entry) => entry.DisplayName + (entry.IsDirectory ? "/" : "");
        if (!await ConfirmDialog.DestructiveAsync(OwnerWindow, Strings.DeleteTitle,
                selected.Count == 1
                    ? Text.Format(Strings.FileDeleteHeadingOne, Name(selected[0]))
                    : Text.Format(Strings.FileDeleteHeadingMany, selected.Count),
                Strings.ActionDelete,
                subject: Text.Format(Strings.ServerPath, folder.Session.Label, folder.Directory),
                bullets: [Strings.FileDeleteNoBin, Strings.FileDeleteRules],
                items: selected.Count == 1 ? null : selected.Select(Name).ToList()))
        {
            return;
        }

        // Autre opération lancée sur ce serveur pendant la question.
        if (RefuseIfBusy(folder.Browser))
        {
            return;
        }

        await DeleteEntriesAsync(folder, selected);
    }

    /// <summary>Supprime les éléments confirmés ; le bilan, erreurs comprises, n'est jamais perdu (voir <see cref="Report"/>).</summary>
    internal async Task DeleteEntriesAsync(ShownFolder folder, IReadOnlyList<RemoteEntry> selected)
    {
        var browser = folder.Browser;
        var errors = new List<string>();
        BeginBusy(browser);
        try
        {
            foreach (var entry in selected)
            {
                // Avancement affiché tant que le panneau montre ce serveur (de nouveau après un aller-retour sur un autre onglet).
                if (ReferenceEquals(_browser, browser))
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

        Report(folder.Session,
            errors.Count > 0 ? Text.Format(Strings.DeleteIncomplete, string.Join(" ; ", errors)) : Text.Format(Strings.Deleted, selected.Count),
            error: errors.Count > 0);
    }

    // ===================== Envoi (glisser-déposer) et téléchargement =====================

    /// <summary>Ligne de dossier survolée pendant un dépôt : les fichiers iront dans ce dossier.</summary>
    private DataGridRow? _dropRow;

    /// <summary>Fichiers et dossiers de ce poste glissés sur la liste (chemins locaux), ou null.</summary>
    private static List<string>? DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { Length: > 0 } items
            ? items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList() is { Count: > 0 } paths ? paths : null
            : null;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool ok = _browser is not null && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        var row = ok ? FolderRowAt(e) : null;
        SetDropRow(row);
        if (ok && row?.DataContext is RemoteEntry folder)
        {
            // Sur un dossier : la ligne en surbrillance, la destination dans la barre d'état (le voile cacherait la ligne).
            DropHint.IsVisible = false;
            SetStatus(Text.Format(Strings.DropIntoFolder, Text.Format(Strings.ServerPath, _session?.Label ?? "", RemoteEntry.Visible(folder.FullPath))));
        }
        else if (ok)
        {
            DropHintText.Text = Text.Format(_queue.ActiveCount > 0 ? Strings.DropHintQueued : Strings.DropHint, Protocol,
                Text.Format(Strings.ServerPath, _session?.Label ?? "", _browser!.CurrentDirectory));
            DropHint.IsVisible = true;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        SetDropRow(null);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        var target = (FolderRowAt(e)?.DataContext as RemoteEntry)?.FullPath;
        SetDropRow(null);
        // Seuls des fichiers et dossiers de ce poste (chemins locaux) s'envoient : un élément sans chemin local (fichier
        // d'un autre programme, adresse) est ignoré.
        if (_browser is not null && DroppedPaths(e) is { } paths)
        {
            e.DragEffects = DragDropEffects.Copy;
            _ = EnqueueUploadAsync(paths, target);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    /// <summary>Ligne de dossier sous le pointeur (dossier parent compris), ou null.</summary>
    private DataGridRow? FolderRowAt(DragEventArgs e) =>
        RowAt(FileList.InputHitTest(e.GetPosition(FileList))) is { DataContext: RemoteEntry { IsDirectory: true } } row ? row : null;

    private void SetDropRow(DataGridRow? row)
    {
        if (ReferenceEquals(row, _dropRow))
        {
            return;
        }

        _dropRow?.Classes.Remove("drop");
        _dropRow = row;
        row?.Classes.Add("drop");
    }

    private async void OnUploadDialog(object? sender, RoutedEventArgs e)
    {
        // Serveur et dossier pris avant la fenêtre, et nommés dans son titre : les fichiers vont là, même si le panneau
        // passe entre-temps à une autre session ou suit le terminal dans un autre dossier.
        if (CurrentFolder() is not { } folder || OwnerWindow is not { } owner)
        {
            return;
        }

        var where = Text.Format(Strings.ServerPath, folder.Session.Label, RemoteEntry.Visible(folder.Directory));
        var files = await Pickers.OpenFilesAsync(owner, title: Text.Format(Strings.UploadTo, where));
        if (files.Count > 0)
        {
            await EnqueueUploadAsync(files, from: folder);
        }
    }

    private string Protocol => ProtocolOf(_browser);

    /// <summary>Protocole des envois vers ce serveur : celui des Paramètres (SCP ou SFTP), sinon le seul possible (FTP, FTPS).</summary>
    private string ProtocolOf(IRemoteFiles? browser) =>
        browser is { ChoosesUploadProtocol: false } ? browser.UploadProtocol.Label() : _settings.PreferredUploadProtocol.Label();

    /// <summary>
    /// Télécharge les fichiers choisis ; un dossier se télécharge en le glissant vers le gestionnaire de fichiers (c'est dit).
    /// </summary>
    private void OnDownload(object? sender, RoutedEventArgs e)
    {
        var selected = SelectedEntries();
        if (selected.Any(s => s.IsDirectory))
        {
            SetStatus(Strings.DownloadFolderHint);
        }

        _ = RequestDownloadAsync(selected.Where(s => !s.IsDirectory).ToList());
    }

    /// <summary>Demande où enregistrer, puis met le téléchargement en file.</summary>
    private async Task RequestDownloadAsync(IReadOnlyList<RemoteEntry> files)
    {
        // Connexion prise avant la fenêtre d'enregistrement : les fichiers viennent du serveur où on les a choisis, même si
        // le panneau passe entre-temps à une autre session (vue parallèle, terminal détaché).
        if (CurrentFolder() is not { } from || files.Count == 0 || OwnerWindow is not { } owner)
        {
            return;
        }

        string folder;
        IReadOnlyList<string> targets;
        if (files.Count == 1)
        {
            // La fenêtre d'enregistrement demande elle-même avant de remplacer un fichier. Nom nettoyé (« ..\ », « : »,
            // « CON »…) : le serveur ne choisit pas où le fichier s'écrit.
            var path = await Pickers.SaveFileAsync(owner, suggestedName: WindowsFileName.Sanitize(files[0].Name), title: Strings.DownloadTitle);
            if (path is null)
            {
                return;
            }

            targets = [path];
            folder = System.IO.Path.GetDirectoryName(path)!;
        }
        else
        {
            var picked = await Pickers.OpenFolderAsync(owner, Text.Format(Strings.DownloadManyTitle, files.Count));
            if (picked is null)
            {
                return;
            }

            folder = picked;
            // Noms Windows nettoyés et distincts sans tenir compte de la casse (« Report.txt » et « report.txt »), puis une
            // seule question pour ceux qui existent déjà.
            targets = VirtualFiles.AssignNames(files.Select(f => (IReadOnlyList<string>)[f.Name]).ToList())
                .Select(name => System.IO.Path.Combine(folder, name)).ToList();
            var existing = targets.Where(File.Exists).Select(t => System.IO.Path.GetFileName(t)).ToList();
            if (existing.Count > 0 && !await ConfirmDialog.ConfirmAsync(owner, new ConfirmRequest
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

        EnqueueDownload(files, folder, targets, from);
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
            _ = new TransferChecksDialog(checks).ShowDialogAsync(OwnerWindow);
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
        StatusText[!TextBlock.ForegroundProperty] = StatusText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
    }

    private void ShowMessage(string text, bool retry)
    {
        MessageText.Text = text;
        RetryButton.IsVisible = retry;
        MessagePanel.IsVisible = true;
    }

    private void HideMessage() => MessagePanel.IsVisible = false;

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
            ToolTip.SetTip(button, missing is null ? tip : tip + "\n" + missing);
        }
    }
}
