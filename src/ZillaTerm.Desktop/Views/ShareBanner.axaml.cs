using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>Ce que font les boutons du bandeau d'une session partagée (fenêtre principale).</summary>
internal sealed record ShareCommands(
    Action<ActiveShare, ShareWatcher> Answer,
    Action<ActiveShare, ShareWatcher> Refuse,
    Action<ActiveShare, ShareWatcher> GiveControl,
    Action<ActiveShare, ShareWatcher> IgnoreControl,
    Action<ActiveShare> TakeControl,
    Action<ActiveShare?> Manage,
    Action<ActiveShare> Stop);

/// <summary>
/// Bandeau d'une session partagée, au-dessus de la session (terminal, Bureau à distance, VNC) : qui regarde, qui a la
/// main, jusqu'à quand ; les demandes en attente (regarder, avoir la main) ; reprendre la main, gérer, arrêter.
/// </summary>
public partial class ShareBanner : UserControl
{
    private ActiveShare? _share;
    private ShareCommands? _commands;

    public ShareBanner()
    {
        InitializeComponent();
    }

    /// <summary>Montre le partage de la session (fil de l'interface) ; le bandeau disparaît à son arrêt.</summary>
    internal void Attach(ActiveShare share, ShareCommands commands)
    {
        Detach();
        _share = share;
        _commands = commands;
        share.Changed += Refresh;
        share.JoinRequested += OnRequest;
        share.WatcherLeft += OnLeft;
        share.Stopped += OnStopped;
        IsVisible = true;
        Refresh();
    }

    private void Detach()
    {
        if (_share is { } share)
        {
            share.Changed -= Refresh;
            share.JoinRequested -= OnRequest;
            share.WatcherLeft -= OnLeft;
            share.Stopped -= OnStopped;
        }

        _share = null;
        IsVisible = false;
        RequestsPanel.Children.Clear();
    }

    private void OnRequest(ShareWatcher watcher) => Refresh();

    private void OnLeft(ShareWatcher watcher, ShareEndReason reason) => Refresh();

    private void OnStopped(ShareEndReason reason) => Detach();

    private void Refresh()
    {
        if (_share is not { } share || _commands is not { } commands)
        {
            return;
        }

        var watching = share.Watching;
        var until = share.Share.Info.EndsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        var summary = watching.Count == 0
            ? Text.Format(Strings.ShareBannerNobody, until)
            : Text.Format(Strings.ShareBannerWatching, watching.Count, string.Join(", ", watching.Select(w => w.Join.Name)), until);
        var controller = share.Share.Controller;
        if (controller is not null)
        {
            summary += " " + Text.Format(Strings.ShareBannerController, controller.Join.Name);
        }

        SummaryText.Text = summary;
        TakeBackButton.IsVisible = controller is not null;

        RequestsPanel.Children.Clear();
        foreach (var watcher in share.Pending)
        {
            AddRequest(Text.Format(Strings.ShareBannerJoinRequest, watcher.Join.Name, watcher.Join.User, watcher.Join.Machine),
                (Strings.ShareAnswer, Strings.ShareAnswerTip, () => commands.Answer(share, watcher)),
                (Strings.ShareRefuse, null, () => commands.Refuse(share, watcher)));
        }

        foreach (var watcher in share.ControlAsked.Where(w => w.State == ShareWatcherState.Watching).ToList())
        {
            AddRequest(Text.Format(Strings.ShareBannerControlRequest, watcher.Join.Name),
                (Strings.ShareGiveControl, Strings.ShareGiveControlTip, () => commands.GiveControl(share, watcher)),
                (Strings.ShareIgnore, null, () => commands.IgnoreControl(share, watcher)));
        }
    }

    private void AddRequest(string text, params (string Label, string? Tip, Action Click)[] buttons)
    {
        var line = new DockPanel { Margin = new Thickness(17, 4, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right);
        foreach (var (label, tip, click) in buttons)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => click();
            actions.Children.Add(button);
        }

        line.Children.Add(actions);
        line.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        RequestsPanel.Children.Add(line);
    }

    private void OnTakeBack(object? sender, RoutedEventArgs e)
    {
        if (_share is { } share)
        {
            _commands?.TakeControl(share);
        }
    }

    private void OnManage(object? sender, RoutedEventArgs e) => _commands?.Manage(_share);

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        if (_share is { } share)
        {
            _commands?.Stop(share);
        }
    }
}
