using System.Net;
using System.Security.Cryptography;
using ZillaTerm.Core.Sharing;

namespace ZillaTerm.Core.Tests.Sharing;

public sealed class ShareCryptoTests
{
    /// <summary>Dossier relais d'exemple : chemin réseau sous Windows, chemin absolu ailleurs (tests sous Linux).</summary>
    private static readonly string Relay = OperatingSystem.IsWindows() ? @"\\files.corp.local\equipe\zillaterm-relais" : "/srv/equipe/relais";

    private static ShareInvitation Invitation(string? relay = "") =>
        ShareInvitation.Create([new IPEndPoint(IPAddress.Parse("10.1.2.3"), 51234), new IPEndPoint(IPAddress.Parse("fd00::12"), 51234)],
            relay == "" ? Relay : relay, new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Utc));

    /// <summary>L'invitation se relit telle quelle, même entourée de texte (message Teams) ou coupée en lignes.</summary>
    [Fact]
    public void InvitationRoundTrips()
    {
        var invitation = Invitation();
        var text = invitation.ToString();
        Assert.StartsWith(ShareInvitation.Prefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("srv01", text, StringComparison.Ordinal);

        var pasted = "Voici l'invitation :\r\n" + text[..40] + "\r\n" + text[40..] + "\r\nà tout de suite";
        Assert.True(ShareInvitation.TryParse(pasted, out var read));
        Assert.Equal(invitation.ShareId, read.ShareId);
        Assert.Equal(invitation.Secret, read.Secret);
        Assert.Equal(invitation.Endpoints, read.Endpoints);
        Assert.Equal(invitation.RelayFolder, read.RelayFolder);
        Assert.Equal(invitation.ExpiresAt, read.ExpiresAt);

        Assert.True(ShareInvitation.TryParse(Invitation(relay: null).ToString(), out var direct));
        Assert.Null(direct.RelayFolder);
    }

    /// <summary>Une invitation abîmée, tronquée ou piégée est refusée.</summary>
    [Fact]
    public void BadInvitationsAreRefused()
    {
        var text = Invitation().ToString();
        foreach (var bad in new[]
        {
            "", "bonjour", ShareInvitation.Prefix, text[..^4], text + "AAAA", text.Replace(ShareInvitation.Prefix, "zillaterm-share:2:"),
            ShareInvitation.Prefix + "%%%%",
        })
        {
            Assert.False(ShareInvitation.TryParse(bad, out _), bad);
        }

        foreach (var relay in new[] { "relais", @"\\srv\partage\..\..\Windows", "C:\\a\u202Etxt", @"\\.\pipe\x" })
        {
            Assert.False(ShareInvitation.IsRelayFolder(relay), relay);
        }

        // Ni adresse ni dossier relais : rien pour joindre.
        Assert.False(ShareInvitation.TryParse(ShareInvitation.Create([], null, DateTime.UtcNow).ToString(), out _));
    }

    /// <summary>
    /// Bonjour et réponse authentifiés par le secret : les deux côtés obtiennent les mêmes clés (croisées) et le même
    /// code de vérification ; les messages chiffrés passent dans les deux sens.
    /// </summary>
    [Fact]
    public void HandshakeAgreesOnKeysAndCode()
    {
        var invitation = Invitation();
        using var guest = ShareHandshake.CreateHello(invitation);
        Assert.True(ShareHandshake.TryReadShareId(guest.Hello, out var id));
        Assert.Equal(invitation.ShareId, id);

        var answer = ShareHandshake.AnswerHello(guest.Hello, invitation.Secret);
        Assert.NotNull(answer);
        var (welcome, hostKeys) = answer.Value;
        var guestKeys = ShareHandshake.AcceptWelcome(guest, welcome);
        Assert.NotNull(guestKeys);
        Assert.Equal(hostKeys.Send, guestKeys.Receive);
        Assert.Equal(hostKeys.Receive, guestKeys.Send);
        Assert.NotEqual(hostKeys.Send, hostKeys.Receive);
        Assert.Matches(@"^\d{3} \d{3}$", hostKeys.VerificationCode);
        Assert.Equal(hostKeys.VerificationCode, guestKeys.VerificationCode);

        using var host = new ShareCipher(hostKeys);
        using var viewer = new ShareCipher(guestKeys);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("écran"u8.ToArray(), viewer.Open(host.Seal("écran"u8)));
            Assert.Equal("touche"u8.ToArray(), host.Open(viewer.Seal("touche"u8)));
        }
    }

    /// <summary>Sans le bon secret, ni le bonjour ni la réponse ne passent ; une clé publique fausse non plus.</summary>
    [Fact]
    public void WrongSecretOrKeyIsRefused()
    {
        var invitation = Invitation();
        using var guest = ShareHandshake.CreateHello(invitation);
        Assert.Null(ShareHandshake.AnswerHello(guest.Hello, RandomNumberGenerator.GetBytes(32)));

        var tampered = (byte[])guest.Hello.Clone();
        tampered[30] ^= 1;
        Assert.Null(ShareHandshake.AnswerHello(tampered, invitation.Secret));
        Assert.Null(ShareHandshake.AnswerHello(guest.Hello.AsSpan(0, 100), invitation.Secret));

        // Réponse d'un autre partage (autre secret) : refusée par l'invité.
        var other = Invitation();
        using var otherGuest = ShareHandshake.CreateHello(other);
        var foreign = ShareHandshake.AnswerHello(otherGuest.Hello, other.Secret)!.Value.Welcome;
        Assert.Null(ShareHandshake.AcceptWelcome(guest, foreign));

        // Clé publique qui n'est pas un point de la courbe, MAC recalculé avec le bon secret : refusée quand même.
        var forged = ShareHandshake.CreateHello(invitation);
        var bytes = (byte[])forged.Hello.Clone();
        Array.Fill(bytes, (byte)0x11, 5 + ShareInvitation.IdLength + 1, 64);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, invitation.Secret, 32, "zillaterm-share-v1"u8.ToArray(), "zillaterm-share-v1 hello"u8.ToArray());
        HMACSHA256.HashData(key, bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
        Assert.Null(ShareHandshake.AnswerHello(bytes, invitation.Secret));
    }

    /// <summary>Un message altéré, rejoué ou sauté ne se déchiffre pas.</summary>
    [Fact]
    public void CipherRefusesTamperingReplayAndReordering()
    {
        var invitation = Invitation();
        using var guest = ShareHandshake.CreateHello(invitation);
        var (welcome, hostKeys) = ShareHandshake.AnswerHello(guest.Hello, invitation.Secret)!.Value;
        var guestKeys = ShareHandshake.AcceptWelcome(guest, welcome)!;

        using (var host = new ShareCipher(hostKeys))
        using (var viewer = new ShareCipher(guestKeys))
        {
            var first = host.Seal("un"u8);
            var second = host.Seal("deux"u8);
            var altered = (byte[])first.Clone();
            altered[0] ^= 1;
            Assert.Null(viewer.Open(altered));
            Assert.Null(viewer.Open(second));
            Assert.NotNull(viewer.Open(first));
            Assert.Null(viewer.Open(first));
            Assert.Equal("deux"u8.ToArray(), viewer.Open(second));
        }

        // Le message d'un sens ne se lit pas comme venant de l'autre.
        using var a = new ShareCipher(hostKeys);
        Assert.Null(new ShareCipher(hostKeys).Open(a.Seal("x"u8)));
    }
}
