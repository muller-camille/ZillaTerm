using System.Windows.Threading;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.Sharing;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Services.Sharing;

/// <summary>Ce qu'un partage montre : l'écran à l'accord d'une personne, puis tout ce qui change.</summary>
internal interface IShareSource : IDisposable
{
    /// <summary>Messages qui montrent l'écran actuel à une personne qui vient d'être acceptée (fil de l'interface).</summary>
    IEnumerable<ShareMessage> Initial();

    /// <summary>Une personne a pris du retard : son écran est renvoyé entier.</summary>
    void Resync(ShareWatcher watcher);
}

/// <summary>
/// Terminal SSH partagé : l'écran à l'accord (instantané, sans l'historique), puis le flux tel que le terminal le reçoit ;
/// un redimensionnement renvoie l'écran entier, comme l'effacement de la commande de suivi du dossier (que ZillaTerm fait
/// de son côté seulement). La saisie de la personne qui a la main est encodée selon les modes du terminal de celui qui
/// partage, comme sa propre frappe.
/// </summary>
internal sealed class TerminalShareSource : IShareSource
{
    private readonly HostedShare _share;
    private readonly SshSession _session;
    private readonly Dispatcher _dispatcher;
    private bool _resetQueued;
    private bool _erased;
    private bool _disposed;

    public TerminalShareSource(HostedShare share, SshSession session, Dispatcher dispatcher)
    {
        _share = share;
        _session = session;
        _dispatcher = dispatcher;
        session.Emulator.Fed += OnFed;
        session.Emulator.EraseMarkerReceived += OnErased;
        session.Emulator.Resized += OnResized;
        session.StateChanged += OnStateChanged;
        share.InputReceived += OnInput;
    }

    public IEnumerable<ShareMessage> Initial() => [Reset()];

    public void Resync(ShareWatcher watcher) => _dispatcher.BeginInvoke(() =>
    {
        if (!_disposed)
        {
            _share.Send(watcher, Reset());
        }
    });

    private ShareMessage Reset() =>
        ShareMessage.TerminalReset(_session.Emulator.Columns, _session.Emulator.Rows, _session.Emulator.Snapshot());

    private void OnFed(string text)
    {
        _share.Broadcast(ShareMessage.TerminalData(text));
        if (_erased)
        {
            // La commande de suivi du dossier vient d'être effacée ici : le terminal de celui qui regarde ne sait pas
            // l'effacer (il n'a pas la marque), il reçoit l'écran entier.
            _erased = false;
            _share.Broadcast(Reset());
        }
    }

    // Pendant Feed, avant Fed et sur le même fil.
    private void OnErased() => _erased = true;

    private void OnResized()
    {
        // Une fois la taille posée (un glisser de la fenêtre en fait plusieurs).
        if (_resetQueued)
        {
            return;
        }

        _resetQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _resetQueued = false;
            if (!_disposed)
            {
                _share.Broadcast(Reset());
            }
        });
    }

    private void OnStateChanged()
    {
        if (_session.State is RemoteSessionState.Closed or RemoteSessionState.Failed)
        {
            _share.Stop(ShareEndReason.SessionClosed);
        }
    }

    private void OnInput(ShareWatcher watcher, TerminalInput input) => _dispatcher.BeginInvoke(() =>
    {
        // Main reprise entre-temps, ou partage arrêté : rien n'est envoyé.
        if (!_disposed && watcher.HasControl && !input.IsEmpty)
        {
            _session.SendInput(input.Encode(_session.Emulator));
        }
    });

    public void Dispose()
    {
        _disposed = true;
        _session.Emulator.Fed -= OnFed;
        _session.Emulator.EraseMarkerReceived -= OnErased;
        _session.Emulator.Resized -= OnResized;
        _session.StateChanged -= OnStateChanged;
        _share.InputReceived -= OnInput;
    }
}

/// <summary>
/// Écran partagé (Bureau à distance dans un onglet, VNC) : capturé quatre fois par seconde, seules les zones modifiées
/// partent (<see cref="ScreenTileEncoder"/> en direct) ; une image complète à chaque arrivée ou retard. Bureau décodé par
/// le client FreeRDP : inchangé, il n'est ni copié ni comparé ; un défilement ou une fenêtre déplacée part en copies
/// d'écran, comme le serveur les a demandées. Écran
/// indisponible (onglet en arrière-plan pour le Bureau à distance, plein écran, déconnexion) : la personne qui regarde
/// le sait, l'image reste figée.
/// </summary>
internal sealed class ScreenShareSource : IShareSource
{
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(250);

