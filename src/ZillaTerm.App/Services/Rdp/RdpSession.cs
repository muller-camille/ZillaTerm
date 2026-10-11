using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Services.Rdp;

internal enum RdpSessionState
{
    Connecting,
    Connected,
    Ended,
    Failed,
}

/// <summary>Ce qu'il faut pour (re)lancer une connexion : réglages et, pour une connexion directe, le mot de passe.</summary>
internal sealed record RdpConnectionRequest(RdpConnectionSettings Settings, string? Password)
{
    /// <summary>Client FreeRDP de ZillaTerm (session PSM, application distante comprise) au lieu du contrôle de Windows.</summary>
    public bool FreeRdp { get; init; }

    /// <summary>
    /// Certificat présenté au client FreeRDP (serveur, redirection, passerelle) : vrai pour l'accepter. Sans vérification,
    /// tout certificat est refusé.
    /// </summary>
    public Func<FreeRdpCertificateRequest, Task<bool>>? VerifyCertificate { get; init; }
}

/// <summary>
/// Session Bureau à distance dans un onglet, vue du thread de l'interface. Chaque connexion (<see cref="RdpConnection"/>)
/// a son propre contrôle sur son propre thread : l'interface ne l'attend jamais, et un contrôle qui bloque ne la fige
/// pas. La demande de connexion (<see cref="RdpConnectionRequest"/>) est refaite à chaque fois (le mot de passe n'est
/// pas gardé).
/// </summary>
internal sealed class RdpSession : IDisposable
{
    // Raisons de déconnexion normales : par ce poste, par l'utilisateur distant, par le serveur.
    private static readonly int[] NormalDisconnects = [1, 2, 3];

    /// <summary>Sans réponse du thread de la connexion au-delà de ce délai, l'onglet le signale.</summary>
    private static readonly TimeSpan NotRespondingAfter = TimeSpan.FromSeconds(5);

    // Sessions fermées dont l'emplacement n'est pas encore libéré (fenêtre d'un contrôle bloqué).
    private static int _unreleased;

    private readonly Func<CancellationToken, Task<RdpConnectionRequest>> _prepare;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _watchdog;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dispatcher _dispatcher;

    // Connexions dont la fenêtre est peut-être encore dans l'onglet (en cours, ou en cours de libération).
    private readonly List<IRdpConnection> _attached = [];
    private IRdpConnection? _connection;
    private bool _starting;
    private bool _pingPending;
    private long _pingSentAt;
    private bool _disposed;

    /// <param name="label">« compte@cible », titre de l'onglet et du plein écran.</param>
    /// <param name="prepare">Demande de connexion (lecture du coffre KeePass...).</param>
    public RdpSession(string label, Func<CancellationToken, Task<RdpConnectionRequest>> prepare)
    {
        Label = label;
        _prepare = prepare;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // Masqué tant qu'aucune session n'est affichée : le message de l'onglet (WPF) reste alors visible.
        Host = new RdpSlot { Visibility = Visibility.Hidden };
        Host.PixelSizeChanged += () => _connection?.Resize(Host.PixelSize);
        _watchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += OnWatchdog;
    }

    public event Action? StateChanged;

    public string Label { get; }

    /// <summary>Emplacement de la session dans l'onglet (fenêtre du thread de l'interface).</summary>
    public RdpSlot Host { get; }

    public RdpSessionState State { get; private set; } = RdpSessionState.Connecting;

    /// <summary>Serveur de la dernière connexion.</summary>
    public string Server { get; private set; } = "";

    /// <summary>Message de la dernière fin de session ou erreur.</summary>
    public string? Error { get; private set; }

    public bool IsFullScreen { get; private set; }

    /// <summary>Session PSM : adresse du PSM (infobulle de l'onglet) ; null pour une connexion directe.</summary>
    public string? Psm { get; init; }

    /// <summary>Vrai tant que Windows affiche son avertissement sur l'identité du serveur (certificat).</summary>
    public bool IsAuthenticationWarningOpen { get; private set; }

    /// <summary>Vrai si Windows a affiché son avertissement sur l'identité du serveur pendant la dernière connexion.</summary>
    public bool AuthenticationWarningShown { get; private set; }

