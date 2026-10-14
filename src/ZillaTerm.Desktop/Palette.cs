using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using ZillaTerm.Core;

namespace ZillaTerm.App;

/// <summary>
/// Thème de l'interface : clair, sombre, ou celui du système (réglage). Les couleurs et les icônes des deux thèmes sont
/// dans Themes/Palette.axaml, référencées en DynamicResource : les fenêtres ouvertes suivent sans redémarrage.
/// </summary>
internal static class Palette
{
    /// <summary>Thème sombre affiché.</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Couleurs changées : pour ce qui n'est pas en DynamicResource (suivi tail -f, terminal).</summary>
    public static event Action? Changed;

    /// <summary>Applique le thème choisi maintenant, puis suit les changements (réglage ou thème du système).</summary>
    public static void Follow(Application app, AppTheme choice)
    {
        Choose(app, choice);
        app.ActualThemeVariantChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>Thème choisi dans les Paramètres, appliqué aux fenêtres ouvertes.</summary>
    public static void Choose(Application app, AppTheme choice) => app.RequestedThemeVariant = choice switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Icône de Themes/Palette.axaml créée par le code, qui suit le changement de thème.</summary>
    internal static Image Icon(string key, double size)
    {
        var image = new Image { Width = size, Height = size };
        image[!Image.SourceProperty] = image.GetResourceObservable(key).ToBinding();
        return image;
    }

    /// <summary>Texte secondaire créé par le code, dans la couleur du thème en vigueur.</summary>
    internal static TextBlock Muted(TextBlock text)
    {
        text.Classes.Add("Muted");
        return text;
    }

    /// <summary>Pinceau du thème en vigueur (pour le code qui dessine lui-même : terminal, courbes).</summary>
    internal static IBrush Brush(string key, IBrush fallback) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
