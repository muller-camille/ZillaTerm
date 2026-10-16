using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Sharing;

/// <summary>Réglages des partages de ce poste.</summary>
public sealed record ShareHostOptions
{
    /// <summary>Port d'écoute des connexions directes ; 0 : tiré au hasard (le pare-feu de l'équipe peut en fixer un).</summary>
    public int Port { get; init; }

    /// <summary>Accepter les connexions directes (un port est ouvert pendant les partages).</summary>
    public bool Direct { get; init; } = true;

    /// <summary>Dossier relais (partage réseau de l'équipe) ; null : pas de relais.</summary>
    public string? RelayFolder { get; init; }

    /// <summary>Personnes qui regardent un même partage, au plus.</summary>
    public int MaxWatchers { get; init; } = 5;
}

/// <summary>
/// Partages de ce poste : un seul port d'écoute pour tous (ouvert du premier partage au dernier), et un dossier par
/// partage dans le dossier relais. Chaque connexion s'ouvre par un échange authentifié par le secret de l'invitation
/// (<see cref="ShareHandshake"/>), puis attend l'accord de celui qui partage.
/// </summary>
/// <remarks>
/// Les évènements arrivent sur des fils de travail ; les méthodes s'appellent de n'importe quel fil.
/// </remarks>
public sealed class ShareHost : IAsyncDisposable
{
    /// <summary>Partages en même temps, au plus.</summary>
    public const int MaxShares = 10;

    /// <summary>Ouvertures d'échange en cours, au plus (connexions qui n'ont pas encore dit pour quel partage).</summary>
    private const int MaxPendingHandshakes = 64;

    /// <summary>Ouvertures en attente au plus depuis une même adresse : un poste seul ne bloque pas les connexions directes.</summary>
    internal const int MaxPendingPerAddress = 4;

    /// <summary>Attente au plus du bonjour d'une connexion, puis de sa demande.</summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly List<HostedShare> _shares = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _listening;
    private int _pendingHandshakes;
    private readonly Dictionary<IPAddress, int> _pendingFrom = [];

    public ShareHost(ShareHostOptions options) => Options = options;

    /// <summary>Réglages des prochains partages.</summary>
    public ShareHostOptions Options { get; set; }

    public IReadOnlyList<HostedShare> Shares
    {
        get
        {
            lock (_lock)
            {
                return [.. _shares];
            }
        }
    }

    /// <summary>Port d'écoute ; null hors partage ou sans connexion directe.</summary>
    public int? ListeningPort
    {
        get
        {
            lock (_lock)
            {
                return (_listener?.LocalEndpoint as IPEndPoint)?.Port;
            }
        }
    }

    /// <summary>Partages commencés ou arrêtés.</summary>
    public event Action? SharesChanged;

    /// <summary>
    /// Commence un partage. La connexion directe est tentée si elle est permise (port ouvert pour tous les partages) ;
    /// le dossier du partage est créé dans le dossier relais s'il y en a un.
    /// </summary>
    /// <exception cref="ShareException">Ni connexion directe ni dossier relais possible (raison dans le message).</exception>
    /// <exception cref="InvalidOperationException">Trop de partages.</exception>
    public HostedShare Start(ShareInfo info)
    {
        var options = Options;
        lock (_lock)
        {
            if (_shares.Count >= MaxShares)
            {
                throw new InvalidOperationException("Too many shares.");
            }
        }

        var endpoints = new List<IPEndPoint>();
        Exception? directProblem = null;
        if (options.Direct)
        {
            try
            {
                int port = EnsureListening(options.Port);
                endpoints.AddRange(ShareNetwork.LocalAddresses().Select(a => new IPEndPoint(a, port)));
            }
            catch (SocketException e)
            {
                directProblem = e;
                DebugLog.Write("partage", "Écoute des connexions directes impossible", e);
            }
        }

        string? relay = ShareInvitation.IsRelayFolder(options.RelayFolder) ? options.RelayFolder : null;
        var invitation = ShareInvitation.Create(endpoints, relay, info.EndsAt);
        string? folder = null;
        if (relay is not null)
        {
            try
            {
                folder = ShareNetwork.ShareFolder(relay, invitation);
                Directory.CreateDirectory(folder);
                CleanStaleFolders(relay);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write("partage", "Dossier relais inaccessible", e);
                folder = null;
                invitation = invitation with { RelayFolder = null };
                directProblem ??= e;
            }
        }

        if (invitation.Endpoints.Count == 0 && folder is null)
        {
            StopListeningIfIdle();
            throw new ShareException(ShareFailure.Unreachable, directProblem?.Message, directProblem);
        }

        var share = new HostedShare(this, invitation, info, folder, options.MaxWatchers);
        lock (_lock)
        {
            _shares.Add(share);
        }

        share.Begin();
        DebugLog.Write("partage", $"Partage {invitation.IdText} commencé ({invitation.Endpoints.Count} adresses, relais : {folder is not null}).");
        SharesChanged?.Invoke();
        return share;
    }

