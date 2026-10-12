using System.Net;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests;

public class PsmConnectTests
{
    private const string Rdp = "full address:s:psm.corp.local\r\nusername:s:jdoe\r\nalternate shell:s:psm /u admin /a srv01 /c PSM-RDP\r\n";

    private static FakePvwa Pvwa(Func<HttpRequestMessage, HttpResponseMessage> psmConnect) =>
        new(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : psmConnect(req));

    private static HttpResponseMessage RdpResponse(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/octet-stream" } } } };

    [Fact]
    public async Task PsmConnect_PostsComponentAndAsksForRdpFile()
    {
        var pvwa = Pvwa(_ => RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var file = await client.PsmConnectAsync("12_34", new PsmConnectOptions { ConnectionComponent = "PSM-RDP" });

        Assert.Equal(Rdp, Encoding.UTF8.GetString(file));
        var (method, path, auth, body) = pvwa.Requests[1];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/PasswordVault/API/Accounts/12_34/PSMConnect", path);
        Assert.Equal("tok", auth);
        Assert.Equal("application/octet-stream", pvwa.Accepts[1]);
        Assert.NotNull(pvwa.ContentLengths[1]);
        Assert.Equal("""{"ConnectionComponent":"PSM-RDP"}""", body);
    }

    [Fact]
    public async Task PsmConnect_SendsReasonTicketAndRemoteMachine()
    {
        var pvwa = Pvwa(_ => RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.PsmConnectAsync("1_1", new PsmConnectOptions
        {
            ConnectionComponent = " PSM-RDP ",
            Reason = "Incident INC42",
            TicketingSystemName = "ServiceNow",
            TicketId = "INC42",
            RemoteMachine = "srv01.corp.local",
        });

        using var json = JsonDocument.Parse(pvwa.Requests[1].Body);
        var root = json.RootElement;
        Assert.Equal("PSM-RDP", root.GetProperty("ConnectionComponent").GetString());
        Assert.Equal("Incident INC42", root.GetProperty("reason").GetString());
        Assert.Equal("ServiceNow", root.GetProperty("TicketingSystemName").GetString());
        Assert.Equal("INC42", root.GetProperty("TicketId").GetString());
        var remote = root.GetProperty("ConnectionParams").GetProperty("PSMRemoteMachine");
        Assert.Equal("srv01.corp.local", remote.GetProperty("value").GetString());
        Assert.False(remote.GetProperty("ShouldSave").GetBoolean());
    }

    [Fact]
    public async Task PsmConnect_EscapesAccountId()
    {
        var pvwa = Pvwa(_ => RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.PsmConnectAsync("../Users", new PsmConnectOptions { ConnectionComponent = "PSM-RDP" });

        Assert.Equal("/PasswordVault/API/Accounts/..%2FUsers/PSMConnect", pvwa.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task PsmConnect_RefusalIsReportedWithPvwaMessage()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json(
            """{"ErrorCode":"PASWS204E","ErrorMessage":"You must specify a reason for this operation."}""",
            HttpStatusCode.BadRequest));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(
            () => client.PsmConnectAsync("1_1", new PsmConnectOptions { ConnectionComponent = "PSM-RDP" }));

        Assert.Equal("You must specify a reason for this operation. (PASWS204E)", ex.Message);
        Assert.False(ex.IsUnknownComponent);
    }

    /// <summary>
    /// Composant absent de la plateforme du compte (réponse constatée : 500 et EPVWA093E) : reconnu, pour inviter à
    /// saisir celui de la plateforme (par ex. WIN-PSM au lieu de PSM-RDP).
    /// </summary>
    [Fact]
    public async Task PsmConnect_RecognizesAnUnknownComponent()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json(
            """{"ErrorCode":"EPVWA093E","ErrorMessage":"Failed to get the relevant connection component (CAWS00001E)"}""",
            HttpStatusCode.InternalServerError));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.PsmConnectAsync("2302_3", new PsmConnectOptions
        {
            ConnectionComponent = "PSM-RDP",
            RemoteMachine = "srv01.corp.local",
        }));

        Assert.True(ex.IsUnknownComponent);
    }

    [Fact]
    public void RdpFile_KeepsRawFileBytes()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Rdp)).ToArray();

        Assert.Same(utf16, RdpFile.FromPsmConnectResponse(utf16, "application/octet-stream"));
    }

    [Fact]
    public void RdpFile_UnwrapsJsonString()
    {
        var wrapped = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Rdp));

        var file = RdpFile.FromPsmConnectResponse(wrapped, "application/json");

        Assert.Equal(Rdp, Encoding.Unicode.GetString(file, 2, file.Length - 2));
        Assert.Equal(Encoding.Unicode.GetPreamble(), file[..2]);
    }

    /// <summary>Session PSM dans mstsc, disques du poste partagés : la ligne est ajoutée, encodage et fins de ligne gardés.</summary>
    [Fact]
    public void RdpFile_AddsAllDrives_InTheSameEncoding()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Rdp)).ToArray();

        var file = RdpFile.WithAllDrives(utf16);

        Assert.Equal(Encoding.Unicode.GetPreamble(), file[..2]);
        Assert.Equal(Rdp + "drivestoredirect:s:*\r\n", Encoding.Unicode.GetString(file, 2, file.Length - 2));
        Assert.True(RdpConnectionSettings.FromRdpFile(file).RedirectDrives);
    }

    /// <summary>Une liste de lecteurs (ou aucun) demandée par le PVWA devient « tous » ; sans fin de ligne finale, la ligne s'ajoute.</summary>
    [Theory]
    [InlineData("full address:s:psm.corp.local\ndrivestoredirect:s:\nusername:s:jdoe\n",
        "full address:s:psm.corp.local\ndrivestoredirect:s:*\nusername:s:jdoe\n")]
    [InlineData("full address:s:psm.corp.local\r\nDrivesToRedirect:s:C:;D:;\r\n",
        "full address:s:psm.corp.local\r\ndrivestoredirect:s:*\r\n")]
    [InlineData("full address:s:psm.corp.local", "full address:s:psm.corp.local\ndrivestoredirect:s:*")]
    public void RdpFile_ReplacesTheDrivesLine(string rdp, string expected) =>
        Assert.Equal(expected, Encoding.UTF8.GetString(RdpFile.WithAllDrives(Encoding.UTF8.GetBytes(rdp))));

    /// <summary>Fichier signé par le PVWA : jamais modifié (la Connexion Bureau à distance en vérifie la signature).</summary>
    [Fact]
    public void RdpFile_LeavesASignedFileAsIs()
    {
        var signed = Encoding.UTF8.GetBytes(Rdp + "signscope:s:Full Address,Alternate Shell\r\nsignature:s:AQABAAEAAAA=\r\n");

        Assert.True(RdpFile.IsSigned(signed));
        Assert.Same(signed, RdpFile.WithAllDrives(signed));
        Assert.False(RdpFile.IsSigned(Encoding.UTF8.GetBytes(Rdp + "signature:s:\r\n")));
    }

    [Theory]
    [InlineData("""{"PSMGWURL":"https://gw/guac","PSMGWRequest":"abc"}""", "PSM Gateway")]
    [InlineData("<html>Error</html>", "pas de fichier RDP")]
    [InlineData("", "pas de fichier RDP")]
    public void RdpFile_RejectsAnythingElse(string body, string expected)
    {
        using var _ = UiCulture.Use("fr-FR");
        var ex = Assert.Throws<PvwaException>(() => RdpFile.FromPsmConnectResponse(Encoding.UTF8.GetBytes(body), null));

        Assert.Contains(expected, ex.Message);
    }
}
