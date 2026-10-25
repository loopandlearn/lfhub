using System.Text;
using System.Text.Json.Nodes;

namespace LFHub.Nightscout;

/// <summary>
/// Read-only Nightscout REST calls, with URLs built the way LoopFollow's
/// NightscoutUtils.constructURL builds them: the endpoint replaces the base URL's
/// path, the token goes in the query as `token`.
/// </summary>
public sealed class NightscoutRest
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _origin;
    private readonly string? _token;

    public NightscoutRest(HttpClient http, string baseUrl, string? token)
    {
        _http = http;
        var uri = new Uri(baseUrl.Trim());
        _origin = uri.GetLeftPart(UriPartial.Authority);
        _token = string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// GETs `endpoint` and parses the body as JSON whatever the status, as LoopFollow
    /// decodes any body it gets; throws on a transport error or a body that is not JSON.
    /// </summary>
    public async Task<JsonNode?> GetAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        var sb = new StringBuilder(_origin).Append(endpoint);
        var first = true;
        void Add(string key, string value)
        {
            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        if (_token is not null) Add("token", _token);
        foreach (var (key, value) in query) Add(key, value);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        return JsonNode.Parse(body);
    }
}
