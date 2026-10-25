using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LFHub.Nightscout;

/// <summary>One Dexcom Share reading as LoopFollow's ShareClient.fetchData gives it: Unix seconds, a Nightscout-style direction.</summary>
public sealed record DexcomReading(int Sgv, double Date, string Direction);

public enum DexcomStatus
{
    Ok,
    /// A login is needed and the account is inside its post-failure back-off; nothing was sent.
    LoginBackoff,
    /// Share rejected the account or password at login; the back-off has started.
    LoginRejected,
    /// The session was rejected again right after a fresh login.
    SessionRejected,
    /// <summary>
    /// Transport error, timeout, a response that could not be decoded, or a login answer that
    /// is no id and does not reject the account or password (Share failing for a while).
    /// </summary>
    Error,
}

public sealed record DexcomResult(DexcomStatus Status, IReadOnlyList<DexcomReading>? Readings, string? Detail = null);

/// <summary>
/// Read-only Dexcom Share client following LoopFollow's ShareClient pod: the publisher
/// login (account id, then session id), ReadPublisherLatestGlucoseValues for the last
/// 24 hours, and ShareClientExtension's trend table. The session id is kept and reused,
/// and replaced only when Share rejects it. A login Share rejects for its account or
/// password blocks further logins for <see cref="LoginBackoff"/>, since Share locks accounts
/// after repeated bad logins; any other failed login is tried again at the next fetch.
/// Calls are not thread-safe; the caller serializes them.
/// </summary>
public sealed partial class DexcomShare
{
    public const string UsServer = "https://share2.dexcom.com";
    public const string NonUsServer = "https://shareous1.dexcom.com";
    public static readonly TimeSpan LoginBackoff = TimeSpan.FromMinutes(15);

    private const string ApplicationId = "d89443d2-327c-4a6f-89e5-496bbb0317db";
    private const string UserAgent = "Dexcom Share/3.0.2.11 CFNetwork/711.2.23 Darwin/14.0.0";
    private const string AuthenticatePath = "/ShareWebServices/Services/General/AuthenticatePublisherAccount";
    private const string LoginByIdPath = "/ShareWebServices/Services/General/LoginPublisherAccountById";
    private const string LatestGlucosePath = "/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues";
    private const string EmptyId = "00000000-0000-0000-0000-000000000000";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// ShareClientExtension.swift TrendTable, indexed by Share's numeric trend.
    private static readonly string[] TrendTable =
        ["NONE", "DoubleUp", "SingleUp", "FortyFiveUp", "Flat", "FortyFiveDown", "SingleDown", "DoubleDown", "NOT COMPUTABLE", "RATE OUT OF RANGE"];

    /// ShareClient.swift's map for the string-encoded Trend; anything else is 0.
    private static readonly Dictionary<string, int> TrendCodes = new()
    {
        [""] = 0,
        ["DoubleUp"] = 1,
        ["SingleUp"] = 2,
        ["FortyFiveUp"] = 3,
        ["Flat"] = 4,
        ["FortyFiveDown"] = 5,
        ["SingleDown"] = 6,
        ["DoubleDown"] = 7,
        ["NotComputable"] = 8,
        ["RateOutOfRange"] = 9,
    };

    private readonly HttpClient _http;
    private readonly string _username;
    private readonly string _password;
    private readonly string _server;
    private readonly Func<DateTime> _utcNow;
    private readonly ILogger _log;

    private string? _sessionId;
    private DateTime _loginBlockedUntil = DateTime.MinValue;

    /// <param name="server">As LoopFollow stores shareServer: "US" for the US server, anything else for the international one.</param>
    public DexcomShare(HttpClient http, string username, string password, string server, Func<DateTime> utcNow, ILogger log)
    {
        _http = http;
        _username = username;
        _password = password;
        _server = server == "US" ? UsServer : NonUsServer;
        _utcNow = utcNow;
        _log = log;
    }

    /// When the next login may be attempted, if a rejected login is holding it back.
    public DateTime? LoginBlockedUntil => _utcNow() < _loginBlockedUntil ? _loginBlockedUntil : null;

    public bool HasSession => _sessionId is not null;

