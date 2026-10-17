using System.Globalization;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Bilan de l'écran d'une session enregistré, par période (<see cref="Period"/>), pour le journal de débogage : captures,
/// images gardées (dont complètes), poids, tuiles nouvelles, copies d'écran (défilement, fenêtre déplacée). Un écran immobile ne garde rien : du poids sur un écran
/// immobile montre une image qui change sans que cela se voie (image compressée avec pertes qui s'affine, par exemple).
/// </summary>
public sealed class ScreenRecordingStats
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    private DateTime? _start;
    private int _captures;
    private int _frames;
    private int _keys;
    private long _newTiles;
    private long _moves;
    private long _bytes;
    private long _largest;
    private int _width;
    private int _height;

    /// <summary>Une capture de l'écran a été encodée, qu'elle ait donné une image ou non.</summary>
    public void Captured(DateTime now)
    {
        _start ??= now;
        _captures++;
    }

    /// <summary>Image gardée dans le journal.</summary>
    public void Kept(ScreenFrame frame)
    {
        _frames++;
        _keys += frame.Header.Key ? 1 : 0;
        _newTiles += frame.Header.NewTiles;
        _moves += frame.Header.Moves;
        _bytes += frame.Data.Length;
        _largest = Math.Max(_largest, frame.Data.Length);
        (_width, _height) = (frame.Header.Width, frame.Header.Height);
    }

    /// <summary>
    /// Bilan depuis le précédent, une fois la période écoulée (ou tout de suite avec <paramref name="final"/>, à la fin de
    /// l'enregistrement) ; null s'il n'est pas encore temps ou s'il n'y a eu aucune capture. Les compteurs repartent de zéro.
    /// </summary>
    public string? Report(DateTime now, bool final = false)
    {
        if (_start is not { } start || _captures == 0 || (!final && now - start < Period))
        {
            return null;
        }

        var c = CultureInfo.InvariantCulture;
        var size = _width > 0 ? string.Create(c, $", écran {_width}×{_height}") : "";
        var line = string.Create(c,
            $"{(now - start).TotalSeconds:0} s : {_captures} capture(s), {_frames} image(s) gardée(s) dont {_keys} complète(s), " +
            $"{Bytes(_bytes)} (plus grosse {Bytes(_largest)}), {_newTiles} tuile(s) nouvelle(s), {_moves} copie(s) d'écran{size}");
        _start = null;
        _captures = _frames = _keys = 0;
        _newTiles = _moves = _bytes = _largest = 0;
        return line;
    }

    /// <summary>Taille lisible dans le journal de débogage : octets, Ko ou Mo.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} o"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} Ko"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.##} Mo"),
    };
}
