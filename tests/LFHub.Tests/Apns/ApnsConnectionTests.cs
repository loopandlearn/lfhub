using System.Net;
using LFHub.Apns;
using LFHub.Tests.Fakes;

namespace LFHub.Tests.Apns;

/// What the hub records and logs about each connection to APNs.
public sealed class ApnsConnectionTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("2620:149:149::1");
    private static readonly IPAddress V4 = IPAddress.Parse("17.188.0.1");
    private const string Sandbox = "api.sandbox.push.apple.com";

    private long _now = 1_000_000;

    /// A connection over a 1 KB buffer: what it writes moves the position, and reads return what follows.
    private ApnsConnection Open(ListLogger log, IPAddress? address = null, Stream? inner = null, string host = Sandbox) =>
        new(inner ?? new MemoryStream(new byte[1024]), host, address ?? V6, log, () => _now);

    [Fact]
    public async Task APushOnADeadConnection_LeavesTheTimeSinceAPNsLastAnswered()
    {
        var log = new ListLogger();
        await using var c = Open(log);
        await c.WriteAsync(new byte[5], TestContext.Current.CancellationToken);
        await c.ReadExactlyAsync(new byte[5], TestContext.Current.CancellationToken);
        _now += 65_000;

        await c.WriteAsync(new byte[5], TestContext.Current.CancellationToken);
        _now += 3_000;

        Assert.Equal(TimeSpan.FromSeconds(3), c.SinceSent);
        Assert.Equal(TimeSpan.FromSeconds(68), c.SinceReceived);
        Assert.Contains($"#{c.Id} sandbox IPv6 open 68 s, sent 3 s ago, received 68 s ago", ApnsConnection.DescribeOpen());
    }

    [Fact]
    public void ReadingNothing_IsNotReceiving()
    {
        var log = new ListLogger();
        using var c = Open(log, inner: new MemoryStream());
        _now += 30_000;
        Assert.Equal(0, c.Read(new byte[4], 0, 4));
        Assert.Equal(TimeSpan.FromSeconds(30), c.SinceReceived);
    }

    [Fact]
    public void DescribeOpen_NamesTheEnvironment()
    {
        var log = new ListLogger();
        using var c = Open(log, host: "api.push.apple.com");
        Assert.Contains($"#{c.Id} production IPv6 open", ApnsConnection.DescribeOpen());
    }

    [Fact]
    public async Task Closing_LogsAgeTimesAndTrafficOnce_AndLeavesTheOpenList()
    {
        var log = new ListLogger();
        var c = Open(log, V4);
        await c.WriteAsync(new byte[120], TestContext.Current.CancellationToken);
        _now += 10_000;
        await c.ReadExactlyAsync(new byte[40], TestContext.Current.CancellationToken);
        _now += 690_000;

        c.Dispose();
        await c.DisposeAsync();

        var closed = Assert.Single(log.Lines, l => l.Contains($"APNs connection #{c.Id} "));
        Assert.Equal($"Information: APNs connection #{c.Id} to {Sandbox} (IPv4) closed after 11 min 40 s; last sent 11 min 40 s ago, last received 11 min 30 s ago; 120 bytes sent, 40 received",
            closed);
        Assert.DoesNotContain($"#{c.Id} ", ApnsConnection.DescribeOpen());
    }

    [Fact]
    public async Task ClosingAsyncFirst_AlsoLogsOnce()
    {
        var log = new ListLogger();
        var c = Open(log);
        await c.DisposeAsync();
        c.Dispose();
        Assert.Single(log.Lines, l => l.Contains($"APNs connection #{c.Id} "));
        Assert.DoesNotContain($"#{c.Id} ", ApnsConnection.DescribeOpen());
    }

    [Fact]
    public void Connections_AreNumberedInOrder()
    {
        var log = new ListLogger();
        using var a = Open(log);
        using var b = Open(log);
        Assert.True(b.Id > a.Id);
    }

    [Theory]
    [InlineData(850, "850 ms")]
    [InlineData(65_000, "65 s")]
    [InlineData(119_000, "119 s")]
    [InlineData(120_000, "2 min 0 s")]
    [InlineData(646_000, "10 min 46 s")]
    public void Span_IsShortAndReadable(long ms, string expected) =>
        Assert.Equal(expected, ApnsConnection.Span(TimeSpan.FromMilliseconds(ms)));

    [Theory]
    [InlineData("17.188.143.34", true)]
    [InlineData("2620:149:149:1022::15", true)]
    [InlineData("2403:300:a42:100::f", true)]
    [InlineData("::ffff:17.1.2.3", true)]
    [InlineData("64:ff9b::11bc:8f22", true)]
    [InlineData("64:ff9b::a00:5", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("2a00:1450:4001::1", false)]
    public void AppleNetwork_KnowsApplesRanges(string address, bool expected) =>
        Assert.Equal(expected, AppleNetwork.Contains(IPAddress.Parse(address)));

    [Fact]
    public void AppleNetwork_DescribesAddressesWithoutListingThem()
    {
        Assert.Equal("2 IPv6 and 1 IPv4, all in Apple's network",
            AppleNetwork.Describe([V6, IPAddress.Parse("2620:149:149::2"), V4]));
        Assert.Equal("0 IPv6 and 1 IPv4, none in Apple's network", AppleNetwork.Describe([IPAddress.Loopback]));
        Assert.Equal("1 IPv6 and 1 IPv4, 1 in Apple's network", AppleNetwork.Describe([V6, IPAddress.Loopback]));
        Assert.Equal("no addresses", AppleNetwork.Describe([]));
    }
}
