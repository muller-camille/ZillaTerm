using ZillaTerm.App.Services;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Ssh;

namespace ZillaTerm.App.Views;

/// <summary>Fin de la session du poste (déconnexion, arrêt, redémarrage) pendant que la fenêtre est ouverte.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Fin de la session du poste : le système n'attend ni les questions de la fermeture ordonnée ni ses suites
    /// asynchrones. Le nettoyage essentiel est fait ici, tout de suite et sans question : réglages enregistrés,
    /// presse-papiers du mot de passe vidé, sessions SSH fermées (copies des fichiers modifiés effacées), coffres
    /// verrouillés, fichiers .rdp effacés, sessions Bureau à distance libérées, session PVWA fermée en meilleur effort
    /// (2 s au plus). La fermeture qui suit n'a plus rien à faire.
    /// </summary>
    internal void EndWithWindowsSession()
    {
        if (_closeConfirmed)
        {
            return;
        }

        bool closing = _loggedOff;
        _loggedOff = true;
        _closeConfirmed = true;
        DebugLog.Write("app", "Fin de la session du poste : fermeture immédiate, sans question.");
        if (!closing)
        {
            RememberLayout();
            SaveSettings();
            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException ex)
            {
                // Une opération annulée a levé une erreur : le nettoyage continue quand même.
                DebugLog.Write("app", "Annulation des opérations en cours", ex);
            }
        }

        _passwordClipboard?.Dispose();
        _mfaKey = null;
        SshAnswerCache.ForgetAll();
        EndSharing(logout: false);
        CloseAllSshSessions();
        _keePass.LockAll(localStore: true);
        _launcher.Cleanup();
        foreach (var view in _rdpViews.ToList())
        {
            view.Session.Dispose();
            RdpLayer.Children.Remove(view);
        }

        _rdpViews.Clear();
        if (_client is { } client)
        {
            bool revoke = _mfaKeyIssued;
            try
            {
                // Hors du fil de l'interface (aucune suite n'y revient) et borné : le système n'attend pas.
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (revoke)
                    {
                        try
                        {
                            await client.RevokeMfaCachingSshKeyAsync(timeout.Token);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // Clé non retirée : elle expirera d'elle-même.
                        }
                    }

                    await client.LogoffAsync(timeout.Token);
                }).Wait(TimeSpan.FromSeconds(2.5));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Au pire, la session expirera d'elle-même côté PVWA (et la clé MFA caching aussi).
            }
        }
    }
}
