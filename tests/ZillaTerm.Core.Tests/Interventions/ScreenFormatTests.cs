using System.Buffers.Binary;
using System.IO.Compression;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

/// <summary>Format 3 des images d'écran : copies d'écran, couleurs exactes en 16 ou 24 bits, flux Brotli, format 2 relu.</summary>
public sealed class ScreenFormatTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc);

    private const int Width = 320;
    private const int Height = 256;

    /// <summary>
    /// Écran BGRA : une fenêtre de texte (lignes de 16 pixels, motif tiré d'après le numéro de ligne) dont la première ligne
    /// affichée est <paramref name="firstLine"/>, sur un fond à motif ; couleurs <paramref name="exact16"/> : pixels 16
    /// bits élargis (session en 16 bits), sinon des valeurs que le 16 bits ne rend pas.
    /// </summary>
    private static byte[] Screen(int firstLine, bool exact16 = false)
    {
        // Gris en 16 bits : pixel RGB565 élargi (les trois composantes ne tombent pas toutes sur la même valeur).
        uint Color(byte gray) => exact16 ? ScreenTileEncoder.From565(ScreenTileEncoder.To565((uint)(gray | (gray << 8) | (gray << 16)))) : (uint)(gray | (gray << 8) | (gray << 16));
        uint ink = Color(23), paper = Color(250), back = Color(131);
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                uint value = back;
                if (x is >= 32 and < 288 && y is >= 32 and < 224)
                {
                    int line = firstLine + ((y - 32) / 16), column = (x - 32) / 8;
                    uint h = (uint)((line * 73856093) ^ (column * 19349663));
                    h ^= h >> 13;
                    // Motif d'après la place dans la ligne : une ligne qui défile garde ses pixels.
                    value = ((h >> ((x + ((y - 32) % 16)) % 7)) & 1) == 1 && (y - 32) % 16 is >= 3 and <= 12 ? ink : paper;
                }
                else if ((x / 8 + y / 8) % 5 == 0)
                {
                    value = paper;
                }

                int o = ((y * Width) + x) * 4;
                pixels[o] = (byte)value;
                pixels[o + 1] = (byte)(value >> 8);
                pixels[o + 2] = (byte)(value >> 16);
                pixels[o + 3] = 255;
            }
        }

        return pixels;
    }

    private static uint[] Exact(byte[] bgra) =>
        [.. Enumerable.Range(0, bgra.Length / 4).Select(i => (uint)(bgra[4 * i] | (bgra[(4 * i) + 1] << 8) | (bgra[(4 * i) + 2] << 16)))];

    /// <summary>
    /// Défilement d'une ligne dans la fenêtre de texte, connu du client FreeRDP (copie d'écran) : gardé comme une copie
    /// plus la seule ligne nouvelle, bien moins lourd que sans, et relu au pixel près.
    /// </summary>
    [Fact]
    public void KnownMoveCostsLittleAndDecodesExactly()
    {
        var withMoves = new ScreenTileEncoder(stream: 1);
        var without = new ScreenTileEncoder(stream: 1);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(withMoves.Encode(T0, Screen(0), Width, Height, Width * 4)!.Data));
        without.Encode(T0, Screen(0), Width, Height, Width * 4);
        var scroll = new ScreenMove(32, 48, 256, 176, 32, 32);

        var moved = withMoves.Encode(T0.AddSeconds(1), Screen(1), Width, Height, Width * 4, moves: [scroll])!;
        var plain = without.Encode(T0.AddSeconds(1), Screen(1), Width, Height, Width * 4, moves: [new ScreenMove(0, 0, 8, 8, 300, 200)])!;

        Assert.Equal(1, moved.Header.Moves);
        Assert.True(decoder.Apply(moved.Data));
        Assert.Equal(Exact(Screen(1)), decoder.Pixels);
        Assert.True(moved.Data.Length * 3 < plain.Data.Length, $"avec la copie {moved.Data.Length}, sans {plain.Data.Length}");
    }

    /// <summary>Sans copie connue (capture de la fenêtre), le défilement est reconnu entre les deux captures.</summary>
    [Fact]
    public void ScrollIsRecognizedWithoutMoves()
    {
        var encoder = new ScreenTileEncoder(stream: 2);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(encoder.Encode(T0, Screen(0), Width, Height, Width * 4)!.Data));
        for (int line = 1; line <= 3; line++)
        {
            var frame = encoder.Encode(T0.AddSeconds(line), Screen(line), Width, Height, Width * 4)!;
            Assert.True(frame.Header.Moves > 0);
            Assert.True(decoder.Apply(frame.Data));
            Assert.Equal(Exact(Screen(line)), decoder.Pixels);
        }
    }

    /// <summary>
    /// Copie annoncée qui ne correspond pas à l'écran (ou hors de l'écran) : écartée, l'image garde ses tuiles et se relit au
    /// pixel près.
    /// </summary>
    [Fact]
    public void MisleadingMovesAreDropped()
    {
        var encoder = new ScreenTileEncoder(stream: 3);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(encoder.Encode(T0, Screen(0), Width, Height, Width * 4)!.Data));

        var frame = encoder.Encode(T0.AddSeconds(1), Screen(7), Width, Height, Width * 4,
            moves: [new ScreenMove(0, 0, Width, 100, 0, 150), new ScreenMove(300, 0, 64, 64, 0, 0)])!;

        Assert.Equal(0, frame.Header.Moves);
        Assert.True(decoder.Apply(frame.Data));
        Assert.Equal(Exact(Screen(7)), decoder.Pixels);
    }

    /// <summary>
    /// Écran d'une session en 16 bits : tuiles gardées en RGB565 (2 octets par pixel), sans perte ; autres couleurs : en 24
    /// bits, au pixel près. Une image du même écran pèse moins en 16 bits.
    /// </summary>
    [Fact]
    public void ColorsAreExactIn16Or24Bits()
    {
        var narrow = new ScreenTileEncoder(stream: 4).Encode(T0, Screen(0, exact16: true), Width, Height, Width * 4)!;
        var wide = new ScreenTileEncoder(stream: 4).Encode(T0, Screen(0), Width, Height, Width * 4)!;

        Assert.False(narrow.Header.TrueColor);
        Assert.True(wide.Header.TrueColor);
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(narrow.Data));
        Assert.Equal(Exact(Screen(0, exact16: true)), decoder.Pixels);
        Assert.True(decoder.Apply(wide.Data));
        Assert.Equal(Exact(Screen(0)), decoder.Pixels);
        Assert.True(narrow.Header.BodyLength < wide.Header.BodyLength);
    }

    /// <summary>
    /// Flux Brotli d'une image clé à l'autre : une image identique à une image déjà vue depuis l'image clé ne pèse presque
    /// rien ; chaque image compressée se décompresse seule, à la suite des précédentes.
    /// </summary>
    [Fact]
    public void BrotliStreamRemembersEarlierFrames()
    {
        var encoder = new ScreenTileEncoder(stream: 5, live: true);
        var decoder = new ScreenTileDecoder();
        var first = encoder.Encode(T0, Screen(0), Width, Height, Width * 4)!;
        var other = encoder.Encode(T0.AddSeconds(1), Screen(40), Width, Height, Width * 4)!;
        var back = encoder.Encode(T0.AddSeconds(2), Screen(80), Width, Height, Width * 4)!;
        Assert.True(decoder.Apply(first.Data) && decoder.Apply(other.Data) && decoder.Apply(back.Data));
        Assert.Equal(Exact(Screen(80)), decoder.Pixels);

        // Contenu tronqué : illisible, et plus rien jusqu'à l'image clé suivante.
        var cut = encoder.Encode(T0.AddSeconds(3), Screen(0), Width, Height, Width * 4)!;
        Assert.False(cut.Header.Key);
        Assert.False(new ScreenTileDecoder().Apply(cut.Data));
        Assert.False(decoder.Apply(cut.Data[..(ScreenFrameHeader.Length3 + ((cut.Data.Length - ScreenFrameHeader.Length3) / 2))]));
        Assert.False(decoder.Apply(encoder.Encode(T0.AddSeconds(4), Screen(1), Width, Height, Width * 4)!.Data));
        var key = encoder.Encode(T0.AddSeconds(5), Screen(2), Width, Height, Width * 4, key: true)!;
        Assert.True(decoder.Apply(key.Data));
        Assert.Equal(Exact(Screen(2)), decoder.Pixels);
    }

    /// <summary>
    /// Image d'un journal d'avant la 0.42 (format 2 : RGB565, zlib par image) : relue, pixels élargis en 24 bits comme le
    /// fait FreeRDP.
    /// </summary>
    [Fact]
    public void Format2FramesAreStillRead()
    {
        const int width = 100, height = 70;
        // Image clé 2 × 2 tuiles, toutes la même tuile unie (RGB565 0xF800 : rouge).
        var body = new MemoryStream();
        using (var zlib = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
        {
            var value = new byte[8];
            for (int index = 0; index < 4; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)index);
                BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), 0);
                zlib.Write(value);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(value, ScreenTileEncoder.FullTile);
            zlib.Write(value.AsSpan(0, 4));
            for (int p = 0; p < 64 * 64; p++)
            {
                zlib.Write([0x00, 0xF8]);
            }
        }

        var header = new byte[ScreenFrameHeader.Length2];
        "ZTS2"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), height);
        header[16] = 1;
        header[17] = 64;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 4);
        byte[] frame = [.. header, .. body.ToArray()];

        Assert.True(ScreenFrameHeader.TryRead(frame, out var read));
        Assert.Equal((2, 9, false, 0), (read.Format, read.Stream, read.TrueColor, read.Moves));
        var decoder = new ScreenTileDecoder();
        Assert.True(decoder.Apply(frame));
        Assert.All(decoder.Pixels, pixel => Assert.Equal(0xFF0000u, pixel));
    }

    /// <summary>
    /// Script du lecteur du rapport : le décodeur Brotli embarqué y est une déclaration locale (pas de module), et rien ne
    /// ferme la balise script avant la fin.
    /// </summary>
    [Fact]
    public void PlayerScriptEmbedsTheBrotliDecoder()
    {
        var script = InterventionScreenPlayer.Script;
        Assert.Contains("const BrotliDecode = makeBrotliDecode();", script, StringComparison.Ordinal);
        Assert.DoesNotContain("export ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("</", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', script);
    }

    /// <summary>En-têtes refusés : copies dans une image clé ou une pause, drapeau inconnu, trop de copies.</summary>
    [Fact]
    public void InvalidHeadersAreRefused()
    {
        var frame = new ScreenTileEncoder(stream: 6).Encode(T0, Screen(0), Width, Height, Width * 4)!.Data;
        Assert.True(ScreenFrameHeader.TryRead(frame, out _));

        byte[] With(Action<byte[]> change)
        {
            var copy = (byte[])frame.Clone();
            change(copy);
            return copy;
        }

        Assert.False(ScreenFrameHeader.TryRead(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(32), 1)), out _));
        Assert.False(ScreenFrameHeader.TryRead(With(f => f[16] |= 16), out _));
        Assert.False(ScreenFrameHeader.TryRead(With(f =>
        {
            f[16] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(32), ScreenTileEncoder.MaxMoves + 1);
        }), out _));
        Assert.False(ScreenFrameHeader.TryRead(frame.AsSpan(0, ScreenFrameHeader.Length3 - 1), out _));
    }
}