    /// <summary>Code de la dernière déconnexion signalée par le contrôle (événement OnDisconnected).</summary>
    public int? DisconnectReason { get; private set; }

    /// <summary>Code détaillé de la dernière déconnexion (ExtendedDisconnectReason du contrôle).</summary>
    public uint? ExtendedDisconnectReason { get; private set; }

    /// <summary>
    /// Vrai si la dernière tentative a échoué dans le contrôle Bureau à distance lui-même (création ou réglages),
    /// avant toute connexion au serveur.
    /// </summary>
    public bool ControlFailed { get; private set; }

    public bool IsConnected => State == RdpSessionState.Connected;

    /// <summary>Vrai tant que le contrôle Bureau à distance existe (connexion en cours ou établie).</summary>
    public bool HasControl => _connection is not null;

    /// <summary>Vrai si l'onglet affiche le bureau distant (le contrôle existe).</summary>
    public bool ShowsDesktop => HasControl;

    /// <summary>Plein écran possible pour la connexion en cours (contrôle de Windows seulement).</summary>
    public bool CanFullScreen => _connection?.CanFullScreen == true;

    /// <summary>Ouverture de la session (fin de l'ouverture de session Windows), ou null.</summary>
    public DateTime? ConnectedAt { get; private set; }

    /// <summary>
    /// Vrai quand le thread du contrôle Bureau à distance ne répond plus depuis quelques secondes : l'onglet le
    /// signale ; le reste de l'application reste utilisable.
    /// </summary>
    public bool IsNotResponding { get; private set; }

    /// <summary>Terminée quand la session est fermée et que sa fenêtre a quitté l'onglet (voir <see cref="Dispose"/>).</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Vrai tant qu'une session fermée garde la fenêtre de son contrôle (thread bloqué) : l'arrêt normal de
    /// l'application la détruirait en attendant ce thread.
    /// </summary>
    public static bool ControlWindowsLeft => Volatile.Read(ref _unreleased) > 0;

    public async Task ConnectAsync()
    {
        ReleaseConnection(disconnect: false);
        Error = null;
        ConnectedAt = null;
        ControlFailed = false;
        DisconnectReason = null;
        ExtendedDisconnectReason = null;
        IsFullScreen = false;
        IsAuthenticationWarningOpen = false;
        AuthenticationWarningShown = false;
        SetState(RdpSessionState.Connecting);
        RdpConnectionRequest request;
        try
        {
            request = await _prepare(_lifetime.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("rdp", $"{Label} : échec de la préparation de la connexion", ex);
            // Onglet fermé pendant la préparation (appel au PVWA annulé) : plus rien à afficher.
            if (!_disposed)
            {
                Error = ErrorText.Describe(ex);
                SetState(RdpSessionState.Failed);
            }

            return;
        }

        if (_disposed)
        {
            return;
        }

        Server = request.Settings.Server;
        IRdpConnection connection;
        try
        {
            connection = request.FreeRdp ? new FreeRdpConnection(this, request, Label) : new RdpConnection(this, request, Label);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex);
            return;
        }

        _connection = connection;
        _attached.Add(connection);
        _ = connection.Detached.ContinueWith(_ => _dispatcher.BeginInvoke(() => _attached.Remove(connection)), TaskScheduler.Default);
        _starting = true;
        try
        {
            // L'emplacement s'affiche avant la création du contrôle : celui-ci prend sa taille dans l'onglet.
            UpdateHost();
            Host.UpdateLayout();
            await connection.StartAsync(Host.SlotHandle, Host.PixelSize);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed && _connection == connection)
            {
                ReleaseConnection(disconnect: false);
                Fail(ex);
            }

            return;
        }
        finally
        {
            _starting = false;
        }

        if (_disposed || _connection != connection)
        {
            return;
        }

