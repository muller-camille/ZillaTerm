using Avalonia.Threading;
using ZillaTerm.App.Localization;
using ZillaTerm.Core;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Services;

/// <summary>
/// Session de fichiers seuls, sans terminal, montrée par l'onglet Fichiers : SFTP via le PSMP pour un compte CyberArk,
/// ou SFTP, FTP, FTPS d'une entrée KeePass.
/// </summary>
public sealed class FilesSession : RemoteSession
{
    // Ouverture d'une connexion (celle de l'onglet Fichiers, ou une dédiée au suivi).
    private readonly Func<CancellationToken, Task<IRemoteFiles>> _open;

    // Numéro de la connexion en cours : une ouverture remplacée par une reconnexion ne change plus l'état.
    private int _connection;

    /// <param name="protocol">« SFTP », « FTP », « FTPS »… pour l'affichage.</param>
    public FilesSession(string label, string protocol, Func<CancellationToken, Task<IRemoteFiles>> open, Dispatcher dispatcher,
        PvwaAccount? account = null, SavedSession? saved = null)
        : base(account, label, dispatcher, saved)
    {
        Protocol = protocol;
        _open = open;
    }

    /// <summary>Protocole affiché (« SFTP », « FTPS »…).</summary>
    public string Protocol { get; }

    protected override Task<IRemoteFiles> OpenFilesAsync(CancellationToken ct) => _open(ct);

    /// <summary>Ouvre (ou rouvre) la connexion de l'onglet Fichiers.</summary>
    public async Task ConnectAsync()
    {
        int connection = ++_connection;
        SetState(RemoteSessionState.Connecting, null);
        // Reconnexion : l'ancienne connexion est fermée (ses transferts ont été annulés avant) ; une ouverture encore en
        // cours le sera dès qu'elle aboutit.
        ReleaseBrowser(Browser);
        // Les demandes faites pendant l'ouverture (éditeur, comparaison, suivi) attendent cette connexion-ci : jamais de
        // seconde connexion en parallèle.
        var opening = _open(Lifetime);
        Browser = opening;
        try
        {
            DebugLog.Write("files", $"{Label} : connexion {Protocol}");
            await opening;
            if (connection == _connection && !IsDisposed)
            {
                SetState(RemoteSessionState.Connected, null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DebugLog.Write("files", $"{Label} : échec de la connexion", ex);
            if (connection == _connection && !IsDisposed)
            {
                SetState(RemoteSessionState.Failed, ex is HostKeyRefusedException ? ex.Message : ex is OperationCanceledException ? Strings.ConnectionCancelled : ErrorText.Describe(ex));
            }

            throw;
        }
    }
}
