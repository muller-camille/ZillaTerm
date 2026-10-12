using System.Net;
using System.Net.Sockets;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Relais TCP local vers un serveur, que le test coupe comme une perte du réseau : connexions en cours réinitialisées,
/// nouvelles connexions refusées (acceptées puis réinitialisées) jusqu'à ce qu'il soit rétabli.
/// </summary>
internal sealed class TcpRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Socket> _open = [];
    private readonly Task _accept;
    private volatile bool _cut;

    public TcpRelay(IPEndPoint target)
    {
        _target = target;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Cut()
    {
        _cut = true;
        lock (_open)
        {
            foreach (var socket in _open)
            {
                Reset(socket);
            }

            _open.Clear();
        }
    }

    public void Restore() => _cut = false;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        await _accept;
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (_cut)
            {
                Reset(client);
                continue;
            }

            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(Socket client)
    {
        var server = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await server.ConnectAsync(_target, _stop.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Reset(client);
            server.Dispose();
            return;
        }

        lock (_open)
        {
            _open.Add(client);
            _open.Add(server);
        }

        // Un côté fermé : l'autre aussi, comme une connexion directe.
        await Task.WhenAny(CopyAsync(client, server), CopyAsync(server, client));
        lock (_open)
        {
            _open.Remove(client);
            _open.Remove(server);
        }

        Reset(client);
        Reset(server);
    }

    private async Task CopyAsync(Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                for (int sent = 0; sent < read;)
                {
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, _stop.Token);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Connexion coupée (par le test ou par un des deux côtés).
        }
    }

    /// <summary>Fermeture brutale (RST) : l'autre côté voit la connexion perdue tout de suite.</summary>
    private static void Reset(Socket socket)
    {
        try
        {
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Déjà fermée.
        }
    }
}
