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
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core;
using ZillaTerm.Core.Interventions;
using ZillaTerm.Core.Sharing;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

/// <summary>
/// Partage de sessions avec un autre ZillaTerm : partager une session (terminal SSH, Bureau à distance dans l'onglet, VNC)
/// avec une invitation, accepter chaque personne après comparaison d'un code, donner et reprendre la main (terminal),
/// et regarder les sessions des autres dans des onglets. Tout passe au journal d'une intervention en cours.
/// </summary>
public partial class MainWindow
{
    private SharesWindow? _sharesWindow;
    private ShareCommands? _shareCommands;

    private static ShareService Sharing => ShareService.Current;

    private ShareCommands Commands => _shareCommands ??= new ShareCommands(AnswerJoin, RefuseJoin, GiveControl,
        (share, watcher) => share.IgnoreControlRequest(watcher), TakeControl, ShowShares, StopShare);

    private void InitSharing()
    {
        Sharing.Configure(_settings, Dispatcher.UIThread);
        Sharing.Changed += UpdateShareIndicator;
        Closed += (_, _) =>
        {
            Sharing.Changed -= UpdateShareIndicator;
            _sharesWindow?.Close();
        };
        UpdateShareIndicator();
    }

    /// <summary>Fermeture de la fenêtre (déconnexion, sortie, fin de la session Windows) : plus rien n'est partagé ni regardé.</summary>
    private static void EndSharing(bool logout) => Sharing.EndAll(logout ? ShareEndReason.Stopped : ShareEndReason.ApplicationClosed);

    /// <summary>Réglages enregistrés : options des prochains partages ; partage désactivé, ceux en cours s'arrêtent.</summary>
    private void ApplySharingSettings()
    {
        Sharing.Configure(_settings, Dispatcher.UIThread);
        if (!_settings.SessionSharing)
        {
            Sharing.Host.StopAll(ShareEndReason.Stopped);
        }

        _sharesWindow?.SetSharingEnabled(_settings.SessionSharing);
    }

    // ===================== Barre d'état et barre d'outils =====================

    private void UpdateShareIndicator()
    {
        int shares = Sharing.Shares.Count, watched = Sharing.Watched.Count;
        int pending = Sharing.Shares.Sum(s => s.Pending.Count + s.ControlAsked.Count);
        var parts = new List<string>();
        if (shares > 0)
        {
            parts.Add(Text.Format(Strings.ShareIndicatorShared, shares, Sharing.Shares.Sum(s => s.Watching.Count)));
        }

        if (watched > 0)
        {
            parts.Add(Text.Format(Strings.ShareIndicatorWatched, watched));
        }

        if (pending > 0)
        {
            parts.Add(Text.Format(Strings.ShareIndicatorPending, pending));
        }

        ShareIndicator.Text = "● " + string.Join(" · ", parts);
        ShareIndicator.IsVisible = parts.Count > 0;
        ShareBadge.IsVisible = pending > 0;
    }

    private void OnShareIndicatorClick(object? sender, PointerReleasedEventArgs e) => ShowShares(null);

    private void OnSharing(object? sender, RoutedEventArgs e) => ShowShares(null);

    /// <summary>Fenêtre « Partage de sessions » (une seule), sur le partage donné.</summary>
    private void ShowShares(ActiveShare? share)
    {
        if (_sharesWindow is null)
        {
            _sharesWindow = new SharesWindow(new SharesWindowCommands(Commands, ShowInvitation, JoinShare, ShowWatched, LeaveWatched),
                _settings.SessionSharing);
            _sharesWindow.Closed += (_, _) => _sharesWindow = null;
            _sharesWindow.Show(this);
        }
        else
        {
            _sharesWindow.Activate();
        }

        _sharesWindow.Select(share);
    }

    // ===================== Partager une session =====================

    /// <summary>Session d'un onglet qui peut être partagée maintenant (ouverte ; Bureau à distance dans l'onglet).</summary>
    private bool IsShareable(TabItem tab) => tab.Tag switch
    {
        SshSession ssh => ssh.State == RemoteSessionState.Connected,
        RdpSession rdp => rdp.IsConnected && RdpViewOf(rdp) is not null,
        VncSession vnc => vnc.IsConnected,
        _ => false,
    };

