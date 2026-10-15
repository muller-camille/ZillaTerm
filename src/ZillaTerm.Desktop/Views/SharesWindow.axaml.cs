using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>Ce que fait la fenêtre « Partage de sessions » (fenêtre principale).</summary>
internal sealed record SharesWindowCommands(
    ShareCommands Share,
    Action<ActiveShare> Invitation,
    Action Join,
    Action<WatchedShare> Show,
    Action<WatchedShare> Leave);

/// <summary>
/// Partage de sessions : mes sessions partagées et les personnes de chacune (répondre à une demande, donner ou
/// reprendre la main, exclure, arrêter), et les sessions des autres que je regarde (afficher, quitter). Une seule
/// fenêtre, qui suit les changements en direct.
/// </summary>
public partial class SharesWindow : Window
{
    private readonly SharesWindowCommands _commands = null!;
    private bool _refreshing;

    /// <summary>Pour le chargeur XAML d'Avalonia seulement ; la fenêtre principale ouvre l'autre constructeur.</summary>
    public SharesWindow() => InitializeComponent();

    internal SharesWindow(SharesWindowCommands commands, bool sharingEnabled)
        : this()
    {
        _commands = commands;
        DisabledText.IsVisible = !sharingEnabled;
        ShareService.Current.Changed += Refresh;
        Closed += (_, _) => ShareService.Current.Changed -= Refresh;
        Refresh();
    }

    /// <summary>Partage à montrer (bouton « Gérer » d'un bandeau).</summary>
    internal void Select(ActiveShare? share)
    {
        if (share is not null)
        {
            SharesList.SelectedItem = SharesList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == share);
        }
    }

    internal void SetSharingEnabled(bool enabled) => DisabledText.IsVisible = !enabled;

    private ActiveShare? SelectedShare => (SharesList.SelectedItem as ListBoxItem)?.Tag as ActiveShare;

    private ShareWatcher? SelectedPerson => (PeopleList.SelectedItem as ListBoxItem)?.Tag as ShareWatcher;

    private WatchedShare? SelectedWatched => (WatchedList.SelectedItem as ListBoxItem)?.Tag as WatchedShare;

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var service = ShareService.Current;
            Fill(SharesList, service.Shares, ShareText);
            if (SharesList.SelectedItem is null && SharesList.ItemCount > 0)
            {
                SharesList.SelectedIndex = 0;
            }

            NoSharesText.IsVisible = SharesList.ItemCount == 0;
            var share = SelectedShare;
            Fill(PeopleList, share is null ? [] : share.Share.Watchers.Where(w => w.State != ShareWatcherState.Closed), w => PersonText(share!, w));
            Fill(WatchedList, service.Watched, WatchedText);
            NoWatchedText.IsVisible = WatchedList.ItemCount == 0;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateButtons();
    }

    /// <summary>Remplit une liste en gardant l'élément choisi s'il y est encore.</summary>
    private static void Fill<T>(ListBox list, IEnumerable<T> items, Func<T, string> text)
        where T : class
    {
        var selected = (list.SelectedItem as ListBoxItem)?.Tag;
        list.Items.Clear();
        ListBoxItem? keep = null;
        foreach (var item in items)
        {
            var row = new ListBoxItem { Content = text(item), Tag = item };
            list.Items.Add(row);
            if (ReferenceEquals(item, selected))
            {
                keep = row;
            }
        }

        list.SelectedItem = keep;
    }

    private static string Time(DateTime utc) => utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private static string ShareText(ActiveShare share)
    {
        var info = share.Share.Info;
        var text = Text.Format(Strings.SharesShareLine, info.Label, share.Watching.Count, Time(info.EndsAt));
        int pending = share.Pending.Count;
        return pending > 0 ? text + " · " + Text.Format(Strings.SharesPendingCount, pending) : text;
    }

    private static string PersonText(ActiveShare share, ShareWatcher watcher)
    {
        var who = Text.Format(Strings.SharesPerson, watcher.Join.Name, watcher.Join.User, watcher.Join.Machine);
        var route = watcher.IsRelay ? Strings.WatchRouteRelay : watcher.Route;
        var state = watcher.State == ShareWatcherState.Pending
            ? Text.Format(Strings.SharesPersonPending, watcher.VerificationCode)
            : Text.Format(Strings.SharesPersonWatching, Time(watcher.AcceptedAt ?? watcher.RequestedAt))
              + (watcher.HasControl ? " · " + Strings.SharesPersonHasControl : "")
              + (share.ControlAsked.Contains(watcher) ? " · " + Strings.SharesPersonAsksControl : "");
        return $"{who} · {state} · {route}";
    }

    private static string WatchedText(WatchedShare watched) =>
        Text.Format(Strings.SharesWatchedLine, watched.Info.Label, watched.Info.Sharer, watched.Info.Machine, Time(watched.Info.EndsAt));

    private void UpdateButtons()
    {
        var share = SelectedShare;
        var person = SelectedPerson;
        bool control = share is not null && share.Share.Info.ControlAllowed && share.Share.Info.Kind == ShareKind.Terminal;
        InvitationButton.IsEnabled = StopButton.IsEnabled = share is not null;
        AnswerButton.IsEnabled = person is { State: ShareWatcherState.Pending };
        GiveButton.IsEnabled = control && person is { State: ShareWatcherState.Watching, HasControl: false };
        TakeButton.IsEnabled = share?.Share.Controller is not null;
        ExcludeButton.IsEnabled = person is { State: ShareWatcherState.Watching };
        ShowButton.IsEnabled = LeaveButton.IsEnabled = SelectedWatched is not null;
    }

    private void OnShareSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
        {
            Refresh();
        }
    }

    private void OnPersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
        {
            UpdateButtons();
        }
    }

    private void OnWatchedSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing)
        {
            UpdateButtons();
        }
    }

    private void OnInvitation(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share)
        {
            _commands.Invitation(share);
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share)
        {
            _commands.Share.Stop(share);
        }
    }

    private void OnAnswer(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share && SelectedPerson is { } person)
        {
            _commands.Share.Answer(share, person);
        }
    }

    private void OnGive(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share && SelectedPerson is { } person)
        {
            _commands.Share.GiveControl(share, person);
        }
    }

    private void OnTake(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share)
        {
            _commands.Share.TakeControl(share);
        }
    }

    private void OnExclude(object? sender, RoutedEventArgs e)
    {
        if (SelectedShare is { } share && SelectedPerson is { } person)
        {
            share.Share.Exclude(person);
        }
    }

    private void OnShow(object? sender, RoutedEventArgs e)
    {
        if (SelectedWatched is { } watched)
        {
            _commands.Show(watched);
        }
    }

    private void OnWatchedDoubleClick(object? sender, TappedEventArgs e) => OnShow(sender, e);

    private void OnLeave(object? sender, RoutedEventArgs e)
    {
        if (SelectedWatched is { } watched)
        {
            _commands.Leave(watched);
        }
    }

    private void OnJoin(object? sender, RoutedEventArgs e) => _commands.Join();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
