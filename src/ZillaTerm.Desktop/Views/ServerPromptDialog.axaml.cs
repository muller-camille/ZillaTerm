using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Serveur sur lequel ouvrir la session d'un compte de domaine (enregistré pour son domaine, pas pour un serveur) :
/// serveurs déjà utilisés avec ce compte proposés, option « Garder ce serveur dans « Mes serveurs » » mémorisée, avec
/// son dossier et son étiquette. À l'ajout d'un tel compte dans « Mes serveurs », le serveur est facultatif (vide :
/// demandé à chaque connexion) ; pour un autre serveur, seuls le dossier et l'étiquette sont demandés.
/// </summary>
public partial class ServerPromptDialog : DialogWindow
{
    private readonly IReadOnlyList<string> _allowed = [];
    private readonly bool _adding;
    private readonly Func<string, bool> _isSaved = _ => false;
    private readonly Func<string, string, string?>? _guessTag;
    private readonly List<(RadioButton Button, string? Tag)> _tags = [];

    /// <summary>Étiquette cochée par l'utilisateur : elle ne suit plus le serveur ni le dossier saisis.</summary>
    private bool _tagChosen;

    public ServerPromptDialog() => InitializeComponent();

    /// <param name="known">Serveurs à proposer, le plus probable d'abord (voir <see cref="SessionLibrary.KnownMachines"/>).</param>
    /// <param name="isSaved">Ce serveur est déjà dans « Mes serveurs » pour ce compte.</param>
    /// <param name="keep">Dernier choix de « Garder dans « Mes serveurs » ».</param>
    /// <param name="adding">Ajout à « Mes serveurs » plutôt que connexion.</param>
    /// <param name="domainAccount">Compte de domaine (sinon : compte limité à ses machines, sans phrase sur le domaine).</param>
    /// <param name="tags">Étiquettes à proposer (aucune : la question n'est pas posée).</param>
    /// <param name="guessTag">Étiquette devinée d'après le serveur et le dossier saisis.</param>
    /// <param name="name">Nom du serveur ajouté sans question de serveur (connexion récente, onglet, compte d'un serveur) ;
    /// null : le serveur est demandé.</param>
    public ServerPromptDialog(PvwaAccount account, IReadOnlyList<string> known, IEnumerable<string> folders, Func<string, bool> isSaved,
        bool keep, string folder, bool adding = false, bool domainAccount = true, IReadOnlyList<ServerTag>? tags = null,
        Func<string, string, string?>? guessTag = null, string? name = null)
        : this()
    {
        _adding = adding || name is not null;
        _isSaved = isSaved;
        _guessTag = guessTag;
        _allowed = AccountClassifier.IsRestrictedToRemoteMachines(account) ? AccountClassifier.RemoteMachineList(account) : [];

        AccountKindIcon.Show(KindIcon, account);
        TitleText.Text = $"{account.UserName}@{account.Address}";
        DetailsText.Text = Text.Format(Strings.PlatformAndSafe, account.PlatformId, account.SafeName);
        QuestionText.Text = adding ? Strings.ServerPromptAddQuestion : Strings.ServerPromptQuestion;
        var domain = string.IsNullOrWhiteSpace(account.LogonDomain) ? account.Address : account.LogonDomain;
        HintText.Text = Text.Format(Strings.ServerPromptHint, domain);
        HintText.IsVisible = domainAccount;

        ServerBox.ItemsSource = known;
        ServerBox.Text = known.FirstOrDefault() ?? "";
        ServerHint.Text = _allowed.Count > 0 ? Text.Format(Strings.ServerPromptRestricted, string.Join(", ", _allowed))
            : adding ? Strings.ServerPromptOptional
            : known.Count > 0 ? Strings.ServerPromptKnown
            : "";
        ServerHint.IsVisible = ServerHint.Text.Length > 0;

        KeepBox.IsChecked = keep;
        FolderBox.ItemsSource = folders.ToList();
        FolderBox.Text = folder;
        AddTags(tags ?? []);
        if (_adding)
        {
            // Ajout : dossier et étiquette demandés directement, sans case « Garder ».
            Title = Strings.AddToMyServersTitle;
            KeepBox.IsVisible = false;
            SavePanel.Margin = new Thickness(0);
            AdvancedButton.IsVisible = false;
            OkButton.Content = Strings.ServerPromptAdd;
        }

        if (name is not null)
        {
            // Serveur déjà connu : ni question de serveur ni phrase sur le domaine.
            QuestionText.Text = Text.Format(Strings.AddToMyServersQuestion, name);
            HintText.IsVisible = false;
            ServerPanel.IsVisible = false;
            ServerBox.Text = "";
        }

        // Texte saisi ou choisi dans la liste, même avant l'affichage de la fenêtre.
        EditableComboText.OnChanged(ServerBox, UpdateKeep);
        EditableComboText.OnChanged(FolderBox, UpdateGuess);
        UpdateKeep();
        Opened += (_, _) => EditableComboText.Focus(name is null ? ServerBox : FolderBox, selectAll: true);
    }

