using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Terminal;
using ZillaTerm.Core;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

/// <summary>Contenu d'un onglet de session SSH : le terminal, et un voile pendant la connexion ou après la fermeture.</summary>
/// <remarks>
/// Un contrôle Avalonia n'a qu'un parent : la vue est déplacée telle quelle entre son onglet, une fenêtre séparée et la
/// vue parallèle (retirée de l'un avant d'être mise dans l'autre). Le terminal garde son émulateur (donc l'historique),
/// se réajuste à sa nouvelle place et en informe le serveur.
/// </remarks>
public partial class SshSessionView : UserControl
{
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly string _connectingText = "";
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _current = -1;

    // Valeurs posées par UpdateHistoryBar : seule une action de l'utilisateur sur la barre déplace le terminal.
    private bool _updatingHistoryBar;

    /// <summary>Pour le chargeur XAML d'Avalonia seulement ; une session s'affiche avec l'autre constructeur.</summary>
    public SshSessionView()
    {
        InitializeComponent();
        Session = null!;
        Target = "";
    }

    /// <param name="connectingText">Message pendant la connexion (via le PSMP, ou directe pour un accès d'urgence).</param>
    public SshSessionView(SshSession session, string target, string connectingText)
    {
        InitializeComponent();
        Session = session;
        Target = target;
        _connectingText = connectingText;
        Terminal.Emulator = session.Emulator;
        Terminal.Input += OnInput;
        Terminal.TerminalResized += session.Resize;
        session.ScreenUpdated += Terminal.Refresh;
        session.StateChanged += UpdateOverlay;
        session.ResponseChanged += UpdateOverlay;
        UpdateOverlay();
        Terminal.ScrollStateChanged += UpdateHistoryBar;
        Terminal.SearchRequested += ShowSearch;
        Terminal.SaveRequested += () => _ = SaveContentAsync();
        Terminal.ScrollbackCleared += () =>
        {
            if (SearchBar.IsVisible)
            {
                RunSearch(keepPosition: false);
            }
        };
        session.ScreenUpdated += OnScreenUpdatedForSearch;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSearch(keepPosition: true);
        };
        HistoryBar.ValueChanged += OnHistoryScroll;
        // Entrée et Échap de la recherche avant que la zone de texte ne les traite.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        // Fond autour du terminal : celui de la palette (suivi tant que la vue est affichée, où qu'elle soit).
        Loaded += (_, _) =>
        {
            ApplyBackground();
            TerminalAppearance.Changed += OnAppearanceChanged;
        };
        Unloaded += (_, _) => TerminalAppearance.Changed -= OnAppearanceChanged;
    }

    /// <summary>Palette changée, éventuellement depuis un autre fil : le fond est mis à jour sur celui de la vue.</summary>
    private void OnAppearanceChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyBackground();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyBackground);
        }
    }

    private void ApplyBackground()
    {
        var rgb = TerminalAppearance.Theme.Background;
        RootGrid.Background = new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    // ===================== Recherche (Ctrl+Maj+F) =====================

    public void ShowSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        RunSearch(keepPosition: false);
    }

    /// <summary>Occurrences de la recherche (tests).</summary>
    internal IReadOnlyList<TerminalMatch> Matches => _matches;

    internal int CurrentMatch => _current;

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RunSearch(keepPosition: false);

    /// <summary>Cherche dans tout le terminal ; la plus récente occurrence d'abord.</summary>
    private void RunSearch(bool keepPosition)
    {
        int fromEnd = _matches.Count - 1 - _current;
        _matches = TerminalSearch.Find(Session.Emulator, SearchBox.Text ?? "");
        _current = _matches.Count == 0 ? -1 : keepPosition ? Math.Max(0, _matches.Count - 1 - fromEnd) : _matches.Count - 1;
        Terminal.ShowMatches(_matches, _current, scroll: !keepPosition);
        UpdateSearchCount();
    }

    /// <summary>Nouvelles lignes pendant la recherche : les occurrences sont recalculées un peu après.</summary>
    private void OnScreenUpdatedForSearch()
    {
        if (SearchBar.IsVisible && !string.IsNullOrEmpty(SearchBox.Text))
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    /// <param name="direction">-1 : plus ancien, 1 : plus récent.</param>
    internal void MoveSearch(int direction)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _current = ((_current + direction) % _matches.Count + _matches.Count) % _matches.Count;
        Terminal.ShowMatches(_matches, _current);
        UpdateSearchCount();
    }

    private void UpdateSearchCount() =>
        SearchCount.Text = string.IsNullOrEmpty(SearchBox.Text) ? ""
            : _matches.Count == 0 ? Strings.TailSearchNone
            : Text.Format(Strings.TailSearchCount, _current + 1, _matches.Count);

    private void OnSearchOlder(object? sender, RoutedEventArgs e) => MoveSearch(-1);

    private void OnSearchNewer(object? sender, RoutedEventArgs e) => MoveSearch(1);

    private void OnSearchClose(object? sender, RoutedEventArgs e) => CloseSearch();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                MoveSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
        }
    }

    private void CloseSearch()
    {
        _searchTimer.Stop();
        SearchBar.IsVisible = false;
        _matches = [];
        _current = -1;
        Terminal.ClearMatches();
        FocusTerminal();
    }

    // ===================== Enregistrement (Ctrl+Maj+S) =====================

    /// <summary>Contenu du terminal (historique compris) dans un fichier texte de ce poste, à la demande.</summary>
    public async Task SaveContentAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var name = string.Concat(Session.Label.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = await Pickers.SaveFileAsync(owner, Strings.TerminalSaveFilter, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt", Strings.TerminalSaveTitle);
        if (path is null)
        {
            FocusTerminal();
            return;
        }

        try
        {
            // Le terminal tel qu'il est une fois le fichier choisi (la session a pu continuer pendant le choix).
            System.IO.File.WriteAllText(path, TerminalSearch.AllText(Session.Emulator) + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            await ConfirmDialog.ShowMessageAsync(owner, ex.Message, ConfirmKind.Danger, Strings.TerminalSaveTitle);
        }

        FocusTerminal();
    }

    public SshSession Session { get; }

    /// <summary>Étiquette du serveur (PROD, QA…), montrée en cadre de sa couleur autour du terminal.</summary>
    public ServerTag? ServerTag { get; private set; }

    public void ShowTag(ServerTag? tag)
    {
        ServerTag = tag;
        ServerTagView.Frame(TagFrame, tag);
    }

    /// <summary>« coffre@compte@cible via psmp », affiché pendant la connexion.</summary>
    public string Target { get; }

    /// <summary>
    /// Destinataires de la saisie (vue parallèle avec saisie simultanée) ; sans aiguillage, elle va à cette session.
    /// </summary>
    public Action<SshSessionView, TerminalInput>? InputRouter { get; set; }

    /// <summary>Saisie reçue, encodée selon l'état du terminal de cette session.</summary>
    public void Send(TerminalInput input) => Session.SendInput(input.Encode(Session.Emulator));

    /// <summary>
    /// Question avant un collage de plusieurs lignes que le shell exécuterait une à une (collage protégé non activé :
    /// bash avant 5.1, ksh…) ; vrai pour coller. Sans question : le collage part.
    /// </summary>
    public Func<SshSessionView, TerminalInput, Task<bool>>? PasteGuard { get; set; }

    /// <summary>
    /// Vrai si cette saisie peut partir dans la session : pas un collage de plusieurs lignes à risque, ou accepté. Sans
    /// question à poser, la réponse est immédiate (la frappe part dans l'ordre où elle a été faite).
    /// </summary>
    public Task<bool> MayPasteAsync(TerminalInput input) =>
        !input.IsMultiLinePaste || Session.Emulator.BracketedPaste || PasteGuard is not { } guard
            ? Task.FromResult(true)
            : guard(this, input);

    private async void OnInput(TerminalInput input)
    {
        if (InputRouter is { } router && input.IsTyping)
        {
            router(this, input);
        }
        else if (await MayPasteAsync(input))
        {
            Send(input);
        }
    }

    /// <summary>Lignes d'un texte collé, pour l'aperçu de la question (les dernières fins de ligne ne comptent pas).</summary>
    public static IReadOnlyList<string> PastedLines(string text) =>
        text.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n', '\r');

    /// <summary>Actions de la session (reconnecter, dupliquer…) à la fin du menu du clic droit dans le terminal.</summary>
    public Action<ItemCollection>? SessionMenu
    {
        get => Terminal.ExtraMenuItems;
        set => Terminal.ExtraMenuItems = value;
    }

    public (int Columns, int Rows) TerminalSize => Terminal.Bounds.Width > 0 ? Terminal.SizeInCells : (100, 30);

    public void FocusTerminal() => Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);

    public async Task ConnectAsync()
    {
        // Laisse la mise en page se faire pour connaître la taille du terminal.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var (columns, rows) = TerminalSize;
        try
        {
            await Session.ConnectAsync(columns, rows);
            // Le terminal a pu changer de place pendant la connexion (vue parallèle, fenêtre séparée) : bonne taille au serveur.
            Terminal.Refit();
            FocusTerminal();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // L'état « Failed » et son message sont affichés par UpdateOverlay.
        }
    }

    // La session a été ouverte au moins une fois : à sa fin, le terminal garde son contenu, lisible sous un bandeau.
    private bool _wasConnected;

    private void UpdateOverlay()
    {
        switch (Session.State)
        {
            case RemoteSessionState.Connected when Session.IsNotResponding:
            {
                // Saisie restée sans réponse : terminal grisé et bandeau, jusqu'à ce que le serveur réponde ou que la
                // connexion soit déclarée perdue ; « Reconnecter » n'attend pas. Annoncé quand le serveur cesse de
                // répondre, pas à chaque seconde qui s'ajoute.
                _wasConnected = true;
                Overlay.IsVisible = false;
                var text = Text.Format(Strings.SshNotResponding, (int)Session.NotRespondingFor.TotalSeconds);
                if (EndBanner.IsVisible)
                {
                    EndText.SetQuietly(text);
                }
                else
                {
                    EndBanner.IsVisible = true;
                    EndText.Text = text;
                }

                Terminal.Opacity = 0.45;
                break;
            }

            case RemoteSessionState.Connected:
                _wasConnected = true;
                Overlay.IsVisible = EndBanner.IsVisible = false;
                // Bandeau caché : son prochain texte sera annoncé, même identique (« Session terminée » après une reconnexion).
                EndText.Text = "";
                Terminal.Opacity = 1;
                break;
            case RemoteSessionState.Connecting:
                Terminal.Opacity = 1;
                EndBanner.IsVisible = false;
                EndText.Text = "";
                Overlay.IsVisible = true;
                OverlayText.Text = _connectingText;
                OverlayDetail.Text = Target;
                OverlayProgress.IsVisible = true;
                ReconnectButton.IsVisible = false;
                break;
            default:
                Terminal.Opacity = 1;
                var title = Session.State == RemoteSessionState.Failed ? Strings.ConnectionImpossible : Strings.SessionEnded;
                if (_wasConnected)
                {
                    // Non bloquant : les dernières lignes (message d'erreur du serveur…) restent lisibles et copiables.
                    Overlay.IsVisible = false;
                    EndBanner.IsVisible = true;
                    EndText.Text = string.IsNullOrEmpty(Session.Error) ? title : Text.Format(Strings.SessionEndedWith, title, Session.Error);
                }
                else
                {
                    Overlay.IsVisible = true;
                    OverlayText.Text = title;
                    OverlayDetail.Text = Session.Error ?? "";
                    OverlayProgress.IsVisible = false;
                    ReconnectButton.IsVisible = true;
                }

                break;
        }
    }

    private async void OnReconnect(object? sender, RoutedEventArgs e) => await ConnectAsync();

    /// <summary>
    /// Barre de l'historique : du plus ancien (en haut) à la fin (en bas), la partie visible à la taille de l'écran ;
    /// « Revenir en bas » quand on lit l'historique pendant que la sortie continue.
    /// </summary>
    private void UpdateHistoryBar()
    {
        var (scrollback, offset, rows) = Terminal.ScrollState;
        _updatingHistoryBar = true;
        try
        {
            // La barre d'Avalonia décide elle-même de son affichage d'après Visibility (pas IsVisible).
            HistoryBar.Visibility = scrollback > 0 ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
            HistoryBar.Maximum = scrollback;
            HistoryBar.ViewportSize = Math.Max(1, rows);
            HistoryBar.LargeChange = Math.Max(1, rows - 1);
            HistoryBar.Value = scrollback - offset;
        }
        finally
        {
            _updatingHistoryBar = false;
        }

        BottomButton.IsVisible = offset > 0;
    }

    private void OnHistoryScroll(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingHistoryBar)
        {
            return;
        }

        var (scrollback, _, _) = Terminal.ScrollState;
        Terminal.ScrollTo(scrollback - (int)Math.Round(HistoryBar.Value));
    }

    private void OnBackToBottom(object? sender, RoutedEventArgs e)
    {
        Terminal.ScrollTo(0);
        FocusTerminal();
    }
}
