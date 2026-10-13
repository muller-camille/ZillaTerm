using System.IO;
using System.Windows;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Views;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Onglet Fichiers et fenêtres de fichiers : serveur et dossier gardés pendant une fenêtre de choix, bilans jamais perdus,
/// noms venant du serveur affichés sans caractère invisible.
/// </summary>
public sealed partial class DialogTests
{
    /// <summary>
    /// Envoi et téléchargement : la connexion, la session et le dossier pris avant la fenêtre de choix sont gardés. Pendant
    /// la fenêtre, le panneau suit le terminal dans un autre dossier puis passe à un autre serveur (clic dans la vue
    /// parallèle détachée) : les fichiers partent quand même là où la fenêtre l'annonçait, et viennent du serveur choisi.
    /// </summary>
    [Fact]
    public void UploadAndDownloadKeepTheServerSeenBeforeTheDialog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            UseDispatcherContext();
            var qa = new RecordingFiles("/opt/app", only: TransferProtocol.Ftps);
            var prod = new RecordingFiles("/var/www");
            var srv01 = ConnectedFiles("jdoe@srv01.corp.local", qa);
            var srv02 = ConnectedFiles("jdoe@srv02.corp.local", prod);
            var panel = new FileBrowserPanel();
            panel.Initialize(new AppSettings(), () => { }, new TransferHistory());
            panel.Attach(srv01);
            PumpUntil(() => panel.PathBox.Text == "/opt/app");
            var shown = panel.CurrentFolder()!;
            Assert.Same(qa, shown.Browser);

            // Pendant la fenêtre : le terminal de srv01 change de dossier, puis le panneau passe à srv02.
            qa.ListAsync("/etc/app", false, CancellationToken.None).GetAwaiter().GetResult();
            panel.Attach(srv02);
            PumpUntil(() => panel.PathBox.Text == "/var/www");

