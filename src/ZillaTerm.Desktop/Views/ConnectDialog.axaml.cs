using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>Paramètres d'une connexion : type, composant PSM, machine cible, motif et ticket.</summary>
public partial class ConnectDialog : DialogWindow
{
    private readonly PvwaAccount _account = new();
    private readonly AppSettings _settings = new();
    private readonly string _vaultUser = "";
    private readonly bool _requireMachine;

    public ConnectDialog() => InitializeComponent();

    /// <param name="componentError">Le PVWA ne connaît pas le composant demandé pour ce compte (EPVWA093E).</param>
    /// <param name="requireMachine">
    /// Compte de domaine ou limité à des machines : la machine cible est obligatoire (jamais de session vers le domaine).
    /// </param>
    public ConnectDialog(PvwaAccount account, ConnectRequest initial, AppSettings settings, string vaultUser, string? error,
        bool componentError = false, bool requireMachine = false)
        : this()
    {
        _account = account;
        _settings = settings;
        _vaultUser = vaultUser;
        _requireMachine = requireMachine || AccountClassifier.NeedsRemoteMachine(account);

        AccountKindIcon.Show(KindIcon, account);
        TitleText.Text = $"{account.UserName}@{account.Address}";
        DetailsText.Text = Text.Format(Strings.PlatformAndSafe, account.PlatformId, account.SafeName);

        ComponentBox.ItemsSource = settings.KnownComponents(account.PlatformId);
        ComponentBox.Text = initial.Component;
        MachineBox.ItemsSource = AccountClassifier.RemoteMachineList(account);
        // Machine obligatoire : on propose la première machine autorisée, modifiable. Sinon, vide : la session s'ouvre
        // sur l'adresse du compte.
        MachineBox.Text = initial.RemoteMachine
                          ?? (_requireMachine ? AccountClassifier.RemoteMachineList(account).FirstOrDefault() : null) ?? "";
        ReasonBox.Text = initial.Reason ?? "";
        TicketSystemBox.Text = initial.TicketingSystem ?? "";
        TicketIdBox.Text = initial.TicketId ?? "";
        // Texte simple : un « _ » du nom de la plateforme (Unix_SSH) reste affiché, il n'est pas pris pour une touche d'accès.
        var remember = Text.Format(Strings.RememberComponent, account.PlatformId);
        RememberBox.Content = new TextBlock { Text = remember, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(RememberBox, remember);
        // Jamais cochée d'office, même après un refus du composant : un nom mémorisé vaut pour tous les comptes de la
        // plateforme, il reste un choix de l'utilisateur (et n'est retenu qu'une fois accepté par le PVWA).
        RememberBox.IsChecked = initial.RememberComponent;
        X11Box.IsChecked = initial.X11Forwarding;

        bool sshAvailable = PsmpRouting.Any(settings);
        SshRadio.IsEnabled = sshAvailable;
        ToolTip.SetTip(SshRadio, sshAvailable ? null : Strings.SetPsmpAddress);
        SftpRadio.IsEnabled = sshAvailable;
        ToolTip.SetTip(SftpRadio, sshAvailable ? Strings.MenuConnectSftpTip : Strings.SetPsmpAddress);
        // Grisés faute de PSMP : l'infobulle dit pourquoi.
        ToolTip.SetShowOnDisabled(SshRadio, true);
        ToolTip.SetShowOnDisabled(SftpRadio, true);
        ((sshAvailable ? initial.Mode : ConnectMode.Psm) switch
        {
            ConnectMode.Ssh => SshRadio,
            ConnectMode.Sftp => SftpRadio,
            _ => PsmRadio,
        }).IsChecked = true;

        EditableComboText.OnChanged(MachineBox, UpdateSshHint);

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText.Text = error;
            ErrorPanel.IsVisible = true;
        }

        Opened += (_, _) =>
        {
            if (componentError || (error is not null && error.Contains("component", StringComparison.OrdinalIgnoreCase)))
            {
                EditableComboText.Focus(ComponentBox, selectAll: true);
            }
            else if (error is not null)
            {
                FocusLater(ReasonBox);
            }
            else if (_requireMachine)
            {
                EditableComboText.Focus(MachineBox);
            }
            else if (ComponentBox.IsEnabled)
            {
                EditableComboText.Focus(ComponentBox);
            }
            else
            {
                // SSH ou SFTP : les champs du PSM sont grisés, le curseur va sur le mode choisi.
                FocusLater(SftpRadio.IsChecked == true ? SftpRadio : SshRadio);
            }
        };
    }

