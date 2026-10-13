using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core;

/// <summary>Dernière version publiée sur GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? PackageName, string? PackageUrl, string? SumsUrl,
    DateTimeOffset? Published);

/// <summary>
/// Recherche d'une nouvelle version (page des versions GitHub du projet) et téléchargement vérifié : l'archive est
/// comparée à <c>SHA256SUMS.txt</c> de la même version avant d'être gardée. Rien n'est installé ni lancé : l'utilisateur
/// remplace lui-même l'exécutable. Seules les adresses du dépôt du projet sont suivies.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "muller-camille/ZillaTerm";

    /// <summary>Ancien nom du dépôt (CyberArkTerm), redirigé par GitHub : ses adresses restent acceptées.</summary>
    public const string LegacyRepository = "muller-camille/CyberArkTerm";

    public const string ProjectUrl = "https://github.com/" + Repository;
    public const string ReleasesUrl = ProjectUrl + "/releases";
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>Au-delà, le téléchargement est interrompu (l'archive fait environ 70 Mo).</summary>
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    private static readonly string[] ProjectUrls = [ProjectUrl, "https://github.com/" + LegacyRepository];

    /// <summary>Archive de la version : « ZillaTerm-… », ou « CyberArkTerm-… » pour une version publiée sous l'ancien nom.</summary>
    private const string PackagePrefix = "ZillaTerm-";

    private const string LegacyPackagePrefix = AppSettings.LegacyName + "-";

    /// <summary>Version de l'application (celle de Directory.Build.props).</summary>
    public static Version CurrentVersion => Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static UpdateInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            throw new FormatException(string.Format(CultureInfo.CurrentCulture, CoreStrings.UpdateBadVersion, tag));
        }

        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        if (page is null || !ProjectUrls.Any(p => page.StartsWith(p + "/", StringComparison.Ordinal)))
        {
            page = ReleasesUrl;
        }

        string? packageName = null;
        string? packageUrl = null;
        string? sumsUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                // Seulement des fichiers publiés par le dépôt du projet, aux noms attendus.
                if (!ProjectUrls.Any(p => url.StartsWith(p + "/releases/download/", StringComparison.Ordinal)) || name != Path.GetFileName(name))
                {
                    continue;
                }

                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sumsUrl = url;
                }
                else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)
                         && (name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
                             || (name.StartsWith(LegacyPackagePrefix, StringComparison.OrdinalIgnoreCase) && packageName is null)))
                {
                    // L'archive au nouveau nom passe avant celle à l'ancien nom.
                    packageName = name;
                    packageUrl = url;
                }
            }
        }

        DateTimeOffset? published = root.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
        return new UpdateInfo(Normalize(version), tag, page, packageName, packageUrl, sumsUrl, published);
    }

    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    /// <summary>Somme attendue d'un fichier dans SHA256SUMS.txt (lignes « somme  nom » ou « somme *nom »).</summary>
    public static string? ExpectedSha256(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || !line[..64].All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64]))
            {
                continue;
            }

            var name = line[64..].TrimStart();
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (name == fileName)
            {
                return line[..64].ToLowerInvariant();
            }
        }

        return null;
    }

    public static async Task<UpdateInfo> CheckAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Télécharge l'archive de la version dans <paramref name="folder"/> et vérifie sa somme SHA-256. En cas d'écart ou
    /// d'erreur, rien n'est gardé.
    /// </summary>
    /// <returns>Chemin de l'archive vérifiée.</returns>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.PackageName is null || info.PackageUrl is null || info.SumsUrl is null)
        {
            throw new InvalidOperationException(CoreStrings.UpdateNoPackage);
        }

        var sums = await http.GetStringAsync(info.SumsUrl, ct).ConfigureAwait(false);
        var expected = ExpectedSha256(sums, info.PackageName) ?? throw new InvalidDataException(CoreStrings.UpdateNoChecksum);
        var target = Path.Combine(folder, WindowsFileName.Sanitize(info.PackageName));
        var partial = target + ".partial";
        try
        {
            using (var response = await http.GetAsync(info.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxPackageBytes)
                    {
                        throw new InvalidDataException(CoreStrings.UpdateTooLarge);
                    }

                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (total is > 0)
                    {
                        progress?.Report((double)done / total.Value);
                    }
                }

                var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (actual != expected)
                {
                    throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, CoreStrings.UpdateChecksumMismatch, expected, actual));
                }
            }

            // Archive venue d'Internet (Mark-of-the-Web, gardée par le renommage, reprise par l'extraction de Windows) :
            // SmartScreen contrôle ensuite l'exécutable extrait.
            MarkOfTheWeb.Apply(partial, Uri.TryCreate(info.PackageUrl, UriKind.Absolute, out var url) ? url : null);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}
