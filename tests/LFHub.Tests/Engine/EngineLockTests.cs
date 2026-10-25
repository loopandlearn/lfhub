namespace LFHub.Tests.Engine;

/// One instance alone runs the engine; the others stand by until the engine lock is free.
public class EngineLockTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WhileAnotherInstanceHoldsTheLock_TheEngineStandsByAndFollowsNoOne()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        h.Store.EngineLockedElsewhere = true;
        await h.Engine.StartAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => h.Engine.StandingBy);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(h.Engine.Status(h.Patient.Id));
        Assert.True(h.Engine.IsTicking);

        h.Store.EngineLockedElsewhere = false;
        await WaitUntilAsync(() => h.Engine.Status(h.Patient.Id) is not null);
        Assert.False(h.Engine.StandingBy);
        await h.Engine.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LostLock_StopsTheEngineUntilTheLockIsTakenAgain()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        await h.Engine.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => h.Engine.Status(h.Patient.Id) is not null);

        h.Store.EngineLockedElsewhere = true;
        h.Store.EngineLock!.Held = false;
        h.Engine.Kick(h.Patient.Id);
        await WaitUntilAsync(() => h.Engine.Status(h.Patient.Id) is null);
        Assert.Contains(h.Logs.Logger.Lines, l => l.Contains("Engine lock lost", StringComparison.Ordinal));
        await WaitUntilAsync(() => h.Engine.StandingBy);

        h.Store.EngineLockedElsewhere = false;
        await WaitUntilAsync(() => h.Engine.Status(h.Patient.Id) is not null);
        await h.Engine.StopAsync(TestContext.Current.CancellationToken);
    }
}
