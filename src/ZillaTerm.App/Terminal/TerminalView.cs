using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Terminal;

/// <summary>
/// Affichage d'un <see cref="TerminalEmulator"/> et saisie clavier :
/// sélection = copie, clic droit = menu (ou collage, option des Paramètres), molette = historique.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    private const double Padding = 4;

    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Consolas, Lucida Console, Courier New");

    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private readonly Typeface _regular = new(MonoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _bold = new(MonoFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
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
    private bool _selecting;

    // Molette : reste des pas plus petits qu'une ligne (pavé tactile de précision, molette libre), pour l'historique et
    // pour la taille de police (Ctrl), gardé jusqu'aux événements suivants dans le même sens.
    private int _wheelDelta;
    private int _zoomDelta;

    // Sélection dessinée pendant un rendu, début avant fin ; null sans sélection visible.
    private ((int Row, int Column) From, (int Row, int Column) To)? _shownSelection;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        MeasureCell();
        // Palette et taille des Paramètres : suivies tant que le terminal est affiché (il peut changer de place), et
        // rattrapées à son retour (onglet en arrière-plan, déplacé dans une fenêtre séparée ou la vue parallèle).
        Loaded += (_, _) =>
        {
            TerminalAppearance.Changed += OnAppearanceChangedElsewhere;
            OnAppearanceChanged();
        };
        Unloaded += (_, _) => TerminalAppearance.Changed -= OnAppearanceChangedElsewhere;
    }

    /// <summary>Réglage changé depuis un autre fil que celui du terminal : appliqué sur le fil du terminal.</summary>
    private void OnAppearanceChangedElsewhere()
    {
        if (Dispatcher.CheckAccess())
        {
            OnAppearanceChanged();
        }
        else
        {
            Dispatcher.BeginInvoke(OnAppearanceChanged);
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

    public TerminalTheme Theme => _theme;

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

    private void MeasureCell()
    {
        var probe = Format("M", _regular, Brushes.White);
        _cellWidth = probe.WidthIncludingTrailingWhitespace;
        _cellHeight = Math.Ceiling(MonoFamily.LineSpacing * _fontSize);
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
        Math.Max(2, (int)((ActualWidth - 2 * Padding) / _cellWidth)),
        Math.Max(2, (int)((ActualHeight - 2 * Padding) / _cellHeight)));

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

    /// <summary>Affiche l'historique à <paramref name="offset"/> lignes du bas (0 = la fin, comme à la saisie).</summary>
    public void ScrollTo(int offset)
    {
        _viewport?.ScrollTo(offset);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brush(_theme.Background), null, new Rect(RenderSize));
        var emulator = _emulator;
        if (emulator is null)
        {
            return;
        }

        // Chaque changement (sortie, défilement, taille) passe par un rendu : la barre de défilement suit.
        if (ScrollState != _lastScrollState)
        {
            _lastScrollState = ScrollState;
            Dispatcher.BeginInvoke(() => ScrollStateChanged?.Invoke());
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

            DrawMatches(dc, emulator, row, Padding + screenRow * _cellHeight);
            DrawLine(dc, emulator, row, Padding + screenRow * _cellHeight);
        }

        // Curseur : pavé plein si le terminal a le focus, contour sinon ; sur un caractère large, ses deux cases.
        if (emulator.CursorVisible && offset == 0)
        {
            var line = emulator.GetLine(emulator.CursorRow);
            int column = Math.Min(emulator.CursorColumn, line.Length - 1);
            int cells = WidthAt(line, column, line.Length);
            var rect = new Rect(Padding + column * _cellWidth, Padding + emulator.CursorRow * _cellHeight, cells * _cellWidth, _cellHeight);
            if (IsKeyboardFocused)
            {
                dc.DrawRectangle(Brush(_theme.Cursor), null, rect);
                var cell = line[column];
                if (cell.CodePoint != ' ' && !cell.IsWideTail)
                {
                    DrawGlyph(dc, emulator.CellText(cell), _regular, Brush(_theme.Background), rect.TopLeft, cells);
                }
            }
            else
            {
                dc.DrawRectangle(null, new Pen(Brush(_theme.Cursor), 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
            }
        }
    }

    private void DrawLine(DrawingContext dc, TerminalEmulator emulator, int row, double y)
    {
        var line = emulator.GetLine(row);
        int columns = Math.Min(line.Length, emulator.Columns);
        int col = 0;
        while (col < columns)
        {
            var (fg, bg, bold, underline) = Colors(line[col], IsSelected(line, row, col));
            int start = col;
            bool ascii = true;
            while (col < columns && Colors(line[col], IsSelected(line, row, col)) == (fg, bg, bold, underline))
            {
                ascii &= (uint)line[col].CodePoint < 0x80;
                col++;
            }

            double x = Padding + start * _cellWidth;
            double width = (col - start) * _cellWidth;
            if (bg != _theme.Background)
            {
                dc.DrawRectangle(Brush(bg), null, new Rect(x, y, width, _cellHeight));
            }

            var typeface = bold ? _bold : _regular;
            var brush = Brush(fg);
            if (ascii)
            {
                var text = string.Create(col - start, (line, start), static (chars, run) =>
                {
                    for (int i = 0; i < chars.Length; i++)
                    {
                        chars[i] = (char)run.line[run.start + i].CodePoint;
                    }
                });
                if (!string.IsNullOrWhiteSpace(text))
                {
                    dc.DrawText(Format(text, typeface, brush), new Point(x, y));
                }
            }
            else
            {
                // Caractères hors ASCII (cadres, accents, chinois, emoji...) : placés case par case pour garder
                // l'alignement même si la police de secours a une autre largeur ; un caractère large sur ses deux cases.
                for (int c = start; c < col; c++)
                {
                    if (line[c].CodePoint != ' ' && !line[c].IsWideTail)
                    {
                        DrawGlyph(dc, emulator.CellText(line[c]), typeface, brush, new Point(Padding + c * _cellWidth, y), WidthAt(line, c, columns));
                    }
                }
            }

            if (underline)
            {
                dc.DrawRectangle(brush, null, new Rect(x, y + _cellHeight - 2, width, 1));
            }
        }
    }

    /// <summary>Cases occupées par le caractère de la case <paramref name="col"/> : 2 pour un caractère large visible en entier.</summary>
    private static int WidthAt(Cell[] line, int col, int columns) => col + 1 < columns && line[col + 1].IsWideTail ? 2 : 1;

    /// <summary>
    /// Texte d'une case, à sa place. Un caractère large dont le glyphe déborde de ses deux cases (emoji d'une police de
    /// secours) est resserré pour ne pas empiéter sur le voisin.
    /// </summary>
    private void DrawGlyph(DrawingContext dc, string text, Typeface typeface, Brush brush, Point origin, int cells)
    {
        var formatted = Format(text, typeface, brush);
        double room = cells * _cellWidth;
        if (cells > 1 && formatted.WidthIncludingTrailingWhitespace > room)
        {
            dc.PushTransform(new ScaleTransform(room / formatted.WidthIncludingTrailingWhitespace, 1, origin.X, origin.Y));
            dc.DrawText(formatted, origin);
            dc.Pop();
        }
        else
        {
            dc.DrawText(formatted, origin);
        }
    }

    /// <summary>
    /// Fond des occurrences de la recherche (jaune ; orange pour la courante) sur la ligne <paramref name="row"/>. Une
    /// occurrence coupée par le repli d'une ligne longue continue au début des lignes suivantes.
    /// </summary>
    private void DrawMatches(DrawingContext dc, TerminalEmulator emulator, int row, double y)
    {
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
                dc.DrawRectangle(Brush(i == _currentMatch ? 0xFF9632u : 0xFFE066u), null,
                    new Rect(Padding + column * _cellWidth, y, cells * _cellWidth, _cellHeight));
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

    private SolidColorBrush Brush(uint rgb)
    {
        if (!_brushes.TryGetValue(rgb, out var brush))
        {
            // Couleurs 24 bits : une sortie qui en change sans cesse remplirait le cache, vidé au-delà de 4096 couleurs.
            if (_brushes.Count >= 4096)
            {
                _brushes.Clear();
            }

            brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            _brushes[rgb] = brush;
        }

        return brush;
    }

    private FormattedText Format(string text, Typeface typeface, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _fontSize, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ===================== Taille =====================

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        FitToSize();
    }

    /// <summary>
    /// Réajuste l'émulateur à la taille affichée (et le signale) : utile quand la session a imposé une autre taille, par
    /// exemple celle mesurée avant que le terminal ne change de place pendant la connexion.
    /// </summary>
    public void Refit() => FitToSize();

    private void FitToSize()
    {
        if (FixedSize || _emulator is null || ActualWidth <= 0 || ActualHeight <= 0)
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_emulator is null)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        bool alt = mods.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

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

        if (ctrl && shift && key == Key.C)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if ((ctrl && shift && key == Key.V) || (shift && key == Key.Insert))
        {
            Paste();
            e.Handled = true;
            return;
        }

        if (key == Key.Apps)
        {
            // Touche Menu du clavier : le menu du clic droit, au curseur.
            OpenMenu(atCursor: true);
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
            // Alt+F4 & co. restent gérés par Windows.
            if (alt && terminalKey is >= TerminalKey.F1 and <= TerminalKey.F12)
            {
                return;
            }

            Send(TerminalInput.Special(terminalKey, shift, alt, ctrl));
            e.Handled = true;
            return;
        }

        // Ctrl+lettre → caractère de contrôle. Ctrl+Alt = AltGr sur clavier français : laissé à la saisie de texte.
        if (ctrl && !alt && KeyChar(key) is { } c && TerminalKeys.Control(c) is { } control)
        {
            Send(TerminalInput.Typed(control));
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text))
        {
            Send(TerminalInput.Typed(e.Text));
        }
        else if (!string.IsNullOrEmpty(e.SystemText))
        {
            Send(TerminalInput.Typed("\x1b" + e.SystemText)); // Alt+touche : préfixe Échap (« Meta »).
        }
        else if (!string.IsNullOrEmpty(e.ControlText))
        {
            Send(TerminalInput.Typed(e.ControlText));
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

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_emulator is null)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+molette : taille de police de ce terminal, un point par cran.
            int steps = WheelSteps(ref _zoomDelta, e.Delta, Mouse.MouseWheelDeltaForOneLine);
            if (steps != 0)
            {
                SetFontSize(_fontSize + steps);
            }

            e.Handled = true;
            return;
        }

        // Trois lignes par cran (120).
        int lines = WheelSteps(ref _wheelDelta, e.Delta, 40);
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
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
            CaptureMouse();
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_selecting && _emulator is not null)
        {
            _viewport?.ExtendSelection(CellAt(e.GetPosition(this)));
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndSelection();
    }

    /// <summary>
    /// Capture de la souris perdue pendant une sélection (fenêtre ouverte par-dessus, menu) : le bouton sera relâché
    /// ailleurs, la sélection se termine ici. Sinon les mouvements de la souris l'étendraient encore, sans la copier.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndSelection();
    }

    /// <summary>Fin d'une sélection à la souris : copiée, ou retirée si elle est vide.</summary>
    private void EndSelection()
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        ReleaseMouseCapture();
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

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Focus();
        // Collage direct (option des Paramètres) : Maj+clic droit ouvre alors le menu.
        if (TerminalAppearance.RightClickPastes && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Paste();
        }
        else
        {
            OpenMenu(atCursor: false);
        }

        e.Handled = true;
    }

    // ===================== Menu du clic droit =====================

    /// <summary>
    /// Copier, coller, tout sélectionner, rechercher, enregistrer, effacer l'historique, taille de police, puis les
    /// actions de la session. Recréé à chaque ouverture pour refléter la sélection et le presse-papiers.
    /// </summary>
    /// <param name="atCursor">Sous le curseur du terminal (touche Menu) plutôt que sous la souris.</param>
    private void OpenMenu(bool atCursor)
    {
        if (BuildMenu(atCursor) is { } menu)
        {
            menu.IsOpen = true;
        }
    }

    internal ContextMenu? BuildMenu(bool atCursor)
    {
        if (_emulator is null)
        {
            return null;
        }

        bool selection = _viewport?.HasSelection == true;
        var menu = new ContextMenu { PlacementTarget = this, Placement = atCursor ? PlacementMode.Relative : PlacementMode.MousePoint };
        if (atCursor)
        {
            // Sous le curseur ; historique remonté (curseur hors de la vue) : en bas du terminal.
            int row = Math.Min(_emulator.CursorRow + ScrollOffset, _emulator.Rows - 1);
            menu.HorizontalOffset = Padding + (_emulator.CursorColumn * _cellWidth);
            menu.VerticalOffset = Padding + ((row + 1) * _cellHeight);
        }

        menu.Items.Add(MenuEntry(Strings.MenuTerminalCopy, CopySelection, Strings.ShortcutTerminalCopy, enabled: selection));
        menu.Items.Add(MenuEntry(Strings.MenuTerminalPaste, Paste, Strings.ShortcutTerminalPaste, enabled: ClipboardHasText()));
        var selectAll = MenuEntry(Strings.MenuTerminalSelectAll, SelectAll);
        selectAll.ToolTip = Strings.MenuTerminalSelectAllTip;
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
            save.ToolTip = Strings.MenuTerminalSaveTip;
            menu.Items.Add(save);
        }

        var clear = MenuEntry(Strings.MenuTerminalClear, ClearScrollback, enabled: _emulator.ScrollbackCount > 0, icon: "IconDelete");
        clear.ToolTip = Strings.MenuTerminalClearTip;
        ToolTipService.SetShowOnDisabled(clear, true);
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

        return menu;
    }

    private MenuItem MenuEntry(string header, Action action, string? gesture = null, bool enabled = true, string? icon = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (icon is not null && TryFindResource(icon) is ImageSource source)
        {
            item.Icon = new System.Windows.Controls.Image { Source = source, Width = 16, Height = 16 };
        }

        item.Click += (_, _) => action();
        return item;
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

    private static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
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
        if (text.Length > 0)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Presse-papiers occupé par une autre application : on ignore.
            }
        }
    }

    private void Paste()
    {
        if (_emulator is null)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText())
            {
                Send(TerminalInput.Pasted(Clipboard.GetText()));
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
