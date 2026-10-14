using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Terminal;

/// <summary>
/// Affichage d'un <see cref="TerminalEmulator"/> et saisie clavier :
/// sélection = copie, clic droit = menu (ou collage, option des Paramètres), molette = historique.
/// </summary>
/// <remarks>
/// Le texte est dessiné par segments de cases de même style, à pas fixe : un glyphe par case (deux pour un caractère
/// large), sans mise en forme du texte à chaque image. Un caractère absent de la police à espacement fixe (chinois,
/// emoji…) vient d'une police du système qui l'a ; un caractère accompagné de marques combinantes (accent décomposé,
/// emoji composé) est mis en forme à part. Les couleurs sont celles de la palette du terminal, pas du thème de l'interface.
/// </remarks>
public sealed class TerminalView : Control
{
    private const double Padding = 4;

    // Police à espacement fixe de Themes/Controls.axaml (MonospaceFont), si la ressource n'est pas trouvée.
    private static readonly FontFamily DefaultFamily = new("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Menlo, monospace");

    // Emoji en couleur : police d'emoji de Windows, de macOS, des distributions Linux.
    private static readonly FontFamily EmojiFamily = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");

    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = [];

    // Glyphe de chaque caractère déjà rencontré (normal, gras, emoji sur deux cases) : la police de secours n'est
    // cherchée qu'une fois.
    private readonly Dictionary<int, Glyph>[] _glyphs = [new(), new(), new()];

    // Caractères à marques combinantes déjà mis en forme : texte, style (normal, gras, emoji) et couleur.
    private readonly Dictionary<(string Text, int Style, IBrush Brush), TextLayout> _clusters = [];

    // Tampons d'un segment de texte, réutilisés : la forme dessinée en est copiée au moment du dessin.
    private GlyphInfo[] _glyphBuffer = new GlyphInfo[256];
    private char[] _charBuffer = new char[512];
    private int[] _columnBuffer = new int[256];

    private readonly InputMethodClient _inputMethod;
    private FontFamily _family = DefaultFamily;
    private Typeface _regular;
    private Typeface _bold;
    private GlyphTypeface _regularFace = null!;
    private GlyphTypeface _boldFace = null!;

    // Police d'emoji en couleur du système, null s'il n'en a pas.
    private GlyphTypeface? _emojiFace;
    private double _fontSize = TerminalAppearance.FontSize;

    // Taille par défaut des Paramètres déjà appliquée à ce terminal.
    private double _defaultFontSize = TerminalAppearance.FontSize;
    private TerminalTheme _theme = TerminalAppearance.Theme;
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _currentMatch = -1;

    // Première ligne de l'écran quand les occurrences ont été trouvées : elles suivent leurs lignes jusqu'au calcul suivant.
    private long _matchesScreenLine;
    private TerminalEmulator? _emulator;

    // Position dans l'historique et sélection, ancrées sur les lignes : elles ne glissent pas quand la sortie continue.
    private TerminalViewport? _viewport;
    private double _cellWidth;
    private double _cellHeight;

    // Du haut d'une case à la ligne de base du texte.
    private double _baseline;
    private bool _selecting;

    // Molette : reste des pas plus petits qu'une ligne (pavé tactile de précision, molette libre), pour l'historique et
    // pour la taille de police (Ctrl), gardé jusqu'aux événements suivants dans le même sens.
    private int _wheelDelta;
    private int _zoomDelta;

    // Sélection dessinée pendant un rendu, début avant fin ; null sans sélection visible.
    private ((int Row, int Column) From, (int Row, int Column) To)? _shownSelection;

    // Fenêtre où le terminal est affiché : Alt+touche y est traité après ses touches d'accès (voir OnUnhandledKeyDown).
    private TopLevel? _topLevel;

    // Échelle de l'écran (pixels par unité), pour caler les fonds sur les pixels.
    private double _scaling = 1;

    public TerminalView()
    {
        Focusable = true;
        FocusAdorner = null;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        // Nom pour les lecteurs d'écran (le XAML de la session peut en donner un autre).
        AutomationProperties.SetName(this, Strings.SettingsPageTerminal);
        _inputMethod = new InputMethodClient(this);
        if (Application.Current?.TryGetResource("MonospaceFont", null, out var font) == true && font is FontFamily family)
        {
            _family = family;
        }

        LoadFonts();
        MeasureCell();
        // Saisie de texte par une méthode de saisie (chinois, japonais, coréen) : sa fenêtre suit le curseur du terminal.
        TextInputMethodClientRequested += (_, e) => e.Client = _inputMethod;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Menu du clic droit ouvert par le terminal lui-même (touche Menu comprise) : la demande de menu du système
        // (relâchement de la touche Menu, Maj+F10) n'en ouvre pas d'autre chez les éléments parents.
        ContextRequested += (_, e) => e.Handled = true;
    }

