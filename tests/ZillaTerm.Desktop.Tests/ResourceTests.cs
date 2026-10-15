using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace ZillaTerm.Desktop.Tests;

/// <summary>
/// Couleurs, icônes et thèmes de contrôles : chaque ressource nommée par le XAML ou le code existe dans le thème clair
/// et dans le thème sombre. Une ressource absente ne se voit pas à la compilation : en DynamicResource, l'élément reste
/// sans couleur ou sans icône dans un des deux thèmes.
/// </summary>
public sealed partial class ResourceTests
{
    [AvaloniaFact]
    public void EveryNamedResourceExistsInBothThemes()
    {
        var app = Avalonia.Application.Current!;
        var missing = new List<string>();
        foreach (var path in Repository.DesktopFiles("*.axaml").Concat(Repository.DesktopFiles("*.cs")))
        {
            // Les commentaires du XAML citent parfois l'ancienne syntaxe WPF ({StaticResource SectionTitle}).
            string text = XmlComment().Replace(File.ReadAllText(path), "");
            var local = LocalKey().Matches(text).Concat(LocalKey().Matches(ViewXaml(path)))
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var used = (path.EndsWith(".axaml", StringComparison.Ordinal) ? XamlReference() : CodeReference()).Matches(text)
                .Select(m => m.Groups[1].Value)
                .Where(key => !local.Contains(key))
                .Distinct(StringComparer.Ordinal);
            foreach (var key in used)
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    if (!app.TryGetResource(key, theme, out _))
                    {
                        missing.Add($"{Path.GetRelativePath(Repository.Desktop, path)} : {key} ({theme})");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// XAML de la vue d'un fichier de code (MainWindow.Accounts.cs → MainWindow.axaml) : les ressources de la fenêtre
    /// que son code cherche par leur nom. Vide pour un fichier XAML ou un code sans vue.
    /// </summary>
    private static string ViewXaml(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.Ordinal))
        {
            return "";
        }

        string xaml = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path).Split('.')[0] + ".axaml");
        return File.Exists(xaml) ? File.ReadAllText(xaml) : "";
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    /// <summary>{StaticResource X} et {DynamicResource X} du XAML.</summary>
    [GeneratedRegex(@"\{(?:StaticResource|DynamicResource)\s+([\w.]+)\s*\}")]
    private static partial Regex XamlReference();

    /// <summary>Ressources nommées par le code (thème du contrôle, icône, pinceau).</summary>
    [GeneratedRegex(@"(?:TryFindResource|GetResourceObservable|FindResource|Palette\.Icon|Palette\.Brush)\(\s*""([\w.]+)""")]
    private static partial Regex CodeReference();

    /// <summary>Ressource définie dans le fichier même (Resources d'une fenêtre ou d'un modèle).</summary>
    [GeneratedRegex(@"x:Key=""([\w.]+)""")]
    private static partial Regex LocalKey();
}
