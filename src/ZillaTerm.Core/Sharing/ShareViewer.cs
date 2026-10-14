using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Sharing;

/// <summary>
/// Regarder une session partagée par un autre ZillaTerm : connexion directe au poste qui partage (ses adresses, essayées
/// ensemble), sinon par le dossier relais ; ouverture authentifiée par le secret de l'invitation ; demande (nom, poste) ;
/// puis attente de l'accord. Plusieurs se regardent en même temps : une instance par session regardée.
/// </summary>
/// <remarks>Les évènements arrivent sur un fil de travail.</remarks>
public sealed class ShareViewer : IAsyncDisposable
{
    /// <summary>Attente au plus d'une connexion directe à une adresse.</summary>
    public static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Attente au plus de la réponse par le dossier relais (relu toutes les demi-secondes de l'autre côté).</summary>
    public static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Attente au plus de l'accord de celui qui partage.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private readonly IShareLink _link;
    private readonly ShareCipher _cipher;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<ShareInfo> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastHeard = DateTime.UtcNow.Ticks;
    private int _ended;

    private ShareViewer(IShareLink link, ShareCipher cipher, string verificationCode)
    {
        _link = link;
        _cipher = cipher;
        VerificationCode = verificationCode;
    }

    /// <summary>Code de vérification (le même chez celui qui partage).</summary>
    public string VerificationCode { get; }

    /// <summary>Adresse du poste qui partage, ou « relay ».</summary>
    public string Route => _link.Route;

    public bool IsRelay => _link.IsRelay;

    /// <summary>Ce qui est partagé, une fois l'accord donné ; en échec (<see cref="ShareException"/>) si la demande est refusée.</summary>
    public Task<ShareInfo> Accepted => _accepted.Task;

    /// <summary>Écran, flux du terminal, image d'écran, main donnée ou reprise, avis.</summary>
    public event Action<ShareMessage>? MessageReceived;

    /// <summary>Fin : partage arrêté, exclu, connexion perdue, ou départ.</summary>
    public event Action<ShareEndReason>? Ended;

    /// <summary>
    /// Se connecte et demande à regarder ; l'accord se lit ensuite dans <see cref="Accepted"/>.
    /// </summary>
    /// <exception cref="ShareException">Invitation terminée, poste injoignable, invitation refusée, dossier introuvable.</exception>
    public static async Task<ShareViewer> ConnectAsync(ShareInvitation invitation, ShareJoin join, CancellationToken cancellationToken)
    {
        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            throw new ShareException(ShareFailure.Expired);
        }

        bool rejected = false;
        if (invitation.Endpoints.Count > 0)
        {
            var (viewer, refused) = await ConnectDirectAsync(invitation, cancellationToken).ConfigureAwait(false);
            rejected = refused;
            if (viewer is not null)
            {
                await viewer.BeginAsync(join, cancellationToken).ConfigureAwait(false);
                return viewer;
            }
        }

        if (invitation.RelayFolder is { } relay && ShareInvitation.IsRelayFolder(relay))
        {
            var viewer = await ConnectRelayAsync(invitation, relay, cancellationToken).ConfigureAwait(false);
            await viewer.BeginAsync(join, cancellationToken).ConfigureAwait(false);
            return viewer;
        }

        throw new ShareException(rejected ? ShareFailure.Rejected : ShareFailure.Unreachable);
    }

    /// <summary>
    /// Toutes les adresses à la fois ; la première dont l'ouverture réussit est gardée. Refusée : l'invitation n'est pas
    /// celle de ce poste (ou plus).
    /// </summary>
    private static async Task<(ShareViewer? Viewer, bool Rejected)> ConnectDirectAsync(ShareInvitation invitation, CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = invitation.Endpoints.Select(endpoint => TryDirectAsync(invitation, endpoint, race.Token)).ToList();
        bool rejected = false;
        ShareViewer? winner = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            var (viewer, refused) = await done.ConfigureAwait(false);
            rejected |= refused;
            if (viewer is null)
            {
                continue;
            }

            if (winner is null)
            {
                winner = viewer;
                race.Cancel();
            }
            else
            {
                await viewer.DisposeAsync().ConfigureAwait(false);
            }
        }

