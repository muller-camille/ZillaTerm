using System.Globalization;
using System.Text.RegularExpressions;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Ssh;

/// <summary>Niveau d'une ligne de journal, pour sa couleur.</summary>
public enum TailLevel
{
    None,
    Warning,
    Error,
}

/// <summary>Analyse des lignes suivies : niveau, mots à surligner, découpage pour l'affichage.</summary>
public static class TailText
{
    /// <summary>Le niveau est cherché au début de la ligne (horodatage, niveau), pas dans tout le message.</summary>
    public const int LevelSearchLength = 200;

    // Formes usuelles des niveaux (log4j, syslog, Python, JSON...) ; « err » et « crit » seulement en majuscules,
    // trop fréquents en minuscules dans un message ordinaire. Le premier niveau trouvé l'emporte.
    private static readonly Regex Level = new(
        @"\b(?:(?<e>FATAL|CRITICAL|CRIT|EMERG|SEVERE|PANIC|ERROR|ERR|[Ff]atal|[Cc]ritical|[Ss]evere|[Pp]anic|[Ee]rror)|(?<w>WARNING|WARN|[Ww]arning|[Ww]arn))\b",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));

    public static TailLevel DetectLevel(string line)
    {
        try
        {
            var match = Level.Match(line, 0, Math.Min(line.Length, LevelSearchLength));
            return !match.Success ? TailLevel.None : match.Groups["e"].Success ? TailLevel.Error : TailLevel.Warning;
        }
        catch (RegexMatchTimeoutException)
        {
            return TailLevel.None;
        }
    }

    /// <summary>Liste de mots séparés par des virgules ou des points-virgules (« ERROR, OutOfMemory; Connection refused »).</summary>
    public static IReadOnlyList<string> ParseTerms(string? text) =>
        (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>La ligne contient l'un des mots (sans tenir compte de la casse).</summary>
    public static bool ContainsAny(string line, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Positions des mots dans la ligne, triées et fusionnées quand elles se chevauchent.</summary>
    public static IReadOnlyList<(int Start, int Length)> FindTerms(string line, IReadOnlyList<string> terms)
    {
        var found = new List<(int Start, int Length)>();
        foreach (var term in terms)
        {
            for (int at = line.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = at + term.Length < line.Length ? line.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase) : -1)
            {
                found.Add((at, term.Length));
            }
        }

        return Merge(found);
    }

    /// <summary>Plages triées, celles qui se chevauchent ou se touchent réunies.</summary>
    public static IReadOnlyList<(int Start, int Length)> Merge(IEnumerable<(int Start, int Length)> ranges)
    {
        var merged = new List<(int Start, int Length)>();
        foreach (var (start, length) in ranges.Where(r => r.Length > 0).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, start + length) - last.Start);
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }

    /// <summary>
    /// Découpe une ligne de <paramref name="length"/> caractères en morceaux consécutifs, chacun marqué s'il est dans
    /// un mot surligné et/ou dans un résultat de recherche.
    /// </summary>
    public static IReadOnlyList<TailSegment> Segments(int length, IReadOnlyList<(int Start, int Length)> highlights,
        IReadOnlyList<(int Start, int Length)> matches)
    {
        var cuts = new SortedSet<int> { 0, length };
        foreach (var (start, count) in highlights.Concat(matches))
        {
            cuts.Add(Math.Clamp(start, 0, length));
            cuts.Add(Math.Clamp(start + count, 0, length));
        }

        static bool Inside(IReadOnlyList<(int Start, int Length)> ranges, int position) =>
            ranges.Any(r => position >= r.Start && position < r.Start + r.Length);

        var segments = new List<TailSegment>();
        int previous = -1;
        foreach (var cut in cuts)
        {
            if (previous >= 0 && cut > previous)
            {
                var segment = new TailSegment(previous, cut - previous, Inside(highlights, previous), Inside(matches, previous));
                if (segments.Count > 0 && segments[^1].Highlight == segment.Highlight && segments[^1].Match == segment.Match)
                {
                    segments[^1] = segments[^1] with { Length = segments[^1].Length + segment.Length };
                }
                else
                {
                    segments.Add(segment);
                }
            }

            previous = cut;
        }

        return segments;
    }
}

