using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// File d'attente des transferts de l'onglet Fichiers : envois et téléchargements s'exécutent un par un, dans l'ordre
/// des demandes, quelle que soit la session (onglet) d'origine. La destination d'un envoi est le dossier affiché au
/// moment du dépôt. Un élément en attente peut être retiré, l'élément en cours annulé (le fichier interrompu,
/// incomplet, est supprimé) ; une erreur n'arrête pas la file. La navigation, la suppression, les droits, l'éditeur
/// et le glisser vers l'Explorateur passent entre deux fichiers.
/// </summary>
public partial class FileBrowserPanel
{
    private readonly TransferQueue _queue = new(ex =>
    {
        Core.Diagnostics.DebugLog.Write("files", "Transfert en échec", ex);
        return Describe(ex);
    });
    private TransferItem? _watched;

    // Historique des transferts ; sans chemin (tests, avant l'initialisation), rien n'est écrit sur le disque.
    private TransferHistory _history = new();

    /// <summary>File des transferts (tests).</summary>
    internal TransferQueue Queue => _queue;

    private void InitializeQueue()
    {
        QueueList.ItemsSource = _queue.Items;
        _queue.Changed += OnQueueChanged;
        _queue.ItemFinished += OnTransferFinished;
        _queue.Drained += OnTransfersDrained;
    }

    /// <summary>Transferts en cours ou en attente d'une session, ou de toutes si <paramref name="session"/> est null.</summary>
    public int ActiveTransfers(RemoteSession? session) =>
        _queue.Items.Count(i => i.State is TransferState.Pending or TransferState.Running && (session is null || ReferenceEquals(i.Owner, session)));

    /// <summary>Vrai si l'on peut fermer : aucun transfert en cours ou en attente, ou l'utilisateur accepte de les annuler.</summary>
    public bool ConfirmCancelTransfers(Window owner, RemoteSession? session)
    {
        int count = ActiveTransfers(session);
        return count == 0 || ConfirmDialog.Confirm(owner, new ConfirmRequest
        {
            Title = Strings.QueueTitle,
            Heading = Strings.QueueCloseHeading,
            Subject = session?.Label,
            Message = Text.Format(Strings.QueueCloseCount, count) + "\n" + Strings.QueueCloseMessage,
            Kind = ConfirmKind.Warning,
            Actions = [Strings.QueueCloseAction],
            DangerAction = 0,
            CancelLabel = Strings.QueueKeepGoing,
        });
    }

    /// <summary>
    /// Annule les transferts d'une session (de toutes si null) et attend l'arrêt de celui en cours, pour que son fichier
    /// incomplet soit supprimé avant la fermeture de la connexion (au plus 5 s).
    /// </summary>
    public async Task CancelTransfersAsync(RemoteSession? session)
    {
        bool Matches(TransferItem item) => session is null || ReferenceEquals(item.Owner, session);
        if (ActiveTransfers(session) == 0)
        {
            return;
        }

        _queue.CancelWhere(Matches);
        await _queue.WhenStoppedAsync(Matches, TimeSpan.FromSeconds(5));
    }

    /// <summary>Emplacement de gzip sur le serveur de chaque connexion (null : absent), cherché une seule fois.</summary>
    private readonly ConditionalWeakTable<IRemoteFiles, Task<string?>> _gzip = [];

