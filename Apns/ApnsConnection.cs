using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LFHub.Apns;

/// <summary>
/// One TCP connection to APNs, numbered in the order the hub opened them. It records when it
/// last sent and last received bytes (requests, answers and HTTP/2 pings alike) and logs both
/// with its age and traffic when it closes. A write to a connection the network has dropped
/// still succeeds locally, so the time since APNs last sent anything is what shows a dead one.
/// </summary>
internal sealed class ApnsConnection : Stream
{
    private static int _lastId;
    private static readonly ConcurrentDictionary<int, ApnsConnection> Open = new();

    private readonly Stream _inner;
    private readonly ILogger _log;
    private readonly Func<long> _now;
    private readonly long _openedAt;
    private long _lastSent;
    private long _lastReceived;
    private long _sent;
    private long _received;
    private int _closed;

    public int Id { get; }
    public string Host { get; }
    public string Family { get; }

    /// <param name="now">Milliseconds on a monotonic clock.</param>
    internal ApnsConnection(Stream inner, string host, IPAddress address, ILogger log, Func<long>? now = null)
    {
        _inner = inner;
        _log = log;
        _now = now ?? (() => Environment.TickCount64);
        _openedAt = _lastSent = _lastReceived = _now();
        Id = Interlocked.Increment(ref _lastId);
        Host = host;
        Family = FamilyOf(address);
        Open[Id] = this;
    }

    public TimeSpan Age => TimeSpan.FromMilliseconds(_now() - _openedAt);
    public TimeSpan SinceSent => TimeSpan.FromMilliseconds(_now() - Interlocked.Read(ref _lastSent));
    public TimeSpan SinceReceived => TimeSpan.FromMilliseconds(_now() - Interlocked.Read(ref _lastReceived));

    /// <summary>"sandbox" or "production", from the APNs host.</summary>
    public string Target => Host.Contains("sandbox", StringComparison.Ordinal) ? "sandbox" : "production";

    /// <summary>
    /// The connections open now, as "#3 sandbox IPv6 open 10 min 46 s, sent 3 s ago, received 65 s ago";
    /// "none" without any.
    /// </summary>
    public static string DescribeOpen()
    {
        var open = Open.Values.OrderBy(c => c.Id)
            .Select(c => $"#{c.Id} {c.Target} {c.Family} open {Span(c.Age)}, sent {Span(c.SinceSent)} ago, received {Span(c.SinceReceived)} ago")
            .ToList();
        return open.Count > 0 ? string.Join("; ", open) : "none";
    }

    internal static string FamilyOf(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

    /// <summary>"850 ms", "65 s" (seconds up to two minutes) or "10 min 46 s".</summary>
    internal static string Span(TimeSpan t) => t.TotalSeconds < 1
        ? $"{(long)t.TotalMilliseconds} ms"
        : t.TotalSeconds < 120
            ? $"{(long)t.TotalSeconds} s"
            : $"{(long)t.TotalMinutes} min {t.Seconds} s";

    private void Received(int n)
    {
        if (n <= 0) return;
        Interlocked.Add(ref _received, n);
        Interlocked.Exchange(ref _lastReceived, _now());
    }

    private void Sent(int n)
    {
        Interlocked.Add(ref _sent, n);
        Interlocked.Exchange(ref _lastSent, _now());
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Received(n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Received(n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        Received(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        Received(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Sent(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Sent(buffer.Length);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), ct);
        Sent(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.WriteAsync(buffer, ct);
        Sent(buffer.Length);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close(() => _inner.Dispose());
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            LogClosed();
            await _inner.DisposeAsync();
        }
        await base.DisposeAsync();
    }

    private void Close(Action dispose)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        LogClosed();
        dispose();
    }

    private void LogClosed()
    {
        Open.TryRemove(Id, out _);
        _log.LogInformation("APNs connection #{Id} to {Host} ({Family}) closed after {Age}; last sent {SinceSent} ago, last received {SinceReceived} ago; {Sent} bytes sent, {Received} received",
            Id, Host, Family, Span(Age), Span(SinceSent), Span(SinceReceived), Interlocked.Read(ref _sent),
            Interlocked.Read(ref _received));
    }
}

/// <summary>Whether an address lies in one of Apple's own networks.</summary>
internal static class AppleNetwork
{
    private static readonly IPNetwork[] Networks =
    [
        IPNetwork.Parse("17.0.0.0/8"),
        IPNetwork.Parse("2620:149::/32"),
        IPNetwork.Parse("2403:300::/32"),
        IPNetwork.Parse("2a01:b740::/32"),
    ];

    /// <summary>The well-known NAT64 prefix (RFC 6052), which embeds an IPv4 address in its last 32 bits.</summary>
    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");

    public static bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        else if (Nat64.Contains(address)) address = new IPAddress(address.GetAddressBytes()[12..]);
        return Networks.Any(n => n.Contains(address));
    }

    /// <summary>"2 IPv6 and 2 IPv4, all in Apple's network", without the addresses themselves.</summary>
    public static string Describe(IReadOnlyCollection<IPAddress> addresses)
    {
        if (addresses.Count == 0) return "no addresses";
        var v6 = addresses.Count(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        var v4 = addresses.Count - v6;
        var apple = addresses.Count(Contains);
        var where = apple == addresses.Count ? "all in Apple's network"
            : apple == 0 ? "none in Apple's network"
            : $"{apple} in Apple's network";
        return $"{v6} IPv6 and {v4} IPv4, {where}";
    }

    /// <summary>Logs what DNS answers for both APNs hosts, as counts only.</summary>
    public static async Task LogResolutionAsync(ILogger log)
    {
        foreach (var host in new[] { "api.push.apple.com", "api.sandbox.push.apple.com" })
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                log.LogInformation("APNs host {Host} resolves in {Ms} ms to {Addresses}", host,
                    watch.ElapsedMilliseconds, Describe(addresses));
            }
            catch (Exception ex)
            {
                log.LogWarning("APNs host {Host} does not resolve: {Reason}", host, ex.Message);
            }
        }
    }
}
