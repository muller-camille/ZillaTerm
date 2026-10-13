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
    /// <summary>Distingue les blocs du journal d'intervention des autres données protégées par DPAPI.</summary>
    private static readonly byte[] Entropy = "ZillaTerm.InterventionJournal.v1"u8.ToArray();

    private readonly ConditionalWeakTable<SshSession, InterventionTerminalTranscript> _transcripts = [];
    private readonly ISecretProtector _protector;
    private readonly string _directory;
    private volatile InterventionJournal? _journal;

    public InterventionRecorder(string directory, ISecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
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
        // Disque plein ou écriture refusée (écritures du minuteur, sur un autre fil) : l'interface le dit aussitôt.
        var ui = SynchronizationContext.Current;
        journal.Failed += () =>
        {
            DebugLog.Write("intervention", $"Journal d'intervention refusé par le disque : {journal.Failure?.Message}");
            if (ui is null)
            {
                Changed?.Invoke();
            }
            else
            {
                ui.Post(_ => Changed?.Invoke(), null);
            }
        };
        journal.Write(new InterventionEntry(now, InterventionKind.Started, "",
            Text.Format(Strings.InterventionStartedEntry, who, Environment.MachineName, UpdateChecker.CurrentVersion, mode)));
        journal.Flush();
        StartedAt = now;
        _journal = journal;
        DebugLog.Write("intervention", $"Intervention enregistrée dans {journal.Path}");
        Changed?.Invoke();
    }

    /// <summary>Arrête l'enregistrement (fin écrite, fichier fermé).</summary>
    public void Stop(string reason)
    {
        var journal = _journal;
        if (journal is null)
        {
            return;
        }

        _journal = null;
        StartedAt = null;
        journal.Stop(new InterventionEntry(DateTime.UtcNow, InterventionKind.Stopped, "", reason));
        DebugLog.Write("intervention", "Fin de l'enregistrement de l'intervention.");
        Changed?.Invoke();
    }

    /// <summary>Écrit sur le disque ce qui attend (avant de relire l'intervention en cours).</summary>
    public void Flush() => _journal?.Flush();

    /// <summary>Ajoute un événement si l'enregistrement est lancé (depuis n'importe quel fil).</summary>
    public void Record(InterventionKind kind, string source, string text, byte[]? image = null) =>
        _journal?.Write(new InterventionEntry(DateTime.UtcNow, kind, source, text, image));

    /// <summary>Texte du terminal d'une session SSH, gardé pendant l'enregistrement (fil de l'interface).</summary>
    public void Attach(SshSession session) =>
        _transcripts.GetValue(session, s => new InterventionTerminalTranscript(s.Emulator, line =>
        {
            if (_journal is { } journal)
            {
                journal.Write(new InterventionEntry(DateTime.UtcNow, InterventionKind.Terminal, s.Label, line));
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
