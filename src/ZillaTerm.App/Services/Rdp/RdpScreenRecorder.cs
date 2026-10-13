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
/// capturé (<see cref="Show"/>) : un onglet en arrière-plan ou une fenêtre réduite n'a rien à montrer. Chaque fois que
/// l'onglet est de nouveau affiché, l'écran entier est gardé (image clé).
/// </remarks>
internal sealed class RdpScreenRecorder : IDisposable
{
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromSeconds(1);

    // Numéro d'enregistrement d'écran : unique dans un journal, pour que le rapport sépare les sessions.
    private static int _streams;

    private readonly RdpSession _session;
    private readonly InterventionRecorder _recorder;
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _slot;
    private int _keyWanted;
    private int _disposed;

    // Fil des captures seulement.
    private ScreenTileEncoder? _encoder;
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
    public void Show(bool showing, IntPtr slot)
    {
        var target = showing ? slot : IntPtr.Zero;
        var previous = Interlocked.Exchange(ref _slot, target);
        if (target != IntPtr.Zero && target != previous)
        {
            Volatile.Write(ref _keyWanted, 1);
        }
    }

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
        var slot = Volatile.Read(ref _slot);
        // Plein écran du contrôle Bureau à distance : l'emplacement de l'onglet est vide, le bureau est ailleurs. Session
        // qui ne répond plus : sa fenêtre pourrait bloquer la capture.
        if (slot == IntPtr.Zero || _session.IsFullScreen || _session.IsNotResponding || _recorder.JournalPath is not { } journal)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_recorder.UpdateScreenThrottle() && now - _lastCapture < InterventionRecorder.ScreenThrottledInterval)
        {
            return;
        }

        bool key = Interlocked.Exchange(ref _keyWanted, 0) == 1;
        if (WindowCapture.CaptureChildPixels(slot, _buffer) is not { } pixels
            || pixels.Width > ScreenTileEncoder.MaxSide || pixels.Height > ScreenTileEncoder.MaxSide)
        {
            if (key)
            {
                Volatile.Write(ref _keyWanted, 1);
            }

            return;
        }

        _buffer = pixels.Bgra;
        _lastCapture = now;
        // Nouvel enregistrement : nouvel enregistrement d'écran, qui commence par une image clé.
        if (_encoder is null || journal != _journal)
        {
            _encoder = new ScreenTileEncoder(Interlocked.Increment(ref _streams));
            _journal = journal;
        }

        int stride = pixels.Width * 4;
        if (_encoder.Encode(now, pixels.Bgra.AsSpan(0, stride * pixels.Height), pixels.Width, pixels.Height, stride, key) is { } frame)
        {
            _recorder.RecordScreen(journal, _session.Label, frame, now);
        }
    }
}
