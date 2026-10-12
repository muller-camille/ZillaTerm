using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;
using Renci.SshNet;

namespace ZillaTerm.App.Services;

/// <summary>
/// Session SSH intégrée (onglet) via le PSMP ou en direct : shell interactif affiché dans un
/// <see cref="TerminalEmulator"/>, et navigateur de fichiers SFTP associé, ouvert à la demande.
/// Les événements sont toujours déclenchés sur le thread de l'interface.
/// </summary>
public sealed class SshSession : RemoteSession
{
    private readonly SshConnector _connector;
    private readonly bool _followTerminal;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _sendLock = new();
    private readonly object _readLock = new();
    private Task _sending = Task.CompletedTask;
    private SshClient? _client;
    private ShellStream? _shell;
    private int _drainScheduled;
    private DateTime _lastData;
    private bool _userTyped;

    // Attente du marqueur de la commande de suivi du dossier : au plus ce délai, pendant lequel la saisie de
    // l'utilisateur est retenue (null hors de l'attente). Voir SendFolderTracking.
    private static readonly TimeSpan FolderTrackingWait = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _holdTimer;
    private StringBuilder? _heldInput;

    // Numéro de la connexion en cours : la préparation du shell d'une connexion précédente s'arrête à la reconnexion.
    private int _connection;

