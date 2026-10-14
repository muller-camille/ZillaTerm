using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ZillaTerm.App.Localization;

namespace ZillaTerm.App.Views;

/// <summary>Nature d'une confirmation : son icône (question, information, avertissement, danger).</summary>
internal enum ConfirmKind
{
    Question,
    Info,
    Warning,
    Danger,
}

/// <summary>Contenu d'une <see cref="ConfirmDialog"/>.</summary>
internal sealed record ConfirmRequest
{
    /// <summary>Titre de la fenêtre : l'action, pas le nom de l'application.</summary>
    public required string Title { get; init; }

    /// <summary>Question principale, en gras.</summary>
    public required string Heading { get; init; }

    /// <summary>Ce qui est visé : serveur, compte, safe… (sous la question).</summary>
    public string? Subject { get; init; }

    public string? Message { get; init; }

    /// <summary>« Ce qui va se passer » : 2 ou 3 conséquences.</summary>
    public IReadOnlyList<string> Bullets { get; init; } = [];

    /// <summary>Éléments concernés (fichiers, serveurs), dans une liste qui défile.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>Valeurs à comparer ou à copier (empreintes) : libellé et valeur, en police à espacement fixe.</summary>
    public IReadOnlyList<(string Label, string Value)> Codes { get; init; } = [];

    public ConfirmKind Kind { get; init; } = ConfirmKind.Question;

    /// <summary>Bandeau rouge en haut de la fenêtre (clé changée…).</summary>
    public string? Banner { get; init; }

    /// <summary>Boutons d'action, dans l'ordre ; « Annuler » est ajouté à la fin.</summary>
    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>Action irréversible (bouton rouge), ou -1.</summary>
    public int DangerAction { get; init; } = -1;

    /// <summary>Action déclenchée par Entrée ; -1 (par défaut) : Entrée annule.</summary>
    public int DefaultAction { get; init; } = -1;

    public string? CancelLabel { get; init; }

    /// <summary>Case à cocher sans laquelle les actions restent grisées (« J'ai vérifié… »).</summary>
    public string? Acknowledge { get; init; }

    /// <summary>Case « Ne plus demander » (son état est rendu par <see cref="ConfirmDialog.AskAsync(Window?, ConfirmRequest, bool)"/>).</summary>
    public string? DontAskAgain { get; init; }

    /// <summary>Pas de bouton « Annuler » : simple message (remplace MessageBox), seule action « OK ».</summary>
    public bool NoCancel { get; init; }
}

/// <summary>
/// Confirmation aux boutons explicites (« Supprimer le compte », « Remplacer la clé »…) dans la langue de l'application,
/// avec « Annuler » par défaut. Remplace les boîtes de message Oui/Non, que l'on valide par réflexe.
/// </summary>
public partial class ConfirmDialog : DialogWindow
{
    private readonly List<Button> _actionButtons = [];

    public ConfirmDialog() => InitializeComponent();

    internal ConfirmDialog(ConfirmRequest request)
        : this()
    {
        Title = request.Title;
        Width = request.Codes.Count > 0 || request.Items.Count > 0 ? 560 : 460;
        KindImage[!Image.SourceProperty] = this.GetResourceObservable(request.Kind switch
        {
            ConfirmKind.Info => "IconInfo",
            ConfirmKind.Warning => "IconWarning",
            ConfirmKind.Danger => "IconDanger",
            _ => "IconQuestion",
        }).ToBinding();
        HeadingText.Text = request.Heading;
        SetText(SubjectText, request.Subject);
        SetText(MessageText, request.Message);
        SetText(BannerText, request.Banner);
        Banner.IsVisible = BannerText.IsVisible;
        // Alerte (clé changée…) écrite avant l'ouverture : annoncée une fois la fenêtre affichée.
        Opened += (_, _) => BannerText.Announce();
        if (request.Bullets.Count > 0)
        {
            BulletList.ItemsSource = request.Bullets;
            BulletList.IsVisible = true;
        }

        if (request.Items.Count > 0)
        {
            ItemsList.ItemsSource = request.Items;
            ItemsPanel.IsVisible = true;
        }

        if (request.Codes.Count > 0)
        {
            foreach (var (label, value) in request.Codes)
            {
                CodeList.Children.Add(CodeRow(label, value));
            }

            CodeList.IsVisible = true;
        }

        if (request.Acknowledge is { } acknowledge)
        {
            AcknowledgeText.Text = acknowledge;
            AcknowledgeBox.IsVisible = true;
        }

        if (request.DontAskAgain is { } dontAsk)
        {
            DontAskBox.Content = dontAsk;
            DontAskBox.IsVisible = true;
        }

        for (int i = 0; i < request.Actions.Count; i++)
        {
            int choice = i;
            var button = new Button
            {
                Content = request.Actions[i],
                IsDefault = request.DefaultAction == i && request.DangerAction != i,
                IsEnabled = request.Acknowledge is null,
            };
            if (request.DangerAction == i && this.TryFindResource("DangerButton", out var theme) && theme is ControlTheme danger)
            {
                button.Theme = danger;
            }

            button.Click += (_, _) =>
            {
                Choice = choice;
                DialogResult = true;
            };
            _actionButtons.Add(button);
            ButtonsPanel.Children.Add(button);
        }

        Button? cancel = null;
        if (!request.NoCancel)
        {
            cancel = new Button
            {
                Content = request.CancelLabel ?? Strings.Cancel,
                IsCancel = true,
                IsDefault = request.DefaultAction < 0 || request.DefaultAction == request.DangerAction,
            };
            ButtonsPanel.Children.Add(cancel);
        }

        var focus = _actionButtons.FirstOrDefault(b => b.IsDefault) ?? cancel ?? _actionButtons.FirstOrDefault();
        if (focus is not null)
        {
            Opened += (_, _) => FocusLater(focus);
        }
    }