    private RdpSessionView? RdpViewOf(RdpSession session) => _rdpViews.FirstOrDefault(v => v.Session == session);

    /// <summary>Bandeau du partage dans la vue de la session (où qu'elle soit : onglet, fenêtre séparée, vue parallèle).</summary>
    private ShareBanner? ShareBarOf(TabItem tab) => tab.Tag switch
    {
        SshSession => SshViewOf(tab)?.ShareBar,
        RdpSession rdp => RdpViewOf(rdp)?.ShareBar,
        VncSession => (tab.Content as VncSessionView)?.ShareBar,
        _ => null,
    };

    /// <summary>« Partager la session… » (menu de l'onglet) ; une session déjà partagée ouvre la gestion de son partage.</summary>
    private async void ShareTab(TabItem tab, Window? owner)
    {
        owner ??= this;
        if (tab.Tag is { } shared && Sharing.ShareOf(shared) is { } existing)
        {
            ShowShares(existing);
            return;
        }

        if (!_settings.SessionSharing)
        {
            if (await ConfirmDialog.AskAsync(owner, new ConfirmRequest
                {
                    Title = Strings.ShareTitle,
                    Heading = Strings.ShareDisabledHeading,
                    Message = Strings.ShareDisabledMessage,
                    Kind = ConfirmKind.Info,
                    Actions = [Strings.ShareOpenSettings],
                    CancelLabel = Strings.CloseButton,
                }) == 0)
            {
                OpenSettings(sharing: true);
            }

            return;
        }

        if (!IsShareable(tab))
        {
            SetStatus(Strings.ShareNotConnected, isError: true);
            return;
        }

        var kind = tab.Tag is SshSession ? ShareKind.Terminal : ShareKind.Screen;
        var label = TabLabel(tab);
        await new ShareSessionDialog(label, kind, (duration, control) => StartShare(tab, kind, label, duration, control)).ShowDialogAsync(owner);
    }

    private ActiveShare StartShare(TabItem tab, ShareKind kind, string label, TimeSpan duration, bool control)
    {
        // Réglages relus à chaque partage (port, dossier relais).
        Sharing.Configure(_settings, Dispatcher.UIThread);
        var session = tab.Tag!;
        var info = new ShareInfo(kind, label, SharedServerList.Who(_sessionUser), Environment.MachineName,
            control && kind == ShareKind.Terminal, DateTime.UtcNow + duration);
        var active = Sharing.Start(info, share => session switch
        {
            SshSession ssh => new TerminalShareSource(share, ssh, Dispatcher.UIThread),
            RdpSession rdp when RdpViewOf(rdp) is { } view => new ScreenShareSource(share, view.CaptureForShare),
            VncSession vnc => new ScreenShareSource(share, buffer => CaptureVnc(vnc, buffer)),
            _ => throw new InvalidOperationException("Session non partageable."),
        }, session);
        FollowShare(active, tab);
        InterventionRecord(InterventionKind.Action, label, Text.Format(Strings.InterventionShareStarted, ShareSessionDialog.DurationText(duration),
            info.ControlAllowed ? Strings.InterventionShareControlAllowed : Strings.InterventionShareReadOnly,
            ShareSessionDialog.RouteDescription(active.Share.Invitation).Replace(Environment.NewLine, " ; ", StringComparison.Ordinal)));
        SessionStatus(label, Text.Format(Strings.ShareStartedStatus, label));
        return active;
    }

    /// <summary>Image de l'écran VNC (copie sous le verrou du décodage) ; null hors connexion.</summary>
    private static ScreenPixels? CaptureVnc(VncSession session, byte[]? buffer)
    {
        if (!session.IsConnected || session.Client is not { } client)
        {
            return null;
        }

        var framebuffer = client.Framebuffer;
        lock (framebuffer.Sync)
        {
            int length = framebuffer.Stride * framebuffer.Height;
            if (length == 0)
            {
                return null;
            }

            if (buffer is null || buffer.Length < length)
            {
                buffer = new byte[length];
            }

            Buffer.BlockCopy(framebuffer.Pixels, 0, buffer, 0, length);
            return new ScreenPixels(buffer, framebuffer.Width, framebuffer.Height);
        }
    }

