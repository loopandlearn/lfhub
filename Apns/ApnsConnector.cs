using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LFHub.Apns;

/// <summary>
/// Opens the TCP connections to APNs. The host's addresses are tried IPv6 and IPv4 in turn,
/// a new attempt starting every <see cref="Stagger"/> while the earlier ones are still pending,
/// each given at most <see cref="AttemptTimeout"/>; the first to connect is used. A network that
/// drops one address family therefore costs a moment, not a request. Each new connection is
/// numbered and logged with its address, its family, whether the address is Apple's and the
/// attempts that failed before it; <see cref="ApnsConnection"/> logs it again when it closes.
/// </summary>
internal sealed class ApnsConnector(ILogger log)
{
    internal static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    private const int MaxAttempts = 6;
    private static readonly ConcurrentDictionary<string, IPAddress[]> LastResolved = new();

    internal delegate Task<Socket> Connect(IPAddress address, int port, CancellationToken ct);

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var watch = Stopwatch.StartNew();
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await ResolveAsync(host, ct);
        var failures = new List<string>();
        try
        {
            var (socket, address) = await ConnectFirstAsync(Interleave(addresses), port, ConnectSocketAsync, Stagger,
                AttemptTimeout, failures, ct);
            var ms = watch.ElapsedMilliseconds;
            var connection = new ApnsConnection(new NetworkStream(socket, ownsSocket: true), host, address, log);
            var network = AppleNetwork.Contains(address) ? "Apple's network" : "outside Apple's network";
            if (failures.Count == 0)
                log.LogInformation("APNs connection #{Id} to {Host} at {Address} ({Family}, {Network}) in {Ms} ms",
                    connection.Id, host, address, connection.Family, network, ms);
            else
                log.LogWarning("APNs connection #{Id} to {Host} at {Address} ({Family}, {Network}) in {Ms} ms; failed first: {Failures}",
                    connection.Id, host, address, connection.Family, network, ms, string.Join("; ", failures));
            return connection;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = failures.Count > 0 ? string.Join("; ", failures) : ex.Message;
            log.LogWarning("APNs connection to {Host} failed after {Ms} ms: {Reason}", host,
                watch.ElapsedMilliseconds, reason);
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"could not connect to {host}: {reason}",
                ex);
        }
    }

    /// <summary>
    /// The host's addresses from DNS. When DNS fails or answers with none, the addresses of its
    /// last good answer are used, so a resolver outage does not hold back pushes.
    /// </summary>
    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length > 0)
            {
                LastResolved[host] = addresses;
                return addresses;
            }
            if (LastResolved.TryGetValue(host, out var last))
            {
                log.LogWarning("APNs host {Host} resolves to no addresses; using its last {Count}", host, last.Length);
                return last;
            }
            return addresses;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && LastResolved.TryGetValue(host, out var last))
        {
            log.LogWarning("APNs host {Host} does not resolve ({Reason}); using its last {Count} addresses", host,
                ex.Message, last.Length);
            return last;
        }
    }

    /// <summary>The addresses alternating by family, the family of the first one leading.</summary>
    internal static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0) return [];
        var lead = addresses.Where(a => a.AddressFamily == addresses[0].AddressFamily).ToList();
        var other = addresses.Where(a => a.AddressFamily != addresses[0].AddressFamily).ToList();
        var result = new List<IPAddress>();
        for (var i = 0; i < Math.Max(lead.Count, other.Count); i++)
        {
            if (i < lead.Count) result.Add(lead[i]);
            if (i < other.Count) result.Add(other[i]);
        }
        return result.Take(MaxAttempts).ToList();
    }

    /// <summary>
    /// Connects to the first of <paramref name="addresses"/> that answers. Attempt i starts after
    /// i × <paramref name="stagger"/>, or at once when every earlier one has failed. Each failure
    /// is added to <paramref name="failures"/> as "address: reason"; a socket that connects after
    /// another has won is closed.
    /// </summary>
    internal static async Task<(Socket Socket, IPAddress Address)> ConnectFirstAsync(IReadOnlyList<IPAddress> addresses,
        int port, Connect connect, TimeSpan stagger, TimeSpan attemptTimeout, List<string> failures,
        CancellationToken ct)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new Dictionary<Task<Socket>, IPAddress>();
        var next = 0;
        var nextStart = Task.CompletedTask;
        try
        {
            while (true)
            {
                if (next < addresses.Count && (nextStart.IsCompleted || pending.Count == 0))
                {
                    var address = addresses[next++];
                    pending[AttemptAsync(connect, address, port, attemptTimeout, race.Token)] = address;
                    nextStart = next < addresses.Count ? Task.Delay(stagger, race.Token) : Task.Delay(Timeout.Infinite, race.Token);
                }
                if (pending.Count == 0) throw new SocketException((int)SocketError.HostUnreachable);

                var done = await Task.WhenAny(pending.Keys.Append(nextStart));
                if (done == nextStart)
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }
                var attempt = (Task<Socket>)done;
                var tried = pending[attempt];
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully) return (attempt.Result, tried);
                ct.ThrowIfCancellationRequested();
                failures.Add($"{tried}: {Reason(attempt.Exception?.GetBaseException(), attemptTimeout)}");
                if (pending.Count == 0 && next >= addresses.Count)
                    throw new SocketException((int)SocketError.HostUnreachable);
            }
        }
        finally
        {
            race.Cancel();
            foreach (var other in pending.Keys)
                _ = other.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                    else _ = t.Exception;
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private static async Task<Socket> AttemptAsync(Connect connect, IPAddress address, int port, TimeSpan timeout,
        CancellationToken race)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(race);
        deadline.CancelAfter(timeout);
        try
        {
            return await connect(address, port, deadline.Token);
        }
        catch (OperationCanceledException) when (!race.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static string Reason(Exception? ex, TimeSpan timeout) => ex switch
    {
        TimeoutException => $"no answer within {timeout.TotalMilliseconds:0} ms",
        SocketException s => s.SocketErrorCode.ToString(),
        null => "failed",
        _ => ex.Message,
    };

    private static async Task<Socket> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
