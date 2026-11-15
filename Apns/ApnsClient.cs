using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LFHub.Db;

namespace LFHub.Apns;

/// <param name="Code">HTTP status from APNs; 0 when the request never got an answer.</param>
/// <param name="Ms">From the first attempt to APNs' answer; 0 when there was none.</param>
/// <param name="Attempt">The attempt APNs answered; 0 when none was answered.</param>
public sealed record PushResult(bool Delivered, string Status, bool TokenInvalid, int Code = 0, long Ms = 0,
    int Attempt = 0)
{
    /// <summary>APNs rejected the token as unregistered or invalid: 410, <c>Unregistered</c> or <c>BadDeviceToken</c>.</summary>
    public bool TokenGone => !Delivered && (Code == 410 ||
                                            Status.EndsWith(" Unregistered", StringComparison.Ordinal) ||
                                            Status.EndsWith(" BadDeviceToken", StringComparison.Ordinal));

    /// <summary>The push got no answer from APNs: it could not connect, or the connection failed or timed out.</summary>
    public bool TransportFailed => IsTransportStatus(Status);

    /// <summary>A push status (alone, or after a prefix such as "start ") that says the push got no answer.</summary>
    public static bool IsTransportStatus(string? status) =>
        status?.Contains("transport: ", StringComparison.Ordinal) == true;
}

/// <summary>
/// Token-based APNs over HTTP/2. Each device brings its own team's .p8, so
/// provider JWTs are cached per (team, key id, SHA-256 of the key) and reused for
/// 50 minutes, inside Apple's one-hour validity. Two devices that upload different
/// keys under the same key id get a JWT each.
/// A push that gets no answer within <see cref="AttemptTimeout"/> is sent once more. Connections
/// come from <see cref="ApnsConnector"/>; HTTP/2 pings while a push waits close a connection that
/// stopped answering, so the next attempt opens a new one. A connection without a push for
/// <see cref="IdleTimeout"/> is closed, so pushes do not wait on a connection that a firewall or
/// NAT on the way has silently forgotten.
/// </summary>
public sealed class ApnsClient : IApnsClient, IDisposable
{
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan MinJwtRenewal = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(7);
    /// The handler closes idle connections on a timer that runs every quarter of this, so a
    /// connection is closed 30 to 37.5 s after its last push.
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private const int Attempts = 2;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly TimeSpan _attemptTimeout;

    private readonly ConcurrentDictionary<(string Team, string KeyId, string KeyHash), (string Jwt, DateTime IssuedAt)> _jwts = new();

    public ApnsClient(TimeProvider time, ILogger<ApnsClient> log)
        : this(Handler(log), time, log, AttemptTimeout)
    {
        LogProxy();
        _ = AppleNetwork.LogResolutionAsync(log);
    }

