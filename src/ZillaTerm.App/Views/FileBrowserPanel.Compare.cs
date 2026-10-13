using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>
/// Comparaison d'un fichier du serveur avec le même fichier sur un autre serveur, un autre fichier, ou un fichier de ce
/// poste (ou de deux fichiers sélectionnés). Les contenus sont lus en mémoire, sans copie sur le disque.
/// </summary>
public partial class FileBrowserPanel
{
    /// <summary>Au-delà, un fichier n'est pas lu pour être comparé.</summary>
    private const long MaxCompareBytes = 50L * 1024 * 1024;

    private readonly List<CompareWindow> _compares = [];

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        if (_browser is not { } browser || _session is not { } session)
        {
            return;
        }

        var files = SelectedEntries().Where(s => !s.IsDirectory).ToList();
        if (files.Count == 2)
        {
            _ = CompareAsync(RemoteSide(session, browser, files[0].FullPath), RemoteSide(session, browser, files[1].FullPath));
            return;
        }

        if (files is not [var file])
        {
            return;
        }

        var dialog = new CompareDialog(Label(session, file.FullPath), file.FullPath, OpenSessions(), session) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var right = dialog.Session is { } other ? RemoteSide(other, null, dialog.RemoteFile) : LocalSide(dialog.LocalFile);
        _ = CompareAsync(RemoteSide(session, browser, file.FullPath), right);
    }

    // « serveur : chemin », ponctuation de la langue de l'interface.
    private static string Label(RemoteSession session, string path) => Text.Format(Strings.CompareRemoteLabel, session.Label, path);

    /// <summary>Fichier d'un serveur ; « ~ » et les chemins relatifs partent du dossier personnel du compte.</summary>
    private static (string Label, Func<Task<byte[]>> Read) RemoteSide(RemoteSession session, IRemoteFiles? browser, string path) =>
        (Label(session, path), async () =>
        {
            var target = browser ?? await session.GetBrowserAsync();
            return await target.ReadAllBytesAsync(RemotePath.ResolveHome(path, target.HomeDirectory), MaxCompareBytes, CancellationToken.None);
        });

    private static (string Label, Func<Task<byte[]>> Read) LocalSide(string path) =>
        (Text.Format(Strings.CompareLocalLabel, path), async () =>
        {
            var length = new FileInfo(path).Length;
            if (length > MaxCompareBytes)
            {
                throw new FileTooLargeException(path, length, MaxCompareBytes);
            }

            return await File.ReadAllBytesAsync(path);
        });

    private async Task CompareAsync((string Label, Func<Task<byte[]>> Read) left, (string Label, Func<Task<byte[]>> Read) right)
    {
        try
        {
            SetStatus(Text.Format(Strings.CompareReading, left.Label));
            var leftBytes = await left.Read();
            SetStatus(Text.Format(Strings.CompareReading, right.Label));
            var rightBytes = await right.Read();
            SetStatus("");
            var window = new CompareWindow(left.Label, leftBytes, right.Label, rightBytes, _settings);
            window.Closed += (_, _) => _compares.Remove(window);
            _compares.Add(window);
            window.Show();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(Text.Format(Strings.CompareFailed, Describe(ex)), error: true);
        }
    }

    /// <summary>Menu « Comparer » : un fichier (avec…) ou deux fichiers sélectionnés (entre eux).</summary>
    private void UpdateCompareMenu(MenuItem item, IReadOnlyList<RemoteEntry> selected)
    {
        int files = selected.Count(s => !s.IsDirectory);
        item.Header = files == 2 && selected.Count == 2 ? Strings.FileCompareTwo : Strings.FileCompareWith;
        item.IsEnabled = selected.Count == files && files is 1 or 2;
    }
}
