using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.KeePass;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.KeePass;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

/// <summary>
/// Enregistrement d'une intervention, pour toute l'application : il continue après une déconnexion de CyberArk ou le passage
/// en accès d'urgence, et s'arrête avec ZillaTerm. Rien n'est gardé tant qu'il n'est pas lancé.
/// </summary>
/// <remarks>
/// Les événements arrivent de plusieurs fils (transferts, X11) : <see cref="Record"/> les accepte de partout. Démarrer,
/// arrêter et <see cref="Attach"/> se font sur le fil de l'interface.
/// </remarks>
public sealed class InterventionRecorder
{
    /// <summary>
    /// Taille du journal au-delà de laquelle l'écran des sessions Bureau à distance n'est plus gardé qu'une fois toutes les
    /// <see cref="ScreenThrottledInterval"/> (une longue intervention sur un écran très actif ne remplit pas le disque).
    /// </summary>
    public const long ScreenThrottleBytes = 100L * 1024 * 1024;

    /// <summary>Intervalle des captures de l'écran une fois le journal au-delà de <see cref="ScreenThrottleBytes"/>.</summary>
    public static readonly TimeSpan ScreenThrottledInterval = TimeSpan.FromSeconds(10);

    /// <summary>Distingue les blocs du journal d'intervention des autres données protégées par DPAPI.</summary>
    private static readonly byte[] Entropy = "ZillaTerm.InterventionJournal.v1"u8.ToArray();

    private readonly ConditionalWeakTable<SshSession, InterventionTerminalTranscript> _transcripts = [];
    private readonly ISecretProtector _protector;
    private readonly string _directory;
    private readonly long _screenThrottleBytes;
    private volatile InterventionJournal? _journal;
    private volatile InterventionHistory? _history;
    private readonly object _throttleLock = new();
    private InterventionJournal? _throttled;
    private SynchronizationContext? _ui;

    // Numéro du dernier enregistrement d'écran : unique dans un journal, même repris après un arrêt de ZillaTerm.
    private int _screenStreams;

    // Rappels déjà montrés pour l'enregistrement en cours (minutes et texte), et textes du journal repris (un rappel
    // montré avant l'arrêt de ZillaTerm ne revient pas).
    private readonly HashSet<InterventionReminder> _remindersShown = [];
    private HashSet<string> _resumedActions = new(StringComparer.Ordinal);

    /// <param name="screenThrottleBytes">Taille du journal au-delà de laquelle l'écran est bridé (tests).</param>
    public InterventionRecorder(string directory, ISecretProtector protector, long screenThrottleBytes = ScreenThrottleBytes)
    {
        _directory = directory;
        _protector = protector;
        _screenThrottleBytes = screenThrottleBytes;
    }

    /// <summary>Enregistrement de l'application (journaux dans <see cref="InterventionJournal.DefaultDirectory"/>, DPAPI).</summary>
    public static InterventionRecorder Current { get; } = new(InterventionJournal.DefaultDirectory, new DpapiProtector(Entropy));

    /// <summary>Protection des journaux, pour les relire.</summary>
    public ISecretProtector Protector => _protector;

    public string Directory => _directory;

    public bool IsRecording => _journal is not null;

    /// <summary>Heure (UTC) du début de l'enregistrement en cours.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Événements gardés depuis le début de l'enregistrement en cours.</summary>
    public int Count => _journal?.Count ?? 0;

    /// <summary>Fichier de l'enregistrement en cours.</summary>
    public string? JournalPath => _journal?.Path;

    /// <summary>Le disque a refusé une écriture : la suite de l'intervention n'est plus gardée.</summary>
    public Exception? Failure => _journal?.Failure;

    /// <summary>Heure (UTC) depuis laquelle plus rien n'est gardé (voir <see cref="Failure"/>).</summary>
    public DateTime? FailedAt => _journal?.FailedAt;