    /// <summary>Bandeau, demandes, départs et fin d'un partage ; il s'arrête avec sa session (Bureau à distance, VNC).</summary>
    private void FollowShare(ActiveShare active, TabItem tab)
    {
        var label = active.Label;
        ShareBarOf(tab)?.Attach(active, Commands);
        Action? unfollow = null;
        switch (tab.Tag)
        {
            case RdpSession rdp:
                void RdpEnded()
                {
                    if (rdp.State is RdpSessionState.Ended or RdpSessionState.Failed)
                    {
                        active.Share.Stop(ShareEndReason.SessionClosed);
                    }
                }

                rdp.StateChanged += RdpEnded;
                unfollow = () => rdp.StateChanged -= RdpEnded;
                break;
            case VncSession vnc:
                // Évènement levé hors du fil de l'interface ; l'arrêt du partage s'y fait sans risque.
                void VncEnded()
                {
                    if (vnc.State is VncSessionState.Closed or VncSessionState.Failed)
                    {
                        active.Share.Stop(ShareEndReason.SessionClosed);
                    }
                }

                vnc.StateChanged += VncEnded;
                unfollow = () => vnc.StateChanged -= VncEnded;
                break;
        }

        active.JoinRequested += watcher =>
        {
            InterventionRecord(InterventionKind.Action, label, Text.Format(Strings.InterventionShareRequest, watcher.Join.Name, watcher.Join.User, watcher.Join.Machine,
                watcher.IsRelay ? Strings.WatchRouteRelay : watcher.Route));
            SessionStatus(label, Text.Format(Strings.ShareRequestStatus, watcher.Join.Name, label));
            TailAlerts.Flash(WindowOf(tab));
        };
        active.ControlRequested += watcher =>
        {
            SessionStatus(label, Text.Format(Strings.ShareControlRequestStatus, watcher.Join.Name, label));
            TailAlerts.Flash(WindowOf(tab));
        };
        active.WatcherLeft += (watcher, reason) =>
        {
            var text = reason switch
            {
                ShareEndReason.Refused => Strings.InterventionShareRefused,
                ShareEndReason.Excluded => Strings.InterventionShareExcluded,
                ShareEndReason.Full => Strings.InterventionShareFull,
                _ => Strings.InterventionShareLeft,
            };
            InterventionRecord(InterventionKind.Action, label, Text.Format(text, watcher.Join.Name));
        };
        active.Stopped += reason =>
        {
            unfollow?.Invoke();
            InterventionRecord(InterventionKind.Action, label, Text.Format(Strings.InterventionShareStopped, EndText(reason)));
            SessionStatus(label, Text.Format(Strings.ShareStoppedStatus, label, EndText(reason)));
        };
    }

    /// <summary>Raison de la fin d'un partage, vue de celui qui partage.</summary>
    private static string EndText(ShareEndReason reason) => reason switch
    {
        ShareEndReason.Expired => Strings.ShareEndExpired,
        ShareEndReason.SessionClosed => Strings.ShareEndSessionClosed,
        ShareEndReason.Locked => Strings.ShareEndLocked,
        ShareEndReason.TooManyAttempts => Strings.ShareEndTooMany,
        ShareEndReason.ApplicationClosed => Strings.ShareEndClosed,
        _ => Strings.ShareEndStopped,
    };

    /// <summary>Fenêtre où poser les questions d'un partage : celle de sa session (fenêtre séparée) ou la principale.</summary>
    private Window OwnerOf(ActiveShare share) =>
        SessionTabs().FirstOrDefault(t => ReferenceEquals(t.Tag, share.Session)) is { } tab ? WindowOf(tab) : this;

    /// <summary>Fenêtre où la session d'un onglet est affichée (fenêtre séparée) ; la principale sinon.</summary>
    private Window WindowOf(TabItem tab) => ShareBarOf(tab) is { } bar && TopLevel.GetTopLevel(bar) is Window window ? window : this;

