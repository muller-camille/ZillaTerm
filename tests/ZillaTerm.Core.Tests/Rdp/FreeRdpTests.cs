using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Client FreeRDP de ZillaTerm : réglages écrits sur son entrée standard, messages de sa sortie, codes de fin, et
/// vérification du certificat qu'il transmet.
/// </summary>
public class FreeRdpTests
{
    // Fichier PSM du PVWA en application distante (valeurs d'exemple).
    private const string RemoteAppFile =
        "full address:s:psm.corp.local\r\nusername:s:jdoe\r\nenablecredsspsupport:i:0\r\n" +
        "remoteapplicationmode:i:1\r\nremoteapplicationprogram:s:||PSMInitSession\r\nremoteapplicationname:s:PSM-RDP\r\n" +
        "remoteapplicationcmdline:s:/u admin-srv01 /a srv01.corp.local /c PSM-RDP, \"x:y\"\r\n" +
        "alternate shell:s:rdpinit.exe\r\nredirectclipboard:i:0\r\ndrivestoredirect:s:*\r\n";

    private static RdpConnectionSettings Read(string file) => RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes(file));

    [Fact]
    public void RemoteAppFileGivesTheApplicationAsSettingsNeverAsOptions()
    {
        var settings = Read(RemoteAppFile).ForPsmTab();

        var lines = FreeRdpArguments.Build(settings, 0x1234, (1280, 800), password: null, verbose: false);

        Assert.Equal("", lines[^1]);
        Assert.Contains("/parent-window:4660", lines);
        Assert.Contains("/v:psm.corp.local", lines);
        Assert.Contains("/port:3389", lines);
        Assert.Contains("/size:1280x800", lines);
        Assert.Contains("/smart-sizing", lines);
        Assert.Contains("/sec:rdp:off", lines);
        Assert.Contains("/sec:nla:off", lines);
        Assert.Contains("-clipboard", lines);
        Assert.Contains("/drives", lines);
        Assert.Contains("FreeRDP_RemoteApplicationMode=TRUE", lines);
        Assert.Contains("FreeRDP_RemoteApplicationProgram=||PSMInitSession", lines);
        Assert.Contains("FreeRDP_RemoteApplicationName=PSM-RDP", lines);
        // Virgules, deux-points et guillemets passent tels quels : aucune analyse d'option.
        Assert.Contains("FreeRDP_RemoteApplicationCmdLine=/u admin-srv01 /a srv01.corp.local /c PSM-RDP, \"x:y\"", lines);
        Assert.Contains("FreeRDP_Username=jdoe", lines);
        // Ni mot de passe, ni programme de démarrage (FreeRDP lance l'application distante lui-même).
        Assert.DoesNotContain(lines, l => l.StartsWith("FreeRDP_Password=", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("FreeRDP_AlternateShell=", StringComparison.Ordinal));
        // Chaque ligne est une option ou un réglage, sauf la ligne vide finale.
        Assert.All(lines.SkipLast(1), l => Assert.True(l[0] is '/' or '+' or '-' || l.StartsWith("FreeRDP_", StringComparison.Ordinal), l));
    }

    [Fact]
    public void DesktopSessionStartsItsProgramAndSplitsTheDomain()
    {
        var settings = Read("full address:s:[fe80::1]:3390\r\nusername:s:CORP\\jdoe\r\n" +
                            "alternate shell:s:psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP\r\n" +
                            "disable wallpaper:i:1\r\nallow font smoothing:i:1\r\naudiomode:i:2\r\n");

        var lines = FreeRdpArguments.Build(settings, 1, (1001, 100), password: "s3cret", verbose: true);

        Assert.Contains("/v:[fe80::1]", lines);
        Assert.Contains("/port:3390", lines);
        // Largeur paire, 200 pixels au moins.
        Assert.Contains("/size:1000x200", lines);
        Assert.Contains("/log-level:INFO", lines);
        Assert.Contains("FreeRDP_Username=jdoe", lines);
        Assert.Contains("FreeRDP_Domain=CORP", lines);
        Assert.Contains("FreeRDP_Password=s3cret", lines);
        Assert.Contains("FreeRDP_AlternateShell=psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP", lines);
        Assert.Contains("-wallpaper", lines);
        Assert.Contains("+themes", lines);
        Assert.Contains("+fonts", lines);
        Assert.Contains("-aero", lines);
        Assert.Contains("/audio-mode:2", lines);
        Assert.Contains("+clipboard", lines);
        Assert.DoesNotContain("/sec:nla:off", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("FreeRDP_RemoteApplication", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("jdoe", "", "jdoe", "")]
    [InlineData("CORP\\jdoe", "", "jdoe", "CORP")]
    [InlineData("CORP\\jdoe", "OTHER", "jdoe", "OTHER")]
    [InlineData("jdoe@corp.local", "", "jdoe@corp.local", "")]
    [InlineData("\\jdoe", "", "\\jdoe", "")]
    public void SplitsUserNamesLikeRemoteDesktopConnection(string user, string domain, string expectedUser, string expectedDomain) =>
        Assert.Equal((expectedUser, expectedDomain), FreeRdpArguments.SplitUser(user, domain));

    [Theory]
    [InlineData("password", "a\nb")]
    [InlineData("password", "a\rb")]
    [InlineData("password", "a\0b")]
    [InlineData("cmd", "/u x\n/p y")]
    public void RefusesValuesThatWouldBreakTheLines(string field, string value)
    {
        var settings = Read(RemoteAppFile);
        if (field == "cmd")
        {
            settings = settings with { RemoteApplicationCmdLine = value };
        }

        Assert.Throws<ArgumentException>(() =>
            FreeRdpArguments.Build(settings, 1, (800, 600), field == "password" ? value : null, verbose: false));
    }

    [Fact]
    public void GatewayConnectionsStayWithRemoteDesktopConnection()
    {
        var direct = Read("full address:s:psm.corp.local\r\ngatewayhostname:s:rdg.corp.local\r\ngatewayusagemethod:i:0\r\n");
        var gateway = Read("full address:s:psm.corp.local\r\ngatewayhostname:s:rdg.corp.local\r\ngatewayusagemethod:i:1\r\n");

        Assert.True(FreeRdpArguments.Supports(direct));
        Assert.False(FreeRdpArguments.Supports(gateway));
        Assert.Throws<ArgumentException>(() => FreeRdpArguments.Build(gateway, 1, (800, 600), null, false));
    }

    [Fact]
    public void ReadsTheRemoteApplicationFromThePvwaFile()
    {
        var settings = Read(RemoteAppFile);

        Assert.True(settings.IsRemoteApp);
        Assert.Equal("||PSMInitSession", settings.RemoteApplicationProgram);
        Assert.Equal("PSM-RDP", settings.RemoteApplicationName);
        Assert.StartsWith("/u admin-srv01", settings.RemoteApplicationCmdLine, StringComparison.Ordinal);
        Assert.False(settings.UsesGateway);
    }

    [Fact]
    public void ParsesTheClientMessages()
    {
        Assert.Equal(new FreeRdpConnected(132456), FreeRdpMessage.Parse("zillaterm:connected 132456"));
        Assert.Equal(new FreeRdpEnded(0x0001000C, "ERRINFO_LOGOFF_BY_USER"), FreeRdpMessage.Parse("zillaterm:ended 65548 ERRINFO_LOGOFF_BY_USER"));
        Assert.Equal(new FreeRdpEnded(0, ""), FreeRdpMessage.Parse("zillaterm:ended 0"));

        var pem = "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n";
        var request = Assert.IsType<FreeRdpCertificateRequest>(
            FreeRdpMessage.Parse($"zillaterm:certificate 544 3389 {Convert.ToBase64String(Encoding.ASCII.GetBytes(pem))} psm.corp.local"));
        Assert.Equal(pem, request.Pem);
        Assert.Equal(3389, request.Port);
        Assert.Equal("psm.corp.local", request.Host);
        Assert.True(request.IsGateway);
        Assert.False(request.IsRedirect);

        // Lignes du journal de FreeRDP, messages incomplets ou mal formés : pas des messages.
        Assert.Null(FreeRdpMessage.Parse("[12:00:00:000] [42:43] [WARN][com.freerdp] - zillaterm:connected 1"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:connected"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:connected -1"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:certificate 0 99999 AAAA psm.corp.local"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:certificate 0 3389 !!! psm.corp.local"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:certificate 0 3389 AAAA psm corp"));
        Assert.Null(FreeRdpMessage.Parse("zillaterm:unknown 1"));
    }

    [Fact]
    public void ErrorNamesKeepOnlyIdentifierCharacters() =>
        Assert.Equal(new FreeRdpEnded(1, "ABC_1"), FreeRdpMessage.Parse("zillaterm:ended 1 A<B>C_1"));

    [Theory]
    [InlineData(0u, true, FreeRdpEnd.Normal)]
    [InlineData(0x0001000Cu, true, FreeRdpEnd.Normal)]
    [InlineData(0x0001000Bu, true, FreeRdpEnd.Normal)]
    [InlineData(0x00010002u, true, FreeRdpEnd.Normal)]
    [InlineData(0x0002000Bu, true, FreeRdpEnd.Normal)]
    [InlineData(0x00010003u, false, FreeRdpEnd.Timeout)]
    [InlineData(0x00010005u, false, FreeRdpEnd.TakenOver)]
    [InlineData(0x00010007u, false, FreeRdpEnd.Denied)]
    [InlineData(0x00020005u, false, FreeRdpEnd.NameNotFound)]
    [InlineData(0x00020006u, false, FreeRdpEnd.Unreachable)]
    [InlineData(0x00020008u, false, FreeRdpEnd.Tls)]
    [InlineData(0x0002000Cu, false, FreeRdpEnd.Negotiation)]
    [InlineData(0x00020015u, false, FreeRdpEnd.Authentication)]
    [InlineData(0x00020018u, false, FreeRdpEnd.Account)]
    [InlineData(0x00020016u, false, FreeRdpEnd.AccessDenied)]
    [InlineData(0x00030001u, false, FreeRdpEnd.Other)]
    public void ClassifiesEndCodes(uint code, bool normal, FreeRdpEnd end) =>
        Assert.Equal((normal, end), FreeRdpExit.Classify(code));

    [Fact]
    public void CertificateFromThePemChainIsCheckedForTheServerName()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=psm.corp.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("psm.corp.local");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pem = certificate.ExportCertificatePem() + "\n";

        var good = RdpCertificateProbe.FromPemChain(pem, "psm.corp.local");
        var other = RdpCertificateProbe.FromPemChain(pem, "srv01.corp.local");

        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), good.Sha256);
        Assert.Equal("CN=psm.corp.local", good.Subject);
        // Autosigné : jamais approuvé sans question ; un autre nom est signalé en plus.
        Assert.False(good.Trusted);
        Assert.False(other.Trusted);
        Assert.True(other.Problem.Length > good.Problem.Length);
    }

    [Fact]
    public void PemWithoutCertificateIsRefused() =>
        Assert.Throws<RdpCertificateException>(() => RdpCertificateProbe.FromPemChain("nothing here", "psm.corp.local"));
}
