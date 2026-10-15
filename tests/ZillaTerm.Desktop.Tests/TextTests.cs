using System.Reflection;
using System.Text.RegularExpressions;
using ZillaTerm.App.Localization;

namespace ZillaTerm.Desktop.Tests;

/// <summary>
/// Textes : traduits dans les trois langues, et ceux qui parlent de Windows (compte Windows, Bloc-notes, pare-feu…)
/// pris dans <see cref="PlatformText"/>, qui donne leur variante neutre (« …Unix ») sous Linux et macOS.
/// </summary>
public sealed partial class TextTests
{
    [Fact]
    public void EveryTextIsTranslated()
    {
        var english = AccessKeysTests.Texts("");
        var problems = new List<string>();
        foreach (var suffix in new[] { ".fr", ".it" })
        {
            var texts = AccessKeysTests.Texts(suffix);
            problems.AddRange(english.Keys.Where(k => !texts.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value))
                .Select(k => $"{k} : absent ou vide dans Strings{suffix}.resx"));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void WindowsSpecificTextsGoThroughPlatformText()
    {
        var keys = AccessKeysTests.Texts("").Keys.ToHashSet(StringComparer.Ordinal);
        var windowsOnly = keys.Where(k => k.EndsWith("Unix", StringComparison.Ordinal) && keys.Contains(k[..^4]))
            .Select(k => k[..^4])
            .ToHashSet(StringComparer.Ordinal);
        var platformText = typeof(PlatformText).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => p.Name).ToHashSet();
        var problems = windowsOnly.Where(k => !platformText.Contains(k)).Select(k => $"PlatformText.{k} manque (Strings.{k}Unix existe)").ToList();
        foreach (var path in Repository.DesktopFiles("*.axaml").Concat(Repository.DesktopFiles("*.cs")))
        {
            if (Path.GetFileName(path) == "PlatformText.cs")
            {
                continue;
            }

            foreach (Match m in StringsReference().Matches(File.ReadAllText(path)))
            {
                if (windowsOnly.Contains(m.Groups[1].Value))
                {
                    problems.Add($"{Path.GetRelativePath(Repository.Desktop, path)} : Strings.{m.Groups[1].Value} → PlatformText.{m.Groups[1].Value}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.NotEmpty(windowsOnly);
    }

    [GeneratedRegex(@"\bStrings\.(\w+)\b")]
    private static partial Regex StringsReference();
}
