using System.Text;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

public class RdpConnectionSettingsTests
{
    // Fichier PSM-RDP du PVWA (valeurs d'exemple) : bureau, plein écran demandé, redirections choisies par le PVWA.
    private const string PsmFile =
        "screen mode id:i:2\r\nuse multimon:i:1\r\ndesktopwidth:i:1280\r\ndesktopheight:i:800\r\nsession bpp:i:24\r\n" +
        "full address:s:psm.corp.local\r\nusername:s:jdoe\r\n" +
        "alternate shell:s:psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP\r\n" +
        "authentication level:i:0\r\nenablecredsspsupport:i:0\r\nredirectclipboard:i:0\r\n" +
        "disable wallpaper:i:1\r\nallow font smoothing:i:1\r\nkeyboardhook:i:1\r\n";

    [Fact]
    public void ReadsPsmFileInUtf16WithBom()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(PsmFile)).ToArray();

        var s = RdpConnectionSettings.FromRdpFile(bytes);

        Assert.Equal("psm.corp.local", s.Server);
        Assert.Equal(3389, s.Port);
        Assert.Equal("jdoe", s.UserName);
        Assert.Equal("psm /u admin-srv01 /a srv01.corp.local /c PSM-RDP", s.StartProgram);
        Assert.Equal(0, s.AuthenticationLevel);
        Assert.False(s.EnableCredSsp);
        Assert.False(s.RedirectClipboard);
        Assert.Equal(24, s.ColorDepth);
        Assert.Equal(0x01 | 0x80, s.PerformanceFlags);
        Assert.Equal(1, s.KeyboardHookMode);
        Assert.False(s.IsRemoteApp);
        // Plein écran et taille du fichier ignorés : la session prend la taille de l'onglet.
        Assert.False(s.SmartSizing);
    }

    [Fact]
    public void FileUtf8AndUtf16WithoutBomGiveTheSameSettings()
    {
        const string text = "full address:s:srv01.corp.local:3390\nusername:s:CORP\\jdoe\n";

        foreach (var s in new[] { RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes(text)), RdpConnectionSettings.FromRdpFile(Encoding.Unicode.GetBytes(text)) })
        {
            Assert.Equal("srv01.corp.local", s.Server);
            Assert.Equal(3390, s.Port);
            Assert.Equal("CORP\\jdoe", s.UserName);
        }
    }

    [Fact]
    public void MissingRedirectionsStayOffExceptClipboard()
    {
        var s = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes("full address:s:psm.corp.local\r\n"));

        Assert.True(s.RedirectClipboard);
        Assert.False(s.RedirectDrives);
        Assert.False(s.RedirectPrinters);
        Assert.False(s.RedirectPorts);
        Assert.False(s.RedirectSmartCards);
        Assert.False(s.RedirectPosDevices);
        Assert.Equal(2, s.AuthenticationLevel);
        Assert.True(s.EnableCredSsp);
        Assert.Equal(32, s.ColorDepth);
        Assert.Equal(0, s.PerformanceFlags);
    }

    [Theory]
    [InlineData("drivestoredirect:s:*", true)]
    [InlineData("drivestoredirect:s:*;DynamicDrives", true)]
    [InlineData("drivestoredirect:s:C:;D:;", false)]
    [InlineData("drivestoredirect:s:", false)]
    [InlineData("drivestoredirect:s:\r\nredirectdrives:i:1", false)]
    [InlineData("redirectdrives:i:1", true)]
    [InlineData("redirectdrives:i:0", false)]
    public void RedirectsDrivesOnlyWhenAllAreRequested(string lines, bool expected)
    {
        var s = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes($"full address:s:psm.corp.local\r\n{lines}\r\n"));

        Assert.Equal(expected, s.RedirectDrives);
    }

    [Fact]
    public void UsesServerPortWhenAddressHasNone()
    {
        var s = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes("full address:s:psm.corp.local\r\nserver port:i:4000\r\n"));

        Assert.Equal(4000, s.Port);
    }

    [Fact]
    public void RejectsFileWithoutAddress()
    {
        Assert.Throws<FormatException>(() => RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes("username:s:jdoe\r\n")));
    }

    /// <summary>Composant en application distante (PSM-SSH, WinSCP...) : reconnu, pour être confié à mstsc.</summary>
    [Theory]
    [InlineData("remoteapplicationmode:i:1\r\nremoteapplicationprogram:s:||PSMInitSession\r\n", true)]
    [InlineData("remoteapplicationmode:i:0\r\n", false)]
    [InlineData("", false)]
    public void RecognisesRemoteApplications(string lines, bool expected)
    {
        var s = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes($"full address:s:psm.corp.local\r\nalternate shell:s:PSM@0123abcd\r\n{lines}"));

        Assert.Equal(expected, s.IsRemoteApp);
    }

    /// <summary>
    /// Session PSM dans l'onglet : résolution fixe et image mise à l'échelle, pas de cache d'images sur disque ; le reste
    /// (redirections, sécurité, programme de démarrage) reste celui du fichier.
    /// </summary>
    [Fact]
    public void PsmTabKeepsTheFileSettingsButFixesTheResolutionAndTheCache()
    {
        var file = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes(
            "full address:s:psm.corp.local\r\nalternate shell:s:PSM@0123abcd\r\nauthentication level:i:2\r\n" +
            "redirectclipboard:i:1\r\ndrivestoredirect:s:*\r\nbitmapcachepersistenable:i:1\r\nsmart sizing:i:0\r\n"));

        var tab = file.ForPsmTab();

        Assert.True(tab.SmartSizing);
        Assert.False(tab.BitmapPersistence);
        Assert.Equal(file with { SmartSizing = true, BitmapPersistence = false }, tab);
        Assert.True(tab.RedirectClipboard);
        Assert.True(tab.RedirectDrives);
        Assert.Equal("PSM@0123abcd", tab.StartProgram);
    }

    /// <summary>
    /// Redimensionnement dynamique (option expérimentale) : la résolution suit l'onglet, sans image mise à l'échelle, même
    /// si le fichier en demande une ; le reste des règles de l'onglet ne change pas.
    /// </summary>
    [Fact]
    public void PsmTabWithDynamicResolutionFollowsTheTabSize()
    {
        var file = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes(
            "full address:s:psm.corp.local\r\nauthentication level:i:0\r\nbitmapcachepersistenable:i:1\r\nsmart sizing:i:1\r\n"));

        var tab = file.ForPsmTab(dynamicResolution: true);

        Assert.Equal(file.ForPsmTab() with { SmartSizing = false }, tab);
        Assert.False(tab.BitmapPersistence);
        Assert.Equal(2, tab.AuthenticationLevel);
    }

    /// <summary>Session PSM dans l'onglet : jamais de connexion sans vérification du serveur, même si le fichier le permet.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void PsmTabNeverConnectsWithoutCheckingTheServer(int fileLevel, int expected)
    {
        var file = RdpConnectionSettings.FromRdpFile(Encoding.UTF8.GetBytes($"full address:s:psm.corp.local\r\nauthentication level:i:{fileLevel}\r\n"));

        Assert.Equal(expected, file.ForPsmTab().AuthenticationLevel);
    }

    [Fact]
    public void ReadsUtf8AndUtf16Files()
    {
        const string text = "full address:s:host.example:3390\nusername:s:CORP\\jdoe\n";

        foreach (var bytes in new[]
                 {
                     Encoding.UTF8.GetBytes(text), Encoding.Unicode.GetBytes(text),
                     [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
                 })
        {
            var values = RdpConnectionSettings.ReadFile(bytes);
            Assert.Equal("host.example:3390", values["full address"]);
            Assert.Equal("CORP\\jdoe", values["username"]);
        }
    }

    [Theory]
    [InlineData("srv01", "srv01", null)]
    [InlineData("srv01:3390", "srv01", 3390)]
    [InlineData("[fe80::1]:3390", "fe80::1", 3390)]
    [InlineData("[fe80::1]", "fe80::1", null)]
    [InlineData("fe80::1", "fe80::1", null)]
    [InlineData("srv01:abc", "srv01", null)]
    public void SplitsAddress(string address, string host, int? port)
    {
        Assert.Equal((host, port), RdpConnectionSettings.SplitAddress(address));
    }

    [Fact]
    public void ParseKeepsColonsInValuesAndIgnoresMalformedLines()
    {
        var values = RdpConnectionSettings.Parse("alternate shell:s:psm /a 10.0.0.1:22\r\nnot a setting\r\nfoo:xx:1\r\n");

        Assert.Equal("psm /a 10.0.0.1:22", values["alternate shell"]);
        Assert.Single(values);
    }

    [Fact]
    public void DirectConnectionUsesSecureDefaults()
    {
        var s = RdpConnectionSettings.Direct("srv", 0, "admin");

        Assert.Equal(3389, s.Port);
        Assert.Equal(2, s.AuthenticationLevel);
        Assert.True(s.EnableCredSsp);
        Assert.False(s.RedirectDrives);
        Assert.False(s.RedirectPrinters);
        // Pas d'images de la session gardées sur le disque de ce poste.
        Assert.False(s.BitmapPersistence);
    }
}
