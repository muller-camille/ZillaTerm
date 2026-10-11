using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Lecture du certificat d'un faux serveur Bureau à distance : négociation X.224, TLS jusqu'au certificat, et rien
/// d'autre d'envoyé ; serveurs sans TLS, refus et réponses incorrectes signalés.
/// </summary>
public sealed class RdpCertificateProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Faux serveur : une connexion, lit la demande X.224, répond <paramref name="confirm"/>, puis TLS si un certificat est donné.</summary>
    private sealed class FakeRdp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeRdp(byte[] confirm, X509Certificate2? certificate = null)
        {
            _listener.Start();
            Run = Task.Run(async () =>
            {
                using var socket = await _listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                Request = new byte[19];
                await stream.ReadExactlyAsync(Request);
                await stream.WriteAsync(confirm);
                if (certificate is not null)
                {
                    using var tls = new SslStream(stream);
                    try
                    {
                        await tls.AuthenticateAsServerAsync(certificate);
                        // Après la poignée de main, le client ne doit rien envoyer (ni identifiant, ni mot de passe).
                        AfterHandshake = await tls.ReadAsync(new byte[16]);
                    }
                    catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException)
                    {
                        // Certificat refusé par le client : attendu.
                        AfterHandshake = 0;
                    }
                }

                await Task.Delay(100);
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task Run { get; }

        public byte[]? Request { get; private set; }

        public int AfterHandshake { get; private set; } = -1;

        public void Dispose() => _listener.Stop();
    }

    /// <summary>X.224 Connection Confirm avec RDP_NEG_RSP (protocole choisi) ou RDP_NEG_FAILURE (code).</summary>
    private static byte[] Confirm(byte type, uint value) =>
        [0x03, 0x00, 0x00, 0x13, 0x0E, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00, type, 0x00, 0x08, 0x00, (byte)value, 0x00, 0x00, 0x00];

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=srv01.corp.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(8u)]
    public async Task ReadsTheCertificateAndSendsNothingElse(uint selected)
    {
        using var certificate = SelfSigned();
        using var server = new FakeRdp(Confirm(0x02, selected), certificate);

        var received = await RdpCertificateProbe.GetAsync("127.0.0.1", server.Port, CancellationToken.None).WaitAsync(Timeout);
        await server.Run.WaitAsync(Timeout);

        Assert.Equal(RdpCertificateProbe.ConnectionRequest.ToArray(), server.Request);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), received.Sha256);
        Assert.Contains("srv01.corp.local", received.Subject);
        // Auto-signé : Windows ne l'approuve pas, il faudra l'épingler.
        Assert.False(received.Trusted);
        Assert.False(string.IsNullOrWhiteSpace(received.Problem));
        Assert.Equal(0, server.AfterHandshake);
    }

    /// <summary>Ancienne « sécurité RDP » (pas de négociation, ou TLS refusé) : pas de certificat, la connexion s'arrête.</summary>
    [Fact]
    public async Task ServerWithoutTlsIsRefused()
    {
        foreach (var confirm in new[]
                 {
                     new byte[] { 0x03, 0x00, 0x00, 0x0B, 0x06, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00 },
                     Confirm(0x02, 0),
                     Confirm(0x03, 2),
                 })
        {
            using var server = new FakeRdp(confirm);
            var error = await Assert.ThrowsAsync<RdpCertificateException>(() => RdpCertificateProbe.GetAsync("127.0.0.1", server.Port, CancellationToken.None));
            Assert.Equal(CoreStrings.RdpNoTls, error.Message);
        }
    }

    [Fact]
    public async Task RefusalsAndOtherServersAreReported()
    {
        using (var server = new FakeRdp(Confirm(0x03, 5)))
        {
            var error = await Assert.ThrowsAsync<RdpCertificateException>(() => RdpCertificateProbe.GetAsync("127.0.0.1", server.Port, CancellationToken.None));
            Assert.Contains("5", error.Message, StringComparison.Ordinal);
            Assert.NotEqual(CoreStrings.RdpNoTls, error.Message);
        }

        using (var server = new FakeRdp("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray()))
        {
            var error = await Assert.ThrowsAsync<RdpCertificateException>(() => RdpCertificateProbe.GetAsync("127.0.0.1", server.Port, CancellationToken.None));
            Assert.Equal(CoreStrings.RdpNotRdp, error.Message);
        }
    }
}
