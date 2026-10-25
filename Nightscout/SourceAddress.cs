using System.Net;
using System.Net.Sockets;

namespace LFHub.Nightscout;

/// <summary>
/// Where the hub may fetch a patient's sources from. Unless private addresses are allowed
/// (<c>LFHUB_ALLOW_PRIVATE_NIGHTSCOUT=true</c>), a source host must resolve to public
/// addresses only, so a source URL cannot reach into the hub's own network: loopback,
/// private (RFC 1918), shared (100.64.0.0/10), link-local, unique-local, unspecified,
/// multicast and broadcast addresses are refused, also when embedded in an IPv6 address
/// (IPv4-mapped, NAT64 64:ff9b::/96, 6to4 2002::/16). The check runs when the URL is set
/// and again on every connection, so a name that later resolves elsewhere is refused as well.
/// </summary>
public static class SourceAddress
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224 ||
                     (b[0] == 100 && (b[1] & 0xC0) == 64) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 172 && (b[1] & 0xF0) == 16) ||
                     (b[0] == 192 && b[1] == 168));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast)
            return false;
        var v6 = address.GetAddressBytes();
        if (v6.AsSpan(0, 12).SequenceEqual(Nat64Prefix)) return IsPublic(new IPAddress(v6.AsSpan(12, 4)));
        if (v6[0] == 0x20 && v6[1] == 0x02) return IsPublic(new IPAddress(v6.AsSpan(2, 4)));
        return true;
    }

    private static readonly byte[] Nat64Prefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// Why <paramref name="host"/> may not be a source, or null. A host that does not resolve
    /// now is accepted; every connection to it is checked when it does.
    /// </summary>
    public static async Task<string?> ProblemAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await ResolveAsync(host, ct);
        }
        catch (SocketException)
        {
            return null;
        }
        return addresses.All(IsPublic) ? null : $"{host} resolves to an address that is not public";
    }

    /// <summary>
    /// The HTTP handler sources are fetched with. Connections are renewed every 15 minutes, so
    /// a changed DNS answer is followed, and without <paramref name="allowPrivate"/> each one
    /// connects only when every address of its host is public.
    /// </summary>
    public static SocketsHttpHandler Handler(bool allowPrivate) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectCallback = allowPrivate ? null : ConnectPublicAsync,
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAsync(host, ct);
        if (addresses.Length == 0 || !addresses.All(IsPublic))
            throw new HttpRequestException($"{host} resolves to an address that is not public");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
}
