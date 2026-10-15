using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Une connexion avec le client FreeRDP (<see cref="FreeRdpClient"/>), dans son propre processus : sa fenêtre est une
/// enfant de l'emplacement de l'onglet (<see cref="RdpSlot"/>). Le certificat (TLS) ou la clé (ancienne sécurité RDP,
/// client de ZillaTerm sous Windows seulement) du serveur est lu et vérifié par ZillaTerm avant le lancement, question
/// comprise ; le client reçoit les empreintes acceptées et refuse tout autre serveur, sans rien demander
/// (<see cref="FreeRdpArguments"/>, <see cref="XFreeRdpArguments"/>). Sa fenêtre est repérée à sa création, sa fin par
/// son code de sortie ; il est déconnecté en fermant sa fenêtre. Le clavier passe par lui quand il a le focus ; un
/// processus bloqué est arrêté à la fermeture de l'onglet. Thread de l'interface uniquement, sauf la lecture des sorties.
/// </summary>
/// <remarks>
/// Linux : le client du système (xfreerdp) crée sa fenêtre dans la fenêtre d'accueil de l'emplacement, puis ZillaTerm la
/// place dans l'onglet ; s'il la recrée (reconnexion automatique de certaines versions), la nouvelle y est placée de même.
/// </remarks>
internal sealed class FreeRdpConnection : IRdpConnection
{
    private const int WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;

    // Refus du certificat (TLS) ou de la clé (sécurité RDP) par le client : un autre serveur a pu répondre.
    private const uint TlsConnectFailed = 0x0002_0008;
    private const uint SecurityNegotiationFailed = 0x0002_000C;

    /// <summary>
    /// Nouvelles connexions lancées d'elles-mêmes (autre serveur derrière la même adresse), au plus, depuis la dernière
    /// ouverture de session.
    /// </summary>
    private const int MaxAutomaticRetries = 2;

    /// <summary>Délai laissé au client pour se déconnecter proprement avant d'être arrêté.</summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(3);

    // Déconnexion demandée pendant la fin de la connexion (canaux) : FreeRDP peut tarder à s'arrêter (constaté).
    private static readonly TimeSpan DisconnectDelay = TimeSpan.FromSeconds(10);

    private readonly RdpSession _session;
    private readonly RdpConnectionRequest _request;
    private readonly string _label;
    private readonly TaskCompletionSource _detached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Tentative de la session qui a créé cette connexion : une connexion terminée ne relance la session que si aucune
    // autre tentative n'a commencé depuis.
    private readonly int _attempt;

    // Lecture de l'identité du serveur avant le lancement, arrêtée quand la connexion est libérée (onglet fermé,
    // nouvelle tentative).
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private RdpSlot? _slot;
    private bool _slotEvents;
    private IntPtr _window;
    private bool _opened;
    private volatile bool _released;
    private bool _disconnecting;
    private bool _legacySecurity;
    private IReadOnlyCollection<string> _fingerprints = [];

    public FreeRdpConnection(RdpSession session, RdpConnectionRequest request, string label)
    {
        _session = session;
        _request = request;
        _label = label;
        _attempt = session.Attempt;
    }

    /// <summary>Terminée quand le processus est arrêté : sa fenêtre a quitté l'onglet avec lui.</summary>
    public Task Detached => _detached.Task;

    public bool CanFullScreen => false;

    /// <summary>
    /// Vrai dès que la fenêtre du client est dans l'onglet (connexion ouverte), et jusqu'à sa fin ; une reconnexion qui la
    /// recréerait (Linux) ne la retire pas de l'onglet (l'onglet le dit, voir <see cref="RdpSession.IsReconnecting"/>).
    /// </summary>
    public bool HasWindow => _opened;

