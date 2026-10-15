using System.Globalization;
using System.Net;
using System.Text;

namespace ZillaTerm.Core.Interventions;

/// <summary>Textes du rapport d'intervention, dans la langue de l'interface.</summary>
public sealed class InterventionReportText
{
    public string Title { get; init; } = "On-call intervention";

    /// <summary>{0} = début, {1} = fin, {2} = durée.</summary>
    public string Period { get; init; } = "From {0} to {1} ({2})";

    public string Unfinished { get; init; } = "Recording interrupted: ZillaTerm stopped before the end of the intervention.";

    public string Damaged { get; init; } = "Part of the journal could not be read or was changed.";

    public string Confidential { get; init; } = "Confidential: this report contains what the terminals showed. It is not encrypted.";

    public string Summary { get; init; } = "Summary";

    public string Timeline { get; init; } = "Timeline";

    public string TimeHeader { get; init; } = "Time";

    public string SourceHeader { get; init; } = "Session";

    public string EventHeader { get; init; } = "Event";

    /// <summary>Nom de chaque nature d'événement.</summary>
    public Func<InterventionKind, string> Kind { get; init; } = k => k.ToString();

    /// <summary>Ligne du résumé : nombre d'événements d'une nature (accord du pluriel).</summary>
    public Func<InterventionKind, int, string> Count { get; init; } = (k, n) => $"{k}: {n}";

    /// <summary>Durée lisible.</summary>
    public Func<TimeSpan, string> Duration { get; init; } = d => d.ToString(@"h\:mm", CultureInfo.InvariantCulture);

    public string Screens { get; init; } = "Recorded screens";

    public string ScreenHelp { get; init; } =
        "Remote desktop sessions in a tab: only the changed areas of the screen are kept, one image per second at most. "
        + "The graph under the cursor shows the share of the screen that changed: click a peak to go there. "
        + "The orange marks below are the notes: click one to go to its time. "
        + "The grey bands are the display pauses (the server no longer sent the picture): the screen shows \"Display paused\".";

    /// <summary>{0} = session, {1} = début, {2} = fin, {3} = taille.</summary>
    public string ScreenHeading { get; init; } = "{0}: {1} to {2} ({3})";

    public string Play { get; init; } = "Play";

    public string Pause { get; init; } = "Pause";

    public string Speed { get; init; } = "Speed";

    public string Activity { get; init; } = "Position and screen activity";

    public string ScriptNeeded { get; init; } = "The player needs JavaScript and a recent browser.";

    /// <summary>Note modifiée : {0} = heure de la dernière modification, {1} = texte d'origine.</summary>
    public string NoteEdited { get; init; } = "Edited on {0}. Original text: {1}";

    /// <summary>Repère d'une note sous le lecteur d'écran : {0} = heure, {1} = texte.</summary>
    public string NoteMarker { get; init; } = "Note at {0}: {1}";

    /// <summary>Écran pendant une pause de l'affichage (le serveur n'envoyait plus l'image).</summary>
    public string Paused { get; init; } = "Display paused";

    /// <summary>Raison d'une pause : poste inactif.</summary>
    public string PausedIdle { get; init; } = "computer idle";

    /// <summary>Raison d'une pause : session que personne ne pouvait voir.</summary>
    public string PausedHidden { get; init; } = "session not shown";

    /// <summary>Repère d'une pause sous le lecteur d'écran : {0} = début, {1} = fin, {2} = raison.</summary>
    public string PauseMarker { get; init; } = "Display paused from {0} to {1} ({2})";

    /// <summary>Images d'écran manquantes (bloc du journal abîmé) : l'écran reste figé jusqu'à l'image clé suivante.</summary>
    public string ScreenGap { get; init; } = "Images missing here (damaged journal): the screen resumes at the next full image.";

    /// <summary>Secrets masqués à l'export : phrase selon leur nombre (accord du pluriel).</summary>
    public Func<int, string> Redacted { get; init; } = n =>
        $"Secrets masked: {n} passage(s) that look like a password, a private key or a token were replaced by [masked]. "
        + "Images (captures, recorded screens) are not masked. The encrypted journal keeps everything.";

    /// <summary>Rapport de plusieurs enregistrements : phrase d'introduction selon leur nombre.</summary>
    public Func<int, string> Parts { get; init; } = n => $"This report brings together {n} recordings:";

    /// <summary>Un des enregistrements réunis : {0} = début, {1} = fin, {2} = durée, {3} = titre (vide s'il n'y en a pas).</summary>
    public string Part { get; init; } = "{0} to {1} ({2}) {3}";
}

