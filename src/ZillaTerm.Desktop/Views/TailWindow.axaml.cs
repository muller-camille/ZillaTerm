using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Suivi de fichiers du serveur comme <c>tail -f</c> : la fin du fichier, puis chaque nouvelle ligne, relevées par
/// SFTP chaque seconde (aucune commande sur le serveur). Plusieurs fichiers, de plusieurs serveurs, peuvent être suivis
/// dans la même fenêtre : leurs lignes s'intercalent dans l'ordre d'arrivée, préfixées par le fichier d'origine.
/// Couleurs par niveau, mots surlignés, alertes, filtre (texte ou expression régulière, exclusion, contexte),
/// recherche, repères, enregistrement ; reprise après une reconnexion de l'onglet. Les 10 000 dernières lignes sont
/// gardées, chacune jusqu'à <see cref="TailLineSplitter.MaxLineLength"/> caractères.
/// </summary>
/// <remarks>
/// Les lignes reçues ensemble sont ajoutées à la liste en une seule fois (<see cref="TailRowList.AddRange"/>), et la
/// liste n'affiche que les lignes visibles : un fichier très bavard (des milliers de lignes par seconde) reste fluide.
/// </remarks>
public partial class TailWindow : Window
{
    private const int MaxLines = 10_000;

    // Les lignes en trop sont retirées par paquets : la liste n'est pas reconstruite à chaque ligne.
    private const int TrimBatch = 2_000;

    // Une ligne restée sans fin de ligne est affichée telle quelle après ce délai.
    private static readonly TimeSpan PartialDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly ObservableCollection<TailFeed> _feeds = [];
    private readonly List<ITailLink> _links = [];
    private readonly List<TailLine> _lines = [];
    private readonly Dictionary<TextBox, object?> _tips = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _closing = new();
    private TailRowList _rows = [];
    private TailFilter _filter = TailFilter.None;

    /// <summary>Lignes retenues par le filtre (repères compris), pour « Filtre : n / N lignes ».</summary>
    private int _matched;
    private TailFilterRun<TailLine> _run;
    private TailPattern?[] _filterPatterns = [];
    private TailPattern? _search;
    private TailStyle _style;
    private IReadOnlyList<string> _alertTerms;
    private ScrollViewer? _scroller;
    private bool _opened;
    private bool _polling;
    private bool _paused;
    private bool _combined;
    private int _colors;
    private DateTime _lastUpdate;
    private int _alertCount;
    private int _unnotified;
    private DateTime _lastNotification;
    private StreamWriter? _record;
    private string? _recordPath;
    private string? _notice;
    private bool _noticeError;

    // Défilement demandé par le code (suivre la fin, aller à une ligne) : il ne décoche pas « Suivre la fin ».
    private bool _scrolling;

    public TailWindow()
        : this(new AppSettings(), () => { })
    {
    }

