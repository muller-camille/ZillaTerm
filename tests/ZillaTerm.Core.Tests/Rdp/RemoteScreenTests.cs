using System.Buffers.Binary;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

public sealed class RemoteScreenTests
{
    /// <summary>Message du flux /frame-pipe : taille du bureau, puis chaque rectangle rempli d'une couleur (B, G, R, 0).</summary>
    private static byte[] Message(int width, int height, params (int X, int Y, int W, int H, byte B, byte G, byte R)[] rects)
    {
        var body = new MemoryStream();
        Span<byte> value = stackalloc byte[4];
        foreach (var (x, y, w, h, _, _, _) in rects)
        {
            foreach (int n in (ReadOnlySpan<int>)[x, y, w, h])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(value, (ushort)n);
                body.Write(value[..2]);
            }
        }

        foreach (var (_, _, w, h, b, g, r) in rects)
        {
            for (int i = 0; i < w * h; i++)
            {
                body.Write([b, g, r, 0]);
            }
        }

        var message = new MemoryStream();
        foreach (uint n in (ReadOnlySpan<uint>)[(uint)(12 + body.Length), (uint)width, (uint)height, (uint)rects.Length])
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value, n);
            message.Write(value);
        }

        body.Position = 0;
        body.CopyTo(message);
        return message.ToArray();
    }

    private static MemoryStream Stream(params byte[][] messages)
    {
        var stream = new MemoryStream();
        stream.Write(RemoteScreen.Magic);
        foreach (var message in messages)
        {
            stream.Write(message);
        }

        stream.Position = 0;
        return stream;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int width, int x, int y)
    {
        int o = ((y * width) + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]);
    }

    /// <summary>Bureau entier, puis une zone modifiée : l'écran garde le reste, pixels opaques.</summary>
    [Fact]
    public void ChangedRegionsUpdateTheScreen()
    {
        var screen = new RemoteScreen();
        screen.Read(Stream(
            Message(40, 30, (0, 0, 40, 30, 10, 20, 30)),
            Message(40, 30, (5, 6, 3, 2, 200, 100, 50), (30, 20, 10, 10, 1, 2, 3))));

        var copy = screen.Copy(null, out long version)!.Value;

        Assert.Equal(2, version);
        Assert.Equal(1, screen.PartialUpdates);
        Assert.Equal((40, 30), (copy.Width, copy.Height));
        Assert.Equal((10, 20, 30, 255), Pixel(copy.Bgra, 40, 0, 0));
        Assert.Equal((200, 100, 50, 255), Pixel(copy.Bgra, 40, 7, 7));
        Assert.Equal((10, 20, 30, 255), Pixel(copy.Bgra, 40, 8, 7));
        Assert.Equal((1, 2, 3, 255), Pixel(copy.Bgra, 40, 39, 29));
        Assert.True(screen.Ended);
        Assert.False(screen.Failed);
    }

    /// <summary>Nouvelle taille du bureau : nouvel écran, à la taille annoncée.</summary>
    [Fact]
    public void ResizeStartsANewScreen()
    {
        var screen = new RemoteScreen();
        screen.Read(Stream(Message(40, 30, (0, 0, 40, 30, 9, 9, 9)), Message(64, 48, (0, 0, 64, 48, 7, 7, 7))));

        var copy = screen.Copy(new byte[10], out _)!.Value;

        Assert.Equal((64, 48), (copy.Width, copy.Height));
        Assert.Equal(64 * 48 * 4, copy.Bgra.Length);
        Assert.Equal((7, 7, 7, 255), Pixel(copy.Bgra, 64, 63, 47));
    }

    /// <summary>
    /// Flux illisible (autre format, rectangle hors du bureau, longueur fausse, bureau trop grand) : arrêté, marqué
    /// <see cref="RemoteScreen.Failed"/>, les messages lus avant restent.
    /// </summary>
    [Theory]
    [InlineData("magic")]
    [InlineData("outside")]
    [InlineData("length")]
    [InlineData("size")]
    [InlineData("empty")]
    public void UnreadableStreamFails(string fault)
    {
        var good = Message(40, 30, (0, 0, 40, 30, 1, 1, 1));
        byte[] bad = fault switch
        {
            "outside" => Message(40, 30, (35, 0, 10, 10, 1, 1, 1)),
            "length" => Message(40, 30, (0, 0, 4, 4, 1, 1, 1))[..^4],
            "size" => Message(RemoteScreen.MaxSide + 1, 30, (0, 0, 1, 1, 1, 1, 1)),
            "empty" => Message(40, 30, (0, 0, 0, 4, 1, 1, 1)),
            _ => good,
        };
        if (fault == "length")
        {
            // Longueur annoncée plus courte que les pixels des rectangles.
            BinaryPrimitives.WriteUInt32LittleEndian(bad, BinaryPrimitives.ReadUInt32LittleEndian(bad) - 4);
        }

        var stream = Stream(good, bad, good);
        if (fault == "magic")
        {
            stream.GetBuffer()[0] = (byte)'X';
        }

        var screen = new RemoteScreen();
        screen.Read(stream);

        Assert.True(screen.Failed);
        Assert.True(screen.Ended);
        Assert.Equal(fault == "magic" ? 0 : 1, screen.Version);
    }

    /// <summary>Flux coupé au milieu d'un message (client fermé) : terminé, pas illisible.</summary>
    [Fact]
    public void CutStreamEndsWithoutFailing()
    {
        var screen = new RemoteScreen();
        var second = Message(40, 30, (0, 0, 40, 30, 2, 2, 2));
        screen.Read(Stream(Message(40, 30, (0, 0, 40, 30, 1, 1, 1)), second[..^10]));

        Assert.True(screen.Ended);
        Assert.False(screen.Failed);
        Assert.Equal(1, screen.Version);
        Assert.Null(new RemoteScreen().Copy(null, out _));
    }

    /// <summary>Attente d'un écran plus récent : rendue dès qu'un message arrive, fausse à la fin du flux sans nouveauté.</summary>
    [Fact]
    public async Task WaitNewerReturnsWhenAMessageArrives()
    {
        var screen = new RemoteScreen();
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var client = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        var reader = Task.Run(() => screen.Read(client));

        Assert.False(screen.WaitNewer(0, TimeSpan.FromMilliseconds(50)));
        pipe.Write(RemoteScreen.Magic);
        pipe.Write(Message(8, 8, (0, 0, 8, 8, 1, 1, 1)));
        Assert.True(screen.WaitNewer(0, TimeSpan.FromSeconds(10)));
        pipe.Dispose();
        await reader;

        Assert.False(screen.WaitNewer(1, TimeSpan.FromSeconds(10)));
        Assert.True(screen.Ended);
        client.Dispose();
    }
}
