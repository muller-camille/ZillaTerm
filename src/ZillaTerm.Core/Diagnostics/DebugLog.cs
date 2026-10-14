using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZillaTerm.Core.Diagnostics;

/// <summary>
/// Journal de débogage, désactivé par défaut (option du menu Paramètres) : déroulement des connexions PVWA, PSM,
/// Bureau à distance et SSH, pour comprendre un échec. Jamais de mot de passe, de jeton ni de contenu de session :
/// les appelants n'en transmettent pas, et chaque ligne passe en plus par <see cref="Redact"/>. Les noms de serveurs
/// et de comptes y figurent. Une écriture qui échoue est ignorée : le journal ne doit jamais gêner l'application.
/// </summary>
public static partial class DebugLog
{
    /// <summary>Au-delà, le journal est renommé en « .1 » (une génération gardée).</summary>
    public const long MaxSize = 5 * 1024 * 1024;

    public const string Masked = "***";

    private static readonly object Lock = new();
    private static string? _path;

    /// <summary>Emplacement du journal : dossier local de l'utilisateur (non itinérant).</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "ZillaTerm",
        "debug.log");

    public static bool Enabled => Volatile.Read(ref _path) is not null;

    /// <summary>Fichier du journal en cours, ou null s'il est désactivé.</summary>
    public static string? FilePath => Volatile.Read(ref _path);

    /// <summary>Active le journal dans <paramref name="path"/> et y note <paramref name="header"/> (version, système...).</summary>
    public static void Start(string path, string header)
    {
        Volatile.Write(ref _path, Path.GetFullPath(path));
        Write("log", header);
    }

    public static void Stop()
    {
        Write("log", "Journal de débogage désactivé.");
        Volatile.Write(ref _path, null);
    }

    public static void Write(string category, string message)
    {
        if (FilePath is not { } path)
        {
            return;
        }

        var prefix = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {category,-6} ");
        var lines = Clean(Redact(message)).Split('\n');
        var text = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            // Lignes suivantes d'un même message (fichier .rdp, pile d'appels) : décalées sous la première.
            text.Append(i == 0 ? prefix : new string(' ', prefix.Length)).Append(lines[i].TrimEnd('\r')).Append(Environment.NewLine);
        }

        try
        {
            lock (Lock)
            {
                Append(path, new UTF8Encoding(false).GetBytes(text.ToString()));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Journal inaccessible (disque plein, droits) : on continue sans.
        }
    }

    /// <summary>Message suivi de l'exception : type, message, code d'erreur et pile d'appels, exceptions internes comprises.</summary>
    public static void Write(string category, string message, Exception exception)
    {
        if (Enabled)
        {
            Write(category, $"{message}\n{Describe(exception)}");
        }
    }

    public static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (!ReferenceEquals(e, exception))
            {
                text.Append("--- interne : ");
            }

            text.Append(e.GetType().FullName).Append(string.Create(CultureInfo.InvariantCulture, $" (0x{e.HResult:X8}) : ")).Append(e.Message).Append('\n');
            if (e.StackTrace is { } stack)
            {
                text.Append(stack.Replace("\r", "", StringComparison.Ordinal)).Append('\n');
            }
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Masque ce qui ressemble à un secret : demande de session PSM (« PSM@… »), et valeur d'un champ dont le nom
    /// évoque un mot de passe, un jeton, une signature ou un cookie (« password=… », « "token":"…" »…).
    /// </summary>
    public static string Redact(string text)
    {
        text = PsmTicket().Replace(text, "${prefix}" + Masked);
        return SecretField().Replace(text, m => m.Groups["name"].Value + m.Groups["sep"].Value + Masked);
    }

    /// <summary>Valeur qui peut contenir un secret : seulement sa longueur (vide si elle est vide).</summary>
    public static string Hidden(string value) =>
        value.Length == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{Masked} ({value.Length} car.)");

    /// <summary>
    /// Contenu d'un fichier .rdp pour le journal : une ligne par réglage, valeurs secrètes masquées (signature,
    /// mot de passe chiffré, jetons, arguments d'application distante), en plus des règles de <see cref="Redact"/>.
    /// </summary>
    public static string DescribeRdpFile(IReadOnlyDictionary<string, string> values)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var shown = IsSecretRdpKey(name) ? Hidden(value) : value;
            text.Append(name).Append(" = ").Append(shown).Append('\n');
        }

        return Redact(text.ToString().TrimEnd('\n'));
    }

    private static bool IsSecretRdpKey(string name) =>
        name.Contains("password", StringComparison.Ordinal) || name.Contains("signature", StringComparison.Ordinal)
        || name.Contains("token", StringComparison.Ordinal) || name.Contains("cookie", StringComparison.Ordinal)
        || name is "loadbalanceinfo" or "kdcproxyname" or "remoteapplicationcmdline";

    private static void Append(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxSize)
        {
            try
            {
                File.Move(path, path + ".1", overwrite: true);
            }
            catch (IOException)
            {
                // Fichier ouvert ailleurs : on réessaiera à la ligne suivante.
            }
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
    }

    /// <summary>Caractères de contrôle remplacés (sauf retour à la ligne) : une valeur ne peut pas fabriquer de fausse ligne d'en-tête.</summary>
    private static string Clean(string text) =>
        new(text.Replace("\r\n", "\n", StringComparison.Ordinal).Select(c => c != '\n' && char.IsControl(c) ? ' ' : c).ToArray());

    // « PSM@<jeton> » : utilisateur et programme de démarrage des fichiers .rdp du PVWA.
    [GeneratedRegex(@"(?<prefix>\bPSM@)[^\s""';,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsmTicket();

    // Nom du champ (suivi d'un numéro dans un .rdp : « password 51:b:… »), puis valeur entre guillemets (guillemets
    // échappés compris) ou jusqu'au prochain blanc, « , » ou « ; ».
    [GeneratedRegex(@"(?<name>\b(?:password|passwd|pwd|secret|token|authorization|cookie|signature)\w*(?:\s+\d+)?)(?<sep>""?\s*[:=]\s*)(?:""(?:[^""\\]|\\.)*""|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretField();
}
