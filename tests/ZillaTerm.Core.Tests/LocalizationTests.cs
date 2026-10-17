using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Tests;

/// <summary>Traductions : fichiers .resx complets et cohérents, choix de la langue.</summary>
public partial class LocalizationTests
{
    private static readonly string[] Translations = ["fr", "it"];

    public static TheoryData<string> NeutralResxFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.resx", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                     .Where(f => Path.GetFileNameWithoutExtension(f).IndexOf('.') < 0)
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(RepositoryRoot(), file));
        }

        return data;
    }

    [Fact]
    public void BothProjectsHaveResources() => Assert.Equal(2, NeutralResxFiles().Count);

    [Theory]
    [MemberData(nameof(NeutralResxFiles))]
    public void Translations_HaveTheSameKeysPlaceholdersAndAccessKeys(string neutralFile)
    {
        var neutral = Load(Path.Combine(RepositoryRoot(), neutralFile));
        Assert.NotEmpty(neutral);
        foreach (var language in Translations)
        {
            var translated = Load(Path.ChangeExtension(Path.Combine(RepositoryRoot(), neutralFile), $".{language}.resx"));
            Assert.Equal(neutral.Keys.Order(StringComparer.Ordinal), translated.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, english) in neutral)
            {
                var text = translated[key];
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}/{key} : texte vide");
                Assert.True(Placeholders(english).SetEquals(Placeholders(text)), $"{language}/{key} : paramètres {{n}} différents");
                Assert.True(english.Count(c => c == '_') == text.Count(c => c == '_'), $"{language}/{key} : touche d'accès « _ » différente");
            }
        }
    }

    /// <summary>
    /// Dans un même menu, deux éléments ne partagent pas une touche d'accès : sinon la touche passe de l'un à l'autre au
    /// lieu d'agir (et peut finir sur « Fermer l'onglet »).
    /// </summary>
    [Theory]
    [InlineData("MenuTerminalCopy", "MenuTerminalPaste", "MenuTerminalSelectAll", "MenuTerminalSearch", "MenuTerminalSave",
        "MenuTerminalClear", "MenuTerminalFont", "MenuTabReconnect", "MenuTabDuplicate", "MenuTabDetach", "MenuTabAddParallel",
        "MenuTabClose")]
    [InlineData("MenuTerminalCopy", "MenuTerminalPaste", "MenuTerminalSelectAll", "MenuTerminalSearch", "MenuTerminalSave",
        "MenuTerminalClear", "MenuTerminalFont", "MenuTabReconnect", "MenuTabDuplicate", "MenuTabDetach", "MenuTabRemoveParallel",
        "MenuTabClose")]
    [InlineData("MenuTabReconnect", "MenuTabDuplicate", "MenuTabDetach", "MenuTabAddParallel", "MenuTerminalSearch",
        "MenuTerminalSave", "MenuTabClose", "MenuTabCloseOthers")]
    [InlineData("MenuFontBigger", "MenuFontSmaller", "MenuFontDefault")]
    public void MenuItems_HaveDistinctAccessKeys(params string[] keys)
    {
        var neutral = Path.Combine(RepositoryRoot(), "src", "ZillaTerm.App", "Localization", "Strings.resx");
        foreach (var file in new[] { neutral }.Concat(Translations.Select(l => Path.ChangeExtension(neutral, $".{l}.resx"))))
        {
            var texts = Load(file);
            var accessKeys = keys.Select(k => char.ToLowerInvariant(texts[k][texts[k].IndexOf('_') + 1])).ToList();
            Assert.True(accessKeys.Distinct().Count() == keys.Length,
                $"{Path.GetFileName(file)} : touches d'accès en double ({string.Join(", ", keys.Zip(accessKeys, (k, c) => $"{k}={c}"))})");
        }
    }

    [Theory]
    [MemberData(nameof(NeutralResxFiles))]
    public void DesignerClass_MatchesTheResx(string neutralFile)
    {
        // Le .Designer.cs est généré par Visual Studio : il doit exposer exactement les clés du .resx.
        var path = Path.Combine(RepositoryRoot(), neutralFile);
        var designer = File.ReadAllText(Path.ChangeExtension(path, ".Designer.cs"));
        var properties = DesignerProperty().Matches(designer).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Load(path).Keys.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("fr-FR", "Réseau")]
    [InlineData("it-IT", "Rete")]
    [InlineData("en-US", "Network")]
    [InlineData("de-DE", "Network")]
    public void CoreStrings_FollowTheInterfaceLanguage(string culture, string expected)
    {
        using var _ = UiCulture.Use(culture);

        Assert.Equal(expected, CoreStrings.KindNetwork);
    }

    [Theory]
    [InlineData("", "fr-FR", "fr-FR")]
    [InlineData("", "fr-CA", "fr-CA")]
    [InlineData("", "it-CH", "it-CH")]
    [InlineData("", "de-DE", "en")]
    [InlineData("", "en-GB", "en-GB")]
    [InlineData("it", "fr-FR", "it")]
    [InlineData(" EN ", "fr-FR", "en")]
    [InlineData("fr", "fr-BE", "fr-BE")]
    [InlineData("de", "fr-FR", "fr-FR")]
    [InlineData(null, "es-ES", "en")]
    public void Resolve_PrefersSettingThenWindowsThenEnglish(string? setting, string system, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(setting, CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void Supported_HaveNativeNames()
    {
        Assert.Equal(["fr", "en", "it"], UiLanguage.Supported);
        Assert.Equal(["Français", "English", "Italiano"], UiLanguage.Supported.Select(UiLanguage.NativeName));
    }

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ZillaTerm.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Racine du dépôt introuvable.");
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"public static string (\w+) \{")]
    private static partial Regex DesignerProperty();
}
