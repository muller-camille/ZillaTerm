using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Glisser des fichiers ou dossiers distants vers le gestionnaire de fichiers ou le bureau, en passant par un dossier
/// temporaire réservé à l'utilisateur (<see cref="PrivateTemp"/>), effacé ensuite.
/// <list type="bullet">
/// <item>Windows : comme dans ZillaTerm.App, des « fichiers virtuels » : l'Explorateur ne voit que noms, tailles et dates
/// pendant le glissement ; tout est téléchargé au dépôt, avec une fenêtre de progression, puis copié par l'Explorateur là
/// où on les dépose.</item>
/// <item>Linux, macOS : pas de fichiers virtuels (le gestionnaire de fichiers reçoit une liste d'URI de fichiers qui doivent
/// déjà exister). Les éléments sont donc téléchargés dès le début du glissement, bouton maintenu (avancement dans la barre
/// d'état ; relâcher le bouton annule), puis glissés comme fichiers de ce poste ; le dossier temporaire est effacé une heure
/// après le dépôt (le gestionnaire de fichiers copie après le dépôt, à son rythme), sinon au démarrage suivant.</item>
/// </list>
/// </summary>
public partial class FileBrowserPanel
{
    /// <summary>Au-delà, le glissement est refusé (dossiers trop volumineux).</summary>
    private const int MaxDragItems = 10_000;

    /// <summary>Déplacement du pointeur, bouton enfoncé, à partir duquel un glissement commence (celui de Windows).</summary>
    private const double DragDistance = 4;

    /// <summary>Temps laissé au gestionnaire de fichiers pour copier les fichiers déposés (hors Windows).</summary>
    private static readonly TimeSpan DropCopyDelay = TimeSpan.FromHours(1);

    private Point? _dragStart;
    private RemoteEntry? _pressedEntry;
    private PointerPressedEventArgs? _pressedEvent;
    private bool _keepSelection;
    private bool _dragging;

    /// <summary>Bouton gauche enfoncé depuis le début du geste (téléchargement préalable au glissement, hors Windows).</summary>
    private bool _buttonDown;

    /// <summary>Téléchargement préalable au glissement en cours (hors Windows) : relâcher le bouton l'annule.</summary>
    private CancellationTokenSource? _dragPreparation;

    /// <summary>Vérification des fichiers du dernier glisser-déposer, affichée quand l'Explorateur a copié.</summary>
    private List<TransferCheck> _dragChecks = [];

    /// <summary>Dossier des téléchargements par glisser-déposer (un sous-dossier par glissement).</summary>
    private static string DragRoot => PrivateTemp.Combine("drag");

    /// <summary>Archives .tar.gz préparées pour un envoi, supprimées après l'envoi (ou au démarrage suivant).</summary>
    private static string ArchiveRoot => PrivateTemp.Combine("archives");

