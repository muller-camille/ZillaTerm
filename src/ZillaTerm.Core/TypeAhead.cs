namespace ZillaTerm.Core;

/// <summary>
/// Sélection au clavier dans une liste, comme dans l'Explorateur : les lettres tapées à la suite forment le début du nom
/// cherché ; la même lettre tapée de nouveau passe à l'élément suivant qui commence par elle. Après une pause, une lettre
/// commence une nouvelle recherche, à partir de l'élément qui suit la sélection.
/// </summary>
public sealed class TypeAhead
{
    /// <summary>Pause après laquelle une lettre commence une nouvelle recherche.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    private string _typed = "";
    private DateTime _last;

    /// <summary>
    /// Vrai si un nom est en cours de saisie à <paramref name="now"/> : une espace le prolonge (la liste la prend sinon pour
    /// elle, pour sélectionner l'élément qui a le clavier).
    /// </summary>
    public bool IsTyping(DateTime now) => _typed.Length > 0 && now - _last <= Pause;

    /// <summary>Position de l'élément à sélectionner après la frappe de <paramref name="text"/> ; -1 si aucun nom ne convient.</summary>
    /// <param name="names">Noms affichés, dans l'ordre de la liste.</param>
    /// <param name="current">Position de l'élément sélectionné (-1 : aucun).</param>
    /// <param name="text">Caractères tapés.</param>
    /// <param name="now">Heure de la frappe.</param>
    public int Find(IReadOnlyList<string> names, int current, string text, DateTime now)
    {
        if (now - _last > Pause)
        {
            _typed = "";
        }

        // Une espace ne commence pas de recherche (elle peut faire partie d'un nom déjà commencé).
        if (text.Length == 0 || (_typed.Length == 0 && string.IsNullOrWhiteSpace(text)))
        {
            return -1;
        }

        _last = now;
        if (_typed.Length == 0)
        {
            _typed = text;
            return Search(names, text, current + 1);
        }

        // Suite du nom : l'élément sélectionné reste s'il commence encore ainsi.
        _typed += text;
        int index = Search(names, _typed, current);
        if (index >= 0)
        {
            return index;
        }

        // Même lettre répétée (« sss ») : élément suivant qui commence par cette lettre.
        var first = _typed[..text.Length];
        return Repeats(_typed, first) ? Search(names, first, current + 1) : -1;
    }

    private static bool Repeats(string typed, string unit)
    {
        for (int i = 0; i < typed.Length; i += unit.Length)
        {
            if (!typed.AsSpan(i).StartsWith(unit, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Premier nom qui commence par <paramref name="prefix"/>, à partir de <paramref name="start"/> et en revenant au début.</summary>
    private static int Search(IReadOnlyList<string> names, string prefix, int start)
    {
        int count = names.Count;
        for (int i = 0; i < count; i++)
        {
            int index = (((start + i) % count) + count) % count;
            if (names[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