/// <summary>Morceau d'une ligne affichée.</summary>
public readonly record struct TailSegment(int Start, int Length, bool Highlight, bool Match);

/// <summary>
/// Texte cherché dans les lignes : simple (sans tenir compte de la casse) ou expression régulière. Une expression trop
/// lente sur une ligne est abandonnée pour toute la suite (<see cref="TimedOut"/>) : elle ne doit pas figer la fenêtre.
/// </summary>
public sealed class TailPattern
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly string? _text;
    private readonly Regex? _regex;

    private TailPattern(string? text, Regex? regex)
    {
        _text = text;
        _regex = regex;
    }

    /// <summary>L'expression a dépassé le temps permis : elle ne trouve plus rien.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>Motif du texte saisi ; null si le texte est vide ou l'expression invalide (<paramref name="error"/>).</summary>
    public static TailPattern? Create(string? text, bool regex, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!regex)
        {
            return new TailPattern(text, null);
        }

        try
        {
            return new TailPattern(null, new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public bool IsMatch(string line)
    {
        if (_regex is null)
        {
            return line.Contains(_text!, StringComparison.OrdinalIgnoreCase);
        }

        if (TimedOut)
        {
            return false;
        }

        try
        {
            return _regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            TimedOut = true;
            return false;
        }
    }

    /// <summary>Positions des résultats dans la ligne (ceux de longueur nulle sont ignorés).</summary>
    public IReadOnlyList<(int Start, int Length)> Find(string line)
    {
        if (_regex is null)
        {
            return TailText.FindTerms(line, [_text!]);
        }

        var found = new List<(int Start, int Length)>();
        if (TimedOut)
        {
            return found;
        }

        try
        {
            for (var match = _regex.Match(line); match.Success; match = match.NextMatch())
            {
                if (match.Length > 0)
                {
                    found.Add((match.Index, match.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            TimedOut = true;
        }

        return found;
    }
}

/// <summary>Rôle d'une ligne affichée par un filtre.</summary>
public enum TailShownKind
{
    /// <summary>Ligne retenue par le filtre (ou toute ligne sans filtre).</summary>
    Line,

    /// <summary>Ligne voisine d'une ligne retenue (comme <c>grep -C</c>).</summary>
    Context,

    /// <summary>« -- » entre deux groupes de lignes non contigus.</summary>
    Separator,
}

public readonly record struct TailShown<T>(T? Item, TailShownKind Kind);

/// <summary>
/// Filtre des lignes affichées : lignes contenant un texte (<c>grep</c>), sans celles qui en contiennent un autre
/// (<c>grep -v</c>), avec éventuellement des lignes de contexte avant et après (<c>grep -C</c>).
/// </summary>
public sealed class TailFilter(TailPattern? include, TailPattern? exclude, int context)
{
    public const int MaxContext = 50;

    public static TailFilter None { get; } = new(null, null, 0);

    public int Context { get; } = Math.Clamp(context, 0, MaxContext);

    public bool IsActive => include is not null || exclude is not null;

    public bool Accepts(string text) => (include?.IsMatch(text) ?? true) && !(exclude?.IsMatch(text) ?? false);

    /// <summary>Filtrage d'une suite de lignes, ligne par ligne (affichage complet ou lignes qui arrivent).</summary>
    public TailFilterRun<T> Start<T>() => new(this);
}

/// <summary>État du filtre sur une suite de lignes : contexte en attente avant et après les lignes retenues.</summary>
public sealed class TailFilterRun<T>
{
    private readonly TailFilter _filter;
    private readonly Queue<T> _before = new();
    private int _after;
    private bool _shownAny;
    private bool _gap;

    internal TailFilterRun(TailFilter filter) => _filter = filter;

    /// <summary>
    /// Ajoute une ligne ; <paramref name="output"/> reçoit ce qui doit être affiché (rien, la ligne, ou le contexte
    /// en attente puis la ligne). Une ligne <paramref name="always"/> (repère) est toujours affichée.
    /// </summary>
    public void Feed(T item, string text, bool always, ICollection<TailShown<T>> output)
    {
        if (!_filter.IsActive || always)
        {
            output.Add(new TailShown<T>(item, TailShownKind.Line));
            return;
        }

        if (_filter.Accepts(text))
        {
            if (_gap && _shownAny && _filter.Context > 0)
            {
                output.Add(new TailShown<T>(default, TailShownKind.Separator));
            }

            while (_before.Count > 0)
            {
                output.Add(new TailShown<T>(_before.Dequeue(), TailShownKind.Context));
            }

            output.Add(new TailShown<T>(item, TailShownKind.Line));
            _after = _filter.Context;
            _shownAny = true;
            _gap = false;
        }
        else if (_after > 0)
        {
            output.Add(new TailShown<T>(item, TailShownKind.Context));
            _after--;
        }
        else
        {
            _before.Enqueue(item);
            if (_before.Count > _filter.Context)
            {
                _before.Dequeue();
                _gap = true;
            }
        }
    }
}

/// <summary>
/// Découpe le texte reçu en lignes complètes ; la ligne en cours d'écriture attend sa fin. Une ligne plus longue que
/// <see cref="MaxLineLength"/> est coupée, suivie de la mention <see cref="CutMark"/>, et son reste ignoré jusqu'à sa fin :
/// un fichier qui grossit sans fin de ligne (barre de progression en « \r », fichier piégé) ou des lignes géantes ne
/// remplissent pas la mémoire.
/// </summary>
public sealed class TailLineSplitter
{
    /// <summary>Caractères gardés d'une ligne (ligne en cours comprise) ; au-delà, elle est coupée.</summary>
    public const int MaxLineLength = 8 * 1024;

    // La ligne en cours a été coupée : son reste est ignoré jusqu'à la fin de ligne.
    private bool _cut;

    /// <summary>Début d'une ligne pas encore terminée.</summary>
    public string Partial { get; private set; } = "";

    /// <summary>Mention ajoutée à la fin d'une ligne coupée (visible dans la copie et l'enregistrement).</summary>
    public static string CutMark => string.Format(CultureInfo.CurrentCulture, CoreStrings.TailLineCut, MaxLineLength);

    public IReadOnlyList<string> Push(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        int start = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', start);
            var piece = text.AsSpan(start, (newline < 0 ? text.Length : newline) - start);
            if (_cut)
            {
                // Reste d'une ligne déjà coupée.
            }
            else if (Partial.Length + piece.Length > MaxLineLength)
            {
                var kept = string.Concat(Partial, piece[..(MaxLineLength - Partial.Length)]);
                // Pas de moitié de caractère (emoji…) à la coupure.
                lines.Add((char.IsHighSurrogate(kept[^1]) ? kept[..^1] : kept) + CutMark);
                Partial = "";
                _cut = true;
            }
            else if (newline >= 0)
            {
                lines.Add(string.Concat(Partial, piece));
                Partial = "";
            }
            else
            {
                Partial = string.Concat(Partial, piece);
            }

            if (newline < 0)
            {
                return lines;
            }

            _cut = false;
            start = newline + 1;
        }
    }

    /// <summary>
    /// Rend la ligne en cours comme si elle était terminée (fichier relu, ligne restée sans fin) ; le reste d'une ligne
    /// coupée n'est plus ignoré.
    /// </summary>
    public string? Flush()
    {
        _cut = false;
        if (Partial.Length == 0)
        {
            return null;
        }

        var line = Partial;
        Partial = "";
        return line;
    }
}
