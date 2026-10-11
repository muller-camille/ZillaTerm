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
    /// option désactivée, application distante (composants PSM pour SSH, WinSCP…), fichier illisible ou contrôle
    /// Bureau à distance absent (<paramref name="reason"/> le dit alors à l'utilisateur).
    /// </summary>
    private RdpConnectionSettings? PsmTabSettings(byte[] rdp, string label, out string? reason)
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

        if (settings.IsRemoteApp)
        {
            DebugLog.Write("psm", "Composant en application distante (RemoteApp) : ouvert avec mstsc.");
            return null;
        }

        if (!RdpClientHost.IsAvailable)
        {
            reason = Text.Format(Strings.PsmTabNoControl, label);
            DebugLog.Write("psm", "Contrôle Bureau à distance absent : session PSM ouverte avec mstsc.");
            return null;
        }

        return settings.ForPsmTab();
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
        if (!TrustRdpCertificate(address, settings, certificate))
        {
            throw new OperationCanceledException(Strings.RdpCertificateDeclined);
        }
    }

    /// <summary>
    /// Certificat épinglé par serveur (clé « rdp://hôte:port », comme les certificats FTPS). Approuvé par Windows : noté
    /// sans question (un certificat non approuvé présenté ensuite est un changement). Sinon : accepté s'il est celui
    /// épinglé, question au premier usage, alerte s'il a changé, qui ne s'accepte qu'après confirmation.
    /// </summary>
    private bool TrustRdpCertificate(string address, RdpConnectionSettings settings, RdpCertificate certificate)
    {
        var host = "rdp://" + settings.Server;
        var status = KnownHosts.Check(_settings.KnownHosts, host, settings.Port, "X.509", certificate.Sha256);
        if (status == HostKeyStatus.Trusted)
        {
            return true;
        }

        if (!certificate.Trusted
            && !ConfirmCertificate(Strings.RdpCertificateTitle, Strings.RdpCertVerifyHeading, Strings.RdpCertChangedHeading, address,
                (certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Sha256, certificate.Problem),
                status == HostKeyStatus.Unknown ? null : KnownHosts.Known(_settings.KnownHosts, host, settings.Port, "X.509")?.Sha256 ?? ""))
        {
            return false;
        }

        KnownHosts.Remember(_settings.KnownHosts, host, settings.Port, "X.509", certificate.Sha256);
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