    /// <summary>
    /// Journal que le disque refuse : la raison, l'heure depuis laquelle plus rien n'est gardé et quoi faire ; null sinon.
    /// </summary>
    public string? FailureMessage => _journal is { Failure: { } failure } journal
        ? Text.Format(Strings.InterventionWriteFailed, failure.Message,
            (journal.FailedAt ?? DateTime.UtcNow).ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
        : null;

    public bool TerminalTextCapped => _journal?.TerminalTextCapped ?? false;

    /// <summary>
    /// Historique de l'enregistrement en cours, pour l'afficher pendant l'intervention ; null hors enregistrement. Un nouvel
    /// enregistrement en a un nouveau ; celui d'un enregistrement arrêté garde sa fin.
    /// </summary>
    public InterventionHistory? History => _history;

    /// <summary>
    /// Le journal en cours dépasse <see cref="ScreenThrottleBytes"/> : l'écran des sessions Bureau à distance n'est plus
    /// gardé qu'une fois toutes les <see cref="ScreenThrottledInterval"/> (voir <see cref="UpdateScreenThrottle"/>).
    /// </summary>
    public bool ScreenThrottled
    {
        get
        {
            var journal = _journal;
            lock (_throttleLock)
            {
                return journal is not null && ReferenceEquals(journal, _throttled);
            }
        }
    }

    /// <summary>Titre de l'intervention en cours ; vide s'il n'y en a pas.</summary>
    public string Title { get; private set; } = "";

    /// <summary>Enregistrement lancé, arrêté, ou interrompu par le disque (sur le fil de l'interface).</summary>
    public event Action? Changed;

    /// <summary>Commence l'enregistrement ; <paramref name="who"/> et <paramref name="mode"/> sont écrits en tête.</summary>
    /// <exception cref="System.IO.IOException">Dossier des interventions inaccessible.</exception>
    public void Start(string who, string mode)
    {
        if (_journal is not null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var journal = InterventionJournal.Start(_directory, _protector, now);
        var history = new InterventionHistory();
        // Disque plein ou écriture refusée (écritures du minuteur, sur un autre fil) : l'interface le dit aussitôt.
        _ui = SynchronizationContext.Current;
        journal.Failed += () =>
        {
            DebugLog.Write("intervention", $"Journal d'intervention refusé par le disque : {journal.Failure?.Message}");
            PostChanged();
        };
        var started = new InterventionEntry(now, InterventionKind.Started, "",
            Text.Format(Strings.InterventionStartedEntry, who, Environment.MachineName, UpdateChecker.CurrentVersion, mode));
        journal.Write(started);
        history.Add(started);
        journal.Flush();
        StartedAt = now;
        Title = "";
        _remindersShown.Clear();
        _resumedActions = new(StringComparer.Ordinal);
        _history = history;
        _journal = journal;
        DebugLog.Write("intervention", $"Intervention enregistrée dans {journal.Path}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Reprend un enregistrement interrompu par l'arrêt de ZillaTerm (<see cref="InterventionJournal.FindUnfinished"/>) :
    /// la suite s'ajoute au même journal, après un événement qui dit qui reprend et depuis quand rien n'a été gardé.
    /// L'historique affiché repart de tout ce que le journal contenait.
    /// </summary>
    /// <exception cref="System.IO.IOException">Journal inaccessible.</exception>
    /// <exception cref="System.IO.InvalidDataException">Journal d'un autre format, ou illisible.</exception>
    public void Resume(string path, string who, string mode)
    {
        if (_journal is not null)
        {
            return;
        }

        var journal = InterventionJournal.Resume(path, _protector, out var recording);
        var history = new InterventionHistory();
        foreach (var entry in recording.Entries.Where(e => e.Kind != InterventionKind.Screen))
        {
            history.Add(entry);
        }

        _ui = SynchronizationContext.Current;
        journal.Failed += () =>
        {
            DebugLog.Write("intervention", $"Journal d'intervention refusé par le disque : {journal.Failure?.Message}");
            PostChanged();
        };
        var now = DateTime.UtcNow;
        var last = recording.End ?? now;
        var resumed = new InterventionEntry(now, InterventionKind.Action, "",
            Text.Format(Strings.InterventionResumedEntry, who, Environment.MachineName, UpdateChecker.CurrentVersion, mode,
                last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        journal.Write(resumed);
        history.Add(resumed);
        journal.Flush();
        // Les numéros d'enregistrement d'écran continuent après ceux du journal.
        foreach (var entry in recording.Entries)
        {
            if (entry.Kind == InterventionKind.Screen && ScreenFrameHeader.TryRead(entry.Image, out var header) && header.Stream > _screenStreams)
            {
                _screenStreams = header.Stream;
            }
        }

        StartedAt = recording.Start ?? now;
        Title = recording.Title ?? "";
        _remindersShown.Clear();
        _resumedActions = recording.Entries.Where(e => e.Kind == InterventionKind.Action).Select(e => e.Text).ToHashSet(StringComparer.Ordinal);
        _history = history;
        _journal = journal;
        DebugLog.Write("intervention", $"Intervention reprise dans {journal.Path}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Clôt un enregistrement interrompu sans le reprendre : un événement de fin, daté de son dernier événement (la durée
    /// de l'intervention ne change pas), dit que ZillaTerm s'était arrêté.
    /// </summary>
    /// <exception cref="System.IO.IOException">Journal inaccessible.</exception>
    /// <exception cref="System.IO.InvalidDataException">Journal d'un autre format, ou illisible.</exception>
    public void CloseUnfinished(string path)
    {
        using var journal = InterventionJournal.Resume(path, _protector, out var recording);
        var end = recording.End ?? DateTime.UtcNow;
        journal.Stop(new InterventionEntry(end, InterventionKind.Stopped, "",
            Text.Format(Strings.InterventionClosedEntry, DateTime.Now.ToString("g", CultureInfo.CurrentCulture))));
        DebugLog.Write("intervention", $"Intervention interrompue close : {path}");
        Changed?.Invoke();
    }

    /// <summary>Numéro d'un nouvel enregistrement d'écran, unique dans le journal (depuis n'importe quel fil).</summary>
    public int NextScreenStream() => Interlocked.Increment(ref _screenStreams);

    /// <summary>
    /// Rappels arrivés à leur heure depuis le dernier appel (fil de l'interface) : chacun est noté au journal, une fois par
    /// enregistrement. Un rappel déjà noté avant l'arrêt de ZillaTerm (enregistrement repris) ne revient pas.
    /// </summary>
    public IReadOnlyList<InterventionReminder> DueReminders(IReadOnlyList<InterventionReminder> reminders, DateTime now)
    {
        if (_journal is null || StartedAt is not { } start)
        {
            return [];
        }

        var due = new List<InterventionReminder>();
        foreach (var reminder in reminders)
        {
            if (now < start.AddMinutes(reminder.Minutes) || !_remindersShown.Add(reminder))
            {
                continue;
            }

            var text = ReminderEntry(reminder);
            if (_resumedActions.Contains(text))
            {
                continue;
            }

            Write(new InterventionEntry(now, InterventionKind.Action, "", text));
            due.Add(reminder);
        }

        return due;
    }

    /// <summary>Prochain rappel de l'enregistrement en cours et le temps qui reste ; null s'il n'y en a plus.</summary>
    public (InterventionReminder Reminder, TimeSpan Remaining)? NextReminder(IReadOnlyList<InterventionReminder> reminders, DateTime now)
    {
        if (_journal is null || StartedAt is not { } start)
        {
            return null;
        }

        foreach (var reminder in reminders.OrderBy(r => r.Minutes))
        {
            var at = start.AddMinutes(reminder.Minutes);
            if (at > now)
            {
                return (reminder, at - now);
            }
        }

        return null;
    }

    /// <summary>Rappel vu par l'utilisateur : noté au journal.</summary>
    public void ReminderSeen(InterventionReminder reminder) =>
        Record(InterventionKind.Action, "", Text.Format(Strings.InterventionReminderSeenEntry, reminder.Text));

    /// <summary>Texte d'un rappel dans le journal.</summary>
    internal static string ReminderEntry(InterventionReminder reminder) =>
        Text.Format(Strings.InterventionReminderEntry, reminder.Minutes, reminder.Text);

    /// <summary>Arrête l'enregistrement (fin écrite, fichier fermé).</summary>
    public void Stop(string reason)
    {
        var journal = _journal;
        if (journal is null)
        {
            return;
        }

        var history = _history;
        _journal = null;
        _history = null;
        StartedAt = null;
        Title = "";
        _remindersShown.Clear();
        var stopped = new InterventionEntry(DateTime.UtcNow, InterventionKind.Stopped, "", reason);
        if (journal.Write(stopped))
        {
            history?.Add(stopped);
        }

        journal.Dispose();
        DebugLog.Write("intervention", "Fin de l'enregistrement de l'intervention.");
        Changed?.Invoke();
    }

    /// <summary>Écrit sur le disque ce qui attend (avant de relire l'intervention en cours).</summary>
    public void Flush() => _journal?.Flush();

    /// <summary>
    /// Modifie une note de l'enregistrement en cours. Le journal n'est jamais réécrit : la modification s'y ajoute avec
    /// l'heure de la note (<see cref="InterventionEntry.Edits"/>), et le rapport montre le dernier texte et celui d'origine.
    /// </summary>
    /// <param name="noteTime">Heure (UTC) de la note modifiée.</param>
    /// <returns>Vrai si la modification est gardée (enregistrement en cours, journal accepté).</returns>
    public bool EditNote(DateTime noteTime, string text)
    {
        var history = _history;
        var entry = new InterventionEntry(DateTime.UtcNow, InterventionKind.Note, "", text, Edits: noteTime);
        if (_journal is not { } journal || !journal.Write(entry))
        {
            return false;
        }

        history?.Add(entry);
        return true;
    }

    /// <summary>Ajoute un événement si l'enregistrement est lancé (depuis n'importe quel fil).</summary>
    public void Record(InterventionKind kind, string source, string text, byte[]? image = null) =>
        Write(new InterventionEntry(DateTime.UtcNow, kind, source, text, image));

    /// <summary>
    /// Image d'écran d'une session Bureau à distance, prise à <paramref name="time"/> (depuis n'importe quel fil). Elle ne va
    /// qu'au journal pour lequel elle a été encodée (<paramref name="journalPath"/>) : la suite d'images d'un enregistrement
    /// arrêté n'a pas de sens dans le suivant, qui commence par sa propre image clé. Pas dans l'historique affiché.
    /// </summary>
    public void RecordScreen(string journalPath, string source, ScreenFrame frame, DateTime time)
    {
        if (_journal is { } journal && journal.Path == journalPath)
        {
            journal.Write(new InterventionEntry(time, InterventionKind.Screen, source, "", frame.Data));
        }
    }

    /// <summary>
    /// Regarde si le journal en cours dépasse <see cref="ScreenThrottleBytes"/> (depuis n'importe quel fil) : la première
    /// fois, le journal le note et l'interface en est prévenue (<see cref="Changed"/>).
    /// </summary>
    /// <returns>Vrai si l'écran n'est plus gardé qu'une fois toutes les <see cref="ScreenThrottledInterval"/>.</returns>
    public bool UpdateScreenThrottle()
    {
        var journal = _journal;
        if (journal is null || journal.Length < _screenThrottleBytes)
        {
            return false;
        }

        lock (_throttleLock)
        {
            if (ReferenceEquals(journal, _throttled))
            {
                return true;
            }

            _throttled = journal;
        }

        DebugLog.Write("intervention", "Journal au-delà de 100 Mo : écran des Bureaux à distance gardé toutes les 10 s.");
        Write(new InterventionEntry(DateTime.UtcNow, InterventionKind.Action, "", Strings.InterventionScreenThrottled));
        PostChanged();
        return true;
    }

    /// <summary><see cref="Changed"/> sur le fil de l'interface, depuis un autre fil.</summary>
    private void PostChanged()
    {
        if (_ui is { } ui)
        {
            ui.Post(_ => Changed?.Invoke(), null);
        }
        else
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Événement gardé par le journal, et alors ajouté à l'historique affiché.</summary>
    private void Write(InterventionEntry entry)
    {
        var history = _history;
        if (_journal is { } journal && journal.Write(entry))
        {
            history?.Add(entry);
        }
    }

    /// <summary>
    /// Donne un titre à l'intervention en cours (ticket, incident…) : écrit dans le journal, et à côté pour la liste des
    /// interventions. Sans effet hors enregistrement ou si le titre ne change pas.
    /// </summary>
    /// <returns>Vrai si le titre a changé.</returns>
    public bool SetTitle(string title)
    {
        title = InterventionJournal.CleanTitle(title);
        var journal = _journal;
        if (journal is null || title == Title)
        {
            return false;
        }

        Title = title;
        Write(new InterventionEntry(DateTime.UtcNow, InterventionKind.Title, "", title));
        try
        {
            InterventionJournal.WriteTitle(journal.Path, _protector, title);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Le journal garde le titre (rapport) ; seule la liste des interventions ne le montrera pas.
            DebugLog.Write("intervention", $"Titre de l'intervention non écrit à côté du journal : {e.Message}");
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Texte du terminal d'une session SSH, gardé pendant l'enregistrement (fil de l'interface).</summary>
    public void Attach(SshSession session) =>
        _transcripts.GetValue(session, s => new InterventionTerminalTranscript(s.Emulator, line =>
        {
            if (_journal is not null)
            {
                Write(new InterventionEntry(DateTime.UtcNow, InterventionKind.Terminal, s.Label, line));
            }
        }));

    /// <summary>Transfert terminé : sens, serveur, résultat, et l'empreinte SHA-256 de chaque fichier.</summary>
    public void RecordTransfer(TransferRecord record)
    {
        if (_journal is null)
        {
            return;
        }

        var text = new StringBuilder();
        text.Append(Text.Format(record.Upload ? Strings.InterventionUpload : Strings.InterventionDownload, record.Label, record.Destination));
        text.Append(" — ").Append(record.State switch
        {
            TransferState.Done => Strings.InterventionTransferDone,
            TransferState.Cancelled => Strings.InterventionTransferCancelled,
            _ => Text.Format(Strings.InterventionTransferFailed, record.Error ?? ""),
        });
        foreach (var file in record.Files)
        {
            text.Append('\n').Append(file.Name);
            if (file.LocalSha256 is { Length: > 0 } sha)
            {
                text.Append("  SHA-256 ").Append(Convert.ToHexString(sha).ToLowerInvariant());
            }

            if (file.Error is { } error)
            {
                text.Append("  (").Append(error).Append(')');
            }
        }

        Record(InterventionKind.Transfer, record.Server, text.ToString());
    }
}