    /// <summary>Partage arrêté : retiré, et le port fermé s'il n'en reste plus.</summary>
    internal void Remove(HostedShare share)
    {
        lock (_lock)
        {
            if (!_shares.Remove(share))
            {
                return;
            }
        }

        StopListeningIfIdle();
        SharesChanged?.Invoke();
    }

    /// <summary>Arrête tous les partages (fermeture de ZillaTerm, verrouillage de Windows).</summary>
    public void StopAll(ShareEndReason reason)
    {
        foreach (var share in Shares)
        {
            share.Stop(reason);
        }
    }

    public ValueTask DisposeAsync()
    {
        StopAll(ShareEndReason.ApplicationClosed);
        StopListeningIfIdle();
        return ValueTask.CompletedTask;
    }

    private int EnsureListening(int port)
    {
        lock (_lock)
        {
            if (_listener is not null)
            {
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }

            TcpListener listener;
            try
            {
                // IPv4 et IPv6 sur le même port.
                listener = new TcpListener(IPAddress.IPv6Any, Math.Clamp(port, 0, 65535));
                listener.Server.DualMode = true;
                listener.Start();
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressFamilyNotSupported)
            {
                // IPv6 désactivé sur le poste.
                listener = new TcpListener(IPAddress.Any, Math.Clamp(port, 0, 65535));
                listener.Start();
            }

            _listener = listener;
            _listening = new CancellationTokenSource();
            _ = AcceptAsync(listener, _listening.Token);
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private void StopListeningIfIdle()
    {
        lock (_lock)
        {
            if (_shares.Count > 0 || _listener is null)
            {
                return;
            }

            _listening?.Cancel();
            _listener.Stop();
            _listener = null;
            _listening = null;
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (client.Client.RemoteEndPoint is not IPEndPoint remote || client.Client.LocalEndPoint is not IPEndPoint local
                || !Invited(local.Address) || !TryStartHandshake(Normalize(remote.Address)))
            {
                client.Dispose();
                continue;
            }

            _ = HandleAsync(new TcpShareLink(client), Normalize(remote.Address));
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Connexion arrivée par une adresse de l'invitation d'un partage en cours (ou la boucle locale) : l'écoute porte sur
    /// toutes les cartes, mais une carte qui n'est pas dans l'invitation (Wi-Fi de la maison, réseau virtuel) est refusée.
    /// </summary>
    internal bool Invited(IPAddress local)
    {
        local = Normalize(local);
        if (IPAddress.IsLoopback(local))
        {
            return true;
        }

        lock (_lock)
        {
            return _shares.Any(s => s.Invitation.Endpoints.Any(e => Normalize(e.Address).Equals(local)));
        }
    }

    /// <summary>Ouverture comptée, si ni le total ni celles venues de cette adresse ne sont au plus haut.</summary>
    internal bool TryStartHandshake(IPAddress from)
    {
        lock (_lock)
        {
            int fromThere = _pendingFrom.GetValueOrDefault(from);
            if (_pendingHandshakes >= MaxPendingHandshakes || fromThere >= MaxPendingPerAddress)
            {
                return false;
            }

            _pendingHandshakes++;
            _pendingFrom[from] = fromThere + 1;
            return true;
        }
    }

    internal void EndHandshake(IPAddress from)
    {
        lock (_lock)
        {
            _pendingHandshakes--;
            if (--_pendingFrom[from] == 0)
            {
                _pendingFrom.Remove(from);
            }
        }
    }

    /// <summary>Connexion directe : son bonjour dit quel partage elle vise.</summary>
    private async Task HandleAsync(IShareLink link, IPAddress from)
    {
        try
        {
            using var timeout = new CancellationTokenSource(HandshakeTimeout);
            var hello = await link.ReceiveAsync(ShareHandshake.HelloLength, timeout.Token).ConfigureAwait(false);
            HostedShare? share = null;
            if (hello is not null && ShareHandshake.TryReadShareId(hello, out var id))
            {
                lock (_lock)
                {
                    share = _shares.FirstOrDefault(s => s.Invitation.ShareId.AsSpan().SequenceEqual(id));
                }
            }

            if (share is null || hello is null)
            {
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await share.OpenAsync(link, hello).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            EndHandshake(from);
        }
    }

    /// <summary>Dossiers de partages oubliés (ZillaTerm arrêté brutalement) depuis plus de deux jours : effacés.</summary>
    private static void CleanStaleFolders(string relay)
    {
        try
        {
            foreach (var folder in new DirectoryInfo(relay).EnumerateDirectories(ShareNetwork.RelayFolderPrefix + "*"))
            {
                if (DateTime.UtcNow - folder.LastWriteTimeUtc > TimeSpan.FromDays(2))
                {
                    folder.Delete(recursive: true);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dossier d'un autre poste encore ouvert, ou droits insuffisants : laissé.
        }
    }
}

/// <summary>
/// Un partage en cours : son invitation, les personnes qui regardent et celles qui attendent l'accord. Ce qui est
/// diffusé (<see cref="Broadcast"/>) part vers toutes les personnes acceptées, dans l'ordre, chacune à son rythme.
/// </summary>
public sealed class HostedShare
{
    /// <summary>Bonjours non authentifiés (invitation fausse) au-delà desquels le partage s'arrête.</summary>
    public const int MaxFailedAttempts = 5;

    private readonly ShareHost _host;
    private readonly object _lock = new();
    private readonly List<ShareWatcher> _watchers = [];
    private readonly string? _relayShareFolder;
    private readonly int _maxWatchers;
    private readonly CancellationTokenSource _stop = new();
    private int _failedAttempts;
    private int _stopped;

    internal HostedShare(ShareHost host, ShareInvitation invitation, ShareInfo info, string? relayShareFolder, int maxWatchers)
    {
        _host = host;
        Invitation = invitation;
        Info = info;
        _relayShareFolder = relayShareFolder;
        _maxWatchers = Math.Clamp(maxWatchers, 1, 20);
        StartedAt = DateTime.UtcNow;
    }

    public ShareInvitation Invitation { get; }

    public ShareInfo Info { get; }

    public DateTime StartedAt { get; }

    /// <summary>Le partage passe aussi par le dossier relais.</summary>
    public bool UsesRelay => _relayShareFolder is not null;

    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    public int FailedAttempts => Volatile.Read(ref _failedAttempts);

    /// <summary>Personnes acceptées qui regardent, et celles qui attendent l'accord.</summary>
    public IReadOnlyList<ShareWatcher> Watchers
    {
        get
        {
            lock (_lock)
            {
                return [.. _watchers];
            }
        }
    }

    /// <summary>Personne qui a la main ; null : lecture seule pour tous.</summary>
    public ShareWatcher? Controller
    {
        get
        {
            lock (_lock)
            {
                return _watchers.FirstOrDefault(w => w.HasControl);
            }
        }
    }

    /// <summary>Quelqu'un demande à regarder : <see cref="Accept"/> ou <see cref="Refuse"/>.</summary>
    public event Action<ShareWatcher>? JoinRequested;

    /// <summary>Arrivée, départ, main donnée ou reprise.</summary>
    public event Action? WatchersChanged;

    /// <summary>Quelqu'un est parti : de lui-même, perdu, exclu, ou demande retirée avant l'accord.</summary>
    public event Action<ShareWatcher, ShareEndReason>? WatcherLeft;

    /// <summary>Saisie de la personne qui a la main.</summary>
    public event Action<ShareWatcher, TerminalInput>? InputReceived;

    /// <summary>Une personne demande la main (vrai) ou la rend (faux).</summary>
    public event Action<ShareWatcher, bool>? ControlRequested;

    /// <summary>Une personne qui regarde a pris du retard (réseau lent) : lui renvoyer l'écran entier.</summary>
    public event Action<ShareWatcher>? ResyncNeeded;

    /// <summary>Invitations fausses répétées : le partage s'arrête de lui-même.</summary>
    public event Action<ShareEndReason>? Stopped;

    internal void Begin()
    {
        if (_relayShareFolder is not null)
        {
            _ = WatchRelayAsync(_relayShareFolder, _stop.Token);
        }

        var remaining = Info.EndsAt - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero && remaining < TimeSpan.FromDays(7))
        {
            _ = Task.Delay(remaining, _stop.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    Stop(ShareEndReason.Expired);
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Ouverture d'un échange : réponse au bonjour, puis attente de la demande (nom, poste).</summary>
    internal async Task OpenAsync(IShareLink link, byte[] hello)
    {
        if (IsStopped)
        {
            await link.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (ShareHandshake.AnswerHello(hello, Invitation.Secret) is not { } answer)
        {
            DebugLog.Write("partage", $"Invitation refusée (partage {Invitation.IdText}, {link.Route}).");
            await link.DisposeAsync().ConfigureAwait(false);
            if (Interlocked.Increment(ref _failedAttempts) >= MaxFailedAttempts)
            {
                Stop(ShareEndReason.TooManyAttempts);
            }

            return;
        }

        var (welcome, keys) = answer;
        var cipher = new ShareCipher(keys);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(ShareHost.HandshakeTimeout);
        await link.SendAsync(welcome, timeout.Token).ConfigureAwait(false);
        var sealedJoin = await link.ReceiveAsync(ShareMessage.MaxGuestLength, timeout.Token).ConfigureAwait(false);
        if (sealedJoin is null || cipher.Open(sealedJoin) is not { } plain || ShareMessage.Parse(plain) is not { Type: ShareMessageType.Join } message
            || message.ReadJoin() is not { } join)
        {
            cipher.Dispose();
            await link.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var watcher = new ShareWatcher(this, link, cipher, join, keys.VerificationCode);
        lock (_lock)
        {
            if (IsStopped || _watchers.Count >= _maxWatchers)
            {
                watcher.Close(IsStopped ? ShareEndReason.Stopped : ShareEndReason.Full, notify: false);
                return;
            }

            _watchers.Add(watcher);
        }

        watcher.Start();
        DebugLog.Write("partage", $"Demande pour le partage {Invitation.IdText} ({link.Route}).");
        JoinRequested?.Invoke(watcher);
        WatchersChanged?.Invoke();
    }

    /// <summary>
    /// Accepte une personne : elle reçoit ce qui est partagé (<see cref="Info"/>), puis <paramref name="initial"/>
    /// (l'écran actuel), puis tout ce qui sera diffusé. Pour que rien ne manque ni ne soit en double, appeler depuis le
    /// fil qui diffuse.
    /// </summary>
    public void Accept(ShareWatcher watcher, IEnumerable<ShareMessage> initial)
    {
        lock (_lock)
        {
            if (watcher.State != ShareWatcherState.Pending || !_watchers.Contains(watcher))
            {
                return;
            }

            watcher.Enqueue(ShareMessage.Info(Info));
            foreach (var message in initial)
            {
                watcher.Enqueue(message);
            }

            watcher.State = ShareWatcherState.Watching;
            watcher.AcceptedAt = DateTime.UtcNow;
        }

        WatchersChanged?.Invoke();
    }

    public void Refuse(ShareWatcher watcher) => watcher.Close(ShareEndReason.Refused);

    /// <summary>Exclut une personne (qui ne pourra revenir qu'avec un nouvel accord).</summary>
    public void Exclude(ShareWatcher watcher) => watcher.Close(ShareEndReason.Excluded);

    /// <summary>Diffuse un message à toutes les personnes acceptées.</summary>
    public void Broadcast(ShareMessage message)
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers)
            {
                if (watcher.State == ShareWatcherState.Watching)
                {
                    watcher.Enqueue(message);
                }
            }
        }
    }

    /// <summary>Message pour une seule personne (écran renvoyé après un retard).</summary>
    public void Send(ShareWatcher watcher, ShareMessage message)
    {
        lock (_lock)
        {
            if (watcher.State == ShareWatcherState.Watching)
            {
                watcher.Enqueue(message);
            }
        }
    }

    /// <summary>Donne la main à une personne (terminal seulement, si le partage le permet) ; la reprend aux autres.</summary>
    public void GiveControl(ShareWatcher watcher)
    {
        if (!Info.ControlAllowed || Info.Kind != ShareKind.Terminal)
        {
            return;
        }

        lock (_lock)
        {
            if (watcher.State != ShareWatcherState.Watching)
            {
                return;
            }

            foreach (var other in _watchers.Where(w => w.HasControl && w != watcher))
            {
                other.HasControl = false;
                other.Enqueue(ShareMessage.Control(false));
            }

            if (!watcher.HasControl)
            {
                watcher.HasControl = true;
                watcher.Enqueue(ShareMessage.Control(true));
            }
        }

        WatchersChanged?.Invoke();
    }

    /// <summary>Reprend la main : lecture seule pour tous.</summary>
    public void TakeControl()
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var watcher in _watchers.Where(w => w.HasControl))
            {
                watcher.HasControl = false;
                watcher.Enqueue(ShareMessage.Control(false));
                changed = true;
            }
        }

        if (changed)
        {
            WatchersChanged?.Invoke();
        }
    }

    /// <summary>Arrête le partage : chacun est prévenu, le port se ferme s'il n'y a plus de partage, le dossier relais s'efface.</summary>
    public void Stop(ShareEndReason reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        foreach (var watcher in Watchers)
        {
            watcher.Close(reason, notify: false);
        }

        _stop.Cancel();
        _host.Remove(this);
        if (_relayShareFolder is not null)
        {
            _ = Task.Run(async () =>
            {
                // Les fichiers des personnes qui regardaient se ferment d'abord.
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(_relayShareFolder, recursive: true);
                        return;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
            });
        }

        DebugLog.Write("partage", $"Partage {Invitation.IdText} arrêté ({reason}).");
        Stopped?.Invoke(reason);
    }

    /// <summary>Connexions arrivées par le dossier relais : un fichier « .g » par personne.</summary>
    private async Task WatchRelayAsync(string folder, CancellationToken stop)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.g"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length != 32 || !name.All(char.IsAsciiHexDigit) || !known.Add(name))
                    {
                        continue;
                    }

                    var link = new FolderShareLink(file, Path.Combine(folder, name + ".h"), deleteOnClose: false);
                    _ = OpenRelayAsync(link, stop);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Partage réseau momentanément indisponible : nouvel essai au tour suivant.
            }

            try
            {
                await Task.Delay(FolderShareLink.PollInterval * 2, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task OpenRelayAsync(IShareLink link, CancellationToken stop)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(ShareHost.HandshakeTimeout);
            var hello = await link.ReceiveAsync(ShareHandshake.HelloLength, timeout.Token).ConfigureAwait(false);
            if (hello is null || !ShareHandshake.TryReadShareId(hello, out var id) || !id.AsSpan().SequenceEqual(Invitation.ShareId))
            {
                await link.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await OpenAsync(link, hello).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <param name="notify">Prévenir (<see cref="WatcherLeft"/>) ; pas à l'arrêt du partage, qui le dit lui-même.</param>
    internal void Departed(ShareWatcher watcher, ShareEndReason reason, bool notify)
    {
        lock (_lock)
        {
            if (!_watchers.Remove(watcher))
            {
                return;
            }
        }

        DebugLog.Write("partage", $"Départ ({reason}) du partage {Invitation.IdText}.");
        if (notify)
        {
            WatcherLeft?.Invoke(watcher, reason);
            WatchersChanged?.Invoke();
        }
    }

    internal void Received(ShareWatcher watcher, ShareMessage message)
    {
        switch (message.Type)
        {
            case ShareMessageType.Input when watcher.HasControl && watcher.State == ShareWatcherState.Watching:
                if (message.ReadInput() is { } input)
                {
                    InputReceived?.Invoke(watcher, input);
                }

                break;
            case ShareMessageType.ControlRequest when watcher.State == ShareWatcherState.Watching:
                bool want = message.ReadFlag();
                if (!want && watcher.HasControl)
                {
                    lock (_lock)
                    {
                        watcher.HasControl = false;
                    }

                    WatchersChanged?.Invoke();
                }

                if (Info.ControlAllowed)
                {
                    ControlRequested?.Invoke(watcher, want);
                }

                break;
        }
    }

    internal void Lagging(ShareWatcher watcher) => ResyncNeeded?.Invoke(watcher);
}

public enum ShareWatcherState
{
    Pending,
    Watching,
    Closed,
}

/// <summary>Une personne qui regarde un partage (ou attend l'accord) : sa connexion, sa file d'envoi, son état.</summary>
public sealed class ShareWatcher
{
    /// <summary>Retard au-delà duquel le terminal et les images en attente sont abandonnés et l'écran renvoyé entier.</summary>
    internal const long MaxQueuedBytes = 48L << 20;

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    /// <summary>Après la fermeture, délai au-delà duquel un envoi bloqué (personne qui ne lit plus) est abandonné.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly HostedShare _share;
    private readonly IShareLink _link;
    private readonly ShareCipher _cipher;
    // Lue par la boucle d'envoi et, quand le réseau ne suit plus, par Enqueue qui l'allège : plusieurs lecteurs.
    private readonly Channel<Queued> _queue = Channel.CreateUnbounded<Queued>(new UnboundedChannelOptions { SingleReader = false });
    private readonly CancellationTokenSource _stop = new();
    private long _queuedBytes;
    private long _lastHeard = DateTime.UtcNow.Ticks;
    private int _closed;
    private int _sending;
    private int _finished;
    private int _closeReason;

    internal ShareWatcher(HostedShare share, IShareLink link, ShareCipher cipher, ShareJoin join, string verificationCode)
    {
        _share = share;
        _link = link;
        _cipher = cipher;
        Join = join;
        VerificationCode = verificationCode;
        RequestedAt = DateTime.UtcNow;
    }

    public ShareJoin Join { get; }

    /// <summary>Code de vérification de cette connexion (à comparer de vive voix).</summary>
    public string VerificationCode { get; }

    /// <summary>Adresse de la personne, ou « relay » pour le dossier relais.</summary>
    public string Route => _link.Route;

    public bool IsRelay => _link.IsRelay;

    public DateTime RequestedAt { get; }

    public DateTime? AcceptedAt { get; internal set; }

    public ShareWatcherState State { get; internal set; } = ShareWatcherState.Pending;

    public bool HasControl { get; internal set; }

    public HostedShare Share => _share;

    internal void Start()
    {
        Volatile.Write(ref _sending, 1);
        _ = SendLoopAsync(_stop.Token);
        _ = ReceiveLoopAsync(_stop.Token);
        _ = PingLoopAsync(_stop.Token);
    }

    /// <summary>Message en attente d'envoi, avec son type (pour alléger la file sans perdre les messages d'état).</summary>
    private readonly record struct Queued(ShareMessageType Type, byte[] Data);

    /// <summary>Flux du terminal et images : remplacés par l'écran entier quand le réseau ne suit plus.</summary>
    private static bool IsBulk(ShareMessageType type) =>
        type is ShareMessageType.TerminalData or ShareMessageType.ScreenFrame or ShareMessageType.TerminalReset;

    internal void Enqueue(ShareMessage message)
    {
        var item = new Queued(message.Type, message.ToBytes());
        if (Interlocked.Add(ref _queuedBytes, item.Data.Length) > MaxQueuedBytes && IsBulk(message.Type))
        {
            // Réseau trop lent pour suivre : le terminal et les images en attente sont abandonnés (l'écran sera renvoyé
            // entier). Les messages d'état restent, dans leur ordre : main reprise, affichage en pause ou repris, avis.
            var kept = new List<Queued>();
            while (_queue.Reader.TryRead(out var queued))
            {
                if (IsBulk(queued.Type))
                {
                    Interlocked.Add(ref _queuedBytes, -queued.Data.Length);
                }
                else
                {
                    kept.Add(queued);
                }
            }

            foreach (var queued in kept)
            {
                _queue.Writer.TryWrite(queued);
            }

            Interlocked.Add(ref _queuedBytes, -item.Data.Length);
            _share.Lagging(this);
            return;
        }

        _queue.Writer.TryWrite(item);
    }

    /// <summary>Types des messages en attente, dans l'ordre (tests).</summary>
    internal IReadOnlyList<ShareMessageType> QueuedTypes()
    {
        var items = new List<Queued>();
        while (_queue.Reader.TryRead(out var queued))
        {
            items.Add(queued);
        }

        foreach (var queued in items)
        {
            _queue.Writer.TryWrite(queued);
        }

        return [.. items.Select(q => q.Type)];
    }

    /// <summary>
    /// Ferme la connexion ; <paramref name="notify"/> : prévenir le partage (départ). Ce qui attendait encore dans la file
    /// ne part plus (personne exclue : plus rien ne lui est montré) ; seul l'au revoir, avec la raison, part, scellé par
    /// la boucle d'envoi après le dernier message (les messages se suivent : un message hors d'ordre ne s'ouvrirait pas).
    /// </summary>
    internal void Close(ShareEndReason reason, bool notify = true)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closeReason, (int)reason);
        State = ShareWatcherState.Closed;
        HasControl = false;
        _queue.Writer.TryComplete();
        _ = Task.Delay(CloseTimeout).ContinueWith(_ => CancelQuietly(), TaskScheduler.Default);
        if (Volatile.Read(ref _sending) == 0)
        {
            // Pas encore de boucle d'envoi (partage plein, arrêt pendant l'ouverture) : l'au revoir part d'ici.
            _ = FinishAsync();
        }

        _share.Departed(this, reason, notify);
    }

    private async Task SendLoopAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -queued.Data.Length);
                if (Volatile.Read(ref _closed) != 0)
                {
                    break;
                }

                await _link.SendAsync(_cipher.Seal(queued.Data), stop).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            Close(ShareEndReason.Lost);
        }

        await FinishAsync().ConfigureAwait(false);
    }

    /// <summary>Au revoir (sauf connexion perdue), puis fermeture du lien ; une seule fois.</summary>
    private async Task FinishAsync()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        var reason = (ShareEndReason)Volatile.Read(ref _closeReason);
        try
        {
            if (reason != ShareEndReason.Lost)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _link.SendAsync(_cipher.Seal(ShareMessage.Bye(reason).ToBytes()), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // Déjà partie.
        }
        finally
        {
            CancelQuietly();
            await _link.DisposeAsync().ConfigureAwait(false);
            _cipher.Dispose();
        }
    }

    private void CancelQuietly()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Déjà fermé.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var sealedData = await _link.ReceiveAsync(ShareMessage.MaxGuestLength + ShareCipher.TagLength, stop).ConfigureAwait(false);
                if (sealedData is null || _cipher.Open(sealedData) is not { } plain || ShareMessage.Parse(plain) is not { } message)
                {
                    Close(ShareEndReason.Lost);
                    return;
                }

                Volatile.Write(ref _lastHeard, DateTime.UtcNow.Ticks);
                if (message.Type == ShareMessageType.Leave)
                {
                    Close(ShareEndReason.Left);
                    return;
                }

                _share.Received(this, message);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or InvalidDataException or ObjectDisposedException)
        {
            Close(ShareEndReason.Lost);
        }
    }

    private async Task PingLoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, stop).ConfigureAwait(false);
                if (DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastHeard), DateTimeKind.Utc) > SilenceLimit)
                {
                    Close(ShareEndReason.Lost);
                    return;
                }

                Enqueue(ShareMessage.Ping());
            }
        }
        catch (OperationCanceledException)
        {
            // Fermée.
        }
    }
}