    /// <summary>Efface les dossiers temporaires d'anciens glissements (au démarrage).</summary>
    public static void CleanupDragFolders()
    {
        // Glisser-déposer, et archives .tar.gz d'envoi (copies de fichiers parfois sensibles) restées après un arrêt brutal.
        string[] roots;
        try
        {
            roots = [DragRoot, ArchiveRoot];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dossier temporaire refusé (plus réservé à l'utilisateur) : rien n'y est effacé.
            DebugLog.Write("files", $"Dossiers temporaires du glisser-déposer non nettoyés : {e.Message}");
            return;
        }

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void OnListPreviewMouseDown(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _pressedEntry = null;
        _pressedEvent = null;
        _keepSelection = false;
        var point = e.GetCurrentPoint(FileList);
        _buttonDown = point.Properties.IsLeftButtonPressed;
        if (_browser is null || _dragging || !point.Properties.IsLeftButtonPressed || e.ClickCount > 1
            || RowAt(e.Source) is not { DataContext: RemoteEntry { IsParentLink: false } entry })
        {
            return;
        }

        _dragStart = point.Position;
        _pressedEntry = entry;
        _pressedEvent = e;
        // Clic dans une sélection de plusieurs éléments : elle est gardée pour pouvoir tout glisser, et se réduit à
        // l'élément cliqué au relâchement s'il n'y a pas eu de glissement (comme dans l'Explorateur).
        if (FileList.SelectedItems.Contains(entry) && FileList.SelectedItems.Count > 1 && e.KeyModifiers == KeyModifiers.None)
        {
            _keepSelection = true;
            FileList.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseMove(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(FileList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _buttonDown = false;
        }

        if (_dragStart is not { } start || !point.Properties.IsLeftButtonPressed || _pressedEvent is not { } pressedEvent)
        {
            return;
        }

        var moved = point.Position - start;
        if (Math.Abs(moved.X) < DragDistance && Math.Abs(moved.Y) < DragDistance)
        {
            return;
        }

        _dragStart = null;
        _keepSelection = false;
        var entries = SelectedEntries();
        if (_pressedEntry is { } pressed && !entries.Contains(pressed))
        {
            entries = [pressed];
        }

        if (entries.Count > 0)
        {
            _ = DragOutAsync(entries, pressedEvent);
        }
    }

    private void OnListPreviewMouseUp(object? sender, PointerReleasedEventArgs e)
    {
        if (_keepSelection && _pressedEntry is { } entry)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = entry;
        }

        _dragStart = null;
        _pressedEntry = null;
        _keepSelection = false;
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            ButtonReleased();
        }
    }

    /// <summary>Bouton relâché : fin du geste ; un téléchargement préalable au glissement est annulé (hors Windows).</summary>
    private void ButtonReleased()
    {
        _buttonDown = false;
        _dragPreparation?.Cancel();
    }

    /// <summary>
    /// Glisse les éléments choisis vers le gestionnaire de fichiers. Les dossiers sont lus d'abord (noms, tailles) ; sous
    /// Windows, rien n'est téléchargé avant le dépôt.
    /// </summary>
    private async Task DragOutAsync(List<RemoteEntry> entries, PointerPressedEventArgs trigger)
    {
        var browser = _browser;
        if (browser is null || _dragging)
        {
            return;
        }

        _dragging = true;
        string? staging = null;
        try
        {
            List<RemoteTreeItem> items;
            if (entries.Any(e => e.IsDirectory))
            {
                SetStatus(Strings.DragPreparing);
                items = await browser.ListTreeAsync(entries, MaxDragItems, CancellationToken.None);
                SetStatus("");
                if (!IsLeftButtonDown())
                {
                    // Bouton relâché pendant la lecture des dossiers : pas de glissement.
                    return;
                }
            }
            else
            {
                items = entries.Select(e => new RemoteTreeItem(e, [e.Name])).ToList();
            }

            var names = VirtualFiles.AssignNames(items.Select(i => i.Path).ToList());
            if (OperatingSystem.IsWindows())
            {
                DragVirtualFiles(browser, items, names, trigger, root => staging = root);
            }
            else
            {
                await DragLocalFilesAsync(browser, items, names, trigger, root => staging = root);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("files", "Échec du glisser-déposer vers le gestionnaire de fichiers", ex);
            SetStatus(Text.Format(Strings.DownloadFailed, Describe(ex)), error: true);
            DeleteStaging(staging);
        }
        finally
        {
            _dragging = false;
        }
    }

    /// <summary>Bouton gauche toujours enfoncé (relâché pendant la lecture des dossiers : pas de glissement).</summary>
    private bool IsLeftButtonDown() => OperatingSystem.IsWindows() ? LeftButtonDropSource.IsLeftButtonDown() : _buttonDown;

    // ===================== Windows : fichiers virtuels =====================

    /// <summary>
    /// Glissement en fichiers virtuels (Windows) ; le dossier temporaire, connu au dépôt (<paramref name="setStaging"/>),
    /// est effacé quand l'Explorateur a fini sa copie.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void DragVirtualFiles(IRemoteFiles browser, List<RemoteTreeItem> items, IReadOnlyList<string> names,
        PointerPressedEventArgs trigger, Action<string> setStaging)
    {
        string? staging = null;
        var files = items.Select((item, i) => new VirtualFile(names[i], item.Entry.IsDirectory, item.Entry.Length,
            item.Entry.LastWriteTime == default ? default : item.Entry.LastWriteTime.ToUniversalTime())).ToList();
        var data = new VirtualFileDataObject(files, () => DownloadForDrop(browser, items, files, root =>
        {
            staging = root;
            setStaging(root);
        }));
        DebugLog.Write("files", $"Glisser-déposer vers l'Explorateur : {items.Count} élément(s)");
        // La souris est à OLE pendant le glissement (Mouse.Capture(null) de WPF).
        trigger.Pointer.Capture(null);
        int effect = LeftButtonDropSource.DoDragDrop(data);
        DebugLog.Write("files", $"Glisser-déposer terminé : effet {effect}, contenu fourni {data.Fetched}, copie en cours {data.InOperation}");
        if (data.InOperation)
        {
            // L'Explorateur copie dans son thread : le dossier temporaire est effacé à la fin de sa copie.
            data.Finished += () => Dispatcher.UIThread.Post(() => FinishDrag(data, staging, items.Count));
        }
        else
        {
            FinishDrag(data, staging, items.Count);
        }
    }

    /// <summary>
    /// Au dépôt (premier contenu demandé par l'Explorateur) : télécharge tout dans un dossier temporaire, avec une
    /// fenêtre de progression. Renvoie le fichier local de chaque élément (null pour un dossier), ou null si le
    /// téléchargement a été annulé ou a échoué.
    /// </summary>
    private IReadOnlyList<string?>? DownloadForDrop(IRemoteFiles browser, List<RemoteTreeItem> items, List<VirtualFile> files,
        Action<string> setStaging)
    {
        var root = Path.Combine(DragRoot, Guid.NewGuid().ToString("N"));
        setStaging(root);
        string?[] local = [];
        int fileCount = items.Count(i => !i.Entry.IsDirectory);
        long totalBytes = items.Where(i => !i.Entry.IsDirectory).Sum(i => Math.Max(i.Entry.Length, 0));
        var checks = new List<TransferCheck>();
        _dragChecks = [];
        var dialog = new TransferDialog(Strings.DragDownloadTitle, fileCount, totalBytes, async (progress, ct) =>
            local = await DownloadTreeAsync(browser, items, files.Select(f => f.RelativePath).ToList(), root, checks, progress, ct));

        // Fenêtre modale ouverte pendant l'appel de l'Explorateur (ShowDialog de WPF) : boucle de messages imbriquée.
        bool ok = UiWait.Run(() => dialog.ShowDialogAsync(OwnerWindow));
        if (ok || checks.Count > 0)
        {
            RecordDrag(items, fileCount, checks, ok ? TransferState.Done : dialog.Error is null ? TransferState.Cancelled : TransferState.Failed,
                dialog.Error);
        }

        if (ok)
        {
            _dragChecks = checks;
            return local;
        }

        SetStatus(dialog.Error is { } error ? Text.Format(Strings.DownloadFailed, Describe(error)) : Strings.DragCancelled,
            error: dialog.Error is not null);
        DebugLog.Write("files", dialog.Error is { } failure ? $"Téléchargement au dépôt en échec : {failure.Message}" : "Téléchargement au dépôt annulé");
        return null;
    }

    [SupportedOSPlatform("windows")]
    private void FinishDrag(VirtualFileDataObject data, string? staging, int count)
    {
        int result = data.OperationResult;
        if (data.FetchError is { } error)
        {
            SetStatus(Text.Format(Strings.DownloadFailed, Describe(error)), error: true);
        }
        else if (data.Fetched && result >= 0)
        {
            ReportChecks(Text.Format(Strings.DragDownloaded, count), _dragChecks);
        }
        else if (data.Fetched || (!data.FetchAttempted && result < 0 && result != unchecked((int)0x80004004)))
        {
            // Téléchargé mais pas copié, ou refusé par l'Explorateur avant le téléchargement. (Annulation ou échec dans
            // la fenêtre de téléchargement : déjà affichés.)
            SetStatus(Text.Format(Strings.DownloadFailed, $"0x{result:X8}"), error: true);
        }

        DeleteStaging(staging);
    }

    // ===================== Linux, macOS : téléchargement préalable =====================

    /// <summary>
    /// Télécharge les éléments dans un dossier temporaire (<paramref name="setStaging"/>, effacé en cas d'erreur) tant que le
    /// bouton reste enfoncé (relâché : annulé), puis les glisse comme fichiers de ce poste (liste d'URI).
    /// </summary>
    private async Task DragLocalFilesAsync(IRemoteFiles browser, List<RemoteTreeItem> items, IReadOnlyList<string> names,
        PointerPressedEventArgs trigger, Action<string> setStaging)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var root = Path.Combine(DragRoot, Guid.NewGuid().ToString("N"));
        setStaging(root);
        int fileCount = items.Count(i => !i.Entry.IsDirectory);
        long totalBytes = items.Where(i => !i.Entry.IsDirectory).Sum(i => Math.Max(i.Entry.Length, 0));
        var checks = new List<TransferCheck>();
        using var preparation = _dragPreparation = new CancellationTokenSource();
        // Relâchement du bouton n'importe où dans la fenêtre (le pointeur a pu quitter la liste) : le geste est fini.
        void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                ButtonReleased();
            }
        }

