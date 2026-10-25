using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;

namespace LFHub.Tests.Nightscout;

/// Dexcom Share as a source, alone and with Nightscout, against a fake Share service.
public class DexcomShareTests
{
    private const string Password = "test-password";
    private static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        public DateTime Now = Start;
    }

    private static readonly string[] BgOnlySnapshotKeys =
        ["delta", "glucose", "isNotLooping", "showRenewalOverlay", "trend", "unit", "updatedAt"];

    private static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    private static PatientState Dexcom(Clock clock, FakeDexcom dex, string password = Password, string server = "US",
        ListLogger? log = null, FakeNightscout? ns = null) =>
        new(new HttpClient(new Router(dex, ns)), ns is null ? null : "https://ns.example.org", null,
            new DexcomCredentials("test-user", password, server), log ?? new ListLogger())
        {
            UtcNow = () => clock.Now,
        };

    /// Nightscout entries at 5-minute spacing, the newest `newestAgo` before now.
    private static FakeNightscout Nightscout(Clock clock, TimeSpan newestAgo)
    {
        return new FakeNightscout().Route("/api/v1/entries.json", _ =>
        {
            var newest = Unix(clock.Now - newestAgo);
            var list = new JsonArray();
            for (var i = 0; i < 288; i++)
                list.Add(new JsonObject { ["sgv"] = 110 + i % 5, ["date"] = (long)((newest - i * 300) * 1000), ["direction"] = "Flat", ["type"] = "sgv" });
            return list.ToJsonString();
        });
    }

    [Fact]
    public async Task Login_ThenFetch_WithLoopFollowsRequests()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { NewestAge = 60, Count = 288 };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["auth", "loginById", "glucose"], dex.Paths);
        Assert.All(dex.Hosts, h => Assert.Equal("share2.dexcom.com", h));
        Assert.Equal("test-user", dex.AuthBodies[0]["accountName"]?.GetValue<string>());
        Assert.Equal(Password, dex.AuthBodies[0]["password"]?.GetValue<string>());
        Assert.Equal("d89443d2-327c-4a6f-89e5-496bbb0317db", dex.AuthBodies[0]["applicationId"]?.GetValue<string>());
        Assert.Equal(FakeDexcom.AccountId, dex.AuthBodies[1]["accountId"]?.GetValue<string>());
        Assert.Contains("minutes=1440", dex.GlucoseQueries[0], StringComparison.Ordinal);
        Assert.Contains("maxCount=288", dex.GlucoseQueries[0], StringComparison.Ordinal);
        Assert.Contains("sessionId=sess-1", dex.GlucoseQueries[0], StringComparison.Ordinal);
        Assert.True(dex.AllPost);
        Assert.True(dex.AllJsonHeaders);
        Assert.True(dex.UserAgentOk);
    }

    [Fact]
    public async Task DexcomOnly_SnapshotAndAlarmDataCarryBgOnly()
    {
        var clock = new Clock();
        var state = Dexcom(clock, new FakeDexcom(clock) { NewestAge = 60 });
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        var snap = state.BuildSnapshot(ClientSettings.Default, clock.Now)!;
        Assert.Equal(BgOnlySnapshotKeys, snap.Select(k => k.Key).Order(StringComparer.Ordinal));
        Assert.Equal("upSlight", snap["trend"]?.GetValue<string>());
        Assert.False(snap["isNotLooping"]?.GetValue<bool>());
        Assert.Equal(Unix(clock.Now) - 60, snap["updatedAt"]!.GetValue<double>(), 2);
        var alarm = state.BuildAlarmData(ClientSettings.Default, clock.Now)!;
        Assert.Equal(24, alarm.BgReadings.Count);
        Assert.Empty(alarm.PredictionData);
        Assert.Null(alarm.LastLoopTime);
        Assert.Null(alarm.Iob);
        Assert.Equal(clock.Now.AddSeconds(-60), state.LatestReading?.At);
    }

    [Fact]
    public async Task FreshReading_NextPollFollowsItsAge_AndReusesTheSession()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { NewestAge = 60 };
        var log = new ListLogger();
        var state = Dexcom(clock, dex, log: log);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        for (var i = 0; i < 8; i++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            if (clock.Now - Start >= TimeSpan.FromSeconds(250)) break;
            await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        }
        Assert.Equal(3, dex.Paths.Count);
        clock.Now = Start.AddSeconds(270);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["glucose"], dex.Paths.Skip(3));
        Assert.DoesNotContain(Password, log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-user", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredSession_LogsInAgainOnce()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { NewestAge = 60 };
        var log = new ListLogger();
        var state = Dexcom(clock, dex, server: "Other", log: log);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.All(dex.Hosts, h => Assert.Equal("shareous1.dexcom.com", h));

        dex.Paths.Clear();
        dex.ExpireSessions();
        clock.Now = clock.Now.AddMinutes(5);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["glucose", "auth", "loginById", "glucose"], dex.Paths);
        Assert.Contains("sessionId=sess-2", dex.GlucoseQueries[^1], StringComparison.Ordinal);
        Assert.NotNull(state.BuildSnapshot(ClientSettings.Default, clock.Now));

        dex.Paths.Clear();
        dex.RejectAllSessions = true;
        clock.Now = clock.Now.AddMinutes(5);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["glucose", "auth", "loginById", "glucose"], dex.Paths);
        Assert.DoesNotContain(Password, log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedLogin_BacksOffFifteenMinutes()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock);
        var log = new ListLogger();
        var state = Dexcom(clock, dex, password: "wrong-password", log: log);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["auth"], dex.Paths);
        Assert.Equal(Start.AddMinutes(15), state.DexcomLoginBlockedUntil);
        Assert.Contains(log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal)
            && l.Contains("AccountPasswordInvalid", StringComparison.Ordinal)
            && l.Contains("no further login attempt before", StringComparison.Ordinal));

        while (clock.Now.AddSeconds(30) < Start.AddMinutes(15))
        {
            clock.Now = clock.Now.AddSeconds(30);
            await state.RefreshAsync(clock.Now.Second == 0 && clock.Now.Minute % 3 == 0, TestContext.Current.CancellationToken);
        }
        Assert.Single(dex.Paths);

        clock.Now = Start.AddMinutes(15);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(["auth", "auth"], dex.Paths);
        clock.Now = clock.Now.AddMinutes(1);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(2, dex.Paths.Count);
        Assert.Null(state.BuildSnapshot(ClientSettings.Default, clock.Now));
        Assert.Null(state.BuildAlarmData(ClientSettings.Default, clock.Now));
    }

    [Fact]
    public async Task ZeroAccountId_CountsAsRejected()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { ZeroAccountId = true };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["auth"], dex.Paths);
        Assert.NotNull(state.DexcomLoginBlockedUntil);
    }

    [Theory]
    [InlineData("SSO_InternalError", "Unexpected error")]
    [InlineData("ServiceUnavailable", "x")]
    public async Task TransientErrorOnLogin_RetriesAtTheNextFetchWithoutBackOff(string code, string message)
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { AuthError = (code, message) };
        var log = new ListLogger();
        var state = Dexcom(clock, dex, log: log);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Null(state.DexcomLoginBlockedUntil);
        Assert.Contains(log.Lines, l => l.Contains(code, StringComparison.Ordinal));
        clock.Now = clock.Now.AddSeconds(60);
        dex.AuthError = null;
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["auth", "auth", "loginById", "glucose"], dex.Paths);
    }

    [Theory]
    [InlineData("SSO_AuthenticatePasswordInvalid", "x")]
    [InlineData("SSO_AuthenticateAccountNotFound", "x")]
    [InlineData("SSO_AuthenticateMaxAttemptsExceeed", "x")]
    [InlineData("SSO_InternalError", "Cannot Authenticate by AccountName")]
    public async Task CredentialErrorOnLogin_BacksOff(string code, string message)
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { AuthError = (code, message) };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(Start.AddMinutes(15), state.DexcomLoginBlockedUntil);
    }

    [Fact]
    public async Task TransportErrorOnLogin_RetriesWithoutBackOff()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { ThrowOnAuth = true };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Null(state.DexcomLoginBlockedUntil);
        clock.Now = clock.Now.AddSeconds(60);
        dex.ThrowOnAuth = false;
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(["auth", "auth", "loginById", "glucose"], dex.Paths);
    }

    [Fact]
    public async Task StaleDexcom_FallsBackToNightscout()
    {
        var clock = new Clock();
        var ns = Nightscout(clock, TimeSpan.FromMinutes(2));
        var state = Dexcom(clock, new FakeDexcom(clock) { NewestAge = 20 * 60 }, ns: ns);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, ns.Count("/api/v1/entries.json"));
        Assert.Equal(1, ns.Count("/api/v1/devicestatus.json"));
        Assert.Equal(1, ns.Count("/api/v1/profiles.json"));
        Assert.Equal(Unix(clock.Now) - 120, state.BuildSnapshot(ClientSettings.Default, clock.Now)?["updatedAt"]?.GetValue<double>());
    }

    [Fact]
    public async Task ShortDexcomHistory_IsMergedOverOlderNightscoutReadings()
    {
        var clock = new Clock();
        var ns = Nightscout(clock, TimeSpan.FromMinutes(2));
        var state = Dexcom(clock, new FakeDexcom(clock) { NewestAge = 300, Count = 24 }, ns: ns);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(Unix(clock.Now) - 300, state.BuildSnapshot(ClientSettings.Default, clock.Now)!["updatedAt"]!.GetValue<double>(), 2);

        var entries = state.Bundle()["parts"]!["entries"]!["data"]!.AsArray();
        var oldestDex = Unix(clock.Now) - 300 - 23 * 300;
        var dates = entries.Select(e => e!["date"]!.GetValue<long>() / 1000.0).ToList();
        Assert.True(dates.Count > 24);
        Assert.All(dates.Take(24), d => Assert.True(d >= oldestDex - 0.01));
        Assert.All(dates.Skip(24), d => Assert.True(d < oldestDex));
    }

    [Fact]
    public async Task RejectedDexcomLogin_ShowsNightscoutAndBacksOff()
    {
        var clock = new Clock();
        var ns = Nightscout(clock, TimeSpan.FromMinutes(2));
        var dex = new FakeDexcom(clock);
        var state = Dexcom(clock, dex, password: "wrong", ns: ns);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(Unix(clock.Now) - 120, state.BuildSnapshot(ClientSettings.Default, clock.Now)?["updatedAt"]?.GetValue<double>());
        clock.Now = clock.Now.AddMinutes(5);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Single(dex.Paths);
    }

    [Fact]
    public async Task DexcomOnly_StaleDataKeptAndPolledWithinTheMissedReadingWindow()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { NewestAge = 25 * 60 };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.NotNull(state.BuildSnapshot(ClientSettings.Default, clock.Now));
        Assert.True(state.HasDexcom);
        Assert.False(state.HasNightscout);

        // LoopFollow waits five minutes after a reading 20 minutes old; the hub checks
        // again within 270 s so the missed-reading alarm's six-minute check window holds.
        clock.Now = clock.Now.AddSeconds(240);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, dex.Paths.Count(p => p == "glucose"));
        clock.Now = clock.Now.AddSeconds(30);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(2, dex.Paths.Count(p => p == "glucose"));
    }

    [Fact]
    public async Task FailedFetch_RetriesAfterAMinuteAndKeepsTheSession()
    {
        var clock = new Clock();
        var dex = new FakeDexcom(clock) { NewestAge = 25 * 60 };
        var state = Dexcom(clock, dex);
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        dex.FailGlucose = true;
        clock.Now = clock.Now.AddSeconds(300);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        var n = dex.Paths.Count;
        clock.Now = clock.Now.AddSeconds(30);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(n, dex.Paths.Count);
        clock.Now = clock.Now.AddSeconds(30);
        await state.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(n + 1, dex.Paths.Count);
        Assert.Equal(1, dex.Paths.Count(p => p == "auth"));
    }

    [Fact]
    public void NoSource_Throws() =>
        Assert.Throws<ArgumentException>(() => new PatientState(new HttpClient(), null, null, null, new ListLogger()));

    /// Dexcom hosts go to the fake Share service, the Nightscout host to the fake Nightscout.
    private sealed class Router(FakeDexcom dex, FakeNightscout? ns) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker? _ns = ns is null ? null : new HttpMessageInvoker(ns);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            if (host.EndsWith("dexcom.com", StringComparison.Ordinal)) return dex.Handle(request);
            if (_ns is not null) return _ns.SendAsync(request, ct);
            throw new InvalidOperationException($"unexpected host {host}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _ns?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Answers the three Share endpoints the way Dexcom's service does.
    private sealed class FakeDexcom(Clock clock)
    {
        public const string AccountId = "acct-1111";
        public List<string> Paths { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<JsonObject> AuthBodies { get; } = [];
        public List<string> GlucoseQueries { get; } = [];
        public bool AllPost { get; private set; } = true;
        public bool AllJsonHeaders { get; private set; } = true;
        public bool UserAgentOk { get; private set; } = true;
        public double NewestAge { get; init; } = 60;
        public int Count { get; init; } = 288;
        public bool ZeroAccountId { get; init; }
        public bool ThrowOnAuth { get; set; }
        /// Share's error code (and message) for every login, whatever the password.
        public (string Code, string Message)? AuthError { get; set; }
        public bool RejectAllSessions { get; set; }
        public bool FailGlucose { get; set; }
        private readonly HashSet<string> _sessions = [];
        private int _sessionCounter;

        public void ExpireSessions() => _sessions.Clear();

        public async Task<HttpResponseMessage> Handle(HttpRequestMessage req)
        {
            Hosts.Add(req.RequestUri!.Host);
            AllPost &= req.Method == HttpMethod.Post;
            AllJsonHeaders &= req.Headers.Accept.ToString() == "application/json"
                && req.Content?.Headers.ContentType?.MediaType == "application/json";
            UserAgentOk &= req.Headers.UserAgent.ToString().StartsWith("Dexcom Share/3.0.2.11", StringComparison.Ordinal);
            var path = req.RequestUri.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();

            switch (path)
            {
                case "/ShareWebServices/Services/General/AuthenticatePublisherAccount":
                    {
                        Paths.Add("auth");
                        if (ThrowOnAuth) throw new HttpRequestException("connection refused");
                        if (AuthError is { } error) return Error(error.Code, error.Message);
                        var o = JsonNode.Parse(body)!.AsObject();
                        AuthBodies.Add(o);
                        if (o["password"]?.GetValue<string>() != Password) return Error("AccountPasswordInvalid");
                        return Json(ZeroAccountId ? "\"00000000-0000-0000-0000-000000000000\"" : $"\"{AccountId}\"");
                    }
                case "/ShareWebServices/Services/General/LoginPublisherAccountById":
                    {
                        Paths.Add("loginById");
                        var o = JsonNode.Parse(body)!.AsObject();
                        AuthBodies.Add(o);
                        if (o["accountId"]?.GetValue<string>() != AccountId || o["password"]?.GetValue<string>() != Password)
                            return Error("AccountPasswordInvalid");
                        var session = $"sess-{++_sessionCounter}";
                        _sessions.Add(session);
                        return Json($"\"{session}\"");
                    }
                case "/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues":
                    {
                        Paths.Add("glucose");
                        GlucoseQueries.Add(req.RequestUri.Query);
                        if (FailGlucose)
                            return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") };
                        var q = HttpUtility.ParseQueryString(req.RequestUri.Query);
                        if (RejectAllSessions || !_sessions.Contains(q["sessionId"] ?? "")) return Error("SessionIdNotFound");
                        var max = int.Parse(q["maxCount"]!, System.Globalization.CultureInfo.InvariantCulture);
                        var arr = new JsonArray();
                        var nowMs = (long)(clock.Now - DateTime.UnixEpoch).TotalMilliseconds;
                        for (var i = 0; i < Math.Min(max, Count); i++)
                        {
                            var wt = nowMs - (long)(NewestAge * 1000) - i * 300_000L;
                            arr.Add(new JsonObject
                            {
                                ["WT"] = $"Date({wt})",
                                ["ST"] = $"Date({wt})",
                                ["DT"] = $"Date({wt}+0200)",
                                ["Value"] = 100 + i % 7,
                                ["Trend"] = i == 0 ? "FortyFiveUp" : "Flat",
                            });
                        }
                        return Json(arr.ToJsonString());
                    }
                default:
                    throw new InvalidOperationException("unknown Share path");
            }
        }

        private static HttpResponseMessage Json(string s) =>
            new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(string code, string message = "x") => new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                $"{{\"Code\":\"{code}\",\"Message\":\"{message}\",\"SubCode\":\"<OnlineException DateThrownLocal=\\\"x\\\" />\",\"TypeName\":\"FaultException\"}}",
                Encoding.UTF8, "application/json"),
        };
    }
}
