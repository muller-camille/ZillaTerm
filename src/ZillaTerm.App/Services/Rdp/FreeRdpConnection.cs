using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Une connexion avec le client FreeRDP de ZillaTerm (<see cref="FreeRdpClient"/>), dans son propre processus : sa
/// fenêtre est une enfant de l'emplacement de l'onglet (<see cref="RdpSlot"/>). La connexion lui est décrite sur son
/// entrée standard (<see cref="FreeRdpArguments"/>) ; il signale sur sa sortie la session ouverte, chaque certificat à
/// vérifier (ZillaTerm répond) et sa fin. Le clavier passe par lui quand l'emplacement a le focus ; un processus bloqué
/// est arrêté à la fermeture de l'onglet. Thread de l'interface uniquement, sauf la lecture des sorties.
/// </summary>
internal sealed class FreeRdpConnection : IRdpConnection
{
    private const int WmNull = 0x0000;
    private const int WmSize = 0x0005;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>Délai laissé au client pour se déconnecter proprement avant d'être arrêté.</summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(3);

    // Déconnexion demandée pendant la fin de la connexion (canaux) : FreeRDP peut tarder à s'arrêter (constaté).
    private static readonly TimeSpan DisconnectDelay = TimeSpan.FromSeconds(10);

    private readonly RdpSession _session;
    private readonly RdpConnectionRequest _request;
    private readonly string _label;
    private readonly Dispatcher _ui;
    private readonly TaskCompletionSource _detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private RdpSlot? _slot;
    private IntPtr _window;
    private FreeRdpEnded? _ended;
    private volatile bool _released;
    private string? _refusal;
    private Func<Task<bool>>? _askThenRetry;
    private bool _disconnecting;

