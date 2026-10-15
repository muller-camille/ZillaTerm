namespace ZillaTerm.Core.Rdp;

/// <summary>Raison pour laquelle l'affichage d'une session Bureau à distance est en pause (voir <see cref="RdpDisplayPause"/>).</summary>
public enum RdpDisplayPauseReason
{
    /// <summary>Affichage reçu normalement.</summary>
    None,

    /// <summary>Session que personne ne peut voir : onglet en arrière-plan, ZillaTerm réduit, Windows verrouillé.</summary>
    Hidden,

    /// <summary>Session affichée, mais poste inactif (ni clavier ni souris) depuis <see cref="RdpDisplayPause.IdleDelay"/>.</summary>
    Idle,
}

/// <summary>
/// Pause de l'affichage d'une session Bureau à distance que personne ne regarde : le client demande au serveur de ne
/// plus envoyer l'image (« Suppress Output » du protocole RDP), la connexion reste ouverte. Ni trafic d'affichage, ni
/// encodage de l'image par le serveur (le PSM) pour une session que personne ne voit ; l'image entière revient à la
/// reprise.
/// </summary>
public static class RdpDisplayPause
{
    /// <summary>
    /// Session invisible depuis ce délai : affichage en pause. Passer d'un onglet à l'autre ne redemande pas l'image entière
    /// à chaque fois.
    /// </summary>
    public static readonly TimeSpan HiddenDelay = TimeSpan.FromMinutes(1);

    /// <summary>Poste inactif depuis ce délai (comme pour le maintien de la session PVWA) : affichage de la session affichée en pause.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Raison de mettre l'affichage en pause, ou <see cref="RdpDisplayPauseReason.None"/> pour le reprendre (aussitôt :
    /// les délais ne valent que pour la pause).
    /// </summary>
    /// <param name="shown">Session visible : onglet affiché, ZillaTerm ni réduit ni caché par le verrouillage de Windows.</param>
    /// <param name="hiddenFor">Temps depuis lequel la session n'est plus visible (sans objet si <paramref name="shown"/>).</param>
    /// <param name="idle">Temps depuis la dernière action au clavier ou à la souris sur le poste.</param>
    /// <param name="watched">
    /// Écran gardé ou montré à quelqu'un (enregistrement d'une intervention, partage) : jamais en pause tant que la session
    /// est affichée, même poste inactif. Session invisible : en pause quand même, marquée dans l'enregistrement.
    /// </param>
    public static RdpDisplayPauseReason Decide(bool shown, TimeSpan hiddenFor, TimeSpan idle, bool watched)
    {
        if (!shown)
        {
            return hiddenFor >= HiddenDelay ? RdpDisplayPauseReason.Hidden : RdpDisplayPauseReason.None;
        }

        return !watched && idle >= IdleDelay ? RdpDisplayPauseReason.Idle : RdpDisplayPauseReason.None;
    }
}
