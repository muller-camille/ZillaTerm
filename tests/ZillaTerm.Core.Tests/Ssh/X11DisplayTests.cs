using System.Net;
using System.Net.Sockets;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public class X11DisplayTests
{
    [Theory]
    [InlineData(":0", "127.0.0.1", 6000, 0)]
    [InlineData(":1.2", "127.0.0.1", 6001, 2)]
    [InlineData("localhost:0.0", "127.0.0.1", 6000, 0)]
    [InlineData(" LOCALHOST:10 ", "127.0.0.1", 6010, 0)]
    [InlineData("127.0.0.1:3", "127.0.0.1", 6003, 0)]
    [InlineData("127.1.2.3:0", "127.1.2.3", 6000, 0)]
    [InlineData("::1:0", "::1", 6000, 0)]
    [InlineData("[::1]:4.1", "::1", 6004, 1)]
    public void DisplaysOfThisComputerAreRead(string text, string address, int port, int screen)
    {
        Assert.True(X11Display.TryParse(text, out var display));
        Assert.Equal(IPAddress.Parse(address), display.EndPoint.Address);
        Assert.Equal(port, display.EndPoint.Port);
        Assert.Equal(screen, display.Screen);
    }

    /// <summary>Sans hôte, sous Linux et les autres Unix : le socket local du serveur X.</summary>
    [Theory]
    [InlineData(":0", true, "/tmp/.X11-unix/X0")]
    [InlineData(":12.1", true, "/tmp/.X11-unix/X12")]
    [InlineData("localhost:1", false, "/tmp/.X11-unix/X1")]
    [InlineData("127.0.0.1:0", false, "/tmp/.X11-unix/X0")]
    public void DisplaysWithoutHostUseTheLocalSocket(string text, bool omitted, string socket)
    {
        Assert.True(X11Display.TryParse(text, out var display));
        Assert.Equal(omitted, display.HostOmitted);
        Assert.Equal(socket, display.UnixSocketPath);
    }

    /// <summary>
    /// Un serveur X sur une autre machine recevrait les fenêtres et les frappes en clair : refusé, comme une adresse
    /// incomplète ou un numéro hors des ports TCP.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData(":")]
    [InlineData(":x")]
    [InlineData(":-1")]
    [InlineData(":+1")]
    [InlineData(": 1")]
    [InlineData(":0.")]
    [InlineData(":0.256")]
    [InlineData(":59536")]
    [InlineData("10.0.0.5:0")]
    [InlineData("pc-collegue:0")]
    [InlineData("192.168.1.10:0.0")]
    [InlineData("[fe80::1]:0")]
    public void OtherDisplaysAreRefused(string? text)
    {
        Assert.False(X11Display.TryParse(text, out _));
    }

    [Fact]
    public void ShortFormIsLikeDisplay()
    {
        Assert.True(X11Display.TryParse("localhost:1.0", out var display));
        Assert.Equal(":1", display.ToString());
        Assert.True(X11Display.TryParse(":2.1", out display));
        Assert.Equal(":2.1", display.ToString());
    }

    [Fact]
    public async Task ListeningServerIsDetected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (port < 6000)
        {
            return;
        }

        var display = new X11Display(IPAddress.Loopback, port - 6000, 0);
        Assert.True(await display.IsListeningAsync(TimeSpan.FromSeconds(5)));
        listener.Stop();
        Assert.False(await display.IsListeningAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SettingsKeepAValidDisplayAndReplaceAnInvalidOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zillaterm-x11-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.Equal(":0", new AppSettings().X11Display);
            new AppSettings { X11Display = "localhost:1", X11Servers = ["abc"] }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal("localhost:1", loaded.X11Display);
            Assert.Equal(["abc"], loaded.X11Servers);

            File.WriteAllText(path, """{ "PvwaUrl": "https://pvwa", "X11Display": "10.0.0.5:0", "X11Servers": null }""");
            loaded = AppSettings.Load(path);
            Assert.Equal(":0", loaded.X11Display);
            Assert.Empty(loaded.X11Servers);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
