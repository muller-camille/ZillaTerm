using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ZillaTerm.Core.Tests;

public sealed class UpdateCheckerTests
{
    private const string Download = "https://github.com/muller-camille/ZillaTerm/releases/download/v0.14.0/";

    private static string Release(string tag = "v0.14.0", string? extraAsset = null) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/muller-camille/ZillaTerm/releases/tag/{{tag}}",
          "published_at": "2026-11-02T10:00:00Z",
          "assets": [
            {{extraAsset}}
            { "name": "ZillaTerm-0.14.0-win-x64.zip", "browser_download_url": "{{Download}}ZillaTerm-0.14.0-win-x64.zip" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "{{Download}}SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheLatestRelease()
    {
        var info = UpdateChecker.ParseRelease(Release());
        Assert.Equal(new Version(0, 14, 0), info.Version);
        Assert.Equal("ZillaTerm-0.14.0-win-x64.zip", info.PackageName);
        Assert.Equal(Download + "SHA256SUMS.txt", info.SumsUrl);
        Assert.EndsWith("/releases/tag/v0.14.0", info.PageUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), info.Published);
    }

    [Fact]
    public void IgnoresFilesFromElsewhere()
    {
        // Une archive servie par une autre adresse que le dépôt du projet n'est jamais retenue.
        var info = UpdateChecker.ParseRelease(Release(extraAsset:
            """{ "name": "ZillaTerm-9.9.9-win-x64.zip", "browser_download_url": "https://evil.example/ZillaTerm-9.9.9-win-x64.zip" },"""));
        Assert.Equal("ZillaTerm-0.14.0-win-x64.zip", info.PackageName);

        var noAssets = UpdateChecker.ParseRelease("""{ "tag_name": "v1.0.0", "html_url": "https://evil.example/" }""");
        Assert.Null(noAssets.PackageUrl);
        Assert.Equal(UpdateChecker.ReleasesUrl, noAssets.PageUrl);

        Assert.Throws<FormatException>(() => UpdateChecker.ParseRelease("""{ "tag_name": "latest" }"""));
    }

    /// <summary>
    /// Transition de CyberArkTerm à ZillaTerm : le dépôt sous son ancien nom est accepté, et l'archive au nouveau nom passe
    /// avant celle à l'ancien nom (même contenu, publiée pour les anciennes versions).
    /// </summary>
    [Fact]
    public void AcceptsTheRepositoryUnderItsFormerName()
    {
        const string legacy = "https://github.com/muller-camille/CyberArkTerm/releases/download/v0.19.0/";
        var both = UpdateChecker.ParseRelease($$"""
            {
              "tag_name": "v0.19.0",
              "html_url": "https://github.com/muller-camille/CyberArkTerm/releases/tag/v0.19.0",
              "assets": [
                { "name": "CyberArkTerm-0.19.0-win-x64.zip", "browser_download_url": "{{legacy}}CyberArkTerm-0.19.0-win-x64.zip" },
                { "name": "ZillaTerm-0.19.0-win-x64.zip", "browser_download_url": "{{legacy}}ZillaTerm-0.19.0-win-x64.zip" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "{{legacy}}SHA256SUMS.txt" }
              ]
            }
            """);
        Assert.Equal("ZillaTerm-0.19.0-win-x64.zip", both.PackageName);
        Assert.Equal(legacy + "ZillaTerm-0.19.0-win-x64.zip", both.PackageUrl);
        Assert.Equal("https://github.com/muller-camille/CyberArkTerm/releases/tag/v0.19.0", both.PageUrl);

        var legacyOnly = UpdateChecker.ParseRelease($$"""
            {
              "tag_name": "v0.19.0",
              "assets": [
                { "name": "CyberArkTerm-0.19.0-win-x64.zip", "browser_download_url": "{{legacy}}CyberArkTerm-0.19.0-win-x64.zip" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "{{legacy}}SHA256SUMS.txt" }
              ]
            }
            """);
        Assert.Equal("CyberArkTerm-0.19.0-win-x64.zip", legacyOnly.PackageName);

        // Un autre dépôt du même compte n'est pas accepté.
        var other = UpdateChecker.ParseRelease("""
            {
              "tag_name": "v9.0.0",
              "html_url": "https://github.com/muller-camille/ZillaTermX/releases/tag/v9.0.0",
              "assets": [
                { "name": "ZillaTerm-9.0.0-win-x64.zip", "browser_download_url": "https://github.com/muller-camille/ZillaTermX/releases/download/v9.0.0/ZillaTerm-9.0.0-win-x64.zip" }
              ]
            }
            """);
        Assert.Null(other.PackageUrl);
        Assert.Equal(UpdateChecker.ReleasesUrl, other.PageUrl);
    }

    [Fact]
    public void ComparesVersions()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(0, 13, 0), new Version(0, 12, 0)));
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0), new Version(0, 99, 9)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 12, 0), new Version(0, 12, 0, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 11, 9), new Version(0, 12, 0)));
        Assert.Equal(3, UpdateChecker.CurrentVersion.ToString().Split('.').Length);
    }

    [Fact]
    public void FindsTheExpectedChecksum()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.zip\r\n{hash.ToUpperInvariant()} *ZillaTerm-0.14.0-win-x64.zip\r\n";
        Assert.Equal(hash, UpdateChecker.ExpectedSha256(sums, "ZillaTerm-0.14.0-win-x64.zip"));
        Assert.Null(UpdateChecker.ExpectedSha256(sums, "missing.zip"));
        Assert.Null(UpdateChecker.ExpectedSha256("not a checksum file", "x"));
    }

    [Fact]
    public async Task KeepsOnlyAVerifiedDownload()
    {
        var package = Encoding.UTF8.GetBytes("archive content");
        var good = Convert.ToHexStringLower(SHA256.HashData(package));
        var folder = Directory.CreateTempSubdirectory("cat-update-").FullName;
        try
        {
            var info = UpdateChecker.ParseRelease(Release());
            using var http = new HttpClient(new FakeGitHub(package, $"{good}  ZillaTerm-0.14.0-win-x64.zip\n"));
            var progress = new List<double>();
            var path = await UpdateChecker.DownloadAsync(http, info, folder, new SyncProgress(progress), default);
            Assert.Equal(Path.Combine(folder, "ZillaTerm-0.14.0-win-x64.zip"), path);
            Assert.Equal(package, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, progress[^1]);
            // Archive venue d'Internet : Mark-of-the-Web (Windows), avec son adresse de téléchargement.
            if (OperatingSystem.IsWindows())
            {
                var zone = MarkOfTheWeb.Read(path);
                Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
                Assert.Contains("HostUrl=" + info.PackageUrl, zone, StringComparison.Ordinal);
            }

            File.Delete(path);

            // Somme différente : rien n'est gardé.
            using var bad = new HttpClient(new FakeGitHub(package, $"{new string('0', 64)}  ZillaTerm-0.14.0-win-x64.zip\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(bad, info, folder, null, default));
            Assert.Empty(Directory.GetFiles(folder));

            // Archive absente de SHA256SUMS.txt : pas de téléchargement.
            using var missing = new HttpClient(new FakeGitHub(package, $"{good}  other.zip\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(missing, info, folder, null, default));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class FakeGitHub(byte[] package, string sums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpContent content = url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? new StringContent(sums)
                : new ByteArrayContent(package);
            if (content is ByteArrayContent && content is not StringContent)
            {
                content.Headers.ContentLength = package.Length;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
