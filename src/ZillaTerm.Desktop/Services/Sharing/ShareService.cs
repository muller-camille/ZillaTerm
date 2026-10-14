using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Win32;
using ZillaTerm.Core;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Services.Sharing;

/// <summary>
/// Une session de ce poste en partage : le partage, ce qu'il montre, la session d'où il vient, et ses évènements
/// ramenés sur le fil de l'interface.
/// </summary>
internal sealed class ActiveShare
{
    internal ActiveShare(HostedShare share, IShareSource source, object session)
    {
        Share = share;
        Source = source;
        Session = session;
    }

    public HostedShare Share { get; }

    public IShareSource Source { get; }

    /// <summary>Session partagée (<c>SshSession</c>, <c>RdpSession</c> ou <c>VncSession</c>).</summary>
    public object Session { get; }

    public string Label => Share.Info.Label;

    /// <summary>Personnes qui ont demandé la main et ne l'ont pas encore (fil de l'interface).</summary>
    public HashSet<ShareWatcher> ControlAsked { get; } = [];

    /// <summary>Personnes en attente de l'accord.</summary>
    public IReadOnlyList<ShareWatcher> Pending => Share.Watchers.Where(w => w.State == ShareWatcherState.Pending).ToList();

    /// <summary>Personnes acceptées, qui regardent.</summary>
    public IReadOnlyList<ShareWatcher> Watching => Share.Watchers.Where(w => w.State == ShareWatcherState.Watching).ToList();

    /// <summary>Une personne demande à regarder (fil de l'interface).</summary>
    public event Action<ShareWatcher>? JoinRequested;

    /// <summary>Une personne qui regarde demande la main (fil de l'interface).</summary>
    public event Action<ShareWatcher>? ControlRequested;

    /// <summary>Une personne est partie, a été refusée ou exclue (fil de l'interface).</summary>
    public event Action<ShareWatcher, ShareEndReason>? WatcherLeft;

    /// <summary>Personnes, demandes ou main changées (fil de l'interface).</summary>
    public event Action? Changed;

    /// <summary>Partage arrêté (fil de l'interface).</summary>
    public event Action<ShareEndReason>? Stopped;

    /// <summary>Demande de main laissée sans suite : elle disparaît des demandes.</summary>
    public void IgnoreControlRequest(ShareWatcher watcher)
    {
        if (ControlAsked.Remove(watcher))
        {
            Changed?.Invoke();
        }
    }

    internal void RaiseJoinRequested(ShareWatcher watcher)
    {
        JoinRequested?.Invoke(watcher);
        Changed?.Invoke();
    }

    internal void RaiseControlRequested(ShareWatcher watcher, bool want)
    {
        bool changed = want ? !watcher.HasControl && ControlAsked.Add(watcher) : ControlAsked.Remove(watcher);
        if (want && changed)
        {
            ControlRequested?.Invoke(watcher);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    internal void RaiseWatcherLeft(ShareWatcher watcher, ShareEndReason reason)
    {
        ControlAsked.Remove(watcher);
        WatcherLeft?.Invoke(watcher, reason);
    }

    internal void RaiseChanged()
    {
        ControlAsked.RemoveWhere(w => w.HasControl || w.State != ShareWatcherState.Watching);
        Changed?.Invoke();
    }

    internal void RaiseStopped(ShareEndReason reason) => Stopped?.Invoke(reason);
}

/// <summary>Une session d'un autre poste regardée ici : la connexion, ce qu'elle montre, et l'onglet qui l'affiche.</summary>
internal sealed class WatchedShare(IShareConnection viewer, ShareInfo info, ShareInvitation invitation)
{
    /// <summary>Connexion à la session (<see cref="ShareViewer"/>).</summary>
    public IShareConnection Viewer { get; } = viewer;

    public ShareInfo Info { get; } = info;

    /// <summary>Invitation utilisée (adresse du partage pour l'infobulle de l'onglet ; jamais réaffichée).</summary>
    public ShareInvitation Invitation { get; } = invitation;

    public string Label => Info.Label;
}

/// <summary>
/// Partages de ce poste et sessions regardées, pour toute l'application (une fenêtre principale peut en remplacer une
/// autre) : les partages s'arrêtent au verrouillage de Windows et à la fermeture de ZillaTerm.
/// </summary>
/// <remarks>
/// <para>Les listes et l'évènement <see cref="Changed"/> sont sur le fil de l'interface.</para>
/// <para>
/// Hors de Windows, le verrouillage est suivi par <see cref="SessionLockWatch"/> (Linux : logind et économiseur d'écran) ;
/// là où il ne peut pas l'être (macOS, pas de D-Bus), <see cref="StopsOnLock"/> est faux et les partages continuent
/// pendant le verrouillage.
/// </para>
/// </remarks>
internal sealed class ShareService
{
    private Dispatcher? _dispatcher;
    private SessionLockWatch? _lockWatch;
    private bool _ended;

    private ShareService()
    {
    }

    public static ShareService Current { get; } = new();

    public ShareHost Host { get; } = new(new ShareHostOptions());

    public ObservableCollection<ActiveShare> Shares { get; } = [];

    public ObservableCollection<WatchedShare> Watched { get; } = [];

    /// <summary>Partage commencé ou arrêté, session regardée ouverte ou fermée, personnes arrivées ou parties.</summary>
    public event Action? Changed;

    /// <summary>
    /// Vrai si le verrouillage de la session arrête les partages sur ce poste : toujours sous Windows, sous Linux une fois
    /// logind suivi (voir <see cref="SessionLockWatch"/>).
    /// </summary>
    public bool StopsOnLock => OperatingSystem.IsWindows() || _lockWatch is { IsWatching: true };

    /// <summary>Réglages des prochains partages ; au premier appel, suit le verrouillage de Windows.</summary>
    public void Configure(AppSettings settings, Dispatcher dispatcher)
    {
        Host.Options = new ShareHostOptions
        {
            Direct = settings.ShareDirect,
            Port = settings.SharePort,
            RelayFolder = settings.ShareRelayFolder.Length > 0 ? settings.ShareRelayFolder : null,
        };
        if (_dispatcher is null)
        {
            _dispatcher = dispatcher;
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            else
            {
                // Linux : logind et économiseur d'écran (appelé depuis un fil de D-Bus, comme SessionSwitch depuis le sien).
                _lockWatch = SessionLockWatch.Start(_ => Host.StopAll(ShareEndReason.Locked));
            }

            dispatcher.ShutdownStarted += (_, _) => Shutdown();
            // Fermeture de l'application Avalonia (le fil de l'interface ne s'arrête pas toujours avant).
            if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
            {
                lifetime.Exit += (_, _) => Shutdown();
            }
        }
    }

    /// <summary>Fermeture de ZillaTerm : plus de suivi du verrouillage, tout est arrêté (une seule fois).</summary>
    private void Shutdown()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }

        _lockWatch?.Dispose();
        EndAll(ShareEndReason.ApplicationClosed);
    }

