namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Une tentative de connexion d'une session Bureau à distance (<see cref="RdpSession"/>) : contrôle Bureau à distance
/// de Windows sur son thread (<see cref="RdpConnection"/>), ou client FreeRDP dans son processus
/// (<see cref="FreeRdpConnection"/>). Appelée sur le thread de l'interface, sans jamais l'attendre.
/// </summary>
internal interface IRdpConnection
{
    /// <summary>Terminée quand la fenêtre de la connexion a quitté l'onglet.</summary>
    Task Detached { get; }

    /// <summary>Plein écran possible (contrôle de Windows seulement).</summary>
    bool CanFullScreen { get; }

    /// <summary>
    /// Vrai quand la fenêtre de la session est à afficher dans l'onglet : dès sa création pour le contrôle de Windows,
    /// à l'ouverture de la connexion pour le client FreeRDP (rien à montrer avant : l'onglet garde sa progression).
    /// </summary>
    bool HasWindow { get; }

    /// <summary>Crée la fenêtre de la session dans l'emplacement de l'onglet et lance la connexion.</summary>
    Task StartAsync(IntPtr slot, (int Width, int Height) pixels);

    void Resize((int Width, int Height) pixels);

    void Disconnect();

    void EnterFullScreen();

    void Focus();

    /// <summary>Retire la fenêtre de l'onglet et libère la connexion (déconnectée si <paramref name="disconnect"/>).</summary>
    void Release(bool disconnect);

    /// <summary>Affichage de la session mis en pause possible, connexion gardée (client FreeRDP seulement).</summary>
    bool CanPauseDisplay { get; }

    /// <summary>
    /// Met l'affichage de la session en pause (le serveur n'envoie plus l'image, la connexion reste ouverte) ou le reprend
    /// (l'image entière revient). Sans effet avant que la fenêtre de la session existe : redit tant que la pause dure.
    /// </summary>
    void PauseDisplay(bool paused);

    /// <summary>
    /// Bureau tel que le client l'a décodé, reçu par un tube (client FreeRDP, option « /frame-pipe ») ; null pour le
    /// contrôle de Windows ou avant le lancement du client. Lisible depuis n'importe quel fil.
    /// </summary>
    Core.Rdp.RemoteScreen? Screen { get; }

    /// <summary>Appelle <paramref name="pong"/> (thread de l'interface) dès que la connexion répond.</summary>
    void Ping(Action pong);

    /// <summary>Exécute <paramref name="func"/> avec le contrôle de Windows, sur son thread (tests).</summary>
    Task<T> InvokeOnControlAsync<T>(Func<object, T> func);
}
