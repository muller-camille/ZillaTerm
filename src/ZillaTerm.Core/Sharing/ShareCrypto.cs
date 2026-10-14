using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ZillaTerm.Core.Sharing;

/// <summary>Clés d'un échange établi : une par sens, et le code de vérification à comparer de vive voix.</summary>
public sealed class ShareSessionKeys(byte[] send, byte[] receive, string verificationCode)
{
    public byte[] Send { get; } = send;

    public byte[] Receive { get; } = receive;

    /// <summary>
    /// Six chiffres (« 482 913 ») calculés des deux côtés à partir de l'échange : identiques chez celui qui partage et
    /// chez celui qui regarde, sauf si quelqu'un s'est glissé entre les deux.
    /// </summary>
    public string VerificationCode { get; } = verificationCode;
}

/// <summary>
/// Ouverture d'un échange de partage, la même par connexion directe ou par dossier relais : deux messages en clair,
/// chacun authentifié par le secret de l'invitation (HMAC-SHA256), qui portent chacun une clé Diffie-Hellman éphémère
/// (P-256). Les clés de session viennent du secret Diffie-Hellman et de celui de l'invitation (HKDF-SHA256).
/// </summary>
/// <remarks>
/// <para>
/// Sans le secret de l'invitation, un message n'est pas accepté. Avec lui mais sans la clé éphémère d'un côté, l'échange
/// ne se déchiffre pas : quelqu'un qui aurait l'invitation et lirait le dossier relais ne voit rien des sessions des
/// autres. Celui qui a l'invitation peut demander à regarder ; c'est l'accord de celui qui partage, avec le code de
/// vérification, qui décide.
/// </para>
/// <para>Messages : « ZTS1 », type, puis (bonjour) identifiant du partage, clé publique, aléa, HMAC ; (réponse) clé
/// publique, aléa, HMAC du bonjour et de la réponse.</para>
/// </remarks>
public static class ShareHandshake
{
    public const int PublicKeyLength = 65;

    public const int NonceLength = 32;

    public const int MacLength = 32;

    public const int HelloLength = 4 + 1 + ShareInvitation.IdLength + PublicKeyLength + NonceLength + MacLength;

    public const int WelcomeLength = 4 + 1 + PublicKeyLength + NonceLength + MacLength;

    private const byte HelloType = 1;
    private const byte WelcomeType = 2;

    private static readonly byte[] Salt = "zillaterm-share-v1"u8.ToArray();

    private static ReadOnlySpan<byte> Magic => "ZTS1"u8;

    /// <summary>Côté invité, entre le bonjour et la réponse.</summary>
    public sealed class GuestState : IDisposable
    {
        internal GuestState(ECDiffieHellman key, byte[] hello, byte[] secret)
        {
            Key = key;
            Hello = hello;
            Secret = secret;
        }

        internal ECDiffieHellman Key { get; }

        public byte[] Hello { get; }

        internal byte[] Secret { get; }

        public void Dispose() => Key.Dispose();
    }