    public async Task StartAsync(IntPtr slot, (int Width, int Height) pixels)
    {
        var settings = _request.Settings;
        _slot = _session.Host;
        _slot.FocusOnChildClick = true;
        _slot.FocusChanged += OnSlotFocus;
        _slot.ChildMouseWheel += OnSlotWheel;
        _slot.ChildCreated += OnChildCreated;
        _slot.ChildGone += OnChildGone;
        _slotEvents = true;
        if (DebugLog.Enabled && OperatingSystem.IsWindows())
        {
            // Copier-coller entre le poste et la session : chaque changement du presse-papiers du poste est noté.
            _slot.ClipboardChanged += OnClipboardChanged;
            _slot.ListenToClipboard(true);
        }

        if (slot == IntPtr.Zero || _slot.Error is not null)
        {
            // Pas de fenêtre où le client puisse s'intégrer (Wayland natif…) : rien n'est lancé, rien n'est envoyé.
            Detach();
            _detached.TrySetResult();
            throw new FreeRdpStartException(_slot.Error ?? "/parent-window");
        }

        // Identité du serveur, vérifiée avant que le client ne parte : aucune question pendant sa connexion.
        _session.ShowStep(Text.Format(Strings.FreeRdpChecking, settings.Server));
        var check = await CheckServerAsync(ask: true, _stop.Token, () => !_released);
        if (_released)
        {
            _detached.TrySetResult();
            return;
        }

        switch (check.Trust)
        {
            case RdpTrust.Refused:
                DebugLog.Write("freerdp", $"{_label} : {check.What} refusé ; client non lancé");
                Detach();
                _session.OnEnded(this, false, check.Error ?? (check.IsKey ? Strings.FreeRdpKeyRefused : Strings.FreeRdpTls));
                _session.OnReleased(this);
                _detached.TrySetResult();
                return;
            case RdpTrust.AcceptedAfterQuestion:
                // Le temps de la question, la demande a pu vieillir (jeton PSM) : nouvelle connexion, nouvelle demande.
                DebugLog.Write("freerdp", $"{_label} : {check.What} accepté après la question ; nouvelle connexion");
                Detach();
                _detached.TrySetResult();
                _session.Restart(this);
                return;
        }

        var known = _request.KnownFingerprints?.Invoke(settings.Server, settings.Port) ?? [];
        _fingerprints = known.Append(check.Fingerprint!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Ancienne sécurité RDP permise au client seulement pour un serveur lu sans TLS : un serveur lu avec TLS qui ne la
        // proposerait plus est refusé, puis relu (RecheckAsync), et le retrait de TLS signalé.
        _legacySecurity = check.IsKey;
        FreeRdpLaunch launch;
        IReadOnlyList<string> options;
        if (OperatingSystem.IsWindows())
        {
            launch = FreeRdpArguments.Build(settings, slot.ToInt64(), pixels, _request.Password, DebugLog.Enabled, _fingerprints,
                _legacySecurity, FreeRdpClient.ConfigPath);
            options = launch.Arguments;
        }
        else
        {
            // Tout sur l'entrée standard, la ligne de commande n'a que « /args-from:stdin ».
            var config = FreeRdpClient.ConfigPath;
            launch = XFreeRdpArguments.Build(settings, slot.ToInt64(), pixels, _request.Password, DebugLog.Enabled, _fingerprints, config);
            options = XFreeRdpArguments.Options(settings, slot.ToInt64(), pixels, DebugLog.Enabled, _fingerprints, config);
        }

        if (DebugLog.Enabled)
        {
            // Ni jeton ni mot de passe dans les options : ils sont dans le fichier de connexion (entrée standard).
            DebugLog.Write("freerdp", $"{_label} : redirections : presse-papiers {OnOff(settings.RedirectClipboard)}, "
                + $"disques {OnOff(settings.RedirectDrives)}, imprimantes {OnOff(settings.RedirectPrinters)}, "
                + $"cartes à puce {OnOff(settings.RedirectSmartCards)} ; options : {string.Join(" ", options)}");
        }

        var process = FreeRdpClient.Start(launch.Arguments);
        _process = process;
        DebugLog.Write("freerdp", $"{_label} : client FreeRDP lancé (processus {process.Id}), session {pixels.Width} × {pixels.Height}, "
            + $"{_fingerprints.Count} empreinte(s) acceptée(s)"
            + (settings.IsRemoteApp ? $", application distante {settings.RemoteApplicationProgram}" : ""));
        _session.ShowStep(Text.Format(Strings.RdpConnecting, settings.Server));

        DataReceivedEventHandler log = (_, e) =>
        {
            if (e.Data is not { } line)
            {
                return;
            }

            if (DebugLog.Enabled)
            {
                DebugLog.Write("freerdp", DebugLog.Redact(line));
            }

            // Connexion perdue et reconnexion automatique : l'onglet le dit (voir RdpSession.IsReconnecting).
            var (step, attempt, max) = FreeRdpReconnection.Read(line);
            if (step == FreeRdpReconnectionStep.None && !OperatingSystem.IsWindows()
                && line.Contains(XFreeRdpArguments.ChannelsLoadedLine, StringComparison.Ordinal))
            {
                // Client du système : pas de « Reconnected » ; ses canaux rechargés disent que la session est reprise.
                step = FreeRdpReconnectionStep.Reconnected;
            }

            if (step != FreeRdpReconnectionStep.None)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_opened || step != FreeRdpReconnectionStep.Reconnected)
                    {
                        _session.OnReconnection(this, step, attempt, max);
                    }
                });
            }
        };
        process.OutputDataReceived += log;
        process.ErrorDataReceived += log;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = WatchAsync(process);

        try
        {
            // Fichier de connexion (Linux : toutes les options) sur l'entrée standard, puis fin de l'entrée : rien d'autre
            // ne passera par elle.
            var input = process.StandardInput;
            await input.WriteAsync(launch.ConnectionFile);
            await input.FlushAsync();
            input.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Client déjà arrêté, ou onglet fermé entre-temps : sa fin est signalée par WatchAsync.
            DebugLog.Write("freerdp", $"{_label} : fichier de connexion non transmis ({e.Message})");
        }
    }

    public void Resize((int Width, int Height) pixels)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            _slot?.ResizeClient(_window, pixels);
        }
    }

    /// <summary>Déconnexion demandée à FreeRDP ; arrêté s'il n'a pas fini dans le délai : la session se termine toujours.</summary>
    public void Disconnect()
    {
        _disconnecting = true;
        if (_process is { } process)
        {
            Close(process);
            _ = StopLaterAsync(process, DisconnectDelay);
        }
    }

    public void EnterFullScreen()
    {
    }

    /// <summary>
    /// Focus clavier à l'emplacement (Windows : FreeRDP en est prévenu, <see cref="OnSlotFocus"/>, et prend le clavier) ou à
    /// la fenêtre du client (Linux).
    /// </summary>
    public void Focus()
    {
        if (!_released && _slot is { } slot)
        {
            slot.FocusClient();
        }
    }

    public void Release(bool disconnect)
    {
        if (_released)
        {
            return;
        }

        _released = true;
        // Lecture de l'identité du serveur en cours : abandonnée, sans question.
        _stop.Cancel();
        Detach();
        if (_process is not { } process)
        {
            _detached.TrySetResult();
            return;
        }

        // La session quitte l'onglet : le client se déconnecte (fenêtre fermée) ; sinon, arrêté au bout du délai.
        Close(process);
        _ = StopLaterAsync(process, StopDelay);
    }

    public void Ping(Action pong)
    {
        var window = _window;
        if (window == IntPtr.Zero || _released || !OperatingSystem.IsWindows())
        {
            // Linux : la fenêtre d'un autre processus ne bloque jamais ZillaTerm (X11 n'attend pas le client) ; il n'y a
            // rien à surveiller.
            Dispatcher.UIThread.Post(pong);
            return;
        }

        // Fenêtre d'un autre processus : interrogée hors du thread de l'interface, de nouveau tant qu'elle ne répond pas
        // (pause de FreeRDP, ouverture de session lente) : la réponse arrive dès qu'elle répond, fermée ou libérée.
        _ = Task.Run(async () =>
        {
            while (SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 4000, out _) == IntPtr.Zero
                   && IsWindow(window) && !_released)
            {
                await Task.Delay(500);
            }

            Dispatcher.UIThread.Post(pong);
        });
    }

    public Task<T> InvokeOnControlAsync<T>(Func<object, T> func) =>
        Task.FromException<T>(new NotSupportedException("Client FreeRDP : pas de contrôle de Windows"));

    /// <summary>
    /// La connexion quitte l'emplacement (libérée, ou terminée d'elle-même) : plus d'abonnement à ses événements, plus de
    /// clic qui donne le focus ni d'écoute du presse-papiers, jusqu'à la connexion suivante. Une seule fois.
    /// </summary>
    private void Detach()
    {
        if (!_slotEvents || _slot is not { } slot)
        {
            return;
        }

        _slotEvents = false;
        slot.FocusChanged -= OnSlotFocus;
        slot.ChildMouseWheel -= OnSlotWheel;
        slot.ChildCreated -= OnChildCreated;
        slot.ChildGone -= OnChildGone;
        slot.ClipboardChanged -= OnClipboardChanged;
        slot.ListenToClipboard(false);
        slot.FocusOnChildClick = false;
        // Linux : le clavier revient à ZillaTerm s'il était à la session.
        slot.ReleaseKeyboard();
    }

    /// <summary>
    /// Lit le certificat TLS du serveur, ou sa clé s'il n'accepte que l'ancienne sécurité RDP (PSM, client de ZillaTerm
    /// sous Windows seulement), et le fait vérifier (<see cref="RdpConnectionRequest.VerifyCertificate"/>,
    /// <see cref="RdpConnectionRequest.VerifyServerKey"/>), avec une question au besoin si <paramref name="ask"/>. Rien n'est
    /// envoyé au serveur : ni identifiant, ni jeton.
    /// </summary>
    /// <param name="stop">Arrête la lecture (onglet fermé, connexion libérée).</param>
    /// <param name="wanted">
    /// Vérifié une fois l'identité lue, avant toute question : faux (onglet fermé, autre tentative), rien n'est demandé
    /// ni épinglé.
    /// </param>
    private async Task<ServerCheck> CheckServerAsync(bool ask, CancellationToken stop, Func<bool> wanted)
    {
        var server = _request.Settings.Server;
        int port = _request.Settings.Port;
        void StillWanted()
        {
            if (stop.IsCancellationRequested || !wanted())
            {
                throw new OperationCanceledException(stop);
            }
        }

        try
        {
            try
            {
                var certificate = await RdpCertificateProbe.GetAsync(server, port, stop);
                var what = $"certificat de {server}:{port} SHA256 {certificate.Sha256}";
                if (!ask)
                {
                    return new ServerCheck(RdpTrust.Trusted, certificate.Sha256, what, false);
                }

                // Onglet fermé ou connexion remplacée pendant la lecture : aucune question, rien d'épinglé.
                StillWanted();
                var trust = _request.VerifyCertificate is { } verify ? await verify(certificate, server, port) : RdpTrust.Refused;
                return new ServerCheck(trust, certificate.Sha256, what, false);
            }
            // Client du système (Linux) : il n'applique pas l'empreinte à l'ancienne sécurité RDP ; un serveur sans TLS est
            // refusé (message de l'exception : identité invérifiable).
            catch (RdpCertificateException e) when (e.NoTls && _request.VerifyServerKey is not null && FreeRdpClient.PinsLegacySecurity)
            {
                var key = await RdpSecurityProbe.GetAsync(server, port, stop);
                var what = $"sécurité RDP sans TLS, clé de {server}:{port} SHA256 {key.Sha256}";
                if (!ask)
                {
                    return new ServerCheck(RdpTrust.Trusted, key.Sha256, what, true);
                }

                StillWanted();
                var trust = await _request.VerifyServerKey(key, server, port);
                return new ServerCheck(trust, key.Sha256, what, true);
            }
        }
        catch (RdpCertificateException e)
        {
            DebugLog.Write("freerdp", $"{_label} : identité de {server}:{port} illisible : {e.Message}");
            return new ServerCheck(RdpTrust.Refused, null, "serveur", false, Text.Format(Strings.FreeRdpCheckFailed, server, e.Message));
        }
        catch (SocketException e)
        {
            DebugLog.Write("freerdp", $"{_label} : {server}:{port} injoignable", e);
            return new ServerCheck(RdpTrust.Refused, null, "serveur", false,
                e.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                    ? Text.Format(Strings.FreeRdpNameNotFound, server)
                    : Text.Format(Strings.FreeRdpUnreachable, server));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            DebugLog.Write("freerdp", $"{_label} : identité de {server}:{port} illisible", e);
            return new ServerCheck(RdpTrust.Refused, null, "serveur", false, Text.Format(Strings.FreeRdpCheckFailed, server, e.Message));
        }
    }

    /// <summary>Fenêtre créée dans l'emplacement (Windows : pendant sa création) : traitée juste après, sur ce thread.</summary>
    private void OnChildCreated(IntPtr window)
    {
        if (_process is { } process)
        {
            Dispatcher.UIThread.Post(() => OnConnected(process, window));
        }
        else if (!_released)
        {
            DebugLog.Write("freerdp", $"{_label} : fenêtre 0x{window.ToInt64():X} créée dans l'onglet avant le lancement du client ; ignorée");
        }
    }

    /// <summary>
    /// Fenêtre de FreeRDP créée : vérifiée (dans l'emplacement, de ce processus), placée dans l'onglet (Linux). La première
    /// ouvre la session ; une suivante (Linux, client qui recrée sa fenêtre) dit qu'elle est reprise après une reconnexion.
    /// </summary>
    private void OnConnected(Process process, IntPtr window)
    {
        if (_released || window == _window || process != _process || _slot is not { } slot)
        {
            return;
        }

        if (!slot.IsClientWindow(window, process.Id))
        {
            // Dialogue du client mis hors de l'écran (reconnexion) : ce n'est pas sa session, il y reste.
            if (!slot.IsHiddenClientDialog(window))
            {
                DebugLog.Write("freerdp", $"{_label} : fenêtre 0x{window.ToInt64():X} créée dans l'onglet mais pas par le client ; ignorée");
            }

            return;
        }

        bool again = _opened;
        _window = window;
        _opened = true;
        slot.Adopt(window);
        Resize(slot.PixelSize);
        if (again)
        {
            DebugLog.Write("freerdp", $"{_label} : nouvelle fenêtre du client 0x{window.ToInt64():X} (reconnexion)");
            _session.OnReconnection(this, FreeRdpReconnectionStep.Reconnected, 0, 0);
            return;
        }

        DebugLog.Write("freerdp", $"{_label} : session ouverte, fenêtre 0x{window.ToInt64():X}");
        if (slot.HasFocus)
        {
            OnSlotFocus(true);
        }

        _session.OnLoggedIn(this);
    }

    /// <summary>Fenêtre du client détruite (Linux : fin du client, ou reconnexion qui la recrée).</summary>
    private void OnChildGone(IntPtr window)
    {
        if (window == _window && window != IntPtr.Zero)
        {
            _window = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Focus de l'emplacement (Windows) : FreeRDP prend le clavier (son crochet clavier vérifie que l'emplacement a le
    /// focus) ou le laisse. Linux : focus de sa fenêtre, noté seulement.
    /// </summary>
    private void OnSlotFocus(bool focused)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            _slot?.TellClientFocus(_window, focused);
            if (focused && DebugLog.Enabled)
            {
                DebugLog.Write("freerdp", $"{_label} : clavier à la session"
                    + (OperatingSystem.IsWindows() ? $" ; presse-papiers du poste : {ClipboardState.Describe()}" : ""));
            }
        }
    }

    /// <summary>Presse-papiers du poste modifié (journal de débogage) : par une copie sur le poste, ou par FreeRDP.</summary>
    private void OnClipboardChanged()
    {
        if (!_released && OperatingSystem.IsWindows())
        {
            DebugLog.Write("freerdp", $"{_label} : presse-papiers du poste modifié : {ClipboardState.Describe()}");
        }
    }

    private static string OnOff(bool on) => on ? "oui" : "non";

    /// <summary>Molette reçue par l'emplacement (qui a le focus) : transmise à FreeRDP, position à l'écran comprise (Windows).</summary>
    private void OnSlotWheel(int message, IntPtr wParam, IntPtr lParam)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            _slot?.ForwardWheel(_window, message, wParam, lParam);
        }
    }

    private async Task WatchAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }

        int code = process.HasExited ? process.ExitCode : -1;
        await Dispatcher.UIThread.InvokeAsync(() => OnExited(process, code));
    }

    private void OnExited(Process process, int exitCode)
    {
        // Fin d'elle-même (session terminée, erreur, refus) : la connexion quitte l'emplacement.
        Detach();
        bool connected = _opened;
        _window = IntPtr.Zero;
        _opened = false;
        if (process == _process)
        {
            _process = null;
        }

        // Code de sortie du client du système (Linux : XF_EXIT_*) ramené aux codes de FreeRDP.
        var code = OperatingSystem.IsWindows() ? FreeRdpExit.FromExitCode(exitCode) : XFreeRdpArguments.ExitCode(exitCode);
        DebugLog.Write("freerdp", $"{_label} : client FreeRDP arrêté (code 0x{exitCode:X8}"
            + (code is { } known && FreeRdpExit.Name(known) is { Length: > 0 } name ? $", {name})" : ")"));
        if (!_released)
        {
            bool recheck = false;
            bool renew = false;
            if (_disconnecting)
            {
                // Déconnexion demandée : fin normale, même arrêté au bout du délai.
                _session.OnEnded(this, true, null);
            }
            else if (code is not { } value)
            {
                _session.OnEnded(this, false, Text.Format(Strings.FreeRdpCrashed, exitCode));
            }
            else
            {
                var (normal, end) = FreeRdpExit.Classify(value);
                bool refused = value is TlsConnectFailed or SecurityNegotiationFailed;
                // Certificat ou clé refusé par le client avant l'ouverture : un autre serveur a pu répondre.
                recheck = !connected && refused;
                // Après l'ouverture, seule une reconnexion automatique de FreeRDP (coupure réseau) peut être refusée : un
                // autre serveur répond à cette adresse (répartition de charge), la session est perdue.
                renew = connected && refused;
                _session.OnEnded(this, normal, normal ? null : Describe(end, value));
            }

            _session.OnReleased(this);
            if (recheck)
            {
                _ = Dispatcher.UIThread.InvokeAsync(RecheckAsync);
            }
            else if (renew)
            {
                _ = Dispatcher.UIThread.InvokeAsync(RenewAsync);
            }
        }

        process.Dispose();
        _detached.TrySetResult();
    }

    /// <summary>
    /// Le client a refusé le serveur : derrière la même adresse (répartition de charge), un autre serveur a pu répondre,
    /// avec un certificat ou une clé encore inconnus. Relu : s'il est nouveau, il est vérifié (question au besoin) et,
    /// accepté, une nouvelle connexion part (nouvelle demande au PVWA, nouveau jeton). Sinon, l'erreur reste affichée.
    /// </summary>
    private async Task RecheckAsync()
    {
        if (_session.AutomaticRetries >= MaxAutomaticRetries)
        {
            DebugLog.Write("freerdp", $"{_label} : identité refusée par le client ; pas de nouvel essai automatique");
            return;
        }

        // Onglet fermé (lecture arrêtée), ou session relancée entre-temps (« Reconnecter ») : rien à demander, rien à relancer.
        bool Wanted() => _session.CanRetry(_attempt);
        var seen = await CheckServerAsync(ask: false, _session.Lifetime, Wanted);
        if (seen.Fingerprint is null || _fingerprints.Contains(seen.Fingerprint, StringComparer.OrdinalIgnoreCase) || !Wanted())
        {
            return;
        }

        DebugLog.Write("freerdp", $"{_label} : autre serveur derrière la même adresse ({seen.What}) ; vérification");
        var verify = await CheckServerAsync(ask: true, _session.Lifetime, Wanted);
        if (!Wanted())
        {
            return;
        }

        if (verify.Trust == RdpTrust.Refused)
        {
            _session.ShowError(verify.Error ?? (verify.IsKey ? Strings.FreeRdpKeyRefused : Strings.FreeRdpTls));
            return;
        }

        _session.AutomaticRetries++;
        await _session.ReconnectAsync();
    }

    /// <summary>
    /// Session perdue, reconnexion refusée par le client (autre serveur derrière la même adresse) : le jeton PSM a déjà
    /// servi, nouvelle connexion avec une nouvelle demande au PVWA. Le serveur qui répondra est vérifié avant le
    /// lancement du client, comme à chaque connexion (question s'il est inconnu) : rien n'est accepté sans vérification.
    /// </summary>
    private async Task RenewAsync()
    {
        if (_session.AutomaticRetries >= MaxAutomaticRetries)
        {
            DebugLog.Write("freerdp", $"{_label} : reconnexion refusée par le client ; pas de nouvel essai automatique");
            return;
        }

        if (!_session.CanRetry(_attempt))
        {
            // Onglet fermé, ou session déjà relancée (« Reconnecter ») : une seule nouvelle connexion.
            return;
        }

        DebugLog.Write("freerdp", $"{_label} : reconnexion refusée par le client (autre serveur à la même adresse) ; "
            + "nouvelle demande de connexion");
        _session.AutomaticRetries++;
        await _session.ReconnectAsync();
    }

    private string Describe(FreeRdpEnd end, uint code)
    {
        var server = _request.Settings.Server;
        var text = end switch
        {
            FreeRdpEnd.TakenOver => Strings.FreeRdpTakenOver,
            FreeRdpEnd.Timeout => Strings.FreeRdpTimeout,
            FreeRdpEnd.Denied => Strings.FreeRdpDenied,
            FreeRdpEnd.NameNotFound => Text.Format(Strings.FreeRdpNameNotFound, server),
            FreeRdpEnd.Unreachable => Text.Format(Strings.FreeRdpUnreachable, server),
            FreeRdpEnd.Tls => Strings.FreeRdpTls,
            FreeRdpEnd.Negotiation when _legacySecurity => Strings.FreeRdpKeyRefused,
            FreeRdpEnd.Negotiation => Strings.FreeRdpNegotiation,
            FreeRdpEnd.Authentication => Strings.FreeRdpAuthentication,
            FreeRdpEnd.Account => Strings.FreeRdpAccount,
            FreeRdpEnd.AccessDenied => Strings.FreeRdpAccessDenied,
            _ => Strings.FreeRdpOther,
        };
        // Nom et code de FreeRDP joints : ils disent ce que le texte, général, ne dit pas.
        var name = FreeRdpExit.Name(code);
        return $"{text} ({(name.Length > 0 ? name + ", " : "")}0x{code:X8})";
    }

    /// <summary>Fenêtre ouverte : fermée (le client se déconnecte) ; pas encore de fenêtre : connexion abandonnée.</summary>
    private void Close(Process process)
    {
        if (_slot?.CloseClient(_window) == true)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DebugLog.Write("freerdp", $"{_label} : arrêt du client impossible : {e.Message}");
        }
    }

    private async Task StopLaterAsync(Process process, TimeSpan delay)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(delay);
        }
        catch (TimeoutException)
        {
            DebugLog.Write("freerdp", $"{_label} : le client ne s'est pas arrêté en {delay.TotalSeconds:0} s ; arrêt forcé");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DebugLog.Write("freerdp", $"{_label} : arrêt forcé impossible : {e.Message}");
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Certificat ou clé lu, et la réponse de ZillaTerm.</summary>
    /// <param name="Fingerprint">Empreinte SHA-256 lue ; null si rien n'a pu être lu.</param>
    /// <param name="Error">Ce qui a empêché la lecture, à montrer ; null sinon.</param>
    private sealed record ServerCheck(RdpTrust Trust, string? Fingerprint, string What, bool IsKey, string? Error = null);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
