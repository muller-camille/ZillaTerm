using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>Connexion d'une fenêtre de suivi à un serveur.</summary>
internal interface ITailLink : IDisposable
{
    /// <summary>Serveur (libellé de la session).</summary>
    string Server { get; }

    /// <summary>Session d'origine : le suivi s'arrête quand elle se ferme (null ensuite, elle n'est plus retenue).</summary>
    RemoteSession? Session { get; }

    /// <summary>Connexion SFTP propre au suivi (option « session indépendante »).</summary>
    bool Dedicated { get; }

    /// <summary>Numéro de la connexion en cours : il change quand une nouvelle connexion remplace la précédente.</summary>
    int Generation { get; }

    bool IsConnecting { get; }

    /// <summary>Échec de la dernière tentative de connexion.</summary>
    string? Error { get; }

    /// <summary>Une connexion peut être ouverte maintenant, sans que l'utilisateur la demande.</summary>
    bool CanReconnect { get; }

    /// <summary>La connexion est ouverte.</summary>
    bool CheckConnected();

    /// <summary>Bouton « Reconnecter » : la prochaine relève rouvre la connexion.</summary>
    void RequestReconnect();

    Task ReconnectAsync();

    ITailSource Source(string path);
}

/// <summary>
/// Connexion d'une fenêtre de suivi à une session SSH : celle de l'onglet Fichiers (partagée), ou une connexion SFTP
/// dédiée, fermée avec la fenêtre. Une connexion perdue n'est rouverte que quand l'onglet se reconnecte ou sur le
/// bouton « Reconnecter » : jamais en boucle, chaque connexion étant une session PSMP (et peut-être une demande MFA).
/// Fermé (session fermée, fichiers retirés), le lien ne retient plus la session : les lignes reçues, gardées dans la
/// fenêtre, ne gardent pas en mémoire son terminal et son historique.
/// </summary>
internal sealed class SessionTailLink : ITailLink
{
    private RemoteSession? _session;
    private IRemoteFiles? _browser;
    private Task? _connecting;
    private bool _wanted;
    private bool _disposed;
    private RemoteSessionState _lastState;

    /// <param name="shared">Connexion de l'onglet Fichiers, utilisée si le suivi n'est pas indépendant.</param>
    public SessionTailLink(RemoteSession session, bool dedicated, IRemoteFiles? shared)
    {
        _session = session;
        Server = session.Label;
        Dedicated = dedicated;
        if (!dedicated && shared is not null)
        {
            _browser = shared;
            Generation = 1;
        }

        // Connexion dédiée : ouverte dès la première relève.
        _wanted = _browser is null;
        _lastState = session.State;
        session.StateChanged += OnStateChanged;
    }

    public string Server { get; }

    public RemoteSession? Session => _session;

    public bool Dedicated { get; }

    public int Generation { get; private set; }

    public string? Error { get; private set; }

    public bool IsConnecting => _connecting is { IsCompleted: false };

    public bool CanReconnect =>
        _wanted && _session is { IsDisposed: false, State: RemoteSessionState.Connected } && !IsConnecting;

    public bool CheckConnected()
    {
        if (_session is not { } session)
        {
            return false;
        }

        if (IsAlive(_browser))
        {
            _wanted = false;
            return true;
        }

        if (!Dedicated && session.OpenedBrowser is { } opened && !ReferenceEquals(opened, _browser))
        {
            // L'onglet Fichiers a rouvert la connexion de la session : le suivi la reprend.
            _browser = opened;
            Generation++;
            Error = null;
            _wanted = false;
            return true;
        }

        return false;
    }

    public void RequestReconnect() => _wanted = true;

    public Task ReconnectAsync()
    {
        if (_connecting is { IsCompleted: false } running)
        {
            return running;
        }

        _wanted = false;
        return _connecting = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            var browser = Dedicated ? await session.OpenDedicatedBrowserAsync() : await session.GetBrowserAsync();
            if (_disposed)
            {
                if (Dedicated)
                {
                    session.CloseDedicatedBrowser(browser);
                }

                return;
            }

            if (Dedicated && _browser is { } previous)
            {
                session.CloseDedicatedBrowser(previous);
            }

            _browser = browser;
            Generation++;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ErrorText.Describe(ex);
        }
    }

    public ITailSource Source(string path) => (_browser ?? throw new InvalidOperationException("Not connected.")).TailSource(path);

    private void OnStateChanged()
    {
        if (_session is not { } session)
        {
            return;
        }

        // L'onglet s'est reconnecté : les fichiers suivis reprennent, sur une nouvelle connexion si l'ancienne est perdue.
        if (session.State == RemoteSessionState.Connected && _lastState != RemoteSessionState.Connected)
        {
            _wanted = true;
        }

        _lastState = session.State;
    }

    private static bool IsAlive(IRemoteFiles? browser)
    {
        try
        {
            return browser?.IsConnected == true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is { } session)
        {
            session.StateChanged -= OnStateChanged;
            if (Dedicated && _browser is { } browser)
            {
                session.CloseDedicatedBrowser(browser);
            }
        }

        _session = null;
        _browser = null;
    }
}

