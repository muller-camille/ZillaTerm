using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.Core.Interventions;

/// <summary>Nature d'un événement du journal d'intervention.</summary>
public enum InterventionKind
{
    /// <summary>Début de l'enregistrement (utilisateur, poste, version).</summary>
    Started,

    /// <summary>Fin de l'enregistrement.</summary>
    Stopped,

    /// <summary>Note saisie par l'utilisateur.</summary>
    Note,

    /// <summary>Ligne affichée par un terminal SSH (commande tapée et son écho, ou sortie d'une commande).</summary>
    Terminal,

    /// <summary>Session ouverte, fermée ou en échec (SSH, fichiers, PSM, Bureau à distance, VNC).</summary>
    Connection,

    /// <summary>Action sur un compte ou sur ZillaTerm (mot de passe copié, CPM, accès d'urgence…).</summary>
    Action,

    /// <summary>Transfert de fichiers, avec les empreintes SHA-256.</summary>
    Transfer,

    /// <summary>Capture d'écran (image PNG).</summary>
    Screenshot,

    /// <summary>Titre donné à l'intervention (ticket, incident…) ; le dernier compte.</summary>
    Title,
}

/// <summary>Événement du journal d'intervention.</summary>
/// <param name="Time">Heure UTC.</param>
/// <param name="Source">Session ou serveur concerné ; vide pour ZillaTerm lui-même.</param>
/// <param name="Image">Capture d'écran (PNG), pour <see cref="InterventionKind.Screenshot"/>.</param>
public sealed record InterventionEntry(DateTime Time, InterventionKind Kind, string Source, string Text, byte[]? Image = null);

/// <summary>Intervention relue : ses événements ; un bloc illisible, d'un autre journal ou déplacé la marque abîmée.</summary>
/// <param name="Unfinished">Pas d'événement de fin : ZillaTerm s'est arrêté pendant l'enregistrement.</param>
public sealed record InterventionRecording(string Path, IReadOnlyList<InterventionEntry> Entries, bool Damaged, bool Unfinished)
{
    public DateTime? Start => Entries.Count > 0 ? Entries[0].Time : null;

    public DateTime? End => Entries.Count > 0 ? Entries[^1].Time : null;

    /// <summary>Dernier titre donné à l'intervention ; null s'il n'y en a pas ou s'il a été effacé.</summary>
    public string? Title => Entries.LastOrDefault(e => e.Kind == InterventionKind.Title) is { Text.Length: > 0 } title ? title.Text : null;
}

/// <summary>Intervention enregistrée (fichier), pour la liste des interventions précédentes.</summary>
/// <param name="Title">Titre de l'intervention (voir <see cref="InterventionJournal.ReadTitle"/>) ; null s'il n'y en a pas.</param>
public sealed record InterventionFile(string Path, DateTime Start, long Length, string? Title = null);

