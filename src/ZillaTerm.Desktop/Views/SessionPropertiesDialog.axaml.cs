using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>Configuration propre à un serveur de « Mes serveurs ».</summary>
public partial class SessionPropertiesDialog : DialogWindow
{
    private readonly SavedSession _session = new();

    public SessionPropertiesDialog() => InitializeComponent();

    /// <param name="components">Composants proposés (voir <see cref="AppSettings.KnownComponents"/>).</param>
    /// <param name="x11">Transfert X11 coché pour ce serveur (réglage personnel, hors du serveur enregistré).</param>
    /// <param name="tags">Étiquettes proposées (Paramètres) ; null : celles par défaut.</param>
    public SessionPropertiesDialog(SavedSession session, PvwaAccount? account, IEnumerable<string> folders, bool sshAvailable,
        IReadOnlyList<string> components, bool x11 = false, IReadOnlyList<ServerTag>? tags = null)
        : this()
    {
        _session = session;

        if (account is not null)
        {
            AccountKindIcon.Show(KindIcon, account);
        }
        else
        {
            AccountKindIcon.Show(KindIcon, session);
        }

        AccountText.Text = $"{session.UserName}@{session.Address}";
        DetailsText.Text = account is null
            ? Strings.AccountNotInList
            : Text.Format(Strings.PlatformAndSafe, account.PlatformId, account.SafeName);

        NameBox.Text = session.Name;
        FillTags(tags ?? ServerTagRules.Defaults(), session.Tag);
        FolderBox.ItemsSource = folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        FolderBox.Text = session.Folder;
        ComponentBox.ItemsSource = components;
        ComponentBox.Text = session.Component ?? "";
        MachineBox.ItemsSource = account is null ? [] : AccountClassifier.RemoteMachineList(account);
        MachineBox.Text = session.RemoteMachine ?? "";
        ReasonBox.Text = session.Reason ?? "";
        StartDirBox.Text = session.StartDirectory ?? "";
        X11Box.IsChecked = x11;
        // Comme dans « Connexion avancée » : sans PSMP, SSH et SFTP sont grisés (le mode enregistré reste coché, visible).
        SshRadio.IsEnabled = SftpRadio.IsEnabled = sshAvailable;
        ToolTip.SetShowOnDisabled(SshRadio, true);
        ToolTip.SetShowOnDisabled(SftpRadio, true);
        ToolTip.SetTip(SshRadio, sshAvailable ? null : Strings.SetPsmpAddress);
        ToolTip.SetTip(SftpRadio, sshAvailable ? Strings.MenuConnectSftpTip : Strings.SetPsmpAddress);
        (session.Mode switch { ConnectMode.Ssh => SshRadio, ConnectMode.Sftp => SftpRadio, _ => PsmRadio }).IsChecked = true;
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Une ligne par étiquette (pastille de sa couleur), puis « (aucune) » ; une étiquette du serveur absente des
    /// Paramètres reste proposée, en gris, pour ne pas être perdue sans le vouloir.
    /// </summary>
    private void FillTags(IReadOnlyList<ServerTag> tags, string? current)
    {
        var items = tags.Select(t => (Tag: t, Known: true)).ToList();
        if (ServerTagRules.NormalizeName(current) is { } name && ServerTagRules.Find(tags, name) is null)
        {
            items.Add((new ServerTag(name, ServerTagRules.NeutralColor), false));
        }

        foreach (var (tag, known) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ServerTagView.Chip(tag, 11));
            if (!known)
            {
                row.Children.Add(new TextBlock
                {
                    Text = Strings.TagUnknown,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var item = new ComboBoxItem { Content = row, Tag = tag.Name };
            AutomationProperties.SetName(item, known ? tag.Name : $"{tag.Name} {Strings.TagUnknown}");
            TagBox.Items.Add(item);
        }

        TagBox.Items.Add(new ComboBoxItem { Content = Strings.TagNoneItem, Tag = null });
        TagBox.SelectedItem = TagBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, ServerTagRules.NormalizeName(current), StringComparison.OrdinalIgnoreCase))
            ?? TagBox.Items[TagBox.Items.Count - 1];
    }

    /// <summary>Étiquette choisie ; null : aucune.</summary>
    public string? ChosenTag => (TagBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        // Le bouton qui se décoche le signale aussi : seul celui qui se coche compte.
        if (sender is not RadioButton { IsChecked: true })
        {
            return;
        }

        ComponentBox.IsEnabled = PsmRadio.IsChecked == true;
        ReasonBox.IsEnabled = PsmRadio.IsChecked == true;
        StartDirBox.IsEnabled = PsmRadio.IsChecked != true;
        X11Box.IsEnabled = SshRadio.IsChecked == true;
        ToolTip.SetTip(X11Box, X11Box.IsEnabled ? Strings.X11ForwardingTip : Strings.X11SshOnly);
    }

    /// <summary>Transfert X11 choisi (sessions SSH seulement), une fois la fenêtre validée.</summary>
    public bool X11Forwarding { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var start = (StartDirBox.Text ?? "").Trim();
        if (start.Length > 0 && !start.StartsWith('/'))
        {
            ErrorText.Text = Strings.StartDirMustBeAbsolute;
            ErrorText.IsVisible = true;
            StartDirBox.Focus();
            StartDirBox.SelectAll();
            return;
        }

        var name = (NameBox.Text ?? "").Trim();
        _session.Name = name.Length > 0 ? name : $"{_session.UserName}@{_session.Address}";
        _session.Folder = SessionFolders.Normalize(FolderBox.Text);
        _session.Mode = SshRadio.IsChecked == true ? ConnectMode.Ssh : SftpRadio.IsChecked == true ? ConnectMode.Sftp : ConnectMode.Psm;
        _session.Component = NullIfEmpty(ComponentBox.Text);
        _session.RemoteMachine = NullIfEmpty(MachineBox.Text);
        _session.Reason = NullIfEmpty(ReasonBox.Text);
        _session.StartDirectory = NullIfEmpty(start);
        _session.Tag = ChosenTag;
        X11Forwarding = _session.Mode == ConnectMode.Ssh && X11Box.IsChecked == true;
        DialogResult = true;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
