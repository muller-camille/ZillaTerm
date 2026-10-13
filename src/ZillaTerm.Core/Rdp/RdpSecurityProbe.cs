using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Rdp;

/// <summary>Clé RSA d'un serveur Bureau à distance en ancienne « sécurité RDP » (sans TLS), montrée et épinglée.</summary>
/// <param name="Sha256">
/// Empreinte SHA-256 de la clé publique (encodage DER PKCS#1 : module et exposant), en hexadécimal : celle que le client
/// FreeRDP compare aux empreintes acceptées (« /cert:fingerprint ») en sécurité RDP.
/// </param>
/// <param name="Bits">Taille de la clé.</param>
public sealed record RdpServerKey(string Sha256, int Bits)
{
    /// <summary>Taille en dessous de laquelle la clé est signalée comme faible (elle chiffre l'aléa de la session).</summary>
    public const int RecommendedBits = 2048;

    /// <summary>
    /// Vrai pour une clé de moins de <see cref="RecommendedBits"/> bits : une clé de 512 bits se factorise en quelques
    /// heures, et qui la connaît se fait passer pour le serveur. Signalé dans la question, sans refus.
    /// </summary>
    public bool IsWeak => Bits < RecommendedBits;
}

/// <summary>
/// Lit la clé d'un serveur Bureau à distance qui n'accepte que l'ancienne sécurité RDP, avant de lui confier quoi que ce
/// soit : demande de connexion X.224 (sécurité RDP, MS-RDPBCGR 2.2.1.1), puis MCS Connect Initial avec les données
/// du client (écran, chiffrement proposé, aucun canal) ; la réponse du serveur porte sa clé (Server Security Data,
/// 2.2.1.4.3). La connexion est alors fermée : ni identifiant, ni mot de passe, ni jeton, ni clé de session envoyés.
/// </summary>
public static class RdpSecurityProbe
{
    private const uint ProtocolRdp = 0;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>TPKT, X.224 Connection Request, RDP_NEG_REQ demandant l'ancienne sécurité RDP seule.</summary>
    internal static ReadOnlySpan<byte> ConnectionRequest =>
    [
        0x03, 0x00, 0x00, 0x13,
        0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <exception cref="RdpCertificateException">Refus, réponse incorrecte, serveur sans chiffrement ou délai dépassé.</exception>
    /// <exception cref="SocketException">Serveur injoignable.</exception>
    public static async Task<RdpServerKey> GetAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var network = tcp.GetStream();
            await network.WriteAsync(ConnectionRequest.ToArray(), timeout.Token).ConfigureAwait(false);
            var confirm = await ReadTpktAsync(network, timeout.Token).ConfigureAwait(false);
            CheckConfirm(confirm);
            await network.WriteAsync(ConnectInitial(), timeout.Token).ConfigureAwait(false);
            var response = await ReadTpktAsync(network, timeout.Token).ConfigureAwait(false);
            return ReadServerKey(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RdpCertificateException(CoreStrings.RdpProbeTimeout);
        }
    }

    /// <summary>X.224 Connection Confirm : l'ancienne sécurité RDP doit être celle choisie (ou pas de négociation).</summary>
    internal static void CheckConfirm(ReadOnlySpan<byte> tpdu)
    {
        // Longueur, code CC (0xD0), références, classe ; puis RDP_NEG_RSP ou RDP_NEG_FAILURE (8 octets).
        if (tpdu.Length < 7 || tpdu[0] != tpdu.Length - 1 || (tpdu[1] & 0xF0) != 0xD0)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        if (tpdu.Length < 15)
        {
            return;
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(tpdu[11..]);
        switch (tpdu[7])
        {
            case 0x02 when value == ProtocolRdp:
                return;
            case 0x02:
                throw new RdpCertificateException(CoreStrings.RdpNotRdp);
            case 0x03:
                throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpNegotiationRefused, value));
            default:
                throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }

    /// <summary>
    /// TPKT, X.224 Data, MCS Connect Initial (BER, T.125) portant la GCC Conference Create Request (PER, T.124) et les
    /// données du client : CS_CORE (1024 × 768, sécurité RDP choisie), CS_SECURITY (chiffrements proposés), CS_NET (aucun
    /// canal). Comme le client FreeRDP (libfreerdp/core/mcs.c, gcc.c).
    /// </summary>
    internal static byte[] ConnectInitial()
    {
        var core = new byte[216];
        var c = core.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(c, 0xC001);
        BinaryPrimitives.WriteUInt16LittleEndian(c[2..], (ushort)core.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(c[4..], 0x00080004); // RDP 5.0 et suivants
        BinaryPrimitives.WriteUInt16LittleEndian(c[8..], 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(c[10..], 768);
        BinaryPrimitives.WriteUInt16LittleEndian(c[12..], 0xCA01); // RNS_UD_COLOR_8BPP
        BinaryPrimitives.WriteUInt16LittleEndian(c[14..], 0xAA03); // RNS_UD_SAS_DEL
        BinaryPrimitives.WriteUInt32LittleEndian(c[16..], 0x0409); // clavier : anglais (États-Unis)
        BinaryPrimitives.WriteUInt32LittleEndian(c[20..], 2600); // clientBuild
        Encoding.Unicode.GetBytes("ZILLATERM", c[24..56]);
        BinaryPrimitives.WriteUInt32LittleEndian(c[56..], 4); // keyboardType : IBM 101/102
        BinaryPrimitives.WriteUInt32LittleEndian(c[64..], 12); // keyboardFunctionKey
        // imeFileName (64 octets) : vide.
        BinaryPrimitives.WriteUInt16LittleEndian(c[132..], 0xCA01); // postBeta2ColorDepth
        BinaryPrimitives.WriteUInt16LittleEndian(c[134..], 1); // clientProductId
        BinaryPrimitives.WriteUInt16LittleEndian(c[140..], 24); // highColorDepth
        BinaryPrimitives.WriteUInt16LittleEndian(c[142..], 0x000F); // supportedColorDepths
        BinaryPrimitives.WriteUInt16LittleEndian(c[144..], 0x0001); // RNS_UD_CS_SUPPORT_ERRINFO_PDU
        // clientDigProductId (64 octets), connectionType, pad1octet : vides ; serverSelectedProtocol : sécurité RDP (0).

        byte[] security = [0x02, 0xC0, 0x0C, 0x00, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // 40, 56, 128 bits, FIPS
        byte[] network = [0x03, 0xC0, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00]; // aucun canal
        byte[] userData = [.. core, .. security, .. network];

        // GCC Conference Create Request : clé T.124 (0.0.20.124.0.1), connectPDU, conférence « 1 », clé H.221 « Duca ».
        byte[] gcc =
        [
            0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01,
            .. PerLength(userData.Length + 14),
            0x00, 0x08, 0x00, 0x10, 0x00, 0x01, 0xC0, 0x00, (byte)'D', (byte)'u', (byte)'c', (byte)'a',
            .. PerLength(userData.Length),
            .. userData,
        ];

        byte[] target = DomainParameters(34, 2, 0, 1, 0, 1, 65535, 2);
        byte[] minimum = DomainParameters(1, 1, 1, 1, 0, 1, 1056, 2);
        byte[] maximum = DomainParameters(65535, 64535, 65535, 1, 0, 1, 65535, 2);
        byte[] content =
        [
            0x04, 0x01, 0x01, // callingDomainSelector
            0x04, 0x01, 0x01, // calledDomainSelector
            0x01, 0x01, 0xFF, // upwardFlag
            .. target, .. minimum, .. maximum,
            0x04, .. BerLength(gcc.Length), .. gcc,
        ];
        byte[] mcs = [0x7F, 0x65, .. BerLength(content.Length), .. content];
        int length = 4 + 3 + mcs.Length;
        return [0x03, 0x00, (byte)(length >> 8), (byte)length, 0x02, 0xF0, 0x80, .. mcs];
    }

    /// <summary>
    /// Clé du serveur dans sa réponse (X.224 Data, MCS Connect Response, GCC Conference Create Response, Server
    /// Security Data) : certificat propriétaire (clé RSA « RSA1 ») ou chaîne X.509 (le dernier certificat est celui du
    /// serveur).
    /// </summary>
    /// <exception cref="RdpCertificateException">Réponse incorrecte, refus ou serveur sans chiffrement.</exception>
    internal static RdpServerKey ReadServerKey(ReadOnlySpan<byte> tpdu)
    {
        try
        {
            if (tpdu.Length < 3 || tpdu[0] != 0x02 || tpdu[1] != 0xF0)
            {
                throw new RdpCertificateException(CoreStrings.RdpNotRdp);
            }

            // MCS Connect Response (BER, [APPLICATION 102]) : résultat, identifiant, paramètres, données de l'utilisateur.
            var reader = new AsnReader(tpdu[3..].ToArray(), AsnEncodingRules.BER);
            var response = reader.ReadSequence(new Asn1Tag(TagClass.Application, 102, isConstructed: true));
            var result = response.ReadEncodedValue();
            if (result.Length < 3 || result.Span[^1] != 0)
            {
                throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpNegotiationRefused,
                    result.Length >= 3 ? result.Span[^1] : -1));
            }

            response.ReadEncodedValue(); // calledConnectId
            response.ReadEncodedValue(); // domainParameters
            var gcc = response.ReadOctetString();
            return ReadSecurityData(gcc);
        }
        catch (AsnContentException)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Réponse tronquée : lue au-delà de sa fin.
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }

    /// <summary>Données du serveur après la clé H.221 « McDn » de la GCC Conference Create Response.</summary>
    private static RdpServerKey ReadSecurityData(ReadOnlySpan<byte> gcc)
    {
        int key = gcc.IndexOf("McDn"u8);
        if (key < 0)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        var rest = gcc[(key + 4)..];
        if (rest.Length < 2)
        {
            // Ni longueur ni données après « McDn » : réponse tronquée.
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        int length = rest[0];
        int header = 1;
        if ((length & 0x80) != 0)
        {
            length = ((length & 0x7F) << 8) | rest[1];
            header = 2;
        }

        var blocks = rest.Slice(header, Math.Min(length, rest.Length - header));
        while (blocks.Length >= 4)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(blocks);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(blocks[2..]);
            if (size < 4 || size > blocks.Length)
            {
                break;
            }

            if (type == 0x0C02)
            {
                return ReadSecurityBlock(blocks[4..size]);
            }

            blocks = blocks[size..];
        }

        throw new RdpCertificateException(CoreStrings.RdpNotRdp);
    }

    /// <summary>SC_SECURITY : méthode et niveau de chiffrement, aléa du serveur, certificat du serveur.</summary>
    private static RdpServerKey ReadSecurityBlock(ReadOnlySpan<byte> block)
    {
        uint method = BinaryPrimitives.ReadUInt32LittleEndian(block);
        uint level = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (method == 0 || level == 0)
        {
            // Aucun chiffrement : pas de clé, rien n'authentifie le serveur ni ne protège la suite.
            throw new RdpCertificateException(CoreStrings.RdpNoEncryption);
        }

        int randomLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[8..]);
        int certificateLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(block[12..]);
        if (randomLength < 0 || certificateLength <= 0 || 16L + randomLength + certificateLength > block.Length)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        return FromServerCertificate(block.Slice(16 + randomLength, certificateLength));
    }