    /// <summary>Partage une session (fil de l'interface) : le partage commence, la source s'y branche.</summary>
    /// <exception cref="ShareException">Ni connexion directe ni dossier relais possible.</exception>
    /// <exception cref="InvalidOperationException">Trop de partages en même temps.</exception>
    public ActiveShare Start(ShareInfo info, Func<HostedShare, IShareSource> source, object session)
    {
        var share = Host.Start(info);
        IShareSource shown;
        try
        {
            shown = source(share);
        }
        catch
        {
            share.Stop(ShareEndReason.Stopped);
            throw;
        }

        var active = new ActiveShare(share, shown, session);
        share.ResyncNeeded += shown.Resync;
        active.Changed += () => Changed?.Invoke();
        share.JoinRequested += w => Post(() => active.RaiseJoinRequested(w));
        share.ControlRequested += (w, want) => Post(() => active.RaiseControlRequested(w, want));
        share.WatcherLeft += (w, reason) => Post(() => active.RaiseWatcherLeft(w, reason));
        share.WatchersChanged += () => Post(active.RaiseChanged);
        share.Stopped += reason => Post(() =>
        {
            shown.Dispose();
            Shares.Remove(active);
            active.RaiseStopped(reason);
            Changed?.Invoke();
        });
        Shares.Add(active);
        Changed?.Invoke();
        return active;
    }

    /// <summary>Partage en cours d'une session ; null si elle n'est pas partagée.</summary>
    public ActiveShare? ShareOf(object session) => Shares.FirstOrDefault(s => ReferenceEquals(s.Session, session) && !s.Share.IsStopped);

    /// <summary>Session regardée ajoutée (fil de l'interface) ; elle disparaît de la liste à sa fin.</summary>
    public void AddWatched(WatchedShare watched)
    {
        Watched.Add(watched);
        watched.Viewer.Ended += _ => Post(() =>
        {
            Watched.Remove(watched);
            Changed?.Invoke();
        });
        Changed?.Invoke();
    }

    /// <summary>Quitte une session regardée (fermeture de son onglet).</summary>
    public void Leave(WatchedShare watched)
    {
        Watched.Remove(watched);
        _ = watched.Viewer.DisposeAsync();
        Changed?.Invoke();
    }

    /// <summary>Arrête tous les partages de ce poste et quitte toutes les sessions regardées (déconnexion, fermeture).</summary>
    public void EndAll(ShareEndReason reason)
    {
        Host.StopAll(reason);
        foreach (var watched in Watched.ToList())
        {
            Leave(watched);
        }
    }

    private void Post(Action action) => _dispatcher?.Post(action);

    /// <summary>Windows verrouillé : plus rien de ce poste n'est montré.</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)
        {
            Host.StopAll(ShareEndReason.Locked);
        }
    }
}
