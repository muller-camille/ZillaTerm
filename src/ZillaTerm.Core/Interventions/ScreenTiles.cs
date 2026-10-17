using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Affichage d'une session en pause (le serveur n'envoie plus l'image, la connexion reste ouverte), marqué dans son
/// enregistrement d'écran (<see cref="ScreenTileEncoder.Mark"/>).
/// </summary>
public enum ScreenPause
{
    /// <summary>Affichage reçu (ou repris).</summary>
    None,

    /// <summary>Session que personne ne pouvait voir : onglet en arrière-plan, ZillaTerm réduit, Windows verrouillé.</summary>
    Hidden,

    /// <summary>Session affichée, poste inactif (ni clavier ni souris).</summary>
    Idle,
}

/// <summary>En-tête d'une image d'écran enregistrée (<see cref="ScreenTileEncoder"/>).</summary>
/// <param name="Stream">Enregistrement d'écran (une session Bureau à distance) auquel l'image appartient.</param>
/// <param name="Sequence">
/// Numéro de l'image dans son enregistrement (0, 1, 2…) : une image manquante (bloc du journal abîmé) se voit, et l'écran
/// n'est repris qu'à l'image clé suivante.
/// </param>
/// <param name="Activity">Part de l'écran modifiée depuis l'image précédente, en millièmes.</param>
/// <param name="Pause">
/// Affichage en pause depuis cette image (sans tuile), jusqu'à l'image suivante ; <see cref="ScreenPause.None"/> pour
/// une image ordinaire. Dans l'octet des drapeaux, après celui de l'image clé : un lecteur qui ne le connaît pas y voit une
/// image sans changement.
/// </param>
/// <param name="Moves">Copies d'écran (défilement, fenêtre déplacée) appliquées à l'écran d'avant, avant les tuiles.</param>
/// <param name="TrueColor">
/// Tuiles nouvelles en 24 bits ; sinon en RGB565, sans perte : chacun de leurs pixels est un pixel 16 bits élargi (session
/// en 16 bits).
/// </param>
/// <param name="Format">3 : écrit aujourd'hui ; 2 : jusqu'à la 0.41 (RGB565, zlib par image, sans copie), relu seulement.</param>
public readonly record struct ScreenFrameHeader(
    int Stream, int Sequence, int Width, int Height, bool Key, int TileSize, int Activity, int FirstNewTile, int NewTiles, int Placements,
    ScreenPause Pause = ScreenPause.None, int Moves = 0, bool TrueColor = false, int Format = 3)
{
    /// <summary>« ZTS3 » : image d'écran, format 3.</summary>
    public static ReadOnlySpan<byte> Magic => "ZTS3"u8;

    /// <summary>« ZTS2 » : image d'écran, format 2 (journaux d'avant la 0.42).</summary>
    public static ReadOnlySpan<byte> Magic2 => "ZTS2"u8;

    /// <summary>Longueur d'un en-tête au format 3.</summary>
    public const int Length3 = 36;

    /// <summary>Longueur d'un en-tête au format 2.</summary>
    public const int Length2 = 32;

    /// <summary>Longueur de cet en-tête : le contenu compressé suit.</summary>
    public int Length => Format == 2 ? Length2 : Length3;

    /// <summary>Tuiles de la grille d'un écran de cette taille.</summary>
    public int Tiles => ((Width + TileSize - 1) / TileSize) * ((Height + TileSize - 1) / TileSize);

    /// <summary>Octets par pixel des tuiles nouvelles.</summary>
    public int BytesPerPixel => TrueColor ? 3 : 2;

    /// <summary>Longueur du contenu une fois décompressé : copies, positions, bases, puis tuiles nouvelles.</summary>
    public long BodyLength =>
        ((long)ScreenMove.Length * Moves) + (8L * Placements) + (4L * NewTiles) + ((long)TileSize * TileSize * BytesPerPixel * NewTiles);

    /// <summary>Lit l'en-tête ; faux si ce n'est pas une image d'écran de l'un de ces formats.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out ScreenFrameHeader header)
    {
        header = default;
        int format = data.Length < 4 ? 0 : data[..4].SequenceEqual(Magic) ? 3 : data[..4].SequenceEqual(Magic2) ? 2 : 0;
        if (format == 0 || data.Length < (format == 2 ? Length2 : Length3))
        {
            return false;
        }

        int flags = data[16];
        header = new ScreenFrameHeader(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            (flags & 1) != 0,
            data[17],
            BinaryPrimitives.ReadUInt16LittleEndian(data[18..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
            (ScreenPause)((flags >> 1) & 3),
            format == 3 ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[32..]), int.MaxValue) : 0,
            format == 3 && (flags & 8) != 0,
            format);
        // Écran pas plus grand que ce que l'encodeur accepte ; chaque tuile nouvelle est posée au moins une fois, et une image
        // ne pose pas plus de tuiles que la grille n'en a. Une pause n'est ni une image clé, ni une image avec des tuiles ou
        // des copies ; une image clé repart d'un écran vide, sans copie.
        return header.Width is > 0 and <= ScreenTileEncoder.MaxSide && header.Height is > 0 and <= ScreenTileEncoder.MaxSide
               && header.TileSize == ScreenTileEncoder.TileSize && header.Activity <= 1000
               && header.Sequence >= 0 && header.FirstNewTile >= 0 && header.NewTiles >= 0 && header.Placements >= 0
               && header.NewTiles <= header.Placements && header.Placements <= header.Tiles
               && header.Moves is >= 0 and <= ScreenTileEncoder.MaxMoves && (format == 2 || flags < 16)
               && header.Pause is ScreenPause.None or ScreenPause.Hidden or ScreenPause.Idle
               && (header.Pause == ScreenPause.None || (!header.Key && header.Placements == 0 && header.Moves == 0))
               && (!header.Key || header.Moves == 0);
    }

    /// <summary>Écrit l'en-tête au format 3.</summary>
    internal void Write(Span<byte> data)
    {
        Magic.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], (uint)Stream);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], (uint)Sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(data[12..], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(data[14..], (ushort)Height);
        data[16] = (byte)((Key ? 1 : 0) | ((int)Pause << 1) | (TrueColor ? 8 : 0));
        data[17] = (byte)TileSize;
        BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)Activity);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)FirstNewTile);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], (uint)NewTiles);
        BinaryPrimitives.WriteUInt32LittleEndian(data[28..], (uint)Placements);
        BinaryPrimitives.WriteUInt32LittleEndian(data[32..], (uint)Moves);
    }
}

