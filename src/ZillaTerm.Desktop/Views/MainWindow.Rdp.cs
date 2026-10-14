using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Rdp;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>Sessions Bureau à distance intégrées : un onglet par session, contrôle Bureau à distance de Windows.</summary>
public partial class MainWindow
{
    private readonly List<RdpSessionView> _rdpViews = [];
    private Grid? _rdpLayer;

    /// <summary>Calque du modèle des onglets où vivent les sessions Bureau à distance (voir Themes/Controls.axaml).</summary>
    private Grid RdpLayer => _rdpLayer ??= MainTabs.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RdpLayer");

    // Dernière action de l'utilisateur au clavier (et non à la souris) : voir ShowRdpView.
    private bool _lastInputWasKeyboard;

    /// <summary>
    /// Ouvre un onglet Bureau à distance et lance la première connexion. <paramref name="prepare"/> fournit les réglages
    /// de chaque connexion (y compris les reconnexions).
    /// </summary>
    /// <param name="duplicate">Ouvre une autre session sur le même compte ou la même entrée (menu de l'onglet).</param>
    /// <param name="tag">Étiquette du serveur (PROD, QA…) : pastille de l'onglet et cadre du bureau.</param>
    /// <param name="psm">Adresse du PSM pour une session PSM ; null pour une connexion directe.</param>
    private async Task<RdpSession> OpenRdpTabAsync(string label, Func<CancellationToken, Task<RdpConnectionRequest>> prepare,
        Func<Task>? duplicate = null, ServerTag? tag = null, string? psm = null)
    {
        var session = AddRdpTab(label, prepare, duplicate, tag, psm);
        await ConnectRdpTabAsync(session);
        return session;
    }