        void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                ButtonReleased();
            }
        }

        top.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        string?[] local;
        Exception? failure = null;
        try
        {
            if (!_buttonDown)
            {
                return;
            }

            var progress = new Progress<TransferStep>(step => SetStatus(Text.Format(Strings.Downloading, step.Name)
                + $" · {step.Index + 1} / {fileCount} · {RemotePath.FormatSize(step.Bytes)} / {RemotePath.FormatSize(totalBytes)}"));
            local = await DownloadTreeAsync(browser, items, names, root, checks, progress, preparation.Token);
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            local = [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
            local = [];
        }
        finally
        {
            top.RemoveHandler(PointerReleasedEvent, OnReleased);
            top.RemoveHandler(PointerMovedEvent, OnMoved);
            _dragPreparation = null;
        }

        if (failure is not null || preparation.IsCancellationRequested || !_buttonDown)
        {
            // Échec, ou bouton relâché avant la fin du téléchargement : pas de dépôt, rien ne reste sur le poste.
            if (checks.Count > 0)
            {
                RecordDrag(items, fileCount, checks, failure is null ? TransferState.Cancelled : TransferState.Failed, failure);
            }

            SetStatus(failure is not null ? Text.Format(Strings.DownloadFailed, Describe(failure)) : Strings.DragCancelled, error: failure is not null);
            DebugLog.Write("files", failure is not null ? $"Téléchargement pour le glisser-déposer en échec : {failure.Message}"
                : "Téléchargement pour le glisser-déposer annulé (bouton relâché)");
            DeleteStaging(root);
            return;
        }

        // Éléments glissés (le premier niveau ; les dossiers emportent leur contenu), en fichiers de ce poste.
        var data = new DataTransfer();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Path.Count != 1)
            {
                continue;
            }

            var path = LocalPath(root, names[i]);
            IStorageItem? item = items[i].Entry.IsDirectory
                ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                : await top.StorageProvider.TryGetFileFromPathAsync(path);
            if (item is not null)
            {
                data.Add(DataTransferItem.CreateFile(item));
            }
        }

        DebugLog.Write("files", $"Glisser-déposer vers le gestionnaire de fichiers : {items.Count} élément(s) téléchargé(s) dans {root}");
        SetStatus("");
        var effect = await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy);
        DebugLog.Write("files", $"Glisser-déposer terminé : effet {effect}");
        bool dropped = effect != DragDropEffects.None;
        RecordDrag(items, fileCount, checks, dropped ? TransferState.Done : TransferState.Cancelled, null);
        if (dropped)
        {
            ReportChecks(Text.Format(Strings.DragDownloaded, items.Count), checks);
            // Le gestionnaire de fichiers copie après le dépôt, sans dire quand il a fini : dossier gardé le temps de la copie.
            _ = DeleteStagingLaterAsync(root);
        }
        else
        {
            SetStatus(Strings.DragCancelled);
            DeleteStaging(root);
        }
    }

    /// <summary>Efface le dossier d'un glissement après le délai de copie (sinon au démarrage suivant).</summary>
    private static async Task DeleteStagingLaterAsync(string root)
    {
        await Task.Delay(DropCopyDelay);
        DeleteStaging(root);
    }

    // ===================== Téléchargement commun =====================

    /// <summary>
    /// Chemin local d'un élément sous le dossier temporaire : chemin relatif de <see cref="VirtualFiles.AssignNames"/>
    /// (noms nettoyés, séparés par « \ ») en chemin du système.
    /// </summary>
    private static string LocalPath(string root, string relative) =>
        Path.GetFullPath(Path.Combine([root, .. relative.Split('\\')]));

    /// <summary>
    /// Télécharge les éléments (dossiers d'abord créés, fichiers vérifiés par SHA-256) dans <paramref name="root"/> ; rend le
    /// fichier local de chaque élément (null pour un dossier). Rien ne s'écrit hors de <paramref name="root"/>.
    /// </summary>
    private async Task<string?[]> DownloadTreeAsync(IRemoteFiles browser, List<RemoteTreeItem> items, IReadOnlyList<string> relativePaths,
        string root, List<TransferCheck> checks, IProgress<TransferStep> progress, CancellationToken ct)
    {
        var local = new string?[items.Count];
        BeginBusy(browser);
        try
        {
            Directory.CreateDirectory(root);
            var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            long done = 0;
            int index = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var path = LocalPath(root, relativePaths[i]);
                // Noms déjà nettoyés ; vérifié encore : rien ne s'écrit hors du dossier temporaire.
                if (!path.StartsWith(rootPrefix, comparison))
                {
                    throw new InvalidOperationException(relativePaths[i]);
                }

                var entry = items[i].Entry;
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                long start = done;
                int n = index;
                var check = await browser.DownloadAsync(entry, path, new Progress<TransferProgress>(p =>
                {
                    if (!p.Verifying)
                    {
                        progress.Report(new TransferStep(entry.DisplayName, n, start + p.Transferred));
                    }
                }), ct);
                // Fichier écrit différent de celui du serveur : le dépôt échoue plutôt que de livrer une copie fausse.
                if (check.Verified && !check.Matches)
                {
                    throw new IOException(Text.Format(Strings.TransferMismatchFile, entry.DisplayName));
                }

                checks.Add(check);
                done += Math.Max(entry.Length, 0);
                local[i] = path;
                index++;
            }
        }
        finally
        {
            EndBusy(browser);
        }

        return local;
    }

    /// <summary>Glissement dans l'historique des transferts (et le journal d'une intervention enregistrée).</summary>
    private void RecordDrag(List<RemoteTreeItem> items, int fileCount, List<TransferCheck> checks, TransferState state, Exception? error)
    {
        var roots = items.Where(i => i.Path.Count == 1).Select(i => i.Entry).ToList();
        Record(new TransferRecord
        {
            Time = DateTime.UtcNow,
            Server = _session?.Label ?? "",
            // Nom affiché (caractères invisibles remplacés) : il ne peut pas en imiter un autre dans l'historique.
            Label = roots.Count == 1 ? roots[0].DisplayName + (roots[0].IsDirectory ? "/" : "") : Text.Format(Strings.QueueItems, roots.Count),
            Destination = Strings.HistoryExplorer,
            State = state,
            Error = error is not null ? Describe(error) : null,
            FileCount = fileCount,
            Files = [.. checks],
        });
    }

    /// <summary>Efface le dossier temporaire d'un glissement ; s'il est encore utilisé, au prochain démarrage.</summary>
    private static void DeleteStaging(string? root)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("files", $"Dossier temporaire du glisser-déposer gardé jusqu'au prochain démarrage : {e.Message}");
        }
    }
}
