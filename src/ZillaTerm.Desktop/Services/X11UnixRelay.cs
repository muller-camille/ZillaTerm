using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

/// <summary>
/// Transfert X11 vers le serveur X de ce poste sous Linux et les autres Unix, qui écoute sur un socket local
/// (/tmp/.X11-unix/Xn) et demande le cookie de la session (Xauthority). SSH.NET ne sait joindre qu'un serveur X en TCP :
/// ce relais écoute sur l'adresse de bouclage, et SSH.NET lui transmet chaque connexion X11 du serveur avec un jeton
/// aléatoire de la session à la place du cookie (le faux cookie donné au serveur SSH est vérifié et retiré avant).
/// </summary>
/// <remarks>
/// <para>
/// Le relais n'ouvre le socket du serveur X qu'à une connexion qui présente ce jeton (comparé en temps constant), et le
/// remplace alors par le vrai cookie : le cookie ne sort pas de ce processus, et un autre utilisateur du poste qui se
/// connecterait au port du relais ne joint pas le serveur X (qui, s'il reconnaît l'utilisateur à sa connexion locale,
/// l'accepterait sinon sous l'identité de ZillaTerm).
/// </para>
/// <para>
/// Socket et dossier vérifiés avant chaque connexion : propriétaire root ou l'utilisateur, jamais un lien ; sinon, un autre
/// utilisateur du poste qui aurait créé /tmp/.X11-unix/Xn recevrait le cookie.
/// </para>
/// </remarks>
internal sealed class X11UnixRelay : IDisposable
{
    private const int TokenLength = 16;
    private const int SocketMode = 0xC000;
    private const int TypeMask = 0xF000;
    private const int DirectoryType = 0x4000;

    /// <summary>Délai pour recevoir la demande d'ouverture d'une connexion.</summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly string _socketPath;
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(TokenLength);
    private readonly byte[]? _cookie;
    private readonly string _label;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _open = [];

    private X11UnixRelay(X11Display display, string label)
    {
        _socketPath = display.UnixSocketPath;
        _label = label;
        _cookie = XAuthority.ReadCookie(XAuthority.DefaultPath(), Dns.GetHostName(), display.Number);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptAsync();
    }

    /// <summary>Adresse du relais, à donner à SSH.NET comme serveur X.</summary>
    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Jeton que SSH.NET doit présenter (AuthenticationData, protocole MIT-MAGIC-COOKIE-1).</summary>
    public byte[] Token => _token;

    /// <summary>Un cookie a été trouvé pour cet affichage (sinon, connexions sans autorisation).</summary>
    public bool HasCookie => _cookie is not null;

    /// <summary>Relais vers le serveur X de l'affichage <paramref name="display"/> (noté sans hôte : « :0 »).</summary>
    public static X11UnixRelay Start(X11Display display, string label) => new(display, label);

    /// <summary>Un serveur X écoute-t-il sur le socket de cet affichage ? (Connexion de test, refermée aussitôt.)</summary>
    public static async Task<bool> IsListeningAsync(X11Display display, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsTrustedSocket(display.UnixSocketPath))
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(display.UnixSocketPath), cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Socket d'un serveur X digne de recevoir le cookie : un socket (pas un lien), à root ou à l'utilisateur, dans un dossier
    /// à root ou à l'utilisateur.
    /// </summary>
    private static bool IsTrustedSocket(string path)
    {
        try
        {
            uint me = PrivateTemp.UnixUserId;
            if (PrivateTemp.UnixStatus(Path.GetDirectoryName(path)!) is not { } folder
                || (folder.Mode & TypeMask) != DirectoryType || (folder.Uid != 0 && folder.Uid != me))
            {
                return false;
            }

            return PrivateTemp.UnixStatus(path) is { } socket
                   && (socket.Mode & TypeMask) == SocketMode && (socket.Uid == 0 || socket.Uid == me);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(Socket client)
    {
        Socket? server = null;
        Track(client);
        try
        {
            var buffer = new byte[X11Setup.MaxLength];
            int received = 0;
            int length;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(SetupTimeout);
                while (true)
                {
                    var status = X11Setup.Check(buffer.AsSpan(0, received), _token, out length);
                    if (status == X11SetupStatus.Accepted)
                    {
                        break;
                    }

                    if (status == X11SetupStatus.Refused || received == buffer.Length)
                    {
                        DebugLog.Write("x11", $"{_label} : connexion au relais X11 sans le jeton de la session, refusée");
                        return;
                    }

                    int read = await client.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    received += read;
                }
            }

            if (!IsTrustedSocket(_socketPath))
            {
                DebugLog.Write("x11", $"{_label} : {_socketPath} n'est pas le socket d'un serveur X de confiance, connexion refusée");
                return;
            }

            server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Track(server);
            await server.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), _stop.Token).ConfigureAwait(false);
            var setup = X11Setup.Rewrite(buffer.AsSpan(0, length), _cookie is null ? null : XAuthority.MagicCookie, _cookie);
            await server.SendAsync(setup, SocketFlags.None, _stop.Token).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(setup);
            if (received > length)
            {
                await server.SendAsync(buffer.AsMemory(length, received - length), SocketFlags.None, _stop.Token).ConfigureAwait(false);
            }

            await Task.WhenAny(PumpAsync(client, server), PumpAsync(server, client)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested)
            {
                DebugLog.Write("x11", $"{_label} : relais X11", e);
            }
        }
        finally
        {
            Close(client);
            if (server is not null)
            {
                Close(server);
            }
        }
    }

    private async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = new byte[65536];
        while (true)
        {
            int read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stop.Token).ConfigureAwait(false);
        }
    }

    private void Track(Socket socket)
    {
        lock (_open)
        {
            if (_stop.IsCancellationRequested)
            {
                socket.Dispose();
                throw new ObjectDisposedException(nameof(X11UnixRelay));
            }

            _open.Add(socket);
        }
    }

    private void Close(Socket socket)
    {
        lock (_open)
        {
            _open.Remove(socket);
        }

        socket.Dispose();
    }

    public void Dispose()
    {
        lock (_open)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
        }

        _listener.Stop();
        Socket[] open;
        lock (_open)
        {
            open = [.. _open];
            _open.Clear();
        }

        foreach (var socket in open)
        {
            socket.Dispose();
        }

        CryptographicOperations.ZeroMemory(_token);
        if (_cookie is not null)
        {
            CryptographicOperations.ZeroMemory(_cookie);
        }
    }
}
