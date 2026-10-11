using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Une connexion Bureau à distance : le contrôle ActiveX et sa fenêtre, sur leur propre thread (<see cref="RdpThread"/>).
/// <see cref="RdpSession"/> (thread de l'interface) en crée une par tentative ; les méthodes publiques lui confient
/// le travail sans l'attendre, et les événements du contrôle lui remontent sur le thread de l'interface.
/// Avant de libérer le contrôle, la connexion retire sa fenêtre de l'onglet : une libération qui tarde ne touche plus
/// l'interface, puis le thread s'arrête.
/// </summary>
internal sealed class RdpConnection : IRdpConnection
{
    private static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private const int DispIdConnecting = 1;
    private const int DispIdConnected = 2;
    private const int DispIdLoginComplete = 3;
    private const int DispIdDisconnected = 4;
    private const int DispIdEnterFullScreen = 5;
    private const int DispIdLeaveFullScreen = 6;
    private const int DispIdFatalError = 10;
    private const int DispIdWarning = 11;
    private const int DispIdRemoteDesktopSizeChange = 12;
    private const int DispIdAuthenticationWarningDisplayed = 18;
    private const int DispIdAuthenticationWarningDismissed = 19;
    private const int DispIdLogonError = 22;
    private const int DispIdServiceMessageReceived = 28;
    private const int DispIdAutoReconnected = 33;
    private const int DispIdAutoReconnecting2 = 34;

    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly RdpSession _session;
    private readonly Dispatcher _ui;
    private readonly RdpThread _thread;
    private readonly string _label;
    private readonly TaskCompletionSource _detachedTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RdpConnectionRequest _request;
    private readonly List<(int DispId, Delegate Handler)> _handlers = [];

    // Thread de la connexion uniquement.
    private Container? _container;
    private RdpClientHost? _client;
    private object? _ocx;
    private System.Windows.Forms.Timer? _resizeTimer;
    private (int Width, int Height) _pixels;
    private (int Width, int Height) _sessionSize;
    private bool _loggedIn;
    private bool _dynamicResize = true;
    private bool _released;

    /// <summary>Démarre le thread de la connexion (thread de l'interface).</summary>
    public RdpConnection(RdpSession session, RdpConnectionRequest request, string label)
    {
        _session = session;
        _ui = Dispatcher.CurrentDispatcher;
        _request = request;
        _label = label;
        _thread = RdpThread.Start($"Bureau à distance {label}");
        DebugLog.Write("rdp", $"{label} : thread de la connexion {_thread.ManagedThreadId}");
    }

    public RdpConnectionSettings Settings => _request.Settings;

    public bool CanFullScreen => true;

    /// <summary>Terminée quand la fenêtre du contrôle a quitté l'onglet (ou n'y est plus : thread arrêté).</summary>
    public Task Detached => _detachedTask.Task;

    /// <summary>
    /// Crée le contrôle dans l'emplacement de l'onglet (<paramref name="slot"/>, <paramref name="pixels"/>), le règle et
    /// lance la connexion. Échoue si le contrôle ne peut pas être créé ou refuse un réglage de sécurité.
    /// </summary>
    public Task StartAsync(IntPtr slot, (int Width, int Height) pixels) => _thread.InvokeAsync(() => Start(slot, pixels));

    /// <summary>Nouvelle taille de l'emplacement de l'onglet, en pixels.</summary>
    public void Resize((int Width, int Height) pixels) => _thread.Post(() =>
    {
        if (_released || _container is not { } container)
        {
            return;
        }

        _pixels = pixels;
        container.Size = new Size(Math.Max(pixels.Width, 1), Math.Max(pixels.Height, 1));
        _resizeTimer?.Stop();
        _resizeTimer?.Start();
    });

    public void Disconnect() => _thread.Post(() =>
    {
        if (!_released && _ocx is { } ocx)
        {
            TryCall(() => Dispatch.Call(ocx, "Disconnect"));
        }
    });

