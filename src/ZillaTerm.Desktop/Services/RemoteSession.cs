using System.IO;
using Avalonia.Threading;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

public enum RemoteSessionState
{
    Connecting,
    Connected,
    Closed,
    Failed,
}

/// <summary>
/// Session d'un onglet dont l'onglet Fichiers montre les fichiers : terminal SSH (<see cref="SshSession"/>) ou fichiers
/// seuls (<see cref="FilesSession"/>). La connexion de l'explorateur, ouverte à la demande, sert au panneau Fichiers, à
/// l'éditeur, à la comparaison et au suivi ; les connexions dédiées à un suivi sont fermées avec la session.
/// Les événements sont toujours déclenchés sur le thread de l'interface.
/// </summary>
public abstract class RemoteSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IRemoteFiles> _dedicated = [];

    /// <param name="account">Compte CyberArk ; null pour une connexion directe (accès d'urgence KeePass).</param>
    protected RemoteSession(PvwaAccount? account, string label, Dispatcher dispatcher, SavedSession? saved)
    {
        Account = account;
        Label = label;
        Dispatcher = dispatcher;
        Saved = saved;
    }

    public event Action? StateChanged;

    public PvwaAccount? Account { get; }

    public SavedSession? Saved { get; }

    public string Label { get; }

    /// <summary>PSMP par lequel passe la session ; null pour une connexion directe.</summary>
    public string? Psmp { get; init; }

    /// <summary>Connexion CyberArk demandée (type, composant, machine cible), pour l'ajouter à « Mes serveurs ».</summary>
    public ConnectRequest? Request { get; init; }

    public RemoteSessionState State { get; private set; } = RemoteSessionState.Connecting;

    public string? Error { get; private set; }

    /// <summary>Fichiers de cette session ouverts dans l'éditeur de texte (créé par la fenêtre principale).</summary>
    public RemoteEditor? Editor { get; set; }

    /// <summary>Dossier affiché par le panneau Fichiers pour cette session (conservé quand on change d'onglet).</summary>
    public string? BrowserDirectory { get; set; }

    public bool IsDisposed { get; private set; }

    protected Dispatcher Dispatcher { get; }

    /// <summary>Annulé à la fermeture de la session.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>Connexion de l'explorateur : ouverte, en cours d'ouverture, ou null.</summary>
    protected Task<IRemoteFiles>? Browser { get; set; }

    /// <summary>Ouvre une connexion de fichiers : celle de l'onglet Fichiers, ou une dédiée au suivi d'un fichier.</summary>
    protected abstract Task<IRemoteFiles> OpenFilesAsync(CancellationToken ct);

    /// <summary>Connexion de l'explorateur, ouverte au premier usage (ou rouverte si elle a été perdue).</summary>
    public Task<IRemoteFiles> GetBrowserAsync()
    {
        if (Browser is { IsFaulted: true } or { IsCanceled: true } || Browser is { IsCompletedSuccessfully: true, Result.IsConnected: false })
        {
            if (Browser.IsCompletedSuccessfully)
            {
                DisposeInBackground(Browser.Result);
            }

            Browser = null;
        }

        return Browser ??= OpenFilesAsync(Lifetime);
    }

    /// <summary>Connexion de l'explorateur si elle est ouverte (sans en ouvrir une).</summary>
    public IRemoteFiles? OpenedBrowser
    {
        get
        {
            try
            {
                return Browser is { IsCompletedSuccessfully: true } task && task.Result.IsConnected ? task.Result : null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Connexion dédiée (pour un compte CyberArk, une session PSMP de plus), pour un suivi de fichier indépendant de
    /// l'onglet Fichiers. Elle est fermée par <see cref="CloseDedicatedBrowser"/>, ou avec la session.
    /// </summary>
    public async Task<IRemoteFiles> OpenDedicatedBrowserAsync()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        DebugLog.Write("files", $"{Label} : connexion dédiée au suivi d'un fichier");
        var browser = await OpenFilesAsync(Lifetime);
        if (IsDisposed)
        {
            // Session fermée pendant la connexion : rien ne doit rester ouvert.
            DisposeInBackground(browser);
            throw new ObjectDisposedException(GetType().Name);
        }

        _dedicated.Add(browser);
        return browser;
    }

    public void CloseDedicatedBrowser(IRemoteFiles browser)
    {
        if (_dedicated.Remove(browser))
        {
            DisposeInBackground(browser);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Editor?.Dispose();
        _lifetime.Cancel();
        CloseConnections();
        // Une connexion encore en cours d'ouverture aboutit malgré l'annulation : fermée dès qu'elle est ouverte.
        ReleaseBrowser(Browser);
        foreach (var dedicated in _dedicated)
        {
            DisposeInBackground(dedicated);
        }

        _dedicated.Clear();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Ferme une connexion de fichiers remplacée ou abandonnée, tout de suite ou dès que son ouverture aboutit, et jamais
    /// pendant le renvoi d'un fichier modifié dans l'éditeur (<see cref="RemoteEditor.WritesDone"/>) : l'écriture, faite sur
    /// place, laisserait le fichier du serveur tronqué.
    /// </summary>
    protected void ReleaseBrowser(Task<IRemoteFiles>? browser)
    {
        var writes = Editor?.WritesDone ?? Task.CompletedTask;
        if (browser is { IsCompletedSuccessfully: true } && writes.IsCompleted)
        {
            DisposeInBackground(browser.Result);
        }
        else if (browser is { IsCompletedSuccessfully: true } or { IsCompleted: false })
        {
            if (!writes.IsCompleted)
            {
                DebugLog.Write("files", $"{Label} : connexion de fichiers fermée à la fin du renvoi en cours");
            }

            _ = Task.WhenAll(browser, writes).ContinueWith(_ =>
            {
                if (browser.IsCompletedSuccessfully)
                {
                    DisposeInBackground(browser.Result);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Fermeture de la session : connexions propres au type de session (shell SSH...).</summary>
    protected virtual void CloseConnections()
    {
    }

    /// <summary>
    /// Fermeture de connexions hors du thread de l'interface (la déconnexion attend le réseau), dans l'ordre donné. Une
    /// écriture en attente échoue alors, sans conséquence.
    /// </summary>
    protected void DisposeInBackground(params IDisposable?[] connections)
    {
        if (connections.All(c => c is null))
        {
            return;
        }

        var label = Label;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var connection in connections)
                {
                    connection?.Dispose();
                }
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
            {
                DebugLog.Write("files", $"{label} : erreur à la fermeture", ex);
            }
        });
    }

    protected void SetState(RemoteSessionState state, string? error)
    {
        if (State == state && Error == error)
        {
            return;
        }

        State = state;
        Error = error;
        StateChanged?.Invoke();
    }
}
