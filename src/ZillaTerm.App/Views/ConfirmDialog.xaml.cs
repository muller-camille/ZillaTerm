using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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

    /// <summary>Case « Ne plus demander » (son état est rendu dans <see cref="ConfirmDialog.DontAskAgain"/>).</summary>
    public string? DontAskAgain { get; init; }
}

/// <summary>
/// Confirmation aux boutons explicites (« Supprimer le compte », « Remplacer la clé »…) dans la langue de l'application,
/// avec « Annuler » par défaut. Remplace les MessageBox Oui/Non, dont les boutons suivent la langue de Windows et que
/// l'on valide par réflexe.
/// </summary>
public partial class ConfirmDialog : Window
{
    private readonly List<Button> _actionButtons = [];

    internal ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        Width = request.Codes.Count > 0 || request.Items.Count > 0 ? 560 : 460;
        KindImage.Source = (ImageSource)FindResource(request.Kind switch
        {
            ConfirmKind.Info => "IconInfo",
            ConfirmKind.Warning => "IconWarning",
            ConfirmKind.Danger => "IconDanger",
            _ => "IconQuestion",
        });
        HeadingText.Text = request.Heading;
        SetText(SubjectText, request.Subject);
        SetText(MessageText, request.Message);
        SetText(BannerText, request.Banner);
        Banner.Visibility = BannerText.Visibility;
        // Alerte (clé changée…) écrite avant l'ouverture : annoncée une fois la fenêtre affichée.
        Loaded += (_, _) => BannerText.Announce();
        if (request.Bullets.Count > 0)
        {
            BulletList.ItemsSource = request.Bullets;
            BulletList.Visibility = Visibility.Visible;
        }

        if (request.Items.Count > 0)
        {
            ItemsList.ItemsSource = request.Items;
            ItemsPanel.Visibility = Visibility.Visible;
        }

        if (request.Codes.Count > 0)
        {
            CodeList.ItemsSource = request.Codes.Select(c => CodeRow(c.Label, c.Value)).ToList();
            CodeList.Visibility = Visibility.Visible;
        }

        if (request.Acknowledge is { } acknowledge)
        {
            AcknowledgeText.Text = acknowledge;
            AcknowledgeBox.Visibility = Visibility.Visible;
        }

        if (request.DontAskAgain is { } dontAsk)
        {
            DontAskBox.Content = dontAsk;
            DontAskBox.Visibility = Visibility.Visible;
        }

        for (int i = 0; i < request.Actions.Count; i++)
        {
            int choice = i;
            var button = new Button
            {
                Content = request.Actions[i],
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = request.DefaultAction == i && request.DangerAction != i,
                IsEnabled = request.Acknowledge is null,
            };
            if (request.DangerAction == i)
            {
                button.Style = (Style)FindResource("DangerButton");
            }

            button.Click += (_, _) =>
            {
                Choice = choice;
                DialogResult = true;
            };
            _actionButtons.Add(button);
            ButtonsPanel.Children.Add(button);
        }

        var cancel = new Button
        {
            Content = request.CancelLabel ?? Strings.Cancel,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
            IsDefault = request.DefaultAction < 0 || request.DefaultAction == request.DangerAction,
        };
        ButtonsPanel.Children.Add(cancel);
        var focus = _actionButtons.FirstOrDefault(b => b.IsDefault) ?? cancel;
        Loaded += (_, _) => focus.Focus();
    }

    /// <summary>Indice de l'action choisie ; -1 si la fenêtre a été annulée.</summary>
    public int Choice { get; private set; } = -1;

    public bool DontAskAgain => DontAskBox.IsChecked == true;

    /// <summary>Affiche la confirmation ; rend l'indice de l'action choisie, ou -1 (Annuler, Échap, fermeture).</summary>
    internal static int Ask(Window? owner, ConfirmRequest request) => Ask(owner, request, out _);

    internal static int Ask(Window? owner, ConfirmRequest request, out bool dontAskAgain)
    {
        var dialog = new ConfirmDialog(request);
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        bool accepted = dialog.ShowDialog() == true;
        dontAskAgain = accepted && dialog.DontAskAgain;
        return accepted ? dialog.Choice : -1;
    }

    /// <summary>Une seule action : vrai si elle est choisie.</summary>
    internal static bool Confirm(Window? owner, ConfirmRequest request) => Ask(owner, request) == 0;

    /// <summary>Action irréversible : bouton rouge au verbe explicite, « Annuler » par défaut.</summary>
    internal static bool Destructive(Window? owner, string title, string heading, string action, string? subject = null,
        string? message = null, IReadOnlyList<string>? bullets = null, IReadOnlyList<string>? items = null, string? acknowledge = null) =>
        Confirm(owner, new ConfirmRequest
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

    private void OnAcknowledgeChanged(object sender, RoutedEventArgs e)
    {
        foreach (var button in _actionButtons)
        {
            button.IsEnabled = AcknowledgeBox.IsChecked == true;
        }
    }

    private static void SetText(TextBlock text, string? value)
    {
        text.Text = value ?? "";
        text.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Une valeur à comparer : son libellé, la valeur (sélectionnable) et un bouton « Copier ».</summary>
    private FrameworkElement CodeRow(string label, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        var copy = new Button { Content = Strings.ConfirmCopy, MinWidth = 0, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var box = new TextBox { Text = value, Style = (Style)FindResource("CodeBox") };
        AutomationProperties.SetName(box, label);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(value);
                copy.Content = Strings.ConfirmCopied;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Presse-papiers occupé par une autre application : la valeur reste sélectionnable.
            }
        };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(box);
        panel.Children.Add(row);
        return panel;
    }
}
