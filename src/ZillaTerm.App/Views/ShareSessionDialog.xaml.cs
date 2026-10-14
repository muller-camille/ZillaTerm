using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services.Sharing;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>
/// Partager une session : durée, main possible (terminal seulement) et ce que l'on accepte en partageant ; puis
/// l'invitation à envoyer à la personne, sa validité et par où elle passera. Le partage continue fenêtre fermée.
/// </summary>
public partial class ShareSessionDialog : Window
{
    /// <summary>Durées proposées ; une heure par défaut.</summary>
    internal static readonly TimeSpan[] Durations =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4)];

    private readonly Func<TimeSpan, bool, ActiveShare> _start;

    /// <param name="start">Commence le partage (durée, main possible) ; lève <see cref="ShareException"/> s'il est impossible.</param>
    internal ShareSessionDialog(string label, ShareKind kind, Func<TimeSpan, bool, ActiveShare> start)
    {
        InitializeComponent();
        _start = start;
        SessionText.Text = label;
        DurationBox.ItemsSource = Durations.Select(d => new KeyValuePair<TimeSpan, string>(d, DurationText(d))).ToList();
        DurationBox.SelectedValue = TimeSpan.FromHours(1);
        bool terminal = kind == ShareKind.Terminal;
        ControlBox.IsChecked = terminal;
        ControlBox.Visibility = ControlHelp.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
        ScreenHelp.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => DurationBox.Focus();
    }

    /// <summary>Partage commencé ; null si la fenêtre a été fermée avant.</summary>
    internal ActiveShare? Started { get; private set; }

    internal static string DurationText(TimeSpan duration) => duration.TotalMinutes < 60
        ? Text.Format(Strings.ShareMinutes, (int)duration.TotalMinutes)
        : Text.Format(Strings.ShareHours, (int)duration.TotalHours);

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var duration = DurationBox.SelectedValue as TimeSpan? ?? TimeSpan.FromHours(1);
        try
        {
            Started = _start(duration, ControlBox.IsChecked == true);
        }
        catch (ShareException ex)
        {
            DebugLog.Write("partage", "Partage impossible", ex);
            ShowError(Strings.ShareUnreachable);
            return;
        }
        catch (InvalidOperationException ex)
        {
            DebugLog.Write("partage", "Partage refusé", ex);
            ShowError(Text.Format(Strings.ShareTooMany, ShareHost.MaxShares));
            return;
        }

        ShowInvitation(Started.Share);
    }

    /// <summary>Invitation d'un partage commencé (aussi pour un partage en cours, depuis « Gérer le partage »).</summary>
    internal void ShowInvitation(HostedShare share)
    {
        ErrorMessage.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        InvitationPanel.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Visible;
        CopyButton.IsDefault = true;
        CancelButton.Content = Strings.CloseButton;
        HeadingText.Text = Strings.ShareStartedTitle;
        InvitationBox.Text = share.Invitation.ToString();
        ValidityText.Text = Text.Format(Strings.ShareValidUntil, share.Info.EndsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        RouteText.Text = RouteDescription(share.Invitation);
        Dispatcher.BeginInvoke(() => CopyButton.Focus());
    }

    /// <summary>Par où la personne passera : adresses et port de ce poste, dossier relais.</summary>
    internal static string RouteDescription(ShareInvitation invitation)
    {
        var lines = new List<string>();
        if (invitation.Endpoints.Count > 0)
        {
            lines.Add(Text.Format(Strings.ShareRouteDirect, string.Join(", ", invitation.Endpoints.Select(e => e.Address.ToString()).Distinct()),
                invitation.Endpoints[0].Port));
        }

        if (invitation.RelayFolder is { } relay)
        {
            lines.Add(Text.Format(invitation.Endpoints.Count > 0 ? Strings.ShareRouteRelayToo : Strings.ShareRouteRelayOnly, relay));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(InvitationBox.Text);
            CopiedText.Visibility = Visibility.Visible;
            CopiedText.Text = Strings.ShareInvitationCopied;
        }
        catch (COMException ex)
        {
            // Presse-papiers tenu par une autre application : l'invitation reste sélectionnable dans la zone de texte.
            DebugLog.Write("partage", "Copie de l'invitation impossible", ex);
            ShowError(Strings.ClipboardBusy);
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
    }
}
