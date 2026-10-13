using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Threading;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Ssh;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Données glissées vers l'Explorateur, vues comme l'Explorateur les lit (interface COM) : description pendant le
/// glissement, contenu seulement au dépôt, téléchargé une seule fois.
/// </summary>
public class VirtualFileDataObjectTests
{
    [Fact]
    public void ContentIsFetchedOnlyWhenTheExplorerAsksForIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var local = Path.GetTempFileName();
        File.WriteAllText(local, "contenu distant", Encoding.UTF8);
        try
        {
            int fetches = 0;
            VirtualFile[] files = [new("logs", true, 0, default), new(@"logs\a.txt", false, 18, DateTime.UtcNow)];
            ComDataObject data = new VirtualFileDataObject(files, () =>
            {
                fetches++;
                return new string?[] { null, local };
            });

            // Pendant le glissement : description et effet préféré, sans téléchargement.
            var descriptor = Format(VirtualFileDataObject.DescriptorFormat, TYMED.TYMED_HGLOBAL);
            Assert.Equal(0, data.QueryGetData(ref descriptor));
            data.GetData(ref descriptor, out var medium);
            Assert.Equal(2, Marshal.ReadInt32(medium.unionmember));
            ReleaseStgMedium(ref medium);

            var effect = Format(VirtualFileDataObject.PreferredDropEffectFormat, TYMED.TYMED_HGLOBAL);
            data.GetData(ref effect, out medium);
            Assert.Equal(1, Marshal.ReadInt32(medium.unionmember));
            ReleaseStgMedium(ref medium);

            // Zone Internet, que l'Explorateur reporte en Mark-of-the-Web sur les fichiers qu'il crée.
            var zone = Format(VirtualFileDataObject.ZoneIdentifierFormat, TYMED.TYMED_HGLOBAL);
            Assert.Equal(0, data.QueryGetData(ref zone));
            data.GetData(ref zone, out medium);
            Assert.Equal(3, Marshal.ReadInt32(medium.unionmember));
            ReleaseStgMedium(ref medium);

            var enumerator = data.EnumFormatEtc(DATADIR.DATADIR_GET);
            var formats = new FORMATETC[8];
            var count = new int[1];
            enumerator.Next(formats.Length, formats, count);
            Assert.Equal(4, count[0]);
            Assert.Equal(0, fetches);

            // Au dépôt : contenu du fichier, téléchargé une fois ; un dossier n'a pas de contenu.
            var contents = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 1);
            data.GetData(ref contents, out medium);
            Assert.Equal("contenu distant", ReadStream(medium.unionmember));
            ReleaseStgMedium(ref medium);
            data.GetData(ref contents, out medium);
            ReleaseStgMedium(ref medium);
            Assert.Equal(1, fetches);

