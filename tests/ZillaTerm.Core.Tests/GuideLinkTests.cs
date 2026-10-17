using System.Text.RegularExpressions;

namespace ZillaTerm.Core.Tests;

/// <summary>« En savoir plus » des Paramètres : guide en ligne de la version, dans la langue, à la section de la page.</summary>
public sealed partial class GuideLinkTests
{
    [Theory]
    [InlineData("fr", "guide.fr.md")]
    [InlineData("it", "guide.it.md")]
    [InlineData("en", "guide.md")]
    [InlineData("de", "guide.md")]
    public void GuideOfTheVersionInTheLanguage(string language, string file) =>
        Assert.Equal($"https://github.com/muller-camille/ZillaTerm/blob/v0.39.0/docs/{file}#settings-files",
            GuideLink.Url(language, new Version(0, 39, 0, 0), GuideLink.SettingsPrefix + "files"));

    /// <summary>Chaque page des Paramètres a son ancre dans les trois guides (sinon le lien ouvre le haut du guide).</summary>
    [Fact]
    public void EverySettingsPageHasItsAnchorInEachGuide()
    {
        var root = LocalizationTests.RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ZillaTerm.App", "Views", "SettingsDialog.xaml"));
        var pages = TabTag().Matches(xaml).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, pages.Count);
        Assert.Equal(pages.Count, Regex.Matches(xaml, "<TabItem ").Count);
        foreach (var guide in new[] { "guide.md", "guide.fr.md", "guide.it.md" })
        {
            var text = File.ReadAllText(Path.Combine(root, "docs", guide));
            Assert.All(pages, page => Assert.Contains($"<a id=\"{GuideLink.SettingsPrefix}{page}\"></a>", text));
        }
    }

    [GeneratedRegex("<TabItem x:Name=\"\\w+\" Tag=\"([a-z]+)\"")]
    private static partial Regex TabTag();
}
