using System.Text;

namespace ZillaTerm.Core.Terminal;

/// <summary>Palette de couleurs du terminal : 16 couleurs de base, texte, fond, curseur.</summary>
public sealed class TerminalTheme
{
    private TerminalTheme(string id, string name, uint foreground, uint background, uint cursor, uint[] base16)
    {
        Id = id;
        Name = name;
        Foreground = foreground;
        Background = background;
        Cursor = cursor;
        Base16 = base16;
    }

    /// <summary>Identifiant enregistré dans les paramètres.</summary>
    public string Id { get; }

    /// <summary>Nom affiché dans les Paramètres.</summary>
    public string Name { get; }

    public override string ToString() => Name;

    public uint Foreground { get; }

    public uint Background { get; }

    public uint Cursor { get; }

    public IReadOnlyList<uint> Base16 { get; }

    /// <summary>Fond clair : la sélection et la recherche utilisent des couleurs adaptées.</summary>
    public bool IsLight => Luminance(Background) > 0.5;

    /// <summary>« Campbell », celle de Windows Terminal (par défaut).</summary>
    public static TerminalTheme Campbell { get; } = new("campbell", "Campbell (Windows Terminal)", 0xCCCCCC, 0x0C0C0C, 0xD0D0D0,
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ]);

    public static TerminalTheme OneHalfDark { get; } = new("one-half-dark", "One Half Dark", 0xDCDFE4, 0x282C34, 0xA3B3CC,
    [
        0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4,
        0x5A6374, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4,
    ]);

    public static TerminalTheme SolarizedDark { get; } = new("solarized-dark", "Solarized Dark", 0x839496, 0x002B36, 0x93A1A1,
    [
        0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
        0x586E75, 0xCB4B16, 0x93A1A1, 0x839496, 0x657B83, 0x6C71C4, 0x93A1A1, 0xFDF6E3,
    ]);

    public static TerminalTheme OneHalfLight { get; } = new("one-half-light", "One Half Light", 0x383A42, 0xFAFAFA, 0x4F525D,
    [
        0x383A42, 0xE45649, 0x50A14F, 0xC18401, 0x0184BC, 0xA626A4, 0x0997B3, 0xFAFAFA,
        0x4F525D, 0xDF6C75, 0x98C379, 0xE4C07A, 0x61AFEF, 0xC577DD, 0x56B5C1, 0xFFFFFF,
    ]);

    public static TerminalTheme SolarizedLight { get; } = new("solarized-light", "Solarized Light", 0x657B83, 0xFDF6E3, 0x586E75,
    [
        0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
        0x586E75, 0xCB4B16, 0x93A1A1, 0x839496, 0x657B83, 0x6C71C4, 0x93A1A1, 0xFDF6E3,
    ]);

    public static IReadOnlyList<TerminalTheme> All { get; } = [Campbell, OneHalfDark, SolarizedDark, OneHalfLight, SolarizedLight];

    /// <summary>Palette enregistrée ; inconnue ou vide : la palette par défaut.</summary>
    public static TerminalTheme Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Campbell;

    /// <summary>Valeur 0xRRGGBB à afficher. Le gras éclaircit les 8 couleurs de base, comme xterm.</summary>
    public uint ToRgb(int color, bool foreground, bool bold = false)
    {
        if (color == TerminalColor.Default)
        {
            return foreground ? (bold ? Brighten(Foreground) : Foreground) : Background;
        }

        return color is >= 0 and < 16
            ? Base16[foreground && bold && color < 8 ? color + 8 : color]
            : TerminalColor.ToRgb(color, foreground, bold);
    }

    private static uint Brighten(uint rgb) =>
        IsLightColor(rgb) ? rgb : (Channel(rgb, 16, 1.25) << 16) | (Channel(rgb, 8, 1.25) << 8) | Channel(rgb, 0, 1.25);

    private static uint Channel(uint rgb, int shift, double factor) => (uint)Math.Min(255, (rgb >> shift & 0xFF) * factor);

    private static bool IsLightColor(uint rgb) => Luminance(rgb) > 0.85;

    private static double Luminance(uint rgb) =>
        ((0.299 * (rgb >> 16 & 0xFF)) + (0.587 * (rgb >> 8 & 0xFF)) + (0.114 * (rgb & 0xFF))) / 255;
}