    public TailWindow(AppSettings settings, Action saveSettings)
    {
        InitializeComponent();
        _settings = settings;
        _saveSettings = saveSettings;
        Title = "tail -f";
        ColorsBox.IsChecked = settings.TailLevelColors;
        HighlightBox.Text = settings.TailHighlights;
        AlertBox.Text = settings.TailAlerts;
        NotifyBox.IsChecked = settings.TailAlertNotify;
        // Windows : pas de notification dans cette interface (voir TailAlerts) ; la case n'apparaît pas, le réglage est gardé.
        NotifyBox.IsVisible = !OperatingSystem.IsWindows();
        ContextBox.ItemsSource = new[] { 0, 1, 2, 3, 5, 10 };
        ContextBox.SelectedIndex = 0;
        foreach (var box in new[] { FilterBox, ExcludeBox, SearchBox })
        {
            _tips[box] = ToolTip.GetTip(box);
        }

        // Raccourcis affichés à droite des entrées du menu (le texte de WPF, « Ctrl+C », « Ctrl+A »).
        CopyItem.Header = MenuHeader.WithGesture(Strings.TailCopy, "Ctrl+C");
        SelectAllItem.Header = MenuHeader.WithGesture(Strings.TailSelectAll, "Ctrl+A");

        _alertTerms = TailText.ParseTerms(settings.TailAlerts);
        _style = BuildStyle();
        _run = _filter.Start<TailLine>();
        LogList.ItemsSource = _rows;
        SourcesList.ItemsSource = _feeds;

        // Branchés après les valeurs initiales : elles ne doivent pas déclencher d'affichage.
        FilterBox.TextChanged += (_, _) => UpdateFilter();
        ExcludeBox.TextChanged += (_, _) => UpdateFilter();
        RegexBox.Click += (_, _) => UpdateFilter();
        ContextBox.SelectionChanged += (_, _) => UpdateFilter();
        HighlightBox.TextChanged += (_, _) => RefreshStyle();
        HighlightBox.LostFocus += (_, _) => SaveTailSettings();
        AlertBox.TextChanged += (_, _) => _alertTerms = TailText.ParseTerms(AlertBox.Text);
        AlertBox.LostFocus += (_, _) => SaveTailSettings();
        SearchBox.TextChanged += (_, _) => UpdateSearch();

        // Avant les contrôles : Ctrl+F et F3 valent dans toute la fenêtre ; Entrée et Échap dans le champ de recherche.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        LogList.KeyDown += OnLogKeyDown;
        LogMenu.Opening += (_, _) => CopyItem.IsEnabled = LogList.Selection.Count > 0;

        _timer.Tick += async (_, _) => await PollAsync();
        Opened += async (_, _) =>
        {
            _opened = true;
            _scroller = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroller is not null)
            {
                _scroller.ScrollChanged += OnLogScrollChanged;
            }

            await PollAsync();
            // Fenêtre fermée pendant la première lecture : le minuteur ne démarre pas (il la garderait en mémoire).
            if (!_closing.IsCancellationRequested)
            {
                _timer.Start();
            }
        };
        Activated += (_, _) => _unnotified = 0;
        Palette.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Palette.Changed -= OnThemeChanged;
            _timer.Stop();
            _closing.Cancel();
            StopRecording(null);
            foreach (var link in _links)
            {
                link.Dispose();
            }

