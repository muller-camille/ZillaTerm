using System.Globalization;
using System.Text;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Lancement du client FreeRDP 3 du système sous Linux (xfreerdp3). Le fichier de connexion sur l'entrée standard est un
/// correctif du fork de ZillaTerm (Windows) : ici, TOUTES les options passent par l'entrée standard (« /args-from:stdin »,
/// une option par ligne, fin à la première ligne vide), la ligne de commande n'a que celle-ci. Rien n'est écrit sur le
/// disque. Mêmes options que le client Windows (<see cref="FreeRdpArguments"/> : fenêtre, taille, empreintes acceptées,
/// redirections…), plus celles du fichier de connexion : serveur, identifiant, mot de passe, programme de démarrage (qui
/// porte le jeton PSM).
/// </summary>
internal static class XFreeRdpArguments
{
    /// <param name="parentWindow">Fenêtre d'accueil de l'emplacement de l'onglet, où FreeRDP crée la sienne.</param>
    /// <exception cref="ArgumentException">
    /// Valeur avec un retour à la ligne ou un caractère nul, passerelle, application distante (ouverte hors de l'onglet
    /// par ce client).
    /// </exception>
    public static FreeRdpLaunch Build(RdpConnectionSettings settings, long parentWindow, (int Width, int Height) pixels,
        string? password, bool verbose, IReadOnlyCollection<string> fingerprints, string? configPath)
    {
        if (settings.IsRemoteApp)
        {
            throw new ArgumentException("Application distante : fenêtres hors de l'onglet avec ce client", nameof(settings));
        }

        var lines = new List<string>();
        foreach (var line in Connection(settings, password).Concat(Options(settings, parentWindow, pixels, verbose, fingerprints, configPath)))
        {
            lines.Add(Check(line));
        }

        // Une ligne vide termine la liste : rien de ce qui suivrait ne serait lu.
        var input = new StringBuilder();
        foreach (var line in lines)
        {
            input.Append(line).Append('\n');
        }

        input.Append('\n');
        return new FreeRdpLaunch([FreeRdpClient.ArgumentsFromStdin], input.ToString());
    }

    /// <summary>
    /// Ligne du journal de FreeRDP qui suit chaque connexion établie, reconnexion automatique comprise (canaux dynamiques
    /// rechargés) : sans le correctif du fork, qui écrit « Reconnected », c'est elle qui dit que la session est reprise.
    /// </summary>
    public const string ChannelsLoadedLine = "[dvcman_load_addin]: Loading Dynamic Virtual Channel";

    /// <summary>
    /// Options sans secret (journal de débogage) : celles du client Windows (<see cref="FreeRdpArguments"/>), sans le
    /// fichier de connexion (« - »), sans « /from-stdin » (l'entrée standard est fermée après la lecture des options),
    /// avec le journal du client X11 au lieu de celui du client Windows, et toujours celui des canaux dynamiques (voir
    /// <see cref="ChannelsLoadedLine"/>).
    /// </summary>
    public static IReadOnlyList<string> Options(RdpConnectionSettings settings, long parentWindow, (int Width, int Height) pixels,
        bool verbose, IReadOnlyCollection<string> fingerprints, string? configPath) =>
        FreeRdpArguments.Build(settings, parentWindow, pixels, null, verbose, fingerprints, legacySecurity: false, configPath).Arguments
            .Where(a => a is not "-" and not "/from-stdin")
            .Select(a => a.Replace("com.freerdp.client.windows:DEBUG", "com.freerdp.client.x11.cliprdr:DEBUG", StringComparison.Ordinal))
            .Select(a => !verbose && a.StartsWith("/log-filters:", StringComparison.Ordinal) ? a + ",com.freerdp.channels.drdynvc.client:INFO" : a)
            .ToList();

    /// <summary>
    /// Ce que le client Windows lit dans le fichier de connexion. Mot de passe toujours donné (« /p » seul : vide) :
    /// FreeRDP ne demande rien. Chaque valeur va jusqu'à la fin de sa ligne, sans analyse (virgules, deux-points,
    /// guillemets), comme dans le fichier.
    /// </summary>
    private static IEnumerable<string> Connection(RdpConnectionSettings settings, string? password)
    {
        yield return "/v:" + (settings.Server.Contains(':') ? $"[{settings.Server}]" : settings.Server);
        yield return "/port:" + settings.Port.ToString(CultureInfo.InvariantCulture);
        var (user, domain) = FreeRdpArguments.SplitUser(settings.UserName, settings.Domain);
        if (user.Length > 0)
        {
            yield return "/u:" + user;
        }

        if (domain.Length > 0)
        {
            yield return "/d:" + domain;
        }

        yield return string.IsNullOrEmpty(password) ? "/p" : "/p:" + password;
        if (settings.StartProgram.Length > 0)
        {
            yield return "/shell:" + settings.StartProgram;
        }

        if (settings.WorkDir.Length > 0)
        {
            yield return "/shell-dir:" + settings.WorkDir;
        }

        if (settings.LoadBalanceInfo.Length > 0)
        {
            yield return "/load-balance-info:" + settings.LoadBalanceInfo;
        }
    }

    private static string Check(string line) =>
        line.AsSpan().IndexOfAny('\r', '\n', '\0') < 0 && line.Length > 0
            ? line
            : throw new ArgumentException("Retour à la ligne ou caractère nul dans un réglage de la connexion");

    /// <summary>
    /// Code de fin de FreeRDP (comme le code de sortie du client Windows : <see cref="FreeRdpExit"/>) d'après le code de
    /// sortie de xfreerdp (XF_EXIT_*, client/X11/xfreerdp.h) ; null pour un code sans équivalent (arrêt brutal, options
    /// refusées).
    /// </summary>
    public static uint? ExitCode(int exitCode)
    {
        const uint errInfo = 0x0001_0000;
        const uint connect = 0x0002_0000;
        return exitCode switch
        {
            0 => 0,
            // Codes indépendants du protocole : ceux de FreeRDP (ERRINFO_*).
            >= 1 and <= 12 => errInfo | (uint)exitCode,
            // Licence, protocole : erreurs du serveur.
            >= 16 and <= 26 => errInfo | (uint)(0x100 + exitCode - 16),
            32 => errInfo | 0x10C9,
            131 => connect | 0x06,
            132 => connect | 0x09,
            133 => connect | 0x0C,
            134 => connect | 0x14,
            135 => connect | 0x18,
            136 => connect | 0x01,
            137 => connect | 0x02,
            138 => connect | 0x03,
            139 => connect | 0x04,
            140 => connect | 0x05,
            141 => connect | 0x06,
            142 => connect | 0x07,
            143 => connect | 0x08,
            144 => connect | 0x0A,
            145 => connect | 0x0B,
            147 => connect | 0x0D,
            148 => connect | 0x0E,
            149 => connect | 0x13,
            150 => connect | 0x11,
            151 => connect | 0x12,
            152 => connect | 0x0F,
            153 => connect | 0x10,
            154 => connect | 0x15,
            155 => connect | 0x16,
            156 => connect | 0x17,
            157 => connect | 0x19,
            158 => connect | 0x1A,
            159 => connect | 0x1B,
            160 => connect | 0x1D,
            161 => connect | 0x1E,
            _ => null,
        };
    }
}
