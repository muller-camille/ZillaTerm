using System.Windows;
using System.Windows.Threading;
using ZillaTerm.App.Services.Rdp;
using ZillaTerm.Core.Rdp;

namespace ZillaTerm.App.Views;

/// <summary>
/// Pause de l'affichage des sessions Bureau à distance que personne ne regarde (option des Paramètres, voir
/// <see cref="RdpDisplayPause"/>) : revue chaque seconde tant qu'un onglet Bureau à distance est ouvert, et aussitôt
/// quand l'onglet affiché change, quand la fenêtre est réduite ou rouverte, ou au premier geste sur un onglet en pause.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _rdpPauseTimer;

    // Depuis quand chaque session n'est plus visible (onglet en arrière-plan, fenêtre réduite, Windows verrouillé).
    private readonly Dictionary<RdpSession, DateTime> _rdpHiddenSince = [];

    /// <summary>Revue des pauses lancée pour un nouvel onglet Bureau à distance (rien à revoir sans onglet).</summary>
    private void WatchRdpDisplayPause(RdpSessionView view)
    {
        view.WakeRequested += UpdateRdpDisplayPause;
        // Onglet affiché ou passé en arrière-plan : revu aussitôt (un retour sur l'onglet reprend l'affichage).
        view.IsVisibleChanged += (_, _) => UpdateRdpDisplayPause();
        if (_rdpPauseTimer is null)
        {
            _rdpPauseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _rdpPauseTimer.Tick += (_, _) => UpdateRdpDisplayPause();
            StateChanged += (_, _) => UpdateRdpDisplayPause();
        }

        _rdpPauseTimer.Start();
    }

    private void UpdateRdpDisplayPause()
    {
        if (_rdpViews.Count == 0)
        {
            _rdpPauseTimer?.Stop();
            _rdpHiddenSince.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        var idle = UserIdle();
        // Fenêtre réduite ou session Windows verrouillée : aucune session n'est visible.
        bool windowShown = IsVisible && WindowState != WindowState.Minimized && !_windowsLocked;
        foreach (var view in _rdpViews)
        {
            var session = view.Session;
            bool shown = windowShown && view.IsVisible;
            var hiddenFor = TimeSpan.Zero;
            if (shown)
            {
                _rdpHiddenSince.Remove(session);
            }
            else if (_rdpHiddenSince.TryGetValue(session, out var since))
            {
                hiddenFor = now - since;
            }
            else
            {
                _rdpHiddenSince[session] = now;
            }

            // Écran gardé pour une intervention ou montré à quelqu'un : jamais en pause tant qu'il est affiché.
            bool watched = view.InterventionRecording || Sharing.ShareOf(session) is not null;
            session.SetDisplayPause(_settings.PauseRdpDisplay
                ? RdpDisplayPause.Decide(shown, hiddenFor, idle, watched)
                : RdpDisplayPauseReason.None);
        }

        foreach (var gone in _rdpHiddenSince.Keys.Where(s => _rdpViews.All(v => v.Session != s)).ToList())
        {
            _rdpHiddenSince.Remove(gone);
        }
    }
}
