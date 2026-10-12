namespace ZillaTerm.Core.Ssh;

/// <summary>
/// Saisie restée sans réponse du serveur. Dès que l'utilisateur tape, le nombre de messages reçus du serveur est noté ;
/// sans nouveau message au bout de <see cref="ProbeAfter"/>, une demande à laquelle le serveur doit répondre est envoyée
/// (puis toutes les <see cref="ProbeEvery"/>) et, au bout de <see cref="SilentAfter"/>, le serveur est dit muet jusqu'au
/// premier message reçu (écho, sortie, ou réponse à la demande). Une saisie sans écho (mot de passe) n'alerte donc pas :
/// le serveur répond à la demande. La connexion n'est déclarée perdue que plus tard, par le maintien de connexion (voir
/// <see cref="SshConnector.KeepAlive"/>).
/// </summary>
public sealed class ServerResponseWatch
{
    public static readonly TimeSpan ProbeAfter = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    private DateTime? _since;
    private DateTime _probed;
    private long _received;

    /// <summary>Une saisie attend une réponse du serveur.</summary>
    public bool IsWatching => _since is not null;

    /// <summary>Le serveur n'a rien envoyé depuis la saisie, ni répondu à la demande, depuis <see cref="SilentAfter"/> au moins.</summary>
    public bool IsSilent { get; private set; }

    /// <summary>Temps écoulé depuis la saisie restée sans réponse.</summary>
    public TimeSpan SilentFor(DateTime now) => _since is { } since ? now - since : TimeSpan.Zero;

    /// <summary>L'utilisateur a tapé : la surveillance commence, si elle n'est pas déjà en cours.</summary>
    /// <param name="received">Nombre de messages reçus du serveur à cet instant.</param>
    public void Typed(DateTime now, long received)
    {
        if (_since is null)
        {
            _since = now;
            _received = received;
            _probed = DateTime.MinValue;
        }
    }

    /// <summary>Point régulier de la surveillance ; vrai s'il faut envoyer une demande au serveur.</summary>
    /// <param name="received">Nombre de messages reçus du serveur à cet instant.</param>
    public bool Check(DateTime now, long received)
    {
        if (_since is not { } since)
        {
            return false;
        }

        if (received != _received)
        {
            // Le serveur a envoyé quelque chose : il est là.
            Stop();
            return false;
        }

        var waited = now - since;
        if (waited >= SilentAfter)
        {
            IsSilent = true;
        }

        if (waited >= ProbeAfter && now - _probed >= ProbeEvery)
        {
            _probed = now;
            return true;
        }

        return false;
    }

    /// <summary>Surveillance arrêtée (réponse reçue, session terminée ou rouverte).</summary>
    public void Stop()
    {
        _since = null;
        IsSilent = false;
    }
}