/// <summary>Image d'écran encodée, à écrire dans le journal (<see cref="InterventionKind.Screen"/>).</summary>
public sealed record ScreenFrame(byte[] Data, ScreenFrameHeader Header);

/// <summary>
/// Écran d'une session Bureau à distance enregistré comme le protocole RDP l'envoie : seules les zones modifiées, au pixel
/// près. Chaque capture est découpée en tuiles de 64×64 pixels ; seules les tuiles modifiées depuis l'image précédente
/// sont gardées, et une tuile déjà vue (même menu, même boîte de dialogue) n'est gardée qu'une fois, désignée ensuite par
/// son numéro. Une tuile peu modifiée (caractère tapé) est gardée en différence (XOR) avec ce qu'elle montrait : presque
/// des zéros. Un défilement ou une fenêtre déplacée est gardé comme une copie de l'écran d'avant (quelques octets) plutôt
/// que comme les tuiles qu'il a changées.
/// </summary>
/// <remarks>
/// <para>
/// Copies d'écran : celles que le client FreeRDP a reçues du serveur depuis la capture précédente
/// (<see cref="Encode"/>, <c>moves</c>), sinon un défilement reconnu entre les deux captures (<see cref="ScreenScroll"/>).
/// Elles ne sont gardées que si elles laissent moins de tuiles à garder : le reste part en tuiles comme avant.
/// </para>
/// <para>
/// Couleurs exactes : une image dont toutes les tuiles nouvelles sont des pixels 16 bits élargis (session en 16 bits, celle
/// des sessions PSM) les garde en RGB565 (2 octets par pixel), sinon en 24 bits (3 octets) : jamais de perte.
/// </para>
/// <para>
/// Seuil de bruit : une tuile dont au plus <see cref="NoiseThreshold"/> pixels changent (curseur texte, horloge) n'est
/// pas gardée tout de suite ; elle l'est dès que les changements cumulés le dépassent (la comparaison se fait avec ce
/// qui a été gardé), ou en rattrapage : une fois la tuile immobile pendant <see cref="CatchUpDelay"/> (le dernier
/// caractère tapé), au plus tard après <see cref="CatchUpMaxDelay"/> (une tuile qui bouge sans cesse).
/// </para>
/// <para>
/// Image clé (écran entier, cache de tuiles reparti d'elle) : à la première capture, après un changement de taille, à la
/// demande, et quand les images depuis la précédente pèsent <see cref="KeyFrameBytesFactor"/> fois sa taille (au plus
/// tôt après <see cref="KeyFrameMinInterval"/>), ou au plus tard après <see cref="KeyFrameMaxInterval"/> si l'écran a
/// changé. Un écran immobile n'en coûte aucune ; un lecteur va à une heure en rejouant depuis l'image clé précédente.
/// </para>
/// <para>
/// Format d'une image (petit-boutiste) : l'en-tête <see cref="ScreenFrameHeader"/> (36 octets), puis le contenu compressé
/// en Brotli. Le flux Brotli part de chaque image clé et se poursuit d'image en image, vidé après chacune (elle se
/// décompresse dès qu'elle est lue) : une image se compresse avec celles d'avant, depuis l'image clé, en mémoire. Contenu :
/// les copies (6 × 2 octets : source x, y, largeur, hauteur, destination x, y), les positions (numéro de tuile dans la
/// grille, numéro de tuile gardée : 2 × 4 octets chacune), la base de chaque tuile nouvelle (4 octets : <see cref="FullTile"/>
/// pour une tuile entière, ou le numéro de la tuile de la grille dont elle est la différence, avec l'écran d'avant cette
/// image, copies appliquées), puis les pixels des tuiles nouvelles (64 × 64, de 2 ou 3 octets : B, V, R), bords de l'écran
/// complétés par des zéros, numérotées à la suite de <see cref="ScreenFrameHeader.FirstNewTile"/>. Une pause n'a pas de
/// contenu.
/// </para>
/// </remarks>
public sealed class ScreenTileEncoder : IDisposable
{
    public const int TileSize = 64;