    public void EnterFullScreen() => _thread.Post(() =>
    {
        if (!_released && _loggedIn && _ocx is { } ocx)
        {
            TryCall(() => Dispatch.Set(ocx, "FullScreen", true));
        }
    });

    public void Focus() => _thread.Post(FocusControl);

    /// <summary>
    /// Libère la connexion : retire sa fenêtre de l'onglet (<see cref="Detached"/>), la déconnecte si
    /// <paramref name="disconnect"/>, libère le contrôle et arrête le thread.
    /// </summary>
    public void Release(bool disconnect)
    {
        // Thread arrêté : ses fenêtres ont disparu avec lui.
        if (!_thread.Post(() => ReleaseCore(disconnect)))
        {
            _detachedTask.TrySetResult();
        }
    }

    /// <summary>Appelle <paramref name="pong"/> (thread de l'interface) dès que le thread de la connexion répond.</summary>
    public void Ping(Action pong) => _thread.Post(() => _ui.BeginInvoke(pong));

    /// <summary>Exécute <paramref name="func"/> avec le contrôle, sur le thread de la connexion (tests, diagnostic).</summary>
    public Task<T> InvokeOnControlAsync<T>(Func<object, T> func) =>
        _thread.InvokeAsync(() => func(_ocx ?? throw new InvalidOperationException("Contrôle Bureau à distance libéré")));

    /// <summary>Taille du bureau distant : entre 200 et 8192 pixels, largeur paire.</summary>
    internal static (int Width, int Height) DesktopSize((int Width, int Height) pixels) =>
        (Math.Clamp(pixels.Width, 200, 8192) & ~1, Math.Clamp(pixels.Height, 200, 8192));

    /// <summary>Échelle d'affichage du bureau distant d'après les DPI de l'écran (100 % à 96 DPI).</summary>
    internal static (uint Desktop, uint Device) ScaleFactors(int dpi)
    {
        uint[] allowed = [100, 125, 150, 175, 200, 250, 300, 400, 500];
        var wanted = dpi * 100.0 / 96;
        var desktop = allowed.MinBy(a => Math.Abs(a - wanted));
        uint device = desktop >= 175 ? 180u : desktop >= 125 ? 140u : 100u;
        return (desktop, device);
    }

    private void Start(IntPtr slot, (int Width, int Height) pixels)
    {
        _pixels = pixels;
        _resizeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            ResizeSession();
        };

        // Fenêtre du contrôle dans l'emplacement de l'onglet (fenêtre du thread de l'interface).
        var container = new Container(slot)
        {
            BackColor = Color.Black,
            Bounds = new Rectangle(0, 0, Math.Max(pixels.Width, 1), Math.Max(pixels.Height, 1)),
        };
        _container = container;
        var client = RdpClientHost.Create();
        client.Dock = DockStyle.Fill;
        container.Controls.Add(client);
        _client = client;
        container.CreateControl();
        client.CreateControl();
        var ocx = client.Ocx ?? throw new COMException("MsRdpClient", unchecked((int)0x80004005));
        _ocx = ocx;
        Subscribe(ocx, DispIdLoginComplete, new Action(OnLoginComplete));
        Subscribe(ocx, DispIdDisconnected, new Action<int>(OnDisconnected));
        Subscribe(ocx, DispIdFatalError, new Action<int>(OnFatalError));

        Subscribe(ocx, DispIdEnterFullScreen, new Action(() =>
        {
            DebugLog.Write("rdp", $"{_label} : plein écran (OnEnterFullScreenMode)");
            ToSession(s => s.OnFullScreenChanged(this, true));
        }));
        Subscribe(ocx, DispIdLeaveFullScreen, new Action(() =>
        {
            DebugLog.Write("rdp", $"{_label} : retour dans l'onglet (OnLeaveFullScreenMode)");
            ToSession(s => s.OnFullScreenChanged(this, false));
            FocusControl();
        }));
        // Avertissement de Windows sur l'identité du serveur (certificat) : l'onglet le signale.
        Subscribe(ocx, DispIdAuthenticationWarningDisplayed, new Action(() =>
        {
            DebugLog.Write("rdp", $"{_label} : avertissement d'authentification du serveur affiché par Windows");
            ToSession(s => s.OnAuthenticationWarning(this, true));
        }));
        Subscribe(ocx, DispIdAuthenticationWarningDismissed, new Action(() =>
        {
            DebugLog.Write("rdp", $"{_label} : avertissement d'authentification du serveur fermé");
            ToSession(s => s.OnAuthenticationWarning(this, false));
        }));
        if (DebugLog.Enabled)
        {
            SubscribeDiagnostics(ocx);
        }

