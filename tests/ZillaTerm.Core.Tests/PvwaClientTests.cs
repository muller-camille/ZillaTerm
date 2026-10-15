using System.Net;
using System.Text.Json;

namespace ZillaTerm.Core.Tests;

public class PvwaClientTests
{
    [Theory]
    [InlineData("pvwa.corp.local", "https://pvwa.corp.local/PasswordVault/")]
    [InlineData("  https://pvwa.corp.local  ", "https://pvwa.corp.local/PasswordVault/")]
    [InlineData("https://pvwa.corp.local/PasswordVault", "https://pvwa.corp.local/PasswordVault/")]
    [InlineData("https://pvwa.corp.local/passwordvault/v10/logon", "https://pvwa.corp.local/passwordvault/")]
    [InlineData("https://pvwa.corp.local:8443/PasswordVault/", "https://pvwa.corp.local:8443/PasswordVault/")]
    [InlineData("https://cyberark.corp.local/prod/PasswordVault/v10/Accounts", "https://cyberark.corp.local/prod/PasswordVault/")]
    public void NormalizeBaseUri_ExtractsPasswordVaultRoot(string input, string expected)
    {
        Assert.Equal(expected, PvwaClient.NormalizeBaseUri(input).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://pvwa.corp.local/PasswordVault")]
    [InlineData("https://")]
    [InlineData("https://pvwa.corp.local:443@other.example/PasswordVault")]
    [InlineData("pvwa.corp.local@other.example")]
    [InlineData("https://user:secret@pvwa.corp.local/PasswordVault")]
    [InlineData("https://pvwa.corp.local\\@other.example/")]
    [InlineData("https://pvw\u0430.corp.local/PasswordVault")]
    [InlineData("https://pvwa.corp.local\u202e/PasswordVault")]
    [InlineData("https://pvwa .corp.local/PasswordVault")]
    public void NormalizeBaseUri_RejectsInvalidOrInsecureUrls(string input)
    {
        Assert.Throws<ArgumentException>(() => PvwaClient.NormalizeBaseUri(input));
    }

    [Theory]
    [InlineData("\"abc123==\"", "abc123==")]
    [InlineData("{\"CyberArkLogonResult\":\"legacy-token\"}", "legacy-token")]
    public void ParseToken_AcceptsV10AndLegacyFormats(string body, string expected)
    {
        Assert.Equal(expected, PvwaClient.ParseToken(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("<html>maintenance</html>")]
    [InlineData("{\"other\":1}")]
    public void ParseToken_RejectsResponsesWithoutToken(string body)
    {
        Assert.Throws<PvwaException>(() => PvwaClient.ParseToken(body));
    }

    [Fact]
    public async Task Logon_PostsCredentialsToMethodEndpoint()
    {
        var pvwa = new FakePvwa(_ => FakePvwa.Json("\"tok\""));
        using var client = pvwa.CreateClient();

        await client.LogonAsync(AuthMethod.LDAP, "jdoe", "s3cr\"et");

        var (method, path, auth, body) = Assert.Single(pvwa.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("/PasswordVault/API/auth/LDAP/Logon", path);
        Assert.Null(auth);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("jdoe", json.RootElement.GetProperty("username").GetString());
        Assert.Equal("s3cr\"et", json.RootElement.GetProperty("password").GetString());
        Assert.True(json.RootElement.GetProperty("concurrentSession").GetBoolean());
        Assert.True(client.IsAuthenticated);
        // Corps envoyé avec Content-Length (pas en « chunked »).
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), Assert.Single(pvwa.ContentLengths));
    }

    [Fact]
    public async Task Logon_Windows_SendsNoCredentialsInBody()
    {
        var pvwa = new FakePvwa(_ => FakePvwa.Json("\"tok\""));
        using var client = pvwa.CreateClient();

        await client.LogonAsync(AuthMethod.Windows, null, (string?)null);

        var request = Assert.Single(pvwa.Requests);
        Assert.Equal("/PasswordVault/API/auth/Windows/Logon", request.PathAndQuery);
        using var json = JsonDocument.Parse(request.Body);
        Assert.False(json.RootElement.TryGetProperty("username", out _));
        Assert.False(json.RootElement.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Logon_Failure_SurfacesPvwaErrorCodeAndMessage()
    {
        var pvwa = new FakePvwa(_ => FakePvwa.Json(
            "{\"ErrorCode\":\"ITATS004E\",\"ErrorMessage\":\"Authentication failure for User [jdoe].\"}",
            HttpStatusCode.Forbidden));
        using var client = pvwa.CreateClient();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.LogonAsync(AuthMethod.CyberArk, "jdoe", "bad"));

        Assert.Equal("ITATS004E", ex.ErrorCode);
        Assert.Equal("Authentication failure for User [jdoe]. (ITATS004E)", ex.Message);
        Assert.False(ex.IsRadiusChallenge);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task Logon_NonJsonError_FallsBackToHttpStatus()
    {
        var pvwa = new FakePvwa(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html>IIS</html>"),
        });
        using var client = pvwa.CreateClient();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw"));

        Assert.Null(ex.ErrorCode);
        Assert.StartsWith("HTTP 503", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect, "https://other.example/PasswordVault/API/auth/CyberArk/Logon?x=1", "https://other.example/PasswordVault/API/auth/CyberArk/Logon")]
    [InlineData(HttpStatusCode.Found, "/sso/login", "https://pvwa.test/sso/login")]
    public async Task Logon_RedirectIsNeverFollowed(HttpStatusCode status, string location, string shown)
    {
        // Le mot de passe ne part jamais vers l'adresse d'une redirection (307/308 renverraient le corps).
        using (var handler = PvwaClient.CreateHandler(AuthMethod.CyberArk))
        {
            Assert.False(handler.AllowAutoRedirect);
        }

        var pvwa = new FakePvwa(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });
        using var client = pvwa.CreateClient();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw"));

        Assert.Single(pvwa.Requests);
        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(shown, ex.Message);
        Assert.DoesNotContain("x=1", ex.Message);
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task Logon_AgainOnTheSameClient_ReplacesTheExpiredToken()
    {
        // Session expirée : la fenêtre de reconnexion rouvre la session sur le même client, que tout le reste garde.
        int logons = 0;
        var pvwa = new FakePvwa(r => FakePvwa.IsLogon(r)
            ? FakePvwa.Json($"\"tok{++logons}\"")
            : r.Headers.GetValues("Authorization").Single() == "tok1"
                ? FakePvwa.Json("{\"ErrorCode\":\"PASWS006E\",\"ErrorMessage\":\"Session expired\"}", HttpStatusCode.Unauthorized)
                : FakePvwa.Json("{\"value\":[],\"count\":0}"));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.KeepAliveAsync());
        Assert.True(ex.IsUnauthorized);

        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw2");
        await client.KeepAliveAsync();

        Assert.Equal("tok2", pvwa.Requests[^1].Authorization);
    }

    [Fact]
    public async Task Logon_RadiusChallenge_CanBeAnsweredOnSameClient()
    {
        int call = 0;
        var pvwa = new FakePvwa(_ => ++call == 1
            ? FakePvwa.Json("{\"ErrorCode\":\"ITATS542I\",\"ErrorMessage\":\"Enter the code sent to your phone\"}",
                HttpStatusCode.InternalServerError)
            : FakePvwa.Json("\"tok\""));
        using var client = pvwa.CreateClient();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.LogonAsync(AuthMethod.RADIUS, "jdoe", "pw"));
        Assert.True(ex.IsRadiusChallenge);
        Assert.Equal("Enter the code sent to your phone", ex.ServerMessage);

        await client.LogonAsync(AuthMethod.RADIUS, "jdoe", "123456");

        Assert.True(client.IsAuthenticated);
        Assert.Contains("\"password\":\"123456\"", pvwa.Requests[1].Body);
    }

    [Fact]
    public async Task GetAccounts_FollowsPaginationWithToken()
    {
        const int total = 2500;
        var pvwa = new FakePvwa(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/Logon", StringComparison.Ordinal))
            {
                return FakePvwa.Json("\"tok\"");
            }

            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            int offset = int.Parse(query["offset"]!);
            int limit = int.Parse(query["limit"]!);
            var items = Enumerable.Range(offset, Math.Max(0, Math.Min(limit, total - offset)))
                .Select(i => new { id = $"1_{i}", address = $"srv{i:D4}.corp.local", userName = "admin" });
            return FakePvwa.Json(JsonSerializer.Serialize(new { value = items, count = total }));
        });
        using var client = pvwa.CreateClient();
        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw");
        var reports = new List<ListPage<PvwaAccount>>();

        var accounts = await client.GetAccountsAsync(new SyncProgress<ListPage<PvwaAccount>>(reports.Add));

        Assert.Equal(total, accounts.Count);
        Assert.Equal("srv2499.corp.local", accounts[^1].Address);
        var pages = pvwa.Requests.Skip(1).ToList();
        Assert.Equal(
            ["/PasswordVault/API/Accounts?offset=0&limit=1000",
             "/PasswordVault/API/Accounts?offset=1000&limit=1000",
             "/PasswordVault/API/Accounts?offset=2000&limit=1000"],
            pages.Select(p => p.PathAndQuery));
        Assert.All(pages, p => Assert.Equal("tok", p.Authorization));
        Assert.Equal([(1000, total), (2000, total), (total, total)], reports.Select(r => (r.Loaded, r.Total)));
        // Chaque page avec ses comptes, dans l'ordre : de quoi remplir la liste au fur et à mesure.
        Assert.Equal([1000, 1000, 500], reports.Select(r => r.Items.Count));
        Assert.Equal(accounts.Select(a => a.Id), reports.SelectMany(r => r.Items).Select(a => a.Id));
    }

    /// <summary>Réponse du PVWA (valeurs fictives) : utilisateur, groupe avec date de fin, pagination sur le total.</summary>
    [Fact]
    public async Task GetSafeMembers_ReadsMembersAndPermissionsPageByPage()
    {
        var pvwa = new FakePvwa(req =>
        {
            if (FakePvwa.IsLogon(req))
            {
                return FakePvwa.Json("\"tok\"");
            }

            return req.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? FakePvwa.Json("""
                    {"value":[{"safeUrlId":"Prod%20Linux","safeName":"Prod Linux","memberId":"12","memberName":"jdoe","memberType":"User",
                       "isPredefinedUser":false,"membershipExpirationDate":null,
                       "permissions":{"useAccounts":true,"retrieveAccounts":false,"listAccounts":true,"addAccounts":false,
                         "viewSafeMembers":true,"requestsAuthorizationLevel1":false}}],
                     "count":2,"nextLink":"api/Safes/Prod%20Linux/Members?offset=1&limit=1000"}
                    """)
                : FakePvwa.Json("""
                    {"value":[{"memberName":"Unix Admins","memberType":"Group","membershipExpirationDate":1767225600,
                       "permissions":{"listAccounts":true,"addAccounts":true,"updateAccountContent":true,
                         "manageSafeMembers":true,"requestsAuthorizationLevel2":true}}],"count":2}
                    """);
        });
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var members = await client.GetSafeMembersAsync("Prod Linux");

        Assert.Equal(
            ["/PasswordVault/API/Safes/Prod%20Linux/Members?offset=0&limit=1000",
             "/PasswordVault/API/Safes/Prod%20Linux/Members?offset=1&limit=1000"],
            pvwa.Requests.Skip(1).Select(r => r.PathAndQuery));
        Assert.All(pvwa.Requests.Skip(1), r => Assert.Equal("tok", r.Authorization));
        Assert.Equal(["jdoe", "Unix Admins"], members.Select(m => m.MemberName));
        Assert.Equal((false, null), (members[0].IsGroup, members[0].Expires));
        Assert.Equal(["UseAccounts", "ListAccounts", "ViewSafeMembers"], members[0].Permissions.Granted());
        Assert.True(members[1].IsGroup);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime(), members[1].Expires);
        Assert.Equal(["ListAccounts", "AddAccounts", "UpdateAccountContent", "ManageSafeMembers", "RequestsAuthorizationLevel2"],
            members[1].Permissions.Granted());
        Assert.Equal(22, members[1].Permissions.All().Count());
    }