    /// <summary>Certificat du serveur (2.2.1.4.3.1) : propriétaire (version 1) ou chaîne X.509 (version 2).</summary>
    internal static RdpServerKey FromServerCertificate(ReadOnlySpan<byte> certificate)
    {
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(certificate) & 0x7FFF_FFFF;
        switch (version)
        {
            case 1:
            {
                // dwSigAlgId, dwKeyAlgId, wPublicKeyBlobType (BB_RSA_KEY_BLOB), wPublicKeyBlobLen, puis RSA_PUBLIC_KEY.
                int blobLength = BinaryPrimitives.ReadUInt16LittleEndian(certificate[14..]);
                var blob = certificate.Slice(16, blobLength);
                if (!blob[..4].SequenceEqual("RSA1"u8))
                {
                    throw new RdpCertificateException(CoreStrings.RdpNotRdp);
                }

                uint keyLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]);
                uint bitLength = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
                if (keyLength <= 8 || keyLength != bitLength / 8 + 8 || 20 + keyLength > blob.Length)
                {
                    throw new RdpCertificateException(CoreStrings.RdpNotRdp);
                }

                // Exposant puis module, petit-boutistes ; 8 octets nuls après le module.
                var exponent = blob.Slice(16, 4).ToArray();
                var modulus = blob.Slice(20, (int)keyLength - 8).ToArray();
                Array.Reverse(exponent);
                Array.Reverse(modulus);
                return Key(modulus, exponent);
            }