        return (winner, rejected && winner is null);
    }

    private static async Task<(ShareViewer? Viewer, bool Rejected)> TryDirectAsync(ShareInvitation invitation, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var client = new TcpClient(endpoint.AddressFamily);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DirectTimeout);
            await client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var link = new TcpShareLink(client);
            timeout.CancelAfter(ShareHost.HandshakeTimeout);
            var viewer = await HandshakeAsync(invitation, link, timeout.Token).ConfigureAwait(false);
            return viewer is null ? (null, true) : (viewer, false);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or InvalidDataException)
        {
            client.Dispose();
            return (null, false);
        }
    }

    private static async Task<ShareViewer> ConnectRelayAsync(ShareInvitation invitation, string relay, CancellationToken cancellationToken)
    {
        var folder = ShareNetwork.ShareFolder(relay, invitation);
        try
        {
            if (!Directory.Exists(folder))
            {
                throw new ShareException(ShareFailure.NotFound, folder);
            }

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var answers = Path.Combine(folder, id + ".h");
            // Le fichier des réponses existe avant la demande : sa relecture ne tombe jamais sur un « fichier absent » mis
            // en cache par le client SMB.
            using (new FileStream(answers, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
            }

            var link = new FolderShareLink(answers, Path.Combine(folder, id + ".g"), deleteOnClose: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RelayTimeout);
            try
            {
                return await HandshakeAsync(invitation, link, timeout.Token).ConfigureAwait(false)
                       ?? throw new ShareException(ShareFailure.Rejected);
            }
            catch (Exception e) when (e is not ShareException)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                throw new ShareException(
                    ShareFailure.Unreachable, e.Message, e);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ShareException(ShareFailure.Unreachable, e.Message, e);
        }
    }

    /// <summary>Bonjour, réponse ; null si la réponse n'est pas authentifiée (invitation qui n'est pas celle de ce poste).</summary>
    private static async Task<ShareViewer?> HandshakeAsync(ShareInvitation invitation, IShareLink link, CancellationToken cancellationToken)
    {
        using var state = ShareHandshake.CreateHello(invitation);
        try
        {
            await link.SendAsync(state.Hello, cancellationToken).ConfigureAwait(false);
            var welcome = await link.ReceiveAsync(ShareHandshake.WelcomeLength, cancellationToken).ConfigureAwait(false);
            if (welcome is null || ShareHandshake.AcceptWelcome(state, welcome) is not { } keys)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new ShareViewer(link, new ShareCipher(keys), keys.VerificationCode);
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task BeginAsync(ShareJoin join, CancellationToken cancellationToken)
    {
        await SendAsync(ShareMessage.Join(join), cancellationToken).ConfigureAwait(false);
        _ = ReceiveLoopAsync(_stop.Token);
        _ = WatchSilenceAsync(_stop.Token);
        _ = Task.Delay(ApprovalTimeout, _stop.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && _accepted.TrySetException(new ShareException(ShareFailure.Refused)))
            {
                End(ShareEndReason.Refused, sendLeave: true);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Saisie envoyée à celui qui partage (prise en compte seulement si la main a été donnée).</summary>
    public Task SendInputAsync(TerminalInput input) => SendAsync(ShareMessage.Input(input), _stop.Token);

    /// <summary>Demande la main (vrai) ou la rend (faux).</summary>
    public Task RequestControlAsync(bool want) => SendAsync(ShareMessage.ControlRequest(want), _stop.Token);

    private async Task SendAsync(ShareMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _link.SendAsync(_cipher.Seal(message.ToBytes()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            End(ShareEndReason.Lost, sendLeave: false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var sealedData = await _link.ReceiveAsync(ShareMessage.MaxLength + ShareCipher.TagLength, stop).ConfigureAwait(false);
                if (sealedData is null || _cipher.Open(sealedData) is not { } plain || ShareMessage.Parse(plain) is not { } message)
                {
                    End(ShareEndReason.Lost, sendLeave: false);
                    return;
                }

                Volatile.Write(ref _lastHeard, DateTime.UtcNow.Ticks);
                switch (message.Type)
                {
                    case ShareMessageType.Info:
                        if (message.ReadInfo() is { } info)
                        {
                            _accepted.TrySetResult(info);
                        }

                        break;
                    case ShareMessageType.Ping:
                        _ = SendAsync(ShareMessage.Pong(), stop);
                        break;
                    case ShareMessageType.Bye:
                        var reason = (ShareEndReason)message.ReadByte();
                        if (_accepted.TrySetException(new ShareException(reason switch
                            {
                                ShareEndReason.Full => ShareFailure.Full,
                                ShareEndReason.Expired or ShareEndReason.Stopped => ShareFailure.Expired,
                                _ => ShareFailure.Refused,
                            })))
                        {
                            End(reason, sendLeave: false);
                            return;
                        }

                        End(Enum.IsDefined(reason) ? reason : ShareEndReason.Stopped, sendLeave: false);
                        return;
                    default:
                        if (_accepted.Task.IsCompletedSuccessfully)
                        {
                            MessageReceived?.Invoke(message);
                        }

                        break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            End(ShareEndReason.Lost, sendLeave: false);
        }
    }

    private async Task WatchSilenceAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                if (DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastHeard), DateTimeKind.Utc) > SilenceLimit)
                {
                    End(ShareEndReason.Lost, sendLeave: false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fini.
        }
    }

    private void End(ShareEndReason reason, bool sendLeave)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        _accepted.TrySetException(new ShareException(ShareFailure.Refused));
        _ = Task.Run(async () =>
        {
            if (sendLeave)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _link.SendAsync(_cipher.Seal(ShareMessage.Leave().ToBytes()), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // Déjà fermé de l'autre côté.
                }
            }

            _stop.Cancel();
            await _link.DisposeAsync().ConfigureAwait(false);
            _cipher.Dispose();
        });
        DebugLog.Write("partage", $"Session regardée terminée ({reason}).");
        Ended?.Invoke(reason);
    }

    /// <summary>Quitte la session regardée (celui qui partage en est prévenu).</summary>
    public ValueTask DisposeAsync()
    {
        End(ShareEndReason.Left, sendLeave: true);
        return ValueTask.CompletedTask;
    }
}