    /// <summary>Bonjour de l'invité : à envoyer à celui qui partage.</summary>
    public static GuestState CreateHello(ShareInvitation invitation)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new byte[HelloLength];
        var span = hello.AsSpan();
        Magic.CopyTo(span);
        span[4] = HelloType;
        invitation.ShareId.CopyTo(span[5..]);
        WritePublicKey(key, span.Slice(5 + ShareInvitation.IdLength, PublicKeyLength));
        RandomNumberGenerator.Fill(span.Slice(5 + ShareInvitation.IdLength + PublicKeyLength, NonceLength));
        Mac(invitation.Secret, "hello", span[..^MacLength], span[^MacLength..]);
        return new GuestState(key, hello, invitation.Secret);
    }

    /// <summary>Identifiant du partage visé par un bonjour (pour trouver son secret) ; faux si ce n'en est pas un.</summary>
    public static bool TryReadShareId(ReadOnlySpan<byte> hello, out byte[] shareId)
    {
        shareId = [];
        if (hello.Length != HelloLength || !hello.StartsWith(Magic) || hello[4] != HelloType)
        {
            return false;
        }

        shareId = hello.Slice(5, ShareInvitation.IdLength).ToArray();
        return true;
    }

    /// <summary>
    /// Réponse de celui qui partage à un bonjour : null si le bonjour n'est pas authentifié par
    /// <paramref name="secret"/> (invitation fausse ou ancienne) ou porte une clé invalide.
    /// </summary>
    public static (byte[] Welcome, ShareSessionKeys Keys)? AnswerHello(ReadOnlySpan<byte> hello, byte[] secret)
    {
        if (!TryReadShareId(hello, out _) || !Verify(secret, "hello", hello[..^MacLength], hello[^MacLength..]))
        {
            return null;
        }

        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var welcome = new byte[WelcomeLength];
        var span = welcome.AsSpan();
        Magic.CopyTo(span);
        span[4] = WelcomeType;
        WritePublicKey(key, span.Slice(5, PublicKeyLength));
        RandomNumberGenerator.Fill(span.Slice(5 + PublicKeyLength, NonceLength));
        var transcript = Concat(hello, span[..^MacLength]);
        Mac(secret, "welcome", transcript, span[^MacLength..]);
        if (Agree(key, hello.Slice(5 + ShareInvitation.IdLength, PublicKeyLength)) is not { } shared)
        {
            return null;
        }

        var keys = DeriveKeys(shared, secret, hello, welcome, guest: false);
        CryptographicOperations.ZeroMemory(shared);
        return (welcome, keys);
    }

    /// <summary>Clés de l'invité d'après la réponse ; null si elle n'est pas authentifiée par le secret de l'invitation.</summary>
    public static ShareSessionKeys? AcceptWelcome(GuestState state, ReadOnlySpan<byte> welcome)
    {
        if (welcome.Length != WelcomeLength || !welcome.StartsWith(Magic) || welcome[4] != WelcomeType
            || !Verify(state.Secret, "welcome", Concat(state.Hello, welcome[..^MacLength]), welcome[^MacLength..])
            || Agree(state.Key, welcome.Slice(5, PublicKeyLength)) is not { } shared)
        {
            return null;
        }

        var keys = DeriveKeys(shared, state.Secret, state.Hello, welcome, guest: true);
        CryptographicOperations.ZeroMemory(shared);
        return keys;
    }

    private static ShareSessionKeys DeriveKeys(byte[] shared, byte[] secret, ReadOnlySpan<byte> hello, ReadOnlySpan<byte> welcome, bool guest)
    {
        var info = Concat(Encoding.ASCII.GetBytes("zillaterm-share-v1 keys"), SHA256.HashData(Concat(hello, welcome)));
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared, secret);
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32 + 32 + 4, info);
        CryptographicOperations.ZeroMemory(prk);
        var guestToHost = okm[..32];
        var hostToGuest = okm[32..64];
        uint code = BinaryPrimitives.ReadUInt32BigEndian(okm.AsSpan(64)) % 1_000_000;
        CryptographicOperations.ZeroMemory(okm);
        var text = $"{code / 1000:000} {code % 1000:000}";
        return guest ? new ShareSessionKeys(guestToHost, hostToGuest, text) : new ShareSessionKeys(hostToGuest, guestToHost, text);
    }

    private static void WritePublicKey(ECDiffieHellman key, Span<byte> target)
    {
        var q = key.ExportParameters(false).Q;
        target[0] = 4;
        q.X!.CopyTo(target[1..]);
        q.Y!.CopyTo(target[33..]);
    }

    /// <summary>Secret Diffie-Hellman avec la clé publique reçue ; null si elle n'est pas un point valide de P-256.</summary>
    private static byte[]? Agree(ECDiffieHellman key, ReadOnlySpan<byte> peer)
    {
        if (peer.Length != PublicKeyLength || peer[0] != 4)
        {
            return null;
        }

        try
        {
            using var other = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = peer.Slice(1, 32).ToArray(), Y = peer.Slice(33, 32).ToArray() },
            });
            return key.DeriveRawSecretAgreement(other.PublicKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] MacKey(byte[] secret, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Salt, Encoding.ASCII.GetBytes("zillaterm-share-v1 " + purpose));

    private static void Mac(byte[] secret, string purpose, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        var key = MacKey(secret, purpose);
        HMACSHA256.HashData(key, data, mac);
        CryptographicOperations.ZeroMemory(key);
    }

    private static bool Verify(byte[] secret, string purpose, ReadOnlySpan<byte> data, ReadOnlySpan<byte> mac)
    {
        Span<byte> expected = stackalloc byte[MacLength];
        Mac(secret, purpose, data, expected);
        return CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }
}

/// <summary>
/// Messages chiffrés d'un échange établi : AES-256-GCM, une clé par sens, un compteur par sens comme aléa (jamais
/// réutilisé). Un message altéré, rejoué, retiré ou déplacé ne se déchiffre pas, et l'échange s'arrête.
/// </summary>
public sealed class ShareCipher : IDisposable
{
    public const int TagLength = 16;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();
    private ulong _sent;
    private ulong _received;

    public ShareCipher(ShareSessionKeys keys)
    {
        _send = new AesGcm(keys.Send, TagLength);
        _receive = new AesGcm(keys.Receive, TagLength);
    }

    /// <summary>Message chiffré, étiquette d'authentification à la fin.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var sealedData = new byte[plain.Length + TagLength];
        lock (_sendLock)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _sent++);
            _send.Encrypt(nonce, plain, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length));
        }

        return sealedData;
    }

    /// <summary>Message déchiffré ; null s'il ne s'authentifie pas (altéré, rejoué, hors d'ordre).</summary>
    public byte[]? Open(ReadOnlySpan<byte> sealedData)
    {
        if (sealedData.Length < TagLength)
        {
            return null;
        }

        var plain = new byte[sealedData.Length - TagLength];
        lock (_receiveLock)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _received);
            try
            {
                _receive.Decrypt(nonce, sealedData[..plain.Length], sealedData[plain.Length..], plain);
            }
            catch (AuthenticationTagMismatchException)
            {
                return null;
            }

            _received++;
        }

        return plain;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}