        // Le contrôle existe : l'onglet affiche le bureau.
        _watchdog.Start();
        UpdateHost();
        SetState(RdpSessionState.Connecting);
    }

    /// <summary>Déconnecte la session puis attend sa fin (3 s au plus) avant de libérer le contrôle.</summary>
    public async Task CloseAsync()
    {
        await DisconnectAndWaitAsync();
        Dispose();
    }

    /// <summary>Nouvelle connexion dans l'onglet ; une session ouverte est d'abord déconnectée.</summary>
    public async Task ReconnectAsync()
    {
        await DisconnectAndWaitAsync();
        if (!_disposed)
        {
            await ConnectAsync();
        }
    }

    private async Task DisconnectAndWaitAsync()
    {
        if (!HasControl || !IsConnected)
        {
            return;
        }

        var ended = new TaskCompletionSource();
        void OnState()
        {
            if (!IsConnected)
            {
                ended.TrySetResult();
            }
        }

        StateChanged += OnState;
        Disconnect();
        await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        StateChanged -= OnState;
    }

    /// <summary>Déconnecte la session ; pendant la connexion (serveur ou PSM qui ne répond pas), l'abandonne.</summary>
    public void Disconnect()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        if (IsConnected)
        {
            connection.Disconnect();
        }
        else if (State == RdpSessionState.Connecting && !_disposed)
        {
            // Le contrôle ne signale pas toujours la fin d'une connexion abandonnée : on la termine nous-mêmes.
            ReleaseConnection(disconnect: true);
            Error = null;
            IsFullScreen = false;
            SetState(RdpSessionState.Ended);
        }
    }

    public void EnterFullScreen()
    {
        if (IsConnected)
        {
            _connection?.EnterFullScreen();
        }
    }

    public void Focus()
    {
        if (IsConnected && !IsNotResponding)
        {
            _connection?.Focus();
        }
    }

    /// <summary>
    /// Ferme la session sans attendre : la connexion retire sa fenêtre de l'onglet puis libère le contrôle sur son
    /// thread. <see cref="Closed"/> se termine une fois la fenêtre retirée.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _unreleased);
        _lifetime.Cancel();
        _watchdog.Stop();
        var connection = _connection;
        _connection = null;
        connection?.Release(disconnect: true);
        // L'emplacement disparaît une fois vide : il ne doit pas emporter une fenêtre d'un autre thread.
        _ = Task.WhenAll(_attached.Select(c => c.Detached))
            .ContinueWith(_ => _dispatcher.BeginInvoke(CloseHost), TaskScheduler.Default);
        _lifetime.Dispose();
    }

    /// <summary>Exécute <paramref name="func"/> avec le contrôle de la connexion en cours, sur son thread (tests).</summary>
    internal Task<T> InvokeOnControlAsync<T>(Func<object, T> func) =>
        _connection?.InvokeOnControlAsync(func) ?? Task.FromException<T>(new InvalidOperationException("Aucune connexion en cours"));

    // ---- Événements de la connexion, sur le thread de l'interface ; ceux d'une connexion remplacée sont ignorés.

    internal void OnLoggedIn(IRdpConnection connection)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        ConnectedAt = DateTime.UtcNow;
        SetState(RdpSessionState.Connected);
    }

    internal void OnDisconnected(IRdpConnection connection, int reason, uint extended, string? description)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        DisconnectReason = reason;
        ExtendedDisconnectReason = extended;
        DebugLog.Write("rdp", ConnectedAt is { } at
            ? $"{Label} : fin de session {(DateTime.UtcNow - at).TotalSeconds:0.0} s après l'ouverture de session"
            : $"{Label} : fin de session avant l'ouverture de session");
        bool normal = NormalDisconnects.Contains(reason);
        // Codes de Windows joints au message : ils disent ce que le texte, souvent générique, ne dit pas.
        var codes = Text.Format(Strings.RdpDisconnectCodes, reason, extended);
        Error = normal && reason == 1 ? null
                : string.IsNullOrWhiteSpace(description) ? Text.Format(Strings.RdpDisconnectCode, codes) : $"{description.Trim()} ({codes})";
        IsFullScreen = false;
        SetState(normal ? RdpSessionState.Ended : RdpSessionState.Failed);
    }

    /// <summary>Fin d'une session FreeRDP : <paramref name="error"/> est null pour une fin normale.</summary>
    internal void OnEnded(IRdpConnection connection, bool normal, string? error)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        DebugLog.Write("rdp", ConnectedAt is { } at
            ? $"{Label} : fin de session {(DateTime.UtcNow - at).TotalSeconds:0.0} s après l'ouverture de session"
            : $"{Label} : fin de session avant l'ouverture de session");
        Error = error;
        IsFullScreen = false;
        SetState(normal ? RdpSessionState.Ended : RdpSessionState.Failed);
    }

    internal void OnFatalError(IRdpConnection connection, int code)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        Error = Text.Format(Strings.RdpControlError, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetState(RdpSessionState.Failed);
    }

    internal void OnFullScreenChanged(IRdpConnection connection, bool fullScreen)
    {
        if (connection == _connection)
        {
            IsFullScreen = fullScreen;
        }
    }

    internal void OnAuthenticationWarning(IRdpConnection connection, bool displayed)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        IsAuthenticationWarningOpen = displayed;
        AuthenticationWarningShown |= displayed;
        StateChanged?.Invoke();
    }

    /// <summary>Contrôle libéré par la connexion (fin de session, erreur) : l'onglet n'a plus de contrôle.</summary>
    internal void OnReleased(IRdpConnection connection)
    {
        if (connection != _connection || _disposed)
        {
            return;
        }

        _connection = null;
        _watchdog.Stop();
        _pingPending = false;
        IsNotResponding = false;
        IsAuthenticationWarningOpen = false;
        UpdateHost();
        StateChanged?.Invoke();
    }

    private void Fail(Exception ex)
    {
        DebugLog.Write("rdp", $"{Label} : échec du contrôle Bureau à distance", ex);
        ControlFailed = true;
        Error = ex switch
        {
            COMException com => Text.Format(Strings.RdpControlError, $"0x{com.HResult:X8}"),
            FreeRdpStartException start => Text.Format(Strings.FreeRdpStartFailed, start.Message),
            _ => ErrorText.Describe(ex),
        };
        SetState(RdpSessionState.Failed);
    }

    /// <summary>Libère la connexion en cours sur son thread, sans l'attendre.</summary>
    private void ReleaseConnection(bool disconnect)
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _connection = null;
        _watchdog.Stop();
        _pingPending = false;
        IsNotResponding = false;
        connection.Release(disconnect);
        UpdateHost();
    }

    /// <summary>
    /// Surveillance du thread de la connexion : une sollicitation à la fois ; sans réponse au-delà du délai, la
    /// session est signalée « ne répond pas » jusqu'à la réponse.
    /// </summary>
    private void OnWatchdog(object? sender, EventArgs e)
    {
        if (_connection is not { } connection)
        {
            _watchdog.Stop();
            return;
        }

        var now = Environment.TickCount64;
        if (!_pingPending)
        {
            _pingPending = true;
            _pingSentAt = now;
            connection.Ping(() => OnPong(connection));
        }
        else if (!IsNotResponding && now - _pingSentAt > NotRespondingAfter.TotalMilliseconds)
        {
            IsNotResponding = true;
            DebugLog.Write("rdp", $"{Label} : le thread du contrôle ne répond plus depuis {(now - _pingSentAt) / 1000} s");
            StateChanged?.Invoke();
        }
    }

    private void OnPong(IRdpConnection connection)
    {
        if (connection != _connection)
        {
            return;
        }

        _pingPending = false;
        if (IsNotResponding)
        {
            IsNotResponding = false;
            DebugLog.Write("rdp", $"{Label} : le thread du contrôle répond de nouveau");
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Emplacement affiché quand l'onglet montre le bureau, et pendant la création du contrôle pour qu'il ait sa
    /// taille. Rien n'est masqué quand le thread ne répond plus : masquer sa fenêtre pourrait déplacer le focus
    /// clavier, ce qui attendrait ce thread.
    /// </summary>
    private void UpdateHost()
    {
        if (!IsNotResponding)
        {
            Host.Visibility = _starting || ShowsDesktop ? Visibility.Visible : Visibility.Hidden;
        }
    }

    private void CloseHost()
    {
        Host.Dispose();
        Interlocked.Decrement(ref _unreleased);
        _closed.TrySetResult();
    }

    private void SetState(RdpSessionState state)
    {
        DebugLog.Write("rdp", $"{Label} : état {state}");
        State = state;
        StateChanged?.Invoke();
    }
}
