using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Tests.Fakes;

namespace LFHub.Tests.Engine;

/// The engine's Live Activity updates and push-to-start renewals.
public class LiveActivityTests
{
    private static long Unix(DateTime t) => (long)(t - DateTime.UnixEpoch).TotalSeconds;

    private static async Task<(EngineHarness H, FakeStore.Row Device)> ActivityAsync(Action<FakeStore.Row>? edit = null)
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r =>
        {
            r.Settings = new JsonObject { ["liveActivity"] = true };
            r.LaToken = "activity-token";
            r.LaPushToStartToken = "start-token";
            r.LaRenewBy = EngineHarness.Start.AddHours(7);
            edit?.Invoke(r);
        });
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        return (h, device);
    }

    [Fact]
    public async Task Update_CarriesTheSnapshotAndAStaleDate()
    {
        var (h, device) = await ActivityAsync();
        var push = Assert.Single(h.Apns.LiveActivities);
        Assert.Equal("activity-token", push.Token);
        Assert.Equal("update", push.Aps["event"]!.GetValue<string>());
        Assert.Equal(Unix(h.Now), push.Aps["timestamp"]!.GetValue<long>());
        Assert.Equal(Unix(h.Now.AddMinutes(15)), push.Aps["stale-date"]!.GetValue<long>());
        var content = push.Aps["content-state"]!;
        Assert.Equal("hub", content["reason"]!.GetValue<string>());
        Assert.Equal(Unix(h.Now), content["seq"]!.GetValue<long>());
        Assert.Equal(125.0, content["snapshot"]!["glucose"]!.GetValue<double>());
        Assert.Equal(5.0, content["snapshot"]!["delta"]!.GetValue<double>());
        Assert.False(content["snapshot"]!["showRenewalOverlay"]!.GetValue<bool>());
        Assert.NotNull(device.LaLastFingerprint);
        Assert.Equal("200", device.LaLastPushStatus);
    }

    [Fact]
    public async Task UnchangedSnapshot_IsResentOnlyAtTheHeartbeat()
    {
        var (h, _) = await ActivityAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(4.5));
        Assert.Single(h.Apns.LiveActivities);
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, h.Apns.LiveActivities.Count);
    }

    [Fact]
    public async Task NewReading_IsSentAtOnce()
    {
        var (h, _) = await ActivityAsync();
        h.NextReading(131);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.LiveActivities.Count);
        Assert.Equal(131.0, h.Apns.LiveActivities[^1].Aps["content-state"]!["snapshot"]!["glucose"]!.GetValue<double>());
    }

    [Fact]
    public async Task Renewal_GoesThroughPushToStart()
    {
        var (h, device) = await ActivityAsync(r => r.LaRenewBy = EngineHarness.Start.AddMinutes(-1));
        var push = Assert.Single(h.Apns.LiveActivities);
        Assert.Equal("start-token", push.Token);
        Assert.Equal("start", push.Aps["event"]!.GetValue<string>());
        Assert.Equal("GlucoseLiveActivityAttributes", push.Aps["attributes-type"]!.GetValue<string>());
        Assert.Equal("LoopFollow", push.Aps["attributes"]!["title"]!.GetValue<string>());
        Assert.Equal(Unix(h.Now.AddMinutes(15)), push.Aps["stale-date"]!.GetValue<long>());
        Assert.Equal("passive", push.Aps["interruption-level"]!.GetValue<string>());
        Assert.Equal("", push.Aps["alert"]!["title"]!.GetValue<string>());
        Assert.Equal(Unix(h.Now.AddMinutes(10)).ToString(System.Globalization.CultureInfo.InvariantCulture), push.Headers!["apns-expiration"]);
        Assert.Equal("org.example.app.la.start", push.Headers!["apns-collapse-id"]);

        Assert.Equal(EngineHarness.Start.AddMinutes(-1), device.LaRenewBy);
        Assert.False(device.LaRenewalFailed);
        Assert.Equal("activity-token", device.LaToken);
        Assert.Equal("start 200", device.LaLastPushStatus);
    }

    [Fact]
    public async Task AfterRenewal_TheOldActivityKeepsUpdatingUntilTheAppReportsTheNewOne()
    {
        var (h, _) = await ActivityAsync(r => r.LaRenewBy = EngineHarness.Start.AddMinutes(-1));
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, h.Apns.LiveActivities.Count);
        Assert.Equal("activity-token", h.Apns.LiveActivities[1].Token);
        Assert.Equal("update", h.Apns.LiveActivities[1].Aps["event"]!.GetValue<string>());
        await h.TicksAsync(TimeSpan.FromMinutes(10));
        Assert.DoesNotContain(h.Apns.LiveActivities.Skip(1), p => p.Token == "start-token");
    }

    [Fact]
    public async Task StartedRenewal_IsRetriedEveryHalfHourUntilTheAppReportsTheNewActivity()
    {
        var (h, device) = await ActivityAsync(r => r.LaRenewBy = EngineHarness.Start.AddMinutes(-1));
        int Starts() => h.Apns.LiveActivities.Count(p => p.Token == "start-token");
        Assert.Equal(1, Starts());

        await h.TicksAsync(TimeSpan.FromMinutes(29.5));
        Assert.Equal(1, Starts());
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Starts());
        Assert.Equal(EngineHarness.Start.AddMinutes(-1), device.LaRenewBy);

        // The same token again is no new activity.
        h.Store.ReportLiveActivity(device.Id, "activity-token", null, null);
        await h.TicksAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(3, Starts());

        h.Store.ReportLiveActivity(device.Id, "new-activity-token", null, h.Now.AddHours(7.5));
        Assert.Equal(h.Now.AddHours(7.5), device.LaRenewBy);
        await h.TicksAsync(TimeSpan.FromHours(2));
        Assert.Equal(3, Starts());
        Assert.Equal("new-activity-token", h.Apns.LiveActivities[^1].Token);
    }

    [Fact]
    public async Task RenewalWithoutPushToStartToken_ShowsTheOverlayAndRetriesEveryHalfHour()
    {
        var (h, device) = await ActivityAsync(r =>
        {
            r.LaRenewBy = EngineHarness.Start.AddMinutes(-1);
            r.LaPushToStartToken = null;
        });
        Assert.Empty(h.Apns.LiveActivities);
        Assert.True(device.LaRenewalFailed);

        await h.TicksAsync(TimeSpan.FromMinutes(4.5));
        Assert.Empty(h.Apns.LiveActivities);
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        var update = Assert.Single(h.Apns.LiveActivities);
        Assert.Equal("activity-token", update.Token);
        Assert.True(update.Aps["content-state"]!["snapshot"]!["showRenewalOverlay"]!.GetValue<bool>());

        device.LaPushToStartToken = "start-token";
        await h.TicksAsync(TimeSpan.FromMinutes(24.5));
        Assert.DoesNotContain(h.Apns.LiveActivities, p => p.Token == "start-token");
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("start-token", h.Apns.LiveActivities[^1].Token);
        Assert.False(device.LaRenewalFailed);
    }

    [Fact]
    public async Task InvalidToken_IsForgotten()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r =>
        {
            r.Settings = new JsonObject { ["liveActivity"] = true };
            r.LaToken = "activity-token";
        });
        h.Readings(120, 125);
        h.Apns.Result = new PushResult(false, "410 Unregistered", true, 410);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Null(device.LaToken);
        Assert.Equal("410 Unregistered", device.LaLastPushStatus);
    }

    [Fact]
    public async Task FailedUpdate_BacksOffFiveMinutes()
    {
        var h = new EngineHarness();
        h.AddDevice(r =>
        {
            r.Settings = new JsonObject { ["liveActivity"] = true };
            r.LaToken = "activity-token";
        });
        h.Readings(120, 125);
        h.Apns.Result = new PushResult(false, "transport: timeout", false);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        h.Apns.Result = new PushResult(true, "200", false, 200);
        await h.TicksAsync(TimeSpan.FromMinutes(4.5));
        Assert.Single(h.Apns.LiveActivities);
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, h.Apns.LiveActivities.Count);
    }

    [Fact]
    public async Task Off_SendsNothing()
    {
        var (h, _) = await ActivityAsync(r => r.Settings = new JsonObject { ["liveActivity"] = false });
        Assert.Empty(h.Apns.LiveActivities);
    }
}