    private const int TilePixels = TileSize * TileSize;

    /// <summary>Base d'une tuile nouvelle gardée entière (pas en différence).</summary>
    public const uint FullTile = uint.MaxValue;

    /// <summary>Pixels modifiés au plus d'une tuile sans qu'elle soit gardée tout de suite (curseur texte, horloge).</summary>
    public const int NoiseThreshold = 32;

    /// <summary>
    /// Pixels modifiés au plus d'une tuile gardée en différence (un mot tapé) ; au-delà, elle l'est entière : une tuile
    /// très modifiée se compresse mieux entière qu'en différence.
    /// </summary>
    public const int DeltaMaxPixels = TilePixels / 16;

    /// <summary>Plus grand côté d'écran accepté (pixels).</summary>
    public const int MaxSide = 8192;

    /// <summary>Copies d'écran au plus dans une image.</summary>
    public const int MaxMoves = 64;

    /// <summary>
    /// Tuiles gardées au plus entre deux images clés (256 Mo chez celui qui relit) : au-delà, une image clé repart d'un
    /// cache vide. Deux fois la grille du plus grand écran : une image clé y tient toujours.
    /// </summary>
    public const int MaxTilesBetweenKeys = 2 * (MaxSide / TileSize) * (MaxSide / TileSize);

    /// <summary>Écart minimal entre deux images clés venues du poids des images.</summary>
    public static readonly TimeSpan KeyFrameMinInterval = TimeSpan.FromMinutes(5);

    /// <summary>Écart maximal entre deux images clés quand l'écran a changé entre-temps (reprise après une image perdue).</summary>
    public static readonly TimeSpan KeyFrameMaxInterval = TimeSpan.FromMinutes(30);

    /// <summary>Poids des images depuis l'image clé, en multiple de sa taille, qui en appelle une nouvelle.</summary>
    public const int KeyFrameBytesFactor = 4;

    /// <summary>Immobilité d'une tuile après laquelle son reste sous le seuil de bruit est gardé.</summary>
    public static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(3);

    /// <summary>Délai au plus avant qu'un reste sous le seuil de bruit soit gardé, même si la tuile bouge encore.</summary>
    public static readonly TimeSpan CatchUpMaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Qualité Brotli : une image clé d'un écran 1080p en un dixième de seconde environ (le zlib le plus fort d'avant la
    /// 0.42 y passait plus d'une seconde), plus compacte que les qualités plus rapides (mesuré).
    /// </summary>
    private const int BrotliQuality = 7;

    /// <summary>Fenêtre Brotli (2^24 : 16 Mo), assez pour une image clé d'un écran 4K en 24 bits.</summary>
    internal const int BrotliWindow = 24;

    private readonly int _stream;
    private readonly int _noise;
    private readonly Dictionary<UInt128, int> _ids = [];
    private int _nextId;
    private int _sequence;
    private int _width;
    private int _height;
    private int _columns;
    private int _rows;

    // Pixels 0x00RRGGBB, lignes de la largeur de la grille (bords complétés par des zéros).
    private uint[] _current = [];
    private uint[] _previous = [];
    private uint[] _predicted = [];
    private uint[]? _shown;

    // Par tuile : depuis quand un reste sous le seuil attend, et depuis quand la tuile n'a pas bougé.
    private DateTime[] _residueSince = [];
    private DateTime[] _stableSince = [];
    private DateTime _lastKey;
    private long _lastKeyBytes;
    private long _sinceKey;

    // Flux Brotli depuis la dernière image clé.
    private BrotliEncoder _brotli;
    private bool _brotliOpen;

    // Pause de l'affichage marquée : la capture suivante est une image clé (l'écran revient entier).
    private ScreenPause _paused;
    private bool _keyAfterPause;