    public FreeRdpConnection(RdpSession session, RdpConnectionRequest request, string label)
    {
        _session = session;
        _request = request;
        _label = label;
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>Terminée quand le processus est arrêté : sa fenêtre a quitté l'onglet avec lui.</summary>
    public Task Detached => _detached.Task;

    public bool CanFullScreen => false;

    public bool HasWindow => _window != IntPtr.Zero;

    public async Task StartAsync(IntPtr slot, (int Width, int Height) pixels)
    {
        var lines = FreeRdpArguments.Build(_request.Settings, slot.ToInt64(), pixels, _request.Password, DebugLog.Enabled,
            legacySecurity: _request.VerifyServerKey is not null);
        var process = FreeRdpClient.Start();
        _process = process;
        DebugLog.Write("freerdp", $"{_label} : client FreeRDP lancé (processus {process.Id}), session {pixels.Width} × {pixels.Height}"
            + (_request.Settings.IsRemoteApp ? $", application distante {_request.Settings.RemoteApplicationProgram}" : ""));
        _slot = _session.Host;
        _slot.FocusOnChildClick = true;
        _slot.FocusChanged += OnSlotFocus;
        _slot.ChildMouseWheel += OnSlotWheel;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                _ui.BeginInvoke(() => OnOutput(process, line));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line && DebugLog.Enabled)
            {
                DebugLog.Write("freerdp", DebugLog.Redact(line));
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = WatchAsync(process);

        try
        {
            var input = process.StandardInput;
            foreach (var line in lines)
            {
                await input.WriteLineAsync(line);
            }

            await input.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Client déjà arrêté, ou onglet fermé entre-temps : sa fin est signalée par WatchAsync.
            DebugLog.Write("freerdp", $"{_label} : réglages non transmis ({e.Message})");
        }
    }

    public void Resize((int Width, int Height) pixels)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            PostMessage(_window, WmSize, IntPtr.Zero, MakeLParam(pixels.Width, pixels.Height));
        }
    }

    /// <summary>Déconnexion demandée à FreeRDP ; arrêté s'il n'a pas fini dans le délai : la session se termine toujours.</summary>
    public void Disconnect()
    {
        _disconnecting = true;
        Send("disconnect");
        if (_process is { } process)
        {
            _ = StopLaterAsync(process, DisconnectDelay);
        }
    }

    public void EnterFullScreen()
    {
    }

    /// <summary>Focus clavier à l'emplacement : FreeRDP en est prévenu (<see cref="OnSlotFocus"/>) et prend le clavier.</summary>
    public void Focus()
    {
        if (!_released && _slot is { SlotHandle: var handle } && handle != IntPtr.Zero)
        {
            SetFocus(handle);
        }
    }

    public void Release(bool disconnect)
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_slot is { } slot)
        {
            slot.FocusChanged -= OnSlotFocus;
            slot.ChildMouseWheel -= OnSlotWheel;
            slot.FocusOnChildClick = false;
        }

        if (_process is not { } process)
        {
            _detached.TrySetResult();
            return;
        }

        if (disconnect)
        {
            Send("disconnect");
        }

        // Fin de l'entrée : le client se déconnecte et s'arrête ; sinon, arrêté au bout du délai.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        _ = StopLaterAsync(process, StopDelay);
    }

    public void Ping(Action pong)
    {
        var window = _window;
        if (window == IntPtr.Zero || _released)
        {
            _ui.BeginInvoke(pong);
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

            _ = _ui.BeginInvoke(pong);
        });
    }

    public Task<T> InvokeOnControlAsync<T>(Func<object, T> func) =>
        Task.FromException<T>(new NotSupportedException("Client FreeRDP : pas de contrôle de Windows"));

    private async void OnOutput(Process process, string line)
    {
        if (process != _process)
        {
            return;
        }

        switch (FreeRdpMessage.Parse(line))
        {
            case FreeRdpConnected connected:
                OnConnected(process, new IntPtr(connected.Window));
                break;
            case FreeRdpCertificateRequest certificate:
                var answer = await AnswerAsync(_request.VerifyCertificate is { } verify ? () => verify(certificate) : null,
                    $"certificat de {certificate.Host}:{certificate.Port}");
                if (!answer.Accept)
                {
                    _refusal = Strings.FreeRdpTls;
                }

                Send(answer.Accept ? "accept" : "deny");
                break;
            case FreeRdpServerKey key:
                var keyAnswer = await AnswerAsync(_request.VerifyServerKey is { } verifyKey ? () => verifyKey(key) : null,
                    $"sécurité RDP sans TLS, clé de {key.Host}:{key.Port} SHA256 {key.Sha256}");
                if (!keyAnswer.Accept)
                {
                    _refusal = Strings.FreeRdpKeyRefused;
                }

                Send(keyAnswer.Accept ? "accept" : "deny");
                break;
            case FreeRdpEnded ended:
                _ended = ended;
                break;
            case FreeRdpReconnecting reconnecting:
                DebugLog.Write("freerdp", $"{_label} : connexion coupée, reconnexion automatique (tentative {reconnecting.Attempt})");
                break;
            default:
                if (DebugLog.Enabled)
                {
                    DebugLog.Write("freerdp", DebugLog.Redact(line));
                }

                break;
        }
    }

    /// <summary>
    /// Réponse à un certificat ou une clé, refus en cas d'erreur. Une question différée est gardée pour la fin de la
    /// connexion (<see cref="OnExited"/>).
    /// </summary>
    private async Task<FreeRdpAnswer> AnswerAsync(Func<Task<FreeRdpAnswer>>? verify, string what)
    {
        var answer = FreeRdpAnswer.Refused;
        if (!_released && verify is not null)
        {
            try
            {
                answer = await verify();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DebugLog.Write("freerdp", $"{_label} : {what} non vérifié", e);
            }
        }

        DebugLog.Write("freerdp", $"{_label} : {what} " + (answer.Accept ? "accepté" : answer.AskThenRetry is null ? "refusé" : "à confirmer après la fin de la connexion"));
        _askThenRetry ??= answer.AskThenRetry;
        return answer;
    }

    /// <summary>Fenêtre de FreeRDP créée dans l'emplacement : vérifiée (enfant de l'emplacement, de ce processus).</summary>
    private void OnConnected(Process process, IntPtr window)
    {
        if (_released || _slot is not { SlotHandle: var slot })
        {
            return;
        }

        GetWindowThreadProcessId(window, out var owner);
        if (GetParent(window) != slot || owner != process.Id)
        {
            DebugLog.Write("freerdp", $"{_label} : fenêtre 0x{window.ToInt64():X} annoncée mais pas dans l'onglet ; ignorée");
            return;
        }

        _window = window;
        DebugLog.Write("freerdp", $"{_label} : session ouverte, fenêtre 0x{window.ToInt64():X}");
        Resize(_slot.PixelSize);
        if (GetFocus() == slot)
        {
            OnSlotFocus(true);
        }

        _session.OnLoggedIn(this);
    }

    /// <summary>
    /// Focus de l'emplacement : FreeRDP prend le clavier (son crochet clavier vérifie que l'emplacement a le focus) ou
    /// le laisse (« lParam = 1 » : message de la fenêtre parente, voir wf_client_thread).
    /// </summary>
    private void OnSlotFocus(bool focused)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            PostMessage(_window, focused ? WmSetFocus : WmKillFocus, IntPtr.Zero, new IntPtr(1));
        }
    }

    /// <summary>Molette reçue par l'emplacement (qui a le focus) : transmise à FreeRDP, position à l'écran comprise.</summary>
    private void OnSlotWheel(int message, IntPtr wParam, IntPtr lParam)
    {
        if (!_released && _window != IntPtr.Zero)
        {
            PostMessage(_window, message, wParam, lParam);
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
        await _ui.InvokeAsync(() => OnExited(process, code));
    }

    private void OnExited(Process process, int exitCode)
    {
        _window = IntPtr.Zero;
        if (process == _process)
        {
            _process = null;
        }

        var ended = _ended;
        DebugLog.Write("freerdp", $"{_label} : client FreeRDP arrêté (code {exitCode}"
            + (ended is null ? ", sans message de fin)" : $", {ended.Name} 0x{ended.Code:X8})"));
        if (!_released)
        {
            if (ended is null)
            {
                // Arrêté après une déconnexion demandée : fin normale, pas un plantage.
                _session.OnEnded(this, _disconnecting, _disconnecting ? null : Text.Format(Strings.FreeRdpCrashed, exitCode));
            }
            else if (_askThenRetry is not null && !_disconnecting)
            {
                _session.OnEnded(this, false, Strings.FreeRdpConfirmPending);
            }
            else
            {
                var (normal, end) = FreeRdpExit.Classify(ended.Code);
                _session.OnEnded(this, normal, normal ? null : Describe(end, ended));
            }

            _session.OnReleased(this);
            // Pas de question après une déconnexion demandée.
            if (_askThenRetry is { } ask && !_disconnecting)
            {
                _ = _ui.InvokeAsync(() => AskThenRetryAsync(ask));
            }
        }

        process.Dispose();
        _detached.TrySetResult();
    }

    /// <summary>
    /// Question posée une fois la connexion fermée (le serveur n'attend pas) ; confiance accordée : nouvelle connexion,
    /// avec une nouvelle demande au PVWA pour une session PSM (nouveau jeton).
    /// </summary>
    private async Task AskThenRetryAsync(Func<Task<bool>> ask)
    {
        bool retry = false;
        try
        {
            retry = await ask();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DebugLog.Write("freerdp", $"{_label} : question après la connexion", e);
        }

        if (retry)
        {
            DebugLog.Write("freerdp", $"{_label} : confiance accordée, nouvelle connexion");
            await _session.ReconnectAsync();
        }
        else
        {
            _session.ShowError(_refusal ?? Strings.FreeRdpKeyRefused);
        }
    }

    private string Describe(FreeRdpEnd end, FreeRdpEnded ended)
    {
        var server = _request.Settings.Server;
        var text = _refusal ?? end switch
        {
            FreeRdpEnd.TakenOver => Strings.FreeRdpTakenOver,
            FreeRdpEnd.Timeout => Strings.FreeRdpTimeout,
            FreeRdpEnd.Denied => Strings.FreeRdpDenied,
            FreeRdpEnd.NameNotFound => Text.Format(Strings.FreeRdpNameNotFound, server),
            FreeRdpEnd.Unreachable => Text.Format(Strings.FreeRdpUnreachable, server),
            FreeRdpEnd.Tls => Strings.FreeRdpTls,
            FreeRdpEnd.Negotiation => Strings.FreeRdpNegotiation,
            FreeRdpEnd.Authentication => Strings.FreeRdpAuthentication,
            FreeRdpEnd.Account => Strings.FreeRdpAccount,
            FreeRdpEnd.AccessDenied => Strings.FreeRdpAccessDenied,
            _ => Strings.FreeRdpOther,
        };
        // Nom et code de FreeRDP joints : ils disent ce que le texte, général, ne dit pas.
        return $"{text} ({(ended.Name.Length > 0 ? ended.Name + ", " : "")}0x{ended.Code:X8})";
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

    /// <summary>Une ligne pour le client (réponse, ordre) ; rien s'il est déjà arrêté.</summary>
    private void Send(string line)
    {
        if (_process is not { } process)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            DebugLog.Write("freerdp", $"{_label} : « {line} » non transmis ({e.Message})");
        }
    }

    private static IntPtr MakeLParam(int low, int high) => new((high << 16) | (low & 0xFFFF));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
