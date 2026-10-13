using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Glisser des fichiers ou dossiers distants vers l'Explorateur ou le bureau : ils sont téléchargés au dépôt, dans un
/// dossier temporaire, puis copiés par l'Explorateur là où on les dépose ; le dossier temporaire est ensuite effacé.
/// </summary>
public partial class FileBrowserPanel
{
    /// <summary>Au-delà, le glissement est refusé (dossiers trop volumineux).</summary>
    private const int MaxDragItems = 10_000;

    private Point? _dragStart;
    private ListViewItem? _pressedItem;
    private bool _keepSelection;
    private bool _dragging;

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
        foreach (var root in new[] { DragRoot, ArchiveRoot })
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

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedItem = null;
        _keepSelection = false;
        if (_browser is null || _dragging || e.ClickCount > 1
            || ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is not ListViewItem item
            || item.DataContext is not RemoteEntry { IsParentLink: false })
        {
            return;
        }

        _dragStart = e.GetPosition(FileList);
        _pressedItem = item;
        // Clic dans une sélection de plusieurs éléments : elle est gardée pour pouvoir tout glisser, et se réduit à
        // l'élément cliqué au relâchement s'il n'y a pas eu de glissement (comme dans l'Explorateur).
        if (item.IsSelected && FileList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _keepSelection = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(FileList) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        _keepSelection = false;
        var entries = SelectedEntries();
        if (_pressedItem?.DataContext is RemoteEntry pressed && !entries.Contains(pressed))
        {
            entries = [pressed];
        }

        if (entries.Count > 0)
        {
            _ = DragOutAsync(entries);
        }
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_keepSelection && _pressedItem?.DataContext is RemoteEntry entry)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = entry;
        }

        _dragStart = null;
        _pressedItem = null;
        _keepSelection = false;
    }

    /// <summary>
    /// Glisse les éléments choisis vers l'Explorateur. Les dossiers sont lus d'abord (noms, tailles) ; rien n'est
    /// téléchargé avant le dépôt.
    /// </summary>
    private async Task DragOutAsync(List<RemoteEntry> entries)
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
            var files = items.Select((item, i) => new VirtualFile(names[i], item.Entry.IsDirectory, item.Entry.Length,
                item.Entry.LastWriteTime == default ? default : item.Entry.LastWriteTime.ToUniversalTime())).ToList();
            var data = new VirtualFileDataObject(files, () => DownloadForDrop(browser, items, files, root => staging = root));
            DebugLog.Write("files", $"Glisser-déposer vers l'Explorateur : {items.Count} élément(s)");
            Mouse.Capture(null);
            int effect = LeftButtonDropSource.DoDragDrop(data);
            DebugLog.Write("files", $"Glisser-déposer terminé : effet {effect}, contenu fourni {data.Fetched}, copie en cours {data.InOperation}");
            if (data.InOperation)
            {
                // L'Explorateur copie dans son thread : le dossier temporaire est effacé à la fin de sa copie.
                data.Finished += () => Dispatcher.BeginInvoke(() => FinishDrag(data, staging, items.Count));
            }
            else
            {
                FinishDrag(data, staging, items.Count);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("files", "Échec du glisser-déposer vers l'Explorateur", ex);
            SetStatus(Text.Format(Strings.DownloadFailed, Describe(ex)), error: true);
            DeleteStaging(staging);
        }
        finally
        {
            _dragging = false;
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
        var local = new string?[items.Count];
        int fileCount = items.Count(i => !i.Entry.IsDirectory);
        long totalBytes = items.Where(i => !i.Entry.IsDirectory).Sum(i => Math.Max(i.Entry.Length, 0));
        var checks = new List<TransferCheck>();
        _dragChecks = [];
        var dialog = new TransferDialog(Strings.DragDownloadTitle, fileCount, totalBytes, async (progress, ct) =>
        {
            BeginBusy(browser);
            try
            {
                Directory.CreateDirectory(root);
                var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                long done = 0;
                int index = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var path = Path.GetFullPath(Path.Combine(root, files[i].RelativePath));
                    // Noms déjà nettoyés ; vérifié encore : rien ne s'écrit hors du dossier temporaire.
                    if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(files[i].RelativePath);
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
        })
        {
            Owner = Window.GetWindow(this),
        };

        bool ok = dialog.ShowDialog() == true;
        if (ok || checks.Count > 0)
        {
            var roots = items.Where(i => i.Path.Count == 1).Select(i => i.Entry).ToList();
            Record(new TransferRecord
            {
                Time = DateTime.UtcNow,
                Server = _session?.Label ?? "",
                // Nom affiché (caractères invisibles remplacés) : il ne peut pas en imiter un autre dans l'historique.
                Label = roots.Count == 1 ? roots[0].DisplayName + (roots[0].IsDirectory ? "/" : "") : Text.Format(Strings.QueueItems, roots.Count),
                Destination = Strings.HistoryExplorer,
                State = ok ? TransferState.Done : dialog.Error is null ? TransferState.Cancelled : TransferState.Failed,
                Error = dialog.Error is { } dragError ? Describe(dragError) : null,
                FileCount = fileCount,
                Files = [.. checks],
            });
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

    private static bool IsLeftButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