        Configure(ocx);
        // Le mot de passe (connexion directe) a été confié au contrôle : la connexion ne le garde pas.
        _request = _request with { Password = null };
        if (DebugLog.Enabled)
        {
            TryCall(() => DebugLog.Write("rdp", $"{_label} : connexion (version du contrôle {Dispatch.Get(ocx, "Version")})"));
        }

        Dispatch.Call(ocx, "Connect");
    }

    /// <summary>Applique les réglages ; ceux qui touchent à la sécurité doivent passer, les autres sont facultatifs.</summary>
    private void Configure(object ocx)
    {
        var s = Settings;
        _sessionSize = DesktopSize(_pixels);
        _dynamicResize = !s.SmartSizing;
        if (DebugLog.Enabled)
        {
            DebugLog.Write("rdp", Describe());
        }

        Dispatch.Set(ocx, "Server", s.Server);
        if (s.UserName.Length > 0)
        {
            Dispatch.Set(ocx, "UserName", s.UserName);
        }

        if (s.Domain.Length > 0)
        {
            Dispatch.Set(ocx, "Domain", s.Domain);
        }

        Dispatch.Set(ocx, "DesktopWidth", _sessionSize.Width);
        Dispatch.Set(ocx, "DesktopHeight", _sessionSize.Height);
        // Toujours dans l'onglet au départ, quel que soit le mode d'affichage demandé ailleurs (fichier du PVWA).
        Optional(() => Dispatch.Set(ocx, "FullScreen", false));
        Optional(() => Dispatch.Set(ocx, "ColorDepth", s.ColorDepth));
        Optional(() => Dispatch.Set(ocx, "FullScreenTitle", _label));

        var advanced = Dispatch.First(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6")
                       ?? throw new COMException("AdvancedSettings", unchecked((int)0x80004002));
        Dispatch.Set(advanced, "RDPPort", s.Port);
        Dispatch.Set(advanced, "EnableCredSspSupport", s.EnableCredSsp);
        Dispatch.Set(advanced, "AuthenticationLevel", (uint)s.AuthenticationLevel);
        Dispatch.Set(advanced, "RedirectDrives", s.RedirectDrives);
        Dispatch.Set(advanced, "RedirectPrinters", s.RedirectPrinters);
        Dispatch.Set(advanced, "RedirectPorts", s.RedirectPorts);
        Dispatch.Set(advanced, "RedirectSmartCards", s.RedirectSmartCards);
        Dispatch.Set(advanced, "RedirectClipboard", s.RedirectClipboard);
        Optional(() => Dispatch.Set(advanced, "RedirectPOSDevices", s.RedirectPosDevices));
        Optional(() => Dispatch.Set(advanced, "RedirectDevices", false));
        Optional(() => Dispatch.Set(advanced, "NegotiateSecurityLayer", s.NegotiateSecurityLayer));
        Optional(() => Dispatch.Set(advanced, "SmartSizing", s.SmartSizing));
        Optional(() => Dispatch.Set(advanced, "EnableAutoReconnect", s.AutoReconnect));
        Optional(() => Dispatch.Set(advanced, "Compress", s.Compress ? 1 : 0));
        if (s.BitmapPersistence)
        {
            Optional(() => Dispatch.Set(advanced, "BitmapPersistence", 1));
        }
        else
        {
            // Pas de cache d'images sur disque (connexion directe) : réglage de sécurité, il doit passer.
            Dispatch.Set(advanced, "BitmapPersistence", 0);
            Optional(() => Dispatch.Set(advanced, "CachePersistenceActive", 0));
        }

        Optional(() => Dispatch.Set(advanced, "PerformanceFlags", s.PerformanceFlags));
        Optional(() => Dispatch.Set(advanced, "AudioRedirectionMode", (uint)s.AudioMode));
        Optional(() => Dispatch.Set(advanced, "AudioCaptureRedirectionMode", s.AudioCapture));
        Optional(() => Dispatch.Set(advanced, "ConnectToAdministerServer", s.ConnectToAdministerServer));
        Optional(() => Dispatch.Set(advanced, "GrabFocusOnConnect", true));
        Optional(() => Dispatch.Set(advanced, "DisplayConnectionBar", true));
        Optional(() => Dispatch.Set(advanced, "ConnectionBarShowMinimizeButton", false));
        Optional(() => Dispatch.Set(advanced, "BandwidthDetection", true));
        if (s.LoadBalanceInfo.Length > 0)
        {
            Dispatch.Set(advanced, "LoadBalanceInfo", s.LoadBalanceInfo);
        }

        if (_request.Password is { Length: > 0 } password)
        {
            // Transmis au contrôle seulement : jamais écrit sur disque ni dans le gestionnaire d'identification.
            Dispatch.Set(advanced, "ClearTextPassword", password);
        }

        var startProgram = s.StartProgram;
        var workDir = s.WorkDir;
        if (startProgram.Length > 0 || workDir.Length > 0 || s.KeyboardHookMode != 2)
        {
            var secured = Dispatch.First(ocx, "SecuredSettings3", "SecuredSettings2")
                          ?? throw new COMException("SecuredSettings", unchecked((int)0x80004002));
            if (startProgram.Length > 0)
            {
                Dispatch.Set(secured, "StartProgram", startProgram);
            }

            if (workDir.Length > 0)
            {
                Dispatch.Set(secured, "WorkDir", workDir);
            }

            Optional(() => Dispatch.Set(secured, "KeyboardHookMode", s.KeyboardHookMode));
        }

        if (s.GatewayHostname.Length > 0 && s.GatewayUsageMethod != 0)
        {
            var transport = Dispatch.First(ocx, "TransportSettings4", "TransportSettings3", "TransportSettings2", "TransportSettings")
                            ?? throw new COMException("TransportSettings", unchecked((int)0x80004002));
            Dispatch.Set(transport, "GatewayHostname", s.GatewayHostname);
            Dispatch.Set(transport, "GatewayUsageMethod", (uint)s.GatewayUsageMethod);
            Optional(() => Dispatch.Set(transport, "GatewayProfileUsageMethod", (uint)s.GatewayProfileUsageMethod));
            Optional(() => Dispatch.Set(transport, "GatewayCredsSource", (uint)s.GatewayCredsSource));
        }

        var (desktopScale, deviceScale) = ScaleFactors(Dpi());
        if (desktopScale > 100 && ocx is IMsRdpExtendedSettings extended)
        {
            Optional(() =>
            {
                object desktop = desktopScale;
                object device = deviceScale;
                extended.SetProperty("DesktopScaleFactor", ref desktop);
                extended.SetProperty("DeviceScaleFactor", ref device);
            });
        }
    }

    private void OnLoginComplete()
    {
        DebugLog.Write("rdp", $"{_label} : ouverture de session Windows terminée (OnLoginComplete)");
        _loggedIn = true;
        ToSession(s => s.OnLoggedIn(this));
        // La taille de l'onglet a pu changer pendant l'ouverture de session.
        _resizeTimer?.Start();
    }

    private void OnDisconnected(int reason)
    {
        if (_released)
        {
            return;
        }

        string? description = null;
        uint extended = 0;
        if (_ocx is { } ocx)
        {
            TryCall(() => extended = Convert.ToUInt32(Dispatch.Get(ocx, "ExtendedDisconnectReason") ?? 0, CultureInfo.InvariantCulture));
            TryCall(() => description = Dispatch.Call(ocx, "GetErrorDescription", (uint)reason, extended) as string);
        }

        DebugLog.Write("rdp", $"{_label} : déconnexion (OnDisconnected), raison {reason}, raison étendue {extended}, « {description?.Trim()} »");
        _loggedIn = false;
        ToSession(s => s.OnDisconnected(this, reason, extended, description));
        // Libération après l'événement en cours : on ne détruit pas le contrôle pendant qu'il nous appelle.
        _thread.Post(() => ReleaseCore(disconnect: false));
    }

    private void OnFatalError(int code)
    {
        DebugLog.Write("rdp", $"{_label} : erreur fatale du contrôle (OnFatalError) {code}");
        ToSession(s => s.OnFatalError(this, code));
        _thread.Post(() => ReleaseCore(disconnect: false));
    }

    private void FocusControl()
    {
        if (!_released && _loggedIn && _client is { IsHandleCreated: true } client)
        {
            client.Focus();
        }
    }

    private void ReleaseCore(bool disconnect)
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _loggedIn = false;
        _resizeTimer?.Stop();
        _resizeTimer?.Dispose();
        if (_container is { IsHandleCreated: true } container)
        {
            // La fenêtre quitte l'onglet avant tout le reste : l'interface ne dépend plus de ce thread.
            ShowWindow(container.Handle, 0);
            SetParent(container.Handle, MessageOnlyParent);
        }

        _detachedTask.TrySetResult();
        DebugLog.Write("rdp", $"{_label} : libération du contrôle{(disconnect ? " (avec déconnexion)" : "")}");
        if (_ocx is { } ocx)
        {
            if (disconnect)
            {
                TryCall(() => Dispatch.Call(ocx, "Disconnect"));
            }

            foreach (var (dispId, handler) in _handlers)
            {
                TryCall(() => ComEventsHelper.Remove(ocx, EventsIid, dispId, handler));
            }
        }

        _handlers.Clear();
        _ocx = null;
        _client?.Dispose();
        _client = null;
        _container?.Dispose();
        _container = null;
        DebugLog.Write("rdp", $"{_label} : contrôle libéré");
        ToSession(s => s.OnReleased(this));
        _thread.Exit();
    }

    /// <summary>Adapte la résolution du bureau distant à la taille de l'onglet (Windows 8.1 / 2012 R2 et plus).</summary>
    private void ResizeSession()
    {
        if (!_loggedIn || !_dynamicResize || _released || _ocx is not { } ocx)
        {
            return;
        }

        var size = DesktopSize(_pixels);
        if (size == _sessionSize || _pixels.Width < 200 || _pixels.Height < 200)
        {
            return;
        }

        var (desktopScale, deviceScale) = ScaleFactors(Dpi());
        try
        {
            Dispatch.Call(ocx, "UpdateSessionDisplaySettings", (uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height,
                0u, desktopScale, deviceScale);
            _sessionSize = size;
        }
        catch (Exception e) when (Dispatch.IsDispatchError(e))
        {
            // Serveur ou contrôle trop ancien : l'image est mise à l'échelle de l'onglet.
            _dynamicResize = false;
            if (Dispatch.First(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6") is { } advanced)
            {
                Optional(() => Dispatch.Set(advanced, "SmartSizing", true));
            }
        }
    }

    /// <summary>DPI de l'écran où se trouve la fenêtre du contrôle (96 par défaut).</summary>
    private int Dpi()
    {
        try
        {
            return _container is { IsHandleCreated: true } container && GetDpiForWindow(container.Handle) is var dpi and > 0 ? (int)dpi : 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    /// <summary>Remonte un événement à la session, sur le thread de l'interface.</summary>
    private void ToSession(Action<RdpSession> action) => _ui.BeginInvoke(() => action(_session));

    private void Subscribe(object ocx, int dispId, Delegate handler)
    {
        ComEventsHelper.Combine(ocx, EventsIid, dispId, handler);
        _handlers.Add((dispId, handler));
    }

    /// <summary>Événements du contrôle utiles seulement au diagnostic, suivis quand le journal de débogage est actif.</summary>
    private void SubscribeDiagnostics(object ocx)
    {
        void Log(string text) => DebugLog.Write("rdp", $"{_label} : {text}");
        Subscribe(ocx, DispIdConnecting, new Action(() => Log("connexion au serveur (OnConnecting)")));
        Subscribe(ocx, DispIdConnected, new Action(() => Log("connecté au serveur (OnConnected)")));
        Subscribe(ocx, DispIdWarning, new Action<int>(code => Log($"avertissement du contrôle (OnWarning) {code}")));
        Subscribe(ocx, DispIdRemoteDesktopSizeChange, new Action<int, int>((width, height) => Log($"taille du bureau distant {width}x{height}")));
        Subscribe(ocx, DispIdLogonError, new Action<int>(code => Log($"erreur d'ouverture de session (OnLogonError) {code}")));
        Subscribe(ocx, DispIdServiceMessageReceived, new Action<string>(message => Log($"message du serveur (OnServiceMessageReceived) « {message} »")));
        Subscribe(ocx, DispIdAutoReconnecting2, new Action<int, bool, int, int>((reason, network, attempt, max) =>
            Log($"reconnexion automatique (raison {reason}, réseau {network}, tentative {attempt}/{max})")));
        Subscribe(ocx, DispIdAutoReconnected, new Action(() => Log("reconnecté automatiquement")));
    }

    /// <summary>Réglages de la connexion pour le journal de débogage ; le mot de passe n'y figure pas, seulement s'il est fourni.</summary>
    private string Describe()
    {
        var s = Settings;
        return string.Create(CultureInfo.InvariantCulture, $"""
            {_label} : réglages de la connexion
            serveur {s.Server}:{s.Port}
            utilisateur « {s.UserName} », domaine « {s.Domain} », mot de passe fourni {!string.IsNullOrEmpty(_request.Password)}
            programme de démarrage « {s.StartProgram} », dossier « {s.WorkDir} »
            NLA (CredSSP) {s.EnableCredSsp}, niveau d'authentification {s.AuthenticationLevel}, couche de sécurité négociée {s.NegotiateSecurityLayer}, session d'administration {s.ConnectToAdministerServer}
            passerelle « {s.GatewayHostname} » (usage {s.GatewayUsageMethod}, identifiants {s.GatewayCredsSource}), répartition de charge fournie {s.LoadBalanceInfo.Length > 0}
            taille {_sessionSize.Width}x{_sessionSize.Height} (emplacement {_pixels.Width}x{_pixels.Height}, {Dpi()} DPI), couleurs {s.ColorDepth} bits, redimensionnement dynamique {_dynamicResize}, mise à l'échelle {s.SmartSizing}
            redirections : presse-papiers {s.RedirectClipboard}, disques {s.RedirectDrives}, imprimantes {s.RedirectPrinters}, ports {s.RedirectPorts}, cartes à puce {s.RedirectSmartCards}, son {s.AudioMode}
            """);
    }

    private static void Optional(Action set, [CallerArgumentExpression(nameof(set))] string what = "") => TryCall(set, what);

    private static void TryCall(Action call, [CallerArgumentExpression(nameof(call))] string what = "")
    {
        try
        {
            call();
        }
        catch (Exception e) when (Dispatch.IsDispatchError(e))
        {
            DebugLog.Write("rdp", $"Refusé par le contrôle : {what} : {e.GetType().Name} 0x{e.HResult:X8} {e.Message}");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Fenêtre (thread de la connexion) qui contient le contrôle, créée directement dans l'emplacement de l'onglet.
    /// Elle ne prévient pas ses fenêtres parentes (WS_EX_NOPARENTNOTIFY) : rien à attendre du thread de l'interface.
    /// </summary>
    private sealed class Container(IntPtr parent) : ContainerControl
    {
        private const int WsChild = 0x40000000;
        private const int WsClipChildren = 0x02000000;
        private const int WsExNoParentNotify = 0x00000004;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (parent != IntPtr.Zero)
                {
                    cp.Parent = parent;
                }

                cp.Style |= WsChild | WsClipChildren;
                cp.ExStyle |= WsExNoParentNotify;
                return cp;
            }
        }
    }
}