    /// <summary>
    /// Demande pour regarder : qui, d'où, et le code à comparer avec celui que la personne voit ; « Refuser » par défaut,
    /// et l'accord seulement une fois la case « code vérifié » cochée.
    /// </summary>
    private async void AnswerJoin(ActiveShare share, ShareWatcher watcher)
    {
        if (watcher.State != ShareWatcherState.Pending)
        {
            return;
        }

        var route = watcher.IsRelay ? Strings.ShareFromRelay : Text.Format(Strings.ShareFromAddress, watcher.Route);
        int choice = await ConfirmDialog.AskAsync(OwnerOf(share), new ConfirmRequest
        {
            Title = Strings.ShareAnswerTitle,
            Heading = Text.Format(Strings.ShareAnswerHeading, watcher.Join.Name),
            Subject = share.Label,
            Message = Text.Format(Strings.ShareAnswerMessage, watcher.Join.Name, watcher.Join.User, watcher.Join.Machine, route),
            Codes = [(Strings.ShareAnswerCode, watcher.VerificationCode)],
            Bullets = [Strings.ShareAnswerSees, share.Share.Info.ControlAllowed ? Strings.ShareAnswerNoControl : Strings.ShareAnswerReadOnly],
            Acknowledge = Strings.ShareAnswerChecked,
            Kind = ConfirmKind.Warning,
            Actions = [Strings.ShareAccept],
            CancelLabel = Strings.ShareRefuse,
        });
        if (watcher.State != ShareWatcherState.Pending)
        {
            // Demande retirée (ou partage arrêté) pendant la question.
            SetStatus(Text.Format(Strings.ShareRequestWithdrawn, watcher.Join.Name));
            return;
        }

        if (choice != 0)
        {
            RefuseJoin(share, watcher);
            return;
        }

        // Sur le fil de l'interface, comme la diffusion : l'écran actuel puis tout ce qui suit, sans manque ni doublon.
        share.Share.Accept(watcher, share.Source.Initial());
        InterventionRecord(InterventionKind.Action, share.Label, Text.Format(Strings.InterventionShareAccepted, watcher.Join.Name, watcher.VerificationCode));
        SessionStatus(share.Label, Text.Format(Strings.ShareAcceptedStatus, watcher.Join.Name, share.Label));
    }

    private static void RefuseJoin(ActiveShare share, ShareWatcher watcher) => share.Share.Refuse(watcher);

    /// <summary>Donner la main (terminal) : ce que la personne tape part dans la session, sous l'identité de celui qui partage.</summary>
    private async void GiveControl(ActiveShare share, ShareWatcher watcher)
    {
        if (watcher.State != ShareWatcherState.Watching || !share.Share.Info.ControlAllowed
            || !await ConfirmDialog.ConfirmAsync(OwnerOf(share), new ConfirmRequest
            {
                Title = Strings.ShareGiveTitle,
                Heading = Text.Format(Strings.ShareGiveHeading, watcher.Join.Name),
                Subject = share.Label,
                Message = Strings.ShareGiveMessage,
                Bullets = [Strings.ShareGiveTakeBack, Strings.ShareGiveStay],
                Kind = ConfirmKind.Warning,
                Actions = [Strings.ShareGiveAction],
            }))
        {
            return;
        }

        share.Share.GiveControl(watcher);
        share.IgnoreControlRequest(watcher);
        if (watcher.HasControl)
        {
            InterventionRecord(InterventionKind.Action, share.Label, Text.Format(Strings.InterventionShareControlGiven, watcher.Join.Name));
        }
    }

    private void TakeControl(ActiveShare share)
    {
        if (share.Share.Controller is { } controller)
        {
            share.Share.TakeControl();
            InterventionRecord(InterventionKind.Action, share.Label, Text.Format(Strings.InterventionShareControlTaken, controller.Join.Name));
            SessionStatus(share.Label, Text.Format(Strings.ShareControlTakenStatus, share.Label));
        }
    }

    private static void StopShare(ActiveShare share) => share.Share.Stop(ShareEndReason.Stopped);

    /// <summary>Invitation d'un partage en cours, à renvoyer (fenêtre « Partage de sessions »).</summary>
    private async void ShowInvitation(ActiveShare share)
    {
        var dialog = new ShareSessionDialog(share.Label, share.Share.Info.Kind, (_, _) => share);
        dialog.ShowInvitation(share.Share);
        await dialog.ShowDialogAsync(_sharesWindow ?? (Window)this);
    }