    /// <param name="account">Compte CyberArk ; null pour une connexion directe (accès d'urgence KeePass).</param>
    public SshSession(PvwaAccount? account, string label, SshConnector connector, Dispatcher dispatcher,
        bool followTerminal, SavedSession? saved)
        : base(account, label, dispatcher, saved)
    {
        _connector = connector;
        _followTerminal = followTerminal;
        Emulator.Response += Send;
        Emulator.EraseMarkerReceived += ReleaseHeldInput;
        _holdTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = FolderTrackingWait };
        _holdTimer.Tick += (_, _) => ReleaseHeldInput();
        Emulator.WorkingDirectoryChanged += dir =>
        {
            TerminalDirectory = dir;
            TerminalDirectoryChanged?.Invoke(dir);
        };
    }

    /// <summary>Écran du terminal mis à jour (données reçues).</summary>
    public event Action? ScreenUpdated;

    /// <summary>Dossier courant du shell (OSC 7), pour l'option « suivre le terminal ».</summary>
    public event Action<string>? TerminalDirectoryChanged;

    public TerminalEmulator Emulator { get; } = new(100, 30);

    /// <summary>
    /// Serveur X de ce poste vers lequel transférer X11 (applications graphiques du serveur), ou null : pas de transfert.
    /// </summary>
    public X11Display? X11 { get; init; }

    public string? TerminalDirectory { get; private set; }

    /// <summary>Le navigateur suit le dossier du terminal (case « Suivre le terminal » du panneau Fichiers).</summary>
    public bool FollowTerminal { get; set; } = true;

    public bool CanFollowTerminal => _followTerminal;

    /// <summary>Connexion SFTP de l'explorateur : une session PSMP de plus pour un compte CyberArk.</summary>
    protected override Task<IRemoteFiles> OpenFilesAsync(CancellationToken ct) => _connector.OpenFileBrowserAsync(ct);

    public async Task ConnectAsync(int columns, int rows)
    {
        SetState(RemoteSessionState.Connecting, null);
        Emulator.Resize(columns, rows);
        // Reconnexion : on libère la connexion précédente avant d'en ouvrir une nouvelle, et on oublie ce qu'elle a
        // reçu ou ce qui y a été tapé (sinon le suivi du dossier ne s'installerait pas, ou trop tôt).
        DisposeInBackground(_shell, _client);
        _shell = null;
        _client = null;
        int connection = ++_connection;
        _userTyped = false;
        DropHeldInput();
        _lastData = default;
        try
        {
            DebugLog.Write("ssh", $"{Label} : connexion");
            var connector = _connector;
            var client = await connector.ConnectShellAsync(Lifetime);
            if (IsDisposed || connection != _connection)
            {
                // Onglet fermé ou nouvelle connexion demandée pendant celle-ci : l'authentification ne s'interrompt pas, la
                // session PSMP aboutit quand même ; elle est refermée aussitôt au lieu de rester ouverte, invisible.
                DebugLog.Write("ssh", $"{Label} : connexion abandonnée, refermée");
                DisposeInBackground(client);
                return;
            }

            _client = client;
            DebugLog.Write("ssh", $"{Label} : connecté ({client.ConnectionInfo.ServerVersion}, {client.ConnectionInfo.CurrentServerEncryption}, bannière {!string.IsNullOrWhiteSpace(connector.Banner)})");
            SshConnector.KeepAlive(client);
            if (connection > 1 && Emulator.CursorColumn > 0)
            {
                // Reconnexion : la nouvelle session commence sur une nouvelle ligne, après l'invite de la précédente.
                Emulator.Feed("\r\n");
            }

            if (!string.IsNullOrWhiteSpace(connector.Banner))
            {
                // Bannière du PSMP (avertissement d'enregistrement) affichée en gris.
                Emulator.Feed("\x1b[90m" + connector.Banner.TrimEnd().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\x1b[0m\r\n");
            }

            var shell = _shell = await CreateShellAsync(client);
            shell.DataReceived += (_, _) => Pump(shell);
            // Ce qui est arrivé avant l'abonnement attend dans le flux.
            _ = Task.Run(() => Pump(shell));
            // Événements d'une connexion remplacée depuis (reconnexion) : sans effet sur l'état de la session.
            shell.Closed += (_, _) =>
            {
                DebugLog.Write("ssh", $"{Label} : session fermée par le serveur");
                Dispatcher.BeginInvoke(() =>
                {
                    if (_shell == shell)
                    {
                        SetState(RemoteSessionState.Closed, Strings.SessionClosedByServer);
                    }
                });
            };
            shell.ErrorOccurred += (_, e) =>
            {
                DebugLog.Write("ssh", $"{Label} : erreur de la session", e.Exception);
                Dispatcher.BeginInvoke(() =>
                {
                    if (_shell == shell)
                    {
                        SetState(RemoteSessionState.Failed, ErrorText.Describe(e.Exception));
                    }
                });
            };
            SetState(RemoteSessionState.Connected, null);
            ScreenUpdated?.Invoke();
            if (_followTerminal || Saved?.StartDirectory is not null)
            {
                _ = PrepareShellAsync(Saved?.StartDirectory, connection);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("ssh", $"{Label} : échec de la connexion", ex);
            if (IsDisposed || connection != _connection)
            {
                // Échec d'une connexion abandonnée : la session fermée ou la connexion suivante ne sont pas concernées.
                return;
            }

            SetState(RemoteSessionState.Failed, ex is HostKeyRefusedException ? ex.Message : ex is OperationCanceledException ? Strings.ConnectionCancelled : ErrorText.Describe(ex));
            throw;
        }
    }

    /// <summary>
    /// Shell interactif, avec le transfert X11 si <see cref="X11"/> est renseigné. Le PSMP ou le serveur peuvent le
    /// refuser : le shell s'ouvre quand même, et une ligne grise dit ce qu'il en est (actif, refusé, pas de serveur X).
    /// </summary>
    private async Task<ShellStream> CreateShellAsync(SshClient client)
    {
        if (X11 is not { } display)
        {
            return client.CreateShellStream("xterm-256color", (uint)Emulator.Columns, (uint)Emulator.Rows, 0, 0, 65536);
        }

        // Avant le shell : sa première sortie ne doit pas passer avant la ligne grise.
        bool listening = await display.IsListeningAsync(TimeSpan.FromSeconds(1), Lifetime);
        var x11 = new X11Forwarding(display.EndPoint, (uint)display.Screen);
        string label = Label;
        x11.RequestReceived += (_, e) =>
        {
            DebugLog.Write("x11", $"{label} : fenêtre X11 ouverte par le serveur ({e.OriginatorHost})");
            InterventionRecorder.Current.Record(Core.Interventions.InterventionKind.Action, label, Strings.InterventionX11Window);
        };
        x11.Exception += (_, e) => DebugLog.Write("x11", $"{label} : connexion X11", e.Exception);
        var shell = client.CreateShellStream("xterm-256color", (uint)Emulator.Columns, (uint)Emulator.Rows, 0, 0, 65536, null, x11);
        DebugLog.Write("x11", $"{Label} : transfert X11 vers {display} {(shell.IsX11ForwardingAccepted ? "accepté" : "refusé")}, serveur X {(listening ? "présent" : "absent")}");
        string message = !shell.IsX11ForwardingAccepted ? Strings.X11Refused
            : listening ? Text.Format(Strings.X11Active, display)
            : Text.Format(Strings.X11NoServer, display, display.EndPoint.Port);
        Emulator.Feed("\x1b[90m" + message + "\x1b[0m\r\n");
        return shell;
    }

    /// <summary>
    /// Lit ce que le flux a reçu. SSH.NET garde chaque paquet dans le tampon du flux jusqu'à sa lecture : sans elle, toute la
    /// sortie de la session (un « tail -f » de plusieurs heures…) resterait en mémoire. Le flux d'une connexion remplacée
    /// est vidé sans affichage.
    /// </summary>
    private void Pump(ShellStream shell)
    {
        lock (_readLock)
        {
            var buffer = new byte[16384];
            while (shell.DataAvailable)
            {
                int read = shell.Read(buffer);
                if (read > 0 && _shell == shell)
                {
                    OnData(buffer.AsSpan(0, read));
                }
            }
        }
    }

    /// <summary>Saisie de l'utilisateur dans le terminal.</summary>
    public void SendInput(string text)
    {
        _userTyped = true;
        if (_heldInput is not null)
        {
            _heldInput.Append(text);
            return;
        }

        Send(text);
    }

    /// <summary>
    /// Installe le suivi du dossier dans le shell courant (case « Suivre » cochée, nouveau shell après « sudo -i »...).
    /// Seulement si le shell attend une commande, pour ne rien écrire au milieu d'une saisie ou d'un éditeur.
    /// </summary>
    public bool InstallFolderTracking()
    {
        if (!_followTerminal || State != RemoteSessionState.Connected || !IsAtPrompt())
        {
            return false;
        }

        SendFolderTracking(null);
        return true;
    }

    /// <summary>
    /// Écrit une commande à l'invite du shell, sans l'exécuter (pas de retour à la ligne) : l'utilisateur la relit et
    /// appuie sur Entrée. Refusé si le shell n'attend pas de commande (programme en cours, saisie commencée).
    /// </summary>
    public bool TypeAtPrompt(string command)
    {
        if (State != RemoteSessionState.Connected || !IsAtPrompt() || command.Any(char.IsControl))
        {
            return false;
        }

        SendInput(command);
        return true;
    }

    public void Send(string text)
    {
        if (_shell is not { } shell || State != RemoteSessionState.Connected)
        {
            return;
        }

        Enqueue(shell, closeOnError: true, s =>
        {
            s.Write(text);
            s.Flush();
        });
    }

    public void Resize(int columns, int rows)
    {
        if (_shell is { } shell)
        {
            Enqueue(shell, closeOnError: false, s => s.ChangeWindowSize((uint)columns, (uint)rows, 0, 0));
        }
    }

    /// <summary>
    /// Envoi au serveur hors du thread de l'interface, dans l'ordre des demandes : une écriture attend le réseau et la
    /// fenêtre SSH du serveur, et ne doit pas figer l'application si le serveur ou le PSMP ne lit plus.
    /// </summary>
    private void Enqueue(ShellStream shell, bool closeOnError, Action<ShellStream> write)
    {
        lock (_sendLock)
        {
            _sending = _sending.ContinueWith(_ =>
            {
                try
                {
                    write(shell);
                }
                catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                    DebugLog.Write("ssh", $"{Label} : échec de l'envoi au serveur", ex);
                    if (closeOnError)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (_shell == shell)
                            {
                                SetState(RemoteSessionState.Closed, ErrorText.Describe(ex));
                            }
                        });
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    protected override void CloseConnections()
    {
        DropHeldInput();
        DisposeInBackground(_shell, _client);
        _shell = null;
        _client = null;
    }

    private void OnData(ReadOnlySpan<byte> data)
    {
        var chars = new char[_decoder.GetCharCount(data, flush: false)];
        _decoder.GetChars(data, chars, flush: false);
        _pending.Enqueue(new string(chars));
        _lastData = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            // Un seul rafraîchissement pour plusieurs paquets reçus en rafale.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, Drain);
        }
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var text))
        {
            sb.Append(text);
        }

        if (sb.Length > 0)
        {
            Emulator.Feed(sb.ToString());
            ScreenUpdated?.Invoke();
        }
    }

    /// <summary>
    /// Installe le suivi du dossier (et le « cd » de départ) dès que le shell du serveur cible affiche son invite.
    /// Avec un vrai PSMP, la connexion à la cible peut prendre plusieurs secondes après la bannière, et ce qui est
    /// envoyé avant que le shell soit prêt est perdu : on attend donc une invite, pas seulement un silence.
    /// Sans suivi du dossier, seul le « cd » (visible) est envoyé.
    /// </summary>
    private async Task PrepareShellAsync(string? startDirectory, int connection)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && State == RemoteSessionState.Connected && connection == _connection)
        {
            await Task.Delay(250);
            if (_userTyped)
            {
                // L'utilisateur a pris la main : on n'écrit pas dans sa ligne (la case « Suivre » permet d'installer le suivi).
                return;
            }

            if (_lastData != default && DateTime.UtcNow - _lastData > TimeSpan.FromMilliseconds(400) && IsAtPrompt())
            {
                if (_followTerminal)
                {
                    SendFolderTracking(startDirectory);
                }
                else
                {
                    Send(WorkingDirectory.ChangeDirectoryCommand(startDirectory!));
                }

                return;
            }
        }
    }

    /// <summary>
    /// Tape la commande de suivi du dossier : la ligne de l'invite est marquée d'abord, et la commande se termine par un
    /// marqueur à la réception duquel l'émulateur efface la commande et son écho à partir de cette ligne. En attendant le
    /// marqueur, ce que tape l'utilisateur est retenu : envoyé plus tôt, son écho pourrait précéder le marqueur et être
    /// effacé avec la commande (avec un shell sans édition de ligne, il resterait invisible mais serait exécuté).
    /// </summary>
    private void SendFolderTracking(string? startDirectory)
    {
        var command = WorkingDirectory.InjectionCommand(startDirectory);
        Emulator.MarkEraseFromCursorLine();
        _heldInput ??= new StringBuilder();
        _holdTimer.Stop();
        _holdTimer.Start();
        Send(command);
    }

    /// <summary>
    /// Fin de l'attente (marqueur reçu, ou délai écoulé sans lui) : la marque est retirée, puis la saisie retenue part.
    /// Tout ce que l'utilisateur tape arrive donc au serveur après l'effacement, et reste affiché.
    /// </summary>
    private void ReleaseHeldInput()
    {
        _holdTimer.Stop();
        Emulator.CancelEraseMark();
        var held = _heldInput;
        _heldInput = null;
        if (held is { Length: > 0 })
        {
            Send(held.ToString());
        }
    }

    /// <summary>Connexion fermée ou remplacée : la saisie retenue n'est envoyée à aucune autre connexion.</summary>
    private void DropHeldInput()
    {
        _holdTimer.Stop();
        Emulator.CancelEraseMark();
        _heldInput = null;
    }

    private bool IsAtPrompt() =>
        !Emulator.IsAlternateScreen && Emulator.CursorColumn > 0
        && WorkingDirectory.LooksLikePrompt(Emulator.GetText(Emulator.CursorRow, 0, Emulator.CursorRow, Emulator.CursorColumn - 1));
}