            var local = Path.Combine(Path.GetTempPath(), $"zt-upload-{Guid.NewGuid():N}.conf");
            File.WriteAllText(local, "listen 80;");
            try
            {
                Pump(panel.EnqueueUploadAsync([local], from: shown));
                var upload = Assert.Single(panel.Queue.Items);
                Assert.Equal(("jdoe@srv01.corp.local", "/opt/app", "FTPS"), (upload.Server, upload.Destination, upload.Protocol));
                PumpUntil(() => upload.IsFinished);
                Assert.Equal(TransferState.Done, upload.State);
                Assert.Equal((local, "/opt/app"), Assert.Single(qa.Uploads));
                Assert.Empty(prod.Uploads);

                // Téléchargement : le fichier vient de srv01 ; la file et l'historique montrent le nom sans l'inversion.
                var bill = new RemoteEntry("facture\u202Efdp.exe", "/opt/app/facture\u202Efdp.exe", false, false, 10,
                    new DateTime(2026, 10, 1, 9, 0, 0), "-rw-r--r--");
                var folder = Path.GetTempPath();
                panel.EnqueueDownload([bill], folder, [Path.Combine(folder, "facture.bin")], shown);
                var download = panel.Queue.Items.Last();
                Assert.Equal(("facture\uFFFDfdp.exe", "jdoe@srv01.corp.local"), (download.Label, download.Server));
                PumpUntil(() => download.IsFinished);
                Assert.Equal(bill.FullPath, Assert.Single(qa.Downloads));
                Assert.Empty(prod.Downloads);
                Assert.Contains(panel.History.Records, r => r.Label == "facture\uFFFDfdp.exe");
                Assert.DoesNotContain(panel.History.Records, r => r.Label.Contains('\u202E'));
            }
            finally
            {
                File.Delete(local);
                srv01.Dispose();
                srv02.Dispose();
            }
        });
    }

    /// <summary>
    /// Suppression longue : le bilan (erreurs comprises) s'affiche à la fin, liste relue, même après un aller-retour sur un
    /// autre onglet ; terminée pendant que le panneau montre un autre serveur, il s'affiche au retour sur le sien.
    /// </summary>
    [Fact]
    public void DeletionReportIsNotLostWhenSwitchingTabs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        RunWithTheme(() =>
        {
            UseDispatcherContext();
            var log = new RemoteEntry("app.log", "/var/log/app/app.log", false, false, 10, new DateTime(2026, 10, 1, 9, 0, 0), "-rw-r--r--");
            var audit = new RemoteEntry("audit.log", "/var/log/app/audit.log", false, false, 10, new DateTime(2026, 10, 1, 9, 0, 0), "-rw-------");
            var qa = new RecordingFiles("/var/log/app", entries: [log, audit]) { RefuseDelete = "audit.log" };
            var prod = new RecordingFiles("/var/www");
            var srv01 = ConnectedFiles("jdoe@srv01.corp.local", qa);
            var srv02 = ConnectedFiles("jdoe@srv02.corp.local", prod);
            var panel = new FileBrowserPanel();
            panel.Initialize(new AppSettings(), () => { }, new TransferHistory());
            panel.Attach(srv01);
            PumpUntil(() => panel.PathBox.Text == "/var/log/app");
            var incomplete = Text.Format(Strings.DeleteIncomplete, Text.Format(Strings.ItemError, "audit.log", Strings.PermissionDenied));

            // Aller-retour sur l'autre serveur pendant la suppression.
            qa.DeleteGate = new TaskCompletionSource();
            var deleting = panel.DeleteEntriesAsync(panel.CurrentFolder()!, [log, audit]);
            panel.Attach(srv02);
            PumpUntil(() => panel.PathBox.Text == "/var/www");
            panel.Attach(srv01);
            PumpUntil(() => panel.PathBox.Text == "/var/log/app");
            int listed = qa.Listed;
            qa.DeleteGate.SetResult();
            Pump(deleting);
            PumpUntil(() => panel.StatusText.Text == incomplete);
            Assert.True(qa.Listed > listed);
            Assert.Equal(["app.log"], qa.Deleted);

            // Terminée pendant que le panneau montre srv02 : rien dans sa barre d'état, le bilan au retour sur srv01.
            qa.DeleteGate = new TaskCompletionSource();
            deleting = panel.DeleteEntriesAsync(panel.CurrentFolder()!, [audit]);
            panel.Attach(srv02);
            PumpUntil(() => panel.PathBox.Text == "/var/www");
            qa.DeleteGate.SetResult();
            Pump(deleting);
            Assert.NotEqual(incomplete, panel.StatusText.Text);
            panel.Attach(srv01);
            PumpUntil(() => panel.StatusText.Text == incomplete);

            srv01.Dispose();
            srv02.Dispose();
        });
    }

    /// <summary>Session de fichiers seuls déjà connectée à <paramref name="files"/>.</summary>
    private static ZillaTerm.App.Services.FilesSession ConnectedFiles(string label, IRemoteFiles files)
    {
        var session = new ZillaTerm.App.Services.FilesSession(label, "SFTP", _ => Task.FromResult(files),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        session.ConnectAsync().GetAwaiter().GetResult();
        return session;
    }

    /// <summary>Fichiers d'un serveur en mémoire qui notent les envois, téléchargements et suppressions demandés.</summary>
    /// <param name="only">Seul protocole d'envoi possible (FTP, FTPS) ; null : celui des Paramètres.</param>
    private sealed class RecordingFiles(string home, TransferProtocol? only = null, List<RemoteEntry>? entries = null) : IRemoteFiles
    {
        private readonly List<RemoteEntry> _entries = entries ?? new List<RemoteEntry>();

        public List<(string Local, string Directory)> Uploads { get; } = [];

        public List<string> Downloads { get; } = [];

        public List<string> Deleted { get; } = [];

        /// <summary>Lectures de dossier.</summary>
        public int Listed { get; private set; }

        /// <summary>Les suppressions attendent ce signal (opération longue).</summary>
        public TaskCompletionSource? DeleteGate { get; set; }

        /// <summary>Nom dont le serveur refuse la suppression.</summary>
        public string? RefuseDelete { get; init; }

        public bool Disposed { get; private set; }
        public string HomeDirectory { get; } = home;
        public string CurrentDirectory { get; private set; } = home;
        public bool IsConnected => !Disposed;
        public bool ChoosesUploadProtocol => only is null;
        public TransferProtocol UploadProtocol => only ?? TransferProtocol.Sftp;

        public Task<List<RemoteEntry>> ListAsync(string path, bool showHidden, CancellationToken ct)
        {
            Listed++;
            CurrentDirectory = path;
            return Task.FromResult(_entries.ToList());
        }

        public Task<List<RemoteEntry>> BrowseAsync(string directory, bool showHidden, CancellationToken ct) => Task.FromResult(_entries.ToList());

        public async Task DeleteAsync(RemoteEntry entry, CancellationToken ct)
        {
            await (DeleteGate?.Task ?? Task.CompletedTask);
            if (entry.Name == RefuseDelete)
            {
                throw new Renci.SshNet.Common.SftpPermissionDeniedException("Permission denied");
            }

            Deleted.Add(entry.Name);
        }

        public Task UploadAsync(string localPath, string remoteDirectory, TransferProtocol protocol, ICollection<TransferCheck> checks,
            IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
        {
            Uploads.Add((localPath, remoteDirectory));
            return Task.CompletedTask;
        }

        public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, ICollection<TransferCheck>? checks,
            IProgress<TransferProgress>? progress, bool background, CancellationToken ct)
        {
            Downloads.Add(entry.FullPath);
            return Task.FromResult(new TransferCheck(entry.Name, localPath, entry.FullPath, 0, [], 0, []));
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(RemoteEntry entry, string newPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(true);
        public ITailSource TailSource(string path) => throw new NotSupportedException();
        public Task<long> GetSizeAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> ReadAllBytesAsync(string path, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<(DateTime LastWriteTime, long Length)> GetStatAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<(DateTime LastWriteTime, long Length)> WriteFileAsync(string remotePath, byte[] content, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<int?> GetModeAsync(string path, CancellationToken ct) => Task.FromResult<int?>(null);
        public Task<PermissionsResult> SetPermissionsAsync(string path, PermissionChange change, bool recursive,
            bool executeOnlyIfAlready, IProgress<int>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<RemoteTreeItem>> ListTreeAsync(IReadOnlyList<RemoteEntry> roots, int maxItems, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TransferCheck> DownloadAsync(RemoteEntry entry, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
}