            var folder = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 0);
            Assert.Throws<COMException>(() => data.GetData(ref folder, out _));
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void CancelledDownloadIsReportedAndNotRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int fetches = 0;
        ComDataObject data = new VirtualFileDataObject([new("a.txt", false, 1, default)], () =>
        {
            fetches++;
            return null;
        });

        var contents = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 0);
        var first = Assert.Throws<COMException>(() => data.GetData(ref contents, out _));
        Assert.Equal(unchecked((int)0x80004004), first.HResult);
        Assert.Throws<COMException>(() => data.GetData(ref contents, out _));
        Assert.Equal(1, fetches);
    }

    /// <summary>
    /// L'Explorateur appelle depuis un autre processus et un autre thread : ses appels doivent arriver sur le thread de
    /// l'interface qui a créé l'objet, où la fenêtre de progression du téléchargement peut s'ouvrir (en 0.5.0, ils
    /// arrivaient sur un thread RPC et la copie échouait avec 0x80131509).
    /// </summary>
    [Fact]
    public async Task ExplorerCallsAreServedOnTheInterfaceThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var local = Path.GetTempFileName();
        File.WriteAllText(local, "contenu distant", Encoding.UTF8);
        using var ui = UiThread.Start();
        try
        {
            int fetchThread = 0;
            var stream = await ui.InvokeAsync(() =>
            {
                var data = new VirtualFileDataObject([new("a.txt", false, 18, default)], () =>
                {
                    fetchThread = Environment.CurrentManagedThreadId;
                    return new string?[] { local };
                });
                var unknown = Marshal.GetComInterfaceForObject<VirtualFileDataObject, ComDataObject>(data);
                try
                {
                    var iid = typeof(ComDataObject).GUID;
                    Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(ref iid, unknown, out var marshaled));
                    return marshaled;
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            });

            // Thread du pool (MTA), comme les threads RPC qui portent les appels de l'Explorateur.
            var content = await Task.Run(() =>
            {
                var iid = typeof(ComDataObject).GUID;
                Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(stream, ref iid, out var pointer));
                var proxy = Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
                try
                {
                    // Un mandataire COM, pas l'objet lui-même : chaque appel est remis au thread de l'interface.
                    Assert.True(Marshal.IsComObject(proxy));
                    var contents = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 0);
                    ((ComDataObject)proxy).GetData(ref contents, out var medium);
                    try
                    {
                        return ReadStream(medium.unionmember);
                    }
                    finally
                    {
                        ReleaseStgMedium(ref medium);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(proxy);
                }
            });

            Assert.Equal("contenu distant", content);
            Assert.Equal(ui.ThreadId, fetchThread);
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>Appel direct depuis un autre thread : le téléchargement se fait quand même sur le thread de l'interface.</summary>
    [Fact]
    public async Task ContentIsFetchedOnTheInterfaceThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var local = Path.GetTempFileName();
        using var ui = UiThread.Start();
        try
        {
            int fetchThread = 0;
            var data = await ui.InvokeAsync(() => new VirtualFileDataObject([new("a.txt", false, 0, default)], () =>
            {
                fetchThread = Environment.CurrentManagedThreadId;
                return new string?[] { local };
            }));

            await Task.Run(() =>
            {
                var contents = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 0);
                ((ComDataObject)data).GetData(ref contents, out var medium);
                ReleaseStgMedium(ref medium);
            });

            Assert.Equal(ui.ThreadId, fetchThread);
            Assert.True(data.Fetched);
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>Échec du téléchargement : signalé à l'Explorateur, gardé pour l'afficher, et pas retenté.</summary>
    [Fact]
    public void FailedDownloadIsReportedAndNotRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int fetches = 0;
        var data = new VirtualFileDataObject([new("a.txt", false, 1, default)], () =>
        {
            fetches++;
            throw new IOException("connexion perdue");
        });

        var contents = Format(VirtualFileDataObject.ContentsFormat, TYMED.TYMED_ISTREAM, lindex: 0);
        var first = Assert.Throws<COMException>(() => ((ComDataObject)data).GetData(ref contents, out _));
        Assert.Equal(unchecked((int)0x80004004), first.HResult);
        Assert.IsType<IOException>(data.FetchError);
        Assert.Throws<COMException>(() => ((ComDataObject)data).GetData(ref contents, out _));
        Assert.Equal(1, fetches);
        Assert.False(data.Fetched);
        Assert.True(data.FetchAttempted);
    }

    private static FORMATETC Format(short format, TYMED tymed, int lindex = -1) => new()
    {
        cfFormat = format,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        tymed = tymed,
    };

    private static string ReadStream(IntPtr pointer)
    {
        var stream = (IStream)Marshal.GetObjectForIUnknown(pointer);
        var buffer = new byte[256];
        var read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            stream.Read(buffer, buffer.Length, read);
            return Encoding.UTF8.GetString(buffer, 0, Marshal.ReadInt32(read)).TrimStart('﻿');
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll")]
    private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, out IntPtr unknown);

    /// <summary>Thread d'interface (STA avec sa boucle de messages WPF), comme celui de l'application.</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        private UiThread(Thread thread, Dispatcher dispatcher)
        {
            _thread = thread;
            _dispatcher = dispatcher;
        }

        public int ThreadId => _thread.ManagedThreadId;

        public static UiThread Start()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Interface (test)",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return new UiThread(thread, ready.Task.GetAwaiter().GetResult());
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => _dispatcher.InvokeAsync(action).Task;

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
