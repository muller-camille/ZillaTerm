namespace ZillaTerm.Core.Tests;

public sealed class CancelOnceTests
{
    /// <summary>Annulée puis libérée une seule fois ; le jeton reste lisible et utilisable après.</summary>
    [Fact]
    public async Task TokenStaysUsableAfterTheSourceIsReleased()
    {
        var cancel = new CancelOnce();
        var token = cancel.Token;
        var pending = Task.Delay(Timeout.Infinite, token);

        cancel.Cancel();
        cancel.Cancel();

        Assert.True(cancel.IsCancellationRequested);
        Assert.True(cancel.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1000, cancel.Token));
        bool called = false;
        using (cancel.Token.Register(() => called = true))
        {
            Assert.True(called);
        }
    }
}
