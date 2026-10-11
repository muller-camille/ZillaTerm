using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core.Rdp;
using Xunit.Abstractions;

namespace ZillaTerm.App.Tests;

[Collection(WpfCollection.Name)]
public class RdpSessionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1001, 700, 1000, 700)]
    [InlineData(50, 50, 200, 200)]
    [InlineData(9000, 9000, 8192, 8192)]
    public void DesktopSizeStaysInRangeWithEvenWidth(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), RdpConnection.DesktopSize((width, height)));
    }

    [Theory]
    [InlineData(96, 100u, 100u)]
    [InlineData(120, 125u, 140u)]
    [InlineData(144, 150u, 140u)]
    [InlineData(192, 200u, 180u)]
    public void ScaleFactorsFollowScreenDpi(int dpi, uint desktop, uint device)
    {
        Assert.Equal((desktop, device), RdpConnection.ScaleFactors(dpi));
    }

    /// <summary>
    /// Contrôle Bureau à distance réel (Windows) : création, réglages d'une connexion directe puis d'un fichier .rdp,
    /// connexion vers un port fermé, et réception de l'événement de déconnexion avec son explication.
    /// </summary>
    [Fact]
    public async Task RealControlConnectsAndReportsTheFailure()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            output.WriteLine("Contrôle Bureau à distance absent : test ignoré.");
            return;
        }

        var direct = new RdpConnectionRequest(RdpConnectionSettings.Direct("127.0.0.1", 1, @"TEST\user"), "not-a-password");
        var psm = new RdpConnectionRequest(new RdpConnectionSettings
        {
            Server = "127.0.0.1",
            Port = 1,
            UserName = "jdoe",
            StartProgram = "psm /u admin /a srv01 /c PSM-RDP",
            AuthenticationLevel = 0,
            EnableCredSsp = false,
            KeyboardHookMode = 1,
            PerformanceFlags = 0x01,
        }, null);

        foreach (var request in new[] { direct, psm })
        {
            var (state, controlFailed, reason, error) = await RunOnStaThread(request);
            output.WriteLine($"{request.Settings.Server}:{request.Settings.Port} → {state}, raison {reason}, {error}");
            Assert.False(controlFailed, error);
            Assert.Equal(RdpSessionState.Failed, state);
            // Code > 3 : échec de connexion signalé par l'événement OnDisconnected (pas une fin de session normale).
            Assert.True(reason > 3, $"raison {reason}");
            Assert.False(string.IsNullOrWhiteSpace(error));
            // Les codes de Windows accompagnent l'explication.
            Assert.Contains(reason!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
        }
    }

    /// <summary>
    /// Vraie session sur ce poste (127.0.0.2) avec le compte de test créé par le workflow « rdp-integration » :
    /// ouverture de session dans l'onglet, puis déconnexion. Ignoré sans RDP_TEST_USER / RDP_TEST_PASSWORD.
    /// </summary>
    [Fact]
    [Trait("Category", "RdpIntegration")]
    public async Task DesktopSessionOpensOnARealServer()
    {
        if (IntegrationAccount() is not { } account)
        {
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("127.0.0.2", 3389, account.User) with { AuthenticationLevel = 0 },
            account.Password);

        await RunOnStaAsync(request, async session =>
        {
            await session.ConnectAsync();
            var state = await WaitForAsync(session, s => s != RdpSessionState.Connecting, TimeSpan.FromSeconds(90));
            output.WriteLine($"Bureau : {state}, raison {session.DisconnectReason}, {session.Error}");
            Assert.Equal(RdpSessionState.Connected, state);
            Assert.True(session.ShowsDesktop);

            session.Disconnect();
            Assert.Equal(RdpSessionState.Ended, await WaitForAsync(session, s => s is RdpSessionState.Ended or RdpSessionState.Failed, TimeSpan.FromSeconds(30)));
        });
    }

    /// <summary>
    /// Vrai serveur Bureau à distance de ce poste : son certificat TLS se lit avant toute connexion (empreinte à épingler),
    /// sans identifiant ni mot de passe.
    /// </summary>
    [Fact]
    [Trait("Category", "RdpIntegration")]
    public async Task CertificateOfARealServerIsReadBeforeConnecting()
    {
        if (IntegrationAccount() is null)
        {
            return;
        }

        var certificate = await RdpCertificateProbe.GetAsync("127.0.0.2", 3389, CancellationToken.None);
        output.WriteLine($"{certificate.Subject} SHA256 {certificate.Sha256}, problème « {certificate.Problem} »");
        Assert.Equal(64, certificate.Sha256.Length);
    }

    /// <summary>Connexion directe : le cache d'images persistant du contrôle est coupé (rien de la session sur le disque).</summary>
    [Fact]
    public async Task DirectConnectionKeepsNoBitmapCacheOnDisk()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("10.255.255.1", 3389, @"TEST\user"), null);
        await RunOnStaAsync(request, async session =>
        {
            await session.ConnectAsync();
            Assert.True(session.HasControl, session.Error);
            var persistence = await session.InvokeOnControlAsync(ocx =>
                Convert.ToInt32(Dispatch.Get(Dispatch.First(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6")!,
                    "BitmapPersistence"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(0, persistence);
        });
    }

    /// <summary>
    /// Quitter ou se déconnecter avec un contrôle bloqué : une fois l'emplacement de la session retiré de la fenêtre
    /// (comme le fait la fenêtre principale après 3 s d'attente), la fenêtre se ferme sans attendre le thread du contrôle.
    /// </summary>
    [Fact]
    public async Task WindowClosesWithoutWaitingForABlockedControlOnceItsSessionIsRemoved()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("10.255.255.1", 3389, @"TEST\user"), null);
        var result = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var session = new RdpSession("test", _ => Task.FromResult(request));
            var layer = new System.Windows.Controls.Grid();
            layer.Children.Add(session.Host);
            var window = new Window { Width = 900, Height = 650, ShowInTaskbar = false, ShowActivated = false, Content = layer };
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () =>
            {
                try
                {
                    await session.ConnectAsync();
                    Assert.True(session.HasControl, session.Error);
                    _ = session.InvokeOnControlAsync(_ =>
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(10));
                        return true;
                    });
                    await Task.Delay(300);
                    session.Dispose();
                    Assert.True(RdpSession.ControlWindowsLeft);
                    await Task.WhenAny(session.Closed, Task.Delay(TimeSpan.FromSeconds(1)));
                    Assert.False(session.Closed.IsCompleted);

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    layer.Children.Remove(session.Host);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    window.Close();
                    result.TrySetResult(watch.ElapsedMilliseconds);
                }
                catch (Exception e)
                {
                    result.TrySetException(e);
                }
                finally
                {
                    window.Dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        long closeMs = await result.Task.WaitAsync(TimeSpan.FromMinutes(2));
        output.WriteLine($"Fenêtre fermée en {closeMs} ms, contrôle encore bloqué");
        Assert.True(closeMs < 2000, $"{closeMs} ms : la fermeture a attendu le thread du contrôle");
    }

    /// <summary>
    /// Session PSM dans l'onglet (fichier du PVWA qui demande le plein écran) : le contrôle reste dans l'onglet, l'image
    /// est mise à l'échelle au lieu de changer la résolution, rien de l'écran n'est gardé sur le disque, et le programme
    /// de démarrage du PSM est bien transmis.
    /// </summary>
    [Fact]
    public async Task PsmTabStartsInTheTabWithAFixedResolution()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var settings = RdpConnectionSettings.FromRdpFile(Encoding.Unicode.GetBytes(
            "screen mode id:i:2\r\nuse multimon:i:1\r\nfull address:s:10.255.255.1\r\nusername:s:jdoe\r\n" +
            "alternate shell:s:psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP\r\nauthentication level:i:0\r\n" +
            "enablecredsspsupport:i:0\r\nbitmapcachepersistenable:i:1\r\n")).ForPsmTab();
        await RunOnStaAsync(new RdpConnectionRequest(settings, null), async session =>
        {
            await session.ConnectAsync();
            Assert.True(session.HasControl, session.Error);
            var (fullScreen, smartSizing, persistence, startProgram, width, height) = await session.InvokeOnControlAsync(ocx =>
            {
                var advanced = Dispatch.First(ocx, "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6")!;
                var secured = Dispatch.First(ocx, "SecuredSettings3", "SecuredSettings2")!;
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                return (Convert.ToBoolean(Dispatch.Get(ocx, "FullScreen"), culture),
                    Convert.ToBoolean(Dispatch.Get(advanced, "SmartSizing"), culture),
                    Convert.ToInt32(Dispatch.Get(advanced, "BitmapPersistence"), culture),
                    Dispatch.Get(secured, "StartProgram") as string,
                    Convert.ToInt32(Dispatch.Get(ocx, "DesktopWidth"), culture),
                    Convert.ToInt32(Dispatch.Get(ocx, "DesktopHeight"), culture));
            });
            // Bureau distant à la taille de l'onglet dès la connexion (pas la taille minimale, 200 × 200).
            var bounds = Win32Input.ScreenBounds(session.Host.SlotHandle);
            output.WriteLine($"Onglet {bounds}, bureau distant {width}x{height}");
            Assert.Equal(RdpConnection.DesktopSize((bounds.Width, bounds.Height)), (width, height));
            Assert.False(fullScreen);
            Assert.False(session.IsFullScreen);
            Assert.True(smartSizing);
            Assert.Equal(0, persistence);
            Assert.Equal("psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP", startProgram);
        });
    }

    /// <summary>
    /// Diagnostic : sans authentification réseau (NLA) et sans vérification du serveur (niveau 0), comme une session PSM
    /// (sans mot de passe) ou avec mot de passe, Windows affiche une boîte de dialogue dès le début de la connexion. Un
    /// guetteur note au journal de débogage le texte de chaque fenêtre qui apparaît (le processus peut s'arrêter juste
    /// après).
    /// </summary>
    [Fact]
    [Trait("Category", "RdpIntegration")]
    public Task NoNlaWithoutPassword() => NoNlaAsync(password: false);

    /// <inheritdoc cref="NoNlaWithoutPassword"/>
    [Fact]
    [Trait("Category", "RdpIntegration")]
    public Task NoNlaWithPassword() => NoNlaAsync(password: true);

    private async Task NoNlaAsync(bool password)
    {
        if (IntegrationAccount() is not { } account)
        {
            return;
        }

        using var watcher = new DialogWatcher();
        var result = await AttemptAsync(account, 0, nla: false, key: null, password, TimeSpan.FromSeconds(password ? 60 : 20));
        output.WriteLine($"Sans NLA, niveau 0, {(password ? "avec" : "sans")} mot de passe → {result}");
        foreach (var seen in watcher.Seen)
        {
            output.WriteLine($"Fenêtre : {seen}");
        }
    }

    /// <summary>Note au journal de débogage le texte de chaque nouvelle fenêtre de ce processus, toutes les 10 ms.</summary>
    private sealed class DialogWatcher : IDisposable
    {
        private readonly HashSet<IntPtr> _known = [.. Win32Input.ProcessWindows()];
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        public DialogWatcher()
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (var window in Win32Input.ProcessWindows())
                    {
                        if (_known.Add(window) && Win32Input.IsWindowVisible(window))
                        {
                            var text = $"{Win32Input.ClassName(window)} {Win32Input.Describe(window)}";
                            lock (Seen)
                            {
                                Seen.Add(text);
                            }

                            Core.Diagnostics.DebugLog.Write("test", $"Nouvelle fenêtre : {text}");
                        }
                    }

                    Thread.Sleep(10);
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public List<string> Seen { get; } = [];

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(1));
            _stop.Dispose();
        }
    }

    private sealed record Attempt(RdpSessionState State, bool Warning, bool? KeyMatched, int? KeyLength, int? Reason, uint? Extended, string? Error);

    /// <summary>
    /// Une connexion à ce poste au niveau d'authentification donné, avec la clé attendue du serveur ; un avertissement
    /// de Windows est fermé (« Non ») et noté. Session ouverte : déconnectée aussitôt.
    /// </summary>
    private async Task<Attempt> AttemptAsync((string User, string Password) account, int level, bool nla, byte[]? key,
        bool password = true, TimeSpan? wait = null)
    {
        var settings = RdpConnectionSettings.Direct("127.0.0.2", 3389, account.User) with { AuthenticationLevel = level, EnableCredSsp = nla };
        Attempt? attempt = null;
        await RunOnStaAsync(new RdpConnectionRequest(settings, password ? account.Password : null) { ServerPublicKey = key }, async session =>
        {
            await session.ConnectAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (session.State == RdpSessionState.Connecting && watch.Elapsed < (wait ?? TimeSpan.FromSeconds(90)))
            {
                if (session.IsAuthenticationWarningOpen)
                {
                    CloseDialogs();
                }

                await Task.Delay(200);
            }

            attempt = new Attempt(session.State, session.AuthenticationWarningShown, session.ServerKeyMatched, session.ServerPublicKey?.Length,
                session.DisconnectReason, session.ExtendedDisconnectReason, session.Error);
            if (session.State is RdpSessionState.Connected or RdpSessionState.Connecting)
            {
                session.Disconnect();
                await WaitForAsync(session, s => s is RdpSessionState.Ended or RdpSessionState.Failed, TimeSpan.FromSeconds(30));
            }
        });
        return attempt!;
    }

    /// <summary>Ferme les boîtes de dialogue de ce processus (avertissement du contrôle Bureau à distance).</summary>
    private void CloseDialogs()
    {
        foreach (var window in Win32Input.ProcessWindows().Where(w => Win32Input.IsWindowVisible(w) && Win32Input.ClassName(w) == "#32770"))
        {
            output.WriteLine($"Boîte de dialogue fermée : {Win32Input.Describe(window)}");
            Win32Input.Close(window);
        }
    }

    /// <summary>
    /// Session PSM dans l'onglet, sur ce poste : le bureau distant prend la taille de l'onglet et la garde quand la fenêtre
    /// change de taille (image mise à l'échelle) ; un clic arrive à l'endroit visé, avant comme après. Une fenêtre lancée à
    /// l'ouverture de session du compte de test (clé Run du workflow) note la taille de son écran et les clics reçus.
    /// </summary>
    [Fact]
    [Trait("Category", "RdpIntegration")]
    public async Task PsmTabKeepsItsSizeAndClicksLandWhereAimed()
    {
        if (IntegrationAccount() is not { } account)
        {
            return;
        }

        var log = Environment.GetEnvironmentVariable("RDP_TEST_CLICKS");
        if (string.IsNullOrEmpty(log))
        {
            output.WriteLine("Pas de fenêtre de test des clics : test ignoré.");
            return;
        }

        var settings = RdpConnectionSettings.Direct("127.0.0.2", 3389, account.User).ForPsmTab() with { AuthenticationLevel = 0 };
        await RunOnStaAsync(new RdpConnectionRequest(settings, account.Password), async (session, window) =>
        {
            await session.ConnectAsync();
            var state = await WaitForAsync(session, s => s != RdpSessionState.Connecting, TimeSpan.FromSeconds(90));
            Assert.Equal(RdpSessionState.Connected, state);

            var slot = session.Host.SlotHandle;
            var bounds = Win32Input.ScreenBounds(slot);
            var (width, height) = RdpConnection.DesktopSize((bounds.Width, bounds.Height));
            output.WriteLine($"Onglet {bounds}, bureau distant attendu {width}x{height}");
            // Le bureau distant a la taille de l'onglet (session reprise : Windows l'adapte à la nouvelle connexion).
            await WaitForLineAsync(log, 0, line => line == $"size {width}x{height}", TimeSpan.FromSeconds(150));

            var host = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            await ClickAndCheckAsync(log, host, bounds, (0.5, 0.5), (width, height));

            // Fenêtre réduite : l'image suit, la résolution du bureau distant ne change pas.
            window.Width = 720;
            window.Height = 540;
            await Task.Delay(TimeSpan.FromSeconds(3));
            bounds = Win32Input.ScreenBounds(slot);
            output.WriteLine($"Onglet réduit {bounds}");
            Assert.True(bounds.Width < width - 100, $"{bounds}");
            Assert.Equal($"size {width}x{height}", Lines(log).Last(l => l.StartsWith("size ", StringComparison.Ordinal)));
            await ClickAndCheckAsync(log, host, bounds, (0.25, 0.25), (width, height));
            await ClickAndCheckAsync(log, host, bounds, (0.75, 0.6), (width, height));
            output.WriteLine($"Clavier : {Win32Input.ClassName(Win32Input.FocusOf(slot))}");
            Assert.Equal(RdpSessionState.Connected, session.State);

            session.Disconnect();
            await WaitForAsync(session, s => s is RdpSessionState.Ended or RdpSessionState.Failed, TimeSpan.FromSeconds(30));
        });
    }

    /// <summary>Clique à la position relative <paramref name="at"/> de l'onglet et vérifie l'endroit reçu sur le bureau distant.</summary>
    private async Task ClickAndCheckAsync(string log, IntPtr host, Win32Input.Bounds tab, (double X, double Y) at, (int Width, int Height) desktop)
    {
        int before = Lines(log).Count;
        var point = (tab.Left + (int)(tab.Width * at.X), tab.Top + (int)(tab.Height * at.Y));
        Win32Input.BringToFront(host);
        await Task.Delay(300);
        Win32Input.Click(point);
        var line = await WaitForLineAsync(log, before, l => l.StartsWith("click ", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
        var xy = line["click ".Length..].Split(',').Select(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var expected = ((int)(desktop.Width * at.X), (int)(desktop.Height * at.Y));
        // Tolérance : arrondis de la mise à l'échelle (quelques pixels du bureau distant).
        int tolerance = Math.Max(4, (int)Math.Ceiling((double)desktop.Width / tab.Width) + 2);
        output.WriteLine($"Clic à {point} (onglet {at}) → reçu {xy[0]},{xy[1]}, attendu {expected}");
        Assert.InRange(xy[0], expected.Item1 - tolerance, expected.Item1 + tolerance);
        Assert.InRange(xy[1], expected.Item2 - tolerance, expected.Item2 + tolerance);
    }

    /// <summary>Lignes du journal de la fenêtre de test (écrit par une autre session : lu sans le bloquer).</summary>
    private static List<string> Lines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    /// <summary>Attend une ligne qui vérifie <paramref name="match"/> après les <paramref name="skip"/> premières.</summary>
    private async Task<string> WaitForLineAsync(string path, int skip, Func<string, bool> match, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var lines = Lines(path);
            if (lines.Skip(skip).FirstOrDefault(match) is { } line)
            {
                return line;
            }

            if (watch.Elapsed > timeout)
            {
                output.WriteLine($"Journal de la fenêtre de test :\n{string.Join("\n", lines.TakeLast(20))}");
                throw new Xunit.Sdk.XunitException("Ligne attendue absente du journal de la fenêtre de test");
            }

            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Compte de test fourni par le workflow d'intégration, ou null (tests ignorés). Le journal de débogage est alors tenu
    /// dans le fichier RDP_TEST_LOG (affiché par le workflow, même si le processus s'arrête).
    /// </summary>
    private (string User, string Password)? IntegrationAccount()
    {
        var user = Environment.GetEnvironmentVariable("RDP_TEST_USER");
        var password = Environment.GetEnvironmentVariable("RDP_TEST_PASSWORD");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || !RdpClientHost.IsAvailable)
        {
            output.WriteLine("Pas de compte RDP de test : test d'intégration ignoré.");
            return null;
        }

        if (Environment.GetEnvironmentVariable("RDP_TEST_LOG") is { Length: > 0 } log && !Core.Diagnostics.DebugLog.Enabled)
        {
            Core.Diagnostics.DebugLog.Start(log, "Tests de sessions réelles");
        }

        return ($"{Environment.MachineName}\\{user}", password);
    }

    /// <summary>Exécute <paramref name="scenario"/> sur un thread STA, la session étant affichée dans une fenêtre.</summary>
    private static Task RunOnStaAsync(RdpConnectionRequest request, Func<RdpSession, Task> scenario) =>
        RunOnStaAsync(request, (session, _) => scenario(session));

    /// <summary>Comme ci-dessus, avec la fenêtre (placée en haut à gauche de l'écran principal).</summary>
    private static async Task RunOnStaAsync(RdpConnectionRequest request, Func<RdpSession, Window, Task> scenario)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var session = new RdpSession("test", _ => Task.FromResult(request));
            var window = new Window
            {
                Width = 900,
                Height = 650,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                Content = session.Host,
            };
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () =>
            {
                try
                {
                    await scenario(session, window);
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
                finally
                {
                    await CloseWindowAsync(session, window);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(4));
    }

    /// <summary>
    /// Ferme la session puis la fenêtre, une fois la fenêtre du contrôle sortie de l'onglet (elle appartient au thread
    /// de la connexion : la fenêtre principale ne doit pas être détruite avec elle).
    /// </summary>
    private static async Task CloseWindowAsync(RdpSession session, Window window)
    {
        session.Dispose();
        try
        {
            await session.Closed.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            window.Dispatcher.InvokeShutdown();
        }
    }

    /// <summary>Attend (sur le thread de la session) un état qui vérifie <paramref name="until"/>.</summary>
    private static async Task<RdpSessionState> WaitForAsync(RdpSession session, Func<RdpSessionState, bool> until, TimeSpan timeout)
    {
        var reached = new TaskCompletionSource<RdpSessionState>();
        void Check()
        {
            if (until(session.State))
            {
                reached.TrySetResult(session.State);
            }
        }

        session.StateChanged += Check;
        try
        {
            Check();
            return await reached.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Délai dépassé : état {session.State}, raison {session.DisconnectReason}, {session.Error}");
        }
        finally
        {
            session.StateChanged -= Check;
        }
    }

    /// <summary>
    /// Le contrôle vit sur son propre thread : bloqué plusieurs secondes (connexion vers une adresse qui ne répond pas),
    /// il ne fige pas l'interface, qui le signale (« ne répond pas ») puis le voit répondre de nouveau.
    /// </summary>
    [Fact]
    public async Task InterfaceStaysResponsiveWhileTheControlIsBlocked()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("10.255.255.1", 3389, @"TEST\user"), null);
        await RunOnStaAsync(request, async session =>
        {
            await session.ConnectAsync();
            Assert.True(session.HasControl, session.Error);
            int controlThread = await session.InvokeOnControlAsync(_ => Environment.CurrentManagedThreadId);
            Assert.NotEqual(Environment.CurrentManagedThreadId, controlThread);

            bool sawNotResponding = false;
            session.StateChanged += () => sawNotResponding |= session.IsNotResponding;
            int ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var blocked = session.InvokeOnControlAsync(_ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
                return true;
            });
            // Signalé en 6 à 7 s d'habitude : sollicitation envoyée jusqu'à 1 s après le blocage, délai de 5 s,
            // surveillance toutes les secondes, en priorité basse (plus tard sur un poste de CI chargé). Le contrôle
            // reste bloqué 10 s.
            while (!sawNotResponding && watch.ElapsedMilliseconds < 9500)
            {
                await Task.Delay(100);
            }

            output.WriteLine($"Interface : {ticks} tops de 100 ms en {watch.ElapsedMilliseconds} ms, « ne répond pas » : {sawNotResponding}");
            Assert.True(ticks >= watch.ElapsedMilliseconds / 200, $"{ticks} tops seulement : l'interface a été bloquée");
            Assert.True(sawNotResponding);

            Assert.True(await blocked);
            for (int i = 0; i < 40 && session.IsNotResponding; i++)
            {
                await Task.Delay(100);
            }

            timer.Stop();
            Assert.False(session.IsNotResponding);
        });
    }

    /// <summary>Fermer une session dont le contrôle est bloqué rend la main aussitôt ; il quitte l'onglet dès qu'il répond.</summary>
    [Fact]
    public async Task ClosingDoesNotWaitForABlockedControl()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("10.255.255.1", 3389, @"TEST\user"), null);
        await RunOnStaAsync(request, async session =>
        {
            await session.ConnectAsync();
            Assert.True(session.HasControl, session.Error);
            _ = session.InvokeOnControlAsync(_ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(4));
                return true;
            });
            await Task.Delay(200);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            session.Dispose();
            long disposeMs = watch.ElapsedMilliseconds;
            Assert.False(session.Closed.IsCompleted);
            await session.Closed.WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"Fermeture : {disposeMs} ms pour rendre la main, fenêtre retirée après {watch.ElapsedMilliseconds} ms");
            Assert.True(disposeMs < 500, $"{disposeMs} ms");
        });
    }

    /// <summary>Fermer l'onglet pendant que la connexion se prépare (appel au PVWA) ne doit pas lever d'erreur.</summary>
    [Fact]
    public async Task ClosingWhilePreparingDoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var session = new RdpSession("test", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("jamais atteint");
            });
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var connect = session.ConnectAsync();
                    session.Dispose();
                    await connect;
                    done.TrySetResult(null);
                }
                catch (Exception e)
                {
                    done.TrySetResult(e);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        var error = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(error);
    }

    /// <summary>
    /// « Déconnecter » pendant une connexion qui ne répond pas (adresse non routable) l'abandonne au lieu de ne rien faire.
    /// </summary>
    [Fact]
    public async Task DisconnectAbandonsAPendingConnection()
    {
        if (!OperatingSystem.IsWindows() || !RdpClientHost.IsAvailable)
        {
            Assert.True(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "Contrôle Bureau à distance absent du poste de CI");
            return;
        }

        var request = new RdpConnectionRequest(RdpConnectionSettings.Direct("10.255.255.1", 3389, @"TEST\user"), null);
        var (state, abandoned) = await RunOnStaThread(request, disconnectAfter: TimeSpan.FromSeconds(2));
        output.WriteLine($"→ {state}, abandonnée pendant la connexion : {abandoned}");
        Assert.NotEqual(RdpSessionState.Connecting, state);
        if (abandoned)
        {
            Assert.Equal(RdpSessionState.Ended, state);
        }
    }

    private static async Task<(RdpSessionState State, bool Abandoned)> RunOnStaThread(RdpConnectionRequest request, TimeSpan disconnectAfter)
    {
        var result = new TaskCompletionSource<(RdpSessionState, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var session = new RdpSession("test", _ => Task.FromResult(request));
            var window = new Window { Width = 900, Height = 650, ShowInTaskbar = false, ShowActivated = false, Content = session.Host };
            bool abandoned = false;
            void Finish()
            {
                result.TrySetResult((session.State, abandoned));
                window.Dispatcher.BeginInvoke(async () => await CloseWindowAsync(session, window));
            }

            var disconnect = new DispatcherTimer { Interval = disconnectAfter };
            disconnect.Tick += (_, _) =>
            {
                disconnect.Stop();
                if (session.State == RdpSessionState.Connecting && session.HasControl)
                {
                    abandoned = true;
                    session.Disconnect();
                }

                // Laisse le temps à un éventuel événement de déconnexion d'arriver.
                var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                settle.Tick += (_, _) =>
                {
                    settle.Stop();
                    Finish();
                };
                settle.Start();
            };
            window.Show();
            disconnect.Start();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () => await session.ConnectAsync());
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return await result.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static Task<(RdpSessionState State, bool ControlFailed, int? Reason, string? Error)> RunOnStaThread(RdpConnectionRequest request)
    {
        var result = new TaskCompletionSource<(RdpSessionState, bool, int?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var session = new RdpSession("test", _ => Task.FromResult(request));
            var window = new Window
            {
                Width = 900,
                Height = 650,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                Content = session.Host,
            };
            void Finish(RdpSessionState state)
            {
                result.TrySetResult((state, session.ControlFailed, session.DisconnectReason, session.Error));
                window.Dispatcher.BeginInvoke(async () => await CloseWindowAsync(session, window));
            }

            session.StateChanged += () =>
            {
                if (session.State is RdpSessionState.Failed or RdpSessionState.Ended)
                {
                    Finish(session.State);
                }
            };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timeout.Tick += (_, _) => Finish(session.State);
            window.Show();
            timeout.Start();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () => await session.ConnectAsync());
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return result.Task;
    }
}
