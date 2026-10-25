using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Engine.Alarms;

namespace LFHub.Tests.Engine;

/// The heartbeat push a device with alarms on gets every 20 minutes.
public class HeartbeatTests
{
    [Fact]
    public async Task Heartbeat_SentAtTheFirstCheckThenEveryTwentyMinutes()
    {
        var h = new EngineHarness();
        h.AlarmDevice([Alarm.Create(AlarmType.Low)]);
        await h.ReconcileAsync();
        await h.TickAsync();
        Assert.Equal([EngineHarness.Start], h.Apns.Heartbeats);

        await h.TicksAsync(TimeSpan.FromMinutes(19.5));
        Assert.Single(h.Apns.Heartbeats);
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([EngineHarness.Start, EngineHarness.Start.AddMinutes(20)], h.Apns.Heartbeats);
    }

    [Fact]
    public async Task Heartbeat_NotSentWithAlarmsOffOrWithoutApns()
    {
        var h = new EngineHarness();
        h.AlarmDevice([Alarm.Create(AlarmType.Low)], edit: r => r.Settings["alarms"] = false);
        var unregistered = h.AlarmDevice([Alarm.Create(AlarmType.Low)], edit: r => r.Apns = null);
        h.AddDevice(r => r.Settings = new JsonObject { ["badge"] = true });
        await h.ReconcileAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(45));
        Assert.Empty(h.Apns.Heartbeats);

        // Once eligible, the next check sends one.
        unregistered.Apns = new LFHub.Db.ApnsConfig("device-token", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "unused");
        await h.TickAsync();
        Assert.Equal([h.Now], h.Apns.Heartbeats);
    }

    [Fact]
    public async Task Heartbeat_SentAgainAtOnceWhenAlarmsAreTurnedBackOn()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Alarm.Create(AlarmType.Low)]);
        await h.ReconcileAsync();
        await h.TickAsync();
        device.Settings["alarms"] = false;
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        device.Settings["alarms"] = true;
        await h.TickAsync();
        Assert.Equal([EngineHarness.Start, h.Now], h.Apns.Heartbeats);
    }

    [Fact]
    public async Task FailedHeartbeat_CountsTowardPushHealth()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Alarm.Create(AlarmType.Low)]);
        h.Apns.Result = new PushResult(false, "503 ServiceUnavailable", false, 503);
        await h.ReconcileAsync();
        await h.TickAsync();
        Assert.Single(h.Apns.Heartbeats);
        Assert.Equal(EngineHarness.Start, device.PushFailingSince);
        Assert.Null(device.LastAlarmPushOk);
    }

    [Fact]
    public void Payload_IsABackgroundPushThatExpiresWithTheNextOneDue()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var (payload, headers) = ApnsClient.Heartbeat(now);
        Assert.Equal(
            $$"""{"aps":{"content-available":1},"lfhubHeartbeat":{{unix}}}""",
            payload.ToJsonString());
        Assert.Equal("lfhub-heartbeat", headers["apns-collapse-id"]);
        Assert.Equal((unix + 1200).ToString(System.Globalization.CultureInfo.InvariantCulture), headers["apns-expiration"]);
    }
}