    /// <summary>Onglet Bureau à distance ajouté et affiché, sans connexion (<see cref="ConnectRdpTabAsync"/>).</summary>
    private RdpSession AddRdpTab(string label, Func<CancellationToken, Task<RdpConnectionRequest>> prepare,
        Func<Task>? duplicate, ServerTag? tag, string? psm)
    {
        var session = new RdpSession(label, prepare) { Psm = psm };
        var view = new RdpSessionView(session) { IsVisible = false, InterventionRecording = Intervention.IsRecording };
        view.ShowTag(tag);
        var tab = new TabItem { Tag = session };
        tab.Header = TabHeader(tab, label, "IconWindows", duplicate, tag);
        // Onglet choisi au clavier : Entrée donne le clavier à la session (voir ShowRdpView).
        tab.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && tab.IsFocused && session.IsConnected)
            {
                session.Focus();
                e.Handled = true;
            }
        };
        session.StateChanged += () =>
        {
            switch (session.State)
            {
                case RdpSessionState.Connected:
                    SessionStatus(label, Text.Format(Strings.RdpOpened, label));
                    break;
                case RdpSessionState.Failed when session.Error is not null:
                    SessionStatus(label, Text.Format(Strings.RdpSessionError, label, session.Error), isError: true);
                    break;
            }
        };

        RdpLayer.Children.Add(view);
        _rdpViews.Add(view);
        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab;
        return session;
    }

    /// <summary>Première connexion d'un onglet ajouté : terminée une fois la connexion lancée (pas ouverte).</summary>
    private async Task ConnectRdpTabAsync(RdpSession session)
    {
        // Laisse la mise en page se faire pour connaître la taille de l'onglet.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await session.ConnectAsync();
    }

    private bool HasRdpTab(RdpSession session) => MainTabs.Items.OfType<TabItem>().Any(t => t.Tag == session);

    /// <summary>Adresse du PSM donnée par le PVWA : dans l'infobulle de l'onglet.</summary>
    private void ShowPsmAddress(RdpSession session, string psm)
    {
        session.Psm = psm;
        if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == session)?.Header is SessionTabHeader header)
        {
            header.SetMode(Text.Format(Strings.TabModePsm, psm));
        }
    }

    /// <summary>
    /// Client pour ouvrir une session PSM dans un onglet, choisi avant la demande au PVWA : vrai pour le client FreeRDP
    /// embarqué (versions publiées : bureau comme application distante), faux pour le contrôle Bureau à distance de
    /// Windows (bureau seulement) ; null pour mstsc : option désactivée, ou ni l'un ni l'autre sur ce poste
    /// (<paramref name="reason"/> le dit alors à l'utilisateur).
    /// </summary>
    private bool? PsmTabClient(string label, out string? reason)
    {
        reason = null;
        if (!_settings.PsmDesktopInTab)
        {
            return null;
        }

        if (FreeRdpClient.IsAvailable)
        {
            return true;
        }

        if (RdpClientHost.IsAvailable)
        {
            return false;
        }

        reason = Text.Format(Strings.PsmTabNoControl, label);
        DebugLog.Write("psm", "Contrôle Bureau à distance absent : session PSM ouverte avec mstsc.");
        return null;
    }

    /// <summary>
    /// Réglages de l'onglet pour ce fichier du PVWA, ou null pour le confier à mstsc : fichier illisible, passerelle
    /// Bureau à distance, application distante sans le client FreeRDP (<paramref name="reason"/> le dit à
    /// l'utilisateur quand il le faut). <paramref name="dynamicResolution"/> : résolution qui suit la taille de l'onglet
    /// (option expérimentale) au lieu d'une image mise à l'échelle. <paramref name="drives"/> : option « partager les
    /// disques » ; décochée, aucun disque partagé, même si le fichier le demande.
    /// </summary>
    private static RdpConnectionSettings? PsmTabSettings(byte[] rdp, string label, bool freeRdp, bool dynamicResolution, bool drives,
        out string? reason)
    {
        reason = null;
        RdpConnectionSettings settings;
        try
        {
            settings = RdpConnectionSettings.FromRdpFile(rdp);
        }
        catch (FormatException)
        {
            DebugLog.Write("psm", "Fichier .rdp sans adresse de serveur : ouvert avec mstsc.");
            return null;
        }

        if (settings.UsesGateway)
        {
            reason = Text.Format(Strings.PsmTabGateway, label);
            DebugLog.Write("psm", "Connexion par une passerelle Bureau à distance : ouverte avec mstsc.");
            return null;
        }

        if (settings.IsRemoteApp && !freeRdp)
        {
            DebugLog.Write("psm", "Composant en application distante (RemoteApp), client FreeRDP absent : ouvert avec mstsc.");
            return null;
        }

        return settings.ForPsmTab(dynamicResolution, drives);
    }

    /// <summary>
    /// Certificat TLS d'un PSM, lu par ZillaTerm avant le lancement du client FreeRDP (aucun jeton envoyé) : épinglé, ou
    /// approuvé par Windows (noté sans question), sinon confirmé par l'utilisateur. Plusieurs par adresse : un PSM en
    /// répartition de charge regroupe plusieurs serveurs, chacun avec son certificat (souvent auto-signé). Un certificat
    /// inconnu alors que d'autres le sont : alerte, puis ajouté (autre serveur PSM) ou à la place des autres
    /// (renouvellement). Le client n'acceptera que les certificats acceptés ici.
    /// </summary>
    private async Task<RdpTrust> VerifyFreeRdpCertificateAsync(RdpCertificate certificate, string server, int port)
    {
        DebugLog.Write("rdp", $"{server}:{port} : certificat SHA256 {certificate.Sha256}, « {certificate.Subject} », "
            + $"{(certificate.Trusted ? "approuvé par Windows" : certificate.Problem)}");
        var host = "rdp://" + server;
        if (KnownHosts.Check(_settings.KnownHosts, host, port, "X.509", certificate.Sha256) == HostKeyStatus.Trusted)
        {
            return RdpTrust.Trusted;
        }

        if (certificate.Trusted)
        {
            KnownHosts.RememberAnother(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
            SaveSettings();
            return RdpTrust.Trusted;
        }

        var address = port == RdpConnectionSettings.DefaultPort ? server : $"{server}:{port}";
        int known = KnownHosts.CountKeys(_settings.KnownHosts, host, port, "X.509");
        if (known == 0)
        {
            return await TrustRdpCertificateAsync(address, server, port, certificate) ? RdpTrust.AcceptedAfterQuestion : RdpTrust.Refused;
        }

        static string Spaced(string sha256) => string.Join(" ", sha256.Chunk(16).Select(c => new string(c)));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        int choice = await ConfirmDialog.AskAsync(this, new ConfirmRequest
        {
            Title = Strings.RdpCertificateTitle,
            Banner = Strings.HostKeyChangedBanner,
            Heading = Strings.RdpCertAnotherHeading,
            Subject = address,
            Message = Text.Format(Strings.RdpCertAnotherMessage, known, certificate.Problem),
            Bullets =
            [
                Text.Format(Strings.FtpCertSubject, certificate.Subject),
                Text.Format(Strings.FtpCertIssuer, certificate.Issuer),
                Text.Format(Strings.FtpCertValidity, certificate.NotBefore.ToString("d", culture), certificate.NotAfter.ToString("d", culture)),
            ],
            Codes = [(Strings.FtpCertFingerprint, Spaced(certificate.Sha256))],
            Kind = ConfirmKind.Danger,
            Actions = [Strings.RdpCertTrustAnother, Strings.RdpCertReplaceKnown],
            CancelLabel = Strings.HostKeyCancel,
            Acknowledge = Strings.RdpCertAckPsm,
        });
        if (choice is not (0 or 1))
        {
            return RdpTrust.Refused;
        }

        if (choice == 0)
        {
            KnownHosts.RememberAnother(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        }
        else
        {
            KnownHosts.Remember(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        }

        SaveSettings();
        return RdpTrust.AcceptedAfterQuestion;
    }

    /// <summary>
    /// Clé d'un PSM qui n'accepte que l'ancienne sécurité RDP, sans TLS, lue par ZillaTerm avant le lancement du client
    /// (aucun jeton envoyé) et épinglée (« rdp://hôte:port », type RDP-RSA). Plusieurs clés par adresse : un PSM en
    /// répartition de charge regroupe plusieurs serveurs, chacun avec la sienne, confirmée une fois. Refusée d'office si ce
    /// serveur a déjà présenté un certificat TLS : le retrait de TLS peut être une interception (la sécurité RDP serait
    /// imposée pour présenter une autre clé).
    /// </summary>
    private async Task<RdpTrust> VerifyFreeRdpServerKeyAsync(RdpServerKey key, string server, int port)
    {
        const string KeyType = "RDP-RSA";
        static string Spaced(string sha256) => string.Join(" ", sha256.Chunk(16).Select(c => new string(c)));
        var host = "rdp://" + server;
        var address = port == RdpConnectionSettings.DefaultPort ? server : $"{server}:{port}";
        DebugLog.Write("rdp", $"{address} : sécurité RDP sans TLS, clé RSA {key.Bits} bits SHA256 {key.Sha256}");
        if (KnownHosts.Known(_settings.KnownHosts, host, port, "X.509") is not null)
        {
            DebugLog.Write("rdp", $"{address} : certificat TLS connu, sécurité RDP sans TLS proposée ; refusée");
            await ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
            {
                Title = Strings.RdpKeyTitle,
                Banner = Strings.HostKeyChangedBanner,
                Heading = Strings.RdpKeyDowngradeHeading,
                Subject = address,
                Message = Strings.RdpKeyDowngradeMessage,
                Kind = ConfirmKind.Danger,
                Actions = [],
                CancelLabel = Strings.HostKeyCancel,
            });
            return RdpTrust.Refused;
        }

        if (KnownHosts.Check(_settings.KnownHosts, host, port, KeyType, key.Sha256) == HostKeyStatus.Trusted)
        {
            return RdpTrust.Trusted;
        }

        int known = KnownHosts.CountKeys(_settings.KnownHosts, host, port, KeyType);
        // Taille de la clé montrée ; moins de 2048 bits, signalée (elle peut être cassée), sans refus.
        (string, string)[] codes =
        [
            (Strings.RdpKeyFingerprint, Spaced(key.Sha256)),
            (Strings.RdpKeySize, Text.Format(Strings.RdpKeyBits, key.Bits)),
        ];
        string[] weak = key.IsWeak ? [Text.Format(Strings.RdpKeyWeak, key.Bits, RdpServerKey.RecommendedBits)] : [];
        var kind = key.IsWeak ? ConfirmKind.Danger : ConfirmKind.Warning;
        var request = known == 0
            ? new ConfirmRequest
            {
                Title = Strings.RdpKeyTitle,
                Heading = Strings.RdpKeyVerifyHeading,
                Subject = address,
                Message = Strings.RdpKeyMessage,
                Bullets = weak,
                Codes = codes,
                Kind = kind,
                Actions = [Strings.HostKeyTrust],
                CancelLabel = Strings.HostKeyCancel,
            }
            : new ConfirmRequest
            {
                Title = Strings.RdpKeyTitle,
                Heading = Strings.RdpKeyAnotherHeading,
                Subject = address,
                Message = Text.Format(Strings.RdpKeyAnotherMessage, known),
                Bullets = weak,
                Codes = codes,
                Kind = kind,
                Actions = [Strings.RdpKeyTrustAnother],
                CancelLabel = Strings.HostKeyCancel,
                Acknowledge = Strings.RdpKeyAckLoadBalanced,
            };
        if (!await ConfirmDialog.ConfirmAsync(this, request))
        {
            return RdpTrust.Refused;
        }

        KnownHosts.RememberAnother(_settings.KnownHosts, host, port, KeyType, key.Sha256);
        SaveSettings();
        return RdpTrust.AcceptedAfterQuestion;
    }

    /// <summary>
    /// Empreintes SHA-256 des certificats TLS et des clés RDP acceptés pour ce serveur : le client FreeRDP n'accepte
    /// qu'elles.
    /// </summary>
    private IReadOnlyCollection<string> KnownFreeRdpFingerprints(string server, int port) =>
        KnownHosts.KnownKeys(_settings.KnownHosts, "rdp://" + server, port)
            .Where(k => k.Algorithm is "X.509" or "RDP-RSA" && k.Sha256.Length == 64 && k.Sha256.All(char.IsAsciiHexDigit))
            .Select(k => k.Sha256)
            .ToList();

    private async Task CloseRdpTabAsync(TabItem tab)
    {
        if (tab.Tag is not RdpSession session || !MainTabs.Items.Contains(tab))
        {
            return;
        }

        if (session.IsConnected && !await ConfirmCloseSessionAsync(this, session.Label, Strings.SessionCloseRdp))
        {
            return;
        }

        if (!MainTabs.Items.Contains(tab))
        {
            return;
        }

        await RemoveRdpTabAsync(session);
        SessionStatus(session.Label, Text.Format(Strings.RdpClosed, session.Label));
    }

    private async Task RemoveRdpTabAsync(RdpSession session)
    {
        if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == session) is { } tab)
        {
            MainTabs.Items.Remove(tab);
        }

        MainTabs.SelectedItem ??= HomeTab;
        if (_rdpViews.FirstOrDefault(v => v.Session == session) is not { } view)
        {
            // Déjà retirée avec toutes les autres (déconnexion, fermeture) : la session est fermée.
            session.Dispose();
            return;
        }

        view.IsVisible = false;
        // Le contrôle reste dans la fenêtre jusqu'à la fin de la déconnexion ; il la quitte avant d'être libéré.
        await session.CloseAsync();
        await Task.WhenAny(session.Closed, Task.Delay(TimeSpan.FromSeconds(3)));
        _rdpViews.Remove(view);
        RdpLayer.Children.Remove(view);
    }

    /// <summary>
    /// Ferme toutes les sessions et attend (3 s au plus) que leurs fenêtres aient quitté les onglets : la fenêtre
    /// principale ne doit pas être détruite avec des fenêtres d'un autre thread. Les sessions sont ensuite retirées de
    /// la fenêtre, comme à la fermeture d'un onglet : l'emplacement d'une session bloquée, avec la fenêtre de son
    /// contrôle, est mis de côté par WPF hors de la fenêtre principale. Faux si l'une n'a pas répondu.
    /// </summary>
    private async Task<bool> CloseAllRdpSessionsAsync()
    {
        var views = _rdpViews.ToList();
        _rdpViews.Clear();
        foreach (var view in views)
        {
            view.Session.Dispose();
            // Onglet retiré avec sa session : une demande au PVWA qui se termine ensuite ne le retrouve plus.
            if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == view.Session) is { } tab)
            {
                MainTabs.Items.Remove(tab);
            }
        }

        var all = Task.WhenAll(views.Select(v => v.Session.Closed));
        bool closed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))) == all;
        foreach (var view in views)
        {
            RdpLayer.Children.Remove(view);
        }

        // Emplacements sortis de la fenêtre avant qu'elle ne soit détruite.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return closed;
    }

    /// <summary>
    /// Connexion directe : le certificat TLS du serveur est lu avant tout envoi, puis vérifié par
    /// <see cref="TrustRdpCertificateAsync"/>. Refusé : la connexion s'arrête là, sans que le mot de passe soit lu.
    /// </summary>
    private async Task CheckRdpCertificateAsync(string address, RdpConnectionSettings settings, CancellationToken ct)
    {
        var certificate = await RdpCertificateProbe.GetAsync(settings.Server, settings.Port, ct);
        DebugLog.Write("rdp", $"{settings.Server}:{settings.Port} : certificat SHA256 {certificate.Sha256}, « {certificate.Subject} », "
            + $"{(certificate.Trusted ? "approuvé par Windows" : certificate.Problem)}");
        if (!await TrustRdpCertificateAsync(address, settings.Server, settings.Port, certificate))
        {
            throw new OperationCanceledException(Strings.RdpCertificateDeclined);
        }
    }

    /// <summary>
    /// Demande d'une connexion directe (accès d'urgence KeePass). Avec le client FreeRDP de ZillaTerm, le certificat
    /// épinglé est imposé à la connexion même qui porte le mot de passe (« /cert:deny,fingerprint:… ») : un autre serveur
    /// est refusé sans rien recevoir, et le mot de passe passe par l'entrée standard du client, jamais par sa ligne de
    /// commande. Sans lui, le contrôle Bureau à distance de Windows : il ouvre sa propre connexion, que rien ne lie au
    /// certificat épinglé (Windows avertit seulement si le certificat n'est pas approuvé).
    /// </summary>
    private RdpConnectionRequest DirectRdpRequest(string address, RdpConnectionSettings settings, string? password, bool freeRdp) =>
        new(settings, password)
        {
            FreeRdp = freeRdp,
            // Certificat déjà vérifié avant la lecture du mot de passe : relu par le client, il doit être le même (ou
            // approuvé après une nouvelle question). Ni ancienne sécurité RDP, ni clé : un serveur sans TLS est refusé.
            VerifyCertificate = freeRdp
                ? async (certificate, server, port) =>
                    await TrustRdpCertificateAsync(address, server, port, certificate) ? RdpTrust.Trusted : RdpTrust.Refused
                : null,
            KnownFingerprints = freeRdp ? KnownFreeRdpFingerprints : null,
        };

    /// <summary>
    /// Certificat épinglé par serveur (clé « rdp://hôte:port », comme les certificats FTPS). Approuvé par Windows : noté
    /// sans question (un certificat non approuvé présenté ensuite est un changement). Sinon : accepté s'il est celui
    /// épinglé, question au premier usage, alerte s'il a changé, qui ne s'accepte qu'après confirmation.
    /// </summary>
    private async Task<bool> TrustRdpCertificateAsync(string address, string server, int port, RdpCertificate certificate)
    {
        var host = "rdp://" + server;
        var status = KnownHosts.Check(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        if (!certificate.Trusted
            && !await ConfirmCertificateAsync(Strings.RdpCertificateTitle, Strings.RdpCertVerifyHeading, Strings.RdpCertChangedHeading, address,
                (certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Sha256, certificate.Problem),
                status == HostKeyStatus.Unknown ? null : KnownHosts.Known(_settings.KnownHosts, host, port, "X.509")?.Sha256 ?? ""))
        {
            return false;
        }

        KnownHosts.Remember(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        SaveSettings();
        return true;
    }

    /// <summary>
    /// Affiche la session Bureau à distance de l'onglet choisi et masque les autres. Choisi à la souris, l'onglet donne
    /// le clavier à la session ; au clavier (Ctrl+Tab, Ctrl+F4…), le focus reste sur l'onglet : le client FreeRDP prend
    /// toutes les touches dès qu'il a le clavier, et Ctrl+Tab n'irait plus à l'onglet suivant. Entrée (ou F6, ou un clic
    /// dans la session) lui donne ensuite le clavier.
    /// </summary>
    private void ShowRdpView(TabItem? tab)
    {
        bool byKeyboard = _lastInputWasKeyboard;
        foreach (var view in _rdpViews)
        {
            bool selected = ReferenceEquals(tab?.Tag, view.Session);
            view.IsVisible = selected;
            if (selected && byKeyboard)
            {
                Dispatcher.UIThread.Post(() => tab!.Focus(), DispatcherPriority.Input);
            }
            else if (selected)
            {
                Dispatcher.UIThread.Post(view.Session.Focus, DispatcherPriority.Input);
            }
        }
    }
}
