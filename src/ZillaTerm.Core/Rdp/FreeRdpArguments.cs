using System.Globalization;
using System.Text;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Lancement du client FreeRDP : ligne de commande (rien de secret : fenêtre, taille, sécurité, redirections, empreintes
/// acceptées) et fichier de connexion écrit sur son entrée standard (« - » : serveur, identifiant, programme de
/// démarrage, application distante et ses arguments, qui portent le jeton PSM). Rien n'est écrit sur le disque.
/// </summary>
public sealed record FreeRdpLaunch(IReadOnlyList<string> Arguments, string ConnectionFile);

/// <summary>
/// Connexion décrite au client FreeRDP (fork muller-camille/FreeRDP, branche patches-3.32.1 : uniquement des
/// fonctions génériques de FreeRDP). Le serveur doit présenter un certificat (TLS) ou une clé (ancienne sécurité RDP)
/// dont l'empreinte a été acceptée par ZillaTerm avant le lancement (« /cert:deny,fingerprint:… ») : FreeRDP refuse
/// tout autre serveur, sans rien demander.
/// </summary>
public static class FreeRdpArguments
{
    /// <summary>Plus petit et plus grand côté du bureau distant (largeur paire), comme le contrôle de Windows.</summary>
    public const int MinSize = 200;

    public const int MaxSize = 8192;

    // Effets (TS_PERF_*) et options de FreeRDP : un bit levé désactive l'effet (les trois premiers et le thème) ou
    // l'active (lissage des polices, composition).
    private static readonly (int Flag, string Option, bool SetDisables)[] Effects =
    [
        (0x01, "wallpaper", true),
        (0x02, "window-drag", true),
        (0x04, "menu-anims", true),
        (0x08, "themes", true),
        (0x80, "fonts", false),
        (0x100, "aero", false),
    ];

    /// <summary>
    /// Vrai si FreeRDP peut ouvrir cette connexion dans ZillaTerm. Une passerelle Bureau à distance n'est pas prise en
    /// charge : la connexion reste à mstsc.
    /// </summary>
    public static bool Supports(RdpConnectionSettings settings) => !settings.UsesGateway;

    /// <param name="parentWindow">Fenêtre de l'onglet où FreeRDP crée la sienne.</param>
    /// <param name="pixels">
    /// Taille de l'onglet en pixels : résolution du bureau distant, mise à l'échelle ensuite
    /// (<see cref="RdpConnectionSettings.SmartSizing"/>) ou adaptée à chaque redimensionnement de l'onglet.
    /// </param>
    /// <param name="password">Mot de passe d'une connexion directe (tests) ; null pour une session PSM (aucun n'est demandé).</param>
    /// <param name="verbose">Journal détaillé de FreeRDP (journal de débogage de ZillaTerm activé).</param>
    /// <param name="fingerprints">
    /// Empreintes SHA-256 (hexadécimal) des certificats et clés acceptés pour ce serveur : FreeRDP refuse les autres.
    /// </param>
    /// <param name="legacySecurity">
    /// Ancienne sécurité RDP permise si le serveur refuse TLS (PSM seulement) : sa clé doit alors être l'une des
    /// empreintes acceptées ; chiffrement 128 bits ou FIPS seulement.
    /// </param>
    /// <param name="configPath">
    /// Dossier de configuration de FreeRDP (certificats et clés qu'il connaîtrait) : vide, propre à ZillaTerm ; null pour
    /// le dossier par défaut.
    /// </param>
    /// <exception cref="ArgumentException">Valeur avec un retour à la ligne ou un caractère nul.</exception>
    public static FreeRdpLaunch Build(RdpConnectionSettings settings, long parentWindow, (int Width, int Height) pixels,
        string? password, bool verbose, IReadOnlyCollection<string> fingerprints, bool legacySecurity = false, string? configPath = null)
    {
        if (!Supports(settings))
        {
            throw new ArgumentException("Passerelle Bureau à distance non prise en charge", nameof(settings));
        }

        var (width, height) = DesktopSize(pixels);
        var arguments = new List<string>
        {
            // Fichier de connexion sur l'entrée standard.
            "-",
            "/parent-window:" + parentWindow.ToString(CultureInfo.InvariantCulture),
            $"/size:{width}x{height}",
            // Image mise à l'échelle, ou résolution qui suit la taille de la fenêtre : l'un ou l'autre pour FreeRDP.
            settings.SmartSizing ? "/smart-sizing" : "/dynamic-resolution",
            "/bpp:" + settings.ColorDepth.ToString(CultureInfo.InvariantCulture),
            "-toggle-fullscreen",
            "/log-level:" + (verbose ? "INFO" : "WARN"),
            // Seuls les certificats et clés acceptés par ZillaTerm : jamais de question de FreeRDP.
            "/cert:" + string.Join(",", fingerprints.Select(f => "fingerprint:sha256:" + Fingerprint(f)).Prepend("deny")),
            // Identifiants demandés, s'il en manquait, sur l'entrée standard déjà fermée : jamais de fenêtre de Windows.
            "/from-stdin",
        };

        // Reconnexion automatique (connexion perdue, tentatives, session reprise) : toujours lue, l'onglet l'affiche (voir
        // FreeRdpReconnection).
        // Avec le journal de débogage, en plus : échanges des canaux des applications distantes (RemoteApp), du
        // presse-papiers et de l'affichage (tailles demandées au serveur), canaux dynamiques ouverts par le serveur, et
        // messages du presse-papiers du client Windows (jamais son contenu) : session qui se ferme, copier-coller qui
        // n'aboutit pas, bureau qui ne suit pas la taille de l'onglet.
        arguments.Add("/log-filters:com.freerdp.client.common:INFO" + (verbose
            ? ",com.freerdp.channels.rail.*:DEBUG,com.freerdp.channels.cliprdr.*:DEBUG,"
              + "com.freerdp.channels.disp.*:DEBUG,com.freerdp.channels.drdynvc.*:DEBUG,com.freerdp.client.windows:DEBUG"
            : ""));

        // Réglages par leur nom, en une seule option : une option donnée deux fois ne garde que sa dernière valeur.
        var tune = new List<string> { "FreeRDP_UseRdpSecurityLayer:FALSE" };
        if (legacySecurity)
        {
            // ENCRYPTION_METHOD_128BIT | ENCRYPTION_METHOD_FIPS : ni 40, ni 56 bits.
            tune.Add("FreeRDP_RdpSecurity:TRUE");
            tune.Add("FreeRDP_EncryptionMethods:18");
        }
        else
        {
            tune.Add("FreeRDP_RdpSecurity:FALSE");
        }

        if (!settings.EnableCredSsp)
        {
            // Sans authentification réseau : ni NLA, ni NLA étendue (activée par défaut dans FreeRDP).
            tune.Add("FreeRDP_NlaSecurity:FALSE");
            tune.Add("FreeRDP_ExtSecurity:FALSE");
        }

        if (settings.IsRemoteApp)
        {
            tune.Add("FreeRDP_RemoteAppLanguageBarSupported:TRUE");
        }

        if (configPath is not null && IsPlainValue(configPath))
        {
            tune.Add("FreeRDP_ConfigPath:" + configPath);
        }

        arguments.Add("/tune:" + string.Join(",", tune));
        arguments.Add(settings.RedirectClipboard ? "+clipboard" : "-clipboard");
        if (settings.RedirectDrives)
        {
            arguments.Add("/drives");
        }

        if (settings.RedirectPrinters)
        {
            arguments.Add("/printer");
        }

        if (settings.RedirectSmartCards)
        {
            arguments.Add("/smartcard");
        }

        arguments.Add(settings.AudioMode switch
        {
            0 => "/sound",
            1 => "/audio-mode:1",
            _ => "/audio-mode:2",
        });
        if (settings.AudioCapture)
        {
            arguments.Add("/microphone");
        }

        arguments.Add(settings.AutoReconnect ? "+auto-reconnect" : "-auto-reconnect");
        arguments.Add(settings.Compress ? "+compression" : "-compression");
        foreach (var (flag, option, setDisables) in Effects)
        {
            bool set = (settings.PerformanceFlags & flag) != 0;
            // Application distante : ni fond d'écran ni déplacement des fenêtres avec leur contenu, comme /app.
            if (settings.IsRemoteApp && option is "wallpaper" or "window-drag")
            {
                set = true;
            }

            arguments.Add((set != setDisables ? "+" : "-") + option);
        }

        if (settings.ConnectToAdministerServer)
        {
            arguments.Add("/admin");
        }

        return new FreeRdpLaunch(arguments, ConnectionFile(settings, password));
    }

    /// <summary>
    /// Fichier de connexion (format .rdp, lu par FreeRDP sur son entrée standard) : chaque valeur va jusqu'à la fin de
    /// sa ligne, sans analyse (virgules, deux-points, guillemets). Toujours un mot de passe, vide pour une session PSM :
    /// FreeRDP ne demande rien et ne lit pas les identifiants enregistrés de Windows.
    /// </summary>
    private static string ConnectionFile(RdpConnectionSettings settings, string? password)
    {
        var file = new StringBuilder();
        void Line(string key, string value) => file.Append(key).Append(':').Append(Check(value)).Append("\r\n");

        Line("full address:s", settings.Server.Contains(':') ? $"[{settings.Server}]" : settings.Server);
        Line("server port:i", settings.Port.ToString(CultureInfo.InvariantCulture));
        var (user, domain) = SplitUser(settings.UserName, settings.Domain);
        if (user.Length > 0)
        {
            Line("username:s", user);
        }

        if (domain.Length > 0)
        {
            Line("domain:s", domain);
        }

        Line("password:s", password ?? "");
        if (settings.IsRemoteApp)
        {
            Line("remoteapplicationmode:i", "1");
            Line("remoteapplicationprogram:s", settings.RemoteApplicationProgram);
            if (settings.RemoteApplicationName.Length > 0)
            {
                Line("remoteapplicationname:s", settings.RemoteApplicationName);
            }

            if (settings.RemoteApplicationCmdLine.Length > 0)
            {
                Line("remoteapplicationcmdline:s", settings.RemoteApplicationCmdLine);
            }
        }
        else if (settings.StartProgram.Length > 0)
        {
            Line("alternate shell:s", settings.StartProgram);
        }

        // Dossier de démarrage du programme, ou de l'application distante.
        if (settings.WorkDir.Length > 0)
        {
            Line("shell working directory:s", settings.WorkDir);
        }

        if (settings.LoadBalanceInfo.Length > 0)
        {
            Line("loadbalanceinfo:s", settings.LoadBalanceInfo);
        }

        return file.ToString();
    }

    /// <summary>Taille du bureau distant : entre 200 et 8192 pixels, largeur paire.</summary>
    public static (int Width, int Height) DesktopSize((int Width, int Height) pixels) =>
        (Math.Clamp(pixels.Width, MinSize, MaxSize) & ~1, Math.Clamp(pixels.Height, MinSize, MaxSize));

    /// <summary>
    /// « DOMAINE\nom » comme mstsc : domaine et nom séparés. « nom@domaine » reste entier (UPN). Un domaine donné à part
    /// l'emporte.
    /// </summary>
    public static (string User, string Domain) SplitUser(string userName, string domain)
    {
        int slash = userName.IndexOf('\\');
        if (slash > 0 && slash < userName.Length - 1)
        {
            return (userName[(slash + 1)..], domain.Length > 0 ? domain : userName[..slash]);
        }

        return (userName, domain);
    }

    /// <summary>Empreinte SHA-256 en hexadécimal : 64 chiffres, rien d'autre (elle va dans une option de FreeRDP).</summary>
    private static string Fingerprint(string sha256) =>
        sha256.Length == 64 && sha256.All(char.IsAsciiHexDigit)
            ? sha256
            : throw new ArgumentException("Empreinte SHA-256 invalide", nameof(sha256));

    /// <summary>Valeur passée telle quelle dans une liste d'options de FreeRDP : ni virgule, ni guillemet, ni retour.</summary>
    private static bool IsPlainValue(string value) => value.AsSpan().IndexOfAny(",'\"\r\n\0") < 0 && value.Length > 0;

    private static string Check(string value) =>
        value.AsSpan().IndexOfAny('\r', '\n', '\0') < 0
            ? value
            : throw new ArgumentException("Retour à la ligne ou caractère nul dans un réglage de la connexion");
}
