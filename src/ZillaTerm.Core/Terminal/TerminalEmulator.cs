using System.Text;

namespace ZillaTerm.Core.Terminal;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,

    /// <summary>
    /// Sur la dernière case d'une ligne seulement : la ligne a été repliée automatiquement et se poursuit sur la suivante
    /// (<see cref="TerminalEmulator.IsWrapped(int)"/>). Attribut de la ligne, sans effet sur l'affichage.
    /// </summary>
    Wrapped = 64,
}

/// <summary>Une case de l'écran : caractère et attributs.</summary>
public struct Cell
{
    /// <summary>Valeur de <see cref="CodePoint"/> dans la case de droite d'un caractère large (chinois, emoji…).</summary>
    public const int WideTail = -1;

    /// <summary>Premier numéro d'un caractère accompagné de marques combinantes, gardé par l'émulateur.</summary>
    internal const int FirstCluster = 0x110000;

    /// <summary>
    /// Caractère : point de code Unicode (hors BMP compris), <see cref="WideTail"/>, ou au-delà de U+10FFFF un caractère
    /// accompagné de marques combinantes (accent décomposé…). Texte : <see cref="TerminalEmulator.CellText"/>.
    /// </summary>
    public int CodePoint;
    public int Foreground;
    public int Background;
    public CellFlags Flags;

    /// <summary>Case de droite d'un caractère large : rien à dessiner ni à copier, il est dans la case de gauche.</summary>
    public readonly bool IsWideTail => CodePoint == WideTail;

    public static Cell Blank(int background = TerminalColor.Default) =>
        new() { CodePoint = ' ', Foreground = TerminalColor.Default, Background = background };
}

/// <summary>
/// Émulateur de terminal compatible xterm (sous-ensemble utilisé par bash, vim, less, top, mc...).
/// Reçoit le flux texte de la session SSH et maintient l'écran, l'historique et les modes.
/// Non thread-safe : à utiliser depuis un seul thread (celui de l'interface).
/// </summary>
public sealed class TerminalEmulator
{
    private const int MaxStringLength = 4096;

    // Paramètres d'une séquence CSI gardés au plus (xterm en garde 30) : « \e[1;1;1;…m » sans fin ne remplit pas la mémoire.
    private const int MaxParams = 32;

    // Caractères accompagnés de marques combinantes : distincts au plus (au-delà, les nouvelles marques sont ignorées),
    // et longueur de chacun (xterm garde lui aussi un nombre limité de marques par case).
    private const int MaxClusters = 16384;
    private const int MaxClusterLength = 32;

    private readonly LineRing _scrollback;
    private readonly List<int> _params = [];
    private readonly StringBuilder _osc = new();

    // Texte de chaque caractère à marques combinantes (Cell.CodePoint - Cell.FirstCluster), et son numéro par texte.
    private readonly List<string> _clusters = [];
    private readonly Dictionary<string, int> _clusterIds = new(StringComparer.Ordinal);

    private Cell[][] _main;
    private Cell[][]? _alt;
    private Cell[][] _screen;
    private State _state;
    private int _param = -1;
    private char _private;
    private char _intermediate;
    private bool _charsetForG1;
    private bool _wrapPending;

    // Première moitié UTF-16 d'un caractère hors BMP, en attente de la seconde (éventuellement dans le texte suivant).
    private char _highSurrogate;

    // Un caractère large a déjà été écrit : sans lui (le cas courant), aucune case à vérifier avant d'écrire.
    private bool _wideWritten;

    // Ligne du curseur de l'écran principal pendant l'écran alternatif : un redimensionnement le traite comme affiché.
    private int _mainCursorRow;

    // Lignes de l'écran principal sorties par le haut depuis le début (historique plein ou non) : numéro absolu d'une
    // ligne = _scrolledOff + rang à l'écran, stable quand l'écran défile.
    private long _scrolledOff;
    private long? _eraseFrom;
    private int _scrollTop;
    private int _scrollBottom;
    private int _fg = TerminalColor.Default;
    private int _bg = TerminalColor.Default;
    private CellFlags _flags;
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;

    // Curseur sauvegardé (DECSC), un par écran comme xterm : vim ou less qui sauvegardent le leur dans l'écran
    // alternatif n'écrasent pas celui que « \e[?1049l » restaure.
    private SavedCursor _savedMain;
    private SavedCursor _savedAlt;

    public TerminalEmulator(int columns = 80, int rows = 24, int maxScrollback = 5000)
    {
        Columns = Math.Max(columns, 2);
        Rows = Math.Max(rows, 2);
        _scrollback = new LineRing(maxScrollback);
        _main = NewScreen(Rows, Columns);
        _screen = _main;
        _scrollBottom = Rows - 1;
        _savedMain = _savedAlt = InitialCursor;
    }

    private static SavedCursor InitialCursor =>
        new(0, 0, TerminalColor.Default, TerminalColor.Default, CellFlags.None, false, false, false, false);

    private enum State
    {
        Ground,
        Escape,
        EscapeCharset,
        EscapeIgnoreNext,
        Csi,
        Osc,
        OscEscape,
        IgnoreString,
        IgnoreStringEscape,
    }

    /// <summary>
    /// Marque la ligne du curseur : le prochain <see cref="WorkingDirectory.EraseMarker"/> reçu efface l'écran depuis le
    /// début de cette ligne. Sert à masquer une commande tapée par ZillaTerm (suivi du dossier) et son écho, sur autant de
    /// lignes que le serveur en a affiché, quelle que soit la largeur qu'il suppose. Sans effet en écran alternatif.
    /// La marque reste posée jusqu'au marqueur (<see cref="EraseMarkerReceived"/>) ou jusqu'à <see cref="CancelEraseMark"/> :
    /// si la commande ne s'exécute pas (shell non Unix), l'appelant la retire après un délai.
    /// </summary>
    public void MarkEraseFromCursorLine() => _eraseFrom = IsAlternateScreen ? null : _scrolledOff + CursorRow;

