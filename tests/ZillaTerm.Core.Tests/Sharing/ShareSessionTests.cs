using System.Collections.Concurrent;
using System.Net;
using ZillaTerm.Core.Sharing;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.Core.Tests.Sharing;

/// <summary>
/// Partage de bout en bout sur ce poste : connexion directe (boucle locale) et dossier relais (dossier temporaire),
/// accord, flux, main donnée et reprise, exclusion, refus, invitation fausse, partage plein, arrêt.
/// </summary>
public sealed class ShareSessionTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _relay = Path.Combine(Path.GetTempPath(), "zillaterm-relay-" + Guid.NewGuid().ToString("N"));
    private readonly List<ShareHost> _hosts = [];

    public ShareSessionTests() => Directory.CreateDirectory(_relay);

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.StopAll(ShareEndReason.ApplicationClosed);
        }

        Thread.Sleep(200);
        try
        {
            Directory.Delete(_relay, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ShareInfo Info(bool control = true, TimeSpan? duration = null) =>
        new(ShareKind.Terminal, "root@srv01", "Jean Dupont", "PC-JDUPONT", control, DateTime.UtcNow + (duration ?? TimeSpan.FromMinutes(30)));

    private static ShareJoin Join(string name = "Paul Martin") => new(name, "pmartin", "PC-PMARTIN", "0.34.0");

    private ShareHost Host(bool direct, bool relay, int maxWatchers = 5)
    {
        var host = new ShareHost(new ShareHostOptions { Direct = direct, RelayFolder = relay ? _relay : null, MaxWatchers = maxWatchers });
        _hosts.Add(host);
        return host;
    }

    /// <summary>Invitation limitée à la boucle locale (le conteneur de test n'a pas forcément d'autre adresse joignable).</summary>
    private static ShareInvitation Local(HostedShare share, int port) =>
        share.Invitation with { Endpoints = [new IPEndPoint(IPAddress.Loopback, port)] };

    /// <summary>Lien qui n'envoie ni ne reçoit rien : la file d'une personne qui regarde, sans réseau.</summary>
    private sealed class IdleLink : IShareLink
    {
        public string Route => "test";

        public bool IsRelay => false;

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void SlowNetworkDropsTheStreamButKeepsStateMessages()
    {
        var share = Host(direct: false, relay: true).Start(Info());
        var resynced = new List<ShareWatcher>();
        share.ResyncNeeded += resynced.Add;
        using var cipher = new ShareCipher(new ShareSessionKeys(new byte[32], new byte[32], "000 000"));
        var watcher = new ShareWatcher(share, new IdleLink(), cipher, Join(), "000 000");
        var half = new string('x', (int)(ShareWatcher.MaxQueuedBytes / 2) + 1024);

        watcher.Enqueue(ShareMessage.TerminalData("avant"));
        watcher.Enqueue(ShareMessage.Control(false));
        watcher.Enqueue(ShareMessage.TerminalData(half));
        watcher.Enqueue(ShareMessage.Notice(ShareNotice.ScreenPaused));
        Assert.Empty(resynced);
        watcher.Enqueue(ShareMessage.TerminalData(half));

        // Le flux en attente est abandonné (l'écran sera renvoyé entier) ; la main reprise et la pause restent, dans l'ordre.
        Assert.Equal([ShareMessageType.Control, ShareMessageType.Notice], watcher.QueuedTypes());
        Assert.Equal([watcher], resynced);
        watcher.Enqueue(ShareMessage.TerminalData("après"));
        Assert.Equal([ShareMessageType.Control, ShareMessageType.Notice, ShareMessageType.TerminalData], watcher.QueuedTypes());
    }

    [Fact]
    public void InvitationKeepsTheCompanyNetworkWhenItIsKnown()
    {
        (IPAddress, string)[] cards =
        [
            (IPAddress.Parse("10.20.30.40"), "corp.local"),
            (IPAddress.Parse("192.168.1.23"), "home"),
            (IPAddress.Parse("172.16.5.6"), "vpn.corp.local"),
            (IPAddress.Parse("169.254.1.1"), "corp.local"),
            (IPAddress.Loopback, ""),
            (IPAddress.Parse("2001:db8::5"), "corp.local"),
            (IPAddress.Parse("fe80::1"), "corp.local"),
        ];

        Assert.Equal(["10.20.30.40", "172.16.5.6", "2001:db8::5"], ShareNetwork.Select(cards, "corp.local").Select(a => a.ToString()));
        // Domaine inconnu, ou aucune carte du domaine : toutes les adresses utilisables.
        Assert.Equal(["10.20.30.40", "192.168.1.23", "172.16.5.6", "2001:db8::5"], ShareNetwork.Select(cards, "").Select(a => a.ToString()));
        Assert.Equal(4, ShareNetwork.Select(cards, "other.example").Count);
        // Une carte du domaine parent (« corp.local ») vaut pour un poste de « paris.corp.local » ; « local » seul, non.
        Assert.Equal(["10.20.30.40", "2001:db8::5"],
            ShareNetwork.Select([.. cards.Where(c => c.Item2 != "vpn.corp.local"), (IPAddress.Parse("10.9.9.9"), "local")], "paris.corp.local")
                .Select(a => a.ToString()));
    }

    [Fact]
    public void DirectConnectionsOnlyOnInvitedAddressesAndFewPendingPerAddress()
    {
        var host = Host(direct: true, relay: false);
        var share = host.Start(Info());
        Assert.True(host.Invited(IPAddress.Loopback));
        Assert.True(host.Invited(IPAddress.IPv6Loopback));
        Assert.False(host.Invited(IPAddress.Parse("203.0.113.9")));
        Assert.All(share.Invitation.Endpoints, e => Assert.True(host.Invited(e.Address)));

        var one = IPAddress.Parse("10.1.1.1");
        for (int i = 0; i < ShareHost.MaxPendingPerAddress; i++)
        {
            Assert.True(host.TryStartHandshake(one));
        }

        Assert.False(host.TryStartHandshake(one));
        Assert.True(host.TryStartHandshake(IPAddress.Parse("10.1.1.2")));
        host.EndHandshake(one);
        Assert.True(host.TryStartHandshake(one));
    }

    [Fact]
    public async Task RelayFileDeliversManySmallMessagesInOrder()
    {
        var a = Path.Combine(_relay, "t.g");
        var b = Path.Combine(_relay, "t.h");
        await using var writer = new FolderShareLink(b, a, deleteOnClose: false);
        await using var reader = new FolderShareLink(a, b, deleteOnClose: false);
        for (int i = 0; i < 2000; i++)
        {
            await writer.SendAsync(BitConverter.GetBytes(i), CancellationToken.None);
        }

        for (int i = 0; i < 2000; i++)
        {
            var message = await Within(reader.ReceiveAsync(16, CancellationToken.None));
            Assert.Equal(i, BitConverter.ToInt32(message!));
        }
    }

    // Suites des attentes hors des fils du partage : un test qui attend ensuite (TryTake) ne bloque pas la réception.
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(Wait);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchControlAndLeave(bool direct)
    {
        var host = Host(direct, relay: !direct);
        var share = host.Start(Info());
        var invitation = direct ? Local(share, host.ListeningPort!.Value) : share.Invitation;
        Assert.Equal(direct, invitation.Endpoints.Count > 0);
        Assert.Equal(!direct, share.UsesRelay);

        var requested = new TaskCompletionSource<ShareWatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        share.JoinRequested += w => requested.TrySetResult(w);
        var inputs = new BlockingCollection<TerminalInput>();
        share.InputReceived += (_, input) => inputs.Add(input);
        var controlAsked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        share.ControlRequested += (_, want) => controlAsked.TrySetResult(want);

        await using var viewer = await Within(ShareViewer.ConnectAsync(invitation, Join(), CancellationToken.None));
        Assert.Equal(direct ? "127.0.0.1" : "relay", viewer.Route);
        var messages = new BlockingCollection<ShareMessage>();
        viewer.MessageReceived += messages.Add;
        var ended = new TaskCompletionSource<ShareEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Ended += r => ended.TrySetResult(r);

        var watcher = await Within(requested.Task);
        Assert.Equal(ShareWatcherState.Pending, watcher.State);
        Assert.Equal(viewer.VerificationCode, watcher.VerificationCode);
        Assert.Equal(new ShareJoin("Paul Martin", "pmartin", "PC-PMARTIN", "0.34.0"), watcher.Join);
        Assert.False(viewer.Accepted.IsCompleted);

        // Rien n'est montré avant l'accord.
        share.Broadcast(ShareMessage.TerminalData("avant l'accord"));
        var emulator = new TerminalEmulator(40, 5);
        emulator.Feed("$ uptime\r\n 10:12:01 up 3 days\r\n$ ");
        share.Accept(watcher, [ShareMessage.TerminalReset(emulator.Columns, emulator.Rows, emulator.Snapshot())]);
        var info = await Within(viewer.Accepted);
        Assert.Equal(("root@srv01", ShareKind.Terminal, "Jean Dupont", true), (info.Label, info.Kind, info.Sharer, info.ControlAllowed));

        Assert.True(messages.TryTake(out var reset, Wait));
        Assert.Equal(ShareMessageType.TerminalReset, reset.Type);
        var (columns, rows, screen) = reset.ReadReset()!.Value;
        var copy = new TerminalEmulator(columns, rows);
        copy.Feed(screen);
        Assert.Equal(" 10:12:01 up 3 days", copy.GetText(1, 0, 1, 39));

        share.Broadcast(ShareMessage.TerminalData("ls\r\n"));
        Assert.True(messages.TryTake(out var data, Wait));
        Assert.Equal("ls\r\n", data.ReadText());

        // Saisie sans la main : ignorée. Demande de la main, main donnée, saisie reçue, main reprise.
        await viewer.SendInputAsync(TerminalInput.Typed("rm -rf /tmp/x"));
        await viewer.RequestControlAsync(true);
        Assert.True(await Within(controlAsked.Task));
        Assert.Empty(inputs);
        share.GiveControl(watcher);
        Assert.True(messages.TryTake(out var granted, Wait));
        Assert.Equal((ShareMessageType.Control, true), (granted.Type, granted.ReadFlag()));
        await viewer.SendInputAsync(TerminalInput.Special(TerminalKey.Up, ctrl: true));
        Assert.True(inputs.TryTake(out var input, Wait));
        Assert.Equal(TerminalInput.Special(TerminalKey.Up, ctrl: true), input);
        Assert.Same(watcher, share.Controller);
        share.TakeControl();
        Assert.True(messages.TryTake(out var revoked, Wait));
        Assert.Equal((ShareMessageType.Control, false), (revoked.Type, revoked.ReadFlag()));
        Assert.Null(share.Controller);

        share.Exclude(watcher);
        Assert.Equal(ShareEndReason.Excluded, await Within(ended.Task));
        Assert.Empty(share.Watchers);
        share.Stop(ShareEndReason.Stopped);
        Assert.Null(host.ListeningPort);
        if (!direct)
        {
            await Task.Delay(1500);
            Assert.False(Directory.Exists(ShareNetwork.ShareFolder(_relay, share.Invitation)));
        }
    }

    /// <summary>
    /// Dossier relais d'une invitation : demandé avant d'être ouvert (il peut désigner n'importe quel serveur). Refusé, rien
    /// n'y est écrit et la demande n'arrive pas ; accepté, la connexion passe par lui.
    /// </summary>
    [Fact]
    public async Task RelayFolderIsOnlyUsedOnceAllowed()
    {
        var host = Host(direct: false, relay: true);
        var share = host.Start(Info());
        var folder = ShareNetwork.ShareFolder(_relay, share.Invitation);
        var asked = new List<string>();

        var refused = await Assert.ThrowsAsync<ShareException>(() => Within(ShareViewer.ConnectAsync(share.Invitation, Join(), CancellationToken.None,
            (relay, _) =>
            {
                asked.Add(relay);
                return Task.FromResult(false);
            })));
        Assert.Equal(ShareFailure.RelayRefused, refused.Failure);
        Assert.Equal([_relay], asked);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Empty(share.Watchers);

        var requested = new TaskCompletionSource<ShareWatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        share.JoinRequested += w => requested.TrySetResult(w);
        await using var viewer = await Within(ShareViewer.ConnectAsync(share.Invitation, Join(), CancellationToken.None,
            (_, _) => Task.FromResult(true)));
        Assert.True(viewer.IsRelay);
        Assert.Equal(viewer.VerificationCode, (await Within(requested.Task)).VerificationCode);
    }

    [Theory]
    [InlineData(@"\\fs01\equipe\relais", @"\\FS01\Equipe\relais\", true)]
    [InlineData(@"\\fs01\equipe\relais", @"//fs01/equipe/relais", true)]
    [InlineData(@"\\fs01\equipe\relais", @"\\fs01\equipe\relais2", false)]
    [InlineData(@"\\fs01\equipe\relais", "", false)]
    [InlineData("", "", false)]
    public void SameRelayFolderIgnoresCaseAndSeparators(string invitation, string settings, bool same) =>
        Assert.Equal(same, ShareInvitation.SameRelayFolder(invitation, settings));

    /// <summary>
    /// L'écran envoyé à l'accord arrive avant que l'onglet ne s'abonne : il lui est remis d'abord, dans l'ordre, avec ce
    /// qui a suivi.
    /// </summary>
    [Fact]
    public async Task MessagesBeforeSubscriptionAreKept()
    {
        var host = Host(direct: true, relay: false);
        var share = host.Start(Info());
        share.JoinRequested += w => share.Accept(w, [ShareMessage.TerminalReset(20, 4, "écran"), ShareMessage.TerminalData("a")]);
        await using var viewer = await Within(ShareViewer.ConnectAsync(Local(share, host.ListeningPort!.Value), Join(), CancellationToken.None));
        await Within(viewer.Accepted);
        share.Broadcast(ShareMessage.TerminalData("b"));
        await Task.Delay(500);

        var messages = new BlockingCollection<ShareMessage>();
        viewer.MessageReceived += messages.Add;
        share.Broadcast(ShareMessage.TerminalData("c"));
        Assert.True(messages.TryTake(out var reset, Wait));
        Assert.Equal("écran", reset.ReadReset()!.Value.Screen);
        foreach (var expected in new[] { "a", "b", "c" })
        {
            Assert.True(messages.TryTake(out var data, Wait));
            Assert.Equal(expected, data.ReadText());
        }
    }

    /// <summary>Refus : la demande échoue ; départ avant l'accord : la demande disparaît chez celui qui partage.</summary>
    [Fact]
    public async Task RefusedOrWithdrawnRequests()
    {
        var host = Host(direct: true, relay: false);
        var share = host.Start(Info());
        var invitation = Local(share, host.ListeningPort!.Value);
        var requests = new BlockingCollection<ShareWatcher>();
        share.JoinRequested += requests.Add;
        var left = new BlockingCollection<ShareEndReason>();
        share.WatcherLeft += (_, reason) => left.Add(reason);

        var refused = await Within(ShareViewer.ConnectAsync(invitation, Join(), CancellationToken.None));
        Assert.True(requests.TryTake(out var first, Wait));
        share.Refuse(first);
        var error = await Assert.ThrowsAsync<ShareException>(() => refused.Accepted.WaitAsync(Wait));
        Assert.Equal(ShareFailure.Refused, error.Failure);
        Assert.True(left.TryTake(out var refusal, Wait));
        Assert.Equal(ShareEndReason.Refused, refusal);

        var withdrawn = await Within(ShareViewer.ConnectAsync(invitation, Join("Léa"), CancellationToken.None));
        Assert.True(requests.TryTake(out _, Wait));
        await withdrawn.DisposeAsync();
        Assert.True(left.TryTake(out var reason, Wait));
        Assert.Equal(ShareEndReason.Left, reason);
        Assert.Empty(share.Watchers);
    }

    /// <summary>
    /// Invitation dont le secret n'est pas le bon : refusée ; au bout de cinq, le partage s'arrête de lui-même. Une
    /// invitation terminée n'essaie même pas.
    /// </summary>
    [Fact]
    public async Task WrongInvitationsStopTheShare()
    {
        var host = Host(direct: true, relay: false);
        var share = host.Start(Info());
        var stopped = new TaskCompletionSource<ShareEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        share.Stopped += r => stopped.TrySetResult(r);
        var forged = Local(share, host.ListeningPort!.Value) with { Secret = new byte[32] };
        for (int i = 0; i < HostedShare.MaxFailedAttempts; i++)
        {
            var error = await Assert.ThrowsAsync<ShareException>(() => ShareViewer.ConnectAsync(forged, Join(), CancellationToken.None));
            Assert.Equal(ShareFailure.Rejected, error.Failure);
        }

        Assert.Equal(ShareEndReason.TooManyAttempts, await Within(stopped.Task));
        Assert.True(share.IsStopped);

        var expired = Local(share, 1) with { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };
        Assert.Equal(ShareFailure.Expired, (await Assert.ThrowsAsync<ShareException>(() => ShareViewer.ConnectAsync(expired, Join(), CancellationToken.None))).Failure);
    }

    /// <summary>
    /// Plusieurs partages en même temps sur le même port, plusieurs personnes par partage, et un poste qui partage et
    /// regarde à la fois ; au-delà du nombre de personnes permis, la demande est refusée (partage plein).
    /// </summary>
    [Fact]
    public async Task SeveralSharesAndWatchersAtOnce()
    {
        var host = Host(direct: true, relay: false, maxWatchers: 2);
        var first = host.Start(Info());
        var second = host.Start(Info(control: false) with { Label = "admin-srv02@srv02", Kind = ShareKind.Screen });
        int port = host.ListeningPort!.Value;
        Assert.Equal(2, host.Shares.Count);
        foreach (var share in new[] { first, second })
        {
            share.JoinRequested += w => share.Accept(w, []);
        }

        var viewers = new List<ShareViewer>();
        foreach (var (share, name) in new[] { (first, "A"), (first, "B"), (second, "C") })
        {
            var viewer = await Within(ShareViewer.ConnectAsync(Local(share, port), Join(name), CancellationToken.None));
            viewers.Add(viewer);
            var info = await Within(viewer.Accepted);
            Assert.Equal(share.Info.Label, info.Label);
        }

        var full = await Within(ShareViewer.ConnectAsync(Local(first, port), Join("D"), CancellationToken.None));
        Assert.Equal(ShareFailure.Full, (await Assert.ThrowsAsync<ShareException>(() => full.Accepted.WaitAsync(Wait))).Failure);

        // Le même poste regarde en même temps le partage d'un autre (un second hôte).
        var other = Host(direct: true, relay: false);
        var theirs = other.Start(Info() with { Label = "oracle@db01" });
        theirs.JoinRequested += w => theirs.Accept(w, []);
        var watching = await Within(ShareViewer.ConnectAsync(Local(theirs, other.ListeningPort!.Value), Join("moi"), CancellationToken.None));
        Assert.Equal("oracle@db01", (await Within(watching.Accepted)).Label);

        var endings = new BlockingCollection<ShareEndReason>();
        foreach (var viewer in viewers)
        {
            viewer.Ended += endings.Add;
        }

        host.StopAll(ShareEndReason.Locked);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(endings.TryTake(out var reason, Wait));
            Assert.Equal(ShareEndReason.Locked, reason);
        }

        Assert.Null(host.ListeningPort);
        await watching.DisposeAsync();
    }
}
