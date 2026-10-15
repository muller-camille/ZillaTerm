using ZillaTerm.Core.Rdp;

namespace ZillaTerm.Core.Tests.Rdp;

/// <summary>
/// Pause de l'affichage d'une session Bureau à distance : session invisible depuis une minute, ou poste inactif depuis
/// 15 minutes (session affichée et pas partagée) ; reprise aussitôt.
/// </summary>
public class RdpDisplayPauseTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void ShownAndUsedSessionIsNeverPaused()
    {
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(shown: true, TimeSpan.Zero, TimeSpan.Zero, watched: false));
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(true, TimeSpan.FromHours(3), RdpDisplayPause.IdleDelay - Second, false));
    }

    [Fact]
    public void HiddenSessionIsPausedAfterTheDelayNotBefore()
    {
        // Passer d'un onglet à l'autre : rien tant que la session n'est pas restée invisible une minute.
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(false, RdpDisplayPause.HiddenDelay - Second, TimeSpan.Zero, false));
        Assert.Equal(RdpDisplayPauseReason.Hidden, RdpDisplayPause.Decide(false, RdpDisplayPause.HiddenDelay, TimeSpan.Zero, false));
    }

    [Fact]
    public void HiddenSessionIsPausedEvenWhenWatched()
    {
        // Le partage ne montre que l'onglet affiché : rien à montrer d'une session invisible.
        Assert.Equal(RdpDisplayPauseReason.Hidden, RdpDisplayPause.Decide(false, TimeSpan.FromMinutes(5), TimeSpan.Zero, watched: true));
    }

    [Fact]
    public void ShownSessionIsPausedWhenTheComputerIsIdle()
    {
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(true, TimeSpan.Zero, RdpDisplayPause.IdleDelay - Second, false));
        Assert.Equal(RdpDisplayPauseReason.Idle, RdpDisplayPause.Decide(true, TimeSpan.Zero, RdpDisplayPause.IdleDelay, false));
    }

    [Fact]
    public void SharedSessionIsNotPausedWhileShown()
    {
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(true, TimeSpan.Zero, TimeSpan.FromHours(2), watched: true));
    }

    [Fact]
    public void ActivityResumesAtOnce()
    {
        // Retour sur l'onglet ou première action : reprise immédiate, sans délai.
        Assert.Equal(RdpDisplayPauseReason.None, RdpDisplayPause.Decide(true, TimeSpan.FromHours(1), TimeSpan.Zero, false));
    }
}