/// <summary>Fichier suivi dans une fenêtre (une source de la vue combinée).</summary>
internal sealed class TailFeed : INotifyPropertyChanged
{
    private readonly int _color;

    /// <param name="color">Rang de la couleur du fichier dans la vue combinée.</param>
    public TailFeed(ITailLink link, string path, int color)
    {
        Link = link;
        Path = path;
        _color = color;
        Tail = new FileTail(NoSource.Instance);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ITailLink Link { get; }

    public string Path { get; }

    /// <summary>
    /// Chemin à afficher (titre, info-bulle) : ses caractères invisibles remplacés par « � », comme dans la liste des
    /// fichiers (<see cref="RemoteEntry.DisplayName"/>), pour qu'un nom ne puisse pas en imiter un autre.
    /// </summary>
    public string DisplayPath => RemoteEntry.Visible(Path);

    /// <summary>Nom du fichier à afficher (notification), comme <see cref="DisplayPath"/>.</summary>
    public string DisplayName => RemoteEntry.Visible(RemotePath.Name(Path));

    /// <summary>Préfixe des lignes dans la vue combinée, par ex. « root@srv01 app.log ».</summary>
    public string Label => $"{Link.Server} {DisplayName}";

    public string Tip => $"{Link.Server} : {DisplayPath}";

    public IBrush Brush => TailBrushes.Source(_color);

    /// <summary>Thème changé : couleur du fichier recalculée.</summary>
    public void ThemeChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    public FileTail Tail { get; }

    public TailLineSplitter Splitter { get; } = new();

    /// <summary>Dernier changement de la ligne en cours d'écriture.</summary>
    public DateTime PartialSince { get; set; }

    /// <summary>Connexion dont l'accès au fichier est utilisé.</summary>
    public int Generation { get; set; }

    /// <summary>Premier relevé fait : les lignes suivantes, nouvelles, peuvent déclencher une alerte.</summary>
    public bool Started { get; set; }

    /// <summary>Repère « connexion perdue » affiché, en attente de reconnexion.</summary>
    public bool Lost { get; set; }

    public bool Busy { get; set; }

    public bool Stopped { get; set; }

    public DateTime NextAttempt { get; set; }

    public string Status { get; private set; } = "";

    private bool _statusError;

    /// <summary>Couleur de l'état, du thème en vigueur (recalculée au changement de thème).</summary>
    public IBrush StatusBrush => _statusError ? TailBrushes.Error : TailBrushes.Muted;

    public void SetStatus(string text, bool error)
    {
        Status = text;
        _statusError = error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    /// <summary>Fichier plus suivi (ses lignes restent affichées) : l'accès au fichier, et sa connexion, ne sont plus retenus.</summary>
    public void Release()
    {
        Stopped = true;
        Tail.Source = NoSource.Instance;
    }

    /// <summary>Avant la première connexion.</summary>
    private sealed class NoSource : ITailSource
    {
        public static readonly NoSource Instance = new();

        public Task<long> GetSizeAsync(CancellationToken ct) => throw new InvalidOperationException("Not connected.");

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => throw new InvalidOperationException("Not connected.");
    }
}

/// <summary>Ligne reçue (ou repère).</summary>
internal sealed class TailLine(string text, TailFeed? feed, TailLevel level, bool marker)
{
    public string Text { get; } = text;

    /// <summary>Fichier d'origine ; null pour un repère ajouté par l'utilisateur.</summary>
    public TailFeed? Feed { get; } = feed;

    public TailLevel Level { get; } = level;

    public bool IsMarker { get; } = marker;

    public bool IsAlert { get; set; }

    /// <summary>Texte copié ou enregistré : préfixé par le fichier d'origine dans la vue combinée.</summary>
    public string Format(bool prefix) => prefix && Feed is not null ? $"[{Feed.Label}] {Text}" : Text;
}

/// <summary>Réglages d'affichage communs à toutes les lignes.</summary>
internal sealed record TailStyle(IReadOnlyList<string> Highlights, TailPattern? Search, bool Colors, bool Prefixes, bool Wrap);

/// <summary>Ligne affichée : ligne reçue (retenue par le filtre ou de contexte) ou séparateur « -- ».</summary>
internal sealed class TailRow(TailLine? line, TailShownKind kind, TailStyle style)
{
    public TailLine? Line { get; } = line;

    public TailShownKind Kind { get; } = kind;

    public TailStyle Style { get; } = style;

    /// <summary>Fond de la ligne : rose pâle pour une ligne d'alerte.</summary>
    public IBrush Background => Kind == TailShownKind.Line && Line?.IsAlert == true ? TailBrushes.Alert : Brushes.Transparent;

    public bool IsText => Kind != TailShownKind.Separator && Line is { IsMarker: false };

    public string Text => Kind == TailShownKind.Separator ? "--" : Line!.Format(Style.Prefixes);
}

/// <summary>
/// Lignes affichées. Les lignes reçues ensemble sont ajoutées en une seule notification (la liste d'Avalonia accepte
/// les ajouts groupés) : un fichier bavard n'en envoie pas une par ligne.
/// </summary>
internal sealed class TailRowList : ObservableCollection<TailRow>
{
    public TailRowList()
    {
    }

    public TailRowList(IEnumerable<TailRow> rows)
        : base(rows)
    {
    }

    public void AddRange(IReadOnlyList<TailRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count == 1)
        {
            Add(rows[0]);
            return;
        }

        CheckReentrancy();
        int start = Count;
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)rows.ToList(), start));
    }
}

