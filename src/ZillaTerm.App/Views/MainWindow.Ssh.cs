using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core;
using ZillaTerm.Core.Ssh;
using ZillaTerm.Core.Terminal;
using Renci.SshNet;

namespace ZillaTerm.App.Views;

/// <summary>Sessions SSH intégrées : un onglet terminal par session, panneau « Fichiers » associé.</summary>
public partial class MainWindow
{
    private readonly SshInteraction _psmpUi;
    // Sessions des onglets terminal et fichiers seuls.
    private readonly List<RemoteSession> _remoteSessions = [];
    private MfaSshKey? _mfaKey;
    private Task<MfaKeyFetch>? _mfaFetch;
    private DateTime _mfaRetryAfter;
    // Une clé MFA a été délivrée pendant cette session PVWA : elle est retirée à la déconnexion.
    private bool _mfaKeyIssued;
    // Sessions ouvertes ensemble (dossier, sélection, vue parallèle) : mot de passe du PSMP réutilisable entre elles.
    private SshAnswerCache? _openingGroup;

    /// <summary>
    /// Onglet terminal d'une session via le PSMP, ouvert tout de suite : la clé MFA du PVWA puis la connexion au PSMP se
    /// font dans l'onglet, qui montre leur progression.
    /// </summary>
    private void OpenSshTab(PvwaAccount account, PsmpEndpoint psmp, string login, string label, SavedSession? saved,
        Func<Task>? duplicate, ConnectRequest request)
    {
        var connector = new SshConnector(psmp.Host, psmp.Port, login, _psmpUi.For(label), PsmpKeyAsync, group: _openingGroup);
        var session = new SshSession(account, label, connector, Dispatcher, _settings.FollowTerminalFolder, saved)
        {
            Psmp = psmp.Host,
            Request = request,
            X11 = request.X11Forwarding && X11Display.TryParse(_settings.X11Display, out var display) ? display : null,
        };
        ShowSshTab(session, $"{login}@{psmp.Host}", Strings.ConnectingViaPsmp, "IconSsh",
            Text.Format(Strings.SshOpened, label, psmp.Host), duplicate);
    }

    /// <summary>Onglet terminal + panneau « Fichiers » pour une session SSH (via le PSMP ou directe).</summary>
    /// <param name="target">« utilisateur@serveur », affiché pendant la connexion.</param>
    /// <param name="duplicate">Ouvre une autre session sur le même compte ou la même entrée (menu de l'onglet).</param>
    private void ShowSshTab(SshSession session, string target, string connectingText, string icon, string openedMessage,
        Func<Task>? duplicate)
    {
        var label = session.Label;
        session.Editor = new RemoteEditor(session, this, _settings, (text, error) => SetStatus(text, error),
            directory => FilesPanel.OnRemoteChanged(session, directory));
        var view = new SshSessionView(session, target, connectingText) { PasteGuard = ConfirmMultiLinePaste };
        view.ShowTag(TagOf(session));
        var tab = new TabItem { Content = view, Tag = session };
        tab.Header = TabHeader(tab, label, icon, duplicate);
        view.SessionMenu = items => AddTerminalSessionItems(items, tab, duplicate);
        // Texte du terminal gardé pendant l'enregistrement d'une intervention.
        Intervention.Attach(session);
        session.StateChanged += () =>
        {
            switch (session.State)
            {
                case RemoteSessionState.Connected:
                    SessionStatus(label, openedMessage);
                    break;
                case RemoteSessionState.Failed:
                    SessionStatus(label, Text.Format(Strings.SshSessionError, label, session.Error), isError: true);
                    break;
                case RemoteSessionState.Closed:
                    InterventionRecord(Core.Interventions.InterventionKind.Connection, label, Strings.InterventionClosedByServer);
                    break;
            }
        };

        _remoteSessions.Add(session);
        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab;
        // Les fichiers du serveur à côté du terminal ; un panneau de gauche replié le reste.
        SelectSideTab(FilesTab, expand: false);
        // La connexion (et ses éventuelles questions : clé d'hôte, mot de passe, MFA) se poursuit dans l'onglet.
        _ = view.ConnectAsync();
    }

