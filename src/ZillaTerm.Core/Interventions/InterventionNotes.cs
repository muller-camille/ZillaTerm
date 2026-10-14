namespace ZillaTerm.Core.Interventions;

/// <summary>Note modifiée : son texte d'origine et l'heure (UTC) de sa dernière modification.</summary>
public sealed record InterventionNoteEdit(string OriginalText, DateTime EditedAt);

/// <summary>
/// Notes modifiées pendant l'intervention. Le journal n'est jamais réécrit : une modification s'y ajoute comme une note
/// qui désigne, par son heure, la note qu'elle modifie (<see cref="InterventionEntry.Edits"/>).
/// </summary>
public static class InterventionNotes
{
    /// <summary>
    /// Applique les modifications : chaque note garde sa place et son heure, avec son dernier texte, et les modifications
    /// ne sont plus des événements à part. Une modification dont la note manque (bloc abîmé) reste une note, à son heure.
    /// </summary>
    /// <param name="edits">Notes modifiées, par heure de la note : texte d'origine et dernière modification.</param>
    public static List<InterventionEntry> Apply(IEnumerable<InterventionEntry> entries, out Dictionary<DateTime, InterventionNoteEdit> edits)
    {
        edits = [];
        var result = new List<InterventionEntry>();
        var notes = new Dictionary<DateTime, int>();
        foreach (var entry in entries)
        {
            if (entry is { Kind: InterventionKind.Note, Edits: { } original } && notes.TryGetValue(original, out int index))
            {
                edits[original] = new InterventionNoteEdit(edits.TryGetValue(original, out var earlier) ? earlier.OriginalText : result[index].Text, entry.Time);
                result[index] = result[index] with { Text = entry.Text };
                continue;
            }

            if (entry.Kind == InterventionKind.Note)
            {
                notes.TryAdd(entry.Time, result.Count);
            }

            result.Add(entry.Edits is null ? entry : entry with { Edits = null });
        }

        return result;
    }
}