            case 2:
            {
                int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(certificate[4..]);
                var rest = certificate[8..];
                ReadOnlySpan<byte> last = default;
                for (int i = 0; i < count; i++)
                {
                    int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(rest);
                    if (length <= 0 || length > rest.Length - 4)
                    {
                        throw new RdpCertificateException(CoreStrings.RdpNotRdp);
                    }

                    last = rest.Slice(4, length);
                    rest = rest[(4 + length)..];
                }

                if (last.IsEmpty)
                {
                    throw new RdpCertificateException(CoreStrings.RdpNotRdp);
                }

                return FromX509(last);
            }

            default:
                throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }

    /// <summary>Clé RSA d'un certificat X.509 (SubjectPublicKeyInfo), lue sans autre vérification.</summary>
    private static RdpServerKey FromX509(ReadOnlySpan<byte> der)
    {
        try
        {
            var certificate = new AsnReader(der.ToArray(), AsnEncodingRules.BER).ReadSequence();
            var tbs = certificate.ReadSequence();
            if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                tbs.ReadEncodedValue(); // version
            }

            tbs.ReadEncodedValue(); // serialNumber
            tbs.ReadEncodedValue(); // signature
            tbs.ReadEncodedValue(); // issuer
            tbs.ReadEncodedValue(); // validity
            tbs.ReadEncodedValue(); // subject
            var spki = tbs.ReadSequence();
            spki.ReadEncodedValue(); // algorithm
            var rsa = new AsnReader(spki.ReadBitString(out _), AsnEncodingRules.BER).ReadSequence();
            var modulus = rsa.ReadIntegerBytes().ToArray();
            var exponent = rsa.ReadIntegerBytes().ToArray();
            return Key(modulus, exponent);
        }
        catch (AsnContentException)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }

    /// <summary>Empreinte de la clé : SHA-256 de son encodage DER PKCS#1 (RSAPublicKey : module, exposant).</summary>
    internal static RdpServerKey Key(byte[] modulus, byte[] exponent)
    {
        var n = Unsigned(modulus);
        var e = Unsigned(exponent);
        if (n.Length == 0 || e.Length == 0 || e.Length > 4)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.WriteIntegerUnsigned(n);
        writer.WriteIntegerUnsigned(e);
        writer.PopSequence();
        return new RdpServerKey(Convert.ToHexString(SHA256.HashData(writer.Encode())), n.Length * 8);
    }

    /// <summary>Entier non signé, gros-boutiste, sans zéros de tête.</summary>
    private static ReadOnlySpan<byte> Unsigned(byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }

        return value.AsSpan(start);
    }

    private static byte[] PerLength(int length) =>
        length < 0x80 ? [(byte)length] : [(byte)(0x80 | (length >> 8)), (byte)length];

    private static byte[] BerLength(int length) => length switch
    {
        < 0x80 => [(byte)length],
        <= 0xFF => [0x81, (byte)length],
        _ => [0x82, (byte)(length >> 8), (byte)length],
    };

    /// <summary>DomainParameters (T.125) : huit entiers BER.</summary>
    private static byte[] DomainParameters(params int[] values)
    {
        var content = new List<byte>();
        foreach (var value in values)
        {
            byte[] bytes = value switch
            {
                < 0x80 => [(byte)value],
                < 0x8000 => [(byte)(value >> 8), (byte)value],
                _ => [(byte)(value >> 16), (byte)(value >> 8), (byte)value],
            };
            content.Add(0x02);
            content.Add((byte)bytes.Length);
            content.AddRange(bytes);
        }

        return [0x30, .. BerLength(content.Count), .. content];
    }

    /// <summary>Un paquet TPKT (version 3) : son contenu, sans l'en-tête.</summary>
    private static async Task<byte[]> ReadTpktAsync(NetworkStream network, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(network, header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 0x03 || length < 7)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        var body = new byte[length - 4];
        await ReadExactlyAsync(network, body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task ReadExactlyAsync(NetworkStream network, byte[] buffer, CancellationToken ct)
    {
        try
        {
            await network.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }
}
