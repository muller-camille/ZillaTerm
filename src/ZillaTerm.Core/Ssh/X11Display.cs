using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Serveur X de ce poste (VcXsrv, X410, Xming…), noté comme la variable DISPLAY : « :0 », « localhost:0.0 »,
/// « 127.0.0.1:1 ». Les fenêtres graphiques des serveurs SSH s'y affichent par le transfert X11.
/// </summary>
/// <remarks>
/// Ce poste seulement : le protocole X n'est pas chiffré, un serveur X sur une autre machine recevrait les fenêtres et
/// les frappes en clair sur le réseau. L'affichage n sur TCP est le port 6000 + n ; sans hôte (« :0 »), sous Linux et les
/// autres Unix, c'est le socket local /tmp/.X11-unix/Xn (<see cref="HostOmitted"/>).
/// </remarks>
public sealed record X11Display(IPAddress Address, int Number, int Screen)
{
    public const string Default = ":0";

    /// <summary>Plus grand numéro d'affichage (port 6000 + 59535 = 65535).</summary>
    private const int MaxNumber = 59535;

    public IPEndPoint EndPoint => new(Address, 6000 + Number);

    /// <summary>Affichage noté sans hôte (« :0 ») : sous Linux et les autres Unix, le socket local du serveur X.</summary>
    public bool HostOmitted { get; init; }

    /// <summary>Socket local du serveur X de cet affichage (Linux et autres Unix).</summary>
    public string UnixSocketPath => $"/tmp/.X11-unix/X{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Lit un affichage : hôte facultatif de ce poste (localhost, 127.0.0.1, ::1), « : », numéro, « .écran » facultatif.
    /// </summary>
    public static bool TryParse(string? text, out X11Display display)
    {
        display = new X11Display(IPAddress.Loopback, 0, 0);
        text = text?.Trim() ?? "";
        int colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        string host = text[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        IPAddress address;
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
        }
        else if (!IPAddress.TryParse(host, out address!) || !IPAddress.IsLoopback(address))
        {
            return false;
        }

        string rest = text[(colon + 1)..];
        int dot = rest.IndexOf('.');
        string number = dot < 0 ? rest : rest[..dot];
        string screen = dot < 0 ? "0" : rest[(dot + 1)..];
        if (!IsDigits(number) || !IsDigits(screen)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > MaxNumber
            || !int.TryParse(screen, NumberStyles.None, CultureInfo.InvariantCulture, out int s) || s > 255)
        {
            return false;
        }

        display = new X11Display(address, n, s) { HostOmitted = host.Length == 0 };
        return true;
    }

    /// <summary>Un serveur X accepte-t-il les connexions TCP de cet affichage ? (Connexion de test, refermée aussitôt.)</summary>
    public async Task<bool> IsListeningAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var tcp = new TcpClient(Address.AddressFamily);
        try
        {
            await tcp.ConnectAsync(EndPoint, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Forme courte, comme DISPLAY : « :0 », « :1.2 ».</summary>
    public override string ToString() => Screen == 0 ? $":{Number}" : $":{Number}.{Screen}";

    private static bool IsDigits(string text) => text.Length is > 0 and <= 5 && text.All(char.IsAsciiDigit);
}
