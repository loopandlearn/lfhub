using LFHub.Api;
using Microsoft.Extensions.Time.Testing;

namespace LFHub.Tests.Api;

public class SlidingWindowLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void OnePerMinute_PerKey()
    {
        var limiter = new SlidingWindowLimiter<Guid>(1, TimeSpan.FromMinutes(1), _time);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.True(limiter.TryTake(a));
        Assert.False(limiter.TryTake(a));
        Assert.True(limiter.TryTake(b));
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(limiter.TryTake(a));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.TryTake(a));
    }

    [Fact]
    public void TurnedAwayRequestsDoNotCount()
    {
        var limiter = new SlidingWindowLimiter<string>(5, TimeSpan.FromMinutes(1), _time);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryTake("claim"));
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.False(limiter.TryTake("claim"));
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(limiter.TryTake("claim"));
        Assert.False(limiter.TryTake("claim"));
    }

    [Fact]
    public void ManyKeys_AreSweptOnceTheirWindowPasses()
    {
        var limiter = new SlidingWindowLimiter<int>(1, TimeSpan.FromMinutes(1), _time);
        for (var i = 0; i < 2000; i++) Assert.True(limiter.TryTake(i));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(limiter.TryTake(-1));
        Assert.True(limiter.TryTake(0));
    }
}