/// <remarks>
/// Les couleurs suivent le thème clair ou sombre de l'application (<see cref="Palette.IsDark"/>). Le contraste élevé de
/// Windows (texte et surlignage aux couleurs du système, dans ZillaTerm.App) n'a pas d'équivalent ici : l'interface
/// Avalonia n'a que les thèmes clair et sombre.
/// </remarks>
internal static class TailBrushes
{
    private static readonly IBrush ErrorColor = Frozen(0xC6, 0x28, 0x28);
    private static readonly IBrush WarningColor = Frozen(0xB2, 0x6A, 0x00);
    private static readonly IBrush MutedColor = Frozen(0x6B, 0x77, 0x85);
    private static readonly IBrush ContextColor = Frozen(0x6B, 0x77, 0x85);
    private static readonly IBrush MarkerColor = Frozen(0x3F, 0x51, 0xB5);
    private static readonly IBrush HighlightColor = Frozen(0xFF, 0xF1, 0x76);
    private static readonly IBrush MatchColor = Frozen(0xFF, 0xB7, 0x4D);
    private static readonly IBrush AlertColor = Frozen(0xFD, 0xEC, 0xEA);

    // Thème sombre : textes éclaircis, surlignages foncés sous un texte clair (4,5:1 au moins).
    private static readonly IBrush DarkErrorColor = Frozen(0xFF, 0x9A, 0x90);
    private static readonly IBrush DarkWarningColor = Frozen(0xF0, 0xC0, 0x60);
    private static readonly IBrush DarkMutedColor = Frozen(0xA3, 0xAD, 0xB8);
    private static readonly IBrush DarkContextColor = Frozen(0x8E, 0x99, 0xA5);
    private static readonly IBrush DarkMarkerColor = Frozen(0xA5, 0xB4, 0xFC);
    private static readonly IBrush DarkHighlightColor = Frozen(0x5C, 0x4D, 0x00);
    private static readonly IBrush DarkMatchColor = Frozen(0x7A, 0x41, 0x00);
    private static readonly IBrush DarkAlertColor = Frozen(0x4A, 0x22, 0x26);

    /// <summary>Couleurs des fichiers de la vue combinée (ni rouge ni orange, réservés aux niveaux).</summary>
    private static readonly IBrush[] SourceColors =
    [
        Frozen(0x15, 0x65, 0xC0), Frozen(0x2E, 0x7D, 0x32), Frozen(0x6A, 0x1B, 0x9A), Frozen(0x00, 0x83, 0x8F),
        Frozen(0xAD, 0x14, 0x57), Frozen(0x4E, 0x34, 0x2E), Frozen(0x28, 0x35, 0x93), Frozen(0x55, 0x8B, 0x2F),
    ];

    private static readonly IBrush[] DarkSourceColors =
    [
        Frozen(0x5A, 0xA9, 0xF0), Frozen(0x6C, 0xC0, 0x70), Frozen(0xC0, 0x8A, 0xE0), Frozen(0x4D, 0xC6, 0xD0),
        Frozen(0xF0, 0x7A, 0xA8), Frozen(0xC8, 0xA0, 0x8A), Frozen(0x8C, 0x9E, 0xFF), Frozen(0xA5, 0xD4, 0x6A),
    ];

