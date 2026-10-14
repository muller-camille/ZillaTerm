using Avalonia;
using Avalonia.Controls;

namespace ZillaTerm.App.Views;

/// <summary>
/// Place une fenêtre détachée à l'endroit où l'onglet a été lâché, sur n'importe quel écran. La position est donnée en
/// pixels de l'écran visé : convertie avec l'échelle de la fenêtre principale, elle serait fausse sur un écran d'une
/// autre échelle (100 % d'un côté, 150 % de l'autre).
/// </summary>
internal static class ScreenPlacement
{
    /// <param name="devicePoint">Point lâché, en pixels de l'écran.</param>
    /// <param name="dx">Décalage horizontal, en pixels indépendants (mis à l'échelle de l'écran visé).</param>
    /// <param name="dy">Décalage vertical, en pixels indépendants.</param>
    public static void OpenAt(Window window, PixelPoint devicePoint, double dx, double dy)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        double scale = window.Screens.ScreenFromPoint(devicePoint)?.Scaling ?? 1.0;
        window.Position = new PixelPoint((int)Math.Round(devicePoint.X + dx * scale), (int)Math.Round(devicePoint.Y + dy * scale));
    }

    /// <summary>Fenêtre ouverte un peu en retrait de <paramref name="owner"/> (décalage en pixels indépendants).</summary>
    public static void OpenBeside(Window window, Window owner, double offset = 40)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        int shift = (int)Math.Round(offset * owner.RenderScaling);
        window.Position = new PixelPoint(owner.Position.X + shift, owner.Position.Y + shift);
    }
}
