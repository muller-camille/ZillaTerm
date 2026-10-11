using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Lecture de la clé d'un serveur en ancienne sécurité RDP : demande envoyée, réponse du serveur (certificat
/// propriétaire ou X.509) et empreinte, la même que celle que FreeRDP compare (SHA-256 de la clé en DER PKCS#1).
/// </summary>
public class RdpSecurityProbeTests
{
    [Fact]
    public void ConnectInitialIsWellFormed()
    {
        var packet = RdpSecurityProbe.ConnectInitial();

        Assert.Equal(0x03, packet[0]);
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(new byte[] { 0x02, 0xF0, 0x80 }, packet[4..7]);

        // MCS Connect Initial (BER) : sélecteurs, drapeau, trois jeux de paramètres, puis les données GCC.
        var reader = new AsnReader(packet.AsMemory(7), AsnEncodingRules.BER);
        var initial = reader.ReadSequence(new Asn1Tag(TagClass.Application, 101, isConstructed: true));
        Assert.False(reader.HasData);
        Assert.Equal(new byte[] { 1 }, initial.ReadOctetString());
        Assert.Equal(new byte[] { 1 }, initial.ReadOctetString());
        Assert.True(initial.ReadBoolean());
        for (int i = 0; i < 3; i++)
        {
            var parameters = initial.ReadSequence();
            for (int j = 0; j < 8; j++)
            {
                parameters.ReadInteger();
            }

            Assert.False(parameters.HasData);
        }

        var gcc = initial.ReadOctetString();
        Assert.False(initial.HasData);

        // GCC (PER) : clé T.124, longueur du connectPDU, « Duca », longueur des données du client.
        Assert.Equal(new byte[] { 0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01 }, gcc[..7]);
        int connectPdu = ((gcc[7] & 0x7F) << 8) | gcc[8];
        Assert.Equal(gcc.Length - 9, connectPdu);
        Assert.Equal("Duca"u8.ToArray(), gcc[17..21]);
        int userData = ((gcc[21] & 0x7F) << 8) | gcc[22];
        Assert.Equal(gcc.Length - 23, userData);

        // Blocs du client : CS_CORE (sécurité RDP choisie), CS_SECURITY, CS_NET.
        var blocks = gcc.AsSpan(23);
        var types = new List<int>();
        while (blocks.Length > 0)
        {
            types.Add(BinaryPrimitives.ReadUInt16LittleEndian(blocks));
            blocks = blocks[BinaryPrimitives.ReadUInt16LittleEndian(blocks[2..])..];
        }

        Assert.Equal([0xC001, 0xC002, 0xC003], types);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(gcc.AsSpan(23 + 212)));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(2048)]
    public void ProprietaryCertificateGivesTheFingerprintOfTheKey(int bits)
    {
        using var rsa = RSA.Create(bits);
        var key = rsa.ExportParameters(false);

        var result = RdpSecurityProbe.ReadServerKey(ServerResponse(Proprietary(key)));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(rsa.ExportRSAPublicKey())), result.Sha256);
        Assert.Equal(bits, result.Bits);
    }

    [Fact]
    public void X509ChainGivesTheFingerprintOfTheServerKey()
    {
        using var issuerKey = RSA.Create(2048);
        using var serverKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=Licensing", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var server = new CertificateRequest("CN=srv01.corp.local", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(12), [1, 2, 3]);

        var result = RdpSecurityProbe.ReadServerKey(ServerResponse(Chain(issuer.RawData, server.RawData)));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(serverKey.ExportRSAPublicKey())), result.Sha256);
    }

    [Fact]
    public void ServerWithoutEncryptionIsRefused()
    {
        var security = new byte[8];
        var error = Assert.Throws<RdpCertificateException>(() => RdpSecurityProbe.ReadServerKey(ServerResponse(security, withKey: false)));
        Assert.Equal(Localization.CoreStrings.RdpNoEncryption, error.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0x0E, 0xD0, 0, 0, 0x12, 0x34, 0, 0x02, 0, 0x08, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0xD0, 0, 0, 0x12, 0x34, 0 }, true)]
    [InlineData(new byte[] { 0x0E, 0xD0, 0, 0, 0x12, 0x34, 0, 0x02, 0, 0x08, 0, 0x01, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x0E, 0xD0, 0, 0, 0x12, 0x34, 0, 0x03, 0, 0x08, 0, 0x05, 0, 0, 0 }, false)]
    public void OnlyTheLegacySecurityIsAccepted(byte[] confirm, bool accepted)
    {
        if (accepted)
        {
            RdpSecurityProbe.CheckConfirm(confirm);
        }
        else
        {
            Assert.Throws<RdpCertificateException>(() => RdpSecurityProbe.CheckConfirm(confirm));
        }
    }

    /// <summary>Certificat propriétaire : en-tête, puis RSA_PUBLIC_KEY (« RSA1 », exposant et module petit-boutistes).</summary>
    private static byte[] Proprietary(RSAParameters key)
    {
        var modulus = key.Modulus!.Reverse().ToArray();
        int keyLength = modulus.Length + 8;
        var blob = new byte[20 + keyLength];
        "RSA1"u8.CopyTo(blob);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)keyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)(modulus.Length * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), (uint)(modulus.Length - 1));
        var exponent = new byte[4];
        key.Exponent!.Reverse().ToArray().CopyTo(exponent, 0);
        exponent.CopyTo(blob, 16);
        modulus.CopyTo(blob, 20);

        var certificate = new byte[16 + blob.Length + 4 + 72];
        BinaryPrimitives.WriteUInt32LittleEndian(certificate, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(certificate.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(certificate.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(certificate.AsSpan(12), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(certificate.AsSpan(14), (ushort)blob.Length);
        blob.CopyTo(certificate, 16);
        return certificate;
    }

    private static byte[] Chain(params byte[][] certificates)
    {
        var chain = new List<byte>();
        chain.AddRange(BitConverter.GetBytes(0x8000_0002u));
        chain.AddRange(BitConverter.GetBytes((uint)certificates.Length));
        foreach (var certificate in certificates)
        {
            chain.AddRange(BitConverter.GetBytes((uint)certificate.Length));
            chain.AddRange(certificate);
        }

        chain.AddRange(new byte[16]);
        return [.. chain];
    }

    /// <summary>X.224 Data, MCS Connect Response, GCC Conference Create Response avec SC_CORE et SC_SECURITY.</summary>
    private static byte[] ServerResponse(byte[] certificateOrSecurity, bool withKey = true)
    {
        byte[] security;
        if (withKey)
        {
            security = new byte[4 + 16 + 32 + certificateOrSecurity.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(security.AsSpan(4), 0x02); // 128 bits
            BinaryPrimitives.WriteUInt32LittleEndian(security.AsSpan(8), 0x02); // client compatible
            BinaryPrimitives.WriteUInt32LittleEndian(security.AsSpan(12), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(security.AsSpan(16), (uint)certificateOrSecurity.Length);
            certificateOrSecurity.CopyTo(security, 4 + 16 + 32);
        }
        else
        {
            security = new byte[4 + certificateOrSecurity.Length];
            certificateOrSecurity.CopyTo(security, 4);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(security, 0x0C02);
        BinaryPrimitives.WriteUInt16LittleEndian(security.AsSpan(2), (ushort)security.Length);
        byte[] core = [0x01, 0x0C, 0x0C, 0x00, 0x04, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] blocks = [.. core, .. security];
        byte[] gcc =
        [
            0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01, 0x2A, 0x14, 0x76, 0x0A, 0x01, 0x01, 0x00, 0x01, 0xC0, 0x00,
            (byte)'M', (byte)'c', (byte)'D', (byte)'n',
            (byte)(0x80 | (blocks.Length >> 8)), (byte)blocks.Length,
            .. blocks,
        ];

        var writer = new AsnWriter(AsnEncodingRules.BER);
        using (writer.PushSequence(new Asn1Tag(TagClass.Application, 102, isConstructed: true)))
        {
            writer.WriteEncodedValue(new byte[] { 0x0A, 0x01, 0x00 }); // rt-successful
            writer.WriteInteger(0);
            using (writer.PushSequence())
            {
                foreach (var value in new[] { 34, 3, 0, 1, 0, 1, 65528, 2 })
                {
                    writer.WriteInteger(value);
                }
            }

            writer.WriteOctetString(gcc);
        }

        return [0x02, 0xF0, 0x80, .. writer.Encode()];
    }
}