    /// <summary>
    /// Met en file l'envoi de fichiers ou de dossiers locaux vers le dossier affiché, ou vers <paramref name="target"/>
    /// (dossier de la liste sur lequel on les a déposés).
    /// </summary>
    /// <param name="from">Serveur et dossier pris avant la fenêtre de choix des fichiers ; par défaut ceux affichés.</param>
    internal async Task EnqueueUploadAsync(IReadOnlyList<string> paths, string? target = null, ShownFolder? from = null)
    {
        if ((from ?? CurrentFolder()) is not { } folder || paths.Count == 0)
        {
            return;
        }

        var browser = folder.Connection;
        var session = folder.Session;
        var directory = target ?? folder.Directory;
        var names = paths.Select(p => Path.GetFileName(p.TrimEnd('\\', '/'))).ToList();

        // Beaucoup de fichiers d'un coup : proposer une seule archive .tar.gz (option des Paramètres).
        string? archiveName = null;
        int archiveFiles = 0;
        string? gzip = null;
        // L'archive se décompresse par une commande tapée dans le terminal : pas de proposition sans terminal.
        var terminal = session as SshSession;
        if (_settings.OfferArchive && terminal is not null)
        {
            // Parcours de toute l'arborescence déposée (partage réseau possible) : hors du fil de l'interface.
            int threshold = _settings.ArchiveThreshold;
            var (files, bytes, tooLong) = await Task.Run(() =>
            {
                var (count, size) = TarGzPacker.Measure(paths);
                return (count, size, count >= threshold ? TarGzPacker.UstarProblem(paths) : null);
            });
            if (tooLong is not null)
            {
                // Nom trop long ou fichier trop gros pour le format tar standard : envoi un par un.
                SetStatus(Text.Format(Strings.ArchiveNotPossible, tooLong));
            }
            else if (files >= threshold)
            {
                SetStatus(Strings.ArchiveLookingForGzip);
                gzip = await _gzip.GetValue(browser, b => TarGzPacker.FindGzipAsync(b.ExistsAsync, CancellationToken.None));
                SetStatus("");
                var offer = new ArchiveOfferDialog(files, bytes, directory, compressed: gzip is not null) { Owner = Window.GetWindow(this) };
                if (offer.ShowDialog() != true)
                {
                    return;
                }

                if (offer.DontOfferAgain)
                {
                    _settings.OfferArchive = false;
                    _saveSettings();
                }

                if (offer.UseArchive)
                {
                    archiveName = TarGzPacker.ArchiveName(paths, DateTime.Now, compressed: gzip is not null);
                    archiveFiles = files;
                    // L'archive arrive dans le dossier, puis son extraction y recrée les éléments déposés.
                    names.Add(archiveName);
                }
            }
        }

        // Écrasement : noms déjà dans le dossier sur le serveur, fichiers cachés compris (la liste affichée peut les masquer,
        // ou montrer un autre dossier si l'on a navigué entre-temps), ou attendus d'un envoi en attente vers ce dossier.
        IEnumerable<string> present;
        try
        {
            SetStatus(Text.Format(Strings.Reading, directory));
            present = (await browser.BrowseAsync(directory, showHidden: true, CancellationToken.None)).Select(e => e.Name).ToList();
            SetStatus("");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(Text.Format(Strings.CannotOpen, directory, ErrorText.Describe(ex)), error: true);
            return;
        }

        var existing = present
            .Concat(_queue.Items
                .Where(i => i.Upload && i.State is TransferState.Pending or TransferState.Running && ReferenceEquals(i.Owner, session) && i.Destination == directory)
                .SelectMany(i => i.Names))
            .ToHashSet(StringComparer.Ordinal);
        var conflicts = names.Where(existing.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (conflicts.Count > 0)
        {
            // Remplacer, ignorer les éléments existants (pas pour une archive, préparée pour tout le dépôt) ou annuler ;
            // le serveur est nommé, et Entrée annule.
            int choice = ConfirmDialog.Ask(Window.GetWindow(this), new ConfirmRequest
            {
                Title = Strings.UploadTitle,
                Heading = conflicts.Count == 1
                    ? Text.Format(Strings.UploadConflictHeadingOne, conflicts[0])
                    : Text.Format(Strings.UploadConflictHeadingMany, conflicts.Count),
                Subject = Text.Format(Strings.ServerPath, session.Label, directory),
                Message = Strings.UploadConflictMessage,
                Items = conflicts.Count == 1 ? [] : conflicts,
                Kind = ConfirmKind.Warning,
                Actions = archiveName is null ? [Strings.ActionReplace, Strings.UploadSkipExisting] : [Strings.ActionReplace],
                DangerAction = 0,
            });
            if (choice < 0)
            {
                return;
            }

            if (choice == 1)
            {
                var kept = Enumerable.Range(0, paths.Count).Where(i => !existing.Contains(names[i])).ToList();
                if (kept.Count == 0)
                {
                    SetStatus(Text.Format(Strings.UploadNothingLeft, directory));
                    return;
                }

                paths = kept.Select(i => paths[i]).ToList();
                names = kept.Select(i => names[i]).ToList();
            }
        }

        var protocol = _settings.PreferredUploadProtocol;
        if (archiveName is not null && terminal is not null)
        {
            EnqueueArchive(browser, terminal, paths, directory, archiveName, archiveFiles, names, gzip);
            return;
        }

        var label = paths.Count > 1 ? Text.Format(Strings.QueueItems, paths.Count) : names[0] + (Directory.Exists(paths[0]) ? "/" : "");
        Enqueue(new TransferItem(true, label, directory, async (item, ct) =>
        {
            var progress = new Progress<TransferProgress>(item.Report);
            item.FileCount = await Task.Run(() => paths.Sum(CountFiles), ct);
            // Un dépôt de plusieurs éléments s'arrête à la première erreur ; la file passe ensuite à l'élément suivant.
            foreach (var path in paths)
            {
                await browser.UploadAsync(path, directory, protocol, item.Checks, progress, background: true, ct);
            }
        })
        {
            Owner = session,
            Server = session.Label,
            Protocol = ProtocolOf(browser),
            Names = names,
        });
    }

    /// <summary>
    /// Met en file l'envoi d'une archive .tar.gz (.tar sans gzip sur le serveur) des éléments déposés : créée dans un
    /// dossier temporaire de ce poste, envoyée et vérifiée (SHA-256), puis supprimée du poste. La commande d'extraction
    /// est donnée à la fin.
    /// </summary>
    private void EnqueueArchive(IRemoteFiles browser, SshSession session, IReadOnlyList<string> paths, string directory,
        string archiveName, int files, IReadOnlyList<string> names, string? gzip)
    {
        var protocol = _settings.PreferredUploadProtocol;
        Enqueue(new TransferItem(true, Text.Format(Strings.ArchiveLabel, archiveName, files), directory, async (item, ct) =>
        {
            var progress = new Progress<TransferProgress>(item.Report);
            item.FileCount = 1;
            var folder = Path.Combine(ArchiveRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var archive = Path.Combine(folder, archiveName);
                // Parcours et compression hors du fil de l'interface (la progression y revient d'elle-même). Liens vers des
                // dossiers laissés de côté : notés après l'envoi de l'archive, là où ils seraient arrivés.
                var links = new List<TransferCheck>();
                await Task.Run(() => TarGzPacker.CreateAsync(paths, archive, compress: gzip is not null, progress, ct,
                    (link, name) => links.Add(TransferCheck.SkippedLink(link, RemotePath.Combine(directory, name)))), ct);
                await browser.UploadAsync(archive, directory, protocol, item.Checks, progress, background: true, ct);
                item.Checks.AddRange(links);
                item.ExtractCommand = TarGzPacker.ExtractCommand(directory, archiveName, gzip);
            }
            finally
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Core.Diagnostics.DebugLog.Write("files", $"Archive temporaire non supprimée : {ex.Message}");
                }
            }
        })
        {
            Owner = session,
            Server = session.Label,
            Protocol = ProtocolOf(browser),
            Names = names,
        });
    }

    /// <summary>Met en file le téléchargement de fichiers vers <paramref name="targets"/> (un chemin local par fichier).</summary>
    /// <param name="from">Serveur des fichiers, pris avant la fenêtre d'enregistrement.</param>
    internal void EnqueueDownload(IReadOnlyList<RemoteEntry> files, string folder, IReadOnlyList<string> targets, ShownFolder from)
    {
        var browser = from.Connection;
        if (files.Count == 0)
        {
            return;
        }

        // Nom affiché (caractères invisibles remplacés) : la file et l'historique ne montrent jamais le nom brut.
        var label = files.Count == 1 ? files[0].DisplayName : Text.Format(Strings.QueueFiles, files.Count);
        Enqueue(new TransferItem(false, label, folder, async (item, ct) =>
        {
            var progress = new Progress<TransferProgress>(item.Report);
            item.FileCount = files.Count;
            for (int i = 0; i < files.Count; i++)
            {
                // Noms Unix nettoyés (« ..\ », « : », « CON »...) par l'appelant : rien ne s'écrit hors du dossier choisi.
                await browser.DownloadAsync(files[i], targets[i], item.Checks, progress, background: true, ct);
            }
        })
        {
            Owner = from.Session,
            Server = from.Session.Label,
        });
    }

    private static int CountFiles(string path)
    {
        try
        {
            return RemoteFileBrowser.CountFiles(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Compte inconnu : l'avancement l'affiche « … ». L'envoi signalera le dossier illisible.
            return 0;
        }
    }

    private void Enqueue(TransferItem item)
    {
        _queue.Enqueue(item);
        if (!ReferenceEquals(_queue.Current, item))
        {
            SetStatus(Text.Format(Strings.QueueAdded, item.Label, _queue.PendingCount));
        }
    }

    /// <summary>Bouton d'une ligne : retirer (en attente), annuler (en cours), ou le détail SHA-256 (terminé).</summary>
    private void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TransferItem item)
        {
            return;
        }

        if (item.IsFinished)
        {
            if (item.HasChecks)
            {
                new TransferChecksDialog(item.Checks) { Owner = Window.GetWindow(this) }.ShowDialog();
            }

            return;
        }

        _queue.Cancel(item);
    }

    private void OnCancelAllTransfers(object sender, RoutedEventArgs e) => _queue.CancelAll();

    private void OnClearFinished(object sender, RoutedEventArgs e) => _queue.ClearFinished();

    /// <summary>Transferts en échec ou différents depuis le dernier passage sur l'onglet Fichiers (pastille de l'onglet).</summary>
    public int UnseenProblems { get; private set; }

    /// <summary>La file a changé, ou un problème est à signaler : pastille de l'onglet Fichiers.</summary>
    public event Action? TransfersChanged;

    /// <summary>L'onglet Fichiers est affiché : les problèmes signalés sont vus.</summary>
    public void MarkTransfersSeen() => UnseenProblems = 0;

    private void OnQueueChanged()
    {
        QueuePanel.Visibility = _queue.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelAllButton.Visibility = _queue.ActiveCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearFinishedButton.Visibility = _queue.Items.Any(i => i.IsFinished) ? Visibility.Visible : Visibility.Collapsed;
        TransfersChanged?.Invoke();
        if (!ReferenceEquals(_watched, _queue.Current))
        {
            if (_watched is not null)
            {
                _watched.PropertyChanged -= OnCurrentTransferChanged;
            }

            _watched = _queue.Current;
            if (_watched is not null)
            {
                _watched.PropertyChanged += OnCurrentTransferChanged;
            }
        }

        UpdateTransferStatus();
    }

    private void OnCurrentTransferChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransferItem.CurrentFile) or nameof(TransferItem.Verifying) or nameof(TransferItem.Packing)
            or nameof(TransferItem.FileCount) or nameof(TransferItem.CurrentProtocol))
        {
            UpdateTransferStatus();
        }
    }

    /// <summary>Barre d'état pendant la file : « Envoi SFTP de deploy/ (3/12)… · 2 en attente » (protocole du fichier en cours).</summary>
    private void UpdateTransferStatus()
    {
        if (_queue.Current is not { } item)
        {
            return;
        }

        var text = item.Packing ? Text.Format(Strings.ArchivePacking, item.Label)
            : item.Verifying ? Text.Format(Strings.VerifyingFile, item.CurrentFile)
            : item.Upload ? Text.Format(Strings.Uploading, item.CurrentProtocol?.Label() ?? item.Protocol, item.Label, TransferStatusConverter.FileNumber(item), TransferStatusConverter.FileTotal(item))
            : Text.Format(Strings.Downloading, item.CurrentFile ?? item.Label);
        if (_queue.PendingCount is > 0 and var pending)
        {
            text += " · " + Text.Format(Strings.QueuePending, pending);
        }

        SetStatus(text);
    }

    private void OnTransferFinished(TransferItem item)
    {
        if (TransferStatusConverter.Outcome(item) == "Error")
        {
            UnseenProblems++;
        }

        // Élément retiré avant d'avoir commencé : rien n'a été transféré, rien à garder.
        if (item.State != TransferState.Cancelled || item.Checks.Count > 0)
        {
            Record(new TransferRecord
            {
                Time = DateTime.UtcNow,
                Upload = item.Upload,
                Server = (item.Owner as RemoteSession)?.Label ?? "",
                Label = item.Label,
                Destination = item.Destination,
                Protocol = item.ProtocolUsed,
                State = item.State,
                Error = item.Error,
                FileCount = item.FileCount,
                Files = [.. item.Checks],
            });
        }

        Core.Diagnostics.DebugLog.Write("files", $"{(item.Upload ? "Envoi" : "Téléchargement")} {item.State} : {item.Label} → {item.Destination}"
            + (item.Error is null ? "" : $" ({item.Error})"));

        // Envoi terminé (ou arrêté) vers le dossier affiché : la liste est relue, sans effacer le bilan des transferts.
        if (item.Upload && ReferenceEquals(item.Owner, _session) && _browser is { } browser && browser.CurrentDirectory == item.Destination)
        {
            _ = NavigateAsync(item.Destination, quiet: true);
        }
    }

    /// <summary>Fin de la série : un seul bilan, et les sommes de contrôle de tous les fichiers passés.</summary>
    private void OnTransfersDrained(IReadOnlyList<TransferItem> run)
    {
        var checks = run.SelectMany(i => i.Checks).ToList();
        var failures = run.Where(i => i.State == TransferState.Failed).ToList();
        string message;
        if (run.Count == 1)
        {
            var item = run[0];
            message = item.State switch
            {
                TransferState.Done when item.ExtractCommand is not null => Text.Format(Strings.ArchiveSent, item.Label, item.Destination),
                TransferState.Done when item.Upload => Text.Format(Strings.Uploaded, item.Names.Count, item.ProtocolUsed, item.Destination),
                TransferState.Done => Text.Format(Strings.Downloaded, item.FileCount, item.Destination),
                TransferState.Failed => Failure(item),
                _ => Text.Format(Strings.QueueCancelledOne, item.Label),
            };
        }
        else
        {
            var parts = new List<string>();
            int done = run.Count(i => i.State == TransferState.Done);
            int cancelled = run.Count(i => i.State == TransferState.Cancelled);
            if (done > 0)
            {
                parts.Add(Text.Format(Strings.QueueSummaryDone, done));
            }

            if (failures.Count > 0)
            {
                parts.Add(Text.Format(Strings.QueueSummaryFailed, failures.Count));
            }

            if (cancelled > 0)
            {
                parts.Add(Text.Format(Strings.QueueSummaryCancelled, cancelled));
            }

            message = Text.Format(Strings.QueueSummary, string.Join(", ", parts));
            if (failures.Count > 0)
            {
                message += " — " + string.Join(" ; ", failures.Select(Failure));
            }
        }

        // Archives envoyées : il reste à les extraire, la commande est affichée dans un encadré. Session fermée entre-temps :
        // plus de terminal où la taper, rien n'est gardé (la session fermée ne reste pas en mémoire).
        foreach (var item in run.Where(i => i.State == TransferState.Done && i.ExtractCommand is not null
                                            && i.Owner is SshSession { IsDisposed: false }))
        {
            _extracts.Add(new PendingExtract(item.Owner as SshSession, item.Destination, item.ExtractCommand!));
        }

        UpdateExtractPanel();
        ReportChecks(message, checks, error: failures.Count > 0, note: FallbackNote(checks));

        static string Failure(TransferItem item) =>
            Text.Format(item.Upload ? Strings.UploadFailed : Strings.QueueDownloadFailed, item.Label, item.Error);
    }

    /// <summary>
    /// Fichiers partis par l'autre protocole parce que le serveur a refusé le premier : lequel, et ce que le serveur a
    /// répondu (le premier refus), dans le bilan, sans interrompre.
    /// </summary>
    private static string? FallbackNote(IReadOnlyList<TransferCheck> checks)
    {
        var sent = checks.Where(c => c is { Refused: not null, Protocol: not null, Failed: false, Interrupted: false }).ToList();
        if (sent.Count == 0)
        {
            return null;
        }

        var first = sent[0];
        var refused = first.Refused!.Value.Label();
        var used = first.Protocol!.Value.Label();
        return sent.Count == 1
            ? Text.Format(Strings.ProtocolFallbackOne, first.Name, refused, first.RefusedReason, used)
            : Text.Format(Strings.ProtocolFallbackMany, sent.Count, refused, first.RefusedReason, used);
    }
}

