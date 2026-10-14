using Avalonia.Controls;
using Avalonia.Interactivity;
using ZillaTerm.App.Localization;
using ZillaTerm.Core.KeePass;

namespace ZillaTerm.App.Views;

/// <summary>Création ou modification d'une entrée de coffre KeePass (serveur joignable en SSH ou en RDP).</summary>
/// <remarks>
/// « Afficher » montre le mot de passe dans le même champ (<see cref="TextBox.RevealPassword"/>) au lieu d'une seconde zone
/// de texte : le mot de passe n'est jamais recopié d'un champ à l'autre. En modification, « laisser vide pour garder le
/// mot de passe actuel » est le texte d'attente du champ, montré tant qu'il est vide.
/// </remarks>
public partial class KeePassEntryDialog : DialogWindow
{
    private readonly bool _editing;
    // Entrée modifiée qui avait déjà une adresse : elle doit la garder. Une entrée sans adresse (mot de passe seul,
    // commande) reste modifiable telle quelle.
    private readonly bool _hadHost;
    // Champs personnalisés de l'entrée modifiée (« Host », « Port »…) : ils comptent pour l'adresse du serveur.
    private readonly IReadOnlyDictionary<string, string> _customFields = new Dictionary<string, string>();

    public KeePassEntryDialog() => InitializeComponent();

    /// <param name="entry">Entrée à modifier ; null pour en créer une dans <paramref name="group"/>.</param>
    public KeePassEntryDialog(string vaultName, IEnumerable<string> groups, KeePassEntry? entry, string group)
        : this()
    {
        _editing = entry is not null;
        _customFields = entry?.CustomFields ?? new Dictionary<string, string>();
        _hadHost = entry is not null && HostOf(entry.Url).Length > 0;
        Title = _editing ? Strings.KeePassEditEntryTitle : Strings.KeePassNewEntryTitle;
        VaultText.Text = vaultName;
        GroupBox.ItemsSource = groups.Where(g => g.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
        GroupBox.Text = entry?.Group ?? group;
        TitleBox.Text = entry?.Title ?? "";
        UrlBox.Text = entry?.Url ?? "";
        UserBox.Text = entry?.UserName ?? "";
        NotesBox.Text = entry?.Notes ?? "";
        PasswordBox.PlaceholderText = _editing ? Strings.KeePassPasswordUnchanged : null;
        UpdateTarget();
        Opened += (_, _) => FocusLater(TitleBox);
    }

    /// <summary>Valeurs saisies ; mot de passe null s'il n'a pas été changé (modification).</summary>
    public KeePassEntryData? Result { get; private set; }

    /// <summary>Dossier choisi dans le coffre (« » pour la racine).</summary>
    public string Group { get; private set; } = "";

    private void OnUrlChanged(object? sender, TextChangedEventArgs e) => UpdateTarget();

    private void UpdateTarget()
    {
        if (TargetText is null)
        {
            return;
        }

        var target = KeePassTarget.From(new KeePassEntry
        {
            Id = "", Title = TitleBox.Text ?? "", Url = UrlBox.Text ?? "", UserName = UserBox.Text ?? "",
        });
        var where = target.UserName.Length > 0 ? $"{target.UserName}@{target.Address}" : target.Address;
        TargetText.Text = target.Host.Length == 0 ? Strings.KeePassTargetNone : target.Protocol switch
        {
            RemoteProtocol.Ssh => Text.Format(Strings.KeePassTargetSsh, where),
            RemoteProtocol.Rdp => Text.Format(Strings.KeePassTargetRdp, where),
            RemoteProtocol.Vnc => Text.Format(Strings.KeePassTargetVnc, target.Address),
            _ when KeePassTarget.IsFileTransfer(target.Protocol) => Text.Format(Strings.KeePassTargetFiles, KeePassTarget.Name(target.Protocol), where),
            _ => Strings.KeePassTargetUnknown,
        };
    }

    /// <summary>Mot de passe en clair ou masqué, dans le même champ.</summary>
    private void OnShowChanged(object? sender, RoutedEventArgs e) => PasswordBox.RevealPassword = ShowBox.IsChecked == true;

    /// <summary>Adresse donnée par l'URL ou les champs personnalisés (pas par le titre, qui n'en est pas une).</summary>
    private string HostOf(string url) =>
        KeePassTarget.From(new KeePassEntry { Id = "", Url = url, CustomFields = _customFields }).Host.Trim();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // Nouvelle entrée sans adresse : elle ne servirait pas à se connecter, l'erreur est dite ici plutôt qu'à la
        // connexion. Une entrée existante sans adresse se modifie quand même (changer son mot de passe ou son titre).
        var host = HostOf(UrlBox.Text ?? "");
        if (host.Length == 0 && (!_editing || _hadHost))
        {
            ErrorText.Text = Strings.KeePassAddressRequired;
            ErrorText.IsVisible = true;
            UrlBox.Focus();
            return;
        }

        var title = (TitleBox.Text ?? "").Trim();
        if (title.Length == 0)
        {
            title = host;
        }

        if (title.Length == 0)
        {
            // Ni titre ni adresse (entrée existante sans adresse) : l'entrée ne se reconnaîtrait plus dans la liste.
            ErrorText.Text = Strings.KeePassTitleRequired;
            ErrorText.IsVisible = true;
            TitleBox.Focus();
            return;
        }

        var password = PasswordBox.Text ?? "";
        Result = new KeePassEntryData(title, (UserBox.Text ?? "").Trim(), _editing && password.Length == 0 ? null : password,
            (UrlBox.Text ?? "").Trim(), NotesBox.Text ?? "");
        Group = KeePassGroupPath.Normalize(GroupBox.Text ?? "");
        PasswordBox.Clear();
        DialogResult = true;
    }

    /// <summary>Fenêtre fermée sans enregistrer : le mot de passe saisi (ou affiché en clair) n'y reste pas.</summary>
    protected override void OnClosed(EventArgs e)
    {
        PasswordBox.Clear();
        base.OnClosed(e);
    }
}
