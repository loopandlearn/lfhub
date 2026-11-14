using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LFHub.Apns;

namespace LFHub.Tests.Apns;

/// How the connections to APNs choose among the host's addresses.
public sealed class ApnsConnectorTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2620:149:149::1");
    private static readonly IPAddress V6b = IPAddress.Parse("2620:149:149::2");
    private static readonly IPAddress V4a = IPAddress.Parse("17.188.0.1");
    private static readonly IPAddress V4b = IPAddress.Parse("17.188.0.2");

    [Fact]
    public void Interleave_AlternatesFamiliesFromTheFirstAddress()
    {
        Assert.Equal([V6a, V4a, V6b, V4b], ApnsConnector.Interleave([V6a, V6b, V4a, V4b]));
        Assert.Equal([V4a, V6a, V4b], ApnsConnector.Interleave([V4a, V4b, V6a]));
        Assert.Empty(ApnsConnector.Interleave([]));
    }

    [Fact]
    public async Task AddressThatDoesNotAnswer_DoesNotHoldBackTheNext()
    {
        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        var (socket, address) = await ApnsConnector.ConnectFirstAsync([V6a, V4a], 443,
            (a, _, ct) => a.AddressFamily == AddressFamily.InterNetworkV6 ? Hang(ct) : Connected(a),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10), failures, CancellationToken.None);
        using (socket)
        {
            Assert.Equal(V4a, address);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Empty(failures);
        }
    }

    [Fact]
    public async Task FailedAddress_StartsTheNextAtOnce()
    {
        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        var (socket, address) = await ApnsConnector.ConnectFirstAsync([V6a, V4a], 443,
            (a, _, _) => a.Equals(V6a) ? Task.FromException<Socket>(new SocketException((int)SocketError.NetworkUnreachable)) : Connected(a),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), failures, CancellationToken.None);
        using (socket)
        {
            Assert.Equal(V4a, address);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal([$"{V6a}: NetworkUnreachable"], failures);
        }
    }

    [Fact]
    public async Task NoAddressAnswers_FailsWithEachReason()
    {
        var failures = new List<string>();
        await Assert.ThrowsAsync<SocketException>(() => ApnsConnector.ConnectFirstAsync([V6a, V4a], 443,
            (a, _, ct) => a.Equals(V6a) ? Hang(ct) : Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused)),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), failures, CancellationToken.None));
        Assert.Equal(2, failures.Count);
        Assert.Contains($"{V4a}: ConnectionRefused", failures);
        Assert.Contains($"{V6a}: no answer within 200 ms", failures);
    }

    private static async Task<Socket> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new UnreachableException();
    }

    private static Task<Socket> Connected(IPAddress address) =>
        Task.FromResult(new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));
}
