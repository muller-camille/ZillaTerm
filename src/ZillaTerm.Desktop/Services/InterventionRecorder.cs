using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Secrets;
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
/// <para>
/// Les événements arrivent de plusieurs fils (transferts, X11) : <see cref="Record"/> les accepte de partout. Démarrer,
/// arrêter et <see cref="Attach"/> se font sur le fil de l'interface.
/// </para>
/// <para>
/// Le journal est protégé par DPAPI sous Windows, par une clé du trousseau du bureau ailleurs
/// (<see cref="SecretProtectors"/>). Le trousseau peut demander son déverrouillage, ou manquer : sa clé est lue d'avance,
/// sans bloquer l'interface (<see cref="PrepareAsync"/>, <see cref="FindUnfinishedAsync"/>, <see cref="ListAsync"/>,
/// <see cref="ReadAsync"/>), avant de commencer, reprendre, lister ou relire un journal. Le journal lui-même ne touche
/// jamais au trousseau : sans clé lue, il refuse d'écrire comme un disque qui refuse (rien n'est écrit, ni chiffré ni en
/// clair) et de relire.
/// </para>
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

    /// <summary>
    /// Distingue les blocs du journal d'intervention des autres données protégées par DPAPI (même entropie que
    /// ZillaTerm.App) ; hors de Windows, nom de la clé du trousseau qui les protège.
    /// </summary>
    private static readonly byte[] Entropy = "ZillaTerm.InterventionJournal.v1"u8.ToArray();

    private readonly ConditionalWeakTable<SshSession, InterventionTerminalTranscript> _transcripts = [];
    private readonly ISecretProtector _protector;
    private readonly ISecretProtector _journalProtector;
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

    static InterventionRecorder()
    {
        // Fenêtre X11 ouverte par le serveur (fil de la connexion X11) : notée dans l'enregistrement de l'application,
        // comme le fait SshSession dans ZillaTerm.App.
        SshSession.RecordIntervention = (kind, source, text) => Current.Record(kind, source, text);
    }

    /// <param name="screenThrottleBytes">Taille du journal au-delà de laquelle l'écran est bridé (tests).</param>
    public InterventionRecorder(string directory, ISecretProtector protector, long screenThrottleBytes = ScreenThrottleBytes)
    {
        _directory = directory;
        _protector = protector;
        _journalProtector = new JournalProtector(protector);
        _screenThrottleBytes = screenThrottleBytes;
    }

    /// <summary>
    /// Enregistrement de l'application (journaux dans <see cref="InterventionJournal.DefaultDirectory"/>, DPAPI sous
    /// Windows, trousseau du bureau ailleurs).
    /// </summary>
    public static InterventionRecorder Current { get; } = new(InterventionJournal.DefaultDirectory, SecretProtectors.Create(Entropy));

    /// <summary>
    /// Protection des journaux, pour les relire : sa clé est d'abord lue sans bloquer l'interface
    /// (<c>SecretProtectors.PrepareAsync</c>, ou <see cref="PrepareAsync"/>).
    /// </summary>
    public ISecretProtector Protector => _protector;

    public string Directory => _directory;

    public bool IsRecording => _journal is not null;

    /// <summary>Heure (UTC) du début de l'enregistrement en cours.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>Événements gardés depuis le début de l'enregistrement en cours.</summary>
    public int Count => _journal?.Count ?? 0;

    /// <summary>Fichier de l'enregistrement en cours.</summary>
    public string? JournalPath => _journal?.Path;

    /// <summary>
    /// Le disque (ou le trousseau, hors de Windows) a refusé une écriture : la suite de l'intervention n'est plus gardée.
    /// </summary>
    public Exception? Failure => _journal?.Failure;

    /// <summary>Heure (UTC) depuis laquelle plus rien n'est gardé (voir <see cref="Failure"/>).</summary>
    public DateTime? FailedAt => _journal?.FailedAt;

    /// <summary>
    /// Journal que le disque refuse : la raison, l'heure depuis laquelle plus rien n'est gardé et quoi faire ; null sinon.
    /// Trousseau perdu en cours d'intervention : l'heure de l'interruption et la raison du trousseau.
    /// </summary>
    public string? FailureMessage => _journal is { Failure: { } failure } journal
        ? FailureText(failure, (journal.FailedAt ?? DateTime.UtcNow).ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
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

    /// <summary>Enregistrement lancé, arrêté, ou interrompu par le disque ou le trousseau (sur le fil de l'interface).</summary>
    public event Action? Changed;

    /// <summary>
    /// Lit d'avance la clé qui protège les journaux (trousseau déverrouillé avec sa fenêtre au besoin), hors du fil de
    /// l'interface ; rien à faire avec DPAPI. À attendre avant <see cref="Start"/>. La clé n'est créée que si le dossier ne
    /// contient encore aucun journal : sinon, introuvable, elle est perdue (jamais recréée en silence, les journaux
    /// existants deviendraient illisibles sans le dire).
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, refusé, muet, clé perdue, ou macOS.</exception>
    /// <exception cref="IOException">Dossier des interventions illisible.</exception>
    public async Task PrepareAsync(CancellationToken cancellation = default)
    {
        bool create = await Task.Run(() => InterventionJournal.List(_directory).Count == 0, cancellation).ConfigureAwait(false);
        await PrepareKeyAsync(create, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Enregistrement interrompu à proposer de reprendre au démarrage (<see cref="InterventionJournal.FindUnfinished"/>),
    /// cherché hors du fil de l'interface. La clé n'est lue (trousseau déverrouillé au besoin) que si le journal le plus
    /// récent a été écrit il y a moins de <see cref="InterventionJournal.ResumeWindow"/> : un démarrage sans intervention
    /// récente ne touche pas au trousseau.
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, refusé, muet, ou clé perdue.</exception>
    public async Task<UnfinishedIntervention?> FindUnfinishedAsync(DateTime now, CancellationToken cancellation = default)
    {
        bool recent = await Task.Run(() =>
        {
            try
            {
                return InterventionJournal.List(_directory) is [var latest, ..]
                       && now - File.GetLastWriteTimeUtc(latest.Path) <= InterventionJournal.ResumeWindow;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, cancellation).ConfigureAwait(false);
        if (!recent)
        {
            return null;
        }

        await PrepareKeyAsync(create: false, cancellation).ConfigureAwait(false);
        return await Task.Run(() => InterventionJournal.FindUnfinished(_directory, _journalProtector, now), cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Interventions du dossier, la plus récente en tête, avec leur titre (<see cref="InterventionJournal.List"/>) : la clé
    /// est lue d'avance, et le dossier lu hors du fil de l'interface. Un dossier vide ne touche pas au trousseau.
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">
    /// Trousseau absent, refusé ou clé perdue : <see cref="InterventionJournal.List"/> sans protection donne les journaux
    /// sans leur titre (pour les supprimer).
    /// </exception>
    /// <exception cref="IOException">Dossier illisible.</exception>
    public async Task<IReadOnlyList<InterventionFile>> ListAsync(CancellationToken cancellation = default)
    {
        var files = await Task.Run(() => InterventionJournal.List(_directory), cancellation).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return files;
        }

        await PrepareKeyAsync(create: false, cancellation).ConfigureAwait(false);
        return await Task.Run(() => InterventionJournal.List(_directory, _journalProtector), cancellation).ConfigureAwait(false);
    }

    /// <summary>Relit une intervention (<see cref="InterventionJournal.Read"/>) hors du fil de l'interface, la clé lue d'avance.</summary>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, refusé, muet, ou clé perdue.</exception>
    /// <exception cref="InvalidDataException">Pas un journal d'intervention.</exception>
    /// <exception cref="IOException">Journal inaccessible.</exception>
    public async Task<InterventionRecording> ReadAsync(string path, CancellationToken cancellation = default)
    {
        await PrepareKeyAsync(create: false, cancellation).ConfigureAwait(false);
        return await Task.Run(() => InterventionJournal.Read(path, _journalProtector), cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Clé du trousseau lue sur un autre fil (jamais celui de l'interface, même pour la partie synchrone de l'appel au
    /// bus) ; une protection fermée entre-temps (clé effacée) se dit comme un trousseau indisponible.
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Trousseau absent, refusé, muet, clé perdue, ou macOS.</exception>
    private async Task PrepareKeyAsync(bool create, CancellationToken cancellation)
    {
        try
        {
            await Task.Run(() => SecretProtectors.PrepareAsync(_protector, create, cancellation), cancellation).ConfigureAwait(false);
        }
        catch (ObjectDisposedException e)
        {
            throw KeyringProtector.Unavailable(SecretProtectorFailure.NoKeyring, e);
        }
    }

    /// <summary>
    /// Commence l'enregistrement ; <paramref name="who"/> et <paramref name="mode"/> sont écrits en tête. La clé doit avoir
    /// été lue (<see cref="PrepareAsync"/>).
    /// </summary>
    /// <exception cref="System.IO.IOException">Dossier des interventions inaccessible.</exception>
    /// <exception cref="SecretProtectorUnavailableException">Clé du trousseau pas lue d'avance.</exception>
    public void Start(string who, string mode)
    {
        if (_journal is not null)
        {
            return;
        }

        EnsurePrepared();
        if (!OperatingSystem.IsWindows() && !System.IO.Directory.Exists(_directory))
        {
            // Dossier des journaux pour l'utilisateur seul (ses fichiers sont chiffrés, leurs noms datent les interventions).
            System.IO.Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var now = DateTime.UtcNow;
        var journal = InterventionJournal.Start(_directory, _journalProtector, now);
        var history = new InterventionHistory();
        // Disque plein ou écriture refusée (écritures du minuteur, sur un autre fil) : l'interface le dit aussitôt.
        _ui = SynchronizationContext.Current;
        journal.Failed += () => OnFailed(journal);
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
    /// L'historique affiché repart de tout ce que le journal contenait. La clé doit avoir été lue
    /// (<see cref="FindUnfinishedAsync"/> ou <see cref="PrepareAsync"/>).
    /// </summary>
    /// <exception cref="System.IO.IOException">Journal inaccessible.</exception>
    /// <exception cref="System.IO.InvalidDataException">Journal d'un autre format, ou illisible.</exception>
    /// <exception cref="SecretProtectorUnavailableException">Clé du trousseau pas lue d'avance.</exception>
    public void Resume(string path, string who, string mode)
    {
        if (_journal is not null)
        {
            return;
        }

        EnsurePrepared();
        var journal = InterventionJournal.Resume(path, _journalProtector, out var recording);
        var history = new InterventionHistory();
        foreach (var entry in recording.Entries.Where(e => e.Kind != InterventionKind.Screen))
        {
            history.Add(entry);
        }

        _ui = SynchronizationContext.Current;
        journal.Failed += () => OnFailed(journal);
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
    /// de l'intervention ne change pas), dit que ZillaTerm s'était arrêté. La clé doit avoir été lue.
    /// </summary>
    /// <exception cref="System.IO.IOException">Journal inaccessible.</exception>
    /// <exception cref="System.IO.InvalidDataException">Journal d'un autre format, ou illisible.</exception>
    /// <exception cref="SecretProtectorUnavailableException">Clé du trousseau pas lue d'avance.</exception>
    public void CloseUnfinished(string path)
    {
        EnsurePrepared();
        using var journal = InterventionJournal.Resume(path, _journalProtector, out var recording);
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

    /// <summary>
    /// Écriture refusée (minuteur du journal, sur un autre fil) : notée dans le journal de débogage, et l'interface prévenue.
    /// </summary>
    private void OnFailed(InterventionJournal journal)
    {
        DebugLog.Write("intervention", journal.Failure is { InnerException: SecretProtectorUnavailableException keyring }
            ? $"Journal d'intervention interrompu, trousseau indisponible ({keyring.Failure}) : {keyring.Message}"
            : $"Journal d'intervention refusé par le disque : {journal.Failure?.Message}");
        PostChanged();
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
            InterventionJournal.WriteTitle(journal.Path, _journalProtector, title);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or CryptographicException)
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

    /// <summary>
    /// Message d'un journal refusé : le disque (sa raison, l'heure depuis laquelle plus rien n'est gardé et quoi faire), ou
    /// le trousseau perdu en cours d'intervention (l'heure de l'interruption et la raison du trousseau, sans parler de
    /// disque).
    /// </summary>
    private static string FailureText(Exception failure, string since) =>
        failure.InnerException is SecretProtectorUnavailableException keyring
            ? Text.Format(Strings.InterventionInterruptedAt, since) + " " + keyring.Message
            : Text.Format(Strings.InterventionWriteFailed, failure.Message, since);

    /// <summary>
    /// Clé du trousseau lue d'avance (<see cref="PrepareAsync"/>) : sans elle, commencer ou reprendre un journal le
    /// laisserait sans protection utilisable (le journal ne touche jamais au trousseau, voir <see cref="JournalProtector"/>).
    /// </summary>
    /// <exception cref="SecretProtectorUnavailableException">Clé pas lue.</exception>
    private void EnsurePrepared()
    {
        if (_protector is KeyringProtector { HasKey: false })
        {
            throw KeyringProtector.Unavailable(SecretProtectorFailure.NoKeyring, null);
        }
    }

    /// <summary>
    /// Protection donnée aux journaux : celle de l'enregistrement, sans jamais passer par le trousseau. Le journal protège
    /// ses blocs depuis un minuteur et ne s'attend qu'aux erreurs de disque ; une demande au trousseau y bloquerait son
    /// verrou (et l'interface qui enregistre un événement), une <see cref="SecretProtectorUnavailableException"/> y serait
    /// une exception non gérée. Sans clé lue d'avance, ou si la protection refuse, l'écriture échoue donc comme sur un disque
    /// qui refuse (<see cref="CryptographicException"/> : rien n'est écrit, ni chiffré ni en clair, et l'interface le dit) ;
    /// la relecture échoue aussitôt avec la raison du trousseau, jamais prise pour un bloc abîmé.
    /// </summary>
    private sealed class JournalProtector(ISecretProtector inner) : ISecretProtector
    {
        public byte[] Protect(byte[] data)
        {
            try
            {
                EnsureKey();
                return inner.Protect(data);
            }
            catch (SecretProtectorUnavailableException e)
            {
                throw new CryptographicException(e.Message, e);
            }
            catch (ObjectDisposedException e)
            {
                // Clé effacée (fin du processus) entre-temps.
                throw new CryptographicException(e.Message, KeyringProtector.Unavailable(SecretProtectorFailure.NoKeyring, e));
            }
        }

        /// <exception cref="SecretProtectorUnavailableException">Clé du trousseau pas lue d'avance.</exception>
        public byte[] Unprotect(byte[] data)
        {
            EnsureKey();
            return inner.Unprotect(data);
        }

        private void EnsureKey()
        {
            if (inner is KeyringProtector { HasKey: false })
            {
                throw KeyringProtector.Unavailable(SecretProtectorFailure.NoKeyring, null);
            }
        }
    }
}
