namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Copie d'une zone de l'écran à un autre endroit du même écran (défilement, fenêtre déplacée) : le rectangle
/// (<see cref="SourceX"/>, <see cref="SourceY"/>, <see cref="Width"/> × <see cref="Height"/>) reparaît en
/// (<see cref="X"/>, <see cref="Y"/>), comme si la source était lue avant que la destination ne soit écrite (les deux
/// peuvent se chevaucher). Plusieurs copies s'appliquent l'une après l'autre.
/// </summary>
public readonly record struct ScreenMove(int SourceX, int SourceY, int Width, int Height, int X, int Y)
{
    /// <summary>Taille en octets d'une copie dans un enregistrement d'écran ou dans le flux du client FreeRDP.</summary>
    public const int Length = 12;

    /// <summary>Source et destination dans un écran de cette taille, et quelque chose de déplacé.</summary>
    public bool FitsIn(int width, int height) =>
        Width > 0 && Height > 0 && SourceX >= 0 && SourceY >= 0 && X >= 0 && Y >= 0
        && SourceX + Width <= width && X + Width <= width && SourceY + Height <= height && Y + Height <= height
        && (SourceX != X || SourceY != Y);

    /// <summary>Applique la copie à un écran de <paramref name="pitch"/> pixels par ligne (en place).</summary>
    public void Apply<T>(Span<T> screen, int pitch)
        where T : unmanaged
    {
        // Lignes prises avant d'être recouvertes : du bas vers le haut quand le contenu descend. Dans une ligne, CopyTo
        // tient compte du chevauchement.
        if (Y > SourceY)
        {
            for (int row = Height - 1; row >= 0; row--)
            {
                screen.Slice(((SourceY + row) * pitch) + SourceX, Width).CopyTo(screen.Slice(((Y + row) * pitch) + X, Width));
            }
        }
        else
        {
            for (int row = 0; row < Height; row++)
            {
                screen.Slice(((SourceY + row) * pitch) + SourceX, Width).CopyTo(screen.Slice(((Y + row) * pitch) + X, Width));
            }
        }
    }
}