    private static bool Dark => Palette.IsDark;

    public static IBrush Error => Dark ? DarkErrorColor : ErrorColor;

    public static IBrush Warning => Dark ? DarkWarningColor : WarningColor;

    public static IBrush Muted => Dark ? DarkMutedColor : MutedColor;

    public static IBrush Context => Dark ? DarkContextColor : ContextColor;

    public static IBrush Marker => Dark ? DarkMarkerColor : MarkerColor;

    public static IBrush Highlight => Dark ? DarkHighlightColor : HighlightColor;

    public static IBrush Match => Dark ? DarkMatchColor : MatchColor;

    public static IBrush Alert => Dark ? DarkAlertColor : AlertColor;

    public static IBrush Source(int index) => (Dark ? DarkSourceColors : SourceColors)[index % SourceColors.Length];

    public static IBrush? Level(TailLevel level) => level switch
    {
        TailLevel.Error => Error,
        TailLevel.Warning => Warning,
        _ => null,
    };

    private static ImmutableSolidColorBrush Frozen(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}

/// <summary>
/// Contenu d'un <see cref="TextBlock"/> de la liste des lignes : préfixe coloré du fichier, couleur du niveau, mots
/// surlignés et résultats de recherche. Construit à l'affichage seulement (liste virtualisée). Public pour le XAML ;
/// la valeur est une ligne affichée (type interne).
/// </summary>
public static class TailRowText
{
    /// <summary>
    /// Au-delà, la ligne est coupée à l'affichage ; la copie et l'enregistrement en ont davantage (jusqu'à
    /// <see cref="TailLineSplitter.MaxLineLength"/> caractères).
    /// </summary>
    public const int MaxShown = 4_000;

    public static readonly AttachedProperty<object?> RowProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, object?>("Row", typeof(TailRowText));

    static TailRowText() => RowProperty.Changed.AddClassHandler<TextBlock>((block, e) => Build(block, e.NewValue as TailRow));

    public static object? GetRow(TextBlock element) => element.GetValue(RowProperty);

    public static void SetRow(TextBlock element, object? value) => element.SetValue(RowProperty, value);

    internal static void Build(TextBlock block, TailRow? row)
    {
        // Le bloc est réutilisé d'une ligne à l'autre (virtualisation) : tout est remis à zéro.
        block.Inlines?.Clear();
        block.Text = null;
        block.ClearValue(TextBlock.ForegroundProperty);
        if (row is null)
        {
            return;
        }

        var style = row.Style;
        block.TextWrapping = style.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (row.Kind == TailShownKind.Separator)
        {
            block.Foreground = TailBrushes.Context;
            block.Text = "--";
            return;
        }

        var line = row.Line!;
        var inlines = new InlineCollection();
        if (style.Prefixes && line.Feed is { } feed)
        {
            inlines.Add(new Run($"[{feed.Label}] ") { Foreground = feed.Brush, FontWeight = FontWeight.SemiBold });
        }

        if (line.IsMarker)
        {
            inlines.Add(new Run(line.Text) { Foreground = TailBrushes.Marker, FontStyle = FontStyle.Italic });
            block.Inlines = inlines;
            return;
        }

        if ((row.Kind == TailShownKind.Context ? TailBrushes.Context : style.Colors ? TailBrushes.Level(line.Level) : null) is { } foreground)
        {
            block.Foreground = foreground;
        }

        var text = line.Text.Length > MaxShown ? line.Text[..MaxShown] : line.Text;
        IReadOnlyList<(int Start, int Length)> highlights = style.Highlights.Count > 0 ? TailText.FindTerms(text, style.Highlights) : [];
        IReadOnlyList<(int Start, int Length)> matches = style.Search?.Find(text) ?? [];
        bool cut = text.Length < line.Text.Length;
        if (highlights.Count == 0 && matches.Count == 0 && inlines.Count == 0 && !cut)
        {
            // Ligne simple (le cas courant) : texte seul, sans morceaux.
            block.Text = text;
            return;
        }

        if (highlights.Count == 0 && matches.Count == 0)
        {
            inlines.Add(new Run(text));
        }
        else
        {
            foreach (var segment in TailText.Segments(text.Length, highlights, matches))
            {
                var run = new Run(text.Substring(segment.Start, segment.Length));
                if (segment.Match)
                {
                    run.Background = TailBrushes.Match;
                }
                else if (segment.Highlight)
                {
                    run.Background = TailBrushes.Highlight;
                }

                inlines.Add(run);
            }
        }

        if (cut)
        {
            inlines.Add(new Run(" …") { Foreground = TailBrushes.Muted });
        }

        block.Inlines = inlines;
    }
}