    /// <param name="handler">The transport to APNs; the client owns it.</param>
    internal ApnsClient(HttpMessageHandler handler, TimeProvider time, ILogger log, TimeSpan attemptTimeout)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _time = time;
        _log = log;
        _attemptTimeout = attemptTimeout;
    }

    internal static SocketsHttpHandler Handler(ILogger log) => new()
    {
        PooledConnectionIdleTimeout = IdleTimeout,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(6),
        ConnectCallback = new ApnsConnector(log).ConnectAsync,
        KeepAlivePingDelay = TimeSpan.FromSeconds(3),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(3),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    };

    /// Logs the proxy the environment routes APNs through, if any.
    private void LogProxy()
    {
        foreach (var host in new[] { "api.push.apple.com", "api.sandbox.push.apple.com" })
        {
            var uri = new Uri($"https://{host}/");
            var proxy = HttpClient.DefaultProxy.GetProxy(uri);
            if (proxy is not null && proxy != uri)
                _log.LogInformation("APNs requests to {Host} go through the proxy {Proxy}", host, proxy.GetLeftPart(UriPartial.Authority));
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public void Dispose() => _http.Dispose();

    public Task<PushResult> SendBadgeAsync(ApnsConfig cfg, int badge, CancellationToken ct) =>
        SendAsync(cfg, cfg.Token, cfg.BundleId, new JsonObject { ["aps"] = new JsonObject { ["badge"] = badge } },
            "alert", "10", ct);

    /// <summary>
    /// An alarm notification: time-sensitive with the alarm's sound, or a
    /// critical alert once the app has that entitlement. The snooze action comes
    /// from LoopFollow's notification category "category"; `lfhubAlarmId` tells
    /// the app which alarm it is. Repeats collapse into one notification.
    /// `mutable-content` routes it through the app's notification service
    /// extension, which also rings it as an AlarmKit alarm (iOS 26+) when the
    /// user allowed that; `lfhubSound` names the sound for that alarm.
    /// `lfhubReceipt` is the push's delivery receipt token (<see cref="Engine.AlarmReceipts"/>).
    /// </summary>
    /// <param name="ring">Ring it as an AlarmKit alarm too; false for a muted alarm.</param>
    /// <param name="ringOnce">The alarm's sound does not repeat: the system alarm stops after one play (`lfhubRingOnce`).</param>
    /// <param name="symbol">SF Symbol for the system alarm's button (`lfhubIcon`).</param>
    /// <param name="category">"category" for a Snooze action, "lfhub-ack" for Acknowledge.</param>
    public Task<PushResult> SendAlarmAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle,
        string? body, string? sound, string? symbol, string category, bool critical, bool ring, bool ringOnce,
        string receipt, CancellationToken ct)
    {
        var aps = new JsonObject
        {
            ["alert"] = Alert(title, subtitle, body),
            ["category"] = category,
            ["thread-id"] = "lfhub-alarm",
            ["interruption-level"] = critical ? "critical" : "time-sensitive",
            ["mutable-content"] = 1,
        };
        if (sound is not null)
            aps["sound"] = critical
                ? new JsonObject { ["critical"] = 1, ["name"] = sound, ["volume"] = 1.0 }
                : sound;
        var payload = new JsonObject
        {
            ["aps"] = aps,
            ["lfhubAlarmId"] = alarmId.ToString().ToUpperInvariant(),
            ["lfhubAlarmKit"] = ring ? 1 : 0,
            ["lfhubReceipt"] = receipt,
        };
        if (sound is not null) payload["lfhubSound"] = sound;
        if (ring && ringOnce) payload["lfhubRingOnce"] = 1;
        if (symbol is not null) payload["lfhubIcon"] = symbol;
        // An alarm that could not be delivered within five minutes is not delivered late;
        // a repeat or a later check sends a current one.
        var headers = new Dictionary<string, string>
        {
            ["apns-collapse-id"] = $"lfhub-alarm-{alarmId}",
            ["apns-expiration"] = _time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return SendAsync(cfg, cfg.Token, cfg.BundleId, payload, "alert", "10", ct, headers);
    }

    /// <summary>
    /// Tells the app's notification service extension to stop the system alarm for
    /// `alarmId`, at priority 10 so it is not held back. It arrives as a passive notification
    /// that replaces the alarm's own.
    /// </summary>
    public Task<PushResult> SendAlarmStopAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle,
        string? body, AlarmStopReason reason, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["apns-collapse-id"] = $"lfhub-alarm-{alarmId}" };
        return SendAsync(cfg, cfg.Token, cfg.BundleId, AlarmStop(alarmId, title, subtitle, body, reason), "alert",
            "10", ct, headers);
    }

    /// <summary>
    /// The stop push's payload. For a cleared condition its subtitle is "Cleared"; for an
    /// alarm that has rung out or was silenced it carries the alarm's own title, subtitle
    /// and body, so Notification Center keeps the alarm's text.
    /// </summary>
    internal static JsonObject AlarmStop(Guid alarmId, string title, string subtitle, string? body,
        AlarmStopReason reason)
    {
        var aps = new JsonObject
        {
            ["alert"] = reason == AlarmStopReason.Cleared ? Alert(title, "Cleared", null) : Alert(title, subtitle, body),
            ["thread-id"] = "lfhub-alarm",
            ["interruption-level"] = "passive",
            ["mutable-content"] = 1,
        };
        return new JsonObject { ["aps"] = aps, ["lfhubStopAlarmId"] = alarmId.ToString().ToUpperInvariant() };
    }

    /// <summary>
    /// LoopFollow's persistent "Latest BG" notification: a plain notification
    /// with the default sound, sent once per new reading.
    /// </summary>
    public Task<PushResult> SendLatestBgAsync(ApnsConfig cfg, string subtitle, CancellationToken ct)
    {
        var aps = new JsonObject
        {
            ["alert"] = Alert("Latest BG", subtitle, null),
            ["category"] = "category",
            ["thread-id"] = "lfhub-latest-bg",
            ["sound"] = "default",
        };
        var headers = new Dictionary<string, string> { ["apns-collapse-id"] = "lfhub-latest-bg" };
        return SendAsync(cfg, cfg.Token, cfg.BundleId, new JsonObject { ["aps"] = aps }, "alert", "10", ct, headers);
    }

    /// <summary>
    /// Speak BG: a notification whose <c>lfhubSpeak</c> (the new reading and the one before
    /// it in mg/dL, the speak language and whether the app shows mmol/L) the app's notification
    /// service extension speaks.
    /// </summary>
    public Task<PushResult> SendSpeakAsync(ApnsConfig cfg, string title, int current, int previous, string language,
        bool mmol, DateTime nowUtc, CancellationToken ct)
    {
        var (payload, headers) = Speak(title, current, previous, language, mmol, nowUtc);
        return SendAsync(cfg, cfg.Token, cfg.BundleId, payload, "alert", "10", ct, headers);
    }

    /// <summary>
    /// The speak push's payload and its collapse and expiration headers; it expires after
    /// 5 minutes, since a late speech is of no use.
    /// </summary>
    internal static (JsonObject Payload, Dictionary<string, string> Headers) Speak(string title, int current,
        int previous, string language, bool mmol, DateTime nowUtc)
    {
        var unix = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["alert"] = new JsonObject { ["title"] = title, ["body"] = "" },
                ["sound"] = "default",
                ["interruption-level"] = "active",
                ["thread-id"] = "lfhub-speak",
                ["mutable-content"] = 1,
            },
            ["lfhubSpeak"] = new JsonObject
            {
                ["current"] = current,
                ["previous"] = previous,
                ["language"] = language,
                ["mmol"] = mmol,
            },
        };
        var headers = new Dictionary<string, string>
        {
            ["apns-collapse-id"] = "lfhub-speak",
            ["apns-expiration"] = (unix + 5 * 60).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return (payload, headers);
    }

    private static JsonObject Alert(string title, string subtitle, string? body)
    {
        var alert = new JsonObject { ["title"] = title, ["subtitle"] = subtitle };
        if (body is { Length: > 0 }) alert["body"] = body;
        return alert;
    }

    /// <summary>
    /// Sends one Live Activity event (update, start or end) with the headers
    /// LoopFollow's own APNSClient uses. <paramref name="token"/> is the
    /// activity's push token, or the push-to-start token for a start.
    /// </summary>
    public Task<PushResult> SendLiveActivityAsync(ApnsConfig cfg, string token, JsonObject aps,
        CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null) =>
        SendAsync(cfg, token, $"{cfg.BundleId}.push-type.liveactivity", new JsonObject { ["aps"] = aps },
            "liveactivity", "10", ct, headers);

    private async Task<PushResult> SendAsync(ApnsConfig cfg, string deviceToken, string topic, JsonObject payload,
        string pushType, string priority, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        string jwt;
        try
        {
            jwt = GetJwt(cfg);
        }
        catch (Exception ex)
        {
            return new PushResult(false, $"key error: {ex.Message}", false);
        }

        var failures = new List<string>();
        string? openAtFailure = null;
        var total = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            // A header value or token the request does not accept fails this push only.
            HttpRequestMessage req;
            try
            {
                req = Request(cfg, deviceToken, topic, payload, pushType, priority, jwt, headers);
            }
            catch (Exception ex)
            {
                return new PushResult(false, $"request: {ex.Message}", false);
            }

            using (req)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_attemptTimeout);
                var openBefore = ApnsConnection.DescribeOpen();
                var watch = Stopwatch.StartNew();
                try
                {
                    using var resp = await _http.SendAsync(req, deadline.Token);
                    if (failures.Count > 0)
                        _log.LogWarning("APNs answered {Host} push on attempt {Attempt}; failed first: {Failures}; connections before the failed attempt: {Open}",
                            req.RequestUri!.Host, attempt, string.Join("; ", failures), openAtFailure);
                    if (resp.IsSuccessStatusCode)
                        return new PushResult(true, "200", false, 200, total.ElapsedMilliseconds, attempt);

                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var reason = TryReason(body);
                    var code = (int)resp.StatusCode;
                    if (code == 403) DropJwt(cfg, reason);
                    var tokenInvalid = code is 404 or 410 || reason is "BadDeviceToken" or "Unregistered" or "DeviceTokenNotForTopic";
                    return new PushResult(false, $"{code} {reason}", tokenInvalid, code, total.ElapsedMilliseconds, attempt);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures.Add(TransportFailure(ex, deadline.IsCancellationRequested, watch.Elapsed));
                    openAtFailure ??= openBefore;
                }
            }
        }
        _log.LogWarning("APNs push got no answer; connections before the first failed attempt: {Open}", openAtFailure);
        return new PushResult(false, $"transport: {string.Join("; then ", failures)}", false);
    }

    /// <summary>
    /// What went wrong on the way to APNs: no answer in time, or the request error and its
    /// innermost message, with how long the attempt took.
    /// </summary>
    internal static string TransportFailure(Exception ex, bool timedOut, TimeSpan elapsed)
    {
        var ms = (long)elapsed.TotalMilliseconds;
        if (timedOut) return $"no answer within {ms} ms";
        var error = FindRequestError(ex);
        var message = ex.GetBaseException().Message;
        return error is { } e and not HttpRequestError.Unknown
            ? $"{e}: {message} ({ms} ms)"
            : $"{message} ({ms} ms)";
    }

    private static HttpRequestError? FindRequestError(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is HttpRequestException h) return h.HttpRequestError;
        return null;
    }

    private static HttpRequestMessage Request(ApnsConfig cfg, string deviceToken, string topic, JsonObject payload,
        string pushType, string priority, string jwt, IReadOnlyDictionary<string, string>? headers)
    {
        var host = cfg.Environment == "production" ? "api.push.apple.com" : "api.sandbox.push.apple.com";
        var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        try
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
            req.Headers.Add("apns-topic", topic);
            req.Headers.Add("apns-push-type", pushType);
            req.Headers.Add("apns-priority", priority);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
                req.Headers.Add(name, value);
            return req;
        }
        catch
        {
            req.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Drops the cached JWT after a 403, so the next push signs a new one. Apple refuses a
    /// key's tokens when they are renewed more often than every 20 minutes, so a younger JWT
    /// is kept unless Apple said it expired; a wrong key fails again with a new token anyway.
    /// </summary>
    private void DropJwt(ApnsConfig cfg, string reason)
    {
        var cacheKey = CacheKey(cfg);
        if (_jwts.TryGetValue(cacheKey, out var cached) && RenewsJwtAfter403(UtcNow - cached.IssuedAt, reason))
            _jwts.TryRemove(cacheKey, out _);
    }

    internal static bool RenewsJwtAfter403(TimeSpan jwtAge, string reason) =>
        reason == "ExpiredProviderToken" || jwtAge >= MinJwtRenewal;

    private static (string Team, string KeyId, string KeyHash) CacheKey(ApnsConfig cfg) =>
        (cfg.TeamId, cfg.KeyId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cfg.Key))));

    private string GetJwt(ApnsConfig cfg)
    {
        var cacheKey = CacheKey(cfg);
        var now = UtcNow;
        if (_jwts.TryGetValue(cacheKey, out var cached) && now - cached.IssuedAt < JwtLifetime)
            return cached.Jwt;

        using var ecdsa = ImportKey(cfg.Key);
        var header = B64Url(Encoding.UTF8.GetBytes(
            new JsonObject { ["alg"] = "ES256", ["kid"] = cfg.KeyId }.ToJsonString()));
        var claims = B64Url(Encoding.UTF8.GetBytes(
            new JsonObject { ["iss"] = cfg.TeamId, ["iat"] = new DateTimeOffset(now).ToUnixTimeSeconds() }
                .ToJsonString()));
        var signingInput = $"{header}.{claims}";
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        var jwt = $"{signingInput}.{B64Url(sig)}";

        _jwts[cacheKey] = (jwt, now);
        return jwt;
    }

    /// <summary>
    /// Why <paramref name="pem"/> cannot sign provider tokens, or null when it imports as a
    /// P-256 private key in PKCS #8, the form of Apple's .p8 file (with or without its PEM lines).
    /// </summary>
    public static string? KeyProblem(string pem)
    {
        try
        {
            using var ecdsa = ImportKey(pem);
            return ecdsa.ExportParameters(false).Curve.Oid?.Value == ECCurve.NamedCurves.nistP256.Oid.Value
                ? null
                : "key is not a P-256 key, which APNs requires";
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "key is not a .p8 private key (PKCS #8, base64)";
        }
    }

    private static ECDsa ImportKey(string pem)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(StripPem(pem)), out _);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static string StripPem(string pem) => string.Concat(pem
        .Replace("-----BEGIN PRIVATE KEY-----", "")
        .Replace("-----END PRIVATE KEY-----", "")
        .Where(c => !char.IsWhiteSpace(c)));

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string TryReason(string body)
    {
        try { return JsonNode.Parse(body)?["reason"]?.GetValue<string>() ?? body; }
        catch { return body; }
    }
}