    /// <summary>Retire la marque posée par <see cref="MarkEraseFromCursorLine"/> : un marqueur reçu ensuite est sans effet.</summary>
    public void CancelEraseMark() => _eraseFrom = null;

    /// <summary>
    /// Numéro (comme <see cref="FirstScreenLine"/>) de la ligne marquée par <see cref="MarkEraseFromCursorLine"/>, tant que
    /// la marque est posée : les lignes à partir d'elle seront effacées si le marqueur arrive.
    /// </summary>
    public long? EraseMark => _eraseFrom;

    /// <summary>
    /// Ligne quittée par un saut de ligne sur l'écran principal (jamais en écran alternatif : vim, less, top) : son numéro
    /// (comme <see cref="FirstScreenLine"/>), son texte (sans les espaces de fin, sauf si elle continue), et vrai si elle
    /// continue sur la suivante (retour automatique en fin de ligne). Sert au journal d'intervention.
    /// </summary>
    public event Action<long, string, bool>? LineLeft;

    /// <summary>Marqueur reçu alors qu'une marque était posée (écran effacé, sauf en écran alternatif) ; la marque est retirée.</summary>
    public event Action? EraseMarkerReceived;

    /// <summary>Dossier courant signalé par le shell (OSC 7), déjà décodé en chemin Unix.</summary>
    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>Réponse à renvoyer au serveur (rapport de position du curseur, attributs du terminal...).</summary>
    public event Action<string>? Response;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public bool ApplicationCursorKeys { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool InsertMode { get; private set; }

    public bool OriginMode { get; private set; }

    public bool IsAlternateScreen => _alt is not null && ReferenceEquals(_screen, _alt);

    /// <summary>Nombre de lignes d'historique consultables (aucune en écran alternatif : vim, less...).</summary>
    public int ScrollbackCount => IsAlternateScreen ? 0 : _scrollback.Count;

    /// <summary>Incrémenté à chaque modification : permet au rendu de savoir s'il doit redessiner.</summary>
    public long Version { get; private set; }

    /// <summary>Ligne <paramref name="row"/> : de -ScrollbackCount (plus ancienne) à Rows - 1.</summary>
    public Cell[] GetLine(int row) =>
        row < 0 ? _scrollback[_scrollback.Count + row] : _screen[row];

    /// <summary>
    /// Numéro de la première ligne de l'écran, compté depuis le début : la ligne <c>row</c> (comme <see cref="GetLine"/>)
    /// porte le numéro <c>FirstScreenLine + row</c>, qui ne change pas quand l'écran défile.
    /// </summary>
    public long FirstScreenLine => _scrolledOff;

    /// <summary>
    /// La ligne <paramref name="row"/> (comme <see cref="GetLine"/>) a été repliée automatiquement : son texte se poursuit
    /// sur la suivante. La copie, la recherche et l'enregistrement les joignent sans saut de ligne.
    /// </summary>
    public bool IsWrapped(int row) => IsWrapped(GetLine(row));

    /// <summary>La ligne se poursuit sur la suivante : sa dernière case porte <see cref="CellFlags.Wrapped"/>.</summary>
    internal static bool IsWrapped(Cell[] line) => line.Length > 0 && (line[^1].Flags & CellFlags.Wrapped) != 0;

    /// <summary>Texte d'une case : son caractère et ses marques combinantes ; vide pour la case de droite d'un caractère large.</summary>
    public string CellText(Cell cell) => cell.CodePoint switch
    {
        Cell.WideTail => "",
        >= Cell.FirstCluster and var id => id - Cell.FirstCluster < _clusters.Count ? _clusters[id - Cell.FirstCluster] : "\uFFFD",
        var c => Rune.TryCreate(c, out var rune) ? rune.ToString() : "\uFFFD",
    };

    /// <summary>Ajoute le texte d'une case (comme <see cref="CellText"/>, sans allocation pour un caractère simple).</summary>
    internal void AppendText(StringBuilder text, Cell cell)
    {
        int c = cell.CodePoint;
        if (c is >= 0 and < 0x10000)
        {
            text.Append((char)c);
        }
        else if (c != Cell.WideTail)
        {
            text.Append(CellText(cell));
        }
    }

    public void Feed(string text)
    {
        foreach (var c in text)
        {
            Process(c);
        }

        Version++;
        Fed?.Invoke(text);
    }

    /// <summary>Texte reçu et traité (<see cref="Feed"/>), pour le transmettre tel quel (session partagée).</summary>
    public event Action<string>? Fed;

    /// <summary>Taille changée (<see cref="Resize"/>).</summary>
    public event Action? Resized;

    /// <summary>
    /// Séquence qui reproduit l'écran dans un émulateur neuf de même taille : l'écran principal, puis l'écran alternatif
    /// par-dessus s'il est affiché (vim, less), les couleurs et attributs, la zone de défilement, les modes (curseur
    /// « application », collage encadré…) et le curseur. Sans l'historique : seul ce qui est à l'écran est transmis.
    /// </summary>
    public string Snapshot()
    {
        var text = new StringBuilder("\x1b[0m\x1b[H\x1b[2J");
        bool alt = IsAlternateScreen && _alt is not null;
        WriteScreen(text, _main);
        if (alt)
        {
            // Le curseur sauvegardé à l'entrée dans vim, que « \e[?1049l » restaurera.
            text.Append("\x1b[0m");
            AppendPosition(text, _savedMain.Row, _savedMain.Column);
            text.Append("\x1b[?1049h");
            WriteScreen(text, _alt!);
        }

        text.Append("\x1b[0m");
        if (_scrollTop != 0 || _scrollBottom != Rows - 1)
        {
            text.Append("\x1b[").Append(_scrollTop + 1).Append(';').Append(_scrollBottom + 1).Append('r');
        }

        text.Append(ApplicationCursorKeys ? "\x1b[?1h" : "")
            .Append(BracketedPaste ? "\x1b[?2004h" : "")
            .Append(AutoWrap ? "" : "\x1b[?7l")
            .Append(InsertMode ? "\x1b[4h" : "")
            .Append(OriginMode ? "\x1b[?6h" : "");
        AppendPosition(text, OriginMode ? CursorRow - _scrollTop : CursorRow, CursorColumn);
        AppendAttributes(text, _fg, _bg, _flags);
        text.Append(_g0Graphics ? "\x1b(0" : "").Append(_g1Graphics ? "\x1b)0" : "").Append(_shiftOut ? "\x0e" : "")
            .Append(CursorVisible ? "" : "\x1b[?25l");
        return text.ToString();
    }

    /// <summary>Lignes d'un écran : couleurs et attributs à chaque changement, fin des lignes vides omise ; une ligne repliée
    /// se poursuit sans repositionnement, pour que la suivante reste repliée.</summary>
    private void WriteScreen(StringBuilder text, Cell[][] screen)
    {
        int fg = TerminalColor.Default, bg = TerminalColor.Default;
        var flags = CellFlags.None;
        text.Append("\x1b[0m");
        bool continued = false;
        for (int row = 0; row < screen.Length; row++)
        {
            var line = screen[row];
            int end = line.Length;
            bool wrapped = IsWrapped(line) && row + 1 < screen.Length;
            if (!wrapped)
            {
                while (end > 0 && line[end - 1] is { CodePoint: ' ', Background: TerminalColor.Default } blank
                       && (blank.Flags & ~CellFlags.Wrapped & (CellFlags.Inverse | CellFlags.Underline)) == 0)
                {
                    end--;
                }
            }

            if (!continued)
            {
                if (end == 0)
                {
                    continue;
                }

                AppendPosition(text, row, 0);
            }

            for (int column = 0; column < end; column++)
            {
                var cell = line[column];
                if (cell.IsWideTail)
                {
                    continue;
                }

                var cellFlags = cell.Flags & ~CellFlags.Wrapped;
                if (cell.Foreground != fg || cell.Background != bg || cellFlags != flags)
                {
                    AppendAttributes(text, cell.Foreground, cell.Background, cellFlags);
                    (fg, bg, flags) = (cell.Foreground, cell.Background, cellFlags);
                }

                AppendText(text, cell);
            }

            continued = wrapped && end == line.Length;
        }
    }

    private static void AppendPosition(StringBuilder text, int row, int column) =>
        text.Append("\x1b[").Append(row + 1).Append(';').Append(column + 1).Append('H');

    private static void AppendAttributes(StringBuilder text, int fg, int bg, CellFlags flags)
    {
        text.Append("\x1b[0");
        foreach (var (flag, code) in new[] { (CellFlags.Bold, 1), (CellFlags.Dim, 2), (CellFlags.Italic, 3), (CellFlags.Underline, 4),
                     (CellFlags.Inverse, 7), (CellFlags.Hidden, 8) })
        {
            if ((flags & flag) != 0)
            {
                text.Append(';').Append(code);
            }
        }

        AppendColor(text, fg, foreground: true);
        AppendColor(text, bg, foreground: false);
        text.Append('m');
    }

    private static void AppendColor(StringBuilder text, int color, bool foreground)
    {
        if (color == TerminalColor.Default)
        {
            return;
        }

        if ((color & 0x1000000) != 0)
        {
            text.Append(foreground ? ";38;2;" : ";48;2;").Append((color >> 16) & 0xFF).Append(';').Append((color >> 8) & 0xFF)
                .Append(';').Append(color & 0xFF);
        }
        else if (color < 8)
        {
            text.Append(';').Append((foreground ? 30 : 40) + color);
        }
        else if (color < 16)
        {
            text.Append(';').Append((foreground ? 90 : 100) + color - 8);
        }
        else
        {
            text.Append(foreground ? ";38;5;" : ";48;5;").Append(color);
        }
    }

    /// <summary>Oublie l'historique (lignes sorties de l'écran) ; l'écran lui-même ne change pas.</summary>
    public void ClearScrollback()
    {
        _scrollback.Clear();
        Version++;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(columns, 2);
        rows = Math.Max(rows, 2);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // Si l'écran rétrécit sous le curseur, les lignes du haut partent dans l'historique. Pendant vim, less ou top
        // (écran alternatif), l'écran principal est redimensionné de même avec son propre curseur : il réapparaît
        // intact en sortant, l'invite sur la bonne ligne.
        bool alt = IsAlternateScreen;
        int mainShift = Math.Max(0, (alt ? _mainCursorRow : CursorRow) - rows + 1);
        for (int i = 0; i < mainShift; i++)
        {
            PushScrollback(_main[i]);
        }

        _main = ResizeScreen(_main, mainShift, rows, columns);
        Shift(ref _savedMain, mainShift, rows, columns);
        _mainCursorRow = Math.Clamp(_mainCursorRow - mainShift, 0, rows - 1);
        int altShift = alt ? Math.Max(0, CursorRow - rows + 1) : 0;
        if (_alt is not null)
        {
            _alt = ResizeScreen(_alt, altShift, rows, columns);
            Shift(ref _savedAlt, altShift, rows, columns);
        }

        _screen = alt ? _alt! : _main;
        Columns = columns;
        Rows = rows;
        CursorRow = Math.Clamp(CursorRow - (alt ? altShift : mainShift), 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _wrapPending = false;
        Version++;
        Resized?.Invoke();
    }

    /// <summary>Curseur sauvegardé d'un écran qui a perdu ses <paramref name="shift"/> premières lignes.</summary>
    private static void Shift(ref SavedCursor saved, int shift, int rows, int columns)
    {
        saved.Row = Math.Clamp(saved.Row - shift, 0, rows - 1);
        saved.Column = Math.Clamp(saved.Column, 0, columns - 1);
    }

    /// <summary>
    /// Texte de la zone sélectionnée (coordonnées absolues, fin incluse), espaces de fin retirés. Une ligne repliée
    /// automatiquement (<see cref="IsWrapped(int)"/>) est jointe à la suivante sans saut de ligne, ses espaces de fin
    /// gardés : une commande ou un chemin longs se copient tels qu'ils ont été écrits. Un caractère large dont une seule
    /// moitié est dans la zone est pris en entier, avec ses marques combinantes.
    /// </summary>
    public string GetText(int startRow, int startColumn, int endRow, int endColumn)
    {
        if (startRow > endRow || (startRow == endRow && startColumn > endColumn))
        {
            (startRow, startColumn, endRow, endColumn) = (endRow, endColumn, startRow, startColumn);
        }

        var sb = new StringBuilder();
        var text = new StringBuilder();
        int first = Math.Max(startRow, -ScrollbackCount);
        int last = Math.Min(endRow, Rows - 1);
        for (int row = first; row <= last; row++)
        {
            var line = GetLine(row);
            int from = row == startRow ? Math.Max(0, startColumn) : 0;
            int to = row == endRow ? Math.Min(endColumn, line.Length - 1) : line.Length - 1;
            if (from > 0 && from < line.Length && line[from].IsWideTail)
            {
                from--;
            }

            text.Clear();
            for (int col = from; col <= to; col++)
            {
                AppendText(text, line[col]);
            }

            // Ligne repliée, prise jusqu'au bout (la zone continue) : la suivante la prolonge.
            if (row < last && IsWrapped(line))
            {
                sb.Append(text);
                continue;
            }

            sb.Append(text.ToString().TrimEnd());
            if (row < last)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    // ===================== Analyseur =====================

    private void Process(char c)
    {
        switch (_state)
        {
            case State.Ground:
                if (c is >= ' ' and < '\x7F' && _highSurrogate == '\0')
                {
                    // ASCII imprimable : le cas courant, sans autre test.
                    Print(c);
                }
                else
                {
                    Ground(c);
                }

                break;
            case State.Escape:
                Escape(c);
                break;
            case State.EscapeCharset:
                if (_charsetForG1)
                {
                    _g1Graphics = c == '0';
                }
                else
                {
                    _g0Graphics = c == '0';
                }

                _state = State.Ground;
                break;
            case State.EscapeIgnoreNext:
                _state = State.Ground;
                break;
            case State.Csi:
                Csi(c);
                break;
            case State.Osc:
                if (c == '\a')
                {
                    EndOsc();
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEscape;
                }
                else if (_osc.Length < MaxStringLength)
                {
                    _osc.Append(c);
                }

                break;
            case State.OscEscape:
                // ESC \ (ST) termine l'OSC ; toute autre séquence l'abandonne.
                if (c == '\\')
                {
                    EndOsc();
                }
                else
                {
                    _osc.Clear();
                    _state = State.Escape;
                    Escape(c);
                }

                break;
            case State.IgnoreString:
                if (c == '\a')
                {
                    _state = State.Ground;
                }
                else if (c == '\x1b')
                {
                    _state = State.IgnoreStringEscape;
                }

                break;
            case State.IgnoreStringEscape:
                _state = c == '\\' ? State.Ground : State.IgnoreString;
                break;
        }
    }

    /// <summary>
    /// Caractère reçu hors séquence, autre que l'ASCII imprimable : contrôle, ou caractère à écrire. Un caractère hors BMP
    /// (emoji, icônes de l'invite) arrive en deux moitiés UTF-16, réunies même reçues dans deux textes ; une moitié
    /// seule est remplacée par U+FFFD.
    /// </summary>
    private void Ground(char c)
    {
        if (_highSurrogate != '\0')
        {
            char high = _highSurrogate;
            _highSurrogate = '\0';
            if (char.IsLowSurrogate(c))
            {
                Print(char.ConvertToUtf32(high, c));
                return;
            }

            Print(0xFFFD);
        }

        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
        }
        else if (char.IsLowSurrogate(c))
        {
            Print(0xFFFD);
        }
        else if (c < 0x20 || c == 0x7F)
        {
            Control(c);
        }
        else
        {
            Print(c);
        }
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\a':
                // Sonnerie : ignorée.
                break;
            case '\b':
                _wrapPending = false;
                if (CursorColumn > 0)
                {
                    CursorColumn--;
                }

                break;
            case '\t':
                CursorColumn = Math.Min(Columns - 1, (CursorColumn / 8 + 1) * 8);
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\r':
                CursorColumn = 0;
                _wrapPending = false;
                break;
            case '\x0E':
                _shiftOut = true;
                break;
            case '\x0F':
                _shiftOut = false;
                break;
            case '\x1b':
                _state = State.Escape;
                break;
            case '\x18':
            case '\x1A':
                _state = State.Ground;
                break;
        }
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _param = -1;
                _private = '\0';
                _intermediate = '\0';
                _state = State.Csi;
                break;
            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = State.IgnoreString;
                break;
            case '(':
            case ')':
                _charsetForG1 = c == ')';
                _state = State.EscapeCharset;
                break;
            case '*':
            case '+':
            case '#':
            case '%':
            case ' ':
                _state = State.EscapeIgnoreNext;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorColumn = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
        }
    }

    private void Csi(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _param = (_param < 0 ? 0 : _param) * 10 + (c - '0');
            if (_param > 99999)
            {
                _param = 99999;
            }

            return;
        }

        switch (c)
        {
            case ';':
            case ':':
                if (_params.Count < MaxParams)
                {
                    _params.Add(_param);
                }

                _param = -1;
                return;
            case '?':
            case '>':
            case '<':
            case '=':
                _private = c;
                return;
            case '\x1b':
                _state = State.Escape;
                return;
        }

        if (c < 0x20)
        {
            Control(c);
            return;
        }

        if (c >= 0x20 && c <= 0x2F)
        {
            _intermediate = c;
            return;
        }

        if (_params.Count < MaxParams)
        {
            _params.Add(_param);
        }

        _state = State.Ground;
        if (c >= 0x40 && c <= 0x7E)
        {
            ExecuteCsi(c);
        }
    }

