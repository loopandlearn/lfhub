using LFHub.Engine;

namespace LFHub.Tests.Engine;

public class StreamHealthTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    [Fact]
    public void NeverAuthorized_FailsOnceTheGraceFromStartPasses()
    {
        var health = new StreamHealth(T0);
        for (var s = 30; s <= 300; s += 30) health.Closed(T0.AddSeconds(s));
        Assert.True(health.IsHealthy(T0.AddSeconds(119), Grace));
        Assert.False(health.IsHealthy(T0.AddSeconds(121), Grace));
        Assert.False(health.IsHealthy(T0.AddMinutes(60), Grace));
    }

    [Fact]
    public void Authorized_IsHealthyUntilTheGraceAfterItDrops()
    {
        var health = new StreamHealth(T0);
        health.Authorized();
        Assert.True(health.IsHealthy(T0.AddHours(5), Grace));

        var drop = T0.AddHours(5);
        health.Closed(drop);
        // Reconnect attempts that fail keep failing; they do not restart the grace.
        health.Closed(drop.AddSeconds(5));
        health.Closed(drop.AddSeconds(15));
        health.Closed(drop.AddSeconds(45));
        health.Closed(drop.AddSeconds(75));
        health.Closed(drop.AddSeconds(105));
        Assert.True(health.IsHealthy(drop.AddSeconds(110), Grace));
        Assert.False(health.IsHealthy(drop.AddSeconds(125), Grace));

        health.Authorized();
        Assert.True(health.IsHealthy(drop.AddMinutes(10), Grace));
    }
}
