using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZillaTerm.Core.Localization;

namespace ZillaTerm.Core.Rdp;

/// <summary>Certificat TLS d'un serveur Bureau à distance, montré à l'utilisateur et épinglé.</summary>
/// <param name="Sha256">Empreinte SHA-256 du certificat (hexadécimal).</param>
/// <param name="Problem">Ce que le système lui reproche (nom, chaîne, dates) ; vide s'il l'approuve pour ce serveur.</param>
public sealed record RdpCertificate(string Subject, string Issuer, string Sha256, DateTime NotBefore, DateTime NotAfter, string Problem)
{
    /// <summary>Certificat approuvé par le système (autorité reconnue, nom du serveur, dates).</summary>
    public bool Trusted => Problem.Length == 0;
}

/// <summary>Le certificat du serveur Bureau à distance n'a pas pu être lu (pas de TLS, pas de réponse...).</summary>
public sealed class RdpCertificateException(string message) : Exception(message);

/// <summary>
/// Lit le certificat d'un serveur Bureau à distance avant de lui confier un mot de passe : demande de connexion X.224
/// (TLS ou NLA demandés, MS-RDPBCGR 2.2.1.1), puis poignée de main TLS interrompue dès le certificat reçu. Rien d'autre
/// n'est envoyé : ni identifiant, ni mot de passe.
/// </summary>
public static class RdpCertificateProbe
{
    private const uint ProtocolSsl = 0x1;
    private const uint ProtocolHybrid = 0x2;
    private const uint ProtocolHybridEx = 0x8;

    // Refus du serveur (RDP_NEG_FAILURE) : TLS interdit, ou pas de certificat.
    private const uint SslNotAllowedByServer = 2;
    private const uint SslCertNotOnServer = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TPKT (version 3, 19 octets), X.224 Connection Request, RDP_NEG_REQ demandant TLS, NLA (CredSSP) et NLA étendue.
    /// </summary>
    internal static ReadOnlySpan<byte> ConnectionRequest =>
    [
        0x03, 0x00, 0x00, 0x13,
        0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x08, 0x00, (byte)(ProtocolSsl | ProtocolHybrid | ProtocolHybridEx), 0x00, 0x00, 0x00,
    ];

    /// <exception cref="RdpCertificateException">Serveur sans TLS, refus, réponse incorrecte ou délai dépassé.</exception>
    /// <exception cref="SocketException">Serveur injoignable.</exception>
    public static async Task<RdpCertificate> GetAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var network = tcp.GetStream();
            await network.WriteAsync(ConnectionRequest.ToArray(), timeout.Token).ConfigureAwait(false);
            var selected = await ReadConfirmAsync(network, timeout.Token).ConfigureAwait(false);
            if ((selected & (ProtocolSsl | ProtocolHybrid | ProtocolHybridEx)) == 0)
            {
                // Ancienne « sécurité RDP » : pas de certificat vérifiable, le mot de passe serait exposé à un intermédiaire.
                throw new RdpCertificateException(CoreStrings.RdpNoTls);
            }

            return await ReadCertificateAsync(network, host, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RdpCertificateException(CoreStrings.RdpProbeTimeout);
        }
    }

    /// <summary>Réponse X.224 Connection Confirm : protocole choisi par le serveur (0 : ancienne sécurité RDP, sans TLS).</summary>
    private static async Task<uint> ReadConfirmAsync(NetworkStream network, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(network, header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        if (header[0] != 0x03 || length is < 11 or > 64)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        var body = new byte[length - 4];
        await ReadExactlyAsync(network, body, ct).ConfigureAwait(false);
        if (body[0] != body.Length - 1 || (body[1] & 0xF0) != 0xD0)
        {
            throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }

        if (body.Length < 15)
        {
            return 0;
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11));
        switch (body[7])
        {
            case 0x02:
                return value;
            case 0x03 when value is SslNotAllowedByServer or SslCertNotOnServer:
                throw new RdpCertificateException(CoreStrings.RdpNoTls);
            case 0x03:
                throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpNegotiationRefused, value));
            default:
                throw new RdpCertificateException(CoreStrings.RdpNotRdp);
        }
    }

    /// <summary>Poignée de main TLS jusqu'au certificat du serveur, puis abandon (le certificat est refusé à dessein).</summary>
    private static async Task<RdpCertificate> ReadCertificateAsync(NetworkStream network, string host, CancellationToken ct)
    {
        RdpCertificate? received = null;
        await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not null)
                {
                    using var copy = new X509Certificate2(certificate);
                    received = new RdpCertificate(copy.Subject, copy.Issuer, Convert.ToHexString(SHA256.HashData(copy.RawData)),
                        copy.NotBefore, copy.NotAfter, Ftp.FtpFileBrowser.Describe(errors));
                }

                return false;
            },
        };
        try
        {
            await tls.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            if (received is null)
            {
                throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpTlsFailed, e.Message));
            }
        }

        return received ?? throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpTlsFailed, ""));
    }

    /// <summary>
    /// Certificat présenté au client FreeRDP pendant sa connexion (chaîne PEM transmise par lui), vérifié comme le fait
    /// Windows pour un serveur Bureau à distance : chaîne jusqu'à une autorité approuvée par le système (certificats
    /// intermédiaires envoyés par le serveur compris ; révocation vérifiée quand elle est joignable), nom du serveur,
    /// dates, et usage « authentification du serveur » ou « authentification Bureau à distance » s'il est précisé.
    /// </summary>
    /// <exception cref="RdpCertificateException">Aucun certificat lisible.</exception>
    public static RdpCertificate FromPemChain(string pem, string host)
    {
        var all = new X509Certificate2Collection();
        try
        {
            all.ImportFromPem(pem);
        }
        catch (CryptographicException e)
        {
            throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpTlsFailed, e.Message));
        }

        try
        {
            if (all.Count == 0)
            {
                throw new RdpCertificateException(string.Format(CultureInfo.CurrentCulture, CoreStrings.RdpTlsFailed, ""));
            }

            var leaf = all[0];
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            // Révocation injoignable (réseau d'administration fermé) : pas un refus, comme pour mstsc.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown
                | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                | X509VerificationFlags.IgnoreCtlSignerRevocationUnknown
                | X509VerificationFlags.IgnoreRootRevocationUnknown;
            for (int i = 1; i < all.Count; i++)
            {
                chain.ChainPolicy.ExtraStore.Add(all[i]);
            }

            var errors = SslPolicyErrors.None;
            if (!chain.Build(leaf) || !ServerUsage(leaf))
            {
                errors |= SslPolicyErrors.RemoteCertificateChainErrors;
            }

            if (!leaf.MatchesHostname(host, allowWildcards: true, allowCommonName: true))
            {
                errors |= SslPolicyErrors.RemoteCertificateNameMismatch;
            }

            return new RdpCertificate(leaf.Subject, leaf.Issuer, Convert.ToHexString(SHA256.HashData(leaf.RawData)),
                leaf.NotBefore, leaf.NotAfter, Ftp.FtpFileBrowser.Describe(errors));
        }
        finally
        {
            foreach (var certificate in all)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>Usage du certificat : non précisé, authentification du serveur, ou authentification Bureau à distance.</summary>
    private static bool ServerUsage(X509Certificate2 certificate)
    {
        const string serverAuthentication = "1.3.6.1.5.5.7.3.1";
        const string remoteDesktopAuthentication = "1.3.6.1.4.1.311.54.1.2";
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is serverAuthentication or remoteDesktopAuthentication);
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
