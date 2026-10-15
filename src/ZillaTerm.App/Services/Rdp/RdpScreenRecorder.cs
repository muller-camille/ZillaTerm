using System.ComponentModel;
using System.Runtime.InteropServices;
using ZillaTerm.Core.Diagnostics;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.App.Services.Rdp;

/// <summary>
/// Écran d'une session Bureau à distance gardé pendant l'enregistrement d'une intervention, comme le protocole RDP
/// l'envoie : l'onglet affiché est capturé chaque seconde et seules les tuiles modifiées vont au journal
/// (<see cref="ScreenTileEncoder"/>). Au-delà de <see cref="InterventionRecorder.ScreenThrottleBytes"/> de journal, une
/// capture toutes les <see cref="InterventionRecorder.ScreenThrottledInterval"/>.
/// </summary>
/// <remarks>
/// Les captures et leur encodage se font sur un fil à part, pas sur celui de l'interface. Seul l'onglet affiché est
/// capturé (<see cref="Show"/>) : un onglet en arrière-plan ou une fenêtre réduite n'a rien à montrer. De retour sur
/// l'onglet, ce qui a changé entre-temps est gardé comme tout autre changement. Une pause de l'affichage
/// (<see cref="SetPause"/>) est marquée dans l'enregistrement, avec sa raison : le lecteur du rapport l'affiche sur l'écran
/// et sur la frise ; l'écran revient entier à la reprise.
/// </remarks>
internal sealed class RdpScreenRecorder : IDisposable
{
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(1);

    private readonly RdpSession _session;
    private readonly InterventionRecorder _recorder;
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _slot;
    private int _disposed;

    // Fil des captures seulement.
    private ScreenTileEncoder? _encoder;
    private ScreenPause _marked;
    private string? _journal;
    private DateTime _lastCapture;
    private byte[]? _buffer;

    public RdpScreenRecorder(RdpSession session, InterventionRecorder recorder)
    {
        _session = session;
        _recorder = recorder;
        _ = RunAsync(_stop.Token);
    }

    /// <summary>
    /// Bureau de la session affiché (vrai) ou non (onglet en arrière-plan, connexion en cours, fin de session), avec la
    /// fenêtre de son emplacement (fil de l'interface).
    /// </summary>
    public void Show(bool showing, IntPtr slot) => Interlocked.Exchange(ref _slot, showing ? slot : IntPtr.Zero);

    /// <summary>Affichage de la session en pause, et pourquoi, ou repris (fil de l'interface) : marqué à la capture suivante.</summary>
    public void SetPause(ScreenPause pause) => Volatile.Write(ref _pause, (int)pause);

    private int _pause;

    public void Dispose()
    {
        Interlocked.Exchange(ref _slot, IntPtr.Zero);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(CaptureInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    Capture();
                }
                catch (Exception e) when (e is ExternalException or Win32Exception or ArgumentException or InvalidOperationException)
                {
                    // Capture impossible cette fois (fenêtre fermée entre-temps, GDI+) : la suivante réessaie.
                    DebugLog.Write("intervention", "Capture de l'écran Bureau à distance impossible", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session fermée.
        }
    }

    private void Capture()
    {
        var journal = _recorder.JournalPath;
        var now = DateTime.UtcNow;
        // Pause de l'affichage, ou sa fin : marquée dans l'enregistrement de ce journal (pas de bridage : une marque ne
        // pèse rien), même onglet en arrière-plan.
        var pause = (ScreenPause)Volatile.Read(ref _pause);
        if (pause != _marked)
        {
            if (journal is not null && journal == _journal && _encoder?.Mark(now, pause) is { } mark)
            {
                _recorder.RecordScreen(journal, _session.Label, mark, now);
            }

            _marked = pause;
        }

        var slot = Volatile.Read(ref _slot);
        // Plein écran du contrôle Bureau à distance : l'emplacement de l'onglet est vide, le bureau est ailleurs. Session
        // qui ne répond plus : sa fenêtre pourrait bloquer la capture. Affichage en pause : rien à capturer.
        if (slot == IntPtr.Zero || _session.IsFullScreen || _session.IsNotResponding || journal is null || pause != ScreenPause.None)
        {
            return;
        }

        if (_recorder.UpdateScreenThrottle() && now - _lastCapture < InterventionRecorder.ScreenThrottledInterval)
        {
            return;
        }

        if (WindowCapture.CaptureChildPixels(slot, _buffer) is not { } pixels
            || pixels.Width > ScreenTileEncoder.MaxSide || pixels.Height > ScreenTileEncoder.MaxSide)
        {
            return;
        }

        _buffer = pixels.Bgra;
        _lastCapture = now;
        // Nouvel enregistrement : nouvel enregistrement d'écran, qui commence par une image clé. L'encodeur voit chaque
        // capture, même sans changement : un reste sous le seuil de bruit se rattrape une fois l'écran immobile.
        if (_encoder is null || journal != _journal)
        {
            // Numéro d'enregistrement d'écran unique dans le journal, pour que le rapport sépare les sessions.
            _encoder = new ScreenTileEncoder(_recorder.NextScreenStream());
            _journal = journal;
        }

        int stride = pixels.Width * 4;
        if (_encoder.Encode(now, pixels.Bgra.AsSpan(0, stride * pixels.Height), pixels.Width, pixels.Height, stride) is { } frame)
        {
            _recorder.RecordScreen(journal, _session.Label, frame, now);
        }
    }
}