    /// <summary>Palette et taille des Paramètres : suivies tant que le terminal est affiché (il peut changer de place).</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Rattrapées à son retour (onglet en arrière-plan, déplacé dans une fenêtre séparée ou la vue parallèle).
        TerminalAppearance.Changed += OnAppearanceChangedElsewhere;
        OnAppearanceChanged();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        TerminalAppearance.Changed -= OnAppearanceChangedElsewhere;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnUnhandledKeyDown, RoutingStrategies.Bubble);
        if (this.TryFindResource("MonospaceFont", out var font) && font is FontFamily family && !family.Equals(_family))
        {
            _family = family;
            LoadFonts();
            MeasureCell();
            FitToSize();
            InvalidateMeasure();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel?.RemoveHandler(KeyDownEvent, OnUnhandledKeyDown);
        _topLevel = null;
    }

    /// <summary>Réglage changé depuis un autre fil que celui du terminal : appliqué sur le fil du terminal.</summary>
    private void OnAppearanceChangedElsewhere()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnAppearanceChanged();
        }
        else
        {
            Dispatcher.UIThread.Post(OnAppearanceChanged);
        }
    }

    /// <summary>Ctrl+Maj+F : recherche dans le terminal.</summary>
    public event Action? SearchRequested;

    /// <summary>Ctrl+Maj+S : enregistrer le contenu du terminal.</summary>
    public event Action? SaveRequested;

    /// <summary>Historique effacé depuis le menu : les occurrences d'une recherche sont à recalculer.</summary>
    public event Action? ScrollbackCleared;

    /// <summary>Actions de la session (reconnecter, dupliquer…) ajoutées à la fin du menu du clic droit, à chaque ouverture.</summary>
    public Action<ItemCollection>? ExtraMenuItems { get; set; }

    /// <summary>Palette du terminal (même nom qu'en WPF ; le thème de contrôle d'Avalonia reste <see cref="StyledElement.ThemeProperty"/>).</summary>
    public new TerminalTheme Theme => _theme;

    public double TerminalFontSize => _fontSize;

    private void OnAppearanceChanged()
    {
        _theme = TerminalAppearance.Theme;
        // Nouvelle taille par défaut : appliquée ; sinon la taille choisie pour ce terminal (Ctrl+molette) reste.
        if (_defaultFontSize != TerminalAppearance.FontSize)
        {
            _defaultFontSize = TerminalAppearance.FontSize;
            SetFontSize(_defaultFontSize);
        }

        InvalidateVisual();
    }

    /// <summary>Taille de police de ce terminal (Ctrl+molette) ; le nombre de lignes et colonnes suit.</summary>
    public void SetFontSize(double size)
    {
        size = Math.Clamp(size, TerminalAppearance.MinFontSize, TerminalAppearance.MaxFontSize);
        if (size == _fontSize)
        {
            return;
        }

        _fontSize = size;
        MeasureCell();
        FitToSize();
        // Taille imposée : la place demandée suit la police.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Police à espacement fixe, normale et grasse ; les glyphes déjà cherchés sont oubliés.</summary>
    private void LoadFonts()
    {
        _regular = new Typeface(_family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
        _bold = new Typeface(_family, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal);
        _regularFace = Face(_regular);
        _boldFace = Face(_bold);
        _emojiFace = FontManager.Current.TryGetGlyphTypeface(new Typeface(EmojiFamily), out var emoji) && emoji.CharacterToGlyphMap.ContainsGlyph(0x1F600)
            ? emoji
            : null;
        foreach (var cache in _glyphs)
        {
            cache.Clear();
        }

        static GlyphTypeface Face(Typeface typeface) =>
            FontManager.Current.TryGetGlyphTypeface(typeface, out var face) ? face : typeface.GlyphTypeface;
    }

    private void MeasureCell()
    {
        var metrics = _regularFace.Metrics;
        double scale = _fontSize / metrics.DesignEmHeight;
        _cellWidth = FindGlyph('M', bold: false).Advance * _fontSize;
        if (_cellWidth <= 0)
        {
            _cellWidth = _fontSize * 0.6;
        }

        _cellHeight = Math.Ceiling(metrics.LineSpacing * scale);
        _baseline = -metrics.Ascent * scale;
        ClearClusters();
    }

    /// <summary>Occurrences de la recherche, surlignées ; la courante est amenée à l'écran.</summary>
    /// <param name="scroll">Faux quand les occurrences sont recalculées après de nouvelles lignes : la vue ne bouge pas.</param>
    public void ShowMatches(IReadOnlyList<TerminalMatch> matches, int current, bool scroll = true)
    {
        if (!ReferenceEquals(matches, _matches))
        {
            _matchesScreenLine = _emulator?.FirstScreenLine ?? 0;
        }

        _matches = matches;
        _currentMatch = current;
        if (scroll && _viewport is { } viewport && current >= 0 && current < matches.Count)
        {
            // Ligne visible : en haut de l'historique si besoin, vers le milieu de l'écran sinon.
            int row = matches[current].Row - MatchesShift;
            int top = -viewport.ScrollOffset;
            if (row < top || row >= top + viewport.Emulator.Rows)
            {
                viewport.ScrollTo(-row + (viewport.Emulator.Rows / 2));
            }
        }

        InvalidateVisual();
    }

    /// <summary>Lignes défilées depuis le calcul des occurrences.</summary>
    private int MatchesShift => _emulator is null ? 0 : (int)Math.Min(_emulator.FirstScreenLine - _matchesScreenLine, int.MaxValue / 2);

    public void ClearMatches()
    {
        _matches = [];
        _currentMatch = -1;
        InvalidateVisual();
    }

    /// <summary>Saisie à envoyer au serveur (frappe clavier, collage, molette), encodée par le destinataire.</summary>
    /// <remarks>
    /// Un collage arrive après la lecture (asynchrone) du presse-papiers ; un collage de plusieurs lignes est à confirmer
    /// par le destinataire (<see cref="TerminalInput.IsMultiLinePaste"/>), comme en WPF.
    /// </remarks>
    public event Action<TerminalInput>? Input;

    /// <summary>Nouvelle taille en caractères (colonnes, lignes) après redimensionnement.</summary>
    public event Action<int, int>? TerminalResized;

    public TerminalEmulator? Emulator
    {
        get => _emulator;
        set
        {
            _emulator = value;
            _viewport = value is null ? null : new TerminalViewport(value);
            FitToSize();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Taille imposée par l'émulateur (session partagée regardée : celle du terminal de la personne qui partage) : le
    /// terminal ne se redimensionne pas à la place qu'il a, il demande celle de ses cases (à mettre dans une Viewbox).
    /// </summary>
    public bool FixedSize { get; set; }

    protected override Size MeasureOverride(Size availableSize) =>
        FixedSize && _emulator is { } emulator
            ? new Size((emulator.Columns * _cellWidth) + (2 * Padding), (emulator.Rows * _cellHeight) + (2 * Padding))
            : base.MeasureOverride(availableSize);

    /// <summary>Lignes remontées dans l'historique (0 = en bas).</summary>
    private int ScrollOffset => _viewport?.ScrollOffset ?? 0;

    public (int Columns, int Rows) SizeInCells => (
        Math.Max(2, (int)((Bounds.Width - 2 * Padding) / _cellWidth)),
        Math.Max(2, (int)((Bounds.Height - 2 * Padding) / _cellHeight)));

    /// <summary>
    /// À appeler après avoir alimenté l'émulateur. Une vue remontée reste sur les mêmes lignes, la sélection sur le même
    /// texte (<see cref="TerminalViewport"/>).
    /// </summary>
    public void Refresh() => InvalidateVisual();

    // ===================== Rendu =====================

    /// <summary>Position dans l'historique : lignes au-dessus de l'écran, lignes remontées (0 = en bas), lignes affichées.</summary>
    public (int Scrollback, int Offset, int Rows) ScrollState =>
        _emulator is null ? (0, 0, 0) : (_emulator.ScrollbackCount, ScrollOffset, _emulator.Rows);

    /// <summary>La position dans l'historique ou sa taille a changé (barre de défilement, « Revenir en bas »).</summary>
    public event Action? ScrollStateChanged;

    private (int, int, int) _lastScrollState;

    // Case du curseur au dernier rendu, pour la fenêtre de la méthode de saisie.
    private Rect _cursorRect;

    /// <summary>Affiche l'historique à <paramref name="offset"/> lignes du bas (0 = la fin, comme à la saisie).</summary>
    public void ScrollTo(int offset)
    {
        _viewport?.ScrollTo(offset);
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        _scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        dc.FillRectangle(Brush(_theme.Background), new Rect(Bounds.Size));
        var emulator = _emulator;
        if (emulator is null)
        {
            return;
        }

        // Chaque changement (sortie, défilement, taille) passe par un rendu : la barre de défilement suit.
        if (ScrollState != _lastScrollState)
        {
            _lastScrollState = ScrollState;
            Dispatcher.UIThread.Post(() => ScrollStateChanged?.Invoke());
        }

        _shownSelection = null;
        if (_viewport?.Selection is { } selection && (selection.Start != selection.End || _selecting))
        {
            var (from, to) = (selection.Start, selection.End);
            if (from.Row > to.Row || (from.Row == to.Row && from.Column > to.Column))
            {
                (from, to) = (to, from);
            }

            _shownSelection = (from, to);
        }

        int offset = ScrollOffset;
        int top = -offset;
        for (int screenRow = 0; screenRow < emulator.Rows; screenRow++)
        {
            int row = top + screenRow;
            if (row < -emulator.ScrollbackCount || row >= emulator.Rows)
            {
                continue;
            }

            DrawMatches(dc, emulator, row, screenRow);
            DrawLine(dc, emulator, row, screenRow);
        }

        // Curseur : pavé plein si le terminal a le focus, contour sinon ; sur un caractère large, ses deux cases.
        var cursor = new Rect(Padding + emulator.CursorColumn * _cellWidth, Y(emulator.CursorRow), _cellWidth, _cellHeight);
        if (emulator.CursorVisible && offset == 0)
        {
            var line = emulator.GetLine(emulator.CursorRow);
            int column = Math.Min(emulator.CursorColumn, line.Length - 1);
            int cells = WidthAt(line, column, line.Length);
            cursor = new Rect(X(column), Y(emulator.CursorRow), X(column + cells) - X(column), Y(emulator.CursorRow + 1) - Y(emulator.CursorRow));
            if (IsFocused)
            {
                dc.FillRectangle(Brush(_theme.Cursor), cursor);
                var cell = line[column];
                if (cell.CodePoint != ' ' && !cell.IsWideTail)
                {
                    DrawText(dc, emulator, line, column, column + 1, line.Length, bold: false, Brush(_theme.Background), cursor.Y);
                }
            }
            else
            {
                dc.DrawRectangle(null, new ImmutablePen(Brush(_theme.Cursor), 1),
                    new Rect(cursor.X + 0.5, cursor.Y + 0.5, cursor.Width - 1, cursor.Height - 1));
            }
        }

        if (cursor != _cursorRect)
        {
            _cursorRect = cursor;
            Dispatcher.UIThread.Post(_inputMethod.MoveCursor);
        }
    }

    /// <summary>Bord gauche de la colonne <paramref name="column"/>, calé sur les pixels de l'écran (fonds sans liseré).</summary>
    private double X(int column) => Snap(Padding + column * _cellWidth);

    /// <summary>Haut de la ligne affichée <paramref name="screenRow"/>.</summary>
    private double Y(int screenRow) => Snap(Padding + screenRow * _cellHeight);

    private double Snap(double value) => Math.Round(value * _scaling) / _scaling;

    private void DrawLine(DrawingContext dc, TerminalEmulator emulator, int row, int screenRow)
    {
        double y = Y(screenRow);
        double height = Y(screenRow + 1) - y;
        var line = emulator.GetLine(row);
        int columns = Math.Min(line.Length, emulator.Columns);
        int col = 0;
        while (col < columns)
        {
            var (fg, bg, bold, underline) = Colors(line[col], IsSelected(line, row, col));
            int start = col;
            while (col < columns && Colors(line[col], IsSelected(line, row, col)) == (fg, bg, bold, underline))
            {
                col++;
            }

            double x = X(start);
            double width = X(col) - x;
            if (bg != _theme.Background)
            {
                dc.FillRectangle(Brush(bg), new Rect(x, y, width, height));
            }

            var brush = Brush(fg);
            // Texte de la couleur du fond (invisible) : rien à dessiner.
            if (fg != bg)
            {
                DrawText(dc, emulator, line, start, col, columns, bold, brush, y);
            }

            if (underline)
            {
                dc.FillRectangle(brush, new Rect(x, y + height - 2, width, 1));
            }
        }
    }

    /// <summary>Cases occupées par le caractère de la case <paramref name="col"/> : 2 pour un caractère large visible en entier.</summary>
    private static int WidthAt(Cell[] line, int col, int columns) => col + 1 < columns && line[col + 1].IsWideTail ? 2 : 1;

    /// <summary>
    /// Texte des cases <paramref name="start"/> à <paramref name="end"/> (exclue), de même style : un segment par police,
    /// chaque glyphe à sa case (un caractère large centré sur ses deux cases) pour garder l'alignement même si la police
    /// de secours a une autre largeur. Les espaces de début et de fin ne sont pas dessinés.
    /// </summary>
    private void DrawText(DrawingContext dc, TerminalEmulator emulator, Cell[] line, int start, int end, int columns, bool bold, IBrush brush,
        double y)
    {
        GlyphTypeface? face = null;
        double size = _fontSize;
        int count = 0;
        int chars = 0;

        // Dernier glyphe qui n'est pas un espace : le segment s'arrête là.
        int lastInk = 0;
        for (int c = start; c < end; c++)
        {
            int codePoint = line[c].CodePoint;
            if (codePoint == Cell.WideTail)
            {
                continue;
            }

            bool blank = codePoint is ' ' or 0;
            if (blank && count == 0)
            {
                continue;
            }

            int cells = WidthAt(line, c, columns);
            if (codePoint > 0x10FFFF)
            {
                // Caractère et ses marques combinantes (accent décomposé, emoji composé) : mis en forme à part.
                Flush();
                DrawCluster(dc, emulator.CellText(line[c]), bold, brush, c, cells, y);
                continue;
            }

            var glyph = blank ? FindGlyph(' ', bold) : FindGlyph(codePoint, bold, emoji: cells > 1 && IsEmoji(codePoint));
            double room = cells * _cellWidth;

            // Caractère large dont le glyphe déborde de ses deux cases (emoji d'une police de secours) : réduit pour ne pas
            // empiéter sur le voisin.
            double glyphSize = cells > 1 && glyph.Advance * _fontSize > room ? room / glyph.Advance : _fontSize;
            if (!ReferenceEquals(glyph.Face, face) || glyphSize != size)
            {
                Flush();
                face = glyph.Face;
                size = glyphSize;
            }

            if (count == _glyphBuffer.Length)
            {
                Array.Resize(ref _glyphBuffer, count * 2);
                Array.Resize(ref _columnBuffer, count * 2);
            }

            if (chars + 2 > _charBuffer.Length)
            {
                Array.Resize(ref _charBuffer, _charBuffer.Length * 2);
            }

            // Un caractère large plus étroit que ses deux cases (chinois) est centré.
            double shift = cells > 1 ? Math.Max(0, room - (glyph.Advance * glyphSize)) / 2 : 0;
            _glyphBuffer[count] = new GlyphInfo(glyph.Index, chars, 0, new Vector(shift, 0));
            _columnBuffer[count] = c;
            chars += (Rune.TryCreate(blank ? ' ' : codePoint, out var rune) ? rune : Rune.ReplacementChar).EncodeToUtf16(_charBuffer.AsSpan(chars));
            count++;
            if (!blank)
            {
                lastInk = count;
            }
        }

        Flush();

        void Flush()
        {
            if (lastInk > 0 && face is not null)
            {
                // Avance de chaque glyphe : jusqu'à la case du suivant (une case, deux pour un caractère large).
                for (int i = 0; i < lastInk; i++)
                {
                    int next = i + 1 < lastInk ? _columnBuffer[i + 1] : _columnBuffer[i] + WidthAt(line, _columnBuffer[i], columns);
                    var info = _glyphBuffer[i];
                    _glyphBuffer[i] = new GlyphInfo(info.GlyphIndex, info.GlyphCluster, (next - _columnBuffer[i]) * _cellWidth, info.GlyphOffset);
                }

                int length = lastInk < count ? _glyphBuffer[lastInk].GlyphCluster : chars;
                using var run = new GlyphRun(face, size, _charBuffer.AsMemory(0, length),
                    new ArraySegment<GlyphInfo>(_glyphBuffer, 0, lastInk), new Point(Padding + _columnBuffer[0] * _cellWidth, y + _baseline));
                dc.DrawGlyphRun(brush, run);
            }

            count = 0;
            chars = 0;
            lastInk = 0;
        }
    }

    /// <summary>
    /// Caractère accompagné de marques combinantes, mis en forme (polices de secours comprises) et posé sur la ligne de base
    /// des autres ; resserré s'il déborde de ses deux cases. Un emoji composé (couleur de peau, ❤️, famille) est dessiné
    /// en couleur, dans sa case.
    /// </summary>
    private void DrawCluster(DrawingContext dc, string text, bool bold, IBrush brush, int column, int cells, double y)
    {
        Rune.DecodeFromUtf16(text, out var first, out _);
        bool emoji = _emojiFace is not null && (IsEmoji(first.Value) || text.Contains('\uFE0F') || text.Contains('\u200D'));
        int style = emoji ? 2 : bold ? 1 : 0;
        if (!_clusters.TryGetValue((text, style, brush), out var layout))
        {
            // Mis en forme une fois (texte, style, couleur) : un écran plein d'accents décomposés reste fluide.
            if (_clusters.Count >= 1024)
            {
                ClearClusters();
            }

            layout = new TextLayout(text, emoji ? new Typeface(EmojiFamily) : bold ? _bold : _regular, _fontSize, brush);
            _clusters[(text, style, brush)] = layout;
        }

        var origin = new Point(Padding + column * _cellWidth, y + _baseline - layout.Baseline);
        double room = cells * _cellWidth;
        double width = layout.WidthIncludingTrailingWhitespace;
        if ((cells > 1 || emoji) && width > room)
        {
            using (dc.PushTransform(Matrix.CreateTranslation(-origin.X, 0) * Matrix.CreateScale(room / width, 1) * Matrix.CreateTranslation(origin.X, 0)))
            {
                layout.Draw(dc, origin);
            }
        }
        else
        {
            layout.Draw(dc, origin);
        }
    }

    private void ClearClusters()
    {
        foreach (var layout in _clusters.Values)
        {
            layout.Dispose();
        }

        _clusters.Clear();
    }

    /// <summary>Glyphe d'un caractère : police du terminal, sinon une police du système qui l'a ; avance en em.</summary>
    private readonly record struct Glyph(GlyphTypeface Face, ushort Index, double Advance);

    /// <summary>Blocs des emoji et symboles qui en ont un (horloges, météo, coches…).</summary>
    private static bool IsEmoji(int codePoint) =>
        codePoint is >= 0x1F000 and <= 0x1FAFF or >= 0x2300 and <= 0x23FF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF;

    /// <param name="emoji">Emoji sur deux cases : en couleur si le système a une police d'emoji.</param>
    private Glyph FindGlyph(int codePoint, bool bold, bool emoji = false)
    {
        emoji &= _emojiFace is not null;
        var cache = _glyphs[emoji ? 2 : bold ? 1 : 0];
        if (cache.TryGetValue(codePoint, out var glyph))
        {
            return glyph;
        }

        var face = bold ? _boldFace : _regularFace;
        ushort index = 0;
        if (emoji && _emojiFace!.CharacterToGlyphMap.TryGetGlyph(codePoint, out ushort emojiIndex) && emojiIndex != 0)
        {
            face = _emojiFace;
            index = emojiIndex;
        }
        else if (!face.CharacterToGlyphMap.TryGetGlyph(codePoint, out index) || index == 0)
        {
            // Absent de la police (chinois, emoji…) : police du système qui l'a ; aucune : le glyphe « manquant ».
            if (FontManager.Current.TryMatchCharacter(codePoint, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal, FontStretch.Normal,
                    null, CultureInfo.CurrentUICulture, out var typeface)
                && FontManager.Current.TryGetGlyphTypeface(typeface, out var fallback)
                && fallback.CharacterToGlyphMap.TryGetGlyph(codePoint, out ushort fallbackIndex) && fallbackIndex != 0)
            {
                face = fallback;
                index = fallbackIndex;
            }
        }

        face.TryGetHorizontalGlyphAdvance(index, out ushort advance);
        glyph = new Glyph(face, index, (double)advance / face.Metrics.DesignEmHeight);

        // Texte très varié (chinois) : le cache est vidé au-delà de 16384 caractères.
        if (cache.Count >= 16384)
        {
            cache.Clear();
        }

        cache[codePoint] = glyph;
        return glyph;
    }

    /// <summary>
    /// Fond des occurrences de la recherche (jaune ; orange pour la courante) sur la ligne <paramref name="row"/>. Une
    /// occurrence coupée par le repli d'une ligne longue continue au début des lignes suivantes.
    /// </summary>
    private void DrawMatches(DrawingContext dc, TerminalEmulator emulator, int row, int screenRow)
    {
        double y = Y(screenRow);
        int shift = MatchesShift;
        for (int i = 0; i < _matches.Count; i++)
        {
            var match = _matches[i];

            // Ligne actuelle du début de l'occurrence ; chaque ligne traversée en prend au moins une case.
            int start = match.Row - shift;
            if (start > row || row - start >= match.Length)
            {
                continue;
            }

            int column = match.Column;
            int length = match.Length;
            for (int r = start; r < row && length > 0; r++)
            {
                length -= Math.Max(LineWidth(emulator, r) - column, 1);
                column = 0;
            }

            // Bornée au bord droit (occurrences calculées avant un rétrécissement du terminal).
            int cells = Math.Min(length, LineWidth(emulator, row) - column);
            if (cells > 0)
            {
                dc.FillRectangle(Brush(i == _currentMatch ? 0xFF9632u : 0xFFE066u),
                    new Rect(X(column), y, X(column + cells) - X(column), Y(screenRow + 1) - y));
            }
        }
    }

    /// <summary>Largeur de la ligne <paramref name="row"/> (celle de l'écran si elle n'est plus dans l'historique).</summary>
    private static int LineWidth(TerminalEmulator emulator, int row) =>
        row >= -emulator.ScrollbackCount && row < emulator.Rows ? emulator.GetLine(row).Length : emulator.Columns;

    private (uint Fg, uint Bg, bool Bold, bool Underline) Colors(Cell cell, bool selected)
    {
        bool bold = cell.Flags.HasFlag(CellFlags.Bold);
        uint fg = _theme.ToRgb(cell.Foreground, foreground: true, bold);
        uint bg = _theme.ToRgb(cell.Background, foreground: false);
        if (cell.Flags.HasFlag(CellFlags.Dim))
        {
            fg = Blend(fg, bg);
        }

        if (cell.Flags.HasFlag(CellFlags.Inverse))
        {
            (fg, bg) = (bg, fg);
        }

        if (cell.Flags.HasFlag(CellFlags.Hidden))
        {
            fg = bg;
        }

        if (selected)
        {
            (fg, bg) = (0x0C0C0C, 0x9CC7F2);
        }

        return (fg, bg, bold, cell.Flags.HasFlag(CellFlags.Underline));
    }

    private static uint Blend(uint a, uint b) =>
        (((a >> 16 & 0xFF) + (b >> 16 & 0xFF)) / 2 << 16) | (((a >> 8 & 0xFF) + (b >> 8 & 0xFF)) / 2 << 8) | ((a & 0xFF) + (b & 0xFF)) / 2;

    private IImmutableSolidColorBrush Brush(uint rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var brush))
        {
            // Couleurs 24 bits : une sortie qui en change sans cesse remplirait le cache, vidé au-delà de 4096 couleurs.
            if (_brushes.Count >= 4096)
            {
                _brushes.Clear();
            }

            brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushes[rgb] = brush;
        }

        return brush;
    }

    // ===================== Taille =====================

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        FitToSize();
    }

    /// <summary>
    /// Réajuste l'émulateur à la taille affichée (et le signale) : utile quand la session a imposé une autre taille, par
    /// exemple celle mesurée avant que le terminal ne change de place pendant la connexion.
    /// </summary>
    public void Refit() => FitToSize();

    private void FitToSize()
    {
        if (FixedSize || _emulator is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var (columns, rows) = SizeInCells;
        if (columns != _emulator.Columns || rows != _emulator.Rows)
        {
            _emulator.Resize(columns, rows);
            TerminalResized?.Invoke(columns, rows);
            InvalidateVisual();
        }
    }

    // ===================== Clavier =====================

    // Touches spéciales et raccourcis ici ; le texte tapé arrive par OnTextInput (dispositions, AltGr, touches mortes,
    // méthodes de saisie), jamais en double : une touche traitée ici ne produit pas de texte.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _emulator is null)
        {
            return;
        }

        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        var key = e.Key;

        // Raccourcis locaux (non envoyés au serveur).
        if (ctrl && shift && key == Key.F)
        {
            SearchRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && key == Key.S)
        {
            SaveRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && !alt && key is Key.D0 or Key.NumPad0)
        {
            // Taille de police par défaut (Ctrl+molette la change).
            SetFontSize(TerminalAppearance.FontSize);
            e.Handled = true;
            return;
        }

        // macOS : Cmd+C et Cmd+V, comme partout sur ce système (Ctrl+lettre y reste un caractère de contrôle).
        bool command = OperatingSystem.IsMacOS() && mods.HasFlag(KeyModifiers.Meta);
        if ((ctrl && shift && key == Key.C) || (command && key == Key.C))
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if ((ctrl && shift && key == Key.V) || (shift && key == Key.Insert) || (command && key == Key.V))
        {
            Paste();
            e.Handled = true;
            return;
        }

        if (command)
        {
            // Autres raccourcis Cmd (fenêtre, application) : rien n'est envoyé au serveur.
            e.Handled = true;
            return;
        }

        if (key == Key.Apps)
        {
            // Touche Menu du clavier : le menu du clic droit, au curseur, ouvert au relâchement (OnKeyUp).
            e.Handled = true;
            return;
        }

        if (shift && key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(key == Key.PageUp ? _emulator.Rows - 1 : -(_emulator.Rows - 1));
            e.Handled = true;
            return;
        }

        if (MapKey(key) is { } terminalKey)
        {
            // Alt+F4 & co. restent gérés par le système.
            if (alt && terminalKey is >= TerminalKey.F1 and <= TerminalKey.F12)
            {
                return;
            }

            Send(TerminalInput.Special(terminalKey, shift, alt, ctrl));
            e.Handled = true;
            return;
        }

        // Ctrl+lettre → caractère de contrôle. Ctrl+Alt = AltGr sous Windows (clavier français…) : laissé à la saisie de
        // texte. Ailleurs AltGr n'est pas Ctrl+Alt : Ctrl+Alt+lettre envoie Échap puis le caractère de contrôle.
        if (ctrl && (!alt || !OperatingSystem.IsWindows()) && ControlText(e) is { } control)
        {
            Send(TerminalInput.Typed(alt ? "\x1b" + control : control));
            e.Handled = true;
        }
    }

    /// <summary>
    /// Touche Menu du clavier : le menu du clic droit, au curseur. Ouvert au relâchement de la touche, qu'Avalonia
    /// traite comme une demande de fermeture d'un menu contextuel déjà ouvert.
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Apps && !e.Handled && _emulator is not null)
        {
            OpenMenu(atCursor: true);
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    /// <summary>
    /// Alt+touche, après les touches d'accès de la fenêtre (comme en WPF) : préfixe Échap (« Meta »). Sous macOS, Option
    /// compose des caractères ({, |, accents) : laissé à la saisie de texte.
    /// </summary>
    private void OnUnhandledKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Handled || !ReferenceEquals(e.Source, this) || _emulator is null || OperatingSystem.IsMacOS()
            || !mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
        {
            return;
        }

        if (MetaText(e) is { } text)
        {
            Send(TerminalInput.Typed("\x1b" + text));
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // Caractères de contrôle : ceux des touches, déjà envoyés par OnPreviewKeyDown.
        if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsControl))
        {
            Send(TerminalInput.Typed(e.Text));
        }

        e.Handled = true;
    }

    private static TerminalKey? MapKey(Key key) => key switch
    {
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    private static char? KeyChar(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('A' + (key - Key.A)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.Oem5 => '\\',
        _ => null,
    };

    /// <summary>Caractère de contrôle de Ctrl+touche (<see cref="TerminalKeys.Control"/>), sinon null.</summary>
    private static string? ControlText(KeyEventArgs e)
    {
        if (KeyChar(e.Key) is { } c)
        {
            return TerminalKeys.Control(c);
        }

        if (e.KeySymbol is not { Length: 1 } symbol)
        {
            return null;
        }

        // Disposition sans lettres latines (cyrillique, grec…) : la lettre à la même place sur un clavier QWERTY.
        if (symbol[0] > 0x7F && e.PhysicalKey is >= PhysicalKey.A and <= PhysicalKey.Z)
        {
            return TerminalKeys.Control((char)('A' + (e.PhysicalKey - PhysicalKey.A)));
        }

        // Autres symboles (Ctrl+@, Ctrl+^, Ctrl+_…) : d'après le caractère de la touche dans la disposition du clavier, ou
        // le caractère de contrôle que le système en a déjà tiré.
        return char.IsControl(symbol[0]) ? symbol : TerminalKeys.Control(symbol[0]);
    }

    /// <summary>Caractère d'Alt+touche (Maj comprise pour une lettre), sinon null.</summary>
    private static string? MetaText(KeyEventArgs e)
    {
        var text = e.KeySymbol is { Length: > 0 } symbol && !symbol.Any(char.IsControl) ? symbol : KeyChar(e.Key)?.ToString();
        if (text is null)
        {
            return null;
        }

        // Le système donne parfois la lettre de la touche en majuscule (Windows), quelle que soit Maj.
        return text.Length == 1 && char.IsLetter(text[0])
            ? (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? text.ToUpperInvariant() : text.ToLowerInvariant())
            : text;
    }

    private void Send(TerminalInput input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        if (_viewport is { } viewport && (viewport.ScrollOffset != 0 || viewport.Selection is not null))
        {
            viewport.ScrollTo(0);
            viewport.ClearSelection();
            InvalidateVisual();
        }

        Input?.Invoke(input);
    }

    // ===================== Souris : focus, historique, sélection, collage =====================

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_emulator is null)
        {
            return;
        }

        // En 120e de cran, comme sous Windows.
        int delta = (int)Math.Round(e.Delta.Y * 120);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+molette : taille de police de ce terminal, un point par cran.
            int steps = WheelSteps(ref _zoomDelta, delta, 120);
            if (steps != 0)
            {
                SetFontSize(_fontSize + steps);
            }

            e.Handled = true;
            return;
        }

        // Trois lignes par cran (120).
        int lines = WheelSteps(ref _wheelDelta, delta, 40);
        if (_emulator.IsAlternateScreen)
        {
            // vim, less... : la molette déplace dans le document comme les flèches.
            var key = lines > 0 ? TerminalKey.Up : TerminalKey.Down;
            for (int i = 0; i < Math.Abs(lines); i++)
            {
                Input?.Invoke(TerminalInput.Scrolled(key));
            }
        }
        else if (lines != 0)
        {
            ScrollBy(lines);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Pas entiers de <paramref name="step"/> dans le cumul des événements de molette : le reste attend les suivants (un
    /// pavé tactile envoie des pas de 10 à 30), et repart de zéro quand le sens change.
    /// </summary>
    private static int WheelSteps(ref int accumulated, int delta, int step)
    {
        if (Math.Sign(delta) != Math.Sign(accumulated))
        {
            accumulated = 0;
        }

        accumulated += delta;
        int steps = accumulated / step;
        accumulated %= step;
        return steps;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            // Menu ou collage au relâchement, comme en WPF.
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
        if (_emulator is null)
        {
            return;
        }

        var cell = CellAt(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            SelectWord(cell);
            CopySelection();
        }
        else
        {
            _viewport?.Select(cell, cell);
            _selecting = true;
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting && _emulator is not null)
        {
            _viewport?.ExtendSelection(CellAt(e.GetPosition(this)));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            EndSelection(e.Pointer);
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            Focus(NavigationMethod.Pointer);
            // Collage direct (option des Paramètres) : Maj+clic droit ouvre alors le menu.
            if (TerminalAppearance.RightClickPastes && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Paste();
            }
            else
            {
                OpenMenu(atCursor: false);
            }

            // Traité ici : pas de demande de menu (ContextRequested) aux éléments parents.
            e.Handled = true;
        }

        base.OnPointerReleased(e);
    }

    /// <summary>
    /// Capture de la souris perdue pendant une sélection (fenêtre ouverte par-dessus, menu) : le bouton sera relâché
    /// ailleurs, la sélection se termine ici. Sinon les mouvements de la souris l'étendraient encore, sans la copier.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndSelection(null);
    }

    /// <summary>Fin d'une sélection à la souris : copiée, ou retirée si elle est vide.</summary>
    private void EndSelection(IPointer? pointer)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        pointer?.Capture(null);
        if (_viewport?.HasSelection != true)
        {
            ClearSelection();
        }
        else
        {
            CopySelection();
        }

        InvalidateVisual();
    }

    /// <summary>Sélection à la souris en cours, bouton enfoncé (tests).</summary>
    internal bool IsSelecting => _selecting;

    // ===================== Menu du clic droit =====================

    /// <summary>
    /// Copier, coller, tout sélectionner, rechercher, enregistrer, effacer l'historique, taille de police, puis les
    /// actions de la session. Recréé à chaque ouverture pour refléter la sélection et le presse-papiers.
    /// </summary>
    /// <param name="atCursor">Sous le curseur du terminal (touche Menu) plutôt que sous la souris.</param>
    private void OpenMenu(bool atCursor) => _ = OpenMenuAsync(atCursor);

    private async Task OpenMenuAsync(bool atCursor)
    {
        if (await BuildMenuAsync(atCursor) is { } menu)
        {
            menu.Open(this);
        }
    }

    internal async Task<ContextMenu?> BuildMenuAsync(bool atCursor)
    {
        bool hasText = await ClipboardHasTextAsync();
        if (_emulator is null)
        {
            return null;
        }

        bool selection = _viewport?.HasSelection == true;
        var menu = new ContextMenu { PlacementTarget = this, Placement = atCursor ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.Pointer };
        if (atCursor)
        {
            // Sous le curseur ; historique remonté (curseur hors de la vue) : en bas du terminal.
            int row = Math.Min(_emulator.CursorRow + ScrollOffset, _emulator.Rows - 1);
            menu.PlacementRect = new Rect(Padding + (_emulator.CursorColumn * _cellWidth), Padding + (row * _cellHeight), _cellWidth, _cellHeight);
        }

        menu.Items.Add(MenuEntry(Strings.MenuTerminalCopy, CopySelection, Strings.ShortcutTerminalCopy, enabled: selection));
        menu.Items.Add(MenuEntry(Strings.MenuTerminalPaste, Paste, Strings.ShortcutTerminalPaste, enabled: hasText));
        var selectAll = MenuEntry(Strings.MenuTerminalSelectAll, SelectAll);
        ToolTip.SetTip(selectAll, Strings.MenuTerminalSelectAllTip);
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        if (SearchRequested is not null)
        {
            menu.Items.Add(MenuEntry(Strings.MenuTerminalSearch, () => SearchRequested?.Invoke(), Strings.ShortcutTerminalSearch,
                icon: "IconSearch"));
        }

        if (SaveRequested is not null)
        {
            var save = MenuEntry(Strings.MenuTerminalSave, () => SaveRequested?.Invoke(), Strings.ShortcutTerminalSave);
            ToolTip.SetTip(save, Strings.MenuTerminalSaveTip);
            menu.Items.Add(save);
        }

        var clear = MenuEntry(Strings.MenuTerminalClear, ClearScrollback, enabled: _emulator.ScrollbackCount > 0, icon: "IconDelete");
        ToolTip.SetTip(clear, Strings.MenuTerminalClearTip);
        ToolTip.SetShowOnDisabled(clear, true);
        menu.Items.Add(clear);
        var font = new MenuItem { Header = Strings.MenuTerminalFont };
        font.Items.Add(MenuEntry(Strings.MenuFontBigger, () => SetFontSize(_fontSize + 1), Strings.ShortcutFontBigger,
            enabled: _fontSize < TerminalAppearance.MaxFontSize));
        font.Items.Add(MenuEntry(Strings.MenuFontSmaller, () => SetFontSize(_fontSize - 1), Strings.ShortcutFontSmaller,
            enabled: _fontSize > TerminalAppearance.MinFontSize));
        font.Items.Add(MenuEntry(Strings.MenuFontDefault, () => SetFontSize(TerminalAppearance.FontSize), Strings.ShortcutFontDefault,
            enabled: _fontSize != TerminalAppearance.FontSize));
        menu.Items.Add(font);
        if (ExtraMenuItems is { } extra)
        {
            menu.Items.Add(new Separator());
            extra(menu.Items);
        }

        // Colonne des icônes comptée dans la largeur de chaque entrée dès la première mise en page : sans cela, une entrée
        // sans icône plus longue que les autres (« Enregistrer le contenu du terminal… ») est coupée à droite.
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Icon ??= new Control { Width = 16, Height = 16 };
        }

        AcceptLetterAccessKeys(menu);
        return menu;
    }

    /// <summary>
    /// Dans le menu ouvert, la lettre soulignée seule choisit l'entrée, comme en WPF (Avalonia demande Alt+lettre dans un
    /// menu contextuel) ; une entrée à sous-menu l'ouvre.
    /// </summary>
    private static void AcceptLetterAccessKeys(ContextMenu menu) => menu.AddHandler(KeyDownEvent, (_, e) =>
    {
        if (e.Handled || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None || e.KeySymbol is not { Length: 1 } symbol
            || !char.IsLetterOrDigit(symbol[0]))
        {
            return;
        }

        // Entrées du menu où se trouve la sélection (le menu lui-même, ou un sous-menu ouvert).
        var items = (e.Source as MenuItem)?.Parent is ItemsControl parent ? parent.Items : menu.Items;
        var item = items.OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled && AccessKey(i.Header) == char.ToUpperInvariant(symbol[0]));
        if (item is null)
        {
            return;
        }

        e.Handled = true;
        if (item.ItemCount > 0)
        {
            item.Open();
            Dispatcher.UIThread.Post(() => item.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEffectivelyEnabled)?.Focus(NavigationMethod.Directional));
        }
        else
        {
            menu.Close();
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
    });

    /// <summary>Lettre soulignée d'un intitulé (« _Copier » : C), en majuscule ; sinon nul.</summary>
    private static char AccessKey(object? header)
    {
        if (header is string text)
        {
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (text[i] == '_')
                {
                    if (text[i + 1] != '_')
                    {
                        return char.ToUpperInvariant(text[i + 1]);
                    }

                    i++;
                }
            }
        }

        return '\0';
    }

    private GestureMenuItem MenuEntry(string header, Action action, string? gesture = null, bool enabled = true, string? icon = null)
    {
        var item = new GestureMenuItem { Header = header, GestureText = gesture ?? "", IsEnabled = enabled };
        if (icon is not null && this.TryFindResource(icon, ActualThemeVariant, out var source) && source is IImage)
        {
            item.Icon = Palette.Icon(icon, 16);
        }

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Entrée de menu qui affiche le raccourci tel qu'il est traduit (« Ctrl+Maj+C », « Ctrl+molette haut »), à la place du
    /// geste clavier d'Avalonia qui n'a ni traduction ni molette.
    /// </summary>
    internal sealed class GestureMenuItem : MenuItem
    {
        public string GestureText { get; init; } = "";

        protected override Type StyleKeyOverride => typeof(MenuItem);

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            if (GestureText.Length > 0 && e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } text)
            {
                text.Text = GestureText;
                text.IsVisible = true;
            }
        }
    }

    /// <summary>
    /// Tout l'historique et l'écran, copiés comme toute sélection ; jusqu'à la dernière ligne écrite (au moins celle du
    /// curseur), sans les lignes vides du bas de l'écran.
    /// </summary>
    private void SelectAll()
    {
        if (_emulator is null)
        {
            return;
        }

        int last = _emulator.Rows - 1;
        while (last > _emulator.CursorRow && _emulator.GetLine(last).All(c => c.CodePoint is ' ' or 0))
        {
            last--;
        }

        _viewport?.Select((-_emulator.ScrollbackCount, 0), (last, _emulator.Columns - 1));
        CopySelection();
        InvalidateVisual();
    }

    /// <summary>Texte sélectionné (tests).</summary>
    internal string SelectedText => _viewport?.SelectedText ?? "";

    /// <summary>Oublie les lignes sorties de l'écran (sur ce poste : rien n'est envoyé au serveur).</summary>
    private void ClearScrollback()
    {
        if (_emulator is null)
        {
            return;
        }

        _emulator.ClearScrollback();
        _viewport?.ScrollTo(0);
        ClearSelection();
        _matches = [];
        _currentMatch = -1;
        ScrollbackCleared?.Invoke();
        InvalidateVisual();
    }

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    private async Task<bool> ClipboardHasTextAsync()
    {
        try
        {
            return Clipboard is { } clipboard && (await clipboard.GetDataFormatsAsync()).Contains(DataFormat.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Presse-papiers indisponible (occupé par une autre application) : « Coller » grisé.
            return false;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void ScrollBy(int lines)
    {
        _viewport?.ScrollBy(lines);
        InvalidateVisual();
    }

    private (int Row, int Column) CellAt(Point p)
    {
        var emulator = _emulator!;
        int row = (int)Math.Floor((p.Y - Padding) / _cellHeight);
        int col = (int)Math.Floor((p.X - Padding) / _cellWidth);
        row = Math.Clamp(row, 0, emulator.Rows - 1) - ScrollOffset;
        return (row, Math.Clamp(col, 0, emulator.Columns - 1));
    }

    private void SelectWord((int Row, int Column) cell)
    {
        var emulator = _emulator!;
        var line = emulator.GetLine(cell.Row);

        // Ni blanc ni séparateur ; la case de droite d'un caractère large compte comme lui.
        bool IsWordChar(int col)
        {
            if (line[col].IsWideTail && col > 0)
            {
                col--;
            }

            var text = emulator.CellText(line[col]);
            return text.Length > 0 && !char.IsWhiteSpace(text[0]) && "\"'`()[]{}<>|;,".IndexOf(text[0]) < 0;
        }

        if (cell.Column >= line.Length || !IsWordChar(cell.Column))
        {
            ClearSelection();
            return;
        }

        int start = cell.Column;
        int end = cell.Column;
        while (start > 0 && IsWordChar(start - 1))
        {
            start--;
        }

        while (end < line.Length - 1 && IsWordChar(end + 1))
        {
            end++;
        }

        _viewport?.Select((cell.Row, start), (cell.Row, end));
    }

    /// <summary>Case sélectionnée ; un caractère large l'est en entier, comme il sera copié.</summary>
    private bool IsSelected(Cell[] line, int row, int col)
    {
        if (_shownSelection is null)
        {
            return false;
        }

        if (line[col].IsWideTail && col > 0)
        {
            col--;
        }

        return IsSelected(row, col) || (col + 1 < line.Length && line[col + 1].IsWideTail && IsSelected(row, col + 1));
    }

    private bool IsSelected(int row, int col)
    {
        if (_shownSelection is not { } selection)
        {
            return false;
        }

        var (from, to) = selection;
        return (row > from.Row || (row == from.Row && col >= from.Column)) && (row < to.Row || (row == to.Row && col <= to.Column));
    }

    private void ClearSelection() => _viewport?.ClearSelection();

    private void CopySelection()
    {
        var text = SelectedText.Replace("\n", Environment.NewLine);
        if (text.Length > 0 && Clipboard is { } clipboard)
        {
            _ = CopyAsync(clipboard, text);
        }

        static async Task CopyAsync(IClipboard clipboard, string text)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Presse-papiers occupé par une autre application : on ignore.
            }
        }
    }

    private void Paste() => _ = PasteAsync();

    private async Task PasteAsync()
    {
        if (_emulator is null || Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                Send(TerminalInput.Pasted(text));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Presse-papiers occupé par une autre application : rien n'est collé.
        }
    }

    /// <summary>
    /// Méthode de saisie (chinois, japonais, coréen) : sa fenêtre de composition s'ouvre au curseur du terminal, le texte
    /// composé arrive par <see cref="OnTextInput"/>. Sans elle, Avalonia désactive la méthode de saisie sous Windows.
    /// </summary>
    private sealed class InputMethodClient(TerminalView view) : TextInputMethodClient
    {
        public override Visual TextViewVisual => view;

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => "";

        public override Rect CursorRectangle => view._cursorRect;

        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        public void MoveCursor() => RaiseCursorRectangleChanged();
    }
}