/// <summary>
/// Rapport d'une intervention : une page HTML autonome (chronologie, texte des terminaux, captures, écrans enregistrés), à
/// joindre au compte rendu. Tout le texte est échappé et la page interdit les ressources externes et tout script, sauf
/// celui du lecteur des écrans enregistrés, autorisé par son empreinte (le texte des terminaux vient des serveurs).
/// </summary>
public static class InterventionReport
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <param name="zone">Fuseau des heures affichées (celui du poste).</param>
    /// <param name="redacted">
    /// Secrets masqués avant l'export (<see cref="InterventionRedaction"/>) : leur nombre, dit en tête du rapport ; null si
    /// le texte n'a pas été masqué.
    /// </param>
    public static string ToHtml(InterventionRecording recording, InterventionReportText text, TimeZoneInfo zone, CultureInfo culture,
        int? redacted = null)
    {
        // Images d'écran en tuiles : rejouées par le lecteur, pas dans la chronologie. Notes modifiées : leur dernier texte, à
        // leur place.
        var entries = InterventionNotes.Apply(recording.Entries.Where(e => e.Kind != InterventionKind.Screen), out var edits);
        // Enregistrements réunis : les numéros d'enregistrement d'écran ne valent que dans leur journal.
        var screens = recording.Parts.Count > 1
            ? recording.Parts.SelectMany(p => InterventionScreenPlayer.Recordings(p.Entries)).ToList()
            : InterventionScreenPlayer.Recordings(recording.Entries);
        var html = new StringBuilder();
        string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
            .ToString("g", culture);

        string script = screens.Count > 0 ? "; script-src " + InterventionScreenPlayer.ScriptHash : "";
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(Encode(culture.TwoLetterISOLanguageName)).Append("\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'")
            .Append(script).Append("\">\n")
            .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
            .Append("<title>").Append(Encode(Heading(recording, text)));
        if (recording.Start is { } titleStart)
        {
            html.Append(" — ").Append(Encode(Local(titleStart)));
        }

        html.Append("</title>\n<style>\n")
            .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1b1f24;background:#fff}")
            .Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:24px 0 8px}")
            .Append(".note{padding:8px 12px;border-radius:4px;margin:8px 0}.warn{background:#fff4ce}.info{background:#e8f1fb}")
            .Append("table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #d0d7de;padding:6px 8px;text-align:left;vertical-align:top}")
            .Append("th{background:#f3f5f7}td.time{white-space:nowrap;color:#57606a}td.source{white-space:nowrap}")
            .Append("pre{margin:0;padding:6px 8px;background:#1e2227;color:#e6e6e6;white-space:pre-wrap;word-break:break-all;font:12px Consolas,monospace;border-radius:4px}")
            .Append("img{max-width:100%;border:1px solid #d0d7de}.kind{font-weight:600}.edited{color:#57606a;font-size:13px}")
            .Append("@media print{pre{background:#fff;color:#000;border:1px solid #999}}")
            .Append(screens.Count > 0 ? InterventionScreenPlayer.Styles : "")
            .Append("\n</style>\n</head>\n<body>\n");

        html.Append("<h1>").Append(Encode(Heading(recording, text))).Append("</h1>\n");
        if (recording.Start is { } start && recording.End is { } end)
        {
            html.Append("<p>").Append(Encode(string.Format(culture, text.Period, Local(start), Local(end), text.Duration(end - start))))
                .Append("</p>\n");
        }

        html.Append("<p class=\"note info\">").Append(Encode(text.Confidential)).Append("</p>\n");
        if (redacted is { } masked)
        {
            html.Append("<p class=\"note info\">").Append(Encode(text.Redacted(masked))).Append("</p>\n");
        }

        if (recording.Parts.Count > 1)
        {
            html.Append("<p>").Append(Encode(text.Parts(recording.Parts.Count))).Append("</p>\n<ul>\n");
            foreach (var part in recording.Parts)
            {
                var title = part.Title is { } partTitle ? "— " + InterventionJournal.CleanTitle(partTitle) : "";
                html.Append("<li>").Append(Encode(string.Format(culture, text.Part, Local(part.Start!.Value), Local(part.End!.Value),
                    text.Duration(part.End.Value - part.Start.Value), title).TrimEnd())).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        if (recording.Unfinished)
        {
            html.Append("<p class=\"note warn\">").Append(Encode(text.Unfinished)).Append("</p>\n");
        }

        if (recording.Damaged)
        {
            html.Append("<p class=\"note warn\">").Append(Encode(text.Damaged)).Append("</p>\n");
        }

        html.Append("<h2>").Append(Encode(text.Summary)).Append("</h2>\n<ul>\n");
        foreach (var kind in new[] { InterventionKind.Connection, InterventionKind.Action, InterventionKind.Transfer, InterventionKind.Screenshot, InterventionKind.Note, InterventionKind.Terminal })
        {
            int count = entries.Count(e => e.Kind == kind);
            if (count > 0)
            {
                html.Append("<li>").Append(Encode(text.Count(kind, count))).Append("</li>\n");
            }
        }

        html.Append("</ul>\n");
        if (screens.Count > 0)
        {
            InterventionScreenPlayer.Append(html, screens, entries.Where(e => e.Kind == InterventionKind.Note).ToList(), text, Local, zone, culture);
        }

        html.Append("<h2>").Append(Encode(text.Timeline)).Append("</h2>\n<table>\n<tr><th>")
            .Append(Encode(text.TimeHeader)).Append("</th><th>").Append(Encode(text.SourceHeader)).Append("</th><th>")
            .Append(Encode(text.EventHeader)).Append("</th></tr>\n");

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            html.Append("<tr><td class=\"time\">").Append(Encode(Local(entry.Time))).Append("</td><td class=\"source\">")
                .Append(Encode(entry.Source)).Append("</td><td>");
            if (entry.Kind == InterventionKind.Terminal)
            {
                // Lignes successives d'un même terminal : un seul bloc.
                html.Append("<pre>");
                int j = i;
                for (; j < entries.Count && entries[j].Kind == InterventionKind.Terminal && entries[j].Source == entry.Source; j++)
                {
                    html.Append(Encode(entries[j].Text)).Append('\n');
                }

                html.Append("</pre>");
                i = j - 1;
            }
            else
            {
                html.Append("<span class=\"kind\">").Append(Encode(text.Kind(entry.Kind))).Append("</span> ")
                    .Append(Encode(entry.Text).Replace("\n", "<br>", StringComparison.Ordinal));
                if (entry.Kind == InterventionKind.Note && edits.TryGetValue(entry.Time, out var edit))
                {
                    // Le texte d'origine reste lisible : une note modifiée ne cache rien.
                    html.Append("<br><span class=\"edited\">")
                        .Append(Encode(string.Format(culture, text.NoteEdited, Local(edit.EditedAt), edit.OriginalText)).Replace("\n", "<br>", StringComparison.Ordinal))
                        .Append("</span>");
                }
                if (entry.Kind == InterventionKind.Screenshot && entry.Image is { } image && IsPng(image))
                {
                    html.Append("<br><img alt=\"").Append(Encode(entry.Text)).Append("\" src=\"data:image/png;base64,")
                        .Append(Convert.ToBase64String(image)).Append("\">");
                }
            }

            html.Append("</td></tr>\n");
        }

        html.Append("</table>\n</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Titre du rapport, suivi du titre donné à l'intervention s'il y en a un.</summary>
    private static string Heading(InterventionRecording recording, InterventionReportText text) =>
        recording.Title is { } title ? $"{text.Title} — {InterventionJournal.CleanTitle(title)}" : text.Title;

    /// <summary>
    /// Nom proposé pour le rapport : date et heure de début, puis le titre de l'intervention réduit aux lettres, chiffres,
    /// espaces, tirets et soulignés (au plus 60 caractères).
    /// </summary>
    public static string FileName(DateTime localStart, string? title)
    {
        var name = new StringBuilder("intervention-").Append(localStart.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture));
        var part = new StringBuilder();
        foreach (char c in InterventionJournal.CleanTitle(title))
        {
            char kept = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : ' ';
            if (kept != ' ' || (part.Length > 0 && part[^1] != ' '))
            {
                part.Append(kept);
            }
        }

        var clean = part.ToString().Trim();
        if (clean.Length > 60)
        {
            clean = clean[..(char.IsHighSurrogate(clean[59]) ? 59 : 60)].TrimEnd();
        }

        if (clean.Length > 0)
        {
            name.Append(' ').Append(clean);
        }

        return name.Append(".html").ToString();
    }

    private static bool IsPng(byte[] image) => image.AsSpan().StartsWith(PngSignature);

    /// <summary>Texte échappé pour HTML, caractères de contrôle et invisibles remplacés (ils viennent des serveurs).</summary>
    internal static string Encode(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t')
            {
                builder.Append((char)rune.Value);
            }
            else if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format)
            {
                builder.Append('�');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        return WebUtility.HtmlEncode(builder.ToString());
    }
}