    /// <param name="stream">Numéro de l'enregistrement d'écran (une session), écrit dans chaque image.</param>
    /// <param name="live">
    /// Écran partagé en direct : tout changement part tout de suite, même un caractère tapé (pas de seuil de bruit ni
    /// de rattrapage différé).
    /// </param>
    public ScreenTileEncoder(int stream, bool live = false)
    {
        _stream = stream;
        _noise = live ? 0 : NoiseThreshold;
    }

    /// <summary>
    /// Encode une capture (pixels BGRA 32 bits, de haut en bas) ; null si rien n'a changé de façon visible, qu'aucun reste
    /// n'est à rattraper et qu'aucune image clé n'est due. À appeler à chaque capture, même sans changement.
    /// </summary>
    /// <param name="stride">Octets par ligne de <paramref name="bgra"/>.</param>
    /// <param name="key">Image clé demandée.</param>
    /// <param name="moves">
    /// Copies d'écran faites depuis la capture précédente, dans l'ordre (client FreeRDP) ; null si elles ne sont pas
    /// connues : un défilement est alors cherché entre les deux captures.
    /// </param>
    public ScreenFrame? Encode(DateTime time, ReadOnlySpan<byte> bgra, int width, int height, int stride, bool key = false,
        IReadOnlyList<ScreenMove>? moves = null)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || stride < width * 4
            || bgra.Length < ((long)stride * (height - 1)) + (width * 4))
        {
            throw new ArgumentException("Invalid screen capture.", nameof(bgra));
        }

        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _columns = (width + TileSize - 1) / TileSize;
            _rows = (height + TileSize - 1) / TileSize;
            _current = new uint[_columns * TileSize * _rows * TileSize];
            _previous = new uint[_current.Length];
            _residueSince = new DateTime[_columns * _rows];
            _stableSince = new DateTime[_columns * _rows];
            _shown = null;
        }
        else
        {
            // La capture d'avant reste à côté : une tuile qui n'a pas bougé depuis se reconnaît.
            (_previous, _current) = (_current, _previous);
        }

        int pitch = _columns * TileSize;
        for (int y = 0; y < height; y++)
        {
            // BGRA petit-boutiste : 0xAARRGGBB, sans l'alpha.
            var row = MemoryMarshal.Cast<byte, uint>(bgra.Slice(y * stride, width * 4));
            var target = _current.AsSpan(y * pitch, width);
            for (int x = 0; x < width; x++)
            {
                target[x] = row[x] & 0xFFFFFF;
            }
        }

        bool first = _shown is null;
        _shown ??= new uint[_current.Length];
        key |= first || KeyDue(time) || _keyAfterPause || _ids.Count + (_rows * _columns) > MaxTilesBetweenKeys;
        _keyAfterPause = false;
        _paused = ScreenPause.None;

        // Copies d'écran d'abord : l'écran d'avant, déplacé, est la base des comparaisons et des différences.
        var applied = key ? [] : Moves(pitch, moves);
        var shown = _shown;

        // Tuiles à garder : modifiées au-delà du seuil, ou dont le reste sous le seuil est à rattraper (toutes pour une
        // image clé).
        var kept = new List<(int Index, int Origin, int Differences)>();
        int changed = 0;
        for (int index = 0; index < _rows * _columns; index++)
        {
            int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
            int differences = first ? TilePixels : Differences(_current, shown, origin, pitch);
            bool visible = differences > _noise;
            bool catchUp = false;
            if (differences == 0 || visible)
            {
                _residueSince[index] = default;
            }
            else
            {
                if (_residueSince[index] == default)
                {
                    _residueSince[index] = time;
                }

                if (!Same(_current, _previous, origin, pitch))
                {
                    _stableSince[index] = time;
                }

                catchUp = time - _stableSince[index] >= CatchUpDelay || time - _residueSince[index] >= CatchUpMaxDelay;
            }

            if (visible || catchUp)
            {
                changed++;
            }

            if (visible || catchUp || key)
            {
                kept.Add((index, origin, differences));
            }
        }

        if (kept.Count == 0 && applied.Count == 0)
        {
            return null;
        }

        if (key)
        {
            // Le cache repart de l'image clé : un lecteur rejoue depuis elle sans garder toutes les tuiles vues.
            _ids.Clear();
        }

        var placements = new List<(int Index, int Id)>(kept.Count);
        var fresh = new List<(uint Base, uint[] Tile, uint[]? Before)>();
        int firstNew = _nextId;
        foreach (var (index, origin, differences) in kept)
        {
            var tile = new uint[TilePixels];
            for (int y = 0; y < TileSize; y++)
            {
                _current.AsSpan(origin + (y * pitch), TileSize).CopyTo(tile.AsSpan(y * TileSize));
            }

            var hash = BinaryPrimitives.ReadUInt128LittleEndian(SHA256.HashData(MemoryMarshal.AsBytes(tile.AsSpan())));
            if (!_ids.TryGetValue(hash, out int id))
            {
                id = _nextId++;
                _ids[hash] = id;
                if (!key && differences <= DeltaMaxPixels)
                {
                    // Différence avec ce que la tuile montrait (l'écran d'avant cette image, que le lecteur a aussi).
                    var before = new uint[TilePixels];
                    for (int y = 0; y < TileSize; y++)
                    {
                        shown.AsSpan(origin + (y * pitch), TileSize).CopyTo(before.AsSpan(y * TileSize));
                    }

                    fresh.Add(((uint)index, tile, before));
                }
                else
                {
                    fresh.Add((FullTile, tile, null));
                }
            }

            placements.Add((index, id));
            for (int y = 0; y < TileSize; y++)
            {
                tile.AsSpan(y * TileSize, TileSize).CopyTo(shown.AsSpan(origin + (y * pitch)));
            }

            _residueSince[index] = default;
        }

        bool trueColor = fresh.Exists(f => !Exact565(f.Tile));
        int activity = first ? 1000 : changed * 1000 / (_rows * _columns);
        var header = new ScreenFrameHeader(_stream, _sequence, width, height, key, TileSize, activity, firstNew, fresh.Count, placements.Count,
            ScreenPause.None, applied.Count, trueColor);
        var body = new byte[header.BodyLength];
        int at = 0;
        foreach (var move in applied)
        {
            foreach (int n in (ReadOnlySpan<int>)[move.SourceX, move.SourceY, move.Width, move.Height, move.X, move.Y])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), (ushort)n);
                at += 2;
            }
        }

        foreach (var (index, id) in placements)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), (uint)index);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 4), (uint)id);
            at += 8;
        }

        foreach (var (origin, _, _) in fresh)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), origin);
            at += 4;
        }

        foreach (var (_, tile, before) in fresh)
        {
            for (int i = 0; i < TilePixels; i++)
            {
                if (trueColor)
                {
                    uint v = before is null ? tile[i] : tile[i] ^ before[i];
                    body[at] = (byte)v;
                    body[at + 1] = (byte)(v >> 8);
                    body[at + 2] = (byte)(v >> 16);
                    at += 3;
                }
                else
                {
                    ushort v = before is null ? To565(tile[i]) : (ushort)(To565(tile[i]) ^ To565(before[i]));
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), v);
                    at += 2;
                }
            }
        }

        if (key || !_brotliOpen)
        {
            // Nouveau flux Brotli : un lecteur décompresse depuis cette image clé.
            if (_brotliOpen)
            {
                _brotli.Dispose();
            }

            _brotli = new BrotliEncoder(BrotliQuality, BrotliWindow);
            _brotliOpen = true;
        }

        var data = Compress(body, header.Length);
        header.Write(data);
        _sequence++;
        if (key)
        {
            _lastKey = time;
            _lastKeyBytes = data.Length;
            _sinceKey = 0;
        }
        else
        {
            _sinceKey += data.Length;
        }

        return new ScreenFrame(data, header);
    }

    /// <summary>
    /// Pause de l'affichage de la session (<paramref name="pause"/>), ou sa fin (<see cref="ScreenPause.None"/>), à
    /// <paramref name="time"/> : image sans tuile ni contenu, marquée, qui fige l'écran dans le lecteur et y dit « pause »
    /// jusqu'à l'image suivante. La capture suivante est une image clé. Null avant la première image (rien n'est encore
    /// enregistré) ou si rien ne change (déjà dans cet état).
    /// </summary>
    public ScreenFrame? Mark(DateTime time, ScreenPause pause)
    {
        if (_shown is null || pause == _paused)
        {
            return null;
        }

        var header = new ScreenFrameHeader(_stream, _sequence, _width, _height, false, TileSize, 0, _nextId, 0, 0, pause);
        var data = new byte[header.Length];
        header.Write(data);
        // Pas un changement de l'écran : ne compte pas pour l'image clé suivante.
        _sequence++;
        _paused = pause;
        _keyAfterPause = true;
        return new ScreenFrame(data, header);
    }

    public void Dispose()
    {
        if (_brotliOpen)
        {
            _brotli.Dispose();
            _brotliOpen = false;
        }
    }

    /// <summary>Pixel 24 bits (0x00RRGGBB) en RGB565.</summary>
    internal static ushort To565(uint pixel) =>
        (ushort)((((pixel >> 19) & 31) << 11) | (((pixel >> 10) & 63) << 5) | ((pixel >> 3) & 31));

    /// <summary>Pixel RGB565 élargi en 24 bits comme le fait FreeRDP (bits hauts recopiés dans les bits bas).</summary>
    internal static uint From565(ushort pixel)
    {
        uint r = (uint)(pixel >> 11), g = (uint)(pixel >> 5) & 63, b = (uint)pixel & 31;
        return (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
    }

    /// <summary>Tuile dont chaque pixel est un pixel 16 bits élargi : gardée en RGB565 sans perte.</summary>
    private static bool Exact565(uint[] tile)
    {
        foreach (uint pixel in tile)
        {
            if (From565(To565(pixel)) != pixel)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Copies d'écran à garder pour cette image, appliquées à l'écran montré (<see cref="_shown"/>) : celles du client si
    /// elles sont connues, sinon un défilement reconnu ; vide si elles ne laissent pas moins de tuiles à garder.
    /// </summary>
    private List<ScreenMove> Moves(int pitch, IReadOnlyList<ScreenMove>? hints)
    {
        var shown = _shown!;
        int tiles = _rows * _columns, changedTiles = 0;
        for (int index = 0; index < tiles && changedTiles < 2; index++)
        {
            int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
            changedTiles += Same(_current, shown, origin, pitch) ? 0 : 1;
        }

        if (changedTiles < 2)
        {
            return [];
        }

        var candidates = hints?.Where(m => m.FitsIn(_width, _height)).Take(MaxMoves).ToList() ?? [];
        if (candidates.Count == 0)
        {
            candidates = [.. ScreenScroll.Detect<uint>(shown, _current, _width, _height, pitch)
                .Select(c => new ScreenMove(c.Left, c.Top + c.Shift, c.Width, c.Height, c.Left, c.Top))
                .Where(m => m.FitsIn(_width, _height))
                .Take(MaxMoves)];
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        if (_predicted.Length != shown.Length)
        {
            _predicted = new uint[shown.Length];
        }

        shown.CopyTo(_predicted, 0);
        foreach (var move in candidates)
        {
            move.Apply<uint>(_predicted, pitch);
        }

        // Tuiles touchées par les copies : celles à garder avec et sans.
        var touched = new bool[tiles];
        foreach (var move in candidates)
        {
            for (int ty = move.Y / TileSize; ty <= (move.Y + move.Height - 1) / TileSize; ty++)
            {
                for (int tx = move.X / TileSize; tx <= (move.X + move.Width - 1) / TileSize; tx++)
                {
                    touched[(ty * _columns) + tx] = true;
                }
            }
        }

        int without = 0, with = 0;
        for (int index = 0; index < tiles; index++)
        {
            if (touched[index])
            {
                int origin = (index / _columns * TileSize * pitch) + (index % _columns * TileSize);
                without += Differences(_current, shown, origin, pitch) > _noise ? 1 : 0;
                with += Differences(_current, _predicted, origin, pitch) > _noise ? 1 : 0;
            }
        }

        if (with >= without)
        {
            return [];
        }

        (_shown, _predicted) = (_predicted, shown);
        return candidates;
    }

    /// <summary>Contenu compressé à la suite du flux Brotli, vidé : l'image se décompresse dès qu'elle est lue.</summary>
    private byte[] Compress(byte[] body, int headerLength)
    {
        var output = new ArrayBufferWriter<byte>(headerLength + Math.Max(256, body.Length / 4));
        output.Advance(headerLength);
        ReadOnlySpan<byte> source = body;
        OperationStatus status;
        do
        {
            status = Check(_brotli.Compress(source, output.GetSpan(Math.Max(4096, source.Length / 4)), out int consumed, out int written, isFinalBlock: false));
            output.Advance(written);
            source = source[consumed..];
        }
        while (status == OperationStatus.DestinationTooSmall || source.Length > 0);

        do
        {
            status = Check(_brotli.Flush(output.GetSpan(4096), out int written));
            output.Advance(written);
        }
        while (status == OperationStatus.DestinationTooSmall);

        return output.WrittenSpan.ToArray();
    }

    private static OperationStatus Check(OperationStatus status) =>
        status == OperationStatus.InvalidData ? throw new InvalidOperationException("Brotli compression failed.") : status;

    /// <summary>
    /// Image clé due : l'écran a changé depuis la précédente, et les images depuis pèsent assez lourd (au plus tôt après
    /// <see cref="KeyFrameMinInterval"/>) ou <see cref="KeyFrameMaxInterval"/> est passé.
    /// </summary>
    private bool KeyDue(DateTime time)
    {
        if (_sinceKey == 0)
        {
            return false;
        }

        var since = time - _lastKey;
        return since >= KeyFrameMaxInterval || (since >= KeyFrameMinInterval && _sinceKey >= KeyFrameBytesFactor * _lastKeyBytes);
    }

    /// <summary>Pixels différents entre deux tuiles (même position) de deux écrans.</summary>
    private static int Differences(uint[] a, uint[] b, int origin, int pitch)
    {
        int count = 0;
        for (int y = 0; y < TileSize; y++)
        {
            var rowA = a.AsSpan(origin + (y * pitch), TileSize);
            var rowB = b.AsSpan(origin + (y * pitch), TileSize);
            if (rowA.SequenceEqual(rowB))
            {
                continue;
            }

            for (int x = 0; x < TileSize; x++)
            {
                if (rowA[x] != rowB[x])
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Même tuile (même position) dans deux écrans.</summary>
    private static bool Same(uint[] a, uint[] b, int origin, int pitch)
    {
        for (int y = 0; y < TileSize; y++)
        {
            if (!a.AsSpan(origin + (y * pitch), TileSize).SequenceEqual(b.AsSpan(origin + (y * pitch), TileSize)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Relecture d'un enregistrement d'écran (<see cref="ScreenTileEncoder"/>, formats 3 et 2) : l'écran (pixels 0x00RRGGBB)
/// après chaque image. Après une image illisible ou manquante (numéro qui ne suit pas), plus rien ne se lit jusqu'à l'image
/// clé suivante : les copies, les différences, les tuiles déjà vues et le flux Brotli ne s'appliqueraient plus au bon
/// écran.
/// </summary>
public sealed class ScreenTileDecoder : IDisposable
{
    private const int TileSize = ScreenTileEncoder.TileSize;
    private const int TilePixels = TileSize * TileSize;

    private readonly Dictionary<int, uint[]> _tiles = [];
    private readonly int _maxTiles;

    // Numéro de l'image attendue ; -1 : en attente d'une image clé.
    private int _next = -1;

    // Flux Brotli depuis la dernière image clé (format 3).
    private BrotliDecoder _brotli;
    private bool _brotliOpen;

    /// <param name="maxTiles">
    /// Tuiles gardées au plus entre deux images clés ; au-delà, plus rien ne se lit jusqu'à l'image clé suivante. Pour un
    /// écran reçu d'un autre poste (partage), <see cref="ScreenTileEncoder.MaxTilesBetweenKeys"/> : un poste bogué ou
    /// malveillant n'épuise pas la mémoire. Sans limite pour relire son propre journal.
    /// </param>
    public ScreenTileDecoder(int maxTiles = int.MaxValue) => _maxTiles = maxTiles;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Écran (pixels 0x00RRGGBB, ligne par ligne) ; vide avant la première image clé.</summary>
    public uint[] Pixels { get; private set; } = [];

    /// <summary>Affichage en pause depuis la dernière image appliquée (<see cref="ScreenTileEncoder.Mark"/>), et pourquoi.</summary>
    public ScreenPause Pause { get; private set; }

    /// <summary>Applique une image ; faux si elle est illisible, ne suit pas la précédente ou arrive avant une image clé.</summary>
    public bool Apply(byte[] data)
    {
        if (!ScreenFrameHeader.TryRead(data, out var header))
        {
            _next = -1;
            return false;
        }

        if (header.Key)
        {
            _tiles.Clear();
            if (header.Width != Width || header.Height != Height)
            {
                Width = header.Width;
                Height = header.Height;
                Pixels = new uint[Width * Height];
            }
        }
        else if (_next < 0 || header.Sequence != _next || header.Width != Width || header.Height != Height)
        {
            _next = -1;
            return false;
        }

        _next = -1;
        if (_tiles.Count + (long)header.NewTiles > _maxTiles)
        {
            // Plus de tuiles que l'encodeur n'en garde entre deux images clés.
            _tiles.Clear();
            return false;
        }

        var body = new byte[header.BodyLength];
        if (!Inflate(header, data.AsSpan(header.Length), body))
        {
            return false;
        }

        int columns = (Width + TileSize - 1) / TileSize;
        int placementsAt = ScreenMove.Length * header.Moves;
        int basesAt = placementsAt + (8 * header.Placements);
        int tilesAt = basesAt + (4 * header.NewTiles);
        var moves = new ScreenMove[header.Moves];
        for (int i = 0; i < moves.Length; i++)
        {
            var m = body.AsSpan(ScreenMove.Length * i, ScreenMove.Length);
            moves[i] = new ScreenMove(
                BinaryPrimitives.ReadUInt16LittleEndian(m), BinaryPrimitives.ReadUInt16LittleEndian(m[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(m[4..]), BinaryPrimitives.ReadUInt16LittleEndian(m[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(m[8..]), BinaryPrimitives.ReadUInt16LittleEndian(m[10..]));
            if (!moves[i].FitsIn(Width, Height))
            {
                return false;
            }
        }

        // Copies d'abord, puis les tuiles nouvelles, les différences d'après l'écran ainsi déplacé.
        foreach (var move in moves)
        {
            move.Apply<uint>(Pixels, Width);
        }

        var fresh = new uint[header.NewTiles][];
        var narrow = new ushort[TilePixels];
        for (int i = 0; i < header.NewTiles; i++)
        {
            uint origin = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(basesAt + (4 * i)));
            bool delta = origin != ScreenTileEncoder.FullTile;
            if (delta && (header.Key || origin >= (uint)header.Tiles))
            {
                return false;
            }

            var tile = new uint[TilePixels];
            if (header.TrueColor)
            {
                var bytes = body.AsSpan(tilesAt + (i * 3 * TilePixels), 3 * TilePixels);
                for (int p = 0; p < TilePixels; p++)
                {
                    tile[p] = bytes[3 * p] | ((uint)bytes[(3 * p) + 1] << 8) | ((uint)bytes[(3 * p) + 2] << 16);
                }

                if (delta)
                {
                    Visit((int)origin, columns, (offset, pixel) => tile[offset] ^= Pixels[pixel]);
                }
            }
            else
            {
                // RGB565 : différence dans cet espace (l'écran d'avant ramené en 16 bits), puis élargi.
                var bytes = body.AsSpan(tilesAt + (i * 2 * TilePixels), 2 * TilePixels);
                for (int p = 0; p < TilePixels; p++)
                {
                    narrow[p] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * p)..]);
                }

                if (delta)
                {
                    Visit((int)origin, columns, (offset, pixel) => narrow[offset] ^= ScreenTileEncoder.To565(Pixels[pixel]));
                }

                for (int p = 0; p < TilePixels; p++)
                {
                    tile[p] = ScreenTileEncoder.From565(narrow[p]);
                }
            }

            fresh[i] = tile;
        }

        for (int i = 0; i < fresh.Length; i++)
        {
            _tiles[header.FirstNewTile + i] = fresh[i];
        }

        for (int i = 0; i < header.Placements; i++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(placementsAt + (8 * i)));
            int id = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(placementsAt + (8 * i) + 4));
            if (index >= (uint)header.Tiles || !_tiles.TryGetValue(id, out var tile))
            {
                return false;
            }

            Visit((int)index, columns, (offset, pixel) => Pixels[pixel] = tile[offset]);
        }

        _next = header.Sequence + 1;
        Pause = header.Pause;
        return true;
    }

    public void Dispose()
    {
        if (_brotliOpen)
        {
            _brotli.Dispose();
            _brotliOpen = false;
        }
    }

    /// <summary>
    /// Contenu décompressé, ni plus court ni plus long que l'annonce de l'en-tête (pas de décompression sans fin) : zlib
    /// seul (format 2), ou à la suite du flux Brotli parti de l'image clé (format 3).
    /// </summary>
    private bool Inflate(ScreenFrameHeader header, ReadOnlySpan<byte> compressed, byte[] body)
    {
        if (header.Format == 2)
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress);
                return zlib.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) == body.Length && zlib.ReadByte() < 0;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        if (header.Key)
        {
            if (_brotliOpen)
            {
                _brotli.Dispose();
            }

            _brotli = new BrotliDecoder();
            _brotliOpen = true;
        }

        if (body.Length == 0)
        {
            // Pause : pas de contenu, le flux n'avance pas.
            return compressed.IsEmpty;
        }

        if (!_brotliOpen)
        {
            return false;
        }

        int total = 0;
        Span<byte> spare = stackalloc byte[1];
        while (true)
        {
            // Une fois le contenu complet, un octet de plus dirait un contenu plus long qu'annoncé.
            var target = total < body.Length ? body.AsSpan(total) : spare;
            var status = _brotli.Decompress(compressed, target, out int consumed, out int written);
            compressed = compressed[consumed..];
            if (total >= body.Length && written > 0)
            {
                return false;
            }

            total += written;
            switch (status)
            {
                case OperationStatus.DestinationTooSmall:
                    continue;
                case OperationStatus.NeedMoreData:
                    return compressed.IsEmpty && total == body.Length;
                default:
                    // Flux terminé (l'encodeur n'en termine jamais) ou illisible.
                    return false;
            }
        }
    }

    /// <summary>Chaque pixel de l'écran couvert par une tuile de la grille : sa place dans la tuile, puis dans l'écran.</summary>
    private void Visit(int index, int columns, Action<int, int> pixel)
    {
        int x0 = index % columns * TileSize;
        int y0 = index / columns * TileSize;
        int width = Math.Min(TileSize, Width - x0);
        for (int y = 0; y < TileSize && y0 + y < Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixel((y * TileSize) + x, ((y0 + y) * Width) + x0 + x);
            }
        }
    }
}
