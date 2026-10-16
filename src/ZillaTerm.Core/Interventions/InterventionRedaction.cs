using System.Buffers;
using System.Text.RegularExpressions;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Secrets masqués dans le rapport exporté (il n'est pas chiffré) : ce qui ressemble à un mot de passe, une clé privée, un
/// jeton ou une empreinte de mot de passe est remplacé par un texte de masquage. Le journal chiffré n'est pas touché.
/// </summary>
/// <remarks>
/// <para>
/// Repéré : les blocs de clé privée (« -----BEGIN … PRIVATE KEY----- », même sur plusieurs lignes de terminal), les
/// valeurs des clés nommées mot de passe, secret, jeton ou clé d'API (« password=… », « "client_secret": "…" »,
/// « --db-password=… »), le mot de passe d'une adresse (« https://jdoe:…@srv01 »), les en-têtes « Bearer … » et
/// « Basic … », les jetons de format connu (JWT, GitHub, GitLab, Slack, clés AWS et Google) et les empreintes de mot de
/// passe (« $6$… » de /etc/shadow).
/// </para>
/// <para>
/// Les images (captures, écrans enregistrés) ne sont pas masquées : le rapport le dit. Un masquage de trop (un mot pris
/// pour une valeur) coûte un peu de lisibilité ; un secret oublié coûte plus, les motifs penchent donc vers le masquage.
/// </para>
/// </remarks>
public static class InterventionRedaction
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Début d'un bloc de clé privée (PEM, OpenSSH, PGP).</summary>
    private static readonly Regex KeyBegin = new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----", RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    private static readonly Regex KeyEnd = new(@"-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----", RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Ligne du corps d'une clé : base64, ou en-tête d'une clé chiffrée (« Proc-Type: », « DEK-Info: »…).</summary>
    private static readonly Regex KeyBody = new(@"^\s*([A-Za-z0-9+/=]{1,100}|[A-Za-z][A-Za-z-]{0,30}:.*)?\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Jetons de format connu, n'importe où dans la ligne.</summary>
    private static readonly Regex Tokens = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
        + @"|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,})"
        + @"|\b(?:AKIA|ASIA)[0-9A-Z]{16}\b|\bAIza[0-9A-Za-z_-]{35}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Empreinte de mot de passe (crypt : /etc/shadow, htpasswd).</summary>
    private static readonly Regex PasswordHash = new(@"\$(?:1|2[abxy]?|5|6|y|gy|7|sha1|md5|apr1)\$[^\s:]{8,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Mot de passe d'une adresse : « schéma://utilisateur:mot de passe@hôte ».</summary>
    private static readonly Regex UrlPassword = new(@"(?<=\b[A-Za-z][A-Za-z0-9+.-]{1,15}://[^\s:/@]{1,256}:)[^\s@/]+(?=@)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>En-tête d'authentification : « Bearer … », « Basic … ».</summary>
    private static readonly Regex Authorization = new(@"(?<=\b(?:[Bb]earer|BEARER|[Bb]asic|BASIC)\s{1,3})[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>
    /// Valeur d'une clé nommée mot de passe, secret, jeton ou clé d'API, avec « = » ou « : » : entre guillemets, jusqu'au
    /// guillemet fermant (espaces compris) ; sinon jusqu'à une espace ou un séparateur. Le nom doit finir par le mot
    /// repéré : « db_password » oui, « password_file » non.
    /// </summary>
    private static readonly Regex Assignment = new(
        @"(?<key>(?<![\w.-])[\w.-]{0,64}?(?:passw(?:or)?d|pwd|passphrase|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)s?)"
        + @"(?<sep>[""']?\s{0,3}[:=]\s{0,3})(?:(?<q>"")(?<value>[^""\r\n]+)|(?<q>')(?<value>[^'\r\n]+)|(?<value>[^\s""'`,;&|<>]+))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled, Timeout);

    // Tri rapide avant les expressions : la plupart des lignes de terminal n'ont aucun de ces mots et sont gardées telles
    // quelles sans autre examen (un journal de 60 Mo se masque en quelques secondes).
    private static readonly SearchValues<string> TokenHints = SearchValues.Create(
        ["eyJ", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "glpat-", "xox", "AKIA", "ASIA", "AIza"], StringComparison.Ordinal);

    private static readonly SearchValues<string> AssignmentHints = SearchValues.Create(
        ["pass", "pwd", "secret", "token", "key"], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> AuthorizationHints = SearchValues.Create(["bearer", "basic"], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Variable ou gabarit à la place d'une valeur : « $DB_PASS », « ${DB_PASS} », « $(cat secret) », « %DB_PASS% »,
    /// « {{ vault_pass }} ». Un mot de passe qui commence par « $ » ou « % » (« $ecr3t!Prod », « %Xk9!pq ») n'en est pas un.
    /// </summary>
    private static readonly Regex Variable = new(@"^(?:\$[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Timeout);

    /// <summary>Valeurs qui ne sont pas des secrets : vides de sens, déjà masquées, variables, gabarits.</summary>
    private static readonly HashSet<string> NotSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "none", "nil", "true", "false", "yes", "no", "on", "off", "required", "optional", "empty", "undefined",
    };

    /// <summary>
    /// Enregistrement dont le texte est masqué (tous les événements, et chaque enregistrement réuni dans un rapport) ; les
    /// images ne changent pas.
    /// </summary>
    /// <param name="mask">Texte mis à la place d'un secret (« [masqué] »).</param>
    /// <param name="count">Passages masqués.</param>
    public static InterventionRecording Apply(InterventionRecording recording, string mask, out int count)
    {
        if (recording.Parts.Count > 1)
        {
            int total = 0;
            var parts = recording.Parts.Select(p =>
            {
                var part = Apply(p, mask, out int n);
                total += n;
                return part;
            }).ToList();
            count = total;
            return InterventionRecording.Merge(parts);
        }

        var redactor = new Redactor(mask);
        var entries = new List<InterventionEntry>(recording.Entries.Count);
        // Bloc de clé privée en cours, par terminal : ses lignes arrivent une à une.
        var keys = new Dictionary<string, KeyState>(StringComparer.Ordinal);
        foreach (var entry in recording.Entries)
        {
            if (entry.Kind == InterventionKind.Screen)
            {
                entries.Add(entry);
                continue;
            }

            if (entry.Kind == InterventionKind.Terminal)
            {
                var state = keys.GetValueOrDefault(entry.Source);
                var line = redactor.Line(entry.Text, ref state);
                keys[entry.Source] = state;
                if (line is not null)
                {
                    entries.Add(line == entry.Text ? entry : entry with { Text = line });
                }

                continue;
            }

            var text = redactor.Text(entry.Text);
            entries.Add(text == entry.Text ? entry : entry with { Text = text });
        }

        count = redactor.Count;
        return recording with { Entries = entries };
    }

    /// <summary>Texte de plusieurs lignes masqué (note, transfert…).</summary>
    internal static string Redact(string text, string mask, out int count)
    {
        var redactor = new Redactor(mask);
        var result = redactor.Text(text);
        count = redactor.Count;
        return result;
    }

    /// <summary>Bloc de clé privée en cours dans un terminal : lignes du corps déjà vues (la première est masquée).</summary>
    private struct KeyState
    {
        public bool Open;
        public int Lines;
    }

    private sealed class Redactor(string mask)
    {
        public int Count;

        public string Text(string text)
        {
            if (!text.Contains('\n', StringComparison.Ordinal))
            {
                var state = default(KeyState);
                return Line(text, ref state) ?? mask;
            }

            var lines = new List<string>();
            var key = default(KeyState);
            foreach (var line in text.Split('\n'))
            {
                if (Line(line, ref key) is { } kept)
                {
                    lines.Add(kept);
                }
            }

            return string.Join('\n', lines);
        }

        /// <summary>Ligne masquée ; null : ligne du corps d'une clé privée, retirée (la première est remplacée par le masque).</summary>
        public string? Line(string line, ref KeyState key)
        {
            try
            {
                if (key.Open)
                {
                    if (KeyEnd.IsMatch(line))
                    {
                        key = default;
                        return line;
                    }

                    // Corps de la clé : du base64 (au plus quelques centaines de lignes). Autre chose (l'invite qui revient
                    // après une clé coupée) : la clé est finie.
                    if (key.Lines < 500 && KeyBody.IsMatch(line))
                    {
                        return key.Lines++ == 0 ? mask : null;
                    }

                    key = default;
                }

                if (line.Contains("PRIVATE KEY", StringComparison.Ordinal) && KeyBegin.Match(line) is { Success: true } begin)
                {
                    // L'en-tête reste : il dit ce qui a été masqué. Une clé sur une seule ligne (fin sur la même ligne) est
                    // masquée entre les deux.
                    Count++;
                    int bodyStart = begin.Index + begin.Length;
                    if (KeyEnd.Match(line, bodyStart) is { Success: true } end)
                    {
                        return line[..bodyStart] + mask + line[end.Index..];
                    }

                    key = new KeyState { Open = true };
                    return line[..bodyStart] + (bodyStart < line.Length && line[bodyStart..].Trim().Length > 0 ? mask : "");
                }

                return Inline(line);
            }
            catch (RegexMatchTimeoutException)
            {
                // Ligne trop longue ou trop retorse pour être examinée à temps : masquée entière.
                Count++;
                return mask;
            }
        }

        private string Inline(string line)
        {
            if (line.AsSpan().ContainsAny(TokenHints))
            {
                line = Replace(Tokens, line);
            }

            if (line.Contains('$', StringComparison.Ordinal))
            {
                line = Replace(PasswordHash, line);
            }

            if (line.Contains("://", StringComparison.Ordinal) && line.Contains('@', StringComparison.Ordinal))
            {
                line = Replace(UrlPassword, line);
            }

            if (line.AsSpan().ContainsAny(AuthorizationHints))
            {
                line = Authorization.Replace(line, m =>
                {
                    // « Basic authentication » : un mot, pas des identifiants. Un jeton a des chiffres ou est long.
                    if (m.Value.Length < 20 && !m.Value.Any(c => char.IsAsciiDigit(c) || c is '=' or '+' or '/'))
                    {
                        return m.Value;
                    }

                    Count++;
                    return mask;
                });
            }

            if (!line.AsSpan().ContainsAny(AssignmentHints))
            {
                return line;
            }

            return Assignment.Replace(line, m =>
            {
                var value = m.Groups["value"].Value;
                if (!IsSecret(m.Groups["key"].Value, m.Groups["sep"].Value, m.Groups["q"].Value, value, line, m.Index + m.Length))
                {
                    return m.Value;
                }

                Count++;
                return m.Value[..(m.Groups["value"].Index - m.Index)] + mask;
            });
        }

        private string Replace(Regex regex, string line) => regex.Replace(line, _ =>
        {
            Count++;
            return mask;
        });

        /// <summary>Variable du shell ou de Windows, substitution de commande, gabarit (voir <see cref="Variable"/>).</summary>
        private static bool IsVariable(string value) =>
            value.StartsWith("${", StringComparison.Ordinal) || value.StartsWith("$(", StringComparison.Ordinal)
            || value.StartsWith("{{", StringComparison.Ordinal) || Variable.IsMatch(value);

        private bool IsSecret(string key, string separator, string quote, string value, string line, int end)
        {
            if (value.Contains(mask, StringComparison.Ordinal) || NotSecrets.Contains(value)
                || value.All(c => c is '*' or 'x' or 'X' or '•' or '.' or '-')
                || IsVariable(value))
            {
                // Déjà masqué, valeur sans contenu, ou variable / gabarit (« $DB_PASSWORD », « {{ vault_pass }} »).
                return false;
            }

            if (key.Equals("pwd", StringComparison.OrdinalIgnoreCase) || key.Equals("oldpwd", StringComparison.OrdinalIgnoreCase))
            {
                // Variables du shell (« PWD=/home/jdoe ») ; « Pwd=… ; » d'une chaîne de connexion reste masqué.
                return value[0] is not ('/' or '~');
            }

            // « passwd: all authentication tokens updated » : une phrase, pas une valeur. Une valeur suivie d'un
            // commentaire (« password: s3cr3t # prod ») reste masquée.
            return quote.Length > 0 || !separator.TrimEnd().EndsWith(':') || end >= line.Length || line[end] != ' '
                   || end + 1 >= line.Length || !char.IsLetter(line[end + 1]);
        }
    }
}
