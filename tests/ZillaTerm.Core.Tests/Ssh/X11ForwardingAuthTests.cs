using System.Buffers.Binary;
using System.Text;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

/// <summary>Cookie du serveur X de ce poste (Xauthority) et demande d'ouverture d'une connexion X11 (relais local).</summary>
public class X11ForwardingAuthTests
{
    private static readonly byte[] Cookie = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    private static byte[] Entry(ushort family, string address, string number, string name, byte[] data)
    {
        var stream = new MemoryStream();
        void Write(ReadOnlySpan<byte> field)
        {
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)field.Length);
            stream.Write(length);
            stream.Write(field);
        }

        Span<byte> head = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(head, family);
        stream.Write(head);
        Write(Encoding.UTF8.GetBytes(address));
        Write(Encoding.ASCII.GetBytes(number));
        Write(Encoding.ASCII.GetBytes(name));
        Write(data);
        return stream.ToArray();
    }

    [Fact]
    public void TheCookieOfThisHostAndDisplayIsFound()
    {
        byte[] other = [9, 9, 9];
        byte[] file =
        [
            .. Entry(256, "autre-poste", "0", XAuthority.MagicCookie, other),
            .. Entry(256, "poste", "1", XAuthority.MagicCookie, other),
            .. Entry(256, "poste", "0", "XDM-AUTHORIZATION-1", other),
            .. Entry(256, "poste", "0", XAuthority.MagicCookie, Cookie),
        ];

        Assert.Equal(Cookie, XAuthority.FindCookie(file, "poste", 0));
        Assert.Equal(other, XAuthority.FindCookie(file, "poste", 1));
        Assert.Null(XAuthority.FindCookie(file, "poste", 2));
    }

    [Fact]
    public void AWildEntryOrAnEmptyNumberMatchesAnyHostOrDisplay()
    {
        Assert.Equal(Cookie, XAuthority.FindCookie(Entry(65535, "", "0", XAuthority.MagicCookie, Cookie), "poste", 0));
        Assert.Equal(Cookie, XAuthority.FindCookie(Entry(256, "poste", "", XAuthority.MagicCookie, Cookie), "poste", 7));
        // Famille Internet (adresse IP) : pas pour le socket local.
        Assert.Null(XAuthority.FindCookie(Entry(0, "poste", "0", XAuthority.MagicCookie, Cookie), "poste", 0));
    }

    [Fact]
    public void ATruncatedFileGivesNoCookie()
    {
        var file = Entry(256, "poste", "0", XAuthority.MagicCookie, Cookie);
        Assert.Null(XAuthority.FindCookie(file.AsSpan(0, file.Length - 3), "poste", 0));
        Assert.Null(XAuthority.FindCookie([1], "poste", 0));
        Assert.Null(XAuthority.ReadCookie(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "poste", 0));
    }

    private static byte[] Setup(char order, string name, byte[] data)
    {
        bool big = order == 'B';
        var nameBytes = Encoding.ASCII.GetBytes(name);
        int Pad(int n) => (n + 3) & ~3;
        var setup = new byte[12 + Pad(nameBytes.Length) + Pad(data.Length)];
        setup[0] = (byte)order;
        void Put(int at, int value)
        {
            if (big)
            {
                BinaryPrimitives.WriteUInt16BigEndian(setup.AsSpan(at), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(at), (ushort)value);
            }
        }

        Put(2, 11);
        Put(6, nameBytes.Length);
        Put(8, data.Length);
        nameBytes.CopyTo(setup, 12);
        data.CopyTo(setup, 12 + Pad(nameBytes.Length));
        return setup;
    }

    [Theory]
    [InlineData('B')]
    [InlineData('l')]
    public void TheTokenIsCheckedAndReplacedByTheRealCookie(char order)
    {
        byte[] token = [.. Enumerable.Repeat((byte)0xAB, 16)];
        var setup = Setup(order, XAuthority.MagicCookie, token);

        // Reçue par morceaux : incomplète jusqu'au dernier octet.
        Assert.Equal(X11SetupStatus.Incomplete, X11Setup.Check(setup.AsSpan(0, 5), token, out _));
        Assert.Equal(X11SetupStatus.Incomplete, X11Setup.Check(setup.AsSpan(0, setup.Length - 1), token, out _));
        Assert.Equal(X11SetupStatus.Accepted, X11Setup.Check(setup, token, out int length));
        Assert.Equal(setup.Length, length);

        var rewritten = X11Setup.Rewrite(setup, XAuthority.MagicCookie, Cookie);
        Assert.Equal(setup.Length, rewritten.Length);
        Assert.Equal(setup.AsSpan(0, 6).ToArray(), rewritten.AsSpan(0, 6).ToArray());
        Assert.Equal(X11SetupStatus.Accepted, X11Setup.Check(rewritten, Cookie, out _));

        // Serveur X sans cookie (utilisateur reconnu à sa connexion locale) : autorisation retirée.
        var bare = X11Setup.Rewrite(setup, null, Cookie);
        Assert.Equal(12, bare.Length);
        Assert.Equal(0, bare[6] | bare[7] | bare[8] | bare[9]);
    }

    [Fact]
    public void AnythingElseIsRefused()
    {
        byte[] token = [.. Enumerable.Repeat((byte)0xAB, 16)];
        byte[] wrong = [.. Enumerable.Repeat((byte)0xAC, 16)];
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('B', XAuthority.MagicCookie, wrong), token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('B', XAuthority.MagicCookie, token[..15]), token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('B', "XDM-AUTHORIZATION-1", token), token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('B', "", []), token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check([(byte)'X'], token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('l', XAuthority.MagicCookie, new byte[300]), token, out _));
        Assert.Equal(X11SetupStatus.Refused, X11Setup.Check(Setup('l', XAuthority.MagicCookie, token), [], out _));
    }
}