    /// <summary>Sans le droit « View Safe Members », le PVWA répond 403 : l'erreur garde le statut pour l'expliquer.</summary>
    [Fact]
    public async Task GetSafeMembers_WithoutViewMembersRight_ThrowsForbidden()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"ErrorCode\":\"SFWS0007E\",\"ErrorMessage\":\"Access denied.\"}", HttpStatusCode.Forbidden));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.GetSafeMembersAsync("Prod"));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.False(ex.IsUnauthorized);
    }

    [Theory]
    [InlineData("\"2026-01-01T00:00:00Z\"", true)]
    [InlineData("0", false)]
    [InlineData("null", false)]
    [InlineData("\"pas une date\"", false)]
    public void SafeMemberExpirationAcceptsSecondsOrText(string json, bool hasDate)
    {
        var member = JsonSerializer.Deserialize<SafeMember>($"{{\"memberName\":\"x\",\"membershipExpirationDate\":{json}}}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(hasDate, member.Expires is not null);
        if (hasDate)
        {
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime(), member.Expires);
        }
    }

    /// <summary>Création d'un compte (valeurs fictives) : champs envoyés, mot de passe en dernier, compte créé relu.</summary>
    [Fact]
    public async Task AddAccount_PostsTheAccountAndReadsTheCreatedOne()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("""{"id":"12_34","name":"Op-srv01","address":"srv01.corp.local","userName":"svc_app","platformId":"WinDomain","safeName":"Prod Windows"}""",
                HttpStatusCode.Created));
        using var client = await pvwa.CreateLoggedOnClientAsync();
        char[] secret = "p\"a\\ss<é>".ToCharArray();

        var created = await client.AddAccountAsync(new NewAccount
        {
            SafeName = " Prod Windows ", PlatformId = "WinDomain", Address = "srv01.corp.local", UserName = "svc_app",
            Name = "Op-srv01", LogonDomain = "CORP", Secret = secret, RemoteMachines = "srv01, srv02 ;srv03",
        });

        var (method, path, auth, body) = pvwa.Requests[1];
        Assert.Equal((HttpMethod.Post, "/PasswordVault/API/Accounts", "tok"), (method, path, auth));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), pvwa.ContentLengths[1]);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("Prod Windows", root.GetProperty("safeName").GetString());
        Assert.Equal("Op-srv01", root.GetProperty("name").GetString());
        Assert.Equal("password", root.GetProperty("secretType").GetString());
        Assert.Equal("CORP", root.GetProperty("platformAccountProperties").GetProperty("LogonDomain").GetString());
        Assert.True(root.GetProperty("secretManagement").GetProperty("automaticManagementEnabled").GetBoolean());
        Assert.Equal("srv01;srv02;srv03", root.GetProperty("remoteMachinesAccess").GetProperty("remoteMachines").GetString());
        Assert.True(root.GetProperty("remoteMachinesAccess").GetProperty("accessRestrictedToRemoteMachines").GetBoolean());
        Assert.Equal("p\"a\\ss<é>", root.GetProperty("secret").GetString());
        Assert.Equal("secret", root.EnumerateObject().Last().Name);
        Assert.Equal(("12_34", "Prod Windows"), (created.Id, created.SafeName));
    }

    /// <summary>Champs facultatifs absents : ni nom, ni mot de passe, ni domaine, ni machines ; gestion manuelle motivée.</summary>
    [Fact]
    public async Task AddAccount_LeavesOutEmptyOptionalFields()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("""{"id":"1_1","address":"lnx01","userName":"root","safeName":"Linux"}""", HttpStatusCode.Created));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.AddAccountAsync(new NewAccount
        {
            SafeName = "Linux", PlatformId = "UnixSSH", Address = "lnx01", UserName = "root", Name = " ", Secret = [],
            AutomaticManagement = false, ManualManagementReason = "Compte de secours",
        });

        using var json = JsonDocument.Parse(pvwa.Requests[1].Body);
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["address", "userName", "platformId", "safeName", "secretType", "secretManagement"], names);
        var management = json.RootElement.GetProperty("secretManagement");
        Assert.False(management.GetProperty("automaticManagementEnabled").GetBoolean());
        Assert.Equal("Compte de secours", management.GetProperty("manualManagementReason").GetString());
    }

    [Fact]
    public async Task AddAccount_Refused_SurfacesThePvwaError()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"ErrorCode\":\"PASWS041E\",\"ErrorMessage\":\"Not authorized.\"}", HttpStatusCode.Forbidden));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.AddAccountAsync(new NewAccount
        {
            SafeName = "S", PlatformId = "P", Address = "a", UserName = "u",
        }));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("Not authorized. (PASWS041E)", ex.Message);
    }

    [Fact]
    public async Task UpdateAccount_SendsTheJsonPatchOperations()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("""{"id":"12_34","address":"srv02","userName":"svc"}"""));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var updated = await client.UpdateAccountAsync("12_34",
            [new PatchOperation("replace", "/address", "srv02"), new PatchOperation("remove", "/platformAccountProperties/LogonDomain"),
             new PatchOperation("replace", "/remoteMachinesAccess/accessRestrictedToRemoteMachines", false)]);

        var (method, path, _, body) = pvwa.Requests[1];
        Assert.Equal((HttpMethod.Patch, "/PasswordVault/API/Accounts/12_34"), (method, path));
        Assert.Equal(
            """[{"op":"replace","path":"/address","value":"srv02"},{"op":"remove","path":"/platformAccountProperties/LogonDomain"},{"op":"replace","path":"/remoteMachinesAccess/accessRestrictedToRemoteMachines","value":false}]""",
            body);
        Assert.Equal("srv02", updated.Address);
    }

    [Fact]
    public async Task DeleteAccount_SendsDelete()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.DeleteAccountAsync("12_34");

        Assert.Equal((HttpMethod.Delete, "/PasswordVault/API/Accounts/12_34", "tok"), (pvwa.Requests[1].Method, pvwa.Requests[1].PathAndQuery, pvwa.Requests[1].Authorization));
    }

    [Theory]
    [InlineData(CpmAction.Verify, "Verify", "")]
    [InlineData(CpmAction.Change, "Change", "{\"ChangeEntireGroup\":true}")]
    [InlineData(CpmAction.Reconcile, "Reconcile", "")]
    public async Task CpmActions_PostToTheirEndpoint(CpmAction action, string endpoint, string body)
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.RunCpmActionAsync("12_34", action);

        Assert.Equal((HttpMethod.Post, $"/PasswordVault/API/Accounts/12_34/{endpoint}", body),
            (pvwa.Requests[1].Method, pvwa.Requests[1].PathAndQuery, pvwa.Requests[1].Body));
    }

    /// <summary>Mot de passe fictif renvoyé comme chaîne JSON (caractères échappés) : décodé tel quel, motif et usage envoyés.</summary>
    [Fact]
    public async Task RetrievePassword_DecodesTheJsonString()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("\"p\\\"a\\\\ss\\u00e9<>\"\r\n"));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var secret = await client.RetrievePasswordAsync("12_34", new RetrieveOptions { Reason = " Incident 42 ", TicketId = "INC42", TicketingSystemName = "SNOW" });

        Assert.Equal("p\"a\\ssé<>", new string(secret));
        var (method, path, _, body) = pvwa.Requests[1];
        Assert.Equal((HttpMethod.Post, "/PasswordVault/API/Accounts/12_34/Password/Retrieve"), (method, path));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("copy", json.RootElement.GetProperty("ActionType").GetString());
        Assert.False(json.RootElement.GetProperty("isUse").GetBoolean());
        Assert.Equal("Incident 42", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("INC42", json.RootElement.GetProperty("TicketId").GetString());
    }

    [Theory]
    [InlineData("plain-text", "plain-text")]
    [InlineData("\"\"", "")]
    public void DecodeSecret_AcceptsPlainTextAndEmptyStrings(string body, string expected)
    {
        Assert.Equal(expected, new string(PvwaClient.DecodeSecret(System.Text.Encoding.UTF8.GetBytes(body))));
    }

    /// <summary>
    /// Page de maintenance HTML, redirection suivie vers une page de connexion SSO, réponse vide ou JSON qui n'est pas une
    /// chaîne : jamais pris pour le mot de passe.
    /// </summary>
    [Theory]
    [InlineData("html")]
    [InlineData("redirect")]
    [InlineData("empty")]
    [InlineData("nocontent")]
    [InlineData("object")]
    public async Task RetrievePassword_RefusesAnythingButThePassword(string kind)
    {
        var pvwa = new FakePvwa(req =>
        {
            if (FakePvwa.IsLogon(req))
            {
                return FakePvwa.Json("\"tok\"");
            }

            return kind switch
            {
                "html" => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("<html>Maintenance</html>", System.Text.Encoding.UTF8, "text/html") },
                "redirect" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("login", System.Text.Encoding.UTF8, "text/plain"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://sso.test/login"),
                },
                "empty" => FakePvwa.Json(" \r\n"),
                "nocontent" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => FakePvwa.Json("{\"ok\":true}"),
            };
        });
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await Assert.ThrowsAsync<PvwaException>(() => client.RetrievePasswordAsync("12_34", new RetrieveOptions()));
    }

    [Fact]
    public async Task RetrievePassword_Refused_SurfacesThePvwaError()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"ErrorCode\":\"ITATS542I\",\"ErrorMessage\":\"Reason required.\"}", HttpStatusCode.Forbidden));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.RetrievePasswordAsync("1", new RetrieveOptions()));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    /// <summary>
    /// Fin d'appartenance un jour de changement d'heure (Paris) : 23:59:59 à l'heure de ce soir-là, sans erreur.
    /// </summary>
    [Theory]
    [InlineData(2026, 3, 29, "2026-03-29T21:59:59Z")]
    [InlineData(2026, 10, 25, "2026-10-25T22:59:59Z")]
    [InlineData(2026, 7, 14, "2026-07-14T21:59:59Z")]
    public void EndOfDayUsesTheOffsetOfThatEvening(int year, int month, int day, string expected)
    {
        var paris = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Paris", out var iana) ? iana
            : TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            UnixTime.EndOfDay(date, paris));
    }

    /// <summary>Dates hors limites (millisecondes au lieu de secondes, valeur aberrante) : reconnues ou ignorées, jamais d'erreur.</summary>
    [Fact]
    public void OutOfRangeTimestampsNeverThrow()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_767_225_600), UnixTime.ToOffset(1_767_225_600_000));
        Assert.Null(UnixTime.ToOffset(long.MaxValue));
        Assert.Null(UnixTime.ToOffset(0));
        Assert.Null(new PvwaAccount { CreatedTime = long.MaxValue }.Created);
        var member = JsonSerializer.Deserialize<SafeMember>("{\"memberName\":\"x\",\"membershipExpirationDate\":9223372036854775807}")!;
        Assert.Null(member.Expires);
    }

    /// <summary>Ajout d'un membre fictif : nom, annuaire, type, fin d'appartenance (fin de journée) et les 22 droits.</summary>
    [Fact]
    public async Task SafeMembers_AddUpdateAndRemove()
    {
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : FakePvwa.Json("{}", HttpStatusCode.Created));
        using var client = await pvwa.CreateLoggedOnClientAsync();
        var expires = new DateTime(2026, 12, 31);

        await client.AddSafeMemberAsync("Prod Linux", new SafeMemberChange
        {
            MemberName = " Unix Admins ", MemberType = "Group", SearchIn = "corp.example", Expires = expires,
            Permissions = new SafePermissions { ListAccounts = true, UseAccounts = true, InitiateCPMAccountManagementOperations = true },
        });
        await client.UpdateSafeMemberAsync("Prod Linux", new SafeMemberChange
        {
            MemberName = "corp\\jdoe", Permissions = new SafePermissions { ListAccounts = true },
        });
        await client.RemoveSafeMemberAsync("Prod Linux", "corp\\jdoe");

        var (addMethod, addPath, _, addBody) = pvwa.Requests[1];
        Assert.Equal((HttpMethod.Post, "/PasswordVault/API/Safes/Prod%20Linux/Members"), (addMethod, addPath));
        using (var json = JsonDocument.Parse(addBody))
        {
            var root = json.RootElement;
            Assert.Equal(("Unix Admins", "corp.example", "Group"),
                (root.GetProperty("memberName").GetString(), root.GetProperty("searchIn").GetString(), root.GetProperty("memberType").GetString()));
            var end = new DateTimeOffset(expires.AddDays(1).AddSeconds(-1), TimeZoneInfo.Local.GetUtcOffset(expires)).ToUnixTimeSeconds();
            Assert.Equal(end, root.GetProperty("membershipExpirationDate").GetInt64());
            var permissions = root.GetProperty("permissions");
            Assert.Equal(22, permissions.EnumerateObject().Count());
            Assert.True(permissions.GetProperty("initiateCPMAccountManagementOperations").GetBoolean());
            Assert.True(permissions.GetProperty("useAccounts").GetBoolean());
            Assert.False(permissions.GetProperty("addAccounts").GetBoolean());
        }

        var (updateMethod, updatePath, _, updateBody) = pvwa.Requests[2];
        Assert.Equal((HttpMethod.Put, "/PasswordVault/API/Safes/Prod%20Linux/Members/corp%5Cjdoe"), (updateMethod, updatePath));
        using (var json = JsonDocument.Parse(updateBody))
        {
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("membershipExpirationDate").ValueKind);
            Assert.False(json.RootElement.TryGetProperty("memberName", out _));
        }

        Assert.Equal((HttpMethod.Delete, "/PasswordVault/API/Safes/Prod%20Linux/Members/corp%5Cjdoe"),
            (pvwa.Requests[3].Method, pvwa.Requests[3].PathAndQuery));
    }

    /// <summary>
    /// PVWA qui renvoie toujours la même page avec un total énorme (compromis, ou qui ignore l'offset) : la lecture
    /// s'arrête à la première page sans compte nouveau, sans les doublons.
    /// </summary>
    [Fact]
    public async Task GetAccounts_StopsWhenAPageBringsNothingNew()
    {
        var page = JsonSerializer.Serialize(new
        {
            value = Enumerable.Range(0, 3).Select(i => new { id = $"1_{i}", address = $"srv{i:D2}.corp.local", userName = "root" }),
            count = int.MaxValue,
        });
        var pvwa = new FakePvwa(req => FakePvwa.IsLogon(req) ? FakePvwa.Json("\"tok\"") : FakePvwa.Json(page));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var accounts = await client.GetAccountsAsync();

        Assert.Equal(["1_0", "1_1", "1_2"], accounts.Select(a => a.Id));
        Assert.Equal(["/PasswordVault/API/Accounts?offset=0&limit=1000", "/PasswordVault/API/Accounts?offset=3&limit=1000"],
            pvwa.Requests.Skip(1).Select(r => r.PathAndQuery));
    }

    /// <summary>
    /// Sans total (count 0) et avec une page suivante toujours annoncée, chaque page apportant un nouvel élément : erreur
    /// après <see cref="PvwaClient.MaxPages"/> pages au lieu d'une boucle sans fin (comptes et membres d'un safe).
    /// </summary>
    [Fact]
    public async Task Pagination_StopsWithAnErrorAfterTheMaximumNumberOfPages()
    {
        int served = 0;
        var pvwa = new FakePvwa(req =>
        {
            if (FakePvwa.IsLogon(req))
            {
                return FakePvwa.Json("\"tok\"");
            }

            int n = Interlocked.Increment(ref served);
            return FakePvwa.Json(JsonSerializer.Serialize(new
            {
                value = new[] { new { id = $"1_{n}", memberName = $"user{n}", address = "srv01.corp.local", userName = "root" } },
                count = 0,
                nextLink = "api/next",
            }));
        });
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var accounts = await Assert.ThrowsAsync<PvwaException>(() => client.GetAccountsAsync());
        Assert.Equal(PvwaClient.MaxPages, served);
        Assert.Contains(PvwaClient.MaxPages.ToString(System.Globalization.CultureInfo.CurrentCulture), accounts.Message);

        served = 0;
        await Assert.ThrowsAsync<PvwaException>(() => client.GetSafeMembersAsync("Prod"));
        Assert.Equal(PvwaClient.MaxPages, served);
    }

    [Fact]
    public async Task GetAccounts_StopsOnEmptyPage()
    {
        var pvwa = new FakePvwa(req => req.RequestUri!.AbsolutePath.EndsWith("/Logon", StringComparison.Ordinal)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"value\":[],\"count\":42}"));
        using var client = pvwa.CreateClient();
        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw");

        var accounts = await client.GetAccountsAsync();

        Assert.Empty(accounts);
        Assert.Equal(2, pvwa.Requests.Count);
    }

    [Fact]
    public async Task GetAccounts_MapsAccountFields()
    {
        var pvwa = new FakePvwa(req => req.RequestUri!.AbsolutePath.EndsWith("/Logon", StringComparison.Ordinal)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("""
                {"value":[{
                  "id":"12_3","name":"Operating System-WinDomain-corp.local-adm.jdoe",
                  "address":"corp.local","userName":"adm.jdoe","platformId":"WinDomain","safeName":"T0-Admins",
                  "secretType":"password","createdTime":1700000000,
                  "platformAccountProperties":{"LogonDomain":"CORP","Port":3389},
                  "remoteMachinesAccess":{"remoteMachines":"srv1;srv2","accessRestrictedToRemoteMachines":true},
                  "secretManagement":{"automaticManagementEnabled":true,"lastModifiedTime":1700000100}
                }],"count":1}
                """));
        using var client = pvwa.CreateClient();
        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw");

        var a = Assert.Single(await client.GetAccountsAsync());

        Assert.Equal("12_3", a.Id);
        Assert.Equal("corp.local", a.Address);
        Assert.Equal("adm.jdoe", a.UserName);
        Assert.Equal("WinDomain", a.PlatformId);
        Assert.Equal("T0-Admins", a.SafeName);
        Assert.Equal("CORP", a.LogonDomain);
        Assert.Equal("3389", a.GetPlatformProperty("port"));
        Assert.Equal("srv1;srv2", a.RemoteMachines);
        Assert.True(a.RemoteMachinesAccess!.AccessRestrictedToRemoteMachines);
        Assert.Equal(1700000000, a.CreatedTime);
    }

    [Fact]
    public async Task GetAccounts_ExpiredSession_IsReportedAsUnauthorized()
    {
        var pvwa = new FakePvwa(req => req.RequestUri!.AbsolutePath.EndsWith("/Logon", StringComparison.Ordinal)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"ErrorCode\":\"PASWS013E\",\"ErrorMessage\":\"Session timed out\"}", HttpStatusCode.Unauthorized));
        using var client = pvwa.CreateClient();
        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw");

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.GetAccountsAsync());

        Assert.True(ex.IsUnauthorized);
    }

    [Fact]
    public async Task GetAccounts_WithoutLogon_Throws()
    {
        var pvwa = new FakePvwa(_ => throw new InvalidOperationException("no request expected"));
        using var client = pvwa.CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccountsAsync());
        Assert.Empty(pvwa.Requests);
    }

    [Fact]
    public async Task Logoff_SendsTokenOnceThenForgetsIt()
    {
        var pvwa = new FakePvwa(_ => FakePvwa.Json("\"tok\""));
        using var client = pvwa.CreateClient();
        await client.LogonAsync(AuthMethod.CyberArk, "jdoe", "pw");

        await client.LogoffAsync();
        await client.LogoffAsync();

        Assert.Equal(2, pvwa.Requests.Count);
        Assert.Equal("/PasswordVault/API/Auth/Logoff", pvwa.Requests[1].PathAndQuery);
        Assert.Equal("tok", pvwa.Requests[1].Authorization);
        Assert.False(client.IsAuthenticated);
    }

    /// <summary><see cref="Progress{T}"/> poste de manière asynchrone ; ici on veut un rapport synchrone.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task KeepAlive_GetsOneAccountWithTheSessionToken()
    {
        var pvwa = new FakePvwa(r => FakePvwa.IsLogon(r) ? FakePvwa.Json("\"tok\"") : FakePvwa.Json("{\"value\":[],\"count\":0}"));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        await client.KeepAliveAsync();

        var (method, path, auth, _) = pvwa.Requests[^1];
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal("/PasswordVault/API/Accounts?offset=0&limit=1", path);
        Assert.Equal("tok", auth);
    }

    [Fact]
    public async Task KeepAlive_ReportsAnExpiredSession()
    {
        var pvwa = new FakePvwa(r => FakePvwa.IsLogon(r)
            ? FakePvwa.Json("\"tok\"")
            : FakePvwa.Json("{\"ErrorCode\":\"PASWS006E\",\"ErrorMessage\":\"Session expired\"}", HttpStatusCode.Unauthorized));
        using var client = await pvwa.CreateLoggedOnClientAsync();

        var ex = await Assert.ThrowsAsync<PvwaException>(() => client.KeepAliveAsync());

        Assert.True(ex.IsUnauthorized);
    }
}
