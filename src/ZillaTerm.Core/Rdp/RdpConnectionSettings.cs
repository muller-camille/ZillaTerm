using System.Globalization;
using System.Text;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Réglages d'une session Bureau à distance ouverte dans un onglet de l'application : connexion directe (accès
/// d'urgence), ou session PSM lue dans le fichier .rdp du PVWA (<see cref="FromRdpFile"/>), en bureau ou en
/// application distante (RemoteApp). Le mot de passe n'en fait pas partie.
/// </summary>
public sealed record RdpConnectionSettings
{
    public const int DefaultPort = 3389;

    // Bits de AdvancedSettings.PerformanceFlags (TS_PERF_*) activés par les clés du fichier .rdp.
    private static readonly (string Key, int Flag)[] PerformanceKeys =
    [
        ("disable wallpaper", 0x01),
        ("disable full window drag", 0x02),
        ("disable menu anims", 0x04),
        ("disable themes", 0x08),
        ("disable cursor setting", 0x40),
        ("allow font smoothing", 0x80),
        ("allow desktop composition", 0x100),
    ];

    /// <summary>Nom ou adresse du serveur (ou du PSM).</summary>
    public required string Server { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>Utilisateur, éventuellement « DOMAINE\nom » ou « nom@domaine ».</summary>
    public string UserName { get; init; } = "";

    public string Domain { get; init; } = "";

    /// <summary>Programme lancé à l'ouverture de session (« alternate shell » : lancement de la session PSM).</summary>
    public string StartProgram { get; init; } = "";

    public string WorkDir { get; init; } = "";

    /// <summary>0 : aucune vérification du serveur, 1 : connexion refusée si la vérification échoue, 2 : avertissement.</summary>
    public int AuthenticationLevel { get; init; } = 2;

    /// <summary>Authentification au niveau du réseau (NLA, CredSSP).</summary>
    public bool EnableCredSsp { get; init; } = true;

    public bool NegotiateSecurityLayer { get; init; } = true;

    public bool RedirectClipboard { get; init; } = true;

    public bool RedirectDrives { get; init; }

    public bool RedirectPrinters { get; init; }

    public bool RedirectPorts { get; init; }

    public bool RedirectSmartCards { get; init; }

    public bool RedirectPosDevices { get; init; }

    /// <summary>Son : 0 sur ce poste, 1 sur le serveur, 2 aucun.</summary>
    public int AudioMode { get; init; }

    public bool AudioCapture { get; init; }

    public int ColorDepth { get; init; } = 32;

    public string LoadBalanceInfo { get; init; } = "";

    public string GatewayHostname { get; init; } = "";

    public int GatewayUsageMethod { get; init; }

    public int GatewayCredsSource { get; init; }

    public int GatewayProfileUsageMethod { get; init; }

    /// <summary>Raccourcis Windows (Alt+Tab...) : 0 sur ce poste, 1 sur le serveur, 2 sur le serveur en plein écran seulement.</summary>
    public int KeyboardHookMode { get; init; } = 2;

    public bool AutoReconnect { get; init; } = true;

    public bool Compress { get; init; } = true;

    /// <summary>Cache d'images persistant : morceaux de l'écran distant gardés sur le disque de ce poste.</summary>
    public bool BitmapPersistence { get; init; } = true;

    /// <summary>Effets désactivés ou activés (bits TS_PERF_*).</summary>
    public int PerformanceFlags { get; init; }

    /// <summary>Session d'administration (« console »).</summary>
    public bool ConnectToAdministerServer { get; init; }

    /// <summary>Mise à l'échelle de l'image au lieu d'adapter la résolution du bureau distant.</summary>
    public bool SmartSizing { get; init; }

    /// <summary>
    /// Vrai si le fichier demande une application distante (RemoteApp) : pas de bureau, seulement les fenêtres de
    /// l'application (avec mstsc, sur le bureau de ce poste ; avec FreeRDP dans ZillaTerm, dans l'onglet).
    /// </summary>
    public bool IsRemoteApp { get; init; }

    /// <summary>Application distante lancée (« ||PSMInitSession » pour une session PSM).</summary>
    public string RemoteApplicationProgram { get; init; } = "";

    public string RemoteApplicationName { get; init; } = "";

    /// <summary>Arguments de l'application distante (jeton de la session PSM) : jamais dans un journal.</summary>
    public string RemoteApplicationCmdLine { get; init; } = "";

    /// <summary>Vrai si la connexion passe par une passerelle Bureau à distance (toujours, ou si le serveur est injoignable).</summary>
    public bool UsesGateway => GatewayHostname.Trim().Length > 0 && GatewayUsageMethod is 1 or 2;

    /// <summary>
    /// Réglages d'une connexion directe (accès d'urgence) : authentification réseau, alerte si le serveur n'est pas
    /// reconnu, et pas de cache d'images sur disque (l'écran d'une session d'administration n'y reste pas).
    /// </summary>
    public static RdpConnectionSettings Direct(string server, int port, string userName) => new()
    {
        Server = server,
        Port = port is > 0 and <= 65535 ? port : DefaultPort,
        UserName = userName,
        BitmapPersistence = false,
    };

    /// <summary>
    /// Lit un fichier .rdp (celui du PVWA pour une session PSM). Les redirections absentes du fichier restent
    /// désactivées (sauf le presse-papiers, comme pour mstsc) ; une liste de lecteurs précise n'est pas reprise, seul
    /// « * » (tous les lecteurs) l'est. Le mode d'affichage du fichier (plein écran, taille, écrans) n'est pas repris :
    /// la session prend la taille de l'onglet.
    /// </summary>
    /// <exception cref="FormatException">Fichier sans adresse de serveur.</exception>
    public static RdpConnectionSettings FromRdpFile(byte[] content)
    {
        var values = ReadFile(content);
        string Text(string key, string fallback = "") => values.TryGetValue(key, out var v) ? v : fallback;
        int Int(string key, int fallback) =>
            values.TryGetValue(key, out var v) && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        bool Bool(string key, bool fallback) => Int(key, fallback ? 1 : 0) != 0;

        var (server, port) = SplitAddress(Text("full address").Trim());
        if (server.Length == 0)
        {
            throw new FormatException("full address");
        }

        int performance = PerformanceKeys.Where(k => Bool(k.Key, false)).Sum(k => k.Flag);

        return new RdpConnectionSettings
        {
            Server = server,
            Port = port ?? (Int("server port", DefaultPort) is var p and > 0 and <= 65535 ? p : DefaultPort),
            UserName = Text("username"),
            Domain = Text("domain"),
            StartProgram = Text("alternate shell"),
            WorkDir = Text("shell working directory"),
            AuthenticationLevel = Math.Clamp(Int("authentication level", 2), 0, 3),
            EnableCredSsp = Bool("enablecredsspsupport", true),
            NegotiateSecurityLayer = Bool("negotiate security layer", true),
            RedirectClipboard = Bool("redirectclipboard", true),
            RedirectDrives = Text("drivestoredirect").Trim() == "*",
            RedirectPrinters = Bool("redirectprinters", false),
            RedirectPorts = Bool("redirectcomports", false),
            RedirectSmartCards = Bool("redirectsmartcards", false),
            RedirectPosDevices = Bool("redirectposdevices", false),
            AudioMode = Math.Clamp(Int("audiomode", 0), 0, 2),
            AudioCapture = Bool("audiocapturemode", false),
            ColorDepth = Int("session bpp", 32) is var bpp and (8 or 15 or 16 or 24 or 32) ? bpp : 32,
            LoadBalanceInfo = Text("loadbalanceinfo"),
            GatewayHostname = Text("gatewayhostname"),
            GatewayUsageMethod = Int("gatewayusagemethod", 0),
            GatewayCredsSource = Int("gatewaycredentialssource", 0),
            GatewayProfileUsageMethod = Int("gatewayprofileusagemethod", 0),
            KeyboardHookMode = Math.Clamp(Int("keyboardhook", 2), 0, 2),
            AutoReconnect = Bool("autoreconnection enabled", true),
            Compress = Bool("compression", true),
            BitmapPersistence = Bool("bitmapcachepersistenable", true),
            PerformanceFlags = performance,
            ConnectToAdministerServer = Bool("administrative session", false) || Bool("connect to console", false),
            SmartSizing = Bool("smart sizing", false),
            IsRemoteApp = Bool("remoteapplicationmode", false),
            RemoteApplicationProgram = Text("remoteapplicationprogram"),
            RemoteApplicationName = Text("remoteapplicationname"),
            RemoteApplicationCmdLine = Text("remoteapplicationcmdline"),
        };
    }

    /// <summary>
    /// Règles de ZillaTerm pour une session PSM dans l'onglet. Par défaut, résolution fixe, celle de l'onglet à la
    /// connexion, et image mise à l'échelle ensuite (<see cref="SmartSizing"/>) : le PSM ouvre la connexion vers la cible
    /// à la taille de sa propre session, qui ne doit plus changer. Pas de cache d'images sur le disque de ce poste (écran
    /// d'une session d'administration). Jamais de connexion sans vérification du serveur : le niveau d'authentification 1
    /// (refus) reste 1, les autres deviennent 2 (avertissement de Windows si le certificat présenté n'est ni approuvé
    /// par Windows ni celui que ZillaTerm a vérifié juste avant). Les redirections restent celles du fichier, jamais plus.
    /// </summary>
    /// <param name="dynamicResolution">
    /// Redimensionnement dynamique (expérimental) : la résolution du bureau distant suit la taille de l'onglet au lieu
    /// d'une image mise à l'échelle. La connexion du PSM vers la cible peut garder sa taille d'origine.
    /// </param>
    public RdpConnectionSettings ForPsmTab(bool dynamicResolution = false) => this with
    {
        SmartSizing = !dynamicResolution,
        BitmapPersistence = false,
        AuthenticationLevel = AuthenticationLevel == 1 ? 1 : 2,
    };

    /// <summary>Réglages d'un fichier .rdp (voir <see cref="Parse"/>), quel que soit son encodage.</summary>
    public static IReadOnlyDictionary<string, string> ReadFile(byte[] content) => Parse(Decode(content));

    /// <summary>Lignes « nom:type:valeur » d'un fichier .rdp ; noms en minuscules, la dernière occurrence l'emporte.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int first = line.IndexOf(':');
            int second = first < 0 ? -1 : line.IndexOf(':', first + 1);
            if (second != first + 2)
            {
                continue;
            }

            var name = line[..first].Trim().ToLowerInvariant();
            if (name.Length > 0 && line[first + 1] is 's' or 'i' or 'b' or 'S' or 'I' or 'B')
            {
                values[name] = line[(second + 1)..];
            }
        }

        return values;
    }

    /// <summary>« hôte », « hôte:port », « [IPv6]:port » ou IPv6 seule.</summary>
    public static (string Host, int? Port) SplitAddress(string address)
    {
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']');
            if (end > 0)
            {
                var host = address[1..end];
                return (host, ParsePort(address[(end + 1)..].TrimStart(':')));
            }
        }

        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon)
        {
            return (address[..colon], ParsePort(address[(colon + 1)..]));
        }

        return (address, null);
    }

    private static int? ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static string Decode(byte[] content)
    {
        // Fichier du PVWA : en général UTF-16 avec BOM ; on accepte aussi l'UTF-8 et l'UTF-16 sans BOM.
        if (content.Length >= 2 && content[0] != 0 && content[1] == 0 && !HasBom(content))
        {
            return Encoding.Unicode.GetString(content);
        }

        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool HasBom(byte[] content) =>
        (content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)
        || (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF);
}
