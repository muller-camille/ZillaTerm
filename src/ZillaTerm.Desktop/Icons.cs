using Avalonia;
using Avalonia.Media;

namespace ZillaTerm.App;

/// <summary>Icônes de Themes/Palette.axaml lues par le code (convertisseurs), dans le thème en vigueur.</summary>
internal static class Icons
{
    public static IImage? Find(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IImage : null;
}