    /// <summary>
    /// The newest `maxCount` readings of the last 24 hours, newest first. Logs in only
    /// when there is no session, and once more when Share rejects the session.
    /// </summary>
    public async Task<DexcomResult> FetchAsync(int maxCount, CancellationToken ct)
    {
        var loggedIn = false;
        while (true)
        {
            if (_sessionId is null)
            {
                if (await LoginAsync(ct) is { } failed) return failed;
                loggedIn = true;
            }

            var url = $"{_server}{LatestGlucosePath}?sessionId={Uri.EscapeDataString(_sessionId!)}&minutes=1440&maxCount={maxCount.ToString(CultureInfo.InvariantCulture)}";
            JsonNode? body;
            try
            {
                body = await PostAsync(url, null, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new DexcomResult(DexcomStatus.Error, null, $"glucose request: {ex.GetType().Name}");
            }

            if (body is JsonArray records)
                return Parse(records) is { } readings
                    ? new DexcomResult(DexcomStatus.Ok, readings)
                    : new DexcomResult(DexcomStatus.Error, null, "glucose records could not be decoded");

            if (!IsSessionRejection(body, out var code))
                return new DexcomResult(DexcomStatus.Error, null, $"glucose response was not a list ({code ?? "no code"})");

            _sessionId = null;
            if (loggedIn) return new DexcomResult(DexcomStatus.SessionRejected, null, code);
            _log.LogInformation("Dexcom Share rejected the session ({Code}); logging in again", code);
        }
    }

    /// <summary>The two-step publisher login. Null on success, otherwise the failure to report.</summary>
    private async Task<DexcomResult?> LoginAsync(CancellationToken ct)
    {
        var now = _utcNow();
        if (now < _loginBlockedUntil)
            return new DexcomResult(DexcomStatus.LoginBackoff, null,
                $"next login attempt not before {_loginBlockedUntil:yyyy-MM-dd HH:mm:ss} UTC");

        var (accountId, failure) = await LoginStepAsync(AuthenticatePath, "accountName", _username, ct);
        if (failure is not null) return failure;

        var (sessionId, failure2) = await LoginStepAsync(LoginByIdPath, "accountId", accountId!, ct);
        if (failure2 is not null) return failure2;

        _sessionId = sessionId;
        _log.LogInformation("Logged in to Dexcom Share");
        return null;
    }

    /// One login POST; success is a JSON string holding a non-empty id, failure a JSON object with a Code.
    private async Task<(string? Id, DexcomResult? Failure)> LoginStepAsync(string path, string idKey, string idValue, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            [idKey] = idValue,
            ["password"] = _password,
            ["applicationId"] = ApplicationId,
        };
        JsonNode? body;
        try
        {
            body = await PostAsync(_server + path, payload, ct);
        }
        catch (JsonException)
        {
            body = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, new DexcomResult(DexcomStatus.Error, null, $"login request: {ex.GetType().Name}"));
        }

        if (body is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } id && id != EmptyId)
            return (id, null);

        var code = body is JsonValue sv && sv.GetValueKind() == JsonValueKind.String
            ? sv.GetValue<string>() == EmptyId ? "account not found" : "empty id"
            : CodeOf(body) ?? "unknown";
        if (!RejectsCredentials(body))
            return (null, new DexcomResult(DexcomStatus.Error, null, $"login answered {code}"));

        _loginBlockedUntil = _utcNow() + LoginBackoff;
        _log.LogWarning(
            "Dexcom Share login rejected ({Code}); no further login attempt before {Until:yyyy-MM-dd HH:mm:ss} UTC to avoid an account lockout",
            code, _loginBlockedUntil);
        return (null, new DexcomResult(DexcomStatus.LoginRejected, null, code));
    }

    /// POSTs with ShareClient's headers and parses the body as JSON whatever the status;
    /// a body that is not JSON throws <see cref="JsonException"/>.
    private async Task<JsonNode?> PostAsync(string url, JsonObject? payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Content = new StringContent(payload?.ToJsonString() ?? "", Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        return JsonNode.Parse(text);
    }

    /// <summary>
    /// Whether a login answer rejects the account or password: the all-zero id Share gives for
    /// an account it does not know, or one of its error codes for a bad account, a bad password
    /// or too many attempts, as pydexcom reads them. Anything else (an internal error, an
    /// empty or unreadable answer) is Share failing for a while.
    /// </summary>
    private static bool RejectsCredentials(JsonNode? body)
    {
        if (body is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>() == EmptyId;
        return CodeOf(body) switch
        {
            "AccountPasswordInvalid" or "AccountNotFound" or "SSO_AuthenticatePasswordInvalid" or
                "SSO_AuthenticateAccountNotFound" or "SSO_AuthenticateMaxAttemptsExceeed" or
                "SSO_AuthenticateMaxAttemptsExceeded" => true,
            "SSO_InternalError" => body?["Message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String &&
                                   m.GetValue<string>().Contains("Cannot Authenticate", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string? CodeOf(JsonNode? body) =>
        body is JsonObject o && o["Code"] is JsonValue c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : null;

    /// Share's answer to an unknown or expired session, e.g. SessionIdNotFound or SessionNotValid.
    private static bool IsSessionRejection(JsonNode? body, out string? code)
    {
        code = CodeOf(body);
        return code is not null && code.StartsWith("Session", StringComparison.Ordinal);
    }

    /// <summary>
    /// ShareClient.fetchLastWithRetries' record decode and fetchData's mapping: an integral
    /// Value, WT as "/Date(ms)/", Trend as a string or a number. One bad record fails the fetch.
    /// </summary>
    internal static List<DexcomReading>? Parse(JsonArray records)
    {
        var result = new List<DexcomReading>(records.Count);
        foreach (var node in records)
        {
            if (node is not JsonObject r) return null;
            if (r["Value"] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
            var glucose = value.GetValue<double>();
            if (glucose != Math.Floor(glucose) || glucose is < 0 or > ushort.MaxValue) return null;
            if (r["WT"] is not JsonValue wt || wt.GetValueKind() != JsonValueKind.String) return null;
            var match = DateRegex().Match(wt.GetValue<string>());
            if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                return null;

            int trend;
            if (r["Trend"] is JsonValue t && t.GetValueKind() == JsonValueKind.String)
                trend = TrendCodes.GetValueOrDefault(t.GetValue<string>());
            else if (r["Trend"] is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.GetValue<double>() is var d && d == Math.Floor(d))
                trend = (int)Math.Clamp(d, -1, TrendTable.Length);
            else
                return null;
            if (trend < 0 || trend >= TrendTable.Length) trend = 0;

            result.Add(new DexcomReading((int)glucose, ms / 1000, TrendTable[trend]));
        }
        return result;
    }

    [GeneratedRegex(@"\((.*)\)")]
    private static partial Regex DateRegex();
}
