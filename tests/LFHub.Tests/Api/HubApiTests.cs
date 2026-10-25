using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Api;
using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine;
using LFHub.Engine.Alarms;
using LFHub.Tests.Db;
using LFHub.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace LFHub.Tests.Api;

/// The HTTP API on a test server, over the real store (see PostgresFixture) and fake APNs.
public sealed class HubApiTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string SetupToken = "setup-token-for-tests";
    private const string StatusKey = "status-key-for-tests";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeApns _apns = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = await StartAppAsync(false);
        _client = _app.GetTestClient();
    }

    private async Task<WebApplication> StartAppAsync(bool allowPrivateSources)
    {
        _ = db.DataSource;
        var store = db.Store;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IHubStore>(store);
        builder.Services.AddSingleton(new HubLogBuffer());
        builder.Services.AddSingleton<IApnsClient>(_apns);
        builder.Services.AddSingleton<HubEngine>();
        var app = builder.Build();
        HubApi.Map(app, SetupToken, StatusKey, allowPrivateSources);
        await app.StartAsync(Ct);
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> ClaimAsync(string token = SetupToken) =>
        await _client.PostAsJsonAsync("/v1/claim", new { setupToken = token, patientName = "Test patient", deviceName = "Test phone" }, Ct);

    private async Task<(Guid Id, string Key)> DeviceAsync()
    {
        var response = await ClaimAsync();
        response.EnsureSuccessStatusCode();
        var claim = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return (claim!["deviceId"]!.GetValue<Guid>(), claim["deviceKey"]!.GetValue<string>());
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is string raw) request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? key, object? body = null)
    {
        using var request = Request(method, path, key, body);
        return await _client.SendAsync(request, Ct);
    }

    private static readonly string P8 = System.Security.Cryptography.ECDsa.Create(
        System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    private static string PushToken() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private async Task RegisterApnsAsync(string key) =>
        (await SendAsync(HttpMethod.Put, "/v1/device/apns", key, new
        {
            token = PushToken(),
            environment = "sandbox",
            bundleId = "org.example.app",
            teamId = "TEAM000000",
            keyId = "KEY0000000",
            key = P8,
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Claim_NeedsTheSetupTokenAndIsLimitedToFiveAMinute()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await ClaimAsync("wrong")).StatusCode);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.OK, (await ClaimAsync()).StatusCode);
        var limited = await ClaimAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ClaimAsync("wrong")).StatusCode);

        _time.Advance(TimeSpan.FromSeconds(61));
        var claim = await (await ClaimAsync()).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.StartsWith("lfh_", claim!["deviceKey"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(Roles.Admin, claim["role"]!.GetValue<string>());
    }

    private async Task<JsonObject> JoinAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/v1/claim", body, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private async Task<string> InviteAsync(string adminKey)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/invites", adminKey);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>();
    }

    private async Task<(Guid Id, string Key)> MemberAsync(string adminKey, object? extra = null)
    {
        var code = await InviteAsync(adminKey);
        var response = await _client.PostAsJsonAsync("/v1/join", new { code, deviceName = "Member phone" }, Ct);
        response.EnsureSuccessStatusCode();
        var join = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(Roles.Member, join!["role"]!.GetValue<string>());
        return (join["deviceId"]!.GetValue<Guid>(), join["deviceKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invite_LetsOnePhoneJoinAsAMemberOnceWithinADay()
    {
        var (adminId, adminKey) = await DeviceAsync();
        var code = await InviteAsync(adminKey);

        var listed = await (await _client.PostAsJsonAsync("/v1/join/devices", new { code }, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        var admin = listed!["devices"]!.AsArray().Single(d => d!["id"]!.GetValue<Guid>() == adminId)!;
        Assert.Equal(Roles.Admin, admin["role"]!.GetValue<string>());

        var join = await _client.PostAsJsonAsync("/v1/join", new { code, deviceName = "Grandma" }, Ct);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        var key = (await join.Content.ReadFromJsonAsync<JsonObject>(Ct))!["deviceKey"]!.GetValue<string>();
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(Roles.Member, me!["device"]!["role"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/v1/join", new { code }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/v1/join/devices", new { code }, Ct)).StatusCode);

        _time.Advance(TimeSpan.FromMinutes(2));
        var expiring = await InviteAsync(adminKey);
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/v1/join", new { code = expiring }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/v1/join", new { code = "not-a-code" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Invite_TakesOverOnlyAMemberDevice()
    {
        var (adminId, adminKey) = await DeviceAsync();
        var (memberId, memberKey) = await MemberAsync(adminKey);

        var code = await InviteAsync(adminKey);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/v1/join", new { code, replace = adminId }, Ct)).StatusCode);
        var join = await (await _client.PostAsJsonAsync("/v1/join", new { code, replace = memberId }, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(memberId, join!["deviceId"]!.GetValue<Guid>());
        Assert.Equal(Roles.Member, join["role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", memberKey)).StatusCode);
    }

    [Fact]
    public async Task Member_CannotChangeSourcesDevicesRolesOrInvite()
    {
        var (adminId, adminKey) = await DeviceAsync();
        var (memberId, memberKey) = await MemberAsync(adminKey);
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, "/v1/patient/source", memberKey,
            new { nightscoutUrl = "https://ns.example.org/" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/v1/devices/{adminId}", memberKey)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"/v1/devices/{memberId}/role", memberKey,
            new { role = Roles.Admin })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/v1/invites", memberKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/patient/source", memberKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/device", memberKey)).StatusCode);
    }

    [Fact]
    public async Task Roles_AreChangedByAnAdminAndOneAdminAlwaysStays()
    {
        var (adminId, adminKey) = await DeviceAsync();
        var (memberId, memberKey) = await MemberAsync(adminKey);
        foreach (var other in (await (await SendAsync(HttpMethod.Get, "/v1/devices", adminKey)).Content.ReadFromJsonAsync<JsonObject>(Ct))!["devices"]!.AsArray()
                     .Select(d => d!["id"]!.GetValue<Guid>()).Where(id => id != adminId && id != memberId))
            await SendAsync(HttpMethod.Delete, $"/v1/devices/{other}", adminKey);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, $"/v1/devices/{memberId}/role", adminKey, new { role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, $"/v1/devices/{adminId}/role", adminKey, new { role = Roles.Member })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"/v1/devices/{Guid.NewGuid()}/role", adminKey, new { role = Roles.Member })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/v1/devices/{memberId}/role", adminKey, new { role = Roles.Admin })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, $"/v1/devices/{adminId}/role", memberKey, new { role = Roles.Member })).StatusCode);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", adminKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(Roles.Member, me!["device"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Source_EachChangeMovesTheRevision()
    {
        var (_, key) = await DeviceAsync();
        async Task<int> RevisionAsync() => (await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content
            .ReadFromJsonAsync<JsonObject>(Ct))!["patient"]!["sourceRevision"]!.GetValue<int>();
        var before = await RevisionAsync();
        await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = "https://ns.example.org/", token = "a" });
        await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = "https://ns.example.org/", token = "b" });
        Assert.Equal(before + 2, await RevisionAsync());
    }

    private async Task<HttpStatusCode> RegisterSharedAsync(string key, string teamId = "TEAM000000", string bundleId = "org.example.app") =>
        (await SendAsync(HttpMethod.Put, "/v1/device/apns", key, new
        {
            token = PushToken(),
            environment = "sandbox",
            bundleId,
            teamId,
        })).StatusCode;

    [Fact]
    public async Task SharedApnsKey_IsAnAdminDevicesKeyForTheSameTeamAndApp()
    {
        // The hub's one patient carries the other tests' devices too, so this test's app is its own.
        var app = $"org.example.shared{Guid.NewGuid():N}";
        Task<HttpResponseMessage> RegisterOwnAsync(string key, string keyId, string p8) =>
            SendAsync(HttpMethod.Put, "/v1/device/apns", key, new
            {
                token = PushToken(),
                environment = "sandbox",
                bundleId = app,
                teamId = "TEAM000000",
                keyId,
                key = p8,
            });

        var (_, adminKey) = await DeviceAsync();
        var (memberId, memberKey) = await MemberAsync(adminKey);
        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.BadRequest, await RegisterSharedAsync(memberKey, bundleId: app));

        (await RegisterOwnAsync(adminKey, "KEY0000000", P8)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, await RegisterSharedAsync(memberKey, teamId: "OTHER00000", bundleId: app));
        Assert.Equal(HttpStatusCode.NoContent, await RegisterSharedAsync(memberKey, bundleId: app));
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", memberKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.True(me!["device"]!["apnsConfigured"]!.GetValue<bool>());
        Assert.True(me["device"]!["apnsKeyShared"]!.GetValue<bool>());

        // A new key on the admin device reaches the device sharing it.
        var newKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();
        (await RegisterOwnAsync(adminKey, "KEY0000001", newKey)).EnsureSuccessStatusCode();
        var shared = (await db.Store.DeviceAsync(memberId))!.Apns!;
        Assert.Equal("KEY0000001", shared.KeyId);
        Assert.Equal(newKey, shared.Key);

        // Its own key ends the sharing.
        (await RegisterOwnAsync(memberKey, "KEY0000002", P8)).EnsureSuccessStatusCode();
        me = await (await SendAsync(HttpMethod.Get, "/v1/me", memberKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.False(me!["device"]!["apnsKeyShared"]!.GetValue<bool>());
    }

    [Fact]
    public async Task JoinPage_TellsACameraUserToOpenLoopFollow()
    {
        var page = await _client.GetStringAsync("/join", Ct);
        Assert.Contains("LoopFollow", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimDevices_ListsTheDevicesForTheSetupTokenOnly()
    {
        var (id, key) = await DeviceAsync();
        await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { appVersion = "7.2.0" });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/v1/claim/devices", new { setupToken = "wrong" }, Ct)).StatusCode);
        var listed = await (await _client.PostAsJsonAsync("/v1/claim/devices", new { setupToken = SetupToken }, Ct))
            .Content.ReadFromJsonAsync<JsonObject>(Ct);
        var device = listed!["devices"]!.AsArray().Single(d => d!["id"]!.GetValue<Guid>() == id)!;
        Assert.Equal("Test phone", device["name"]!.GetValue<string>());
        Assert.Equal("7.2.0", device["appVersion"]!.GetValue<string>());
        Assert.NotNull(device["lastSeenAt"]);
        Assert.Null(device["deviceKey"]);
    }

    [Fact]
    public async Task Claim_ReplaceHandsTheDeviceToTheNewPhone()
    {
        var (id, oldKey) = await DeviceAsync();
        await RegisterApnsAsync(oldKey);
        var export = AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test");
        await SendAsync(HttpMethod.Put, "/v1/device/alarms", oldKey, export);
        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", oldKey, new { timeZone = "Europe/Stockholm" });
        await SendAsync(HttpMethod.Put, "/v1/device/settings", oldKey, new { badge = true, appVersion = "7.2.0", features = Receipts });

        var claim = await JoinAsync(new { setupToken = SetupToken, deviceName = "New phone", replace = id });
        Assert.Equal(id, claim["deviceId"]!.GetValue<Guid>());
        var newKey = claim["deviceKey"]!.GetValue<string>();
        Assert.NotEqual(oldKey, newKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", oldKey)).StatusCode);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", newKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("New phone", me!["device"]!["name"]!.GetValue<string>());
        Assert.False(me["device"]!["apnsConfigured"]!.GetValue<bool>());
        Assert.True(me["device"]!["settings"]!["badge"]!.GetValue<bool>());
        Assert.Null(me["device"]!["settings"]!["appVersion"]);
        Assert.Equal(export, await (await SendAsync(HttpMethod.Get, "/v1/device/alarms", newKey)).Content.ReadAsStringAsync(Ct));
        var client = await (await SendAsync(HttpMethod.Get, "/v1/device/clientsettings", newKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Europe/Stockholm", client!["timeZone"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claim_CopyFromStartsANewDeviceWithTheSameSettingsAndAlarms()
    {
        var (id, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        var export = AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test");
        await SendAsync(HttpMethod.Put, "/v1/device/alarms", key, export);
        await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { badge = true, appVersion = "7.2.0" });

        var claim = await JoinAsync(new { setupToken = SetupToken, deviceName = "Second phone", copyFrom = id });
        var copyKey = claim["deviceKey"]!.GetValue<string>();
        Assert.NotEqual(id, claim["deviceId"]!.GetValue<Guid>());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/v1/me", key)).StatusCode);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", copyKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("Second phone", me!["device"]!["name"]!.GetValue<string>());
        Assert.False(me["device"]!["apnsConfigured"]!.GetValue<bool>());
        Assert.True(me["device"]!["settings"]!["badge"]!.GetValue<bool>());
        Assert.Null(me["device"]!["settings"]!["appVersion"]);
        Assert.Equal(export, await (await SendAsync(HttpMethod.Get, "/v1/device/alarms", copyKey)).Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/device/clientsettings", copyKey)).StatusCode);
    }

    [Fact]
    public async Task Claim_ReplaceAndCopyNeedAKnownDeviceAndExcludeEachOther()
    {
        var (id, _) = await DeviceAsync();
        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync("/v1/claim", new { setupToken = SetupToken, replace = unknown }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync("/v1/claim", new { setupToken = SetupToken, copyFrom = unknown }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/v1/claim", new { setupToken = SetupToken, replace = id, copyFrom = id }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.PostAsJsonAsync("/v1/claim", new { setupToken = "wrong", replace = id }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Devices_ListsThePatientsDevicesAndRemovesOne()
    {
        var (first, firstKey) = await DeviceAsync();
        var (second, _) = await DeviceAsync();

        var listed = await (await SendAsync(HttpMethod.Get, "/v1/devices", firstKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        var devices = listed!["devices"]!.AsArray();
        Assert.True(devices.Single(d => d!["id"]!.GetValue<Guid>() == first)!["current"]!.GetValue<bool>());
        Assert.False(devices.Single(d => d!["id"]!.GetValue<Guid>() == second)!["current"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Delete, $"/v1/devices/{second}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/v1/devices/{second}", firstKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/v1/devices/{second}", firstKey)).StatusCode);
        listed = await (await SendAsync(HttpMethod.Get, "/v1/devices", firstKey)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.DoesNotContain(listed!["devices"]!.AsArray(), d => d!["id"]!.GetValue<Guid>() == second);
    }

    [Fact]
    public async Task Source_IsReadBackInFullByAnyDevice()
    {
        var (_, key) = await DeviceAsync();
        var (_, other) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new
        {
            nightscoutUrl = "https://ns.example.org/",
            token = "ns-token",
            dexcom = new { username = "user", password = "secret", server = "EU" },
        })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/patient/source", null)).StatusCode);
        var source = await (await SendAsync(HttpMethod.Get, "/v1/patient/source", other)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("https://ns.example.org", source!["nightscoutUrl"]!.GetValue<string>());
        Assert.Equal("ns-token", source["token"]!.GetValue<string>());
        Assert.Equal("user", source["dexcom"]!["username"]!.GetValue<string>());
        Assert.Equal("secret", source["dexcom"]!["password"]!.GetValue<string>());
        Assert.Equal("EU", source["dexcom"]!["server"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeviceCalls_NeedTheDevicesBearerKey()
    {
        var (id, key) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", key + "x")).StatusCode);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(id, me!["device"]!["id"]!.GetValue<Guid>());
        Assert.False(me["device"]!["apnsConfigured"]!.GetValue<bool>());
        Assert.DoesNotContain(key, me.ToJsonString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, "/v1/device", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/me", key)).StatusCode);
    }

    [Fact]
    public async Task TestAlarm_OneAMinutePerDevice()
    {
        var (_, key) = await DeviceAsync();
        var (_, other) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { })).StatusCode);
        await RegisterApnsAsync(key);
        await RegisterApnsAsync(other);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { title = "Test" })).StatusCode);
        var limited = await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", other, new { })).StatusCode);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { })).StatusCode);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { })).StatusCode);

        Assert.Equal(3, _apns.Alarms.Count);
        Assert.Equal("Test", _apns.Alarms[0].Title);
        Assert.True(_apns.Alarms[0].Ring);
    }

    [Fact]
    public async Task TestAlarm_WithADelayAnswersAtOnceAndSendsLater()
    {
        var (_, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { delaySeconds = 1 })).StatusCode);
        Assert.Empty(_apns.Alarms);
        for (var i = 0; i < 50 && _apns.Alarms.Count == 0; i++) await Task.Delay(100, Ct);
        Assert.Single(_apns.Alarms);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { delaySeconds = 1 })).StatusCode);
    }

    [Fact]
    public async Task Apns_FirstRegistrationNeedsTheKeyAndAKnownEnvironment()
    {
        var (_, key) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/apns", key, new
        {
            token = "t",
            environment = "sandbox",
            bundleId = "org.example.app",
            teamId = "TEAM000000",
            keyId = "KEY0000000",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/apns", key, new
        {
            token = "t",
            environment = "staging",
            bundleId = "org.example.app",
            teamId = "TEAM000000",
            keyId = "KEY0000000",
            key = "k",
        })).StatusCode);
    }

    [Theory]
    [InlineData("token", "apns-1234")]
    [InlineData("bundleId", "org.example.app\nX-Other: 1")]
    [InlineData("bundleId", "org.example/app")]
    [InlineData("teamId", "team000000")]
    [InlineData("teamId", "TEAM00000")]
    [InlineData("keyId", "KEY00000000")]
    [InlineData("keyId", "KEY-000000")]
    [InlineData("key", "p8-key-body")]
    public async Task Apns_RegistrationIsValidatedBeforeItIsStored(string field, string value)
    {
        var (_, key) = await DeviceAsync();
        var registration = new JsonObject
        {
            ["token"] = PushToken(),
            ["environment"] = "sandbox",
            ["bundleId"] = "org.example.app",
            ["teamId"] = "TEAM000000",
            ["keyId"] = "KEY0000000",
            ["key"] = P8,
        };
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Put, "/v1/device/apns", key, registration.ToJsonString())).StatusCode);
        var stored = (await db.Store.AuthenticateAsync(key))!.Apns;

        registration[field] = value;
        var refused = await SendAsync(HttpMethod.Put, "/v1/device/apns", key, registration.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(field, (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal(stored, (await db.Store.AuthenticateAsync(key))!.Apns);
    }

    private static readonly string[] Receipts = ["receipts"];
    private static readonly string[] ReceiptsAndSpeak = ["receipts", "speak"];
    private static readonly int[] NotText = [1];

    [Fact]
    public async Task Settings_RejectUnknownKeysAndMerge()
    {
        var (_, key) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { colour = "red" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { badge = "yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { unit = "mmol/L" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { features = NotText })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { appVersion = new string('9', 33) })).StatusCode);
        await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { badge = true, appVersion = "7.2.0", features = Receipts });
        await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test"));
        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, new { timeZone = "Europe/Stockholm" });
        var merged = await (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true }))
            .Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.True(merged!["badge"]!.GetValue<bool>());
        Assert.True(merged["alarms"]!.GetValue<bool>());
        Assert.Equal("7.2.0", merged["appVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_AlarmsOnNeedAnAppThatPostsReceipts()
    {
        var (_, key) = await DeviceAsync();
        await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test"));
        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, new { timeZone = "Europe/Stockholm" });

        var refused = await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("update LoopFollow", (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true, features = ReceiptsAndSpeak })).StatusCode);
    }

    [Fact]
    public async Task Settings_AlarmsOnNeedAnAlarmExportAndTheTimeZone()
    {
        var (_, key) = await DeviceAsync();
        await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { features = Receipts });
        var refused = await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("PUT /v1/device/alarms", (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = false })).StatusCode);

        await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test"));
        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, new { units = "mg/dL" });
        refused = await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("timeZone", (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(),
            StringComparison.Ordinal);
        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, new { timeZone = "Nowhere/Unknown" });
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true })).StatusCode);

        await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, new { timeZone = "Europe/Stockholm" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, "/v1/device/settings", key, new { alarms = true })).StatusCode);
    }

    [Fact]
    public async Task Me_ReportsAlarmsReceivedPushHealthAndWhetherThePatientIsFollowed()
    {
        var (id, key) = await DeviceAsync();
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(1, me!["hub"]!["api"]!.GetValue<int>());
        Assert.Contains("receipts", me["hub"]!["features"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(me["hub"]!["version"]!.GetValue<string>()));
        var device = me["device"]!;
        Assert.False(device["alarmsReceived"]!.GetValue<bool>());
        Assert.Equal(0, device["alarmCount"]!.GetValue<int>());
        Assert.Null(device["lastAlarmPushOk"]);
        Assert.Null(device["lastPushOkAt"]);
        Assert.Null(device["pushFailingSince"]);
        Assert.False(me["patient"]!["followed"]!.GetValue<bool>());
        Assert.False(me["patient"]!["hasData"]!.GetValue<bool>());

        await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low), Alarm.Create(AlarmType.High)], new AlarmConfiguration(), "test"));
        var at = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        await db.Store.RecordPushOutcomeAsync(id, at, true, false, false);
        await db.Store.RecordPushOutcomeAsync(id, at.AddMinutes(1), false, true, false);
        me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        device = me!["device"]!;
        Assert.True(device["alarmsReceived"]!.GetValue<bool>());
        Assert.Equal(2, device["alarmCount"]!.GetValue<int>());
        Assert.False(device["lastAlarmPushOk"]!.GetValue<bool>());
        Assert.Equal(at, device["lastPushOkAt"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Equal(at.AddMinutes(1), device["pushFailingSince"]!.GetValue<DateTime>().ToUniversalTime());
    }

    [Fact]
    public async Task TestAlarm_CountsAsAnAlarmPush()
    {
        var (_, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { })).StatusCode);
        var device = (await db.Store.AuthenticateAsync(key))!;
        Assert.True(device.LastAlarmPushOk);
        Assert.NotNull(device.LastPushOkAt);
    }

    [Fact]
    public async Task Alarms_UploadSkipsUnknownTypesAndRejectsABrokenExport()
    {
        var (id, key) = await DeviceAsync();
        var export = JsonNode.Parse(AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low), Alarm.Create(AlarmType.High)],
            new AlarmConfiguration(), "test"))!;
        export["alarms"]![1]!["type"] = "Some Future Alert";
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/device/alarms", key, export.ToJsonString())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/device/alarms", key, "{\"alarms\":[]}")).StatusCode);

        var device = (await db.Store.AuthenticateAsync(key))!;
        Assert.Equal(id, device.Id);
        Assert.Equal(export.ToJsonString(), device.AlarmsJson);
    }

    [Fact]
    public async Task Uploads_AreLimitedInSizeAndAlarmCount()
    {
        var (_, key) = await DeviceAsync();
        var padding = new string(' ', 256 * 1024);
        var export = AlarmSettingsJson.Serialize([Alarm.Create(AlarmType.Low)], new AlarmConfiguration(), "test");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await SendAsync(HttpMethod.Put, "/v1/device/alarms", key, export + padding)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await SendAsync(HttpMethod.Put, "/v1/device/clientsettings", key, "{}" + padding)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Put, "/v1/device/alarms", key, export + padding[..^export.Length])).StatusCode);

        var many = Enumerable.Range(0, 201).Select(_ => Alarm.Create(AlarmType.Low)).ToList();
        var refused = await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize(many, new AlarmConfiguration(), "test"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("200", (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/device/alarms", key,
            AlarmSettingsJson.Serialize(many[..200], new AlarmConfiguration(), "test"))).StatusCode);
    }

    [Fact]
    public async Task Handled_ClearsTheActiveAndCurrentAlarm()
    {
        var (id, key) = await DeviceAsync();
        var alarmId = Guid.NewGuid();
        var at = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        await db.Store.SaveAlarmEvaluationAsync(id, 0, JsonSerializer.Serialize(new AlarmState { CurrentAlarmId = alarmId }),
            new ActiveAlarm(alarmId, "Low", "70", null, "default", "bell", "category", true, true, at, at, "200"), []);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, $"/v1/device/alarm/{alarmId}/handled", key)).StatusCode);
        var device = (await db.Store.AuthenticateAsync(key))!;
        Assert.Null(device.ActiveAlarm);
        Assert.Null(JsonSerializer.Deserialize<AlarmState>(device.AlarmStateJson!)!.CurrentAlarmId);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/v1/device/alarm/not-a-guid/handled", key)).StatusCode);
    }

    [Fact]
    public async Task Receipt_IsSingleUseAndAnswers404ForAnythingElse()
    {
        var (_, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { })).StatusCode);
        var token = Assert.Single(_apns.Alarms).Receipt;
        Assert.Matches("^[0-9a-f]{32}$", token);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/v1/receipt/{token}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/v1/receipt/{token}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/v1/receipt/{new string('0', 32)}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/v1/receipt/{token.ToUpperInvariant()}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/v1/receipt/not-a-token", null, Ct)).StatusCode);
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM lfhub.alarm_receipts WHERE token_hash = @h OR token_hash = @t",
            ("h", AlarmReceipts.Hash(token)), ("t", token)));
    }

    [Fact]
    public async Task Receipt_ExpiresAfterThirtyMinutesAndIsKeptOnlyAsItsHash()
    {
        var (_, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        await SendAsync(HttpMethod.Post, "/v1/device/testalarm", key, new { });
        var token = Assert.Single(_apns.Alarms).Receipt;
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM lfhub.alarm_receipts WHERE token_hash = @h",
            ("h", AlarmReceipts.Hash(token))));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM lfhub.alarm_receipts WHERE token_hash = @t OR fired_at = @t", ("t", token)));
        _time.Advance(AlarmReceipts.Lifetime);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/v1/receipt/{token}", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Receipt_RecordsDeliveryOnTheSoundingAlarm()
    {
        var (id, key) = await DeviceAsync();
        var alarmId = Guid.NewGuid();
        var at = _time.GetUtcNow().UtcDateTime;
        await db.Store.SaveAlarmEvaluationAsync(id, 0, "{}",
            new ActiveAlarm(alarmId, "Low", "70", null, "default", "bell", "category", true, false, at, at, "200"), []);
        var token = AlarmReceipts.NewToken();
        await db.Store.IssueReceiptAsync(AlarmReceipts.Hash(token), id, alarmId, at, at);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Null(me!["device"]!["activeAlarm"]!["deliveredAt"]);

        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/v1/receipt/{token}", null, Ct)).StatusCode);
        me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(at.AddSeconds(4), me!["device"]!["activeAlarm"]!["deliveredAt"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Equal(at.AddSeconds(4), (await db.Store.AuthenticateAsync(key))!.ActiveAlarm!.DeliveredAt);
    }

    [Fact]
    public async Task Receipt_IsLimitedAcrossTheHub()
    {
        for (var i = 0; i < 120; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/v1/receipt/{AlarmReceipts.NewToken()}", null, Ct)).StatusCode);
        var limited = await _client.PostAsync($"/v1/receipt/{AlarmReceipts.NewToken()}", null, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.RetryAfter?.ToString());
    }

    [Fact]
    public async Task LiveActivity_RenewByWithoutANewTokenIsIgnored()
    {
        var (_, key) = await DeviceAsync();
        var renewBy = new DateTimeOffset(2026, 10, 24, 19, 30, 0, TimeSpan.Zero);
        await SendAsync(HttpMethod.Put, "/v1/device/liveactivity", key, new { activityToken = "activity-1", pushToStartToken = "start-1", renewBy = renewBy.ToUnixTimeSeconds() });
        await SendAsync(HttpMethod.Put, "/v1/device/liveactivity", key, new { renewBy = renewBy.AddHours(-8).ToUnixTimeSeconds() });
        var la = (await db.Store.AuthenticateAsync(key))!.LiveActivity;
        Assert.Equal(renewBy.UtcDateTime, la.RenewBy);
        Assert.Equal("activity-1", la.Token);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(HttpMethod.Put, "/v1/device/liveactivity", key, new { activityToken = "activity-2", renewBy = 1e20 })).StatusCode);
    }

    [Fact]
    public async Task Source_IsValidated()
    {
        var (_, key) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = "ftp://ns.example.org" })).StatusCode);
        var plain = await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = "http://ns.example.org" });
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("https", (await plain.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (var local in new[]
                 {
                     "http://localhost:1337", "http://127.0.0.1:1337", "http://[::1]:1337", "https://localhost",
                     "https://127.0.0.1", "https://10.0.0.5", "https://192.168.1.10:1337", "https://169.254.169.254",
                     "https://[fd00::1]", "https://[::ffff:127.0.0.1]",
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = local })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = "https://ns.example.org/", token = "t" })).StatusCode);
        var me = await (await SendAsync(HttpMethod.Get, "/v1/me", key)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("ns.example.org", me!["patient"]!["nightscoutHost"]!.GetValue<string>());
    }

    [Fact]
    public async Task Source_OnAPrivateAddressIsAcceptedWhenAllowed()
    {
        await using var app = await StartAppAsync(true);
        using var client = app.GetTestClient();
        var claim = await (await client.PostAsJsonAsync("/v1/claim",
            new { setupToken = SetupToken, patientName = "Test patient", deviceName = "Test phone" }, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        var key = claim!["deviceKey"]!.GetValue<string>();
        async Task<HttpStatusCode> SetAsync(string url)
        {
            using var request = Request(HttpMethod.Put, "/v1/patient/source", key, new { nightscoutUrl = url });
            return (await client.SendAsync(request, Ct)).StatusCode;
        }
        foreach (var local in new[] { "http://localhost:1337", "http://[::1]:1337", "https://192.168.1.10:1337", "https://[fd00::1]" })
            Assert.Equal(HttpStatusCode.NoContent, await SetAsync(local));
        Assert.Equal(HttpStatusCode.BadRequest, await SetAsync("http://192.168.1.10:1337"));
    }

    [Fact]
    public async Task Bundle_UnavailableUntilThePatientIsFollowed()
    {
        var (_, key) = await DeviceAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Get, "/v1/patient/bundle", key)).StatusCode);
    }

    [Fact]
    public async Task Status_FailsWhileAStoredSecretDoesNotOpen()
    {
        var (id, key) = await DeviceAsync();
        await RegisterApnsAsync(key);
        await db.ExecuteAsync("UPDATE lfhub.devices SET apns_key_sealed = 'bm90IHNlYWxlZCB1bmRlciB0aGlzIGtleQ==' WHERE id = @id", ("id", id));
        try
        {
            var body = await (await _client.GetAsync($"/status?key={StatusKey}", Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct);
            Assert.Contains("secrets", body!["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
        }
        finally
        {
            await db.ExecuteAsync("UPDATE lfhub.devices SET apns_key_sealed = NULL, apns_token = NULL WHERE id = @id", ("id", id));
        }
        var after = await (await _client.GetAsync($"/status?key={StatusKey}", Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.DoesNotContain("secrets", after!["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
    }

    [Fact]
    public async Task Health_LivenessAndStatus()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz", Ct)).StatusCode);

        // The engine is not running here, so liveness and status report it.
        var livez = await _client.GetAsync("/livez", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, livez.StatusCode);
        Assert.Equal("engine\n", await livez.Content.ReadAsStringAsync(Ct));

        var status = await _client.GetAsync($"/status?fresh=1&key={StatusKey}", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("fail", body!["status"]!.GetValue<string>());
        Assert.Equal(["engine"], body["problems"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("no-store", status.Headers.CacheControl?.ToString());

        // Without the key: the verdict only, and `fresh` is ignored.
        foreach (var path in new[] { "/status?fresh=1", "/status?fresh=1&key=wrong" })
        {
            var open = await _client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, open.StatusCode);
            var openBody = await open.Content.ReadFromJsonAsync<JsonObject>(Ct);
            Assert.Equal(["status"], openBody!.Select(kv => kv.Key));
        }

        using var head = new HttpRequestMessage(HttpMethod.Head, "/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.SendAsync(head, Ct)).StatusCode);
    }
}
