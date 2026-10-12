using ZillaTerm.Core.Ssh;

namespace ZillaTerm.Core.Tests.Ssh;

public class ServerResponseWatchTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    /// <summary>Écho reçu peu après la frappe : ni demande au serveur, ni alerte, et la surveillance s'arrête.</summary>
    [Fact]
    public void EchoEndsTheWatchWithoutProbe()
    {
        var watch = new ServerResponseWatch();
        watch.Typed(At(0), 40);
        Assert.False(watch.Check(At(0.5), 40));
        Assert.False(watch.Check(At(1), 41));
        Assert.False(watch.IsWatching);
        Assert.False(watch.IsSilent);
    }

    /// <summary>
    /// Serveur muet : demande au bout de 2 s, renvoyée toutes les 3 s ; alerte au bout de 5 s, jusqu'à la réponse (ici à la
    /// demande, sans écho : saisie d'un mot de passe).
    /// </summary>
    [Fact]
    public void SilentServerIsProbedThenReported()
    {
        var watch = new ServerResponseWatch();
        watch.Typed(At(0), 40);
        Assert.False(watch.Check(At(1.5), 40));
        Assert.True(watch.Check(At(2), 40));
        Assert.False(watch.IsSilent);
        Assert.False(watch.Check(At(4.5), 40));
        // Une autre touche ne relance pas l'attente.
        watch.Typed(At(4.8), 40);
        Assert.True(watch.Check(At(5), 40));
        Assert.True(watch.IsSilent);
        Assert.Equal(TimeSpan.FromSeconds(6), watch.SilentFor(At(6)));
        Assert.False(watch.Check(At(7), 40));
        Assert.True(watch.Check(At(8), 40));

        Assert.False(watch.Check(At(8.5), 41));
        Assert.False(watch.IsSilent);
        Assert.False(watch.IsWatching);
        Assert.Equal(TimeSpan.Zero, watch.SilentFor(At(9)));
    }

    /// <summary>Après une réponse, la frappe suivante repart de zéro.</summary>
    [Fact]
    public void NextKeystrokeStartsAgain()
    {
        var watch = new ServerResponseWatch();
        watch.Typed(At(0), 40);
        Assert.True(watch.Check(At(6), 40));
        Assert.True(watch.IsSilent);
        watch.Stop();
        watch.Typed(At(10), 41);
        Assert.False(watch.Check(At(11), 41));
        Assert.False(watch.IsSilent);
        Assert.True(watch.Check(At(12), 41));
    }

    [Fact]
    public void NothingTypedNothingChecked()
    {
        var watch = new ServerResponseWatch();
        Assert.False(watch.Check(At(30), 0));
        Assert.False(watch.IsSilent);
    }
}
