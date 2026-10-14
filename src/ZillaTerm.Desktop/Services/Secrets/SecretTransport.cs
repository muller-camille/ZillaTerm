using System.Numerics;
using System.Security.Cryptography;

namespace ZillaTerm.App.Services.Secrets;

/// <summary>
/// Chiffrement des secrets entre ZillaTerm et le trousseau, sur le bus de session (algorithme
/// « dh-ietf1024-sha256-aes128-cbc-pkcs7 » de la spécification Secret Service) : échange Diffie-Hellman sur le deuxième
/// groupe Oakley (RFC 2409, 1024 bits, générateur 2), clé AES-128 tirée du secret partagé par HKDF-SHA256 (sans sel ni
/// info), puis AES-128-CBC avec bourrage PKCS#7 et un vecteur d'initialisation aléatoire par secret. La clé de protection
/// ne passe ainsi jamais en clair sur le bus (ni dans les tampons de messages D-Bus, qui ne sont pas effacés).
/// </summary>
/// <remarks>
/// Sans ce chiffrement (« plain », trousseau qui ne connaît pas cet algorithme), le secret passe en clair sur le bus de
/// session : il n'est lisible que par les programmes du même utilisateur, qui peuvent de toute façon lire le trousseau.
/// </remarks>
internal sealed class SecretTransport : IDisposable
{
    public const string Algorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7";

    private const int GroupBytes = 128;
    private const int AesKeyBytes = 16;
    private const int IvBytes = 16;

    // Deuxième groupe Oakley (RFC 2409, section 6.2) : 2^1024 - 2^960 - 1 + 2^64 * ([2^894 pi] + 129093).
    private static readonly BigInteger Prime = new(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B" +
        "302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE6" +
        "49286651ECE65381FFFFFFFFFFFFFFFF"), isUnsigned: true, isBigEndian: true);

    private static readonly BigInteger Generator = 2;

    private BigInteger _private;
    private byte[]? _key;

    private SecretTransport(BigInteger privateKey, byte[] publicKey)
    {
        _private = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>Clé publique de ZillaTerm (grand-boutiste, 128 octets), à envoyer à OpenSession.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Nouvelle paire de clés Diffie-Hellman éphémère.</summary>
    public static SecretTransport Create()
    {
        var random = new byte[GroupBytes];
        RandomNumberGenerator.Fill(random);
        // Exposant privé dans [2, p - 2].
        var privateKey = new BigInteger(random, isUnsigned: true, isBigEndian: true) % (Prime - 3) + 2;
        CryptographicOperations.ZeroMemory(random);
        var publicKey = ToFixedBytes(BigInteger.ModPow(Generator, privateKey, Prime));
        return new SecretTransport(privateKey, publicKey);
    }

    /// <summary>Calcule la clé de session à partir de la clé publique du trousseau (réponse d'OpenSession).</summary>
    /// <exception cref="CryptographicException">Clé publique du trousseau hors du groupe.</exception>
    public void Complete(byte[] servicePublicKey)
    {
        var peer = new BigInteger(servicePublicKey, isUnsigned: true, isBigEndian: true);
        // 0, 1 et p - 1 (et au-delà) donneraient un secret partagé connu de tous.
        if (servicePublicKey.Length > GroupBytes || peer <= BigInteger.One || peer >= Prime - BigInteger.One)
        {
            throw new CryptographicException("Invalid Secret Service public key.");
        }

        var shared = ToFixedBytes(BigInteger.ModPow(peer, _private, Prime));
        try
        {
            var key = GC.AllocateArray<byte>(AesKeyBytes, pinned: true);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, key, salt: [], info: []);
            _key = key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            _private = BigInteger.Zero;
        }
    }

    /// <summary>Chiffre un secret pour le trousseau : (paramètres = vecteur d'initialisation, valeur chiffrée).</summary>
    public (byte[] Parameters, byte[] Value) Encrypt(ReadOnlySpan<byte> plain)
    {
        using var aes = CreateAes();
        var iv = new byte[IvBytes];
        RandomNumberGenerator.Fill(iv);
        return (iv, aes.EncryptCbc(plain, iv, PaddingMode.PKCS7));
    }

    /// <summary>Déchiffre un secret reçu du trousseau, dans un tableau épinglé que l'appelant efface.</summary>
    /// <exception cref="CryptographicException">Secret mal formé.</exception>
    public byte[] Decrypt(byte[] parameters, byte[] value)
    {
        if (parameters.Length != IvBytes || value.Length == 0 || value.Length % IvBytes != 0)
        {
            throw new CryptographicException("Invalid Secret Service secret.");
        }

        using var aes = CreateAes();
        var buffer = GC.AllocateArray<byte>(value.Length, pinned: true);
        try
        {
            if (!aes.TryDecryptCbc(value, parameters, buffer, out int written, PaddingMode.PKCS7))
            {
                throw new CryptographicException("Invalid Secret Service secret.");
            }

            var plain = GC.AllocateArray<byte>(written, pinned: true);
            buffer.AsSpan(0, written).CopyTo(plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public void Dispose()
    {
        if (_key is { } key)
        {
            CryptographicOperations.ZeroMemory(key);
            _key = null;
        }

        _private = BigInteger.Zero;
    }

    private Aes CreateAes()
    {
        var key = _key ?? throw new InvalidOperationException("Secret Service session not established.");
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    /// <summary>Entier en grand-boutiste, complété à gauche par des zéros jusqu'à la taille du groupe.</summary>
    private static byte[] ToFixedBytes(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == GroupBytes)
        {
            return bytes;
        }

        var padded = new byte[GroupBytes];
        bytes.CopyTo(padded, GroupBytes - bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
        return padded;
    }
}
