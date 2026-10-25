using System.Net;
using System.Text;
using System.Web;

namespace LFHub.Tests.Fakes;

/// <summary>
/// A Nightscout that answers from functions per path, records each request, and
/// fails any host or method it was not set up for.
/// </summary>
internal sealed class FakeNightscout(string host = "ns.example.org") : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, string>> Routes { get; } = new();
    public List<string> Requests { get; } = [];

    public FakeNightscout Route(string path, string body) => Route(path, _ => body);

    public FakeNightscout Route(string path, Func<HttpRequestMessage, string> body)
    {
        Routes[path] = body;
        return this;
    }

    /// Paths whose requests never get an answer until they are cancelled.
    public HashSet<string> Hang { get; } = [];

    public int Count(string path) => Requests.Count(r => r.StartsWith(path, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.Host != host || request.Method != HttpMethod.Get)
            throw new InvalidOperationException($"unexpected {request.Method} to {uri.Host}");
        Requests.Add(uri.AbsolutePath + uri.Query);
        if (Hang.Contains(uri.AbsolutePath)) await Task.Delay(Timeout.Infinite, ct);
        var body = Routes.TryGetValue(uri.AbsolutePath, out var route) ? route(request) : "[]";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    public static string Query(HttpRequestMessage request, string key) =>
        HttpUtility.ParseQueryString(request.RequestUri!.Query)[key] ?? "";
}