/// <summary>
/// Journal d'une intervention : ce qui s'est passé pendant l'enregistrement (texte des terminaux, connexions, actions,
/// transferts, captures), pour s'en souvenir et rédiger le compte rendu.
/// </summary>
/// <remarks>
/// <para>
/// Le fichier est une suite de blocs chiffrés ajoutés à la fin (rien n'est réécrit) : un en-tête, puis pour chaque bloc
/// sa longueur (4 octets) et son contenu protégé par <see cref="ISecretProtector"/> (DPAPI, utilisateur Windows seul).
/// Un bloc contient des événements JSON, l'identifiant du journal et son numéro d'ordre : un bloc d'un autre journal,
/// déplacé ou retiré est signalé à la lecture. Les événements arrivent de plusieurs fils : ils sont gardés en mémoire et
/// écrits ensemble au plus tard toutes les <see cref="FlushInterval"/>, en blocs bornés (<see cref="MaxWrittenChunk"/>).
/// </para>
/// <para>
/// Le texte des terminaux est plafonné (<see cref="MaxTerminalText"/>) : au-delà, une ligne le signale et le reste n'est
/// plus gardé (une commande qui affiche des gigaoctets ne remplit pas le disque).
/// </para>
/// </remarks>
public sealed class InterventionJournal : IDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>Texte des terminaux gardé au plus par intervention (caractères).</summary>
    public const long MaxTerminalText = 64L * 1024 * 1024;

    /// <summary>Longueur au plus du titre d'une intervention (caractères).</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Plus grand bloc lu (une capture d'écran en 4K tient largement).</summary>
    private const int MaxChunk = 64 * 1024 * 1024;

    /// <summary>
    /// Taille au plus d'un bloc écrit, estimée large (<see cref="EstimatedSize"/>) : bien en deçà de <see cref="MaxChunk"/>,
    /// même quand une commande affiche des centaines de milliers de lignes entre deux écritures.
    /// </summary>
    internal const int MaxWrittenChunk = 16 * 1024 * 1024;

    private static readonly byte[] Magic = "ZTINTV1\n"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ISecretProtector _protector;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly object _lock = new();
    private readonly List<InterventionEntry> _pending = [];
    private readonly Timer _timer;
    private FileStream? _file;
    private long _sequence;
    private readonly long _terminalTextLimit;
    private long _terminalText;
    private bool _terminalCapped;

    private InterventionJournal(string path, FileStream file, ISecretProtector protector, long terminalTextLimit)
    {
        Path = path;
        _file = file;
        _protector = protector;
        _terminalTextLimit = terminalTextLimit;
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public string Path { get; }

    /// <summary>Nombre d'événements écrits ou en attente.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Le disque a refusé une écriture : les événements suivants sont perdus, ils ne sont plus ni gardés en mémoire ni
    /// comptés.
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <summary>Heure (UTC) du premier événement perdu, depuis laquelle plus rien n'est gardé (voir <see cref="Failure"/>).</summary>
    public DateTime? FailedAt
    {
        get
        {
            lock (_lock)
            {
                return _failedAt;
            }
        }
    }

    private DateTime? _failedAt;

    /// <summary>Le disque vient de refuser une écriture (<see cref="Failure"/>) ; levé une fois, depuis le fil qui écrivait.</summary>
    public event Action? Failed;

    /// <summary>Dossier des interventions de l'utilisateur.</summary>
    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZillaTerm", "Interventions");

    /// <summary>Commence une intervention : nouveau fichier, nommé d'après l'heure de début.</summary>
    /// <param name="terminalTextLimit">Texte des terminaux gardé au plus (caractères).</param>
    public static InterventionJournal Start(string directory, ISecretProtector protector, DateTime now, long terminalTextLimit = MaxTerminalText)
    {
        Directory.CreateDirectory(directory);
        string name = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(directory, $"intervention-{name}.zij");
        for (int i = 2; File.Exists(path); i++)
        {
            path = System.IO.Path.Combine(directory, $"intervention-{name}-{i}.zij");
        }

        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(Magic);
        file.Flush(flushToDisk: true);
        return new InterventionJournal(path, file, protector, terminalTextLimit);
    }

    /// <summary>Ajoute un événement (depuis n'importe quel fil).</summary>
    /// <returns>Faux si l'événement n'est pas gardé : journal fermé ou refusé par le disque, ou texte des terminaux plafonné.</returns>
    public bool Write(InterventionEntry entry)
    {
        lock (_lock)
        {
            if (_file is null || Failure is not null)
            {
                return false;
            }

            if (entry.Kind == InterventionKind.Terminal)
            {
                if (_terminalCapped)
                {
                    return false;
                }

                _terminalText += entry.Text.Length;
                if (_terminalText > _terminalTextLimit)
                {
                    _terminalCapped = true;
                    entry = entry with { Text = "…" };
                }
            }

            _pending.Add(entry);
            Count++;
            return true;
        }
    }

    /// <summary>Le plafond du texte des terminaux est atteint : plus aucune ligne n'est gardée.</summary>
    public bool TerminalTextCapped
    {
        get
        {
            lock (_lock)
            {
                return _terminalCapped;
            }
        }
    }

    /// <summary>Écrit ce qui attend (en blocs chiffrés bornés), jusque sur le disque.</summary>
    public void Flush()
    {
        Action? failed = null;
        lock (_lock)
        {
            if (_file is null || _pending.Count == 0 || Failure is not null)
            {
                return;
            }

            var pending = _pending.ToList();
            _pending.Clear();
            try
            {
                // Plusieurs blocs si besoin : un bloc trop gros ne se relirait pas, ni ce qui le suit.
                for (int start = 0; start < pending.Count;)
                {
                    int end = start + 1;
                    long size = EstimatedSize(pending[start]);
                    while (end < pending.Count && size + EstimatedSize(pending[end]) <= MaxWrittenChunk)
                    {
                        size += EstimatedSize(pending[end++]);
                    }

                    WriteChunk(new Chunk(_id, _sequence, pending.GetRange(start, end - start)));
                    start = end;
                }

                _file.Flush(flushToDisk: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Failure = e;
                _failedAt = pending.Min(entry => entry.Time);
                failed = Failed;
            }
        }

        // Hors du verrou : l'interface relit l'état du journal.
        failed?.Invoke();
    }

    private void WriteChunk(Chunk chunk)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(chunk, JsonOptions);
        byte[] protectedBytes = _protector.Protect(plain);
        Array.Clear(plain);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, protectedBytes.Length);
        _file!.Write(length);
        _file.Write(protectedBytes);
        _sequence++;
    }

    /// <summary>
    /// Taille au plus d'un événement en JSON : chaque caractère peut s'écrire « \uXXXX » (6 octets), l'image en base64.
    /// </summary>
    internal static long EstimatedSize(InterventionEntry entry) =>
        128 + (6L * ((entry.Source?.Length ?? 0) + (entry.Text?.Length ?? 0))) + (((entry.Image?.LongLength ?? 0) + 2) / 3 * 4);

    /// <summary>Termine l'intervention : dernier événement, écriture, fermeture du fichier.</summary>
    public void Stop(InterventionEntry last)
    {
        Write(last);
        Dispose();
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>
    /// Titre saisi pour une intervention : une ligne, sans caractère de contrôle ni de mise en forme invisible (inversion du
    /// sens d'écriture…), espaces réunis, au plus <see cref="MaxTitleLength"/> caractères.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var clean = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            bool space = char.IsWhiteSpace(c) || Ssh.RemoteEntry.IsInvisible(c);
            if (!space)
            {
                clean.Append(c);
            }
            else if (clean.Length > 0 && clean[^1] != ' ')
            {
                clean.Append(' ');
            }
        }

        var text = clean.ToString().TrimEnd();
        if (text.Length > MaxTitleLength)
        {
            // Pas de demi-paire de substitution en fin de titre.
            int length = char.IsHighSurrogate(text[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
            text = text[..length].TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// Fichier du titre d'une intervention, à côté de son journal : pour lister les interventions avec leur titre sans
    /// relire chaque journal. Le journal garde le titre lui aussi (événement <see cref="InterventionKind.Title"/>), c'est
    /// lui qui fait foi pour le rapport.
    /// </summary>
    public static string TitlePath(string journalPath) => System.IO.Path.ChangeExtension(journalPath, ".title");

    /// <summary>Enregistre le titre d'une intervention, protégé comme le journal ; vide : fichier du titre retiré.</summary>
    /// <exception cref="IOException">Écriture refusée.</exception>
    public static void WriteTitle(string journalPath, ISecretProtector protector, string title)
    {
        var path = TitlePath(journalPath);
        title = CleanTitle(title);
        if (title.Length == 0)
        {
            File.Delete(path);
            return;
        }

        byte[] plain = Encoding.UTF8.GetBytes(title);
        byte[] protectedBytes = protector.Protect(plain);
        Array.Clear(plain);
        DurableFile.Write(path, protectedBytes);
    }

    /// <summary>Titre d'une intervention (<see cref="WriteTitle"/>) ; null s'il n'y en a pas ou s'il est illisible.</summary>
    public static string? ReadTitle(string journalPath, ISecretProtector protector)
    {
        try
        {
            var path = TitlePath(journalPath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
            {
                return null;
            }

            byte[] plain = protector.Unprotect(File.ReadAllBytes(path));
            var title = CleanTitle(Encoding.UTF8.GetString(plain));
            Array.Clear(plain);
            return title.Length > 0 ? title : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Supprime une intervention : son journal et le fichier de son titre.</summary>
    /// <exception cref="IOException">Suppression refusée.</exception>
    public static void Delete(string journalPath)
    {
        File.Delete(journalPath);
        File.Delete(TitlePath(journalPath));
    }

    /// <summary>Interventions du dossier, la plus récente en tête ; avec leur titre si <paramref name="protector"/> est donné.</summary>
    public static IReadOnlyList<InterventionFile> List(string directory, ISecretProtector? protector = null)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<InterventionFile>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFiles("intervention-*.zij"))
        {
            var stamp = info.Name["intervention-".Length..^".zij".Length];
            if (stamp.Length >= 15
                && DateTime.TryParseExact(stamp[..15], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var start))
            {
                files.Add(new InterventionFile(info.FullName, start, info.Length, protector is null ? null : ReadTitle(info.FullName, protector)));
            }
        }

        return [.. files.OrderByDescending(f => f.Start).ThenByDescending(f => f.Path, StringComparer.Ordinal)];
    }

    /// <summary>Relit une intervention. Un fichier tronqué ou abîmé rend ce qui a pu être lu, marqué <c>Damaged</c>.</summary>
    /// <exception cref="InvalidDataException">Pas un journal d'intervention.</exception>
    public static InterventionRecording Read(string path, ISecretProtector protector)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var magic = new byte[Magic.Length];
        if (file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) != magic.Length || !magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a ZillaTerm intervention journal.");
        }

        var entries = new List<InterventionEntry>();
        bool damaged = false;
        string? id = null;
        long expected = 0;
        var length = new byte[4];
        while (true)
        {
            int read = file.ReadAtLeast(length, 4, throwOnEndOfStream: false);
            if (read == 0)
            {
                break;
            }

            int size = read == 4 ? BinaryPrimitives.ReadInt32LittleEndian(length) : -1;
            if (size <= 0 || size > file.Length - file.Position)
            {
                damaged = true;
                break;
            }

            if (size > MaxChunk)
            {
                // Bloc trop gros pour être relu (écrit par une version qui ne bornait pas les blocs) : sauté, la suite
                // (notes, transferts, fin de l'enregistrement) se relit.
                file.Seek(size, SeekOrigin.Current);
                damaged = true;
                continue;
            }

            var data = new byte[size];
            file.ReadExactly(data);
            Chunk? chunk;
            try
            {
                byte[] plain = protector.Unprotect(data);
                chunk = JsonSerializer.Deserialize<Chunk>(plain, JsonOptions);
                Array.Clear(plain);
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException)
            {
                damaged = true;
                continue;
            }

            if (chunk?.Entries is null || chunk.Id is null || (id is not null && chunk.Id != id))
            {
                // Bloc vide ou d'un autre journal : ignoré.
                damaged = true;
                continue;
            }

            if (chunk.Sequence != expected)
            {
                // Bloc retiré ou déplacé : ce qui reste est gardé.
                damaged = true;
            }

            id ??= chunk.Id;
            expected = chunk.Sequence + 1;
            entries.AddRange(chunk.Entries.Where(e => e is not null && e.Text is not null && e.Source is not null));
        }

        return new InterventionRecording(path, entries, damaged, entries.Count == 0 || entries[^1].Kind != InterventionKind.Stopped);
    }

    private sealed record Chunk(string Id, long Sequence, List<InterventionEntry> Entries);
}