    public string Server => EditableComboText.Of(ServerBox).Trim();

    /// <summary>Le serveur est à ajouter à « Mes serveurs » après la connexion.</summary>
    public bool Keep => !_adding && KeepPanel.IsVisible && KeepBox.IsChecked == true && Server.Length > 0;

    /// <summary>Case « Garder » cochée : choix mémorisé pour la prochaine fois.</summary>
    public bool KeepChoice => KeepBox.IsChecked == true;

    public string Folder => SessionFolders.Normalize(FolderBox.Text);

    /// <summary>Étiquette choisie pour le serveur gardé ou ajouté ; null : aucune.</summary>
    public string? ChosenTag => _tags.FirstOrDefault(c => c.Button.IsChecked == true).Tag;

    /// <summary>Une case par étiquette (pastille de sa couleur), puis « Aucune étiquette » ; rien sans étiquette.</summary>
    private void AddTags(IReadOnlyList<ServerTag> tags)
    {
        if (tags.Count == 0)
        {
            TagLabel.IsVisible = false;
            TagPanel.IsVisible = false;
            return;
        }

        foreach (var tag in tags)
        {
            var button = new RadioButton
            {
                GroupName = "ServerTag",
                Content = ServerTagView.Chip(tag, 12),
                Margin = new Thickness(0, 4, 12, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, tag.Name);
            AddTag(button, tag.Name);
        }

        AddTag(new RadioButton
        {
            GroupName = "ServerTag",
            Content = new AccessText { Text = Strings.TagNone },
            Margin = new Thickness(0, 4, 12, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        }, null);
        TagLabel.Target = _tags[0].Button;
        UpdateGuess();
    }

    private void AddTag(RadioButton button, string? tag)
    {
        // Clic ou clavier : choix de l'utilisateur, gardé quel que soit le serveur ou le dossier saisi ensuite.
        button.Click += (_, _) =>
        {
            _tagChosen = true;
            GuessText.IsVisible = false;
        };
        _tags.Add((button, tag));
        TagChoices.Children.Add(button);
    }

    /// <summary>Tant que l'utilisateur n'a rien coché, l'étiquette devinée d'après le serveur et le dossier est cochée.</summary>
    private void UpdateGuess()
    {
        if (_tags.Count == 0 || _tagChosen)
        {
            return;
        }

        var guess = _guessTag?.Invoke(Server, Folder);
        var choice = _tags.FirstOrDefault(c => c.Tag is not null && string.Equals(c.Tag, guess, StringComparison.OrdinalIgnoreCase));
        (choice.Button ?? _tags[^1].Button).IsChecked = true;
        GuessText.IsVisible = choice.Tag is not null;
    }

    /// <summary>« Avancée… » : la fenêtre de connexion complète s'ouvre ensuite, avec ce serveur.</summary>
    public bool Advanced { get; private set; }

    /// <summary>Un serveur déjà gardé pour ce compte : la case disparaît, une ligne le dit.</summary>
    private void UpdateKeep()
    {
        UpdateGuess();
        if (_adding)
        {
            return;
        }

        bool saved = Server.Length > 0 && _isSaved(Server);
        KeepPanel.IsVisible = !saved;
        AlreadySavedText.IsVisible = saved;
        SavePanel.IsEnabled = KeepBox.IsChecked == true;
    }

    private void OnKeepChanged(object? sender, RoutedEventArgs e) => SavePanel.IsEnabled = _adding || KeepBox.IsChecked == true;

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (Validate(required: !_adding))
        {
            DialogResult = true;
        }
    }

    private void OnAdvanced(object? sender, RoutedEventArgs e)
    {
        if (Validate(required: false))
        {
            Advanced = true;
            DialogResult = true;
        }
    }

    /// <summary>Nom d'hôte ou adresse IP ; pour un compte limité à ses machines, l'une d'elles.</summary>
    internal bool Validate(bool required)
    {
        string? error = null;
        if (Server.Length == 0)
        {
            error = required ? Strings.ServerPromptRequired : null;
        }
        else if (Uri.CheckHostName(Server) == UriHostNameType.Unknown)
        {
            error = Strings.ServerPromptInvalid;
        }
        else if (_allowed.Count > 0 && !_allowed.Contains(Server, StringComparer.OrdinalIgnoreCase))
        {
            error = Text.Format(Strings.ServerPromptNotAllowed, Server);
        }

        ErrorText.Text = error ?? "";
        ErrorText.IsVisible = error is not null;
        if (error is not null)
        {
            EditableComboText.Focus(ServerBox);
        }

        return error is null;
    }
}
