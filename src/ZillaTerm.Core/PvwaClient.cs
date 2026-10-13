using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Localization;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core;

/// <summary>
/// Client minimal de l'API REST du PVWA CyberArk (v10 et ultérieures).
/// Un client correspond à une session : <see cref="LogonAsync"/>, appels, puis <see cref="LogoffAsync"/>.
/// </summary>
public sealed class PvwaClient : IDisposable
{
    /// <summary>Taille de page maximale acceptée par <c>GET API/Accounts</c>.</summary>
    public const int PageSize = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private string? _token;

    public PvwaClient(Uri baseUri, HttpMessageHandler handler)
    {
        BaseUri = baseUri;
        _http = new HttpClient(new DebugLogHandler(handler)) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Uri BaseUri { get; }

    public bool IsAuthenticated => _token is not null;

    /// <summary>
    /// Crée un client pour l'URL saisie par l'utilisateur. L'authentification Windows
    /// réutilise la session Windows courante (Kerberos/NTLM).
    /// </summary>
    public static PvwaClient Create(string pvwaUrl, AuthMethod method) => new(NormalizeBaseUri(pvwaUrl), CreateHandler(method));

    internal static HttpClientHandler CreateHandler(AuthMethod method) =>
        new()
        {
            UseDefaultCredentials = method == AuthMethod.Windows,
            UseCookies = true,
            // Une redirection n'est jamais suivie : en 307/308, le mot de passe de la connexion serait renvoyé à
            // l'adresse indiquée, et le jeton de session pourrait l'être aussi. Elle est signalée (CreateErrorAsync).
            AllowAutoRedirect = false,
            // Certificat révoqué refusé ; serveur de révocation injoignable : connexion acceptée (voir TlsRevocation).
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                TlsRevocation.Validate(request.RequestUri?.Host, certificate, chain, errors),
        };

    /// <summary>
    /// Transforme « pvwa.corp.local », « https://pvwa.corp.local/PasswordVault/v10/logon », etc.
    /// en « https://pvwa.corp.local/PasswordVault/ ».
    /// </summary>
    public static Uri NormalizeBaseUri(string input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ArgumentException(CoreStrings.PvwaAddressRequired);
        }

        // Rien qui puisse faire passer une adresse pour une autre : espaces, caractères invisibles, « \ » ou lettres
        // hors ASCII (d'apparence identique, d'un autre alphabet) ; un nom international s'écrit sous sa forme « xn-- ».
        if (text.Any(c => c is < '!' or > '~' or '\\'))
        {
            throw new ArgumentException(CoreStrings.PvwaAddressSuspicious);
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, CoreStrings.PvwaAddressInvalid, input));
        }

        // « https://pvwa.corp.local:443@autre/ » joint « autre » : ce qui précède « @ » n'est qu'un nom d'utilisateur.
        if (uri.UserInfo.Length > 0)
        {
            throw new ArgumentException(CoreStrings.PvwaAddressSuspicious);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(CoreStrings.PvwaHttpsRequired);
        }

        var path = uri.AbsolutePath;
        int idx = path.IndexOf("/PasswordVault", StringComparison.OrdinalIgnoreCase);
        path = idx >= 0 ? path[..(idx + "/PasswordVault".Length)] : "/PasswordVault";

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
    }

    /// <summary>
    /// Ouvre une session. Pour une réponse à un challenge RADIUS, rappeler cette méthode sur le même
    /// client avec la réponse comme mot de passe (les cookies de la première tentative sont conservés).
    /// </summary>
    /// <exception cref="PvwaException">Échec d'authentification ; voir <see cref="PvwaException.IsRadiusChallenge"/>.</exception>
    public Task LogonAsync(AuthMethod method, string? userName, string? password, CancellationToken ct = default) =>
        LogonAsync(method, userName, password.AsMemory(), ct);

    /// <inheritdoc cref="LogonAsync(AuthMethod, string?, string?, CancellationToken)"/>
    /// <param name="password">
    /// Mot de passe lu sans chaîne .NET (champ masqué) ; le corps de la requête qui le contient est effacé après l'envoi.
    /// </param>
    public async Task LogonAsync(AuthMethod method, string? userName, ReadOnlyMemory<char> password, CancellationToken ct = default)
    {
        // Corps écrit à l'avance pour envoyer un Content-Length : JsonContent passe en « chunked », que certains load
        // balancers / WAF placés devant le PVWA rejettent.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (method != AuthMethod.Windows)
            {
                writer.WriteString("username", userName);
                writer.WriteString("password", password.Span);
            }

            writer.WriteBoolean("concurrentSession", true);
            writer.WriteEndObject();
        }

        var body = buffer.WrittenSpan.ToArray();
        // Clear() remet à zéro les octets écrits.
        buffer.Clear();
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"API/auth/{method}/Logon") { Content = content };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _token = ParseToken(text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>
    /// Charge tous les comptes visibles par l'utilisateur connecté (safes sur lesquels il a le droit « List »),
    /// page par page.
    /// </summary>
    /// <param name="progress">Reçoit (comptes chargés, total annoncé par le PVWA) après chaque page.</param>
    /// <exception cref="PvwaException">Refus du PVWA, ou liste anormalement longue (voir <see cref="MaxPages"/>).</exception>
    public Task<List<PvwaAccount>> GetAccountsAsync(IProgress<(int Loaded, int Total)>? progress = null, CancellationToken ct = default) =>
        GetAllPagesAsync<PvwaAccount>("API/Accounts", a => a.Id, progress, ct);

    /// <summary>
    /// Membres d'un safe (utilisateurs et groupes) avec leurs droits, page par page. Le PVWA ne les donne qu'à un
    /// utilisateur qui a le droit « View Safe Members » sur ce safe : sinon <see cref="PvwaException"/> (403 en général).
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit manquant, safe introuvable, API absente de cette version...).</exception>
    public async Task<List<SafeMember>> GetSafeMembersAsync(string safeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeName);
        return await GetAllPagesAsync<SafeMember>($"API/Safes/{Uri.EscapeDataString(safeName)}/Members", m => m.MemberName, null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pages lues au plus pour une liste (1 000 pages de 1 000 éléments) : au-delà, le PVWA (compromis, ou en panne) ne
    /// s'arrêterait jamais d'en annoncer, et la mémoire finirait par manquer.
    /// </summary>
    internal const int MaxPages = 1000;

    /// <summary>
    /// Toutes les pages d'une liste du PVWA (<paramref name="path"/> sans paramètres). La liste s'arrête au total annoncé
    /// (<c>count</c>), ou, sans total, quand il n'y a plus de page suivante (<c>nextLink</c>) ; et aussi sur une page vide
    /// ou qui n'apporte aucun élément nouveau (<paramref name="key"/> : identifiant ; même page renvoyée en boucle).
    /// </summary>
    /// <exception cref="PvwaException">Plus de <see cref="MaxPages"/> pages, ou d'éléments que ces pages n'en contiennent.</exception>
    private async Task<List<T>> GetAllPagesAsync<T>(string path, Func<T, string?> key, IProgress<(int Loaded, int Total)>? progress,
        CancellationToken ct)
    {
        var all = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int pages = 0; ; pages++)
        {
            if (pages == MaxPages || all.Count > MaxPages * PageSize)
            {
                DebugLog.Write("pvwa", $"{path} : plus de {MaxPages} pages ({all.Count} éléments), lecture arrêtée.");
                throw new PvwaException(HttpStatusCode.OK, null, string.Format(CultureInfo.CurrentCulture, CoreStrings.PvwaTooManyPages, MaxPages));
            }

            var page = await GetAsync<Page<T>>($"{path}?offset={all.Count}&limit={PageSize}", ct).ConfigureAwait(false);
            var items = page.Value ?? [];
            // Count (et non Any) : chaque identifiant de la page est noté.
            if (items.Count > 0 && items.Count(i => seen.Add((i is null ? null : key(i)) ?? "")) == 0)
            {
                DebugLog.Write("pvwa", $"{path} : page à l'offset {all.Count} sans élément nouveau, lecture arrêtée.");
                return all;
            }

            all.AddRange(items);
            progress?.Report((all.Count, Math.Max(page.Count, all.Count)));

            bool more = page.Count > 0 ? all.Count < page.Count : page.NextLink is not null;
            if (items.Count == 0 || !more)
            {
                return all;
            }
        }
    }

    /// <summary>Ajoute un membre au safe (droit « Manage Safe Members » sur le safe).</summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit manquant, membre introuvable ou déjà présent...).</exception>
    public Task AddSafeMemberAsync(string safeName, SafeMemberChange member, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeName);
        return SendJsonAsync(HttpMethod.Post, $"API/Safes/{Uri.EscapeDataString(safeName)}/Members", member.ToAddBody(), ct);
    }

    /// <summary>Remplace les droits (et la fin d'appartenance) d'un membre du safe (droit « Manage Safe Members »).</summary>
    /// <exception cref="PvwaException">Refus du PVWA.</exception>
    public Task UpdateSafeMemberAsync(string safeName, SafeMemberChange member, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeName);
        return SendJsonAsync(HttpMethod.Put,
            $"API/Safes/{Uri.EscapeDataString(safeName)}/Members/{Uri.EscapeDataString(member.MemberName.Trim())}", member.ToUpdateBody(), ct);
    }

    /// <summary>Retire un membre du safe (droit « Manage Safe Members »).</summary>
    /// <exception cref="PvwaException">Refus du PVWA.</exception>
    public async Task RemoveSafeMemberAsync(string safeName, string memberName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete,
            $"API/Safes/{Uri.EscapeDataString(safeName)}/Members/{Uri.EscapeDataString(memberName.Trim())}");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    private async Task SendJsonAsync(HttpMethod method, string relativeUri, object body, CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(method, relativeUri);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Crée un compte dans un safe (droit « Add accounts » sur le safe, et en général « Update account content » pour
    /// fournir le mot de passe) et renvoie le compte créé. Le corps de la requête, qui contient le mot de passe, est
    /// effacé de la mémoire après l'envoi ; il n'est jamais écrit dans le journal de débogage.
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit manquant, plateforme inconnue, compte existant...).</exception>
    public async Task<PvwaAccount> AddAccountAsync(NewAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var body = account.ToJson();
        try
        {
            using var request = CreateAuthenticatedRequest(HttpMethod.Post, "API/Accounts");
            // Longueur connue : envoyé avec Content-Length (pas en « chunked », rejeté par certains load balancers).
            request.Content = new ReadOnlyMemoryContent(body.WrittenMemory);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
            }

            return await response.Content.ReadFromJsonAsync<PvwaAccount>(JsonOptions, ct).ConfigureAwait(false)
                ?? throw new PvwaException(response.StatusCode, null, CoreStrings.PvwaEmptyResponse);
        }
        finally
        {
            body.Clear();
        }
    }

    /// <summary>
    /// Modifie un compte (droit « Update account properties » sur le safe) : <paramref name="operations"/> vient de
    /// <see cref="AccountChanges.Diff"/>. Renvoie le compte tel que le PVWA l'a enregistré.
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit manquant, valeur refusée...).</exception>
    public async Task<PvwaAccount> UpdateAccountAsync(string accountId, IReadOnlyList<PatchOperation> operations, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var body = operations.Select(o => o.Value is null
            ? new Dictionary<string, object> { ["op"] = o.Op, ["path"] = o.Path }
            : new Dictionary<string, object> { ["op"] = o.Op, ["path"] = o.Path, ["value"] = o.Value });
        using var request = CreateAuthenticatedRequest(HttpMethod.Patch, $"API/Accounts/{Uri.EscapeDataString(accountId)}");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<PvwaAccount>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new PvwaException(response.StatusCode, null, CoreStrings.PvwaEmptyResponse);
    }

    /// <summary>Supprime un compte du coffre (droit « Delete accounts » sur le safe).</summary>
    /// <exception cref="PvwaException">Refus du PVWA.</exception>
    public async Task DeleteAccountAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"API/Accounts/{Uri.EscapeDataString(accountId)}");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Demande au CPM de vérifier, changer ou réconcilier le mot de passe (droit « Initiate CPM account management
    /// operations »). Le PVWA marque le compte ; le CPM le traite ensuite, le résultat n'est donc pas immédiat.
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA.</exception>
    public async Task RunCpmActionAsync(string accountId, CpmAction action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, $"API/Accounts/{Uri.EscapeDataString(accountId)}/{action}");
        if (action == CpmAction.Change)
        {
            // Comptes groupés (mot de passe partagé) : tout le groupe change, sinon le PVWA refuse.
            request.Content = new StringContent("""{"ChangeEntireGroup":true}""", Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Récupère le mot de passe du compte (droit « Retrieve accounts » ; motif ou ticket si la plateforme l'exige). Il
    /// est rendu dans un tableau que l'appelant efface après usage : la réponse est lue dans un tampon effacé ensuite,
    /// sans passer par une chaîne.
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit, motif, double validation...).</exception>
    public async Task<char[]> RetrievePasswordAsync(string accountId, RetrieveOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, $"API/Accounts/{Uri.EscapeDataString(accountId)}/Password/Retrieve");
        request.Content = new StringContent(JsonSerializer.Serialize(options.ToRequestBody()), Encoding.UTF8, "application/json");
        var requested = new Uri(BaseUri, request.RequestUri!);
        // En-têtes seulement : le corps est lu ensuite dans un tampon à nous, que l'on peut effacer.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        bool json = EnsureSecretResponse(requested, response);
        var bytes = await ReadSecretBodyAsync(response.Content, ct).ConfigureAwait(false);
        try
        {
            return DecodeSecret(bytes.Span, json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.Span);
        }
    }

    /// <summary>
    /// Vérifie que la réponse vient bien du PVWA pour cette demande : pas de redirection suivie (vers une page de
    /// connexion SSO…), pas de réponse vide, et un type JSON ou texte (pas une page HTML de maintenance). Sans quoi la
    /// page serait copiée à la place du mot de passe. Vrai si la réponse est du JSON.
    /// </summary>
    private static bool EnsureSecretResponse(Uri requested, HttpResponseMessage response)
    {
        var media = response.Content.Headers.ContentType?.MediaType;
        bool json = string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
        bool redirected = response.RequestMessage is { } final && (final.Method != HttpMethod.Post || final.RequestUri != requested);
        if (redirected || response.StatusCode == HttpStatusCode.NoContent
            || (media is not null && !json && !string.Equals(media, "text/plain", StringComparison.OrdinalIgnoreCase)))
        {
            var what = redirected ? response.RequestMessage!.RequestUri?.GetLeftPart(UriPartial.Path) : media ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            throw new PvwaException(response.StatusCode, null, string.Format(CultureInfo.CurrentCulture, CoreStrings.PvwaUnexpectedSecretResponse, what));
        }

        return json;
    }

    /// <summary>Taille maximale acceptée pour la réponse d'une récupération de mot de passe.</summary>
    private const int MaxSecretResponse = 64 * 1024;

    private static async Task<Memory<byte>> ReadSecretBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Tampon fixe (jamais agrandi, donc jamais copié ailleurs) ; une réponse plus longue n'est pas un mot de passe.
        var buffer = new byte[MaxSecretResponse];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.AsMemory(0, length);
            }

            length += read;
        }

        CryptographicOperations.ZeroMemory(buffer);
        throw new PvwaException(HttpStatusCode.OK, null, CoreStrings.PvwaSecretTooLong);
    }

    /// <summary>
    /// Mot de passe renvoyé par le PVWA : une chaîne JSON (« "secret" », API v10 et ultérieures) ou, sur d'anciennes
    /// versions, le texte brut. Décodé directement dans un tableau de caractères. Réponse vide, ou JSON qui n'est pas
    /// une chaîne (<paramref name="json"/>) : refusée.
    /// </summary>
    internal static char[] DecodeSecret(ReadOnlySpan<byte> body, bool json = false)
    {
        body = body.Trim("\r\n\t "u8);
        if (body.Length == 0)
        {
            throw new PvwaException(HttpStatusCode.OK, null, CoreStrings.PvwaEmptyResponse);
        }

        if (json && body[0] != (byte)'"')
        {
            throw new PvwaException(HttpStatusCode.OK, null,
                string.Format(CultureInfo.CurrentCulture, CoreStrings.PvwaUnexpectedSecretResponse, "application/json"));
        }

        if (body[0] == (byte)'"')
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new PvwaException(HttpStatusCode.OK, null, CoreStrings.PvwaEmptyResponse);
            }

            // Une fois décodée, la chaîne ne compte jamais plus de caractères que d'octets.
            var buffer = new char[reader.ValueSpan.Length];
            int count = reader.CopyString(buffer);
            var secret = buffer.AsSpan(0, count).ToArray();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
            return secret;
        }

        var chars = new char[Encoding.UTF8.GetCharCount(body)];
        Encoding.UTF8.GetChars(body, chars);
        return chars;
    }

    /// <summary>
    /// Demande au PVWA une connexion PSM pour le compte (équivalent du bouton « Connect » du PVWA)
    /// et renvoie le fichier RDP à ouvrir avec <c>mstsc.exe</c>.
    /// </summary>
    /// <remarks>
    /// Disques du poste demandés (<see cref="PsmConnectOptions.MapLocalDrives"/>) et refusés par le PVWA comme paramètre
    /// (composant qui ne le propose pas) : nouvelle demande, sans eux.
    /// </remarks>
    /// <exception cref="PvwaException">Refus du PVWA (composant inconnu, motif exigé, accès non autorisé...).</exception>
    public async Task<byte[]> PsmConnectAsync(string accountId, PsmConnectOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionComponent);
        try
        {
            return await RequestPsmConnectAsync(accountId, options, ct).ConfigureAwait(false);
        }
        catch (PvwaException e) when (options.MapLocalDrives && MayRefuseLocalDrives(e))
        {
            DebugLog.Write("pvwa", $"PSMConnect refusé avec les disques du poste ({e.ErrorCode}) : nouvelle demande sans eux.");
            return await RequestPsmConnectAsync(accountId, options.WithoutLocalDrives(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refus qui peut venir du paramètre des disques : le texte des refus varie selon la version et la langue du PVWA,
    /// donc toute demande refusée (400, 500), sauf les refus connus pour une autre cause (session, droits, composant
    /// inconnu, motif ou ticket exigé). Au pire, une demande de plus, refusée pour la même raison.
    /// </summary>
    private static bool MayRefuseLocalDrives(PvwaException e)
    {
        if (e.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError) || e.IsUnknownComponent)
        {
            return false;
        }

        var text = $"{e.ServerMessage} {e.Message}";
        return !text.Contains("reason", StringComparison.OrdinalIgnoreCase) && !text.Contains("ticket", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> RequestPsmConnectAsync(string accountId, PsmConnectOptions options, CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, $"API/Accounts/{Uri.EscapeDataString(accountId)}/PSMConnect");
        // « octet-stream » : le PVWA renvoie le fichier RDP ; « json » renverrait les données PSM Gateway (HTML5).
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/octet-stream");
        request.Content = new StringContent(JsonSerializer.Serialize(options.ToRequestBody()), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return RdpFile.FromPsmConnectResponse(bytes, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Composants de connexion PSM activés de la plateforme <paramref name="platformId"/> (l'ID de plateforme des comptes,
    /// par ex. WinDomain), dans leur ordre : « Get platform details », lisible par un utilisateur ordinaire. Le format de
    /// la réponse varie selon la version du PVWA (voir <see cref="PlatformComponents"/>) ; liste vide si elle n'en
    /// contient pas.
    /// </summary>
    /// <exception cref="PvwaException">Refus du PVWA (droit manquant, plateforme inconnue, API absente de cette version).</exception>
    /// <exception cref="JsonException">Réponse qui n'est pas du JSON.</exception>
    public async Task<IReadOnlyList<string>> GetPlatformConnectionComponentsAsync(string platformId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"API/Platforms/{Uri.EscapeDataString(platformId.Trim())}/");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        return PlatformComponents.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Demande une clé SSH temporaire « MFA caching » pour s'authentifier au PSMP sans ressaisir
    /// mot de passe et MFA. Renvoie null si la fonctionnalité n'est pas activée sur le PVWA.
    /// </summary>
    public async Task<MfaSshKey?> GetMfaCachingSshKeyAsync(CancellationToken ct = default)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "API/Users/Secret/SSHKeys/Cache");
        request.Content = new StringContent("""{"formats":["OpenSSH","PEM","PPK"]}""", Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return MfaSshKey.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Requête légère (un compte au plus) qui garde la session PVWA active : le délai d'inactivité du PVWA repart
    /// de zéro. <see cref="PvwaException.IsUnauthorized"/> si la session a déjà expiré.
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken ct = default)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, "API/Accounts?offset=0&limit=1");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retire la clé « MFA caching » de l'utilisateur sur le PVWA : elle ne permet plus de se connecter au PSMP, au lieu
    /// de rester valable jusqu'à son expiration. Sans effet si aucune session n'est ouverte ; un refus du PVWA (fonction
    /// absente de sa version) est ignoré.
    /// </summary>
    public async Task RevokeMfaCachingSshKeyAsync(CancellationToken ct = default)
    {
        if (_token is null)
        {
            return;
        }

        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, "API/Users/Secret/SSHKeys/Cache");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        DebugLog.Write("pvwa", $"Clé MFA caching retirée : HTTP {(int)response.StatusCode}");
    }

    /// <summary>Ferme la session côté PVWA. Sans effet si aucune session n'est ouverte.</summary>
    public async Task LogoffAsync(CancellationToken ct = default)
    {
        if (_token is null)
        {
            return;
        }

        using var request = CreateAuthenticatedRequest(HttpMethod.Post, "API/Auth/Logoff");
        _token = null;
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    internal static string ParseToken(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? token = root.ValueKind switch
            {
                // API v10+ : le jeton est renvoyé comme une simple chaîne JSON.
                JsonValueKind.String => root.GetString(),
                // Ancienne API v9 : { "CyberArkLogonResult": "..." }.
                JsonValueKind.Object when root.TryGetProperty("CyberArkLogonResult", out var r) => r.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }
        catch (JsonException)
        {
        }

        throw new PvwaException(HttpStatusCode.OK, null, CoreStrings.PvwaNoToken);
    }

    private async Task<T> GetAsync<T>(string relativeUri, CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, relativeUri);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new PvwaException(response.StatusCode, null, CoreStrings.PvwaEmptyResponse);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string relativeUri)
    {
        var token = _token ?? throw new InvalidOperationException(CoreStrings.PvwaNoSession);
        var request = new HttpRequestMessage(method, relativeUri);
        // Le PVWA attend le jeton brut, sans schéma (« Bearer » etc.).
        request.Headers.TryAddWithoutValidation("Authorization", token);
        return request;
    }

    private async Task<PvwaException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            // Adresse indiquée sans ses paramètres : c'est elle qu'il faudra peut-être saisir comme adresse du PVWA.
            var location = response.Headers.Location;
            if (location is { IsAbsoluteUri: false })
            {
                location = new Uri(response.RequestMessage?.RequestUri is { IsAbsoluteUri: true } from ? from : BaseUri, location);
            }

            var where = location is { IsAbsoluteUri: true } ? location.GetLeftPart(UriPartial.Path) : "?";
            DebugLog.Write("pvwa", $"Redirection non suivie ({(int)response.StatusCode}) vers {where}");
            return new PvwaException(response.StatusCode, null,
                string.Format(CultureInfo.CurrentCulture, CoreStrings.PvwaRedirectNotFollowed, (int)response.StatusCode, where));
        }

        string? code = null;
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.NameEquals("ErrorCode") || p.NameEquals("errorCode"))
                    {
                        code = p.Value.GetString();
                    }
                    else if (p.NameEquals("ErrorMessage") || p.NameEquals("errorMessage"))
                    {
                        message = p.Value.GetString();
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // Corps non JSON (page d'erreur IIS, proxy...) : on se contente du statut HTTP.
        }

        var text = message ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        if (code is not null)
        {
            text = $"{text} ({code})";
        }

        DebugLog.Write("pvwa", $"Erreur renvoyée par le PVWA : {text}");
        return new PvwaException(response.StatusCode, code, text, message);
    }

    /// <summary>Page d'une liste du PVWA (<c>value</c>, <c>count</c> = total, <c>nextLink</c>).</summary>
    private sealed class Page<T>
    {
        public List<T>? Value { get; set; }

        public int Count { get; set; }

        public string? NextLink { get; set; }
    }
}