    // ===================== Regarder la session d'une autre personne =====================

    /// <summary>Qui demande à regarder (déclaratif : le code de vérification fait foi).</summary>
    private ShareJoin JoinIdentity() => new(
        string.IsNullOrWhiteSpace(_sessionUser) ? Environment.UserName : _sessionUser,
        $"{Environment.UserDomainName}\\{Environment.UserName}",
        Environment.MachineName,
        UpdateChecker.CurrentVersion.ToString());

    private async void JoinShare()
    {
        var dialog = new JoinShareDialog(JoinIdentity());
        if (await dialog.ShowDialogAsync(_sharesWindow ?? (Window)this) && dialog.Joined is { } watched)
        {
            OpenWatchedTab(watched);
        }
    }

    private void OnJoinShare(object? sender, RoutedEventArgs e) => JoinShare();

    private void OpenWatchedTab(WatchedShare watched)
    {
        Sharing.AddWatched(watched);
        var info = watched.Info;
        var view = new SharedSessionView(watched) { PasteGuard = input => ConfirmWatchedPasteAsync(watched, input) };
        var tab = new TabItem { Content = view, Tag = watched };
        tab.Header = TabHeader(tab, info.Label, "IconScreenShare", null);
        view.LeaveRequested += () => CloseSessionTab(tab);
        view.ControlChanged += granted =>
        {
            InterventionRecord(InterventionKind.Action, info.Label,
                Text.Format(granted ? Strings.InterventionWatchControlGiven : Strings.InterventionWatchControlEnded, info.Sharer));
            SessionStatus(info.Label, Text.Format(granted ? Strings.WatchControlGivenStatus : Strings.WatchControlEndedStatus, info.Sharer));
            if (granted)
            {
                TailAlerts.Flash(TopLevel.GetTopLevel(view) as Window ?? this);
            }
        };
        watched.Viewer.Ended += reason => Dispatcher.UIThread.Post(() =>
        {
            InterventionRecord(InterventionKind.Action, info.Label, Text.Format(Strings.InterventionWatchEnded, SharedSessionView.EndText(reason, info.Sharer)));
            SessionStatus(info.Label, SharedSessionView.EndText(reason, info.Sharer));
        });
        InterventionRecord(InterventionKind.Action, info.Label, Text.Format(Strings.InterventionWatchStarted, info.Sharer, info.Machine,
            watched.Viewer.VerificationCode));
        MainTabs.Items.Add(tab);
        MainTabs.SelectedItem = tab;
        SessionStatus(info.Label, Text.Format(Strings.WatchStartedStatus, info.Label, info.Sharer));
    }

    private void ShowWatched(WatchedShare watched)
    {
        if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == watched) is { } tab)
        {
            MainTabs.SelectedItem = tab;
            Activate();
        }
    }

    private void LeaveWatched(WatchedShare watched)
    {
        if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == watched) is { } tab)
        {
            CloseSessionTab(tab);
        }
        else
        {
            Sharing.Leave(watched);
        }
    }

    /// <summary>Fermeture de l'onglet d'une session regardée : la personne qui partage voit le départ.</summary>
    private void CloseWatchedTab(TabItem tab, WatchedShare watched)
    {
        Sharing.Leave(watched);
        MainTabs.Items.Remove(tab);
    }

    /// <summary>Collage de plusieurs lignes avec la main, dans la session d'une autre personne : même confirmation que chez soi.</summary>
    private async Task<bool> ConfirmWatchedPasteAsync(WatchedShare watched, TerminalInput input)
    {
        if (!_settings.ConfirmMultiLinePaste)
        {
            return true;
        }

        var lines = SshSessionView.PastedLines(input.Text);
        return await ConfirmDialog.AskAsync(this, new ConfirmRequest
        {
            Title = Strings.PasteAction,
            Heading = Text.Format(Strings.PasteHeading, lines.Count),
            Subject = Text.Format(Strings.WatchPasteSubject, watched.Label, watched.Info.Sharer),
            Message = Strings.PasteMessage,
            Items = lines.Take(100).ToList(),
            Kind = ConfirmKind.Warning,
            Actions = [Strings.PasteAction],
        }) == 0;
    }
}