public partial class FileBrowserPanel
{
    /// <summary>Historique (tests).</summary>
    internal TransferHistory History => _history;

    /// <summary>Archive envoyée qu'il reste à extraire sur le serveur de sa session.</summary>
    private sealed record PendingExtract(SshSession? Session, string Directory, string Command);

    private readonly List<PendingExtract> _extracts = [];
    private readonly List<TailWindow> _tails = [];

    /// <summary>Fenêtres de suivi ouvertes (tests).</summary>
    internal IReadOnlyList<TailWindow> TailWindows => _tails;

    /// <summary>Suit les fichiers sélectionnés dans une nouvelle fenêtre (tail -f), par SFTP ; plusieurs : vue combinée.</summary>
    private void OnTail(object sender, RoutedEventArgs e) => Follow(SelectedFilePaths(), into: null);

    private List<string> SelectedFilePaths() => SelectedEntries().Where(s => !s.IsDirectory).Select(s => s.FullPath).ToList();

    /// <summary>Sous-menu « Ajouter à une fenêtre de suivi » : une entrée par fenêtre ouverte.</summary>
    private void FillTailAddMenu(MenuItem menu, bool files)
    {
        menu.Items.Clear();
        foreach (var window in _tails)
        {
            // Titre dans un TextBlock : un « _ » du chemin n'est pas une touche d'accès.
            var item = new MenuItem { Header = new TextBlock { Text = window.Title } };
            item.Click += (_, _) => Follow(SelectedFilePaths(), window);
            menu.Items.Add(item);
        }

        menu.IsEnabled = files && _tails.Count > 0 && _session is not null;
    }

