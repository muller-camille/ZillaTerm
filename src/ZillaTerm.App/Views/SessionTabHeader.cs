using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>État d'une session tel que l'affiche son onglet.</summary>
public enum SessionTabState
{
    Connecting,
    Connected,

    /// <summary>Connexion perdue ou serveur muet : reconnexion ou vérification en cours.</summary>
    Interrupted,
    Ended,
    Failed,
}

/// <summary>
/// En-tête d'un onglet de session : pastille d'état, icône, nom (tronqué, complet dans l'infobulle) et croix. La forme de la
/// pastille porte l'état, pas seulement sa couleur (contraste élevé, couleurs mal distinguées) : anneau pendant la
/// connexion, pleine une fois connectée, à moitié pleine quand la connexion est interrompue, carré quand la session est
/// terminée, croix en cas d'échec ; le nom est atténué quand la session ne répond plus. L'infobulle donne le nom complet,
/// l'état et le mode (via le PSMP ou accès direct hors CyberArk).
/// </summary>
public sealed class SessionTabHeader : StackPanel
{
    /// <summary>Largeur maximale du nom : au-delà, il est tronqué (« … ») et l'infobulle le donne en entier.</summary>
    public const double TitleMaxWidth = 200;

    // Formes de la pastille, dans 8 × 8 (trait de 1,5) : l'onglet garde sa largeur.
    private static readonly Geometry Ring = Frozen(new EllipseGeometry(new Point(4, 4), 3.25, 3.25));

    // Anneau et moitié droite : remplie en pair-impair, seule la moitié gauche est pleine.
    private static readonly Geometry HalfDisc = Frozen(new GeometryGroup
    {
        Children = { new EllipseGeometry(new Point(4, 4), 3.25, 3.25), Geometry.Parse("M4,0.75 A3.25,3.25 0 0 1 4,7.25 Z") },
    });

    private static readonly Geometry Square = Frozen(new RectangleGeometry(new Rect(1.25, 1.25, 5.5, 5.5)));
    private static readonly Geometry Cross = Frozen(Geometry.Parse("M1.5,1.5 L6.5,6.5 M6.5,1.5 L1.5,6.5"));

    private readonly Path _dot = new()
    {
        Width = 8, Height = 8, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private string _mode;
    private readonly string? _hint;

    /// <param name="label">Nom affiché, déjà numéroté s'il existe un autre onglet du même nom.</param>
    /// <param name="mode">Ligne de l'infobulle qui dit par où passe la session.</param>
    /// <param name="hint">Dernière ligne de l'infobulle (ex. : glisser pour détacher), facultative.</param>
    /// <param name="icon">Clé de l'icône dans Theme.xaml.</param>
    /// <param name="tag">Étiquette du serveur (PROD, QA…) : pastille de sa couleur avant le nom.</param>
    public SessionTabHeader(string label, string icon, Button close, string mode, string? hint, ServerTag? tag = null)
    {
        Label = label;
        _mode = mode;
        _hint = hint;
        Orientation = Orientation.Horizontal;
        Background = Brushes.Transparent;
        Title = new TextBlock
        {
            Text = label,
            MaxWidth = TitleMaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_dot);
        var image = Palette.Icon(icon, 16);
        image.Margin = new Thickness(0, 0, 6, 0);
        Children.Add(image);
        if (tag is not null)
        {
            var chip = ServerTagView.Chip(tag);
            chip.Margin = new Thickness(0, 0, 6, 0);
            Children.Add(chip);
            ServerTag = tag;
        }

        Children.Add(Title);
        Children.Add(close);
        SetState(SessionTabState.Connecting);
    }

    /// <summary>Étiquette du serveur, s'il en a une (dite aussi dans l'infobulle).</summary>
    public ServerTag? ServerTag { get; }

    /// <summary>Nom affiché dans l'onglet (numéroté pour les doublons).</summary>
    public string Label { get; }

    public TextBlock Title { get; }

    public SessionTabState State { get; private set; }

    /// <summary>Pastille d'état (forme et couleur).</summary>
    internal Path StateMark => _dot;

    /// <summary>Ligne du mode de l'infobulle, précisée après l'ouverture de l'onglet (adresse du PSM donnée par le PVWA).</summary>
    public void SetMode(string mode)
    {
        _mode = mode;
        SetState(State);
    }

    public void SetState(SessionTabState state)
    {
        State = state;
        var key = state switch
        {
            SessionTabState.Connected => "StateConnectedBrush",
            SessionTabState.Failed => "StateFailedBrush",
            SessionTabState.Ended => "StateEndedBrush",
            _ => "StateConnectingBrush",
        };
        _dot.SetResourceReference(Shape.StrokeProperty, key);
        _dot.Data = state switch
        {
            SessionTabState.Interrupted => HalfDisc,
            SessionTabState.Ended => Square,
            SessionTabState.Failed => Cross,
            _ => Ring,
        };
        if (state is SessionTabState.Connected or SessionTabState.Interrupted or SessionTabState.Ended)
        {
            _dot.SetResourceReference(Shape.FillProperty, key);
        }
        else
        {
            _dot.Fill = Brushes.Transparent;
        }

        if (state is SessionTabState.Ended or SessionTabState.Failed)
        {
            Title.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        }
        else
        {
            Title.ClearValue(TextBlock.ForegroundProperty);
        }

        var text = StateText(state);
        var name = ServerTag is { } tag ? $"{tag.Name} · {Label}" : Label;
        ToolTip = string.Join("\n", new[] { name, text, _mode, _hint }.Where(l => !string.IsNullOrEmpty(l)));
        System.Windows.Automation.AutomationProperties.SetName(this, $"{name}, {text}");
    }

    public static string StateText(SessionTabState state) => state switch
    {
        SessionTabState.Connected => Strings.TabStateConnected,
        SessionTabState.Interrupted => Strings.TabStateInterrupted,
        SessionTabState.Ended => Strings.TabStateEnded,
        SessionTabState.Failed => Strings.TabStateFailed,
        _ => Strings.TabStateConnecting,
    };

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Nom unique parmi les onglets ouverts : « nom », puis « nom (2) », « nom (3) »…</summary>
    public static string UniqueLabel(string label, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(label))
        {
            return label;
        }

        int n = 2;
        while (taken.Contains($"{label} ({n})"))
        {
            n++;
        }

        return $"{label} ({n})";
    }
}
