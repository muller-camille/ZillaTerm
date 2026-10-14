using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Historique en mémoire de l'intervention en cours, pour l'afficher pendant qu'elle se déroule : les événements gardés
/// par le journal, sans les images des captures. Le texte des terminaux y est plafonné (<see cref="MaxTerminalText"/>) ;
/// au-delà, il n'est plus que dans le journal et le rapport.
/// </summary>
/// <remarks>Les événements arrivent de plusieurs fils : <see cref="Add"/> et <see cref="Since"/> s'appellent de partout.</remarks>
public sealed class InterventionHistory
{
    /// <summary>Texte des terminaux gardé au plus dans l'historique (caractères).</summary>
    public const long DefaultMaxTerminalText = 8L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly List<InterventionEntry> _entries = [];
    private readonly long _maxTerminalText;
    private long _terminalText;
    private bool _terminalCapped;

    public InterventionHistory(long maxTerminalText = DefaultMaxTerminalText)
    {
        _maxTerminalText = maxTerminalText;
    }

    /// <summary>Texte des terminaux gardé au plus (caractères).</summary>
    public long MaxTerminalText => _maxTerminalText;

    /// <summary>Le plafond du texte des terminaux est atteint : les lignes suivantes ne sont plus dans l'historique.</summary>
    public bool TerminalCapped
    {
        get
        {
            lock (_lock)
            {
                return _terminalCapped;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Ajoute un événement (sans son image) ; les images d'écran en tuiles ne sont pas gardées (une par seconde).</summary>
    public void Add(InterventionEntry entry)
    {
        if (entry.Kind == InterventionKind.Screen)
        {
            return;
        }

        lock (_lock)
        {
            if (entry.Kind == InterventionKind.Terminal)
            {
                if (_terminalCapped || _terminalText + entry.Text.Length > _maxTerminalText)
                {
                    _terminalCapped = true;
                    return;
                }

                _terminalText += entry.Text.Length;
            }

            _entries.Add(entry.Image is null ? entry : entry with { Image = null });
        }
    }

    /// <summary>Événements à partir du rang <paramref name="index"/> (ceux arrivés depuis la dernière lecture).</summary>
    public IReadOnlyList<InterventionEntry> Since(int index)
    {
        lock (_lock)
        {
            index = Math.Clamp(index, 0, _entries.Count);
            return _entries.GetRange(index, _entries.Count - index);
        }
    }
}

/// <summary>Bloc de l'historique affiché : un événement, ou une suite de lignes d'un même terminal (une commande et son retour).</summary>
public sealed class InterventionBlock
{
    private readonly List<string> _lines = [];

    internal InterventionBlock(InterventionEntry entry)
    {
        Start = End = entry.Time;
        Kind = entry.Kind;
        Source = InterventionBlockBuilder.Clean(entry.Source);
    }

    /// <summary>Heure (UTC) du premier événement du bloc.</summary>
    public DateTime Start { get; }

    /// <summary>Heure (UTC) du dernier événement du bloc.</summary>
    public DateTime End { get; internal set; }

    public InterventionKind Kind { get; }

    /// <summary>Session ou serveur, caractères invisibles remplacés par « � ».</summary>
    public string Source { get; }

    /// <summary>Lignes du bloc, caractères invisibles (venus des serveurs) remplacés par « � ».</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Note modifiée : heure (UTC) de sa dernière modification ; null sinon.</summary>
    public DateTime? EditedAt { get; private set; }

    /// <summary>Note modifiée : son nouveau texte remplace l'ancien.</summary>
    internal void Replace(string text, DateTime at)
    {
        _lines.Clear();
        Add(text);
        EditedAt = at;
    }

    internal void Add(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            _lines.Add(InterventionBlockBuilder.Clean(line.TrimEnd('\r')));
        }
    }
}

/// <summary>
/// Regroupe les événements de l'historique en blocs à afficher. Les lignes d'un terminal qui se suivent forment un bloc
/// (une commande et son retour) ; un nouveau bloc commence après une pause de la sortie (<see cref="TerminalPause"/>),
/// sauf si le bloc n'a encore qu'une ligne (la commande, dont le retour se fait attendre), à chaque autre événement ou
/// autre session, et au-delà de <see cref="MaxTerminalLines"/> lignes.
/// </summary>
public sealed class InterventionBlockBuilder
{
    /// <summary>Pause de la sortie d'un terminal qui sépare deux commandes.</summary>
    public static readonly TimeSpan TerminalPause = TimeSpan.FromSeconds(2);

    /// <summary>Lignes au plus d'un bloc de terminal (une sortie sans fin continue dans le bloc suivant).</summary>
    public const int MaxTerminalLines = 5000;

    private readonly List<InterventionBlock> _blocks = [];

    public IReadOnlyList<InterventionBlock> Blocks => _blocks;

    /// <summary>
    /// Modification d'une note (<see cref="InterventionEntry.Edits"/>) : le bloc de la note prend le nouveau texte.
    /// </summary>
    /// <returns>Le bloc modifié ; null si l'événement n'est pas une modification ou que la note n'est pas dans l'historique.</returns>
    public InterventionBlock? Edit(InterventionEntry entry)
    {
        if (entry is not { Kind: InterventionKind.Note, Edits: { } original }
            || _blocks.LastOrDefault(b => b.Kind == InterventionKind.Note && b.Start == original) is not { } block)
        {
            return null;
        }

        block.Replace(entry.Text, entry.Time);
        return block;
    }

    /// <summary>Ajoute un événement (une modification de note passe d'abord par <see cref="Edit"/>).</summary>
    /// <returns>Vrai s'il prolonge le dernier bloc, faux s'il en commence un nouveau.</returns>
    public bool Add(InterventionEntry entry)
    {
        if (entry.Kind == InterventionKind.Terminal && _blocks.Count > 0 && _blocks[^1] is var last
            && last.Kind == InterventionKind.Terminal && last.Source == Clean(entry.Source) && last.Lines.Count < MaxTerminalLines
            && (last.Lines.Count == 1 || entry.Time - last.End <= TerminalPause))
        {
            last.Add(entry.Text);
            last.End = entry.Time;
            return true;
        }

        var block = new InterventionBlock(entry);
        block.Add(entry.Text);
        _blocks.Add(block);
        return false;
    }

    /// <summary>
    /// Texte à afficher : caractères de contrôle et de mise en forme invisible (inversion du sens d'écriture…) remplacés
    /// par « � », la tabulation gardée.
    /// </summary>
    internal static string Clean(string text) =>
        text.Any(c => c != '\t' && RemoteEntry.IsInvisible(c))
            ? string.Concat(text.Select(c => c != '\t' && RemoteEntry.IsInvisible(c) ? '�' : c))
            : text;
}