    /// <summary>
    /// Suit des fichiers du serveur de la session active, dans une nouvelle fenêtre ou dans <paramref name="into"/>
    /// (vue combinée, éventuellement avec d'autres serveurs). Sur un serveur « Mes serveurs », les fichiers sont mémorisés
    /// pour être suivis à nouveau d'un clic.
    /// </summary>
    private void Follow(IReadOnlyList<string> paths, TailWindow? into)
    {
        var session = _session;
        if (session is null || paths.Count == 0)
        {
            return;
        }

        if (session.State != RemoteSessionState.Connected)
        {
            SetStatus(NotConnectedText(session), error: true);
            return;
        }

        var window = into is not null && _tails.Contains(into) ? into : NewTailWindow();
        var link = window.LinkFor(session) ?? new SessionTailLink(session, _settings.TailIndependentSession, _browser);
        foreach (var path in paths)
        {
            window.AddFeed(link, path);
        }

        if (session.Saved is { } saved)
        {
            foreach (var path in paths.Reverse())
            {
                saved.RememberTail(path);
            }

            _saveSettings();
            UpdateTailFilesButton();
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private TailWindow NewTailWindow()
    {
        var window = new TailWindow(_settings, _saveSettings);
        window.Closed += (_, _) =>
        {
            _tails.Remove(window);
            if (_tails.Count == 0)
            {
                TailAlerts.Hide();
            }
        };
        _tails.Add(window);
        return window;
    }

    /// <summary>Bouton des fichiers déjà suivis sur ce serveur « Mes serveurs ».</summary>
    private void UpdateTailFilesButton() =>
        TailFilesButton.Visibility = _session?.Saved?.TailFiles is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    private void OnTailFiles(object sender, RoutedEventArgs e)
    {
        if (_session?.Saved is not { TailFiles.Count: > 0 } saved)
        {
            return;
        }

        var files = saved.TailFiles.ToList();
        var menu = new ContextMenu { PlacementTarget = TailFilesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = new MenuItem { Header = Strings.TailFilesAll, FontWeight = FontWeights.SemiBold, Icon = new Image { Source = (System.Windows.Media.ImageSource)FindResource("IconTail"), Width = 16, Height = 16 } };
        all.Click += (_, _) => Follow(files, into: null);
        menu.Items.Add(all);
        menu.Items.Add(new Separator());
        foreach (var path in files)
        {
            var item = new MenuItem { Header = new TextBlock { Text = path } };
            item.Click += (_, _) => Follow([path], into: null);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var forget = new MenuItem { Header = Strings.TailFilesForget };
        forget.Click += (_, _) =>
        {
            saved.TailFiles.Clear();
            _saveSettings();
            UpdateTailFilesButton();
        };
        menu.Items.Add(forget);
        menu.IsOpen = true;
    }

    /// <summary>
    /// La session se ferme : ses fichiers ne sont plus suivis (les fenêtres restent ouvertes, avec leurs lignes), et ses
    /// archives à extraire sont oubliées.
    /// </summary>
    public void ReleaseTails(RemoteSession session)
    {
        foreach (var window in _tails)
        {
            window.EndSession(session);
        }

        _extracts.RemoveAll(x => ReferenceEquals(x.Session, session));
        UpdateExtractPanel();
    }

    /// <summary>Ferme les fenêtres de suivi et de comparaison (fermeture de l'application).</summary>
    public void CloseTailWindows()
    {
        foreach (var window in _tails.ToList())
        {
            window.Close();
        }

        // Fenêtres de comparaison aussi : leurs contenus sont effacés de la mémoire à la fermeture.
        foreach (var window in _compares.ToList())
        {
            window.Close();
        }

        TailAlerts.Hide();
    }

    /// <summary>Demande d'afficher le terminal d'une session (après y avoir écrit la commande d'extraction).</summary>
    public event Action<SshSession>? ShowTerminalRequested;

    /// <summary>Archives de la session affichée qu'il reste à extraire.</summary>
    private List<PendingExtract> SessionExtracts() => _extracts.Where(x => _session is not null && ReferenceEquals(x.Session, _session)).ToList();

    /// <summary>Une seule ligne, sans retour à la ligne : rien ne s'exécute avant que l'utilisateur appuie sur Entrée.</summary>
    private static string ExtractLine(IEnumerable<PendingExtract> extracts) => string.Join(" ; ", extracts.Select(x => x.Command));

    private void UpdateExtractPanel()
    {
        var extracts = SessionExtracts();
        ExtractPanel.Visibility = extracts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (extracts.Count == 0 || _session is not { } session)
        {
            return;
        }

        ExtractTitle.Text = extracts.Count == 1
            ? Text.Format(Strings.ArchiveToExtractOne, extracts[0].Directory, session.Label)
            : Text.Format(Strings.ArchiveToExtractMany, extracts.Count, session.Label);
        ExtractCommandBox.Text = ExtractLine(extracts);
        InsertExtractButton.IsEnabled = session.State == RemoteSessionState.Connected;
    }

    private void OnCopyExtractCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            // Pas un secret : presse-papiers ordinaire, à coller dans le terminal de la session.
            Clipboard.SetText(ExtractLine(SessionExtracts()));
            SetStatus(Strings.ArchiveCommandCopied);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus(Strings.ClipboardBusy, error: true);
        }
    }

    /// <summary>Écrit la commande à l'invite du terminal, sans l'exécuter, puis affiche ce terminal.</summary>
    private void OnInsertExtractCommand(object sender, RoutedEventArgs e)
    {
        if (_session is not SshSession session)
        {
            return;
        }

        if (!session.TypeAtPrompt(ExtractLine(SessionExtracts())))
        {
            SetStatus(Strings.ArchiveNotAtPrompt, error: true);
            return;
        }

        SetStatus(Strings.ArchiveInserted);
        ShowTerminalRequested?.Invoke(session);
    }

    private void OnCloseExtract(object sender, RoutedEventArgs e)
    {
        _extracts.RemoveAll(x => ReferenceEquals(x.Session, _session));
        UpdateExtractPanel();
    }

    /// <summary>Historique des transferts (bouton de la barre d'outils principale), même sans session.</summary>
    /// <param name="problemsOnly">Échec signalé et pas encore vu : seulement les échecs et transferts non vérifiés.</param>
    public void ShowHistory(bool problemsOnly = false) =>
        new TransferHistoryDialog(_history, SaveHistory, problemsOnly) { Owner = Window.GetWindow(this) }.ShowDialog();

    private void Record(TransferRecord record)
    {
        _history.Add(record);
        SaveHistory();
        // Intervention enregistrée : le transfert et l'empreinte de chaque fichier figurent aussi dans son journal.
        Services.InterventionRecorder.Current.RecordTransfer(record);
    }

    /// <summary>Enregistre l'historique sans bloquer l'interface ; un échec d'écriture est seulement journalisé.</summary>
    private void SaveHistory()
    {
        var history = _history;
        _ = Task.Run(() =>
        {
            try
            {
                history.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Core.Diagnostics.DebugLog.Write("files", $"Historique des transferts non enregistré : {ex.Message}");
            }
        });
    }
}

/// <summary>Texte d'état d'un transfert de la file : en attente, avancement, vérification, résultat.</summary>
public sealed class TransferStatusConverter : IMultiValueConverter
{
    /// <summary>
    /// Rang du fichier en cours (les fichiers déjà traités ont chacun leur vérification ; les liens non suivis d'un dossier
    /// ne sont notés qu'à la fin).
    /// </summary>
    public static int FileNumber(TransferItem item) => Math.Max(1, Math.Min(item.Checks.Count + 1, Math.Max(item.FileCount, 1)));

    /// <summary>Nombre de fichiers, « … » tant qu'il n'est pas connu.</summary>
    public static string FileTotal(TransferItem item) => item.FileCount > 0 ? item.FileCount.ToString(CultureInfo.CurrentCulture) : "…";

    // Les autres valeurs (état, avancement...) ne servent qu'à déclencher la mise à jour.
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is TransferItem item ? StateText(item) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string StateText(TransferItem item) => item.State switch
    {
        TransferState.Pending => Strings.QueueStateWaiting,
        TransferState.Running when item.CancelRequested => Strings.QueueStateCancelling,
        TransferState.Running when item.Packing => Text.Format(Strings.QueueStatePacking, Math.Round(item.Percent)),
        TransferState.Running when item.Verifying => Text.Format(Strings.QueueStateVerifying, FileNumber(item), FileTotal(item)),
        TransferState.Running => Text.Format(Strings.QueueStateRunning, Math.Round(item.Percent), FileNumber(item), FileTotal(item)),
        TransferState.Done when item.Checks.Count(c => c.Verified && !c.Matches) is > 0 and var different =>
            Text.Format(Strings.QueueStateDifferent, different),
        // Fichiers transférés mais non relus (droits, vérification annulée) : « terminé » seul les cacherait.
        TransferState.Done when item.Checks.Count(c => c.Unverified) is > 0 and var unverified =>
            Text.Format(Strings.QueueStateUnverified, unverified, Transferred(item)),
        TransferState.Done when Transferred(item) > 0 => Text.Format(Strings.QueueStateVerified, item.Checks.Count(c => c.Matches), Transferred(item)),
        TransferState.Done => Strings.QueueStateDone,
        TransferState.Failed => "✗ " + item.Error,
        _ => Strings.QueueStateCancelled,
    };

    /// <summary>Fichiers transférés (liens vers des dossiers, non suivis, à part).</summary>
    private static int Transferred(TransferItem item) => item.Checks.Count(c => !c.Skipped);

    /// <summary>Couleur du résultat : « Ok » (tout vérifié), « Warning » (non vérifié), « Error » (échec, différent), sinon « None ».</summary>
    public static string Outcome(TransferItem item) => item.State switch
    {
        TransferState.Failed => "Error",
        TransferState.Done when item.Checks.Any(c => c.Verified && !c.Matches) => "Error",
        TransferState.Done when item.Checks.Any(c => c.Unverified) => "Warning",
        TransferState.Done when Transferred(item) > 0 => "Ok",
        _ => "None",
    };
}

/// <summary>Couleur du résultat d'un transfert de la file (voir <see cref="TransferStatusConverter.Outcome"/>).</summary>
public sealed class TransferOutcomeConverter : IMultiValueConverter
{
    // L'état ne sert qu'à déclencher la mise à jour.
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is TransferItem item ? TransferStatusConverter.Outcome(item) : "None";

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
