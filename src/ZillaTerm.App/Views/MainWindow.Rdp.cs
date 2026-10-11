using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    /// <summary>Calque du modèle des onglets où vivent les sessions Bureau à distance (voir Theme.xaml).</summary>
    private Grid RdpLayer => _rdpLayer ??= (Grid)MainTabs.Template.FindName("RdpLayer", MainTabs);

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
        var session = new RdpSession(label, prepare) { Psm = psm };
        var view = new RdpSessionView(session) { Visibility = Visibility.Hidden, InterventionRecording = Intervention.IsRecording };
        view.ShowTag(tag);
        var tab = new TabItem { Tag = session };
        tab.Header = TabHeader(tab, label, "IconWindows", duplicate, tag);
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
        // Laisse la mise en page se faire pour connaître la taille de l'onglet.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await session.ConnectAsync();
        return session;
    }

    /// <summary>
    /// Réglages pour ouvrir dans un onglet la session PSM de ce fichier du PVWA, ou null pour la confier à mstsc :
    /// option désactivée, fichier illisible, passerelle Bureau à distance, ou ni client FreeRDP ni contrôle Bureau à
    /// distance pour ce fichier (<paramref name="reason"/> le dit alors à l'utilisateur). Avec le client FreeRDP
    /// embarqué (versions publiées), bureau comme application distante s'ouvrent dans l'onglet ; sans lui, seulement
    /// un bureau, avec le contrôle de Windows.
    /// </summary>
    private (RdpConnectionSettings Settings, bool FreeRdp)? PsmTabSettings(byte[] rdp, string label, out string? reason)
    {
        reason = null;
        if (!_settings.PsmDesktopInTab)
        {
            return null;
        }

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

        if (FreeRdpClient.IsAvailable)
        {
            return (settings.ForPsmTab(), true);
        }

        if (settings.IsRemoteApp)
        {
            DebugLog.Write("psm", "Composant en application distante (RemoteApp), client FreeRDP absent : ouvert avec mstsc.");
            return null;
        }

        if (!RdpClientHost.IsAvailable)
        {
            reason = Text.Format(Strings.PsmTabNoControl, label);
            DebugLog.Write("psm", "Contrôle Bureau à distance absent : session PSM ouverte avec mstsc.");
            return null;
        }

        return (settings.ForPsmTab(), false);
    }

    /// <summary>
    /// Certificat présenté au client FreeRDP (PSM, redirection ou passerelle), vérifié comme pour une connexion directe
    /// (<see cref="TrustRdpCertificate"/>) : approuvé par Windows ou épinglé, accepté tout de suite ; sinon refusé tout de
    /// suite (le serveur ne patiente pas pendant une question) et la question est posée une fois la connexion fermée,
    /// puis une nouvelle connexion est lancée si l'utilisateur fait confiance. La chaîne est vérifiée hors du thread de
    /// l'interface (révocation en ligne).
    /// </summary>
    private async Task<FreeRdpAnswer> VerifyFreeRdpCertificateAsync(FreeRdpCertificateRequest request)
    {
        RdpCertificate certificate;
        try
        {
            certificate = await Task.Run(() => RdpCertificateProbe.FromPemChain(request.Pem, request.Host));
        }
        catch (RdpCertificateException e)
        {
            DebugLog.Write("rdp", $"{request.Host}:{request.Port} : certificat illisible ({e.Message}) ; refusé");
            return FreeRdpAnswer.Refused;
        }

        var kind = request.IsGateway ? "passerelle " : request.IsRedirect ? "redirection " : "";
        DebugLog.Write("rdp", $"{kind}{request.Host}:{request.Port} : certificat SHA256 {certificate.Sha256}, « {certificate.Subject} », "
            + $"{(certificate.Trusted ? "approuvé par Windows" : certificate.Problem)}");
        var address = request.Port == RdpConnectionSettings.DefaultPort ? request.Host : $"{request.Host}:{request.Port}";
        var status = KnownHosts.Check(_settings.KnownHosts, "rdp://" + request.Host, request.Port, "X.509", certificate.Sha256);
        if (status == HostKeyStatus.Trusted || certificate.Trusted)
        {
            // Approuvé par Windows : noté sans question (TrustRdpCertificate).
            return TrustRdpCertificate(address, request.Host, request.Port, certificate) ? FreeRdpAnswer.Accepted : FreeRdpAnswer.Refused;
        }

        return FreeRdpAnswer.Ask(() => Task.FromResult(TrustRdpCertificate(address, request.Host, request.Port, certificate)));
    }

    /// <summary>
    /// Clé d'un PSM qui n'accepte que l'ancienne sécurité RDP, sans TLS, épinglée (« rdp://hôte:port », type RDP-RSA).
    /// Plusieurs clés par adresse : un PSM en répartition de charge regroupe plusieurs serveurs, chacun avec la sienne,
    /// confirmée une fois. Une clé connue est acceptée tout de suite ; une autre est refusée tout de suite (le PSM ferme
    /// une connexion qui attend) et la question est posée une fois la connexion fermée, puis une nouvelle connexion
    /// (nouveau jeton PSM) est lancée si l'utilisateur fait confiance. Refusée d'office si ce serveur a déjà présenté un
    /// certificat TLS : le retrait de TLS peut être une interception (la sécurité RDP serait imposée pour présenter une
    /// autre clé).
    /// </summary>
    private Task<FreeRdpAnswer> VerifyFreeRdpServerKeyAsync(FreeRdpServerKey key)
    {
        const string KeyType = "RDP-RSA";
        static string Spaced(string sha256) => string.Join(" ", sha256.Chunk(16).Select(c => new string(c)));
        var host = "rdp://" + key.Host;
        var address = key.Port == RdpConnectionSettings.DefaultPort ? key.Host : $"{key.Host}:{key.Port}";
        if (KnownHosts.Known(_settings.KnownHosts, host, key.Port, "X.509") is not null)
        {
            DebugLog.Write("rdp", $"{address} : certificat TLS connu, sécurité RDP sans TLS proposée ; refusée");
            return Task.FromResult(FreeRdpAnswer.Ask(() =>
            {
                ConfirmDialog.Confirm(this, new ConfirmRequest
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
                return Task.FromResult(false);
            }));
        }

        if (KnownHosts.Check(_settings.KnownHosts, host, key.Port, KeyType, key.Sha256) == HostKeyStatus.Trusted)
        {
            return Task.FromResult(FreeRdpAnswer.Accepted);
        }

        return Task.FromResult(FreeRdpAnswer.Ask(() =>
        {
            // Clés connues relues au moment de la question : une autre session a pu en ajouter une entre-temps.
            int known = KnownHosts.CountKeys(_settings.KnownHosts, host, key.Port, KeyType);
            if (KnownHosts.Check(_settings.KnownHosts, host, key.Port, KeyType, key.Sha256) == HostKeyStatus.Trusted)
            {
                return Task.FromResult(true);
            }

            var request = known == 0
                ? new ConfirmRequest
                {
                    Title = Strings.RdpKeyTitle,
                    Heading = Strings.RdpKeyVerifyHeading,
                    Subject = address,
                    Message = Strings.RdpKeyMessage,
                    Codes = [(Strings.RdpKeyFingerprint, Spaced(key.Sha256))],
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.HostKeyTrust],
                    CancelLabel = Strings.HostKeyCancel,
                }
                : new ConfirmRequest
                {
                    Title = Strings.RdpKeyTitle,
                    Heading = Strings.RdpKeyAnotherHeading,
                    Subject = address,
                    Message = Text.Format(Strings.RdpKeyAnotherMessage, known),
                    Codes = [(Strings.RdpKeyFingerprint, Spaced(key.Sha256))],
                    Kind = ConfirmKind.Warning,
                    Actions = [Strings.RdpKeyTrustAnother],
                    CancelLabel = Strings.HostKeyCancel,
                    Acknowledge = Strings.RdpKeyAckLoadBalanced,
                };
            if (!ConfirmDialog.Confirm(this, request))
            {
                return Task.FromResult(false);
            }

            KnownHosts.RememberAnother(_settings.KnownHosts, host, key.Port, KeyType, key.Sha256);
            SaveSettings();
            return Task.FromResult(true);
        }));
    }

    private async Task CloseRdpTabAsync(TabItem tab)
    {
        if (tab.Tag is not RdpSession session || !MainTabs.Items.Contains(tab))
        {
            return;
        }

        if (session.IsConnected && !ConfirmCloseSession(this, session.Label, Strings.SessionCloseRdp))
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
        var view = _rdpViews.First(v => v.Session == session);
        view.Visibility = Visibility.Hidden;
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
        }

        var all = Task.WhenAll(views.Select(v => v.Session.Closed));
        bool closed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))) == all;
        foreach (var view in views)
        {
            RdpLayer.Children.Remove(view);
        }

        // Emplacements sortis de la fenêtre avant qu'elle ne soit détruite.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        return closed;
    }

    /// <summary>
    /// Connexion directe : le certificat TLS du serveur est lu avant tout envoi (le contrôle Bureau à distance ne permet
    /// pas d'épingler un certificat), puis vérifié par <see cref="TrustRdpCertificate"/>. Refusé : la connexion
    /// s'arrête là, sans que le mot de passe soit lu.
    /// </summary>
    private async Task CheckRdpCertificateAsync(string address, RdpConnectionSettings settings, CancellationToken ct)
    {
        var certificate = await RdpCertificateProbe.GetAsync(settings.Server, settings.Port, ct);
        DebugLog.Write("rdp", $"{settings.Server}:{settings.Port} : certificat SHA256 {certificate.Sha256}, « {certificate.Subject} », "
            + $"{(certificate.Trusted ? "approuvé par Windows" : certificate.Problem)}");
        if (!TrustRdpCertificate(address, settings.Server, settings.Port, certificate))
        {
            throw new OperationCanceledException(Strings.RdpCertificateDeclined);
        }
    }

    /// <summary>
    /// Certificat épinglé par serveur (clé « rdp://hôte:port », comme les certificats FTPS). Approuvé par Windows : noté
    /// sans question (un certificat non approuvé présenté ensuite est un changement). Sinon : accepté s'il est celui
    /// épinglé, question au premier usage, alerte s'il a changé, qui ne s'accepte qu'après confirmation.
    /// </summary>
    private bool TrustRdpCertificate(string address, string server, int port, RdpCertificate certificate)
    {
        var host = "rdp://" + server;
        var status = KnownHosts.Check(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        if (!certificate.Trusted
            && !ConfirmCertificate(Strings.RdpCertificateTitle, Strings.RdpCertVerifyHeading, Strings.RdpCertChangedHeading, address,
                (certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Sha256, certificate.Problem),
                status == HostKeyStatus.Unknown ? null : KnownHosts.Known(_settings.KnownHosts, host, port, "X.509")?.Sha256 ?? ""))
        {
            return false;
        }

        KnownHosts.Remember(_settings.KnownHosts, host, port, "X.509", certificate.Sha256);
        SaveSettings();
        return true;
    }

    /// <summary>Affiche la session Bureau à distance de l'onglet choisi et masque les autres.</summary>
    private void ShowRdpView(TabItem? tab)
    {
        foreach (var view in _rdpViews)
        {
            bool selected = ReferenceEquals(tab?.Tag, view.Session);
            view.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
            if (selected)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, view.Session.Focus);
            }
        }
    }
}