    /// <summary>
    /// En-tête d'un onglet de session : pastille d'état, icône, nom (numéroté si un autre onglet porte le même), bouton
    /// de fermeture, menu (clic droit) ; l'infobulle dit l'état et par où passe la session.
    /// </summary>
    /// <param name="tag">Étiquette du serveur ; par défaut, celle d'une session SSH ou Fichiers.</param>
    private SessionTabHeader TabHeader(TabItem tab, string label, string icon, Func<Task>? duplicate, ServerTag? tag = null)
    {
        var closeButton = new Button
        {
            Style = (Style)FindResource("TabCloseButton"),
            Content = Palette.Icon("IconClose", 11),
            ToolTip = Strings.CloseSessionTip,
        };
        closeButton.Click += (_, _) => CloseSessionTab(tab);
        var mode = tab.Tag switch
        {
            RemoteSession { Psmp: { } psmp } => Text.Format(Strings.TabModePsmp, psmp),
            RdpSession { Psm: "" } => Strings.TabModePsmPending,
            RdpSession { Psm: { } psm } => Text.Format(Strings.TabModePsm, psm),
            _ => Strings.TabModeDirect,
        };
        var shown = SessionTabHeader.UniqueLabel(label, SessionTabs().Select(t => t.Header).OfType<SessionTabHeader>().Select(h => h.Label));
        var header = new SessionTabHeader(shown, icon, closeButton, mode,
            tab.Tag is SshSession ? Strings.TabDetachTip : null, tag ?? (tab.Tag is RemoteSession remote ? TagOf(remote) : null));
        // Clic molette sur l'onglet : fermeture.
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseSessionTab(tab);
            }
        };
        header.ContextMenu = TabMenu(tab, duplicate);
        FollowState(tab, header);
        if (tab.Tag is SshSession)
        {
            EnableDragToDetach(tab, header);
        }

        return header;
    }

    /// <summary>
    /// Étiquette (PROD, QA…) du serveur d'une session : celle du serveur de « Mes serveurs » ouvert, ou, pour une session
    /// ouverte depuis « Disponibles » ou les connexions récentes, celle du serveur enregistré pour ce compte et cette
    /// machine cible.
    /// </summary>
    private ServerTag? TagOf(RemoteSession session) => TagOf(session.Account, session.Saved, session.Request?.RemoteMachine);

    /// <summary>Étiquette du serveur ouvert sur ce compte (voir <see cref="TagOf(RemoteSession)"/>).</summary>
    private ServerTag? TagOf(PvwaAccount? account, SavedSession? saved, string? remoteMachine)
    {
        var name = saved?.Tag;
        if (name is null && saved is null && account is not null)
        {
            name = SessionLibrary.FindSession(_settings, account, PvwaHost, remoteMachine)?.Tag;
        }

        return ServerTagView.Resolve(_settings.ServerTags, name);
    }

    /// <summary>La pastille de l'onglet suit l'état de sa session.</summary>
    private void FollowState(TabItem tab, SessionTabHeader header)
    {
        switch (tab.Tag)
        {
            case RemoteSession session:
                session.StateChanged += () => header.SetState(session.State switch
                {
                    RemoteSessionState.Connected => SessionTabState.Connected,
                    RemoteSessionState.Closed => SessionTabState.Ended,
                    RemoteSessionState.Failed => SessionTabState.Failed,
                    _ => SessionTabState.Connecting,
                });
                break;
            case RdpSession rdp:
                rdp.StateChanged += () => header.SetState(rdp.State switch
                {
                    RdpSessionState.Connected => SessionTabState.Connected,
                    RdpSessionState.Ended => SessionTabState.Ended,
                    RdpSessionState.Failed => SessionTabState.Failed,
                    _ => SessionTabState.Connecting,
                });
                break;
            case VncSession vnc:
                // Événement levé hors du fil de l'interface.
                vnc.StateChanged += () => Dispatcher.BeginInvoke(() => header.SetState(vnc.State switch
                {
                    VncSessionState.Connected => SessionTabState.Connected,
                    VncSessionState.Closed => SessionTabState.Ended,
                    VncSessionState.Failed => SessionTabState.Failed,
                    _ => SessionTabState.Connecting,
                }));
                break;
        }
    }

    /// <summary>Menu de l'en-tête d'un onglet de session : reconnecter, dupliquer, fermer, fermer les autres.</summary>
    private ContextMenu TabMenu(TabItem tab, Func<Task>? duplicate)
    {
        var actions = CreateSessionActions(tab, duplicate, () => this);
        var ssh = tab.Tag is SshSession ? Visibility.Visible : Visibility.Collapsed;
        var search = new MenuItem { Header = Strings.MenuTerminalSearch, InputGestureText = Strings.ShortcutTerminalSearch, Visibility = ssh };
        search.Click += (_, _) => SshViewOf(tab)?.ShowSearch();
        var save = new MenuItem { Header = Strings.MenuTerminalSave, InputGestureText = Strings.ShortcutTerminalSave, Visibility = ssh, ToolTip = Strings.MenuTerminalSaveTip };
        save.Click += (_, _) => SshViewOf(tab)?.SaveContent();
        var closeOthers = new MenuItem { Header = Strings.MenuTabCloseOthers };
        closeOthers.Click += async (_, _) => await CloseOtherTabsAsync(tab);
        var menu = new ContextMenu
        {
            Items =
            {
                actions.Reconnect, actions.Duplicate, actions.Detach, actions.Parallel, actions.AddSaved, new Separator { Visibility = ssh },
                search, save,
                new Separator(), actions.Close, closeOthers,
            },
        };
        menu.Opened += (_, _) =>
        {
            actions.Refresh();
            closeOthers.IsEnabled = SessionTabs().Any(t => t != tab);
        };
        return menu;
    }

    /// <summary>
    /// Actions de la session à la fin du menu du clic droit dans son terminal, qu'il soit dans l'onglet, dans une fenêtre
    /// séparée ou dans la vue parallèle : leurs questions s'affichent dans la fenêtre du terminal.
    /// </summary>
    private void AddTerminalSessionItems(ItemCollection items, TabItem tab, Func<Task>? duplicate)
    {
        var actions = CreateSessionActions(tab, duplicate, () => SshViewOf(tab) is { } view ? Window.GetWindow(view) : null);
        actions.Refresh();
        // Déjà dans une fenêtre séparée ou dans la vue parallèle : rien à détacher depuis le terminal.
        actions.Detach.Visibility = actions.Detach.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new object[] { actions.Reconnect, actions.Duplicate, actions.Detach, actions.Parallel, actions.AddSaved, new Separator(), actions.Close })
        {
            items.Add(item);
        }
    }

    /// <summary>
    /// « Ajouter à Mes serveurs » d'une session CyberArk : fenêtre du dossier et de l'étiquette, ou grisé si ce compte et
    /// cette machine y sont déjà ; masqué pour une connexion directe.
    /// </summary>
    private void RefreshAddSaved(MenuItem item, RemoteSession? session)
    {
        if (IsOffline || session is not { Account: { } account, Request: { } request })
        {
            item.Visibility = Visibility.Collapsed;
            return;
        }

        bool saved = SessionLibrary.FindSession(_settings, account, PvwaHost, request.RemoteMachine) is not null;
        item.Visibility = Visibility.Visible;
        item.IsEnabled = !saved;
        item.ToolTip = saved ? Strings.ToolAlreadyInMyServers : null;
    }

    /// <summary>Actions communes au menu de l'en-tête d'un onglet et au menu du clic droit dans son terminal.</summary>
    private sealed record SessionActions(MenuItem Reconnect, MenuItem Duplicate, MenuItem Detach, MenuItem Parallel, MenuItem AddSaved,
        MenuItem Close, Action Refresh);

    /// <param name="owner">Fenêtre où afficher les questions (confirmation de reconnexion, transferts en cours…).</param>
    private SessionActions CreateSessionActions(TabItem tab, Func<Task>? duplicate, Func<Window?> owner)
    {
        static Image MenuIcon(object source) => new() { Source = (System.Windows.Media.ImageSource)source, Width = 16, Height = 16 };

        var ssh = tab.Tag is SshSession ? Visibility.Visible : Visibility.Collapsed;
        var reconnect = new MenuItem { Header = Strings.MenuTabReconnect, Icon = MenuIcon(FindResource("IconRefresh")) };
        reconnect.Click += async (_, _) => await ReconnectTabAsync(tab, owner());
        var copy = new MenuItem
        {
            Header = Strings.MenuTabDuplicate,
            ToolTip = Strings.MenuTabDuplicateTip,
            Icon = MenuIcon(FindResource("IconConnect")),
            IsEnabled = duplicate is not null,
        };
        copy.Click += async (_, _) =>
        {
            if (duplicate is not null)
            {
                await duplicate();
            }
        };
        // Fenêtre séparée et vue parallèle : terminal SSH seulement (voir MainWindow.Detach.cs et MainWindow.Parallel.cs).
        var detach = new MenuItem { Header = Strings.MenuTabDetach, Visibility = ssh };
        detach.Click += (_, _) => DetachTab(tab);
        var parallel = new MenuItem { Header = Strings.MenuTabAddParallel, Icon = MenuIcon(FindResource("IconParallel")), Visibility = ssh };
        parallel.Click += (_, _) =>
        {
            if (tab.Tag is SshSession session)
            {
                ToggleParallel(session);
            }
        };
        // Session CyberArk : « Ajouter à Mes serveurs » avec son type et sa machine cible (fenêtre du dossier et de l'étiquette).
        var addSaved = new MenuItem { Header = Strings.MenuAddToMyServers, Icon = MenuIcon(FindResource("IconBookmark")), Visibility = Visibility.Collapsed };
        ToolTipService.SetShowOnDisabled(addSaved, true);
        addSaved.Click += (_, _) =>
        {
            if (!IsOffline && tab.Tag is RemoteSession { Account: { } account, Request: { } request }
                && SessionLibrary.FindSession(_settings, account, PvwaHost, request.RemoteMachine) is null)
            {
                AddToCurrent(account, request);
            }
        };
        var close = new MenuItem { Header = Strings.MenuTabClose, Icon = MenuIcon(FindResource("IconClose")), InputGestureText = Strings.ShortcutTabClose };
        close.Click += (_, _) => CloseSessionTab(tab, owner());
        return new SessionActions(reconnect, copy, detach, parallel, addSaved, close, () =>
        {
            detach.IsEnabled = tab.Content is SshSessionView;
            bool inParallel = tab.Tag is SshSession session && _parallel?.Contains(session) == true;
            parallel.Header = inParallel ? Strings.MenuTabRemoveParallel : Strings.MenuTabAddParallel;
            RefreshAddSaved(addSaved, tab.Tag as RemoteSession);
        });
    }

    /// <summary>Onglets de session (SSH, fichiers seuls, Bureau à distance, VNC), dans l'ordre affiché.</summary>
    private IEnumerable<TabItem> SessionTabs() => MainTabs.Items.OfType<TabItem>().Where(t => t.Tag is RemoteSession or RdpSession or VncSession);

    /// <summary>Nom de la session d'un onglet (« utilisateur@serveur »).</summary>
    private static string TabLabel(TabItem tab) => tab.Tag switch
    {
        RemoteSession session => session.Label,
        RdpSession rdp => rdp.Label,
        VncSession vnc => vnc.Label,
        _ => "",
    };

    /// <param name="owner">Fenêtre des questions (celle du terminal détaché) ; par défaut la fenêtre principale.</param>
    private void CloseSessionTab(TabItem tab, Window? owner = null)
    {
        switch (tab.Tag)
        {
            case RemoteSession:
                CloseRemoteTab(tab, owner);
                break;
            case RdpSession:
                _ = CloseRdpTabAsync(tab);
                break;
            case VncSession:
                CloseVncTab(tab, owner);
                break;
        }
    }

    /// <summary>Nouvelle connexion dans le même onglet (nouvelle demande au PVWA) ; confirmation si la session est ouverte.</summary>
    /// <param name="owner">Fenêtre de la question (celle du terminal détaché) ; par défaut la fenêtre principale.</param>
    private async Task ReconnectTabAsync(TabItem tab, Window? owner = null)
    {
        owner ??= this;
        if (ReferenceEquals(owner, this))
        {
            // Session de la vue parallèle : c'est là que son terminal est affiché.
            MainTabs.SelectedItem = tab.Tag is SshSession inView && _parallel?.Contains(inView) == true ? _parallelTab : tab;
        }

        bool Confirm(string label) => ConfirmDialog.Confirm(owner, new ConfirmRequest
        {
            Title = Strings.TabReconnectAction,
            Heading = Text.Format(Strings.TabReconnectHeading, label),
            Message = Strings.TabReconnectMessage,
            Actions = [Strings.TabReconnectAction],
        });

        switch (tab.Tag)
        {
            case SshSession ssh when SshViewOf(tab) is { } view:
                if (ssh.State != RemoteSessionState.Connected || Confirm(ssh.Label))
                {
                    await view.ConnectAsync();
                }

                break;
            case FilesSession files when tab.Content is FilesSessionView view:
                if (files.State != RemoteSessionState.Connected || Confirm(files.Label))
                {
                    await view.ConnectAsync();
                }

                break;
            case RdpSession rdp:
                if (!rdp.IsConnected || Confirm(rdp.Label))
                {
                    await rdp.ReconnectAsync();
                }

                break;
            case VncSession vnc:
                if (!vnc.IsConnected || Confirm(vnc.Label))
                {
                    await vnc.ConnectAsync();
                }

                break;
        }
    }

    /// <summary>Ferme les autres onglets de session, après une seule confirmation.</summary>
    private async Task CloseOtherTabsAsync(TabItem keep)
    {
        var others = SessionTabs().Where(t => t != keep).ToList();
        if (others.Count == 0
            || !ConfirmDialog.Confirm(this, new ConfirmRequest
            {
                Title = Strings.TabCloseOthersAction,
                Heading = Strings.TabCloseOthersHeading,
                Message = Strings.TabCloseOthersMessage,
                Items = others.Select(TabLabel).ToList(),
                Actions = [Strings.TabCloseOthersAction],
            }))
        {
            return;
        }

        MainTabs.SelectedItem = keep;
        var closing = new List<Task>();
        foreach (var tab in others)
        {
            switch (tab.Tag)
            {
                case RemoteSession:
                    // Seules questions possibles : fichiers modifiés pas encore renvoyés, transferts en cours.
                    CloseRemoteTab(tab, confirmSession: false);
                    break;
                case RdpSession rdp:
                    closing.Add(RemoveRdpTabAsync(rdp));
                    break;
                case VncSession:
                    RemoveVncTab(tab);
                    break;
            }
        }

        await Task.WhenAll(closing);
        MainTabs.SelectedItem = keep;
    }

    /// <summary>
    /// Ferme un onglet terminal ou fichiers seuls : fichiers modifiés et transferts en cours d'abord, sinon confirmation
    /// pour un terminal connecté (<paramref name="confirmSession"/> faux : déjà confirmé, « Fermer les autres »).
    /// </summary>
    private void CloseRemoteTab(TabItem tab, Window? owner = null, bool confirmSession = true)
    {
        if (tab.Tag is not RemoteSession session)
        {
            return;
        }

        owner ??= this;
        bool asked = false;
        if (session.Editor is { } editor)
        {
            var unsent = RemoteEditor.UnsentFiles([editor]);
            asked = unsent.Count > 0;
            if (!RemoteEditor.ConfirmClose(owner, unsent))
            {
                return;
            }
        }

        asked |= FilesPanel.ActiveTransfers(session) > 0;
        if (!FilesPanel.ConfirmCancelTransfers(owner, session))
        {
            return;
        }

        if (confirmSession && !asked && session is SshSession { State: RemoteSessionState.Connected }
            && !ConfirmCloseSession(owner, session.Label, Strings.SessionCloseSsh))
        {
            return;
        }

        if (session is SshSession ssh)
        {
            CloseDetachedWindow(ssh);
            DropFromParallel(ssh);
        }

        FilesPanel.ReleaseTails(session);
        MainTabs.Items.Remove(tab);
        _remoteSessions.Remove(session);
        _ = DisposeAfterTransfersAsync(session);
        MainTabs.SelectedItem ??= HomeTab;
        SessionStatus(session.Label, Text.Format(session is SshSession ? Strings.SshClosed : Strings.FilesClosed, session.Label));
    }

    /// <summary>Annule les transferts de la session, laisse le fichier interrompu être supprimé, puis ferme ses connexions.</summary>
    private async Task DisposeAfterTransfersAsync(RemoteSession session)
    {
        try
        {
            await FilesPanel.CancelTransfersAsync(session);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Collage de plusieurs lignes dans un terminal dont le shell les exécuterait une à une (option des Paramètres,
    /// activée par défaut) : aperçu des lignes, « Annuler » par défaut, et « Ne plus avertir ».
    /// </summary>
    private bool ConfirmMultiLinePaste(SshSessionView view, TerminalInput input)
    {
        if (!_settings.ConfirmMultiLinePaste)
        {
            return true;
        }

        var lines = SshSessionView.PastedLines(input.Text);
        int choice = ConfirmDialog.Ask(Window.GetWindow(view) ?? this, new ConfirmRequest
        {
            Title = Strings.PasteAction,
            Heading = Text.Format(Strings.PasteHeading, lines.Count),
            Subject = view.Session.Label,
            Message = Strings.PasteMessage,
            Items = lines.Take(100).ToList(),
            Kind = ConfirmKind.Warning,
            Actions = [Strings.PasteAction],
            DontAskAgain = Strings.PasteDontAsk,
        }, out bool dontAsk);
        if (choice != 0)
        {
            return false;
        }

        if (dontAsk)
        {
            _settings.ConfirmMultiLinePaste = false;
            SaveSettings();
        }

        return true;
    }

    /// <summary>
    /// Fermeture d'une session connectée (option des Paramètres, activée par défaut) : « Annuler » par défaut, avec
    /// « Ne plus demander ». La même règle vaut pour SSH, Bureau à distance et VNC.
    /// </summary>
    private bool ConfirmCloseSession(Window owner, string label, string consequence)
    {
        if (!_settings.ConfirmCloseSession)
        {
            return true;
        }

        int choice = ConfirmDialog.Ask(owner, new ConfirmRequest
        {
            Title = Strings.SessionCloseAction,
            Heading = Text.Format(Strings.SessionCloseHeading, label),
            Message = consequence,
            Actions = [Strings.SessionCloseAction],
            DontAskAgain = Strings.SessionCloseDontAsk,
        }, out bool dontAsk);
        if (choice != 0)
        {
            return false;
        }

        if (dontAsk)
        {
            _settings.ConfirmCloseSession = false;
            SaveSettings();
            SetStatus(Strings.SessionCloseDontAskDone);
        }

        return true;
    }

    /// <summary>
    /// Déconnexion ou fermeture de l'application : un seul récapitulatif de ce qui sera fermé (sessions, transferts,
    /// fichiers modifiés). Toujours demandé pour les transferts, les fichiers modifiés et le Bureau à distance ; pour
    /// les terminaux SSH et VNC, selon l'option « Confirmer avant de fermer une session ».
    /// </summary>
    private bool ConfirmCloseAll()
    {
        var unsent = RemoteEditor.UnsentFiles(_remoteSessions.Select(s => s.Editor).OfType<RemoteEditor>());
        int transfers = FilesPanel.ActiveTransfers(null);
        int ssh = _remoteSessions.OfType<SshSession>().Count(s => s.State == RemoteSessionState.Connected);
        int files = _remoteSessions.OfType<FilesSession>().Count(s => s.State == RemoteSessionState.Connected);
        int rdp = _rdpViews.Count(v => v.Session.HasControl);
        int vnc = MainTabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<VncSession>().Count(v => v.IsConnected);
        bool always = unsent.Count > 0 || transfers > 0 || rdp > 0;
        if (!always && !(_settings.ConfirmCloseSession && ssh + vnc > 0))
        {
            return true;
        }

        var bullets = new List<string>();
        void Add(int count, string format)
        {
            if (count > 0)
            {
                bullets.Add(Text.Format(format, count));
            }
        }

        Add(ssh, Strings.CloseAllSsh);
        Add(files, Strings.CloseAllFiles);
        Add(rdp, Strings.CloseAllRdp);
        Add(vnc, Strings.CloseAllVnc);
        Add(transfers, Strings.CloseAllTransfers);
        Add(unsent.Count, Strings.CloseAllEdited);
        return ConfirmDialog.Confirm(this, new ConfirmRequest
        {
            Title = "ZillaTerm",
            Heading = !LogoutRequested ? Strings.CloseAllExitHeading : _client is null ? Strings.CloseAllLeaveEmergencyHeading : Strings.CloseAllLogoutHeading,
            Bullets = bullets,
            Items = unsent,
            Kind = unsent.Count > 0 || transfers > 0 ? ConfirmKind.Warning : ConfirmKind.Question,
            Actions = [!LogoutRequested ? Strings.ActionQuit : _client is null ? Strings.ToolLeaveEmergency : Strings.ActionSignOut],
            DangerAction = unsent.Count > 0 || transfers > 0 ? 0 : -1,
        });
    }

    private void CloseAllSshSessions()
    {
        CloseParallel();
        CloseAllDetachedWindows();
        FilesPanel.CloseTailWindows();
        foreach (var session in _remoteSessions)
        {
            session.Dispose();
        }

        _remoteSessions.Clear();
    }

    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged remonte aussi depuis les listes contenues dans les onglets.
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        var tab = MainTabs.SelectedItem as TabItem;
        BringTabIntoView(tab);
        // Vue parallèle : l'onglet Fichiers suit la session où l'on travaille.
        FilesPanel.Attach(tab?.Tag as RemoteSession ?? (tab?.Tag as ParallelView)?.ActiveSession);
        ShowRdpView(tab);
        if (tab?.Content is SshSessionView view)
        {
            view.FocusTerminal();
        }
        else if (tab?.Content is ParallelView parallel)
        {
            parallel.FocusActive();
        }
    }

    /// <summary>Clé MFA du PVWA pour une connexion au PSMP, demandée sur le fil de l'interface (appel de n'importe quel fil).</summary>
    private Task<PrivateKeyFile?> PsmpKeyAsync(CancellationToken ct) => Dispatcher.InvokeAsync(() => GetPsmpKeyAsync(ct)).Task.Unwrap();

    /// <summary>
    /// Clé « MFA caching » du PVWA : évite de ressaisir mot de passe et MFA à chaque connexion au PSMP.
    /// Si le PVWA ne la fournit pas, le PSMP posera ses questions (mot de passe, code) dans une fenêtre.
    /// </summary>
    private async Task<PrivateKeyFile?> GetPsmpKeyAsync(CancellationToken ct)
    {
        if (_mfaKey is null || _mfaKey.IsExpired(DateTimeOffset.Now))
        {
            _mfaKey = null;
            if (DateTime.UtcNow < _mfaRetryAfter || IsOffline)
            {
                return null;
            }

            // Plusieurs onglets ouverts ensemble (dossier, vue parallèle) : une seule demande au PVWA. Elle s'oublie
            // d'elle-même une fois finie, même si l'onglet qui l'a lancée a été fermé entre-temps.
            var fetch = _mfaFetch ??= FetchMfaKeyAsync();
            if (fetch.IsCompleted && ReferenceEquals(_mfaFetch, fetch))
            {
                _mfaFetch = null;
            }

            var result = await fetch.WaitAsync(ct);
            _mfaKey = result.Key;
            if (_mfaKey is null)
            {
                // Le PVWA ne fournit pas de clé : on ne redemande pas avant 15 minutes. Reconnexion refusée (« Plus
                // tard ») : rien de tel, la prochaine connexion redemandera.
                if (result.NotProvided)
                {
                    _mfaRetryAfter = DateTime.UtcNow.AddMinutes(15);
                }

                return null;
            }
        }

        // Copie de la clé lue par SSH.NET puis effacée : elle ne traîne pas en mémoire après la connexion.
        var bytes = Encoding.UTF8.GetBytes(_mfaKey.PrivateKey);
        try
        {
            return new PrivateKeyFile(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ArgumentException or InvalidOperationException or FormatException)
        {
            _mfaKey = null;
            _mfaRetryAfter = DateTime.UtcNow.AddMinutes(15);
            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Demande de la clé au PVWA ; sans clé s'il ne la fournit pas ou ne répond pas. Session PVWA expirée : la
    /// reconnexion est proposée, puis la demande refaite ; sans reconnexion, le PSMP demandera mot de passe et code.
    /// </summary>
    private async Task<MfaKeyFetch> FetchMfaKeyAsync()
    {
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var key = await Client.GetMfaCachingSshKeyAsync(_lifetime.Token);
                    _mfaKeyIssued |= key is not null;
                    return new MfaKeyFetch(key, NotProvided: key is null);
                }
                catch (PvwaException ex) when (ex.IsUnauthorized && attempt == 0)
                {
                    if (!Reconnect(userAction: true))
                    {
                        return new MfaKeyFetch(null, NotProvided: false);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or PvwaException
                                               || (ex is TaskCanceledException && !_lifetime.IsCancellationRequested))
                {
                    return new MfaKeyFetch(null, NotProvided: true);
                }
            }
        }
        finally
        {
            _mfaFetch = null;
        }
    }

    /// <param name="NotProvided">Le PVWA n'a pas fourni de clé (et non : session expirée, reconnexion refusée).</param>
    private sealed record MfaKeyFetch(MfaSshKey? Key, bool NotProvided);

    /// <summary>Session PVWA rouverte : la clé MFA peut de nouveau être demandée tout de suite.</summary>
    private void ForgetMfaRetryDelay() => _mfaRetryAfter = default;
}