    private readonly HostedShare _share;
    private readonly Func<byte[]?, long, ScreenCapture?> _capture;
    private readonly CancellationTokenSource _stop = new();
    private readonly ScreenTileEncoder _encoder = new(stream: 1, live: true);
    private int _keyWanted = 1;
    private bool _paused;
    private byte[]? _buffer;

    // Version du bureau décodé de la dernière image (0 : capture d'une fenêtre ou écran VNC, ou pas encore) : bureau
    // inchangé tant qu'il a cette version ; les copies d'écran reçues depuis s'appliquent à l'image qu'elle a donnée.
    private long _version;

    /// <param name="capture">
    /// Capture de l'écran (sur un fil de travail) comparée à la version de bureau donnée (0 : toujours copié) ; null s'il
    /// n'est pas disponible.
    /// </param>
    public ScreenShareSource(HostedShare share, Func<byte[]?, long, ScreenCapture?> capture)
    {
        _share = share;
        _capture = capture;
        _ = RunAsync(_stop.Token);
    }

    public IEnumerable<ShareMessage> Initial()
    {
        // L'image complète part à la prochaine capture, pour tout le monde (le cache de tuiles repart d'elle).
        Interlocked.Exchange(ref _keyWanted, 1);
        return _paused ? [ShareMessage.Notice(ShareNotice.ScreenPaused)] : [];
    }

    public void Resync(ShareWatcher watcher) => Interlocked.Exchange(ref _keyWanted, 1);

    private async Task RunAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(CaptureInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    Capture();
                }
                catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception
                                              or ArgumentException or InvalidOperationException)
                {
                    DebugLog.Write("partage", "Capture de l'écran partagé impossible", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Partage arrêté.
        }
        finally
        {
            // Plus aucune capture : le flux de l'encodeur peut être libéré.
            _encoder.Dispose();
        }
    }

    private void Capture()
    {
        if (_share.Watchers.All(w => w.State != ShareWatcherState.Watching))
        {
            // Personne ne regarde : rien à capturer ; l'image complète partira à la première arrivée.
            Interlocked.Exchange(ref _keyWanted, 1);
            return;
        }

        // Image complète demandée (arrivée, resynchronisation, reprise) : le bureau est recopié même inchangé.
        bool full = Volatile.Read(ref _keyWanted) == 1 || _paused;
        var capture = _capture(_buffer, full ? 0 : _version);
        if (capture is { Pixels: null })
        {
            // Bureau inchangé depuis l'image précédente : rien à copier, comparer ni envoyer.
            return;
        }

        if (capture is not { Pixels: { } pixels } screen || pixels.Width > ScreenTileEncoder.MaxSide || pixels.Height > ScreenTileEncoder.MaxSide)
        {
            if (!_paused)
            {
                _paused = true;
                _share.Broadcast(ShareMessage.Notice(ShareNotice.ScreenPaused));
            }

            return;
        }

        _buffer = pixels.Bgra;
        bool key = Interlocked.Exchange(ref _keyWanted, 0) == 1;
        if (_paused)
        {
            _paused = false;
            key = true;
            _share.Broadcast(ShareMessage.Notice(ShareNotice.ScreenResumed));
        }

        // Copies d'écran du client FreeRDP depuis l'image précédente, si elle venait aussi de lui (défilement, fenêtre
        // déplacée : quelques octets au lieu des tuiles) ; sinon l'encodeur cherche lui-même un défilement.
        var moves = !key && _version > 0 ? screen.Moves : null;
        _version = 0;
        int stride = pixels.Width * 4;
        var frame = _encoder.Encode(DateTime.UtcNow, pixels.Bgra.AsSpan(0, stride * pixels.Height), pixels.Width, pixels.Height, stride, key, moves);
        _version = screen.Version;
        if (frame is not null)
        {
            _share.Broadcast(ShareMessage.ScreenFrame(frame.Data));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
