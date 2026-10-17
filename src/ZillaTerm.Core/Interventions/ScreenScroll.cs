using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Bande de l'écran qui a défilé : les lignes [<see cref="Top"/>, <see cref="Top"/> + <see cref="Height"/>) des colonnes
/// [<see cref="Left"/>, <see cref="Left"/> + <see cref="Width"/>) reprennent celles de l'écran d'avant, décalées de
/// <see cref="Shift"/> lignes (positif : le contenu monte, comme un journal qui avance).
/// </summary>
internal readonly record struct ScreenScrollCopy(int Left, int Top, int Width, int Height, int Shift);

/// <summary>
/// Défilement d'un écran (terminal, journal, liste) reconnu entre deux images : copier une bande de l'écran d'avant,
/// décalée, plutôt que de garder à nouveau toutes ses tuiles. Sans perte : seules des lignes identiques au pixel près
/// sont reprises, et ce qui reste différent part en tuiles comme avant.
/// </summary>
/// <remarks>
/// L'écran est découpé en bandes verticales de la largeur d'une tuile : une fenêtre qui défile au milieu d'un bureau
/// immobile couvre des bandes entières, ses bords restent en tuiles. Dans chaque bande modifiée, les lignes changées votent
/// pour le décalage qui les retrouve dans l'écran d'avant (lignes unies écartées : elles se retrouvent partout) ; la plus
/// longue suite de lignes identiques avec le décalage élu est copiée si elle reprend assez de lignes changées.
/// </remarks>
internal static class ScreenScroll
{
    /// <summary>Largeur des bandes (pixels).</summary>
    public const int Band = ScreenTileEncoder.TileSize;

    /// <summary>Lignes changées qu'une copie doit reprendre au moins, sinon les tuiles suffisent.</summary>
    public const int MinRows = 16;

    /// <summary>Lignes de l'écran d'avant examinées au plus pour une même empreinte (motif répété).</summary>
    private const int MaxCandidates = 8;

    /// <summary>
    /// Copies qui rapprochent <paramref name="before"/> de <paramref name="after"/> (une par bande au plus) ; vide si rien
    /// n'a défilé. Les deux écrans ont <paramref name="pitch"/> pixels par ligne, dont <paramref name="width"/> utiles.
    /// </summary>
    public static List<ScreenScrollCopy> Detect<T>(ReadOnlySpan<T> before, ReadOnlySpan<T> after, int width, int height, int pitch)
        where T : unmanaged, IEquatable<T>
    {
        var copies = new List<ScreenScrollCopy>();
        var hashBefore = new ulong[height];
        var hashAfter = new ulong[height];
        var flat = new bool[height];
        for (int left = 0; left < width; left += Band)
        {
            int w = Math.Min(Band, width - left);
            bool changed = false;
            for (int y = 0; y < height; y++)
            {
                var a = after.Slice((y * pitch) + left, w);
                hashBefore[y] = Hash(before.Slice((y * pitch) + left, w));
                hashAfter[y] = Hash(a);
                flat[y] = a.IndexOfAnyExcept(a[0]) < 0;
                changed |= hashBefore[y] != hashAfter[y];
            }

            if (changed && Find(before, after, hashBefore, hashAfter, flat, left, w, height, pitch) is { } copy)
            {
                copies.Add(copy);
            }
        }

        return copies;
    }

    /// <summary>Applique une copie à l'écran (en place : les lignes sont prises avant d'être recouvertes).</summary>
    public static void Apply<T>(Span<T> screen, int pitch, ScreenScrollCopy copy)
        where T : unmanaged
    {
        if (copy.Shift > 0)
        {
            for (int y = copy.Top; y < copy.Top + copy.Height; y++)
            {
                screen.Slice(((y + copy.Shift) * pitch) + copy.Left, copy.Width).CopyTo(screen.Slice((y * pitch) + copy.Left, copy.Width));
            }
        }
        else
        {
            for (int y = copy.Top + copy.Height - 1; y >= copy.Top; y--)
            {
                screen.Slice(((y + copy.Shift) * pitch) + copy.Left, copy.Width).CopyTo(screen.Slice((y * pitch) + copy.Left, copy.Width));
            }
        }
    }

    private static ScreenScrollCopy? Find<T>(ReadOnlySpan<T> before, ReadOnlySpan<T> after, ulong[] hashBefore, ulong[] hashAfter,
        bool[] flat, int left, int w, int height, int pitch)
        where T : unmanaged, IEquatable<T>
    {
        // Où chaque ligne (non unie) de l'écran d'avant se trouvait.
        var rows = new Dictionary<ulong, List<int>>();
        for (int y = 0; y < height; y++)
        {
            var b = before.Slice((y * pitch) + left, w);
            if (b.IndexOfAnyExcept(b[0]) < 0)
            {
                continue;
            }

            if (!rows.TryGetValue(hashBefore[y], out var list))
            {
                rows[hashBefore[y]] = list = [];
            }

            if (list.Count < MaxCandidates)
            {
                list.Add(y);
            }
        }

        var votes = new Dictionary<int, int>();
        for (int y = 0; y < height; y++)
        {
            if (hashAfter[y] == hashBefore[y] || flat[y] || !rows.TryGetValue(hashAfter[y], out var list))
            {
                continue;
            }

            foreach (int from in list)
            {
                if (from != y)
                {
                    votes[from - y] = votes.GetValueOrDefault(from - y) + 1;
                }
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        int shift = votes.MaxBy(vote => (vote.Value, -Math.Abs(vote.Key))).Key;
        // Plus longue suite de lignes identiques à celles d'avant décalées, comptée en lignes changées qu'elle reprend.
        int start = Math.Max(0, -shift), end = Math.Min(height, height - shift);
        int bestTop = 0, bestHeight = 0, bestGain = 0;
        int runTop = start, gain = 0;
        for (int y = start; y <= end; y++)
        {
            bool same = y < end && after.Slice((y * pitch) + left, w).SequenceEqual(before.Slice(((y + shift) * pitch) + left, w));
            if (same)
            {
                gain += hashAfter[y] != hashBefore[y] ? 1 : 0;
                continue;
            }

            if (gain > bestGain)
            {
                (bestTop, bestHeight, bestGain) = (runTop, y - runTop, gain);
            }

            runTop = y + 1;
            gain = 0;
        }

        return bestGain >= MinRows ? new ScreenScrollCopy(left, bestTop, w, bestHeight, shift) : null;
    }

    /// <summary>Empreinte d'un bout de ligne : égale pour des pixels égaux, presque toujours différente sinon.</summary>
    private static ulong Hash<T>(ReadOnlySpan<T> pixels)
        where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(pixels);
        ulong hash = 0x9E3779B97F4A7C15UL ^ (ulong)bytes.Length;
        while (bytes.Length >= 8)
        {
            hash = BitOperations.RotateLeft((hash ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes)) * 0x9E3779B97F4A7C15UL, 31);
            bytes = bytes[8..];
        }

        while (bytes.Length >= 2)
        {
            hash = BitOperations.RotateLeft((hash ^ BinaryPrimitives.ReadUInt16LittleEndian(bytes)) * 0x9E3779B97F4A7C15UL, 31);
            bytes = bytes[2..];
        }

        return hash ^ (hash >> 32);
    }
}
