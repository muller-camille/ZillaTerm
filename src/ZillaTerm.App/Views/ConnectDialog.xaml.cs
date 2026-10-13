using System.Windows;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>Paramètres d'une connexion : type, composant PSM, machine cible, motif et ticket.</summary>
public partial class ConnectDialog : Window
{
    private readonly PvwaAccount _account;
    private readonly AppSettings _settings;
    private readonly string _vaultUser;
    private readonly bool _requireMachine;

    /// <param name="componentError">Le PVWA ne connaît pas le composant demandé pour ce compte (EPVWA093E).</param>
    /// <param name="requireMachine">
    /// Compte de domaine ou limité à des machines : la machine cible est obligatoire (jamais de session vers le domaine).
    /// </param>
    /// <param name="platformComponents">Composants de la plateforme lus sur le PVWA, proposés en premier ; null : inconnus.</param>
    public ConnectDialog(PvwaAccount account, ConnectRequest initial, AppSettings settings, string vaultUser, string? error,
        bool componentError = false, bool requireMachine = false, IReadOnlyList<string>? platformComponents = null)
    {
        InitializeComponent();
        _account = account;
        _settings = settings;
        _vaultUser = vaultUser;
        _requireMachine = requireMachine || AccountClassifier.NeedsRemoteMachine(account);

        KindIcon.Source = (System.Windows.Media.ImageSource?)new KindIconConverter().Convert(account, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture);
        TitleText.Text = $"{account.UserName}@{account.Address}";
        DetailsText.Text = Text.Format(Strings.PlatformAndSafe, account.PlatformId, account.SafeName);

        ComponentBox.ItemsSource = settings.KnownComponents(account.PlatformId, platformComponents);
        ComponentBox.Text = initial.Component;
        MachineBox.ItemsSource = AccountClassifier.RemoteMachineList(account);
        // Machine obligatoire : on propose la première machine autorisée, modifiable. Sinon, vide : la session s'ouvre
        // sur l'adresse du compte.
        MachineBox.Text = initial.RemoteMachine
                          ?? (_requireMachine ? AccountClassifier.RemoteMachineList(account).FirstOrDefault() : null) ?? "";
        ReasonBox.Text = initial.Reason ?? "";
        TicketSystemBox.Text = initial.TicketingSystem ?? "";
        TicketIdBox.Text = initial.TicketId ?? "";
        RememberBox.Content = Text.Format(Strings.RememberComponent, account.PlatformId);
        // Jamais cochée d'office, même après un refus du composant : un nom mémorisé vaut pour tous les comptes de la
        // plateforme, il reste un choix de l'utilisateur (et n'est retenu qu'une fois accepté par le PVWA).
        RememberBox.IsChecked = initial.RememberComponent;
        X11Box.IsChecked = initial.X11Forwarding;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(X11Box, true);

        bool sshAvailable = PsmpRouting.Any(settings);
        SshRadio.IsEnabled = sshAvailable;
        SshRadio.ToolTip = sshAvailable ? null : Strings.SetPsmpAddress;
        SftpRadio.IsEnabled = sshAvailable;
        SftpRadio.ToolTip = sshAvailable ? Strings.MenuConnectSftpTip : Strings.SetPsmpAddress;
        ((sshAvailable ? initial.Mode : ConnectMode.Psm) switch
        {
            ConnectMode.Ssh => SshRadio,
            ConnectMode.Sftp => SftpRadio,
            _ => PsmRadio,
        }).IsChecked = true;

        MachineBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((_, _) => UpdateSshHint()));

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText.Text = error;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            if (componentError || (error is not null && error.Contains("component", StringComparison.OrdinalIgnoreCase)))
            {
                ComponentBox.Focus();
                if (ComponentBox.Template.FindName("PART_EditableTextBox", ComponentBox) is System.Windows.Controls.TextBox text)
                {
                    text.SelectAll();
                }
            }
            else if (error is not null)
            {
                ReasonBox.Focus();
            }
            else if (_requireMachine)
            {
                MachineBox.Focus();
            }
            else if (ComponentBox.IsEnabled)
            {
                ComponentBox.Focus();
            }
            else
            {
                // SSH ou SFTP : les champs du PSM sont grisés, le curseur va sur le mode choisi.
                (SftpRadio.IsChecked == true ? SftpRadio : SshRadio).Focus();
            }
        };
    }

    public ConnectRequest? Result { get; private set; }

    // Infobulles d'origine des champs du PSM, remplacées par la raison quand ils sont grisés.
    private readonly Dictionary<System.Windows.Controls.Control, object?> _psmTips = [];

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (ComponentBox is null)
        {
            return;
        }

        bool psm = PsmRadio.IsChecked == true;
        // Champs propres au PSM : grisés en SSH/SFTP, avec la raison en infobulle.
        foreach (var field in new System.Windows.Controls.Control[] { ComponentBox, ReasonBox, TicketSystemBox, TicketIdBox, RememberBox })
        {
            _psmTips.TryAdd(field, field.ToolTip);
            field.IsEnabled = psm;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(field, true);
            field.ToolTip = psm ? _psmTips[field] : Strings.PsmOnlyField;
        }

        // Transfert X11 : sessions SSH seulement (ni PSM, ni fichiers seuls).
        X11Box.IsEnabled = SshRadio.IsChecked == true;
        X11Box.ToolTip = X11Box.IsEnabled ? Strings.X11ForwardingTip : Strings.X11SshOnly;
        UpdateSshHint();
    }

    private void UpdateSshHint()
    {
        if (PsmRadio.IsChecked == true)
        {
            SshHint.Text = _settings.PsmDesktopInTab ? Strings.PsmHintTab : Strings.PsmHint;
            return;
        }

        try
        {
            var login = PsmpTarget.BuildLogin(_vaultUser, _account, MachineBox.Text);
            // Le PSMP dépend du serveur visé (règle de son domaine, sinon PSMP par défaut).
            var server = string.IsNullOrWhiteSpace(MachineBox.Text) ? _account.Address : MachineBox.Text.Trim();
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

    private void OnConnect(object sender, RoutedEventArgs e)
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
        string component = ComponentBox.Text.Trim();
        if (psm && (component.Length == 0 || !AppSettings.IsValidComponentName(component)))
        {
            // Même règle que dans les Paramètres : un nom retenu ici ne doit pas y bloquer l'enregistrement.
            ErrorText.Text = component.Length == 0 ? Strings.ComponentRequired : Strings.InvalidComponentName;
            ErrorPanel.Visibility = Visibility.Visible;
            ComponentBox.Focus();
            return false;
        }

        if (_requireMachine && string.IsNullOrWhiteSpace(MachineBox.Text))
        {
            ErrorText.Text = Strings.ServerPromptRequired;
            ErrorPanel.Visibility = Visibility.Visible;
            MachineBox.Focus();
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

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
