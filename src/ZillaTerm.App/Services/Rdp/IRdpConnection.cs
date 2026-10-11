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

    /// <summary>Crée la fenêtre de la session dans l'emplacement de l'onglet et lance la connexion.</summary>
    Task StartAsync(IntPtr slot, (int Width, int Height) pixels);

    void Resize((int Width, int Height) pixels);

    void Disconnect();

    void EnterFullScreen();

    void Focus();

    /// <summary>Retire la fenêtre de l'onglet et libère la connexion (déconnectée si <paramref name="disconnect"/>).</summary>
    void Release(bool disconnect);

    /// <summary>Appelle <paramref name="pong"/> (thread de l'interface) dès que la connexion répond.</summary>
    void Ping(Action pong);

    /// <summary>Exécute <paramref name="func"/> avec le contrôle de Windows, sur son thread (tests).</summary>
    Task<T> InvokeOnControlAsync<T>(Func<object, T> func);
}
