using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ZillaTerm.Core.Ssh;

/// <summary>Résultat de l'examen du début d'une connexion X11 (<see cref="X11Setup.Check"/>).</summary>
public enum X11SetupStatus
{
    /// <summary>La demande d'ouverture n'est pas encore entière : lire la suite.</summary>
    Incomplete,

    /// <summary>Demande entière, avec le jeton attendu.</summary>
    Accepted,

    /// <summary>Demande mal formée, trop longue, ou sans le jeton attendu : la connexion est refusée.</summary>
    Refused,
}

/// <summary>
/// Demande d'ouverture d'une connexion X11 (le premier message qu'un client X envoie au serveur X) : vérifie le cookie
/// qu'elle présente et le remplace par celui du serveur X de ce poste.
/// </summary>
/// <remarks>
/// Format : ordre des octets (« B » gros-boutiste, « l » petit-boutiste), un octet inutilisé, versions majeure et
/// mineure (16 bits), longueurs du nom du protocole d'autorisation et de ses données (16 bits), deux octets inutilisés,
/// puis le nom et les données, chacun complété à un multiple de 4 octets.
/// </remarks>
public static class X11Setup
{
    private const int HeaderLength = 12;

    /// <summary>Plus long nom de protocole accepté (MIT-MAGIC-COOKIE-1 en fait 18).</summary>
    private const int MaxNameLength = 64;

    /// <summary>Plus longues données d'autorisation acceptées (un cookie en fait 16).</summary>
    private const int MaxDataLength = 256;

    /// <summary>Taille maximale d'une demande d'ouverture acceptée.</summary>
    public const int MaxLength = HeaderLength + MaxNameLength + MaxDataLength;

    /// <summary>
    /// Examine le début reçu d'une connexion : demande entière (<paramref name="length"/> octets) dont le protocole est
    /// MIT-MAGIC-COOKIE-1 et le cookie égal à <paramref name="token"/> (comparaison en temps constant) ; incomplète ; ou
    /// refusée.
    /// </summary>
    public static X11SetupStatus Check(ReadOnlySpan<byte> received, ReadOnlySpan<byte> token, out int length)
    {
        length = 0;
        if (received.Length < HeaderLength)
        {
            return received.Length > 0 && !IsByteOrder(received[0]) ? X11SetupStatus.Refused : X11SetupStatus.Incomplete;
        }

        if (!IsByteOrder(received[0]))
        {
            return X11SetupStatus.Refused;
        }

        bool bigEndian = received[0] == (byte)'B';
        int nameLength = ReadUInt16(received[6..], bigEndian);
        int dataLength = ReadUInt16(received[8..], bigEndian);
        if (nameLength > MaxNameLength || dataLength > MaxDataLength)
        {
            return X11SetupStatus.Refused;
        }

        int total = HeaderLength + Pad(nameLength) + Pad(dataLength);
        if (received.Length < total)
        {
            return X11SetupStatus.Incomplete;
        }

        var name = received.Slice(HeaderLength, nameLength);
        var data = received.Slice(HeaderLength + Pad(nameLength), dataLength);
        if (!name.SequenceEqual(Encoding.ASCII.GetBytes(XAuthority.MagicCookie)) || token.IsEmpty
            || !CryptographicOperations.FixedTimeEquals(data, token))
        {
            return X11SetupStatus.Refused;
        }

        length = total;
        return X11SetupStatus.Accepted;
    }

    /// <summary>
    /// La demande <paramref name="setup"/> (entière, acceptée par <see cref="Check"/>) avec l'autorisation
    /// <paramref name="protocol"/> / <paramref name="data"/> du serveur X de ce poste ; sans autorisation si
    /// <paramref name="protocol"/> est null (serveur X qui reconnaît l'utilisateur à sa connexion locale).
    /// </summary>
    public static byte[] Rewrite(ReadOnlySpan<byte> setup, string? protocol, ReadOnlySpan<byte> data)
    {
        bool bigEndian = setup[0] == (byte)'B';
        var name = protocol is null ? [] : Encoding.ASCII.GetBytes(protocol);
        if (protocol is null)
        {
            data = [];
        }

        var result = new byte[HeaderLength + Pad(name.Length) + Pad(data.Length)];
        setup[..HeaderLength].CopyTo(result);
        WriteUInt16(result.AsSpan(6), (ushort)name.Length, bigEndian);
        WriteUInt16(result.AsSpan(8), (ushort)data.Length, bigEndian);
        name.CopyTo(result.AsSpan(HeaderLength));
        data.CopyTo(result.AsSpan(HeaderLength + Pad(name.Length)));
        return result;
    }

    private static bool IsByteOrder(byte value) => value is (byte)'B' or (byte)'l';

    private static int Pad(int length) => (length + 3) & ~3;

    private static int ReadUInt16(ReadOnlySpan<byte> data, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static void WriteUInt16(Span<byte> data, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        }
    }
}
