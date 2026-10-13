using System.Net;
using System.Text;
using System.Text.Json;

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

    /// <summary>Disques du poste : paramètre de la case « Map local drives » du PVWA, avec la machine cible.</summary>
    [Fact]
    public async Task PsmConnect_AsksForLocalDrives()
    {
        var pvwa = Pvwa(_ => RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.PsmConnectAsync("1_1", new PsmConnectOptions
        {
            ConnectionComponent = "WIN-PSM",
            RemoteMachine = "srv01.corp.local",
            MapLocalDrives = true,
        });

        using var json = JsonDocument.Parse(pvwa.Requests[1].Body);
        var parameters = json.RootElement.GetProperty("ConnectionParams");
        Assert.Equal("srv01.corp.local", parameters.GetProperty("PSMRemoteMachine").GetProperty("value").GetString());
        var drives = parameters.GetProperty("AllowMappingLocalDrives");
        Assert.Equal("Yes", drives.GetProperty("value").GetString());
        Assert.False(drives.GetProperty("ShouldSave").GetBoolean());
    }

    /// <summary>Paramètre refusé par le PVWA (composant qui ne le propose pas) : nouvelle demande, sans les disques.</summary>
    [Fact]
    public async Task PsmConnect_RetriesWithoutLocalDrivesWhenTheParameterIsRefused()
    {
        int calls = 0;
        var pvwa = Pvwa(_ => ++calls == 1
            ? FakePvwa.Json("""{"ErrorCode":"PASWS999E","ErrorMessage":"Invalid connection parameter AllowMappingLocalDrives."}""",
                HttpStatusCode.BadRequest)
            : RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var file = await client.PsmConnectAsync("1_1", new PsmConnectOptions { ConnectionComponent = "WIN-PSM", MapLocalDrives = true });

        Assert.Equal(Rdp, Encoding.UTF8.GetString(file));
        Assert.Equal(3, pvwa.Requests.Count);
        Assert.Contains("AllowMappingLocalDrives", pvwa.Requests[1].Body);
        Assert.Equal("""{"ConnectionComponent":"WIN-PSM"}""", pvwa.Requests[2].Body);
    }

    /// <summary>
    /// Refus qui ne nomme pas le paramètre (texte d'un autre PVWA, autre langue) ou erreur interne : nouvelle demande sans
    /// les disques aussi.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "PASWS013E", "La requête n'est pas valide.")]
    [InlineData(HttpStatusCode.InternalServerError, "PASWS999E", "An unexpected error occurred.")]
    public async Task PsmConnect_RetriesWithoutLocalDrivesOnAnyRefusalOfTheRequest(HttpStatusCode status, string code, string message)
    {
        int calls = 0;
        var pvwa = Pvwa(_ => ++calls == 1
            ? FakePvwa.Json($$"""{"ErrorCode":"{{code}}","ErrorMessage":"{{message}}"}""", status)
            : RdpResponse(Encoding.UTF8.GetBytes(Rdp)));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var file = await client.PsmConnectAsync("1_1", new PsmConnectOptions { ConnectionComponent = "WIN-PSM", MapLocalDrives = true });

        Assert.Equal(Rdp, Encoding.UTF8.GetString(file));
        Assert.Equal("""{"ConnectionComponent":"WIN-PSM"}""", pvwa.Requests[2].Body);
    }

    /// <summary>Accès refusé ou composant inconnu : la même demande sans les disques échouerait pareil.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "PASWS041E", "You are not authorized to perform this action.")]
    [InlineData(HttpStatusCode.BadRequest, "EPVWA093E", "Connection component WIN-PSM was not found.")]
    [InlineData(HttpStatusCode.BadRequest, "PASWS205E", "A ticket is required to access this account.")]
    public async Task PsmConnect_DoesNotRetryRefusalsWithAnotherCause(HttpStatusCode status, string code, string message)
    {
        var pvwa = Pvwa(_ => FakePvwa.Json($$"""{"ErrorCode":"{{code}}","ErrorMessage":"{{message}}"}""", status));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await Assert.ThrowsAsync<PvwaException>(
            () => client.PsmConnectAsync("1_1", new PsmConnectOptions { ConnectionComponent = "WIN-PSM", MapLocalDrives = true }));

        Assert.Equal(2, pvwa.Requests.Count);
    }

    /// <summary>Autre refus (motif exigé) : pas de nouvelle demande, l'erreur est rendue telle quelle.</summary>
    [Fact]
    public async Task PsmConnect_DoesNotRetryOtherRefusals()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json(
            """{"ErrorCode":"PASWS204E","ErrorMessage":"You must specify a reason for this operation."}""",
            HttpStatusCode.BadRequest));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await Assert.ThrowsAsync<PvwaException>(
            () => client.PsmConnectAsync("1_1", new PsmConnectOptions { ConnectionComponent = "WIN-PSM", MapLocalDrives = true }));

        Assert.Equal(2, pvwa.Requests.Count);
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

    /// <summary>
    /// Fichier en UTF-16 sans BOM : accepté comme par la lecture de l'onglet, et rendu avec son BOM (mstsc).
    /// </summary>
    [Fact]
    public void RdpFile_AcceptsUtf16WithoutBom()
    {
        var bare = Encoding.Unicode.GetBytes(Rdp);

        var file = RdpFile.FromPsmConnectResponse(bare, "application/octet-stream");

        Assert.Equal([.. Encoding.Unicode.GetPreamble(), .. bare], file);
        Assert.Equal("psm.corp.local", Core.Rdp.RdpConnectionSettings.FromRdpFile(file).Server);
        Assert.Equal("psm.corp.local", Core.Rdp.RdpConnectionSettings.FromRdpFile(bare).Server);
    }

    [Fact]
    public void RdpFile_UnwrapsJsonString()
    {
        var wrapped = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Rdp));

        var file = RdpFile.FromPsmConnectResponse(wrapped, "application/json");

        Assert.Equal(Rdp, Encoding.Unicode.GetString(file, 2, file.Length - 2));
        Assert.Equal(Encoding.Unicode.GetPreamble(), file[..2]);
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
