using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Terminal;

namespace ZillaTerm.App.Views;

/// <summary>
/// Choix d'au plus <c>max</c> éléments à afficher dans la vue parallèle : sessions SSH ouvertes, ou serveurs « Mes serveurs »
/// à ouvrir quand un dossier ou une sélection en compte plus que la vue ne peut en recevoir.
/// </summary>
public partial class ParallelDialog : DialogWindow
{
    private readonly List<CheckBox> _boxes = [];
    private readonly IReadOnlyList<SshSession>? _sessions;
    private readonly int _max;

    public ParallelDialog() => InitializeComponent();

    /// <param name="sessions">Sessions SSH ouvertes, dans l'ordre des onglets.</param>
    /// <param name="selected">Sessions cochées au départ (celles déjà dans la vue, ou celle de l'onglet actif).</param>
    public ParallelDialog(IReadOnlyList<SshSession> sessions, IReadOnlyCollection<SshSession> selected)
        : this(sessions.Select(s => s.Label).ToList(),
            sessions.Select((session, index) => (session, index)).Where(x => selected.Contains(x.session)).Select(x => x.index).ToList(),
            ParallelLayout.MaxSessions, Strings.ParallelDialogIntro, Strings.ParallelShow)
    {
        _sessions = sessions;
    }

    /// <param name="labels">Éléments proposés, dans l'ordre.</param>
    /// <param name="selected">Indices cochés au départ (au-delà de <paramref name="max"/>, les derniers ne le sont pas).</param>
    public ParallelDialog(IReadOnlyList<string> labels, IReadOnlyCollection<int> selected, int max, string intro, string action)
        : this()
    {
        _max = Math.Max(1, max);
        IntroText.Text = intro;
        ShowButton.Content = action;
        for (int i = 0; i < labels.Count; i++)
        {
            var box = new CheckBox
            {
                // Une ligne, tronquée : le modèle de la case transmettrait sinon son retour à la ligne (propriété héritée).
                Content = new TextBlock { Text = labels[i], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                IsChecked = selected.Contains(i),
                Margin = new Thickness(0, 2, 0, 2),
            };
            box.Click += (_, _) => Update();
            _boxes.Add(box);
            SessionsPanel.Children.Add(box);
        }

        foreach (var box in _boxes.Where(b => b.IsChecked == true).Skip(_max))
        {
            box.IsChecked = false;
        }

        Update();
        Opened += (_, _) =>
        {
            if ((_boxes.FirstOrDefault(b => b.IsChecked != true) ?? _boxes.FirstOrDefault()) is { } first)
            {
                FocusLater(first);
            }
        };
    }

    /// <summary>Indices choisis, dans l'ordre.</summary>
    public IReadOnlyList<int> SelectedIndexes => _boxes.Select((box, index) => (box, index)).Where(x => x.box.IsChecked == true).Select(x => x.index).ToList();

    /// <summary>Sessions choisies, dans l'ordre des onglets (fenêtre ouverte avec des sessions).</summary>
    public IReadOnlyList<SshSession> Selected => _sessions is null ? [] : SelectedIndexes.Select(i => _sessions[i]).ToList();

    /// <summary>Au plus <c>max</c> cases cochées : les autres sont grisées une fois la limite atteinte.</summary>
    private void Update()
    {
        int count = _boxes.Count(b => b.IsChecked == true);
        foreach (var box in _boxes)
        {
            box.IsEnabled = box.IsChecked == true || count < _max;
        }

        CountText.Text = Text.Format(Strings.ParallelSelected, count, _max);
    }

    private void OnShow(object? sender, RoutedEventArgs e) => DialogResult = true;
}