            _links.Clear();
            TailAlerts.Forget(this);
            SaveTailSettings();
        };
    }

    /// <summary>Thème clair ou sombre changé : couleurs des fichiers et des lignes affichées recalculées.</summary>
    private void OnThemeChanged()
    {
        foreach (var feed in _feeds)
        {
            feed.ThemeChanged();
        }

        Render();
    }

    /// <summary>Notification d'une alerte (tests) ; par défaut, notification du système.</summary>
    internal Action<string, string>? Notifier { get; set; }

    /// <summary>Fichiers suivis (tests).</summary>
    internal IReadOnlyList<TailFeed> Feeds => _feeds;

    internal int AlertCount => _alertCount;

    /// <summary>Lignes affichées, une par ligne de texte (tests).</summary>
    internal string Shown => string.Concat(_rows.Select(r => r.Text + "\n"));

    // ===================== Fichiers suivis =====================

    /// <summary>Connexion de cette fenêtre à la session, à réutiliser pour un autre fichier du même serveur.</summary>
    internal ITailLink? LinkFor(RemoteSession session) => _links.FirstOrDefault(l => ReferenceEquals(l.Session, session));

    /// <summary>Suit un fichier de plus dans cette fenêtre ; à partir de deux, les lignes sont préfixées par leur fichier.</summary>
    internal TailFeed AddFeed(ITailLink link, string path)
    {
        if (_feeds.FirstOrDefault(f => !f.Stopped && ReferenceEquals(f.Link, link) && f.Path == path) is { } existing)
        {
            return existing;
        }

        if (!_links.Contains(link))
        {
            _links.Add(link);
        }

        var feed = new TailFeed(link, path, _colors++);
        feed.SetStatus(Strings.TailConnecting, error: false);
        _feeds.Add(feed);
        UpdateTitle();
        if (!_combined && _feeds.Count > 1)
        {
            _combined = true;
            RefreshStyle();
        }

        if (_opened)
        {
            _ = PollAsync();
        }

        return feed;
    }

    /// <summary>La session se ferme : ses fichiers ne sont plus suivis (les lignes reçues restent affichées).</summary>
    internal void EndSession(RemoteSession session)
    {
        foreach (var feed in _feeds.Where(f => !f.Stopped && ReferenceEquals(f.Link.Session, session)).ToList())
        {
            AddMarker(feed, Strings.TailSessionClosedMarker);
            feed.Release();
            feed.SetStatus(Strings.TailSessionClosed, error: true);
        }

        foreach (var link in _links.Where(l => ReferenceEquals(l.Session, session)).ToList())
        {
            link.Dispose();
            _links.Remove(link);
        }

        UpdateReconnectButton();
    }

    internal void RemoveFeed(TailFeed feed)
    {
        if (!feed.Stopped)
        {
            AddMarker(feed, Strings.TailRemovedMarker);
        }

        feed.Release();

        _feeds.Remove(feed);
        if (!_feeds.Any(f => ReferenceEquals(f.Link, feed.Link)) && _links.Remove(feed.Link))
        {
            feed.Link.Dispose();
        }

        UpdateTitle();
        UpdateReconnectButton();
    }

    private void OnRemoveFeed(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is TailFeed feed)
        {
            RemoveFeed(feed);
        }
    }

    private void UpdateTitle()
    {
        var active = _feeds.Where(f => !f.Stopped).ToList();
        if (active.Count == 1)
        {
            Title = Text.Format(Strings.TailTitle, active[0].DisplayPath, active[0].Link.Server);
        }
        else if (active.Count > 1)
        {
            Title = Text.Format(Strings.TailTitleMany, active.Count);
        }
    }

    // ===================== Relevés =====================

    /// <summary>Relève ce qui a été ajouté aux fichiers et l'affiche.</summary>
    internal async Task PollAsync()
    {
        if (_polling || _paused || _closing.IsCancellationRequested)
        {
            return;
        }

        _polling = true;
        try
        {
            // Les fichiers sont relevés ensemble ; leurs lignes s'affichent dans l'ordre où elles arrivent.
            await Task.WhenAll(_feeds.Where(f => !f.Stopped).ToList().Select(PollFeedAsync));
            FlushStalePartials();
            _lastUpdate = DateTime.Now;
            UpdateReconnectButton();
            UpdateStatus();
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task PollFeedAsync(TailFeed feed)
    {
        if (feed.Busy || DateTime.UtcNow < feed.NextAttempt)
        {
            return;
        }

        var link = feed.Link;
        if (!link.CheckConnected())
        {
            if (feed.Generation > 0 && !feed.Lost)
            {
                feed.Lost = true;
                AddMarker(feed, Strings.TailLostMarker);
            }

            if (link.CanReconnect)
            {
                _ = ReconnectAsync(link);
            }

            feed.SetStatus(link.IsConnecting ? (link.Dedicated ? Strings.TailOpeningDedicated : Strings.TailConnecting)
                : link.Error is { } error ? Text.Format(Strings.TailConnectFailed, error)
                : Strings.TailWaiting, error: !link.IsConnecting);
            return;
        }

        if (feed.Generation != link.Generation)
        {
            // Première connexion, ou nouvelle connexion après une coupure : le suivi reprend là où il s'était arrêté.
            feed.Tail.Source = link.Source(feed.Path);
            feed.Generation = link.Generation;
            if (feed.Lost)
            {
                feed.Lost = false;
                AddMarker(feed, Strings.TailResumedMarker);
            }
        }

        feed.Busy = true;
        try
        {
            var update = await feed.Tail.PollAsync(_closing.Token);
            if (feed.Stopped)
            {
                return;
            }

            if (update.Restarted)
            {
                AddMarker(feed, Strings.TailRestarted);
            }

            if (update.Skipped > 0)
            {
                AddMarker(feed, Text.Format(Strings.TailSkipped, RemotePath.FormatSize(update.Skipped)));
            }

            Receive(feed, update.Text);
            feed.Started = true;
            feed.SetStatus(RemotePath.FormatSize(update.Size), error: false);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Fenêtre fermée.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fichier supprimé, droits, coupure brève : nouvel essai un peu plus tard.
            feed.NextAttempt = DateTime.UtcNow.AddSeconds(5);
            if (!feed.Stopped)
            {
                feed.SetStatus(Text.Format(Strings.TailError, ErrorText.Describe(ex)), error: true);
            }
        }
        finally
        {
            feed.Busy = false;
        }
    }

    private async Task ReconnectAsync(ITailLink link)
    {
        await link.ReconnectAsync();
        if (!_closing.IsCancellationRequested)
        {
            UpdateReconnectButton();
            await PollAsync();
        }
    }

    private void UpdateReconnectButton() =>
        ReconnectButton.IsVisible = _feeds.Any(f => !f.Stopped && !f.Link.IsConnecting && !f.Link.CheckConnected());

    private void OnReconnect(object? sender, RoutedEventArgs e)
    {
        foreach (var link in _links.Where(l => !l.CheckConnected()))
        {
            link.RequestReconnect();
        }

        foreach (var feed in _feeds)
        {
            feed.NextAttempt = default;
        }

        _ = PollAsync();
    }

    // ===================== Lignes =====================

    private void Receive(TailFeed feed, string text)
    {
        var before = feed.Splitter.Partial;
        var lines = feed.Splitter.Push(text);
        if (feed.Splitter.Partial != before)
        {
            feed.PartialSince = DateTime.UtcNow;
        }

        AddLines(feed, lines, alerts: feed.Started, marker: false);
    }

    /// <summary>Ligne restée sans fin de ligne depuis quelques secondes : affichée telle quelle.</summary>
    private void FlushStalePartials()
    {
        foreach (var feed in _feeds)
        {
            if (feed.Splitter.Partial.Length > 0 && DateTime.UtcNow - feed.PartialSince > PartialDelay && feed.Splitter.Flush() is { } line)
            {
                AddLines(feed, [line], alerts: feed.Started, marker: false);
            }
        }
    }

    /// <summary>Repère (fichier relu, connexion perdue...), affiché même avec un filtre.</summary>
    private void AddMarker(TailFeed? feed, string text)
    {
        if (feed?.Splitter.Flush() is { } partial)
        {
            AddLines(feed, [partial], alerts: feed.Started, marker: false);
        }

        AddLines(feed, [text], alerts: false, marker: true);
    }

    private void AddLines(TailFeed? feed, IReadOnlyList<string> texts, bool alerts, bool marker)
    {
        if (texts.Count == 0)
        {
            return;
        }

        int newAlerts = 0;
        var added = new List<TailLine>(texts.Count);
        foreach (var text in texts)
        {
            var line = new TailLine(text, feed, marker ? TailLevel.None : TailText.DetectLevel(text), marker);
            if (!marker && alerts && _alertTerms.Count > 0 && TailText.ContainsAny(text, _alertTerms))
            {
                line.IsAlert = true;
                newAlerts++;
            }

            added.Add(line);
            Record(line);
        }

        FlushRecord();
        _lines.AddRange(added);
        if (_lines.Count > MaxLines + TrimBatch)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
            Render();
        }
        else
        {
            var shown = new List<TailShown<TailLine>>();
            foreach (var line in added)
            {
                _run.Feed(line, line.Text, line.IsMarker, shown);
            }

            // Ajoutées d'un coup : la liste n'est prévenue qu'une fois, quel que soit le nombre de lignes.
            _rows.AddRange(shown.Select(item => new TailRow(item.Item, item.Kind, _style)).ToList());
            _matched += shown.Count(s => s.Kind == TailShownKind.Line);
            if (_filter.IsActive)
            {
                UpdateStatus();
            }

            if (shown.Count > 0)
            {
                ScrollIfFollowing();
            }
        }

        if (newAlerts > 0 && feed is not null)
        {
            OnAlerts(feed, newAlerts);
        }
    }

    /// <summary>Reconstruit la liste affichée (filtre, couleurs, recherche changés ; lignes retirées).</summary>
    private void Render()
    {
        // Garde la ligne sélectionnée, ou la première ligne visible, à l'écran.
        var selected = (LogList.SelectedItem as TailRow)?.Line;
        var anchor = FirstVisibleIndex() is var top and >= 0 && top < _rows.Count ? _rows[top].Line : null;

        _run = _filter.Start<TailLine>();
        var shown = new List<TailShown<TailLine>>(_lines.Count);
        foreach (var line in _lines)
        {
            _run.Feed(line, line.Text, line.IsMarker, shown);
        }

        _rows = new TailRowList(shown.Select(s => new TailRow(s.Item, s.Kind, _style)));
        _matched = shown.Count(s => s.Kind == TailShownKind.Line);
        LogList.ItemsSource = _rows;
        if (FollowBox.IsChecked == true)
        {
            ScrollIfFollowing();
        }
        else if (selected is not null && IndexOf(selected, skipSeparators: true) is var row and >= 0)
        {
            LogList.SelectedIndex = row;
            ShowRow(row);
        }
        else if (anchor is not null && IndexOf(anchor, skipSeparators: false) is var index and >= 0)
        {
            ShowRow(index, atTop: true);
        }

        UpdateStatus();
    }

    private int IndexOf(TailLine line, bool skipSeparators)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i].Line, line) && !(skipSeparators && _rows[i].Kind == TailShownKind.Separator))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Rang de la première ligne visible en haut de la liste ; -1 si aucune.</summary>
    private int FirstVisibleIndex()
    {
        if (_scroller is null)
        {
            return -1;
        }

        int first = -1;
        double best = double.MaxValue;
        foreach (var container in LogList.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _scroller) is not { } point || point.Y + container.Bounds.Height <= 0.5
                || point.Y >= _scroller.Viewport.Height)
            {
                continue;
            }

            if (point.Y < best)
            {
                best = point.Y;
                first = LogList.IndexFromContainer(container);
            }
        }

        return first;
    }

    /// <summary>
    /// Fait paraître la ligne <paramref name="index"/> (en haut de la liste si <paramref name="atTop"/>), sans changer le
    /// défilement horizontal.
    /// </summary>
    private void ShowRow(int index, bool atTop = false)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _scrolling = true;
        try
        {
            double x = _scroller?.Offset.X ?? 0;
            LogList.ScrollIntoView(index);
            if (_scroller is not { } scroller)
            {
                return;
            }

            double y = scroller.Offset.Y;
            if (atTop && LogList.ContainerFromIndex(index) is { } container && container.TranslatePoint(default, scroller) is { } point)
            {
                y += point.Y;
            }

            scroller.Offset = new Vector(x, y);
        }
        finally
        {
            _scrolling = false;
        }
    }

    private void ScrollIfFollowing()
    {
        if (FollowBox.IsChecked == true)
        {
            ShowRow(_rows.Count - 1);
        }
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // L'utilisateur remonte dans les lignes : la vue ne saute plus à la fin à chaque nouvelle ligne.
        if (!_scrolling && _scroller is { } scroller && e.OffsetDelta.Y < 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0
            && scroller.Offset.Y + scroller.Viewport.Height < scroller.Extent.Height - 0.5)
        {
            FollowBox.IsChecked = false;
        }
    }

    private void OnFollow(object? sender, RoutedEventArgs e)
    {
        if (FollowBox.IsChecked == true)
        {
            LogList.UnselectAll();
            ScrollIfFollowing();
        }
    }

    // ===================== Filtre, couleurs, mots surlignés =====================

    private TailStyle BuildStyle() =>
        new(TailText.ParseTerms(HighlightBox.Text), _search, ColorsBox.IsChecked == true, _combined, WrapBox.IsChecked == true);

    private void RefreshStyle()
    {
        _style = BuildStyle();
        Render();
    }

    private void UpdateFilter()
    {
        bool regex = RegexBox.IsChecked == true;
        var filter = FilterBox.Text ?? "";
        var excluded = ExcludeBox.Text ?? "";
        var include = TailPattern.Create(regex ? filter : filter.Trim(), regex, out var includeError);
        var exclude = TailPattern.Create(regex ? excluded : excluded.Trim(), regex, out var excludeError);
        MarkInvalid(FilterBox, includeError);
        MarkInvalid(ExcludeBox, excludeError);
        MarkFilled(FilterBox, ClearFilterButton);
        MarkFilled(ExcludeBox, ClearExcludeButton);
        _filterPatterns = [include, exclude];
        _filter = new TailFilter(include, exclude, ContextBox.SelectedItem is int context ? context : 0);
        Render();
    }

    /// <summary>Champ de filtre rempli : fond ambre et bouton ✕ pour le vider.</summary>
    private static void MarkFilled(TextBox box, Button clear)
    {
        bool filled = !string.IsNullOrEmpty(box.Text);
        box.Classes.Set("Filled", filled);

        // Bouton caché sans libérer sa place (Visibility.Hidden de WPF) : la ligne ne bouge pas.
        clear.Opacity = filled ? 1 : 0;
        clear.IsEnabled = filled;
    }

    private void OnClearFilter(object? sender, RoutedEventArgs e)
    {
        FilterBox.Clear();
        FilterBox.Focus();
    }

    private void OnClearExclude(object? sender, RoutedEventArgs e)
    {
        ExcludeBox.Clear();
        ExcludeBox.Focus();
    }

    /// <summary>Expression régulière invalide : bordure rouge et message dans l'info-bulle.</summary>
    private void MarkInvalid(TextBox box, string? error)
    {
        box.Classes.Set("Invalid", error is not null);
        ToolTip.SetTip(box, error is null ? _tips.GetValueOrDefault(box) : Text.Format(Strings.TailRegexInvalid, error));
    }

    private void OnColors(object? sender, RoutedEventArgs e)
    {
        RefreshStyle();
        SaveTailSettings();
    }

    private void OnWrap(object? sender, RoutedEventArgs e)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(LogList,
            WrapBox.IsChecked == true ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        RefreshStyle();
    }

    private void OnNotify(object? sender, RoutedEventArgs e) => SaveTailSettings();

    /// <summary>Couleurs, mots surlignés et alertes sont gardés pour les prochaines fenêtres.</summary>
    private void SaveTailSettings()
    {
        bool colors = ColorsBox.IsChecked == true;
        bool notify = NotifyBox.IsChecked == true;
        var highlights = (HighlightBox.Text ?? "").Trim();
        var alerts = (AlertBox.Text ?? "").Trim();
        if (_settings.TailLevelColors == colors && _settings.TailAlertNotify == notify
            && _settings.TailHighlights == highlights && _settings.TailAlerts == alerts)
        {
            return;
        }

        _settings.TailLevelColors = colors;
        _settings.TailAlertNotify = notify;
        _settings.TailHighlights = highlights;
        _settings.TailAlerts = alerts;
        _saveSettings();
    }

    // ===================== Alertes =====================

    private void OnAlerts(TailFeed feed, int count)
    {
        _alertCount += count;
        _unnotified += count;
        UpdateAlertButton();
        if (IsActive)
        {
            _unnotified = 0;
            return;
        }

        TailAlerts.Flash(this);
        if (NotifyBox.IsChecked == true && DateTime.UtcNow - _lastNotification >= NotifyInterval)
        {
            // Nombre de lignes et nom du fichier seulement : jamais le contenu du journal.
            var text = Text.Format(Strings.TailNotificationText, _unnotified, feed.DisplayName);
            _lastNotification = DateTime.UtcNow;
            _unnotified = 0;
            if (Notifier is { } notifier)
            {
                notifier(Strings.TailNotificationTitle, text);
            }
            else
            {
                TailAlerts.Notify(this, Strings.TailNotificationTitle, text, () => SelectNext(r => r.Line?.IsAlert == true, -1, fromEnd: true));
            }
        }
    }

    private void UpdateAlertButton()
    {
        AlertButton.IsVisible = _alertCount > 0;
        AlertButton.Content = Text.Format(Strings.TailAlertCount, _alertCount);
    }

    private void OnNextAlert(object? sender, RoutedEventArgs e)
    {
        if (!SelectNext(r => r.Kind == TailShownKind.Line && r.Line?.IsAlert == true, 1))
        {
            ShowNotice(Strings.TailAlertNone, error: false);
        }
    }

    private void OnResetAlerts(object? sender, RoutedEventArgs e)
    {
        _alertCount = 0;
        UpdateAlertButton();
    }

    // ===================== Recherche =====================

    private void OnFind(object? sender, RoutedEventArgs e) => ShowSearch();

    private void ShowSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnCloseSearch(object? sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        SearchBar.IsVisible = false;
        SearchBox.Clear();
        LogList.Focus();
    }

    private void OnSearchOption(object? sender, RoutedEventArgs e) => UpdateSearch();

    private void UpdateSearch()
    {
        _search = TailPattern.Create(SearchBox.Text, SearchRegexBox.IsChecked == true, out var error);
        MarkInvalid(SearchBox, error);
        RefreshStyle();
        SearchCount.Text = _search is null ? "" : CountText(-1);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSearch();
        }
    }

    private void OnSearchPrevious(object? sender, RoutedEventArgs e) => FindNext(-1);

    private void OnSearchNext(object? sender, RoutedEventArgs e) => FindNext(1);

    /// <summary>Ctrl (ou Cmd sous macOS) seul : raccourcis de la fenêtre (Ctrl+F, Ctrl+C, Ctrl+A).</summary>
    private static bool IsCommand(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.Control || (OperatingSystem.IsMacOS() && modifiers == KeyModifiers.Meta);

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && IsCommand(e.KeyModifiers))
        {
            e.Handled = true;
            ShowSearch();
        }
        else if (e.Key == Key.F3)
        {
            e.Handled = true;
            FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }
    }

    /// <summary>Liste des lignes : Ctrl+C copie les lignes sélectionnées, Ctrl+A les sélectionne toutes.</summary>
    private void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCommand(e.KeyModifiers))
        {
            return;
        }

        if (e.Key is Key.C or Key.Insert)
        {
            e.Handled = true;
            _ = CopyAsync();
        }
        else if (e.Key == Key.A)
        {
            e.Handled = true;
            LogList.SelectAll();
        }
    }

    /// <summary>Ligne suivante (ou précédente) contenant le texte cherché ; la vue arrête de suivre la fin.</summary>
    internal void FindNext(int direction)
    {
        if (_search is not { } search)
        {
            ShowSearch();
            return;
        }

        SearchCount.Text = SelectNext(r => r.IsText && search.IsMatch(r.Line!.Text), direction)
            ? CountText(LogList.SelectedIndex)
            : Strings.TailSearchNone;
    }

    /// <summary>Nombre de lignes trouvées, et rang de la ligne sélectionnée.</summary>
    private string CountText(int selected)
    {
        if (_search is not { } search)
        {
            return "";
        }

        int total = 0;
        int rank = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].IsText && search.IsMatch(_rows[i].Line!.Text))
            {
                total++;
                if (i == selected)
                {
                    rank = total;
                }
            }
        }

        return total == 0 ? Strings.TailSearchNone
            : rank > 0 ? Text.Format(Strings.TailSearchCount, rank, total)
            : Text.Format(Strings.TailSearchTotal, total);
    }

    /// <summary>Sélectionne la ligne suivante qui convient, en repartant du début (ou de la fin) si besoin.</summary>
    private bool SelectNext(Func<TailRow, bool> predicate, int direction, bool fromEnd = false)
    {
        int count = _rows.Count;
        if (count == 0)
        {
            return false;
        }

        int start = fromEnd ? count : LogList.SelectedIndex;
        if (start < 0)
        {
            start = direction > 0 ? -1 : count;
        }

        for (int i = 1; i <= count; i++)
        {
            int index = ((start + (direction * i)) % count + count) % count;
            if (predicate(_rows[index]))
            {
                FollowBox.IsChecked = false;
                LogList.SelectedIndex = index;
                ShowRow(index);
                return true;
            }
        }

        return false;
    }

    // ===================== Copie, repère, effacement, pause =====================

    private void OnCopy(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private void OnSelectAll(object? sender, RoutedEventArgs e) => LogList.SelectAll();

    /// <summary>Lignes sélectionnées, dans l'ordre de la liste (pas celui des clics).</summary>
    internal string SelectedText() =>
        string.Join(Environment.NewLine, LogList.Selection.SelectedIndexes.Where(i => i >= 0 && i < _rows.Count).Order().Select(i => _rows[i].Text));

    private async Task CopyAsync()
    {
        if (LogList.Selection.Count == 0)
        {
            return;
        }

        var text = SelectedText();
        try
        {
            // Lignes du journal choisies par l'utilisateur : presse-papiers ordinaire.
            if (Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Core.Diagnostics.DebugLog.Write("tail", "copie impossible", ex);
            ShowNotice(Strings.ClipboardBusy, error: true);
        }
    }

    /// <summary>Ligne « —— 14:32:05 —— » pour retrouver un moment (avant un test, une manipulation...).</summary>
    internal void AddUserMarker() => AddMarker(null, Text.Format(Strings.TailMarkerText, DateTime.Now.ToString("HH:mm:ss")));

    private void OnMarker(object? sender, RoutedEventArgs e) => AddUserMarker();

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        _lines.Clear();
        _alertCount = 0;
        _notice = null;
        UpdateAlertButton();
        Render();
    }

    private async void OnPause(object? sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? Strings.TailResume : Strings.TailPause;
        UpdateStatus();
        if (!_paused)
        {
            await PollAsync();
        }
    }

    // ===================== Enregistrement =====================

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (await Pickers.SaveFileAsync(this, Strings.TailSaveFilter, DefaultFileName(), Strings.TailSaveTitle) is { } path
            && !_closing.IsCancellationRequested)
        {
            SaveTo(path);
        }
    }

    /// <summary>Enregistre les lignes affichées (avec le filtre en cours) dans un fichier de ce poste.</summary>
    internal void SaveTo(string path)
    {
        try
        {
            var lines = _rows.Select(r => r.Text).ToList();
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            ShowNotice(Text.Format(Strings.TailSaved, lines.Count, Path.GetFileName(path)), error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ShowNotice(Text.Format(Strings.TailSaveFailed, ex.Message), error: true);
        }
    }

    private async void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (RecordBox.IsChecked != true)
        {
            StopRecording(null);
            return;
        }

        var path = await Pickers.SaveFileAsync(this, Strings.TailSaveFilter, DefaultFileName(), Strings.TailRecordTitle);
        if (path is not null && !_closing.IsCancellationRequested)
        {
            StartRecording(path);
        }
        else
        {
            RecordBox.IsChecked = false;
        }
    }

    /// <summary>
    /// Enregistrement en continu : les lignes déjà reçues, puis chaque nouvelle ligne dès son arrivée (sans le filtre),
    /// tant que la case est cochée.
    /// </summary>
    /// <remarks>Fins de ligne du système (CRLF sous Windows, comme ZillaTerm.App ; LF sous Linux et macOS).</remarks>
    internal void StartRecording(string path)
    {
        StopRecording(null);
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _record = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = Environment.NewLine };
            _recordPath = path;
            foreach (var line in _lines)
            {
                _record.WriteLine(line.Format(_combined));
            }

            _record.Flush();
            RecordBox.IsChecked = true;
            _notice = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            StopRecording(ex.Message);
        }

        UpdateStatus();
    }

    private void Record(TailLine line)
    {
        if (_record is null)
        {
            return;
        }

        try
        {
            _record.WriteLine(line.Format(_combined));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            StopRecording(ex.Message);
        }
    }

    private void FlushRecord()
    {
        try
        {
            _record?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            StopRecording(ex.Message);
        }
    }

    private void StopRecording(string? error)
    {
        var writer = _record;
        _record = null;
        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // Disque plein, partage perdu : l'erreur d'écriture a déjà été signalée.
        }

        RecordBox.IsChecked = false;
        if (error is not null)
        {
            ShowNotice(Text.Format(Strings.TailRecordFailed, error), error: true);
        }
        else
        {
            UpdateStatus();
        }
    }

    private string DefaultFileName()
    {
        var active = _feeds.Where(f => !f.Stopped).ToList();
        var name = active.Count == 1 ? Path.GetFileNameWithoutExtension(RemotePath.Name(active[0].Path)) : "tail";

        // Caractères interdits dans un nom de fichier, ou invisibles (inversion du sens d'écriture…) : remplacés.
        var invalid = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Select(c => invalid.Contains(c) || RemoteEntry.IsInvisible(c) ? '_' : c));

        return $"{(name.Length == 0 ? "tail" : name)}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
    }

    // ===================== État =====================

    private void ShowNotice(string text, bool error)
    {
        _notice = text;
        _noticeError = error;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();
        bool error = _notice is not null && _noticeError;
        // Filtre actif en tête : on voit combien de lignes il cache.
        if (_filter.IsActive)
        {
            parts.Add(Text.Format(Strings.TailFilterActive, _matched, _lines.Count));
        }

        if (_paused)
        {
            parts.Add(Strings.TailPausedState);
        }
        else if (_lastUpdate != default)
        {
            parts.Add(Text.Format(Strings.TailUpdated, _lastUpdate.ToString("T")));
        }

        if (_record is not null)
        {
            parts.Add(Text.Format(Strings.TailRecording, Path.GetFileName(_recordPath)));
        }

        if (_filterPatterns.Append(_search).Any(p => p?.TimedOut == true))
        {
            parts.Add(Strings.TailRegexSlow);
            error = true;
        }

        if (_notice is not null)
        {
            parts.Add(_notice);
        }

        StatusText.Text = string.Join(" · ", parts);
        StatusText[!TextBlock.ForegroundProperty] = StatusText.GetResourceObservable(error ? "ErrorBrush" : "MutedBrush").ToBinding();
    }
}
