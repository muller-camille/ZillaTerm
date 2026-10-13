using System.Net;
using System.Text.Json;

namespace ZillaTerm.Core.Tests;

/// <summary>Composants PSM d'une plateforme lus dans « Get platform details » (GET API/Platforms/{PlatformID}/).</summary>
public class PlatformComponentsTests
{
    private static FakePvwa Pvwa(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : respond(req));

    /// <summary>
    /// Lecture par le client : ID de plateforme échappé dans l'adresse, composants activés de la plateforme dans leur
    /// ordre, sans les désactivés, ceux du PSMP, les noms invalides ni les doublons.
    /// </summary>
    [Fact]
    public async Task Client_ReadsTheEnabledComponentsOfThePlatform()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json("""
            {"PlatformID":"Win Domain/T1","Active":true,
             "Details":{"PolicyID":"Win Domain/T1","PSMServerID":"PSMServer",
               "ConnectionComponents":[{"PSMConnectorID":"WIN-PSM","Enabled":true},{"PSMConnectorID":"PSM-RDP","Enabled":false},
                 {"PSMConnectorID":"PSMP-SSH"},{"PSMConnectorID":"PSM WinSCP"},{"PSMConnectorID":"PSM-WinSCP"},{"PSMConnectorID":"win-psm"}]}}
            """));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var components = await client.GetPlatformConnectionComponentsAsync(" Win Domain/T1 ");

        Assert.Equal(["WIN-PSM", "PSM-WinSCP"], components);
        var (method, path, auth, _) = pvwa.Requests[1];
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/PasswordVault/API/Platforms/Win%20Domain%2FT1/", path);
        Assert.Equal("tok", auth);
    }

    /// <summary>Réponse sans composant (plateforme sans PSM, version du PVWA qui ne les donne pas) : liste vide.</summary>
    [Fact]
    public async Task Client_ResponseWithoutComponentsGivesAnEmptyList()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json("""
            {"PlatformID":"UnixSSH","Active":true,"Details":{"PolicyID":"UnixSSH","PSMServerID":"PSMServer","Interval":"1440"}}
            """));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        Assert.Empty(await client.GetPlatformConnectionComponentsAsync("UnixSSH"));
    }

    /// <summary>Refus du PVWA (droit manquant) : PvwaException avec son statut, comme les autres appels.</summary>
    [Fact]
    public async Task Client_RefusalIsReported()
    {
        var pvwa = Pvwa(_ => FakePvwa.Json("""{"ErrorCode":"PASWS041E","ErrorMessage":"You are not authorized to perform this action."}""",
            HttpStatusCode.Forbidden));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.GetPlatformConnectionComponentsAsync("WinDomain"));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("PASWS041E", ex.ErrorCode);
    }

    /// <summary>Formes de réponse acceptées : listes à tout niveau, de noms ou d'objets, noms de propriétés sans casse.</summary>
    [Theory]
    // Liste de noms au premier niveau.
    [InlineData("""{"ConnectionComponents":["PSM-RDP","PSM-WinSCP"]}""", "PSM-RDP,PSM-WinSCP")]
    // Réglages PSM de la plateforme (PSMConnectors), « Enabled » booléen.
    [InlineData("""{"PrivilegedSessionManagement":{"PSMServerId":"PSM01","PSMConnectors":[{"PSMConnectorID":"WIN-PSM","Enabled":true},{"PSMConnectorID":"PSM-RDP","Enabled":false}]}}""", "WIN-PSM")]
    // Détails de la plateforme, « Enabled » en texte, noms sous d'autres propriétés.
    [InlineData("""{"Details":{"ConnectionComponents":[{"ConnectionComponentId":"PSM-RDP","Enabled":"Yes"},{"Id":"PSM-WinSCP","Enabled":"No"},{"Name":"PSM-SSH","Enabled":"true"},{"ID":"PSM-Telnet","Enabled":"false"}]}}""", "PSM-RDP,PSM-SSH")]
    // Casse des propriétés ignorée.
    [InlineData("""{"details":{"connectioncomponents":[{"psmconnectorid":"PSM-RDP","enabled":"NO"},{"psmConnectorId":"WIN-PSM"}]}}""", "WIN-PSM")]
    // Nom pris dans la première propriété de la liste, quel que soit l'ordre dans l'objet ; ID numérique : nom suivant.
    [InlineData("""{"PSMConnectors":[{"Name":"Remote Desktop","PSMConnectorID":"PSM-RDP"},{"ID":7,"Name":"WIN-PSM"}]}""", "PSM-RDP,WIN-PSM")]
    // Enveloppe tirée du XML de la plateforme, liste ou élément seul.
    [InlineData("""{"Details":{"ConnectionComponents":{"ConnectionComponent":[{"Id":"PSM-RDP"},{"Id":"PSM-RDP-Admin","Enable":"No"}]}}}""", "PSM-RDP")]
    [InlineData("""{"ConnectionComponents":{"ConnectionComponent":{"Id":"WIN-PSM"}}}""", "WIN-PSM")]
    // Texte « a, b ».
    [InlineData("""{"Details":{"ConnectionComponents":"PSM-RDP, PSM-WinSCP"}}""", "PSM-RDP,PSM-WinSCP")]
    // Deux listes : dans l'ordre de la réponse, sans doublon.
    [InlineData("""{"Details":{"ConnectionComponents":["WIN-PSM","PSM-RDP"]},"PrivilegedSessionManagement":{"PSMConnectors":[{"PSMConnectorID":"psm-rdp"},{"PSMConnectorID":"PSM-WinSCP"}]}}""", "WIN-PSM,PSM-RDP,PSM-WinSCP")]
    // Liste de plateformes.
    [InlineData("""[{"PlatformID":"WinDomain","ConnectionComponents":["WIN-PSM"]}]""", "WIN-PSM")]
    // Aucune liste de composants.
    [InlineData("""{"PlatformID":"WinDomain","Details":{"PolicyID":"WinDomain","PSMServerID":"PSMServer","ConnectionComponentsCount":"2"}}""", "")]
    [InlineData("""{"ConnectionComponents":[]}""", "")]
    [InlineData("[]", "")]
    [InlineData("\"WinDomain\"", "")]
    public void Parse_AcceptsTheKnownShapes(string json, string expected)
    {
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), PlatformComponents.Parse(json));
    }

    /// <summary>
    /// Seuls les noms valides selon la règle des Paramètres, sans ceux du PSMP ni les modes « SSH » / « SFTP » des
    /// connexions récentes, sont gardés.
    /// </summary>
    [Fact]
    public void Parse_KeepsOnlyValidPsmComponents()
    {
        var components = PlatformComponents.Parse("""
            {"ConnectionComponents":["PSM RDP","PSMP-SSH","psmp-scp","SSH","sftp","<PSM>","",null,42,true,
              "PSM-RDP"," psm-rdp ","PSM-SQLServerMgmtStudio",{"PSMConnectorID":"PSMP-SFTP"},{"PSMConnectorID":"PSM-RDP\u202E"},
              {"Enabled":true},"PSM.Custom_1"]}
            """);

        Assert.Equal(["PSM-RDP", "PSM-SQLServerMgmtStudio", "PSM.Custom_1"], components);
        Assert.False(PlatformComponents.IsPsmComponent("PSMP-RDP"));
        Assert.False(PlatformComponents.IsPsmComponent(null));
        Assert.True(PlatformComponents.IsPsmComponent(" WIN-PSM "));
    }

    /// <summary>Réponse qui n'est pas du JSON ou trop imbriquée : refusée (rien de mémorisé par l'appelant).</summary>
    [Fact]
    public void Parse_RejectsNonJsonAndDeepNesting()
    {
        Assert.ThrowsAny<JsonException>(() => PlatformComponents.Parse("<html>Error</html>"));
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 40)) + """{"ConnectionComponents":["PSM-RDP"]}""" + new string('}', 40);
        Assert.ThrowsAny<JsonException>(() => PlatformComponents.Parse(deep));
    }

    /// <summary>
    /// Composant demandé : le prévu s'il est sur la plateforme (casse ignorée) ou si la plateforme n'est pas connue,
    /// sinon le premier de la plateforme (le PVWA refuserait l'autre).
    /// </summary>
    [Fact]
    public void Choose_TakesTheFirstPlatformComponentOnlyWhenThePlannedOneIsMissing()
    {
        IReadOnlyList<string> platform = ["WIN-PSM", "PSM-WinSCP"];

        Assert.Equal("WIN-PSM", PlatformComponents.Choose("PSM-RDP", platform));
        Assert.Equal("psm-winscp", PlatformComponents.Choose("psm-winscp", platform));
        Assert.Equal(" PSM-WinSCP ", PlatformComponents.Choose(" PSM-WinSCP ", platform));
        Assert.Equal("PSM-RDP", PlatformComponents.Choose("PSM-RDP", []));
        Assert.Equal("PSM-RDP", PlatformComponents.Choose("PSM-RDP", null));
    }

    /// <summary>Listes de composants : ceux de la plateforme d'abord, puis les mémorisés et usuels, sans doublon.</summary>
    [Fact]
    public void KnownComponents_PutThePlatformOnesFirst()
    {
        var settings = new AppSettings();
        settings.RememberComponent("WinDomain", "PSM-RDP");

        var components = settings.KnownComponents("WinDomain", ["WIN-PSM", "psm-rdp"]);

        Assert.Equal(["WIN-PSM", "psm-rdp", "PSM-SSH"], components.Take(3));
        Assert.Equal(components.Count, components.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(settings.KnownComponents("WinDomain"), settings.KnownComponents("WinDomain", []));
    }
}
