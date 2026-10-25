using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Db;
using Microsoft.Extensions.Time.Testing;

namespace LFHub.Tests.Apns;

/// The client's requests to APNs and its reading of the answers, against a fake APNs.
public sealed class ApnsClientTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeApnsServer _server = new();
    private readonly ApnsClient _client;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ApnsClientTests() => _client = new ApnsClient(_server, _time);

    public void Dispose()
    {
        _client.Dispose();
        _key.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApnsConfig Config(ECDsa? key = null, string bundleId = "org.example.app") =>
        new("a1b2c3d4", "sandbox", bundleId, "TEAM000000", "KEY0000000", (key ?? _key).ExportPkcs8PrivateKeyPem());

    private Task<PushResult> AlarmAsync(ApnsConfig cfg) =>
        _client.SendAlarmAsync(cfg, Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"), "Low", "70 → -20", null,
            "lfhub-Indeed.caf", "bell", "category", false, true, true, Receipt, Ct);

    private const string Receipt = "0123456789abcdef0123456789abcdef";

    /// The JWT's header and claims, after checking its ES256 signature against <paramref name="key"/>.
    private static (JsonObject Header, JsonObject Claims) VerifiedJwt(string jwt, ECDsa key)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromB64Url(parts[2]),
            HashAlgorithmName.SHA256));
        return (JsonNode.Parse(FromB64Url(parts[0]))!.AsObject(), JsonNode.Parse(FromB64Url(parts[1]))!.AsObject());
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    [Fact]
    public async Task AlarmPush_CarriesAppleHeadersThePayloadAndASignedJwt()
    {
        var result = await AlarmAsync(Config());
        Assert.Equal(new PushResult(true, "200", false, 200), result);

        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.sandbox.push.apple.com/3/device/a1b2c3d4", request.Uri.ToString());
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal("org.example.app", request.Headers["apns-topic"]);
        Assert.Equal("alert", request.Headers["apns-push-type"]);
        Assert.Equal("10", request.Headers["apns-priority"]);
        Assert.Equal("lfhub-alarm-0f0e0d0c-0b0a-0908-0706-050403020100", request.Headers["apns-collapse-id"]);
        Assert.Equal(Start.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Headers["apns-expiration"]);

        var payload = JsonNode.Parse(request.Body)!;
        Assert.Equal("Low", payload["aps"]!["alert"]!["title"]!.GetValue<string>());
        Assert.Equal("time-sensitive", payload["aps"]!["interruption-level"]!.GetValue<string>());
        Assert.Equal("lfhub-Indeed.caf", payload["aps"]!["sound"]!.GetValue<string>());
        Assert.Equal("0F0E0D0C-0B0A-0908-0706-050403020100", payload["lfhubAlarmId"]!.GetValue<string>());
        Assert.Equal(Receipt, payload["lfhubReceipt"]!.GetValue<string>());
        Assert.Equal(1, payload["lfhubRingOnce"]!.GetValue<int>());

        Assert.StartsWith("bearer ", request.Headers["authorization"], StringComparison.Ordinal);
        var (header, claims) = VerifiedJwt(request.Headers["authorization"]["bearer ".Length..], _key);
        Assert.Equal("ES256", header["alg"]!.GetValue<string>());
        Assert.Equal("KEY0000000", header["kid"]!.GetValue<string>());
        Assert.Equal("TEAM000000", claims["iss"]!.GetValue<string>());
        Assert.Equal(Start.ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
    }

    [Fact]
    public async Task ProductionAndLiveActivity_UseTheirHostAndTopic()
    {
        var cfg = Config() with { Environment = "production" };
        await _client.SendLiveActivityAsync(cfg, "beef", new JsonObject { ["event"] = "update" }, Ct);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("https://api.push.apple.com/3/device/beef", request.Uri.ToString());
        Assert.Equal("org.example.app.push-type.liveactivity", request.Headers["apns-topic"]);
        Assert.Equal("liveactivity", request.Headers["apns-push-type"]);
    }

    [Fact]
    public async Task Jwt_IsReusedWithinItsLifetimeAndSignedAgainAfter()
    {
        await AlarmAsync(Config());
        _time.Advance(TimeSpan.FromMinutes(49));
        await AlarmAsync(Config());
        _time.Advance(TimeSpan.FromMinutes(1));
        await AlarmAsync(Config());
        var jwts = _server.Requests.Select(r => r.Headers["authorization"]).ToList();
        Assert.Equal(jwts[0], jwts[1]);
        Assert.NotEqual(jwts[1], jwts[2]);
    }

    [Fact]
    public async Task DifferentKeysUnderOneKeyId_EachSignTheirOwnJwt()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AlarmAsync(Config());
        await AlarmAsync(Config(other));
        VerifiedJwt(_server.Requests[0].Headers["authorization"]["bearer ".Length..], _key);
        VerifiedJwt(_server.Requests[1].Headers["authorization"]["bearer ".Length..], other);
    }

    [Theory]
    [InlineData(410, "Unregistered", true, true)]
    [InlineData(400, "BadDeviceToken", true, true)]
    [InlineData(400, "DeviceTokenNotForTopic", true, false)]
    [InlineData(400, "BadCollapseId", false, false)]
    [InlineData(429, "TooManyRequests", false, false)]
    [InlineData(500, "InternalServerError", false, false)]
    public async Task Rejections_MapToTheirStatusAndWhetherTheTokenIsGone(int code, string reason, bool tokenInvalid,
        bool tokenGone)
    {
        _server.Respond = _ => Answer(code, reason);
        var result = await AlarmAsync(Config());
        Assert.False(result.Delivered);
        Assert.Equal($"{code} {reason}", result.Status);
        Assert.Equal(code, result.Code);
        Assert.Equal(tokenInvalid, result.TokenInvalid);
        Assert.Equal(tokenGone, result.TokenGone);
    }

    [Fact]
    public async Task A403_KeepsAYoungJwtUnlessItExpiredAndSignsANewOneAfter20Minutes()
    {
        _server.Respond = _ => Answer(403, "InvalidProviderToken");
        await AlarmAsync(Config());
        _time.Advance(TimeSpan.FromMinutes(5));
        await AlarmAsync(Config());
        _time.Advance(TimeSpan.FromMinutes(15));
        await AlarmAsync(Config());
        await AlarmAsync(Config());
        _server.Respond = _ => Answer(403, "ExpiredProviderToken");
        _time.Advance(TimeSpan.FromSeconds(1));
        await AlarmAsync(Config());
        await AlarmAsync(Config());

        var jwts = _server.Requests.Select(r => r.Headers["authorization"]).ToList();
        Assert.Equal(jwts[0], jwts[1]);
        Assert.Equal(jwts[1], jwts[2]);
        Assert.NotEqual(jwts[2], jwts[3]);
        Assert.Equal(jwts[3], jwts[4]);
        Assert.NotEqual(jwts[4], jwts[5]);
    }

    [Fact]
    public async Task TransportFailure_IsAPushResult()
    {
        _server.Respond = _ => throw new HttpRequestException("connection refused");
        var result = await AlarmAsync(Config());
        Assert.False(result.Delivered);
        Assert.Equal(0, result.Code);
        Assert.StartsWith("transport: ", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeaderValueTheRequestRefuses_IsAPushResult()
    {
        var result = await AlarmAsync(Config(bundleId: "org.example.app\nX-Other: 1"));
        Assert.False(result.Delivered);
        Assert.StartsWith("request: ", result.Status, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task KeyThatDoesNotImport_IsAPushResult()
    {
        var result = await AlarmAsync(Config() with { Key = "not a key" });
        Assert.False(result.Delivered);
        Assert.StartsWith("key error: ", result.Status, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void KeyProblem_AcceptsAP256Pkcs8KeyWithOrWithoutItsPemLines()
    {
        var pem = _key.ExportPkcs8PrivateKeyPem();
        Assert.Null(ApnsClient.KeyProblem(pem));
        Assert.Null(ApnsClient.KeyProblem(Convert.ToBase64String(_key.ExportPkcs8PrivateKey())));
        Assert.NotNull(ApnsClient.KeyProblem("p8-key-body"));
        Assert.NotNull(ApnsClient.KeyProblem(pem[..^40]));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.NotNull(ApnsClient.KeyProblem(p384.ExportPkcs8PrivateKeyPem()));
        using var rsa = RSA.Create(2048);
        Assert.NotNull(ApnsClient.KeyProblem(rsa.ExportPkcs8PrivateKeyPem()));
    }

    private static HttpResponseMessage Answer(int code, string reason) => new((HttpStatusCode)code)
    {
        Content = new StringContent($$"""{"reason":"{{reason}}"}""", Encoding.UTF8, "application/json"),
    };

    /// Keeps each request as it arrived and answers it with <see cref="Respond"/>.
    private sealed class FakeApnsServer : HttpMessageHandler
    {
        public sealed record Received(HttpMethod Method, Uri Uri, Version Version, Dictionary<string, string> Headers,
            string Body);

        public List<Received> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            Requests.Add(new Received(request.Method, request.RequestUri!, request.Version, headers,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Respond(request);
        }
    }
}