    /// <summary>Indice de l'action choisie ; -1 si la fenêtre a été annulée.</summary>
    public int Choice { get; private set; } = -1;

    public bool DontAskAgain => DontAskBox.IsChecked == true;

    /// <summary>Affiche la confirmation ; rend l'indice de l'action choisie, ou -1 (Annuler, Échap, fermeture).</summary>
    internal static async Task<int> AskAsync(Window? owner, ConfirmRequest request) => (await AskWithDontAskAsync(owner, request)).Choice;

    /// <summary>Comme <see cref="AskAsync(Window?, ConfirmRequest)"/>, avec l'état de « Ne plus demander ».</summary>
    internal static async Task<(int Choice, bool DontAskAgain)> AskWithDontAskAsync(Window? owner, ConfirmRequest request)
    {
        var dialog = new ConfirmDialog(request);
        bool accepted = await dialog.ShowDialogAsync(owner);
        return accepted ? (dialog.Choice, dialog.DontAskAgain) : (-1, false);
    }

    /// <summary>Une seule action : vrai si elle est choisie.</summary>
    internal static async Task<bool> ConfirmAsync(Window? owner, ConfirmRequest request) => await AskAsync(owner, request) == 0;

    /// <summary>Action irréversible : bouton rouge au verbe explicite, « Annuler » par défaut.</summary>
    internal static Task<bool> DestructiveAsync(Window? owner, string title, string heading, string action, string? subject = null,
        string? message = null, IReadOnlyList<string>? bullets = null, IReadOnlyList<string>? items = null, string? acknowledge = null) =>
        ConfirmAsync(owner, new ConfirmRequest
        {
            Title = title,
            Heading = heading,
            Subject = subject,
            Message = message,
            Bullets = bullets ?? [],
            Items = items ?? [],
            Kind = ConfirmKind.Danger,
            Actions = [action],
            DangerAction = 0,
            Acknowledge = acknowledge,
        });

    /// <summary>
    /// Message à lire (remplace MessageBox) : un seul bouton « OK », qui est le bouton par défaut et celui d'Échap.
    /// </summary>
    internal static Task ShowMessageAsync(Window? owner, string message, ConfirmKind kind = ConfirmKind.Info, string? title = null) =>
        AskAsync(owner, new ConfirmRequest
        {
            Title = title ?? "ZillaTerm",
            Heading = message,
            Kind = kind,
            Actions = [Strings.Ok],
            DefaultAction = 0,
            NoCancel = true,
        });

    private void OnAcknowledgeChanged(object? sender, RoutedEventArgs e)
    {
        foreach (var button in _actionButtons)
        {
            button.IsEnabled = AcknowledgeBox.IsChecked == true;
        }
    }

    private static void SetText(TextBlock text, string? value)
    {
        text.Text = value ?? "";
        text.IsVisible = !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>Une valeur à comparer : son libellé, la valeur (sélectionnable) et un bouton « Copier ».</summary>
    private Control CodeRow(string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        var copy = new Button { Content = Strings.ConfirmCopy, MinWidth = 0, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var box = new TextBox { Text = value };
        if (this.TryFindResource("CodeBox", out var theme) && theme is ControlTheme code)
        {
            box.Theme = code;
        }

        AutomationProperties.SetName(box, label);
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, value);
                copy.Content = Strings.ConfirmCopied;
            }
        };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(box);
        panel.Children.Add(row);
        return panel;
    }
}
