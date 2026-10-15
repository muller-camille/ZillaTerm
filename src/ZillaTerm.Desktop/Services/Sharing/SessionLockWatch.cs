using Tmds.DBus.Protocol;
using ZillaTerm.App.Services;
using ZillaTerm.Core.Diagnostics;

namespace ZillaTerm.App.Services.Sharing;

/// <summary>
/// Verrouillage de la session hors de Windows (où <c>SystemEvents.SessionSwitch</c> le signale), suivi une fois pour toute
/// l'application (<see cref="SessionLock"/>) : coffres verrouillés, maintien de la session PVWA suspendu, partages arrêtés,
/// comme au verrouillage de Windows. Sous Linux : logind (bus système) — signal « Lock » de la session, « LockedHint » qui
/// passe à vrai (bureau verrouillé), session qui n'est plus active (changement d'utilisateur, autre console), mise en
/// veille (« PrepareForSleep ») — et l'économiseur d'écran du bureau (bus de session, « ActiveChanged »). Sur demande,
/// le déverrouillage aussi (« Unlock », « LockedHint » repassé à faux, économiseur d'écran arrêté) : le maintien de la
/// session PVWA reprend.
/// </summary>
/// <remarks>
/// <para><c>locked</c> et <c>unlocked</c> sont appelés depuis un fil de D-Bus, une fois par évènement.</para>
/// <para>
/// Sans D-Bus (macOS, conteneur sans bus système, logind absent) rien n'est suivi : <see cref="IsWatching"/> reste faux et
/// le journal de débogage le dit. Rien ne réagit alors au verrouillage (les partages continuent).
/// </para>
/// </remarks>
internal sealed class SessionLockWatch : IDisposable
{
    private const string Login1 = "org.freedesktop.login1";
    private const string Login1Path = "/org/freedesktop/login1";
    private const string ManagerInterface = "org.freedesktop.login1.Manager";
    private const string SessionInterface = "org.freedesktop.login1.Session";

    private readonly Action<string> _locked;
    private readonly Action<string>? _unlocked;
    private readonly List<IDisposable> _watches = [];
    private readonly List<DBusConnection> _connections = [];
    private volatile bool _disposed;

    private SessionLockWatch(Action<string> locked, Action<string>? unlocked)
    {
        _locked = locked;
        _unlocked = unlocked;
    }

    /// <summary>Vrai une fois logind suivi (verrouillage, changement d'utilisateur, veille).</summary>
    public bool IsWatching { get; private set; }

    /// <summary>Fin des abonnements (réussis ou non) : pour les tests et le journal.</summary>
    public Task Started { get; private set; } = Task.CompletedTask;

    /// <param name="locked">Session verrouillée ou quittée ; reçoit la cause (pour le journal).</param>
    /// <param name="unlocked">
    /// Session déverrouillée (seulement celle de ce processus, une fois connue) ; reçoit la cause. Facultatif.
    /// </param>
    public static SessionLockWatch Start(Action<string> locked, Action<string>? unlocked = null)
    {
        var watch = new SessionLockWatch(locked, unlocked);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            watch.Started = watch.StartAsync();
        }
        else
        {
            DebugLog.Write("session", "Verrouillage de la session non suivi sur ce système : coffres, maintien de la session PVWA et partages ne réagissent pas au verrouillage.");
        }

