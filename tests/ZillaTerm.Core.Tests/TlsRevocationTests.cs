using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ZillaTerm.Core.Tests;

/// <summary>Révocation des certificats HTTPS (PVWA, GitHub) en « échec permissif ».</summary>
public class TlsRevocationTests
{
    /// <summary>Révoqué : refusé ; état inconnu (serveur de révocation injoignable) : accepté ; sans erreur : accepté.</summary>
    [Theory]
    [InlineData(X509ChainStatusFlags.NoError, true)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation, true)]
    [InlineData(X509ChainStatusFlags.Revoked, false)]
    [InlineData(X509ChainStatusFlags.Revoked | X509ChainStatusFlags.OfflineRevocation, false)]
    public void RevokedIsRefusedUnknownIsAccepted(X509ChainStatusFlags status, bool accepted) =>
        Assert.Equal(accepted, TlsRevocation.Accept("pvwa.corp.local", status));

    /// <summary>
    /// Certificat refusé par la validation habituelle (non approuvé, mauvais nom) : toujours refusé. Accepté par elle et
    /// dont la liste de révocation est injoignable : accepté.
    /// </summary>
    [Fact]
    public void UsualValidationStaysFirstAndAnUnreachableListDoesNotBlock()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=ZillaTerm Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pvwa.corp.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Liste de révocation sur un port fermé : injoignable, tout de suite.
        request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension(["http://127.0.0.1:1/ca.crl"]));
        using var leaf = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);

        Assert.False(TlsRevocation.Validate("pvwa.corp.local", leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(TlsRevocation.Validate("pvwa.corp.local", leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(TlsRevocation.Validate("pvwa.corp.local", leaf, null, SslPolicyErrors.None));
    }

    /// <summary>Client du PVWA : la révocation est vérifiée (rappel posé), et la validation habituelle garde le dernier mot.</summary>
    [Fact]
    public void PvwaHandlerChecksRevocation()
    {
        using var handler = PvwaClient.CreateHandler(AuthMethod.CyberArk);

        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://pvwa.corp.local/PasswordVault/");
        Assert.False(handler.ServerCertificateCustomValidationCallback(message, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }
}