    private int Param(int index, int fallback)
    {
        int value = index < _params.Count ? _params[index] : -1;
        return value <= 0 ? fallback : value;
    }

    private void ExecuteCsi(char final)
    {
        if (_intermediate != '\0')
        {
            // DECSCUSR (forme du curseur), DECSTR... : sans effet ici.
            if (_intermediate == '!' && final == 'p')
            {
                SoftReset();
            }

            return;
        }

        switch (final)
        {
            case '@':
                InsertChars(Param(0, 1));
                break;
            case 'A':
                MoveCursorRow(-Param(0, 1));
                break;
            case 'B':
            case 'e':
                MoveCursorRow(Param(0, 1));
                break;
            case 'C':
            case 'a':
                SetCursor(CursorRow, CursorColumn + Param(0, 1));
                break;
            case 'D':
                SetCursor(CursorRow, CursorColumn - Param(0, 1));
                break;
            case 'E':
                MoveCursorRow(Param(0, 1));
                CursorColumn = 0;
                break;
            case 'F':
                MoveCursorRow(-Param(0, 1));
                CursorColumn = 0;
                break;
            case 'G':
            case '`':
                SetCursor(CursorRow, Param(0, 1) - 1);
                break;
            case 'H':
            case 'f':
                CursorPosition(Param(0, 1) - 1, Param(1, 1) - 1);
                break;
            case 'J':
                EraseInDisplay(_params.Count > 0 ? Math.Max(_params[0], 0) : 0);
                break;
            case 'K':
                EraseInLine(_params.Count > 0 ? Math.Max(_params[0], 0) : 0);
                break;
            case 'L':
                InsertLines(Param(0, 1));
                break;
            case 'M':
                DeleteLines(Param(0, 1));
                break;
            case 'P':
                DeleteChars(Param(0, 1));
                break;
            case 'S':
                if (_private == '\0')
                {
                    ScrollUp(_scrollTop, _scrollBottom, Param(0, 1), toHistory: true);
                }

                break;
            case 'T':
                if (_private == '\0')
                {
                    ScrollDown(_scrollTop, _scrollBottom, Param(0, 1));
                }

                break;
            case 'X':
                EraseChars(Param(0, 1));
                break;
            case 'd':
                // Ligne absolue, comptée depuis le haut de la zone de défilement en mode origine (CursorPosition).
                CursorPosition(Param(0, 1) - 1, CursorColumn);
                break;
            case 'h':
            case 'l':
                SetModes(final == 'h');
                break;
            case 'm':
                if (_private == '\0')
                {
                    SelectGraphicRendition();
                }

                break;
            case 'n':
                DeviceStatusReport();
                break;
            case 'c':
                if (_private == '>')
                {
                    Response?.Invoke("\x1b[>0;10;1c");
                }
                else if (_private == '\0' && Param(0, 0) == 0)
                {
                    Response?.Invoke("\x1b[?1;2c");
                }

                break;
            case 'r':
                if (_private == '\0')
                {
                    SetScrollRegion(Param(0, 1) - 1, Param(1, Rows) - 1);
                }

                break;
            case 's':
                if (_private == '\0')
                {
                    SaveCursor();
                }

                break;
            case 'u':
                if (_private == '\0')
                {
                    RestoreCursor();
                }

                break;
        }
    }

