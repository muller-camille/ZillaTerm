using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Client FreeRDP de ZillaTerm : ligne de commande (rien de secret), fichier de connexion écrit sur son entrée standard,
/// empreintes acceptées et codes de fin.
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

    private const string Fingerprint = "0FF792338EE757CE1FB548F52BED5BC4C8140F83A0A78ECB33D1E70D4BA17A43";

    private static Dictionary<string, string> Fields(string file) =>
        file.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToDictionary(l => l[..l.IndexOf(':', l.IndexOf(':') + 1)], l => l[(l.IndexOf(':', l.IndexOf(':') + 1) + 1)..]);

    [Fact]
    public void RemoteAppFileGoesOnStandardInputNeverOnTheCommandLine()
    {
        var settings = Read(RemoteAppFile).ForPsmTab();

        var launch = FreeRdpArguments.Build(settings, 0x1234, (1280, 800), password: null, verbose: false, [Fingerprint]);

        var arguments = launch.Arguments;
        Assert.Equal("-", arguments[0]);
        Assert.Contains("/parent-window:4660", arguments);
        Assert.Contains("/size:1280x800", arguments);
        Assert.Contains("/smart-sizing", arguments);
        Assert.DoesNotContain("/dynamic-resolution", arguments);
        Assert.Contains("/cert:deny,fingerprint:sha256:" + Fingerprint, arguments);
        Assert.Contains("/from-stdin", arguments);
        Assert.Contains("/tune:FreeRDP_UseRdpSecurityLayer:FALSE,FreeRDP_RdpSecurity:FALSE,FreeRDP_NlaSecurity:FALSE,"
            + "FreeRDP_ExtSecurity:FALSE,FreeRDP_RemoteAppLanguageBarSupported:TRUE", arguments);
        Assert.Contains("-clipboard", arguments);
        Assert.Contains("/drives", arguments);
        Assert.Contains("-wallpaper", arguments);
        Assert.Contains("-window-drag", arguments);
        // Ni serveur, ni identifiant, ni application, ni jeton sur la ligne de commande.
        Assert.DoesNotContain(arguments, a => a.Contains("psm.corp.local", StringComparison.Ordinal)
            || a.Contains("jdoe", StringComparison.Ordinal) || a.Contains("PSM", StringComparison.Ordinal));
        AssertEachOptionOnce(arguments);

        var file = Fields(launch.ConnectionFile);
        Assert.Equal("psm.corp.local", file["full address:s"]);
        Assert.Equal("3389", file["server port:i"]);
        Assert.Equal("jdoe", file["username:s"]);
        // Mot de passe vide : FreeRDP ne demande rien et ne lit pas les identifiants enregistrés de Windows.
        Assert.Equal("", file["password:s"]);
        Assert.Equal("1", file["remoteapplicationmode:i"]);
        Assert.Equal("||PSMInitSession", file["remoteapplicationprogram:s"]);
        Assert.Equal("PSM-RDP", file["remoteapplicationname:s"]);
        // Virgules, deux-points et guillemets passent tels quels : aucune analyse d'option.
        Assert.Equal("/u admin-srv01 /a srv01.corp.local /c PSM-RDP, \"x:y\"", file["remoteapplicationcmdline:s"]);
        // Pas de programme de démarrage : FreeRDP lance l'application distante lui-même.
        Assert.False(file.ContainsKey("alternate shell:s"));
    }

    /// <summary>
    /// PSM qui n'accepte que l'ancienne sécurité RDP : permise sur demande seulement, avec un chiffrement de 128 bits ou
    /// FIPS (ni 40, ni 56 bits) ; sa clé fait partie des empreintes acceptées comme un certificat.
    /// </summary>
    [Fact]
    public void LegacyRdpSecurityOnlyWhenAllowedAndNeverBelow128Bits()
    {
        var settings = Read(RemoteAppFile).ForPsmTab();
        var other = new string('a', 64);

        var arguments = FreeRdpArguments.Build(settings, 1, (1280, 800), null, false, [Fingerprint, other], legacySecurity: true).Arguments;

        var tune = Assert.Single(arguments, a => a.StartsWith("/tune:", StringComparison.Ordinal));
        Assert.Contains("FreeRDP_RdpSecurity:TRUE", tune, StringComparison.Ordinal);
        Assert.Contains("FreeRDP_EncryptionMethods:18", tune, StringComparison.Ordinal);
        Assert.DoesNotContain("FreeRDP_RdpSecurity:FALSE", tune, StringComparison.Ordinal);
        Assert.Contains($"/cert:deny,fingerprint:sha256:{Fingerprint},fingerprint:sha256:{other}", arguments);
        AssertEachOptionOnce(arguments);
    }

    /// <summary>
    /// Redimensionnement dynamique : FreeRDP adapte la résolution à la taille de sa fenêtre au lieu de mettre l'image à
    /// l'échelle (les deux options s'excluent dans FreeRDP).
    /// </summary>
    [Fact]
    public void DynamicResolutionReplacesSmartSizing()
    {
        var arguments = FreeRdpArguments.Build(Read(RemoteAppFile).ForPsmTab(dynamicResolution: true), 1, (1280, 800), null, false,
            [Fingerprint]).Arguments;

        Assert.Contains("/dynamic-resolution", arguments);
        Assert.DoesNotContain("/smart-sizing", arguments);
        Assert.Contains("/size:1280x800", arguments);
        AssertEachOptionOnce(arguments);
    }

    /// <summary>Aucune empreinte acceptée : FreeRDP refuse tout serveur.</summary>
    [Fact]
    public void WithoutFingerprintEveryServerIsRefused()
    {
        var arguments = FreeRdpArguments.Build(Read(RemoteAppFile), 1, (800, 600), null, false, []).Arguments;

        Assert.Contains("/cert:deny", arguments);
        Assert.Throws<ArgumentException>(() => FreeRdpArguments.Build(Read(RemoteAppFile), 1, (800, 600), null, false, ["sha256:AB,ignore"]));
    }

    /// <summary>Dossier de configuration propre à ZillaTerm, sauf s'il ne peut pas passer tel quel dans « /tune ».</summary>
    [Theory]
    [InlineData(@"C:\Users\jdoe\AppData\Local\ZillaTerm\FreeRDP-config", true)]
    [InlineData(@"C:\Users\o'brien\AppData", false)]
    [InlineData(@"C:\a,b", false)]
    public void ConfigPathOnlyWhenItCannotBreakTheOption(string path, bool used)
    {
        var tune = FreeRdpArguments.Build(Read(RemoteAppFile), 1, (800, 600), null, false, [], configPath: path).Arguments
            .Single(a => a.StartsWith("/tune:", StringComparison.Ordinal));

        Assert.Equal(used, tune.Contains("FreeRDP_ConfigPath:" + path, StringComparison.Ordinal));
        Assert.Equal(used, tune.Contains("ConfigPath", StringComparison.Ordinal));
    }

    /// <summary>
    /// FreeRDP ne garde que la dernière valeur d'une option donnée deux fois (deux « /sec:… » : seul le second
    /// comptait, et la sécurité RDP sans TLS restait permise) : chaque option une seule fois.
    /// </summary>
    private static void AssertEachOptionOnce(IReadOnlyList<string> arguments)
    {
        var names = arguments.Skip(1).Select(a => a.TrimStart('/', '+', '-').Split(':')[0]);
        Assert.All(names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase), g => Assert.True(g.Count() == 1, g.Key));
    }

    [Fact]
    public void DesktopSessionStartsItsProgramAndSplitsTheDomain()
    {
        var settings = Read("full address:s:[fe80::1]:3390\r\nusername:s:CORP\\jdoe\r\n" +
                            "alternate shell:s:psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP\r\n" +
                            "disable wallpaper:i:1\r\nallow font smoothing:i:1\r\naudiomode:i:2\r\n");

        var launch = FreeRdpArguments.Build(settings, 1, (1001, 100), password: "s3cret", verbose: true, [Fingerprint]);

        var arguments = launch.Arguments;
        // Largeur paire, 200 pixels au moins.
        Assert.Contains("/size:1000x200", arguments);
        Assert.Contains("/log-level:INFO", arguments);
        // Journal détaillé : canaux RemoteApp, presse-papiers et affichage, messages du presse-papiers du client Windows.
        var filters = Assert.Single(arguments, a => a.StartsWith("/log-filters:", StringComparison.Ordinal))["/log-filters:".Length..].Split(',');
        Assert.Equal(["com.freerdp.channels.rail.*:DEBUG", "com.freerdp.channels.cliprdr.*:DEBUG", "com.freerdp.channels.disp.*:DEBUG",
            "com.freerdp.client.windows:DEBUG"], filters);
        // Les options sont écrites dans le journal de débogage : ni mot de passe ni jeton.
        Assert.DoesNotContain(arguments, a => a.Contains("s3cret", StringComparison.Ordinal) || a.Contains("admin-srv01", StringComparison.Ordinal));
        Assert.Contains("-wallpaper", arguments);
        Assert.Contains("+themes", arguments);
        Assert.Contains("+fonts", arguments);
        Assert.Contains("-aero", arguments);
        Assert.Contains("/audio-mode:2", arguments);
        Assert.Contains("+clipboard", arguments);
        var tune = Assert.Single(arguments, a => a.StartsWith("/tune:", StringComparison.Ordinal));
        Assert.Contains("FreeRDP_RdpSecurity:FALSE", tune, StringComparison.Ordinal);
        Assert.DoesNotContain("NlaSecurity", tune, StringComparison.Ordinal);
        Assert.DoesNotContain(arguments, a => a.StartsWith("/sec", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("s3cret", StringComparison.Ordinal));
        AssertEachOptionOnce(arguments);

        var file = Fields(launch.ConnectionFile);
        Assert.Equal("[fe80::1]", file["full address:s"]);
        Assert.Equal("3390", file["server port:i"]);
        Assert.Equal("jdoe", file["username:s"]);
        Assert.Equal("CORP", file["domain:s"]);
        Assert.Equal("s3cret", file["password:s"]);
        Assert.Equal("psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP", file["alternate shell:s"]);
        Assert.False(file.ContainsKey("remoteapplicationmode:i"));
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
            FreeRdpArguments.Build(settings, 1, (800, 600), field == "password" ? value : null, false, []));
    }

    [Fact]
    public void GatewayConnectionsStayWithRemoteDesktopConnection()
    {
        var direct = Read("full address:s:psm.corp.local\r\ngatewayhostname:s:rdg.corp.local\r\ngatewayusagemethod:i:0\r\n");
        var gateway = Read("full address:s:psm.corp.local\r\ngatewayhostname:s:rdg.corp.local\r\ngatewayusagemethod:i:1\r\n");

        Assert.True(FreeRdpArguments.Supports(direct));
        Assert.False(FreeRdpArguments.Supports(gateway));
        Assert.Throws<ArgumentException>(() => FreeRdpArguments.Build(gateway, 1, (800, 600), null, false, []));
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

    /// <summary>Code de sortie du client : sa dernière erreur FreeRDP ; tout autre code est un plantage ou un arrêt forcé.</summary>
    [Theory]
    [InlineData(0, 0u, "")]
    [InlineData(0x00020008, 0x00020008u, "ERRCONNECT_TLS_CONNECT_FAILED")]
    [InlineData(0x0002000C, 0x0002000Cu, "ERRCONNECT_SECURITY_NEGO_CONNECT_FAILED")]
    [InlineData(0x0001000C, 0x0001000Cu, "ERRINFO_0x000C")]
    [InlineData(1, null, "")]
    [InlineData(-1073741819, null, "")]
    public void ExitCodesAreFreeRdpErrors(int exitCode, uint? code, string name)
    {
        Assert.Equal(code, FreeRdpExit.FromExitCode(exitCode));
        if (code is { } value)
        {
            Assert.Equal(name, value == 0 ? "" : FreeRdpExit.Name(value));
        }
    }

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

}