        return watch;
    }

    private async Task StartAsync()
    {
        try
        {
            var system = await ConnectAsync(DBusAddress.System);
            // Session de ce processus ; inconnue (lancé hors d'une session logind) : celles de tous les utilisateurs du poste.
            var path = await SessionPathAsync(system);
            DebugLog.Write("session", path is null
                ? "Session logind de ZillaTerm inconnue : le verrouillage de toute session du poste compte."
                : $"Verrouillage de la session suivi (logind, {path}).");
            _watches.Add(await system.WatchSignalAsync(Login1, path, SessionInterface, "Lock",
                (Notification n) => OnSignal(n.IsCompletion, "verrouillage (logind)"), ObserverFlags.None, emitOnCapturedContext: false));
            if (_unlocked is not null && path is not null)
            {
                _watches.Add(await system.WatchSignalAsync(Login1, path, SessionInterface, "Unlock",
                    (Notification n) => OnUnlock(n.IsCompletion, "déverrouillage (logind)"), ObserverFlags.None, emitOnCapturedContext: false));
            }

            _watches.Add(await system.WatchSignalAsync(Login1, Login1Path, ManagerInterface, "PrepareForSleep",
                static (Message m, object? _) => m.GetBodyReader().ReadBool(),
                (Notification<bool> n) => OnSignal(n.IsCompletion || !n.HasValue || !n.Value, "mise en veille"), ObserverFlags.None, emitOnCapturedContext: false));
            _watches.Add(await system.WatchPropertiesChangedAsync(Login1, path, SessionInterface, static (Message m, object? _) => ReadSessionChanges(m),
                (Notification<(bool Locked, bool Inactive, bool Unlocked)> n) =>
                {
                    if (n.IsCompletion || !n.HasValue)
                    {
                        return;
                    }

                    if (n.Value.Locked)
                    {
                        Notify("bureau verrouillé (LockedHint)");
                    }
                    else if (n.Value.Unlocked && path is not null)
                    {
                        // Seulement pour notre session : un autre utilisateur qui déverrouille la sienne ne compte pas.
                        OnUnlock(ignore: false, "bureau déverrouillé (LockedHint)");
                    }
                    else if (n.Value.Inactive && path is not null)
                    {
                        // Seulement pour notre session : celle d'un autre utilisateur devient inactive quand on revient.
                        Notify("session inactive (changement d'utilisateur ou de console)");
                    }
                }, ObserverFlags.None, emitOnCapturedContext: false));
            IsWatching = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("session", "logind injoignable : verrouillage de la session non suivi (coffres, maintien de la session PVWA, partages)", ex);
        }

        try
        {
            // Économiseur d'écran du bureau (KDE, Xfce, Cinnamon… ; GNOME a aussi le sien) : écran verrouillé ou éteint.
            var session = await ConnectAsync(DBusAddress.Session);
            foreach (var name in (string[])["org.freedesktop.ScreenSaver", "org.gnome.ScreenSaver"])
            {
                _watches.Add(await session.WatchSignalAsync(null, null, name, "ActiveChanged",
                    static (Message m, object? _) => m.GetBodyReader().ReadBool(),
                    (Notification<bool> n) =>
                    {
                        OnSignal(n.IsCompletion || !n.HasValue || !n.Value, $"économiseur d'écran ({name})");
                        OnUnlock(n.IsCompletion || !n.HasValue || n.Value, $"économiseur d'écran arrêté ({name})");
                    },
                    ObserverFlags.None, emitOnCapturedContext: false));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("session", "Économiseur d'écran du bureau non suivi (bus de session injoignable)", ex);
        }
    }

    private async Task<DBusConnection> ConnectAsync(string? address)
    {
        var connection = new DBusConnection(address ?? throw new InvalidOperationException("Adresse D-Bus inconnue."));
        lock (_connections)
        {
            if (_disposed)
            {
                connection.Dispose();
                throw new ObjectDisposedException(nameof(SessionLockWatch));
            }

            _connections.Add(connection);
        }

        await connection.ConnectAsync();
        return connection;
    }

    /// <summary>Chemin logind de la session de ce processus ; null si elle est inconnue.</summary>
    private static async Task<string?> SessionPathAsync(DBusConnection connection)
    {
        try
        {
            return await CallAsync(connection, "GetSessionByPID", (uint)Environment.ProcessId, null);
        }
        catch (DBusErrorReplyException ex)
        {
            DebugLog.Write("session", $"logind : session du processus inconnue ({ex.ErrorName})");
        }

        if (Environment.GetEnvironmentVariable("XDG_SESSION_ID") is { Length: > 0 } id)
        {
            try
            {
                return await CallAsync(connection, "GetSession", 0, id);
            }
            catch (DBusErrorReplyException ex)
            {
                DebugLog.Write("session", $"logind : session XDG_SESSION_ID inconnue ({ex.ErrorName})");
            }
        }

        return null;
    }

    /// <summary>Méthode de logind qui rend le chemin d'une session : par numéro de processus, ou par identifiant.</summary>
    private static Task<string> CallAsync(DBusConnection connection, string method, uint pid, string? id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Login1, Login1Path, ManagerInterface, method, id is null ? "u" : "s", MessageFlags.None);
        if (id is null)
        {
            writer.WriteUInt32(pid);
        }
        else
        {
            writer.WriteString(id);
        }

        return connection.CallMethodAsync(writer.CreateMessage(), static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
    }

    /// <summary>« LockedHint » passé à vrai ou à faux, « Active » passé à faux (propriétés absentes : faux).</summary>
    private static (bool Locked, bool Inactive, bool Unlocked) ReadSessionChanges(Message message)
    {
        var reader = message.GetBodyReader();
        reader.ReadString();
        var changed = reader.ReadDictionaryOfStringToVariantValue();
        bool hinted = changed.TryGetValue("LockedHint", out var hint);
        bool locked = hinted && hint.GetBool();
        bool inactive = changed.TryGetValue("Active", out var active) && !active.GetBool();
        return (locked, inactive, hinted && !locked);
    }

    /// <param name="ignore">Fin d'abonnement, ou valeur qui ne signale pas de verrouillage.</param>
    private void OnSignal(bool ignore, string cause)
    {
        if (!ignore)
        {
            Notify(cause);
        }
    }

    /// <param name="ignore">Fin d'abonnement, ou valeur qui ne signale pas de déverrouillage.</param>
    private void OnUnlock(bool ignore, string cause)
    {
        if (ignore || _disposed || _unlocked is null)
        {
            return;
        }

        DebugLog.Write("session", $"Session : {cause}");
        try
        {
            _unlocked(cause);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("session", "Reprise au déverrouillage", ex);
        }
    }

    private void Notify(string cause)
    {
        if (_disposed)
        {
            return;
        }

        DebugLog.Write("session", $"Session : {cause}");
        try
        {
            _locked(cause);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Un gestionnaire de D-Bus ne doit pas lever d'exception (la connexion serait fermée).
            DebugLog.Write("session", "Réaction au verrouillage", ex);
        }
    }

    public void Dispose()
    {
        lock (_connections)
        {
            _disposed = true;
        }

        foreach (var watch in _watches.ToList())
        {
            watch.Dispose();
        }

        foreach (var connection in _connections.ToList())
        {
            connection.Dispose();
        }
    }
}
