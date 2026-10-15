using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Net.Http;
using System.Runtime.InteropServices;
using ZillaTerm.App.Localization;
using ZillaTerm.App.Services;
using ZillaTerm.Core;

namespace ZillaTerm.App.Views;

/// <summary>
/// Maintien de la session PVWA : une requête légère toutes les quelques minutes pour que le délai d'inactivité du
/// PVWA ne ferme pas la session pendant qu'on travaille dans les onglets. Rien n'est envoyé tant que la session
/// Windows est verrouillée, ni quand personne n'a touché au clavier ou à la souris depuis
/// <see cref="KeepAliveMaxIdle"/> : un poste laissé sans surveillance ne garde pas la session ouverte.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(4);

    /// <summary>Inactivité du poste au-delà de laquelle la session n'est plus maintenue (le délai du PVWA reprend).</summary>
    private static readonly TimeSpan KeepAliveMaxIdle = TimeSpan.FromMinutes(15);

    private DispatcherTimer? _keepAlive;
    // Session du poste verrouillée (SessionLock) : rien n'est envoyé au PVWA.
    private bool _windowsLocked;
    private bool _keepAliveRunning;
    // Fenêtre de reconnexion ouverte, ou à ouvrir dès que la fenêtre principale redevient active.
    private bool _reconnecting;
    private bool _reconnectWhenActive;

    private void StartKeepAlive()
    {
        _keepAlive ??= CreateKeepAliveTimer();
        _keepAlive.IsEnabled = !IsOffline && _settings.KeepPvwaSessionAlive;
    }

    private DispatcherTimer CreateKeepAliveTimer()
    {
        var timer = new DispatcherTimer { Interval = KeepAliveInterval };
        timer.Tick += async (_, _) => await KeepAliveAsync();
        Closed += (_, _) => timer.Stop();
        Activated += (_, _) =>
        {
            if (_reconnectWhenActive)
            {
                _reconnectWhenActive = false;
                Dispatcher.UIThread.Post(() => _ = ReconnectAsync(userAction: false));
            }
        };
        return timer;
    }

    private async Task KeepAliveAsync()
    {
        if (_client is null || _windowsLocked || _keepAliveRunning || _loggedOff || UserIdle() > KeepAliveMaxIdle)
        {
            return;
        }

        _keepAliveRunning = true;
        try
        {
            await _client.KeepAliveAsync(_lifetime.Token);
        }
        catch (PvwaException ex) when (ex.IsUnauthorized)
        {
            // Session déjà expirée (poste verrouillé longtemps, délai très court côté PVWA...) : fenêtre de reconnexion,
            // tout de suite si ZillaTerm est au premier plan, sinon dès qu'il y revient. Rien n'est fermé.
            _keepAlive?.Stop();
            SetStatus(Strings.KeepAliveExpired, isError: true);
            if (IsActive)
            {
                await ReconnectAsync(userAction: false);
            }
            else
            {
                _reconnectWhenActive = true;
            }
        }
        catch (Exception ex) when (ex is PvwaException or HttpRequestException or TaskCanceledException)
        {
            // Réseau ou PVWA momentanément indisponible : nouvel essai au prochain passage.
        }
        finally
        {
            _keepAliveRunning = false;
        }
    }

    /// <summary>Temps écoulé depuis la dernière action au clavier ou à la souris sur le poste (voir <see cref="UserActivity"/>).</summary>
    private static TimeSpan UserIdle() => UserActivity.Idle;
}
