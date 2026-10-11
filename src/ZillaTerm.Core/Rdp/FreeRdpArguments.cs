using System.Globalization;

namespace ZillaTerm.Core.Rdp;

/// <summary>
/// Connexion décrite au client Bureau à distance de FreeRDP compilé pour ZillaTerm (tools/freerdp-build.sh), lancé
/// avec le seul argument « /zillaterm » : les lignes écrites sur son entrée standard, terminées par une ligne vide.
/// Les options de FreeRDP (« /… », « +… », « -… ») décrivent la connexion ; les textes libres (identifiant, mot de
/// passe, programme de démarrage, arguments de l'application distante) passent par « FreeRDP_&lt;Réglage&gt;=valeur »,
/// sans l'analyse des options (virgules, deux-points, guillemets). Rien n'est sur la ligne de commande ni sur le disque.
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

    /// <summary>Lignes à écrire sur l'entrée standard (la ligne vide finale comprise).</summary>
    /// <param name="parentWindow">Fenêtre de l'onglet où FreeRDP crée la sienne.</param>
    /// <param name="pixels">Taille de l'onglet en pixels : résolution du bureau distant, mise à l'échelle ensuite.</param>
    /// <param name="password">Mot de passe d'une connexion directe ; null pour une session PSM (aucun n'est demandé).</param>
    /// <param name="verbose">Journal détaillé de FreeRDP (journal de débogage de ZillaTerm activé).</param>
    /// <exception cref="ArgumentException">Valeur avec un retour à la ligne ou un caractère nul.</exception>
    public static IReadOnlyList<string> Build(RdpConnectionSettings settings, long parentWindow, (int Width, int Height) pixels,
        string? password, bool verbose)
    {
        if (!Supports(settings))
        {
            throw new ArgumentException("Passerelle Bureau à distance non prise en charge", nameof(settings));
        }

        var (width, height) = DesktopSize(pixels);
        var lines = new List<string>
        {
            "/parent-window:" + parentWindow.ToString(CultureInfo.InvariantCulture),
            "/v:" + (settings.Server.Contains(':') ? $"[{Check(settings.Server)}]" : Check(settings.Server)),
            "/port:" + settings.Port.ToString(CultureInfo.InvariantCulture),
            $"/size:{width}x{height}",
            "/smart-sizing",
            "/bpp:" + settings.ColorDepth.ToString(CultureInfo.InvariantCulture),
            "-toggle-fullscreen",
            "/log-level:" + (verbose ? "INFO" : "WARN"),
            // Jamais l'ancienne « sécurité RDP » sans TLS : le serveur ne serait pas authentifié (le client compilé
            // pour ZillaTerm l'impose aussi). Réglages explicites plutôt que « /sec » : une option donnée deux fois
            // ne garde que sa dernière valeur.
            "FreeRDP_RdpSecurity=FALSE",
            "FreeRDP_UseRdpSecurityLayer=FALSE",
        };

        if (!settings.EnableCredSsp)
        {
            // Sans authentification réseau : ni NLA, ni NLA étendue (activée par défaut dans FreeRDP).
            lines.Add("FreeRDP_NlaSecurity=FALSE");
            lines.Add("FreeRDP_ExtSecurity=FALSE");
        }

        lines.Add(settings.RedirectClipboard ? "+clipboard" : "-clipboard");
        if (settings.RedirectDrives)
        {
            lines.Add("/drives");
        }

        if (settings.RedirectPrinters)
        {
            lines.Add("/printer");
        }

        if (settings.RedirectSmartCards)
        {
            lines.Add("/smartcard");
        }

        lines.Add(settings.AudioMode switch
        {
            0 => "/sound",
            1 => "/audio-mode:1",
            _ => "/audio-mode:2",
        });
        if (settings.AudioCapture)
        {
            lines.Add("/microphone");
        }

        if (settings.LoadBalanceInfo.Length > 0)
        {
            lines.Add("/load-balance-info:" + Check(settings.LoadBalanceInfo));
        }

        lines.Add(settings.AutoReconnect ? "+auto-reconnect" : "-auto-reconnect");
        lines.Add(settings.Compress ? "+compression" : "-compression");
        foreach (var (flag, option, setDisables) in Effects)
        {
            bool set = (settings.PerformanceFlags & flag) != 0;
            lines.Add((set != setDisables ? "+" : "-") + option);
        }

        if (settings.ConnectToAdministerServer)
        {
            lines.Add("/admin");
        }

        var (user, domain) = SplitUser(settings.UserName, settings.Domain);
        Set(lines, "Username", user);
        Set(lines, "Domain", domain);
        if (password is not null)
        {
            Set(lines, "Password", password);
        }

        if (settings.IsRemoteApp)
        {
            // Comme l'option /app de FreeRDP : mode RemoteApp, barre de langue, ni fond d'écran ni déplacement plein.
            lines.Add("FreeRDP_RemoteApplicationMode=TRUE");
            lines.Add("FreeRDP_RemoteAppLanguageBarSupported=TRUE");
            lines.Add("FreeRDP_DisableWallpaper=TRUE");
            lines.Add("FreeRDP_DisableFullWindowDrag=TRUE");
            Set(lines, "RemoteApplicationProgram", settings.RemoteApplicationProgram);
            Set(lines, "RemoteApplicationName", settings.RemoteApplicationName);
            Set(lines, "RemoteApplicationCmdLine", settings.RemoteApplicationCmdLine);
        }
        else
        {
            Set(lines, "AlternateShell", settings.StartProgram);
        }

        // Dossier de démarrage du programme, ou de l'application distante (FreeRDP l'envoie avec elle).
        Set(lines, "ShellWorkingDirectory", settings.WorkDir);
        lines.Add("");
        return lines;
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

    private static void Set(List<string> lines, string name, string value)
    {
        if (value.Length > 0)
        {
            lines.Add($"FreeRDP_{name}={Check(value)}");
        }
    }

    private static string Check(string value) =>
        value.AsSpan().IndexOfAny('\r', '\n', '\0') < 0
            ? value
            : throw new ArgumentException("Retour à la ligne ou caractère nul dans un réglage de la connexion");
}