    public ConnectRequest? Result { get; private set; }

    // Infobulles d'origine des champs du PSM, remplacées par la raison quand ils sont grisés.
    private readonly Dictionary<Control, object?> _psmTips = [];

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        // Le bouton qui se décoche le signale aussi : seul celui qui se coche compte.
        if (sender is not RadioButton { IsChecked: true })
        {
            return;
        }

        bool psm = PsmRadio.IsChecked == true;
        // Champs propres au PSM : grisés en SSH/SFTP, avec la raison en infobulle.
        foreach (var field in new Control[] { ComponentBox, ReasonBox, TicketSystemBox, TicketIdBox, RememberBox })
        {
            _psmTips.TryAdd(field, ToolTip.GetTip(field));
            field.IsEnabled = psm;
            ToolTip.SetShowOnDisabled(field, true);
            ToolTip.SetTip(field, psm ? _psmTips[field] : Strings.PsmOnlyField);
        }

        // Transfert X11 : sessions SSH seulement (ni PSM, ni fichiers seuls).
        X11Box.IsEnabled = SshRadio.IsChecked == true;
        ToolTip.SetTip(X11Box, X11Box.IsEnabled ? Strings.X11ForwardingTip : Strings.X11SshOnly);
        UpdateSshHint();
    }

    private void UpdateSshHint()
    {
        if (PsmRadio.IsChecked == true)
        {
            SshHint.Text = _settings.PsmDesktopInTab ? Strings.PsmHintTab : Strings.PsmHint;
            return;
        }

        var machine = EditableComboText.Of(MachineBox);
        try
        {
            var login = PsmpTarget.BuildLogin(_vaultUser, _account, machine);
            // Le PSMP dépend du serveur visé (règle de son domaine, sinon PSMP par défaut).
            var server = string.IsNullOrWhiteSpace(machine) ? _account.Address : machine.Trim();
            if (PsmpRouting.Resolve(_settings, server) is not { } psmp)
            {
                SshHint.Text = Text.Format(Strings.PsmpNoRoute, server);
                return;
            }

            var target = $"{login}@{psmp.Host}";
            SshHint.Text = Text.Format(SftpRadio.IsChecked == true ? Strings.SftpCommandHint : Strings.SshCommandHint,
                target.Contains(' ') ? $"\"{target}\"" : target);
        }
        catch (ArgumentException ex)
        {
            SshHint.Text = ex.Message;
        }
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (Accept())
        {
            DialogResult = true;
        }
    }

    /// <summary>Vérifie les champs et prépare <see cref="Result"/> ; faux après avoir affiché l'erreur.</summary>
    internal bool Accept()
    {
        bool psm = PsmRadio.IsChecked == true;
        string component = EditableComboText.Of(ComponentBox).Trim();
        if (psm && (component.Length == 0 || !AppSettings.IsValidComponentName(component)))
        {
            // Même règle que dans les Paramètres : un nom retenu ici ne doit pas y bloquer l'enregistrement.
            ErrorText.Text = component.Length == 0 ? Strings.ComponentRequired : Strings.InvalidComponentName;
            ErrorPanel.IsVisible = true;
            EditableComboText.Focus(ComponentBox);
            return false;
        }

        if (_requireMachine && string.IsNullOrWhiteSpace(MachineBox.Text))
        {
            ErrorText.Text = Strings.ServerPromptRequired;
            ErrorPanel.IsVisible = true;
            EditableComboText.Focus(MachineBox);
            return false;
        }

        Result = new ConnectRequest(
            psm ? ConnectMode.Psm : SftpRadio.IsChecked == true ? ConnectMode.Sftp : ConnectMode.Ssh,
            component,
            NullIfEmpty(MachineBox.Text),
            NullIfEmpty(ReasonBox.Text),
            NullIfEmpty(TicketSystemBox.Text),
            NullIfEmpty(TicketIdBox.Text),
            psm && RememberBox.IsChecked == true,
            SshRadio.IsChecked == true && X11Box.IsChecked == true);
        return true;
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