    private void EndOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        _osc.Clear();
        int sep = text.IndexOf(';');
        if (sep <= 0)
        {
            return;
        }

        var code = text[..sep];
        var value = text[(sep + 1)..];
        // Titre de la fenêtre (OSC 0 / 2) et autres codes : ignorés.
        switch (code)
        {
            case "7":
                if (WorkingDirectory.Parse(value) is { } path)
                {
                    WorkingDirectoryChanged?.Invoke(path);
                }

                break;
            case WorkingDirectory.EraseOscCode:
                EraseFromMark();
                break;
        }
    }

    /// <summary>
    /// Fin d'une commande tapée par ZillaTerm : écran effacé depuis la ligne marquée par <see cref="MarkEraseFromCursorLine"/>
    /// (une seule fois ; ignoré sans marque, ce qu'un programme du serveur ne peut donc pas déclencher de lui-même).
    /// </summary>
    private void EraseFromMark()
    {
        var mark = _eraseFrom;
        _eraseFrom = null;
        if (mark is not long line)
        {
            return;
        }

        if (!IsAlternateScreen)
        {
            SetCursor((int)Math.Clamp(line - _scrolledOff, 0, Rows - 1), 0);
            EraseInDisplay(0);
        }

        EraseMarkerReceived?.Invoke();
    }

    // ===================== Écriture et déplacements =====================

    /// <summary>
    /// Écrit un caractère à la place du curseur, sur une case ou deux selon sa largeur (wcwidth) : un caractère large
    /// occupe aussi la case suivante (<see cref="Cell.WideTail"/>), le curseur avance de deux.
    /// </summary>
    private void Print(int c)
    {
        int width = 1;
        if (c >= 0x80)
        {
            width = CharWidth.Of(c);
            if (width == 0)
            {
                AddMark(c);
                return;
            }
        }
        else if (_shiftOut ? _g1Graphics : _g0Graphics)
        {
            c = DecSpecialGraphics((char)c);
        }

        if (_wrapPending)
        {
            _wrapPending = false;
            if (AutoWrap)
            {
                CursorColumn = 0;
                LineFeed(wrap: true);
            }
        }

        if (width == 2 && CursorColumn == Columns - 1)
        {
            // Une seule colonne libre : le caractère large passe à la ligne (xterm), ou recule d'une case sans retour
            // automatique à la ligne.
            if (AutoWrap)
            {
                CursorColumn = 0;
                LineFeed(wrap: true);
            }
            else
            {
                CursorColumn--;
            }
        }

        if (InsertMode)
        {
            InsertChars(width);
        }

        var line = _screen[CursorRow];
        int col = CursorColumn;

        // Repli de la ligne, porté par sa dernière case : il reste quand le texte est réécrit (shell qui réaffiche une
        // commande longue en cours de modification). Seul un effacement jusqu'au bord droit le retire.
        bool wrapped = IsWrapped(line);
        if (_wideWritten)
        {
            SplitWide(line, col, col + width);
        }

        var cell = new Cell { CodePoint = c, Foreground = _fg, Background = _bg, Flags = _flags };
        line[col] = cell;
        if (width == 2)
        {
            _wideWritten = true;
            cell.CodePoint = Cell.WideTail;
            line[col + 1] = cell;
        }

        if (wrapped)
        {
            line[^1].Flags |= CellFlags.Wrapped;
        }

        if (col + width >= Columns)
        {
            CursorColumn = Columns - 1;
            _wrapPending = AutoWrap;
        }
        else
        {
            CursorColumn = col + width;
        }
    }

    /// <summary>
    /// Marque combinante ou caractère de format (accent décomposé, sélecteur de variante, liant d'emoji…), de largeur 0 :
    /// jointe au caractère précédent, le curseur ne bouge pas (xterm). En début de ligne, sans caractère avant elle : ignorée.
    /// </summary>
    private void AddMark(int mark)
    {
        int col = _wrapPending ? CursorColumn : CursorColumn - 1;
        if (col < 0)
        {
            return;
        }

        var line = _screen[CursorRow];
        if (line[col].IsWideTail && col > 0)
        {
            col--;
        }

        var text = CellText(line[col]) + char.ConvertFromUtf32(mark);
        if (line[col].IsWideTail || text.Length > MaxClusterLength)
        {
            return;
        }

        if (!_clusterIds.TryGetValue(text, out int id))
        {
            if (_clusters.Count >= MaxClusters)
            {
                return;
            }

            id = Cell.FirstCluster + _clusters.Count;
            _clusters.Add(text);
            _clusterIds.Add(text, id);
        }

        line[col].CodePoint = id;
    }

    /// <param name="wrap">Retour automatique en fin de ligne : la ligne quittée se poursuit sur la suivante.</param>
    private void LineFeed(bool wrap = false)
    {
        _wrapPending = false;
        if (wrap)
        {
            _screen[CursorRow][^1].Flags |= CellFlags.Wrapped;
        }

        if (LineLeft is { } left && !IsAlternateScreen)
        {
            var text = new StringBuilder(Columns);
            foreach (var cell in _screen[CursorRow])
            {
                AppendText(text, cell);
            }

            // Morceau d'une ligne coupée : ses espaces de fin font partie du texte.
            left(_scrolledOff + CursorRow, wrap ? text.ToString() : text.ToString().TrimEnd(), wrap);
        }

        if (CursorRow == _scrollBottom)
        {
            ScrollUp(_scrollTop, _scrollBottom, 1, toHistory: true);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _scrollTop)
        {
            ScrollDown(_scrollTop, _scrollBottom, 1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    private void SetCursor(int row, int column)
    {
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
        _wrapPending = false;
    }

    private void CursorPosition(int row, int column)
    {
        if (OriginMode)
        {
            row = Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom);
        }

        SetCursor(row, column);
    }

    private void MoveCursorRow(int delta)
    {
        // Les déplacements verticaux s'arrêtent aux marges de la zone de défilement quand le curseur y est.
        int top = CursorRow >= _scrollTop ? _scrollTop : 0;
        int bottom = CursorRow <= _scrollBottom ? _scrollBottom : Rows - 1;
        SetCursor(Math.Clamp(CursorRow + delta, top, bottom), CursorColumn);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top < bottom)
        {
            _scrollTop = top;
            _scrollBottom = bottom;
            CursorPosition(0, 0);
        }
    }

    /// <summary>Curseur sauvegardé de l'écran affiché.</summary>
    private ref SavedCursor Saved
    {
        get
        {
            if (IsAlternateScreen)
            {
                return ref _savedAlt;
            }

            return ref _savedMain;
        }
    }

    private void SaveCursor() =>
        Saved = new SavedCursor(CursorRow, CursorColumn, _fg, _bg, _flags, _g0Graphics, _g1Graphics, _shiftOut, OriginMode);

    private void RestoreCursor()
    {
        var saved = Saved;
        SetCursor(saved.Row, saved.Column);
        (_fg, _bg, _flags, _g0Graphics, _g1Graphics, _shiftOut, OriginMode) =
            (saved.Foreground, saved.Background, saved.Flags, saved.G0Graphics, saved.G1Graphics, saved.ShiftOut, saved.OriginMode);
    }

    // ===================== Effacement, insertion, défilement =====================

    private Cell Erased => Cell.Blank(_bg);

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++)
                {
                    Fill(_screen[r], 0, Columns, Erased);
                }

                break;
            case 1:
                EraseInLine(1);
                for (int r = 0; r < CursorRow; r++)
                {
                    Fill(_screen[r], 0, Columns, Erased);
                }

                break;
            case 2:
                for (int r = 0; r < Rows; r++)
                {
                    Fill(_screen[r], 0, Columns, Erased);
                }

                break;
            case 3:
                // « Erase Saved Lines » (xterm) : l'historique seul, l'écran reste (« clear » envoie aussi ED 2).
                _scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var line = _screen[CursorRow];
        switch (mode)
        {
            case 0:
                Erase(line, CursorColumn, Columns);
                break;
            case 1:
                Erase(line, 0, CursorColumn + 1);
                break;
            case 2:
                Fill(line, 0, Columns, Erased);
                break;
        }

        _wrapPending = false;
    }

    private void EraseChars(int count) =>
        Erase(_screen[CursorRow], CursorColumn, Math.Min(Columns, CursorColumn + count));

    /// <summary>Efface les cases [from, to) d'une ligne, et en entier un caractère large qui n'y est qu'à moitié.</summary>
    private void Erase(Cell[] line, int from, int to)
    {
        SplitWide(line, from, to);
        Fill(line, from, to, Erased);
    }

    private void InsertChars(int count)
    {
        var line = _screen[CursorRow];
        bool wrapped = TakeWrapped(line);
        count = Math.Min(count, Columns - CursorColumn);
        int kept = Columns - CursorColumn - count;
        // Un caractère large coupé par l'insertion, ou dont la moitié droite sort de la ligne, est effacé.
        SplitWide(line, CursorColumn, CursorColumn);
        bool cutAtEnd = kept > 0 && line[CursorColumn + kept].IsWideTail;
        Array.Copy(line, CursorColumn, line, CursorColumn + count, kept);
        if (cutAtEnd)
        {
            line[Columns - 1] = Cell.Blank(line[Columns - 1].Background);
        }

        Fill(line, CursorColumn, CursorColumn + count, Erased);
        SetWrapped(line, wrapped);
        _wrapPending = false;
    }

    private void DeleteChars(int count)
    {
        var line = _screen[CursorRow];
        bool wrapped = TakeWrapped(line);
        count = Math.Min(count, Columns - CursorColumn);
        SplitWide(line, CursorColumn, CursorColumn + count);
        Array.Copy(line, CursorColumn + count, line, CursorColumn, Columns - CursorColumn - count);
        Fill(line, Columns - count, Columns, Erased);
        SetWrapped(line, wrapped);
        _wrapPending = false;
    }

    /// <summary>
    /// Retire la marque de repli de la ligne avant un décalage de ses cases (elle ne doit pas glisser au milieu) et dit si
    /// elle y était, pour la remettre ensuite (<see cref="SetWrapped"/>) : insérer ou supprimer des caractères ne change
    /// pas la suite de la ligne.
    /// </summary>
    private static bool TakeWrapped(Cell[] line)
    {
        bool wrapped = IsWrapped(line);
        line[^1].Flags &= ~CellFlags.Wrapped;
        return wrapped;
    }

    private static void SetWrapped(Cell[] line, bool wrapped)
    {
        if (wrapped)
        {
            line[^1].Flags |= CellFlags.Wrapped;
        }
    }

    /// <summary>
    /// Les cases [from, to) vont changer : un caractère large qui les déborde (moitié gauche avant <paramref name="from"/>,
    /// ou moitié droite à <paramref name="to"/>) perd sa moitié restante, effacée comme dans xterm.
    /// </summary>
    private static void SplitWide(Cell[] line, int from, int to)
    {
        if (from > 0 && from < line.Length && line[from].IsWideTail)
        {
            line[from - 1] = Cell.Blank(line[from - 1].Background);
        }

        if (to > 0 && to < line.Length && line[to].IsWideTail)
        {
            line[to] = Cell.Blank(line[to].Background);
        }
    }

    private void InsertLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        ScrollDown(CursorRow, _scrollBottom, count);
        CursorColumn = 0;
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        ScrollUp(CursorRow, _scrollBottom, count, toHistory: false);
        CursorColumn = 0;
    }

    /// <param name="toHistory">
    /// Défilement du texte (saut de ligne, CSI S) : sur l'écran principal, les lignes qui sortent par le haut de l'écran
    /// vont dans l'historique. Jamais pour une suppression de lignes (CSI M).
    /// </param>
    private void ScrollUp(int top, int bottom, int count, bool toHistory)
    {
        count = Math.Min(count, bottom - top + 1);
        bool save = toHistory && top == 0 && !IsAlternateScreen;
        for (int i = 0; i < count; i++)
        {
            var removed = _screen[top];
            if (save)
            {
                PushScrollback(removed);
            }

            Array.Copy(_screen, top + 1, _screen, top, bottom - top);
            _screen[bottom] = NewLine(Columns, Erased);
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (int i = 0; i < count; i++)
        {
            Array.Copy(_screen, top, _screen, top + 1, bottom - top);
            _screen[top] = NewLine(Columns, Erased);
        }
    }

    private void PushScrollback(Cell[] line)
    {
        _scrollback.Add(line);
        _scrolledOff++;
    }

    /// <summary>
    /// Historique en anneau : une fois plein, chaque nouvelle ligne remplace la plus ancienne sans déplacer les autres (une
    /// sortie abondante ne fige plus l'interface).
    /// </summary>
    private sealed class LineRing(int capacity)
    {
        private readonly Cell[][] _lines = new Cell[Math.Max(capacity, 0)][];
        private int _start;

        public int Count { get; private set; }

        public Cell[] this[int index] => _lines[(_start + index) % _lines.Length];

        public void Add(Cell[] line)
        {
            if (_lines.Length == 0)
            {
                return;
            }

            if (Count < _lines.Length)
            {
                _lines[(_start + Count) % _lines.Length] = line;
                Count++;
            }
            else
            {
                _lines[_start] = line;
                _start = (_start + 1) % _lines.Length;
            }
        }

        public void Clear()
        {
            Array.Clear(_lines);
            _start = 0;
            Count = 0;
        }
    }

    // ===================== Modes et attributs =====================

    private void SetModes(bool enable)
    {
        foreach (var raw in _params)
        {
            int mode = Math.Max(raw, 0);
            if (_private == '?')
            {
                switch (mode)
                {
                    case 1:
                        ApplicationCursorKeys = enable;
                        break;
                    case 6:
                        OriginMode = enable;
                        CursorPosition(0, 0);
                        break;
                    case 7:
                        AutoWrap = enable;
                        break;
                    case 25:
                        CursorVisible = enable;
                        break;
                    case 47:
                    case 1047:
                        SwitchScreen(enable, clear: enable && mode == 1047);
                        break;
                    case 1049:
                        if (enable)
                        {
                            SaveCursor();
                            SwitchScreen(true, clear: true);
                        }
                        else
                        {
                            SwitchScreen(false, clear: false);
                            RestoreCursor();
                        }

                        break;
                    case 2004:
                        BracketedPaste = enable;
                        break;
                }
            }
            else if (_private == '\0' && mode == 4)
            {
                InsertMode = enable;
            }
        }
    }

    private void SwitchScreen(bool alternate, bool clear)
    {
        if (alternate)
        {
            if (!IsAlternateScreen)
            {
                _mainCursorRow = CursorRow;
            }

            if (_alt is null || clear)
            {
                _alt = NewScreen(Rows, Columns);
            }

            _screen = _alt;
        }
        else
        {
            _screen = _main;
        }

        _wrapPending = false;
    }

    private void SelectGraphicRendition()
    {
        for (int i = 0; i < _params.Count; i++)
        {
            int p = Math.Max(_params[i], 0);
            switch (p)
            {
                case 0:
                    _fg = TerminalColor.Default;
                    _bg = TerminalColor.Default;
                    _flags = CellFlags.None;
                    break;
                case 1:
                    _flags |= CellFlags.Bold;
                    break;
                case 2:
                    _flags |= CellFlags.Dim;
                    break;
                case 3:
                    _flags |= CellFlags.Italic;
                    break;
                case 4:
                    _flags |= CellFlags.Underline;
                    break;
                case 7:
                    _flags |= CellFlags.Inverse;
                    break;
                case 8:
                    _flags |= CellFlags.Hidden;
                    break;
                case 21:
                case 22:
                    _flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    _flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    _flags &= ~CellFlags.Underline;
                    break;
                case 27:
                    _flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    _flags &= ~CellFlags.Hidden;
                    break;
                case >= 30 and <= 37:
                    _fg = p - 30;
                    break;
                case 38:
                    _fg = ExtendedColor(ref i, _fg);
                    break;
                case 39:
                    _fg = TerminalColor.Default;
                    break;
                case >= 40 and <= 47:
                    _bg = p - 40;
                    break;
                case 48:
                    _bg = ExtendedColor(ref i, _bg);
                    break;
                case 49:
                    _bg = TerminalColor.Default;
                    break;
                case >= 90 and <= 97:
                    _fg = p - 90 + 8;
                    break;
                case >= 100 and <= 107:
                    _bg = p - 100 + 8;
                    break;
            }
        }
    }

    /// <summary>38;5;n (palette 256) ou 38;2;r;g;b (couleur vraie).</summary>
    private int ExtendedColor(ref int i, int current)
    {
        int kind = i + 1 < _params.Count ? _params[i + 1] : -1;
        if (kind == 5 && i + 2 < _params.Count)
        {
            i += 2;
            return Math.Clamp(Math.Max(_params[i], 0), 0, 255);
        }

        if (kind == 2 && i + 4 < _params.Count)
        {
            int r = Math.Clamp(Math.Max(_params[i + 2], 0), 0, 255);
            int g = Math.Clamp(Math.Max(_params[i + 3], 0), 0, 255);
            int b = Math.Clamp(Math.Max(_params[i + 4], 0), 0, 255);
            i += 4;
            return TerminalColor.Rgb((byte)r, (byte)g, (byte)b);
        }

        return current;
    }

    private void DeviceStatusReport()
    {
        switch (Param(0, 0))
        {
            case 5:
                Response?.Invoke("\x1b[0n");
                break;
            case 6:
                int row = OriginMode ? CursorRow - _scrollTop : CursorRow;
                Response?.Invoke($"\x1b[{row + 1};{CursorColumn + 1}R");
                break;
        }
    }

    private void SoftReset()
    {
        CursorVisible = true;
        InsertMode = false;
        OriginMode = false;
        AutoWrap = true;
        ApplicationCursorKeys = false;
        _fg = TerminalColor.Default;
        _bg = TerminalColor.Default;
        _flags = CellFlags.None;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        Saved = InitialCursor;
    }

    private void Reset()
    {
        SoftReset();
        BracketedPaste = false;
        _main = NewScreen(Rows, Columns);
        _alt = null;
        _screen = _main;
        _scrollback.Clear();
        _savedMain = _savedAlt = InitialCursor;
        // Plus aucune case ne garde de marques combinantes.
        _clusters.Clear();
        _clusterIds.Clear();
        CursorRow = CursorColumn = 0;
        _wrapPending = false;
        _eraseFrom = null;
    }

    // ===================== Utilitaires =====================

    private static char DecSpecialGraphics(char c) => c switch
    {
        '`' => '◆',
        'a' => '▒',
        'f' => '°',
        'g' => '±',
        'j' => '┘',
        'k' => '┐',
        'l' => '┌',
        'm' => '└',
        'n' => '┼',
        'q' => '─',
        't' => '├',
        'u' => '┤',
        'v' => '┴',
        'w' => '┬',
        'x' => '│',
        'y' => '≤',
        'z' => '≥',
        '~' => '·',
        _ => c,
    };

    private static void Fill(Cell[] line, int from, int to, Cell value)
    {
        for (int i = Math.Max(from, 0); i < Math.Min(to, line.Length); i++)
        {
            line[i] = value;
        }
    }

    private static Cell[] NewLine(int columns, Cell value)
    {
        var line = new Cell[columns];
        Array.Fill(line, value);
        return line;
    }

    private static Cell[][] NewScreen(int rows, int columns)
    {
        var screen = new Cell[rows][];
        for (int r = 0; r < rows; r++)
        {
            screen[r] = NewLine(columns, Cell.Blank());
        }

        return screen;
    }

    private static Cell[][] ResizeScreen(Cell[][] screen, int skip, int rows, int columns)
    {
        var result = new Cell[rows][];
        for (int r = 0; r < rows; r++)
        {
            var line = NewLine(columns, Cell.Blank());
            int source = r + skip;
            if (source < screen.Length)
            {
                Array.Copy(screen[source], line, Math.Min(columns, screen[source].Length));
                if (screen[source].Length > columns && screen[source][columns].IsWideTail)
                {
                    // Caractère large coupé par le bord droit : effacé.
                    line[columns - 1] = Cell.Blank(line[columns - 1].Background);
                }

                // Largeur changée, sans remise en forme des lignes : une ligne repliée ne se poursuit plus sur la suivante.
                // Rétrécie, elle a perdu sa fin (et sa dernière case, qui portait le repli) ; élargie, des cases vides
                // la séparent de la suite. Les lignes de l'historique gardent leur largeur et leur repli.
                if (screen[source].Length < columns)
                {
                    line[screen[source].Length - 1].Flags &= ~CellFlags.Wrapped;
                }
            }

            result[r] = line;
        }

        return result;
    }

    private record struct SavedCursor(
        int Row, int Column, int Foreground, int Background, CellFlags Flags,
        bool G0Graphics, bool G1Graphics, bool ShiftOut, bool OriginMode);
}
