using System.Globalization;
using System.Text;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Rappel pendant une intervention (« 30 Escalader au N3 ») : <see cref="Minutes"/> après le début de l'enregistrement,
/// ZillaTerm le montre et le note au journal. Réglé par l'équipe (fichier d'environnement) ou dans les Paramètres.
/// </summary>
public sealed record InterventionReminder(int Minutes, string Text)
{
    public const int MaxReminders = 10;

    /// <summary>Un jour : au-delà, ce n'est plus la même intervention.</summary>
    public const int MaxMinutes = 24 * 60;

    public const int MaxText = 200;

    /// <summary>
    /// Rappel acceptable : de 1 à <see cref="MaxMinutes"/> minutes, un texte d'une ligne de <see cref="MaxText"/> caractères
    /// au plus, sans caractère de contrôle ni caractère invisible qui changerait le sens de lecture.
    /// </summary>
    public static bool IsValid(InterventionReminder? reminder) =>
        reminder is { Minutes: >= 1 and <= MaxMinutes, Text: { } text }
        && text.Trim().Length is > 0 and <= MaxText
        && text.EnumerateRunes().All(r => !Rune.IsControl(r) && Rune.GetUnicodeCategory(r) != UnicodeCategory.Format);

    /// <summary>Rappels gardés : valables, texte sans espaces autour, par heure, sans doublon, <see cref="MaxReminders"/> au plus.</summary>
    public static List<InterventionReminder> Sanitize(IEnumerable<InterventionReminder?>? reminders) =>
        (reminders ?? [])
            .Where(IsValid)
            .Select(r => new InterventionReminder(r!.Minutes, r.Text.Trim()))
            .Distinct()
            .OrderBy(r => r.Minutes)
            .Take(MaxReminders)
            .ToList();

    /// <summary>Texte des Paramètres : une ligne par rappel, les minutes puis le texte.</summary>
    public static string Format(IEnumerable<InterventionReminder> reminders) =>
        string.Join("\n", reminders.Select(r => r.Minutes.ToString(CultureInfo.InvariantCulture) + " " + r.Text));

    /// <summary>
    /// Lit le texte des Paramètres : une ligne par rappel, les minutes puis le texte (« 30 Escalader au N3 ») ; les lignes
    /// vides sont ignorées.
    /// </summary>
    /// <param name="badLine">Première ligne refusée (numérotée à partir de 1), ou 0.</param>
    public static bool TryParse(string text, out List<InterventionReminder> reminders, out int badLine)
    {
        reminders = [];
        badLine = 0;
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int space = line.IndexOfAny([' ', '\t']);
            var reminder = space > 0 && int.TryParse(line[..space], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                ? new InterventionReminder(minutes, line[(space + 1)..].Trim())
                : null;
            if (!IsValid(reminder) || reminders.Count >= MaxReminders)
            {
                badLine = i + 1;
                reminders = [];
                return false;
            }

            reminders.Add(reminder!);
        }

        reminders = Sanitize(reminders);
        return true;
    }
}
