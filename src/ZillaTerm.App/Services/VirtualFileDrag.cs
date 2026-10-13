using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Threading;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ZillaTerm.App.Services;

/// <summary>
/// Glisser-déposer de fichiers distants vers l'Explorateur, en « fichiers virtuels » : pendant le glissement,
/// l'Explorateur ne voit que leurs noms, tailles et dates (FileGroupDescriptorW) ; au dépôt, il demande leur contenu
/// (FileContents) et ce n'est qu'alors qu'ils sont téléchargés, puis lus par l'Explorateur qui les écrit dans le
/// dossier de dépôt. En mode asynchrone, l'Explorateur fait la copie dans son propre thread : il reste utilisable.
/// Les appels de l'Explorateur arrivent sur le thread de l'interface qui a créé l'objet (marshaling OLE standard,
/// au lieu de celui de .NET qui les ferait arriver sur n'importe quel thread) : la fenêtre de progression du
/// téléchargement doit s'ouvrir sur ce thread.
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileDataObject : StandardOleMarshalObject, ComDataObject, IDataObjectAsyncCapability
{
    private const int S_OK = 0;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_ABORT = unchecked((int)0x80004004);
    private const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
    private const int DropEffectCopy = 1;

    internal static readonly short DescriptorFormat = Format("FileGroupDescriptorW");
    internal static readonly short ContentsFormat = Format("FileContents");
    internal static readonly short PreferredDropEffectFormat = Format("Preferred DropEffect");

    /// <summary>
    /// CFSTR_ZONEIDENTIFIER (Windows 8 et suivants) : zone d'où viennent les fichiers virtuels, que l'Explorateur reporte
    /// en Mark-of-the-Web (flux Zone.Identifier) sur les fichiers qu'il crée à partir de FileContents. Ici la zone
    /// Internet : ils viennent d'un serveur. (« UntrustedDragDrop », lui, ne marque rien : il fait évaluer une action
    /// d'URL par la cible, qui peut refuser le dépôt ou poser une question selon la stratégie de sécurité.)
    /// </summary>
    internal static readonly short ZoneIdentifierFormat = Format("ZoneIdentifier");

    private readonly byte[] _descriptor;
    private readonly IReadOnlyList<VirtualFile> _files;
    private readonly Func<IReadOnlyList<string?>?> _fetch;
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<string?>? _local;
    private bool _fetchFailed;
    private bool _fetching;
    private bool _asyncMode = true;

    /// <summary>Crée l'objet sur le thread de l'interface, où <paramref name="fetch"/> sera toujours appelé.</summary>
    /// <param name="files">Fichiers et dossiers proposés, dans l'ordre de la description.</param>
    /// <param name="fetch">
    /// Télécharge tout au premier contenu demandé (dépôt) et renvoie le fichier local de chaque élément (null pour un
    /// dossier), ou null si le téléchargement a été annulé ou a échoué.
    /// </param>
    public VirtualFileDataObject(IReadOnlyList<VirtualFile> files, Func<IReadOnlyList<string?>?> fetch)
    {
        _files = files;
        _descriptor = VirtualFiles.BuildDescriptor(files);
        _fetch = fetch;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// Opération asynchrone de l'Explorateur terminée (résultat dans <see cref="OperationResult"/>) : les fichiers
    /// locaux peuvent être effacés.
    /// </summary>
    public event Action? Finished;

    /// <summary>Résultat de la copie asynchrone de l'Explorateur (négatif : échec), 0 tant qu'elle n'est pas finie.</summary>
    public int OperationResult { get; private set; }

    /// <summary>Erreur survenue pendant le téléchargement au dépôt, ou null.</summary>
    public Exception? FetchError { get; private set; }

    /// <summary>Vrai pendant que l'Explorateur copie les fichiers dans son propre thread.</summary>
    public bool InOperation { get; private set; }

    /// <summary>Vrai si l'Explorateur a obtenu le contenu des fichiers.</summary>
    public bool Fetched => _local is not null;

    /// <summary>Vrai si le téléchargement a été lancé (réussi, annulé ou en échec).</summary>
    public bool FetchAttempted => _local is not null || _fetchFailed;

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == DescriptorFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(_descriptor);
        }
        else if (format.cfFormat == PreferredDropEffectFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(DropEffectCopy));
        }
        else if (format.cfFormat == ZoneIdentifierFormat && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobal(BitConverter.GetBytes(MarkOfTheWeb.InternetZone));
        }
        else if (format.cfFormat == ContentsFormat && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            if (format.lindex < 0 || format.lindex >= _files.Count || _files[format.lindex].IsDirectory)
            {
                throw new COMException(null, DV_E_LINDEX);
            }

            var local = Fetch() ?? throw new COMException(FetchError?.Message, E_ABORT);
            var path = local[format.lindex] ?? throw new COMException(null, DV_E_LINDEX);
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, StgmRead | StgmShareDenyWrite, 0, false, IntPtr.Zero, out var stream));
            medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = stream };
        }
        else
        {
            throw new COMException(null, DV_E_FORMATETC);
        }
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException(null, E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == DescriptorFormat || format.cfFormat == PreferredDropEffectFormat || format.cfFormat == ZoneIdentifierFormat)
        {
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
        }

        if (format.cfFormat == ContentsFormat)
        {
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_TYMED;
        }

        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// <summary>Informations de l'Explorateur (effet réalisé…) : acceptées et ignorées.</summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        if (release)
        {
            ReleaseStgMedium(ref medium);
        }
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            throw new COMException(null, E_NOTIMPL);
        }

        FORMATETC[] formats =
        [
            new() { cfFormat = DescriptorFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new() { cfFormat = ContentsFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
            new() { cfFormat = PreferredDropEffectFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new() { cfFormat = ZoneIdentifierFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
        ];
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
        return enumerator;
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException(null, OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    // ---- IDataObjectAsyncCapability : l'Explorateur copie dans son thread, sans figer sa fenêtre.

    public void SetAsyncMode(bool doOpAsync) => _asyncMode = doOpAsync;

    public void GetAsyncMode(out bool isOpAsync) => isOpAsync = _asyncMode;

    public void StartOperation(IntPtr bindContext) => InOperation = true;

    public void InOperationAsync(out bool inAsyncOp) => inAsyncOp = InOperation;

    public void EndOperation(int result, IntPtr bindContext, uint effects)
    {
        DebugLog.Write("files", $"Glisser-déposer : copie de l'Explorateur terminée (0x{result:X8}, effet {effects})");
        OperationResult = result;
        InOperation = false;
        Finished?.Invoke();
    }

    /// <summary>
    /// Téléchargement unique, au premier contenu demandé, toujours sur le thread de l'interface ; un échec ou une
    /// annulation n'est pas retenté.
    /// </summary>
    private IReadOnlyList<string?>? Fetch()
    {
        if (!_dispatcher.CheckAccess())
        {
            // Appel venu d'un autre thread : le téléchargement (et sa fenêtre) se fait sur celui de l'interface.
            return _dispatcher.Invoke(Fetch);
        }

        if (_fetching)
        {
            // Autre demande pendant que la fenêtre de téléchargement est ouverte : à refaire plus tard.
            throw new COMException(null, RPC_E_SERVERCALL_RETRYLATER);
        }

        if (_local is null && !_fetchFailed)
        {
            _fetching = true;
            try
            {
                _local = _fetch();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DebugLog.Write("files", "Glisser-déposer : échec du téléchargement au dépôt", e);
                FetchError = e;
            }
            finally
            {
                _fetching = false;
            }

            _fetchFailed = _local is null;
        }

        return _local;
    }

    private static STGMEDIUM HGlobal(byte[] data)
    {
        var handle = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, handle, data.Length);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
    }

    private static short Format(string name) => unchecked((short)RegisterClipboardFormat(name));

    private const uint StgmRead = 0x00000000;
    private const uint StgmShareDenyWrite = 0x00000020;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create,
        IntPtr template, out IntPtr stream);

    [DllImport("shell32.dll")]
    private static extern int SHCreateStdEnumFmtEtc(uint count, FORMATETC[] formats, out IEnumFORMATETC enumerator);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

/// <summary>Interface de l'Explorateur pour copier des fichiers virtuels dans son propre thread.</summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);

    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);

    void StartOperation(IntPtr bindContext);

    void InOperationAsync([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);

    void EndOperation(int result, IntPtr bindContext, uint effects);
}

[ComImport]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, int keyState);

    [PreserveSig]
    int GiveFeedback(int effect);
}

/// <summary>Source du glisser-déposer : dépôt au relâchement du bouton gauche, annulation par Échap.</summary>
[ComVisible(true)]
internal sealed class LeftButtonDropSource : IDropSource
{
    private const int MkLeftButton = 0x1;
    private const int DragDropDrop = 0x00040100;
    private const int DragDropCancel = 0x00040101;
    private const int DragDropUseDefaultCursors = 0x00040102;

    public int QueryContinueDrag(bool escapePressed, int keyState) =>
        escapePressed ? DragDropCancel : (keyState & MkLeftButton) == 0 ? DragDropDrop : 0;

    public int GiveFeedback(int effect) => DragDropUseDefaultCursors;

    /// <summary>Glisse <paramref name="data"/> (copie seulement) ; renvoie l'effet obtenu (0 : rien de déposé).</summary>
    public static int DoDragDrop(VirtualFileDataObject data)
    {
        int hr = DoDragDrop(data, new LeftButtonDropSource(), 1, out int effect);
        return hr == DragDropDrop ? effect : 0;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComDataObject data, IDropSource source, int okEffects, out int effect);
}