/// <summary>
/// Occurrence trouvée dans le terminal : ligne (historique compris, négative), colonne et longueur en cases (un caractère
/// large en occupe deux). Dans une ligne longue repliée automatiquement, l'occurrence peut continuer sur les lignes
/// suivantes : sa longueur dépasse alors la fin de sa première ligne.
/// </summary>
public readonly record struct TerminalMatch(int Row, int Column, int Length);

/// <summary>
/// Recherche dans le terminal, historique compris ; contenu entier pour l'enregistrer. Une ligne repliée automatiquement
/// (<see cref="TerminalEmulator.IsWrapped(int)"/>) et sa suite forment une seule ligne de texte.
/// </summary>
public static class TerminalSearch
{
    /// <summary>Occurrences de <paramref name="text"/> (sans tenir compte de la casse), de la plus ancienne à la plus récente.</summary>
    public static IReadOnlyList<TerminalMatch> Find(TerminalEmulator emulator, string text)
    {
        var matches = new List<TerminalMatch>();
        if (text.Length == 0)
        {
            return matches;
        }

        // Colonnes de chaque caractère du texte de la ligne : un caractère large en occupe deux, une marque combinante aucune.
        var builder = new StringBuilder();
        var starts = new List<int>();
        var ends = new List<int>();
        var widths = new List<int>();
        for (int row = -emulator.ScrollbackCount; row < emulator.Rows;)
        {
            int first = row;
            row = LineText(emulator, row, builder, starts, ends, widths);
            var line = builder.ToString();
            for (int at = line.IndexOf(text, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = at + 1 < line.Length ? line.IndexOf(text, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
            {
                int end = Math.Min(at + text.Length, line.Length) - 1;

                // Ligne et colonne de la première case, dans la ligne repliée où elle se trouve.
                int matchRow = first;
                int column = starts[at];
                for (int i = 0; column >= widths[i]; i++)
                {
                    column -= widths[i];
                    matchRow++;
                }

                matches.Add(new TerminalMatch(matchRow, column, ends[end] - starts[at]));
            }
        }

        return matches;
    }

    /// <summary>Tout le contenu (historique puis écran), espaces et lignes vides de fin retirés.</summary>
    public static string AllText(TerminalEmulator emulator)
    {
        var lines = new List<string>();
        var builder = new StringBuilder();
        for (int row = -emulator.ScrollbackCount; row < emulator.Rows;)
        {
            row = LineText(emulator, row, builder, null, null, null);
            lines.Add(builder.ToString().TrimEnd());
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Texte de la ligne qui commence à <paramref name="row"/>, suivi de celui des lignes suivantes tant qu'elle est
    /// repliée (espaces de fin compris) ; rend la ligne qui vient après. Pour chacun de ses caractères,
    /// <paramref name="starts"/> reçoit la colonne de sa case et <paramref name="ends"/> la colonne qui suit cette case,
    /// comptées depuis le début de la première ligne ; <paramref name="widths"/> reçoit la largeur de chaque ligne.
    /// </summary>
    private static int LineText(TerminalEmulator emulator, int row, StringBuilder text, List<int>? starts, List<int>? ends,
        List<int>? widths)
    {
        text.Clear();
        starts?.Clear();
        ends?.Clear();
        widths?.Clear();
        int offset = 0;
        while (true)
        {
            var cells = emulator.GetLine(row++);
            for (int col = 0; col < cells.Length; col++)
            {
                int length = text.Length;
                emulator.AppendText(text, cells[col]);
                int end = col + 1 < cells.Length && cells[col + 1].IsWideTail ? col + 2 : col + 1;
                for (int i = length; i < text.Length; i++)
                {
                    starts?.Add(offset + col);
                    ends?.Add(offset + end);
                }
            }

            widths?.Add(cells.Length);
            offset += cells.Length;
            if (row >= emulator.Rows || !TerminalEmulator.IsWrapped(cells))
            {
                return row;
            }
        }
    }
}
