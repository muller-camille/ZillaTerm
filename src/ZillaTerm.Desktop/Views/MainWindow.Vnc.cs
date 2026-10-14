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
using ZillaTerm.App.Services;

namespace ZillaTerm.App.Views;

/// <summary>Sessions VNC (accès d'urgence, entrées KeePass vnc://) : un onglet par session, client RFB intégré.</summary>
public partial class MainWindow
{
    /// <param name="password">Mot de passe VNC, lu à chaque connexion (null si le coffre a été verrouillé entre-temps).</param>
    /// <param name="duplicate">Ouvre une autre session sur la même entrée (menu de l'onglet).</param>
    private void OpenVncTab(string label, string host, int port, Func<string?> password, Func<Task>? duplicate)
    {
        // Mot de passe VNC envoyé en clair (ou presque) : accord demandé une fois pour l'onglet, reconnexions comprises.
        bool passwordAccepted = false;
        var session = new VncSession(label, host, port, password,
            () => passwordAccepted || (passwordAccepted = UiWait.Run(() => ConfirmVncPasswordAsync(host, port))));
        var view = new VncSessionView(session);
        var tab = new TabItem { Content = view, Tag = session };
        tab.Header = TabHeader(tab, label, "IconConnect", duplicate);
        session.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            switch (session.State)
            {
                case VncSessionState.Connected:
                    SessionStatus(label, Text.Format(Strings.VncOpened, label));
                    break;
                case VncSessionState.Failed or VncSessionState.Closed when session.Error is not null:
                    SessionStatus(label, Text.Format(Strings.VncSessionError, label, session.Error), isError: true);
                    break;
            }
        });

        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab;
        _ = session.ConnectAsync();
    }

    /// <summary>
    /// Avant le premier envoi du mot de passe VNC : le protocole ne chiffre rien et ne vérifie pas le serveur, qui peut
    /// en retrouver les 8 premiers caractères. « Annuler la connexion » par défaut.
    /// </summary>
    private Task<bool> ConfirmVncPasswordAsync(string host, int port) =>
        ConfirmDialog.ConfirmAsync(this, new ConfirmRequest
        {
            Title = Strings.VncPasswordTitle,
            Heading = Strings.VncPasswordHeading,
            Subject = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}",
            Message = Strings.VncPasswordMessage,
            Bullets = [Strings.VncPasswordOnce],
            Kind = ConfirmKind.Warning,
            Actions = [Strings.VncPasswordSend],
            CancelLabel = Strings.HostKeyCancel,
        });

    private async Task CloseVncTabAsync(TabItem tab, Window? owner = null)
    {
        if (tab.Tag is not VncSession session)
        {
            return;
        }

        if (session.IsConnected && !await ConfirmCloseSessionAsync(owner ?? this, session.Label, Strings.SessionCloseVnc))
        {
            return;
        }

        if (!MainTabs.Items.Contains(tab))
        {
            return;
        }

        RemoveVncTab(tab);
        SessionStatus(session.Label, Text.Format(Strings.VncClosed, session.Label));
    }

    private void RemoveVncTab(TabItem tab)
    {
        MainTabs.Items.Remove(tab);
        (tab.Tag as VncSession)?.Dispose();
        MainTabs.SelectedItem ??= HomeTab;
    }

    /// <summary>Fermeture de la fenêtre : connexions VNC fermées.</summary>
    private void CloseAllVncSessions()
    {
        foreach (var session in MainTabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<VncSession>().ToList())
        {
            session.Dispose();
        }
    }
}
