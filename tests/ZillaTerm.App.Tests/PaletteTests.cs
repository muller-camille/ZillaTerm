using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace ZillaTerm.App.Tests;

/// <summary>
/// Contraste élevé : chaque couleur de la palette a son équivalent système. Thème sombre : chaque couleur a sa valeur
/// sombre, lisible sur son fond.
/// </summary>
public sealed class PaletteTests
{
    [Fact]
    public void EveryThemeColorHasAHighContrastValue()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = XDocument.Load(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", "Theme.xaml")).Root!;
        var colors = theme.Elements()
            .Where(e => e.Name == wpf + "SolidColorBrush" || e.Name == wpf + "LinearGradientBrush")
            .Select(e => (string?)e.Attribute(xaml + "Key"))
            .Where(k => k is not null && !k.StartsWith('{'))
            .ToHashSet();

        Assert.Empty(colors.Except(Palette.Keys));
        Assert.Empty(Palette.Keys.Except(colors!));
    }

    [Fact]
    public void HighContrastUsesSystemColors()
    {
        var dictionary = Palette.HighContrast();
        Assert.Same(SystemColors.WindowBrush, dictionary["ContentBrush"]);
        Assert.Same(SystemColors.WindowTextBrush, dictionary["MutedBrush"]);
        Assert.Same(SystemColors.HighlightBrush, dictionary["AccentBrush"]);
        Assert.Same(SystemColors.HighlightTextBrush, dictionary[SystemColors.InactiveSelectionHighlightTextBrushKey]);
        // « ! » de la pastille rouge : couleur du fond sur celle du texte, jamais la même.
        Assert.Same(SystemColors.WindowBrush, dictionary["BadgeForeground"]);
        Assert.Same(SystemColors.WindowTextBrush, dictionary["StateFailedBrush"]);
    }

    [Fact]
    public void EveryThemeColorHasADarkValue()
    {
        var dark = Colors("ThemeDark.xaml");
        Assert.Empty(Palette.Keys.Except(dark.Keys));
        Assert.Contains("ToolbarBrush", dark.Keys);
    }

    /// <summary>Texte 4,5:1 sur son fond ; bordures, pastilles et barres de défilement 3:1.</summary>
    [Theory]
    [InlineData("SidePanelForeground", "SidePanelBrush", 4.5)]
    [InlineData("MutedBrush", "SidePanelBrush", 4.5)]
    [InlineData("MutedBrush", "ContentBrush", 4.5)]
    [InlineData("MutedBrush", "AlternateRowBrush", 4.5)]
    [InlineData("PlaceholderBrush", "ContentBrush", 4.5)]
    [InlineData("HeadingBrush", "SidePanelBrush", 4.5)]
    [InlineData("ErrorBrush", "ErrorBackgroundBrush", 4.5)]
    [InlineData("WarningBrush", "WarningBackgroundBrush", 4.5)]
    [InlineData("SuccessBrush", "ContentBrush", 4.5)]
    [InlineData("AccentBrush", "ContentBrush", 4.5)]
    [InlineData("SideStripSelectedForeground", "AccentBrush", 4.5)]
    [InlineData("SideStripForeground", "SideStripBrush", 4.5)]
    [InlineData("StatusMutedForeground", "StatusBrush", 4.5)]
    [InlineData("DiffRemovedForeground", "DiffRemovedBrush", 4.5)]
    [InlineData("DiffAddedForeground", "DiffAddedBrush", 4.5)]
    [InlineData("DiffTextBrush", "DiffRemovedBrush", 4.5)]
    [InlineData("LineNumberBrush", "CodeBrush", 4.5)]
    [InlineData("DangerForeground", "DangerBrush", 4.5)]
    [InlineData("BadgeForeground", "StateFailedBrush", 4.5)]
    [InlineData("StateConnectedBrush", "MainTabBrush", 3)]
    [InlineData("StateEndedBrush", "MainTabBrush", 3)]
    [InlineData("StateFailedBrush", "MainTabBrush", 3)]
    [InlineData("FieldBorderBrush", "ContentBrush", 3)]
    [InlineData("ButtonBorderBrush", "ContentBrush", 3)]
    [InlineData("ScrollThumbBrush", "ScrollTrackBrush", 3)]
    public void DarkColorsAreReadable(string text, string background, double minimum)
    {
        var dark = Colors("ThemeDark.xaml");
        Assert.True(Contrast(dark[text], dark[background]) >= minimum, $"{text} sur {background} : {Contrast(dark[text], dark[background]):0.00}:1");
    }

    /// <summary>
    /// Ligne sélectionnée d'un arbre ou d'un tableau : ses textes, secondaires compris (TreeNote, colonnes « Résultat »),
    /// prennent la couleur du texte de la sélection, lisible sur son fond avec ou sans le focus. Thème clair avec le focus :
    /// couleurs de sélection de Windows, que ZillaTerm ne change pas. Et « ! » de la pastille rouge en thème clair.
    /// </summary>
    [Theory]
    [InlineData("Theme.xaml", "InactiveSelectionHighlightTextBrushKey", "InactiveSelectionHighlightBrushKey")]
    [InlineData("ThemeDark.xaml", "InactiveSelectionHighlightTextBrushKey", "InactiveSelectionHighlightBrushKey")]
    [InlineData("ThemeDark.xaml", "HighlightTextBrushKey", "HighlightBrushKey")]
    [InlineData("Theme.xaml", "BadgeForeground", "StateFailedBrush")]
    public void SelectionAndBadgeTextsAreReadable(string file, string text, string background)
    {
        var colors = Colors(file, systemKeys: true);
        Assert.True(Contrast(colors[text], colors[background]) >= 4.5,
            $"{file} : {text} sur {background} : {Contrast(colors[text], colors[background]):0.00}:1");
    }

    /// <summary>
    /// Couleurs des pinceaux de premier niveau d'un dictionnaire (clé → « #RRGGBB ») ; avec <paramref name="systemKeys"/>,
    /// les couleurs système redéfinies aussi (« {x:Static SystemColors.HighlightBrushKey} » → « HighlightBrushKey »).
    /// </summary>
    private static Dictionary<string, string> Colors(string file, bool systemKeys = false)
    {
        const string system = "{x:Static SystemColors.";
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var theme = XDocument.Load(Path.Combine(AccessKeysTests.RepositoryRoot(), "src", "ZillaTerm.App", file)).Root!;
        return theme.Elements()
            .Where(e => e.Name == wpf + "SolidColorBrush" || e.Name == wpf + "LinearGradientBrush")
            .Select(e => (Key: (string?)e.Attribute(xaml + "Key"), Color: (string?)e.Attribute("Color") ?? ""))
            .Where(e => e.Key is not null && (!e.Key.StartsWith('{') || (systemKeys && e.Key.StartsWith(system, StringComparison.Ordinal))))
            .ToDictionary(e => e.Key!.StartsWith('{') ? e.Key[system.Length..^1] : e.Key!, e => e.Color);
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string color)
        {
            var hex = color[^6..];
            double Channel(int i)
            {
                var c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
        }

        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }
}
