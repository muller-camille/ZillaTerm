using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Reçoit l'événement OnReceivedTSPublicKey du contrôle Bureau à distance : clé publique du serveur, et réponse
/// « poursuivre la connexion ». Les paramètres sont lus tels que le contrôle les envoie (IDispatch::Invoke) : la réponse
/// est écrite là où il l'attend, paramètre par référence ou valeur de retour. Les autres événements sont ignorés ici
/// (suivis par <see cref="RdpConnection"/>).
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class RdpPublicKeySink : RdpPublicKeySink.IEventsDispatch, ICustomQueryInterface
{
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly int VariantSize = Marshal.SizeOf<Variant>();
    private const int DispIdReceivedTSPublicKey = 16;
    private const ushort VtBool = 11;
    private const ushort VtBstr = 8;
    private const ushort VtByRef = 0x4000;

    private readonly Func<byte[], bool> _check;
    private readonly string _label;
    private IConnectionPoint? _point;
    private int _cookie;

    private RdpPublicKeySink(Func<byte[], bool> check, string label)
    {
        _check = check;
        _label = label;
    }

    /// <summary>Branche le récepteur sur le contrôle (thread du contrôle) ; <paramref name="check"/> dit s'il faut poursuivre.</summary>
    public static RdpPublicKeySink Advise(object ocx, Guid eventsIid, Func<byte[], bool> check, string label)
    {
        var sink = new RdpPublicKeySink(check, label);
        ((IConnectionPointContainer)ocx).FindConnectionPoint(ref eventsIid, out var found);
        var point = found ?? throw new COMException("IMsTscAxEvents", unchecked((int)0x80040200));
        point.Advise(sink, out sink._cookie);
        sink._point = point;
        return sink;
    }

    /// <summary>Débranche le récepteur (thread du contrôle).</summary>
    public void Unadvise()
    {
        if (_point is { } point)
        {
            _point = null;
            try
            {
                point.Unadvise(_cookie);
            }
            catch (COMException)
            {
                // Contrôle déjà libéré.
            }
        }
    }

    /// <summary>Demandé comme IDispatch : la même table de méthodes que l'interface d'événements.</summary>
    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out IntPtr ppv)
    {
        if (iid == IidDispatch)
        {
            ppv = Marshal.GetComInterfaceForObject(this, typeof(IEventsDispatch), CustomQueryInterfaceMode.Ignore);
            return CustomQueryInterfaceResult.Handled;
        }

        ppv = IntPtr.Zero;
        return CustomQueryInterfaceResult.NotHandled;
    }

    int IEventsDispatch.GetTypeInfoCount(out uint count)
    {
        count = 0;
        return 0;
    }

    int IEventsDispatch.GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo)
    {
        typeInfo = IntPtr.Zero;
        return unchecked((int)0x80004001);
    }

    int IEventsDispatch.GetIDsOfNames(ref Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds) => unchecked((int)0x80004001);

    int IEventsDispatch.Invoke(int dispId, ref Guid riid, uint lcid, ushort flags, IntPtr dispParams, IntPtr result, IntPtr excepInfo, IntPtr argErr)
    {
        if (dispId != DispIdReceivedTSPublicKey || dispParams == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var parameters = Marshal.PtrToStructure<DispParams>(dispParams);
            var args = Enumerable.Range(0, parameters.Count)
                .Select(i => (Address: parameters.Args + (i * VariantSize), Value: Marshal.PtrToStructure<Variant>(parameters.Args + (i * VariantSize))))
                .ToArray();
            DebugLog.Write("rdp", $"{_label} : événement OnReceivedTSPublicKey ({args.Length} paramètre(s) : "
                + $"{string.Join(", ", args.Select(a => $"0x{a.Value.Type:X4}"))} ; retour {(result != IntPtr.Zero ? "attendu" : "aucun")})");

            // Paramètres en ordre inverse : la clé est le premier, donc le dernier du tableau.
            byte[] key = args.Length > 0 ? Bytes(args[^1].Value) : [];
            bool proceed = _check(key);
            bool answered = false;
            foreach (var (_, value) in args.SkipLast(1))
            {
                if (value.Type == (VtBool | VtByRef) && value.Pointer != IntPtr.Zero)
                {
                    Marshal.WriteInt16(value.Pointer, proceed ? (short)-1 : (short)0);
                    answered = true;
                }
            }

            if (!answered && result != IntPtr.Zero)
            {
                // VARIANT de type VT_BOOL : type au début, valeur 8 octets plus loin.
                Marshal.WriteInt16(result, (short)VtBool);
                Marshal.WriteInt64(result, 8, proceed ? 0xFFFF : 0);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Jamais d'exception vers le contrôle.
            DebugLog.Write("rdp", $"{_label} : événement OnReceivedTSPublicKey illisible", e);
        }

        return 0;
    }

    /// <summary>Octets d'un BSTR (éventuellement binaire, de longueur impaire), passé directement ou par référence.</summary>
    private static byte[] Bytes(Variant value)
    {
        IntPtr bstr = value.Type switch
        {
            VtBstr => value.Pointer,
            VtBstr | VtByRef when value.Pointer != IntPtr.Zero => Marshal.ReadIntPtr(value.Pointer),
            _ => IntPtr.Zero,
        };
        if (bstr == IntPtr.Zero)
        {
            return [];
        }

        var bytes = new byte[SysStringByteLen(bstr)];
        Marshal.Copy(bstr, bytes, 0, bytes.Length);
        return bytes;
    }

    [DllImport("oleaut32.dll")]
    private static extern uint SysStringByteLen(IntPtr bstr);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispParams
    {
        public IntPtr Args;
        public IntPtr NamedArgs;
        public int Count;
        public int NamedCount;
    }

    /// <summary>VARIANT : type, puis la valeur (pointeur ou entier court) après 8 octets.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    /// <summary>
    /// Interface d'événements du contrôle (IMsTscAxEvents) vue comme IDispatch : même table de méthodes, sous l'IID que
    /// le point de connexion demande au récepteur.
    /// </summary>
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEventsDispatch
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);

        [PreserveSig]
        int GetIDsOfNames(ref Guid riid, IntPtr names, uint count, uint lcid, IntPtr dispIds);

        [PreserveSig]
        int Invoke(int dispId, ref Guid riid, uint lcid, ushort flags, IntPtr dispParams, IntPtr result, IntPtr excepInfo, IntPtr argErr);
    }
}
