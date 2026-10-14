using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.App.Views;

/// <summary>
/// Partager une session : durée, main possible (terminal seulement) et ce que l'on accepte en partageant ; puis
/// l'invitation à envoyer à la personne, sa validité et par où elle passera. Le partage continue fenêtre fermée.
/// </summary>
/// <remarks>
/// Le partage est commencé par la fenêtre principale (délégué <c>start</c>), qui garde le partage actif de la session
/// (<c>ActiveShare</c> de <c>ShareService</c>, pas encore porté) : la fenêtre n'en connaît que le partage lui-même.
/// </remarks>
public partial class ShareSessionDialog : DialogWindow
{
    /// <summary>Durées proposées ; une heure par défaut.</summary>
    internal static readonly TimeSpan[] Durations =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4)];

    private readonly Func<TimeSpan, bool, HostedShare>? _start;

    public ShareSessionDialog() => InitializeComponent();

    /// <param name="start">Commence le partage (durée, main possible) ; lève <see cref="ShareException"/> s'il est impossible.</param>
    internal ShareSessionDialog(string label, ShareKind kind, Func<TimeSpan, bool, HostedShare> start)
        : this()
    {
        _start = start;
        SessionText.Text = label;
        DurationBox.DisplayMemberBinding = new Binding(nameof(KeyValuePair<TimeSpan, string>.Value));
        DurationBox.SelectedValueBinding = new Binding(nameof(KeyValuePair<TimeSpan, string>.Key));
        DurationBox.ItemsSource = Durations.Select(d => new KeyValuePair<TimeSpan, string>(d, DurationText(d))).ToList();
        DurationBox.SelectedValue = TimeSpan.FromHours(1);
        bool terminal = kind == ShareKind.Terminal;
        ControlBox.IsChecked = terminal;
        ControlBox.IsVisible = ControlHelp.IsVisible = terminal;
        ScreenHelp.IsVisible = !terminal;
        // Invitation d'un partage en cours (montrée avant l'ouverture) : « Copier l'invitation » a le focus.
        Opened += (_, _) => FocusLater(OptionsPanel.IsVisible ? DurationBox : CopyButton);
    }

    /// <summary>Partage commencé ; null si la fenêtre a été fermée avant.</summary>
    internal HostedShare? Started { get; private set; }

    internal static string DurationText(TimeSpan duration) => duration.TotalMinutes < 60
        ? Text.Format(Strings.ShareMinutes, (int)duration.TotalMinutes)
        : Text.Format(Strings.ShareHours, (int)duration.TotalHours);

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (_start is null)
        {
            return;
        }

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

        ShowInvitation(Started);
    }

    /// <summary>Invitation d'un partage commencé (aussi pour un partage en cours, depuis « Gérer le partage »).</summary>
    internal void ShowInvitation(HostedShare share)
    {
        ErrorMessage.IsVisible = false;
        OptionsPanel.IsVisible = false;
        InvitationPanel.IsVisible = true;
        StartButton.IsVisible = false;
        StartButton.IsDefault = false;
        CopyButton.IsVisible = true;
        CopyButton.IsDefault = true;
        CancelButton.Content = Strings.CloseButton;
        HeadingText.Text = Strings.ShareStartedTitle;
        InvitationBox.Text = share.Invitation.ToString();
        ValidityText.Text = Text.Format(Strings.ShareValidUntil, share.Info.EndsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        RouteText.Text = RouteDescription(share.Invitation);
        if (IsVisible)
        {
            FocusLater(CopyButton);
        }
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

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                ShowError(Strings.ClipboardBusy);
                return;
            }

            await clipboard.SetTextAsync(InvitationBox.Text ?? "");
            CopiedText.Text = Strings.ShareInvitationCopied;
            // Copié une seconde fois : redit aux lecteurs d'écran (le texte, inchangé, ne l'annoncerait pas).
            if (CopiedText.IsVisible)
            {
                CopiedText.Announce();
            }

            CopiedText.IsVisible = true;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or TimeoutException)
        {
            // Presse-papiers tenu par une autre application : l'invitation reste sélectionnable dans la zone de texte.
            DebugLog.Write("partage", "Copie de l'invitation impossible", ex);
            ShowError(Strings.ClipboardBusy);
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.IsVisible = true;
    }
}
