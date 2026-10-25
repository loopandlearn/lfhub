using LFHub.Apns;
using LFHub.Engine.Alarms;

namespace LFHub.Tests.Engine;

/// The device's push health: the last accepted push, when rejections began, and the last alarm push.
public class PushHealthTests
{
    private static readonly PushResult Rejected = new(false, "503 ServiceUnavailable", false, 503);
    private static readonly PushResult Accepted = new(true, "200", false, 200);

    private static int Warnings(EngineHarness h) =>
        h.Logs.Logger.Lines.Count(l => l.StartsWith("Warning", StringComparison.Ordinal) &&
                                       l.Contains("have been rejected since", StringComparison.Ordinal));

    [Fact]
    public async Task RejectedPushes_AreTrackedAndWarnedOnceAfterTenMinutes()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Alarm.Create(AlarmType.Low)]);
        h.Readings(90, 70);
        h.Apns.Result = Rejected;
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(EngineHarness.Start, device.PushFailingSince);
        Assert.Null(device.LastPushOkAt);
        Assert.False(device.LastAlarmPushOk);

        await h.TicksAsync(TimeSpan.FromMinutes(9.5));
        Assert.Equal(0, Warnings(h));
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, Warnings(h));
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, Warnings(h));
        Assert.Equal(EngineHarness.Start, device.PushFailingSince);

        h.Apns.Result = Accepted;
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Null(device.PushFailingSince);
        Assert.InRange(device.LastPushOkAt!.Value, h.Now.AddMinutes(-1), h.Now);
        Assert.True(device.LastAlarmPushOk);

        // A new outage is warned about again.
        h.Apns.Result = Rejected;
        await h.TicksAsync(TimeSpan.FromMinutes(12));
        Assert.Equal(2, Warnings(h));
    }

    [Fact]
    public async Task OtherPushes_LeaveTheAlarmOutcomeUnset()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new System.Text.Json.Nodes.JsonObject { ["badge"] = true });
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(h.Now, device.LastPushOkAt);
        Assert.Null(device.PushFailingSince);
        Assert.Null(device.LastAlarmPushOk);
    }

    [Theory]
    [InlineData(410, "410 Unregistered", true)]
    [InlineData(400, "400 BadDeviceToken", true)]
    [InlineData(410, "410 ExpiredToken", true)]
    [InlineData(400, "400 DeviceTokenNotForTopic", false)]
    [InlineData(503, "503 ServiceUnavailable", false)]
    [InlineData(0, "transport: timeout", false)]
    public void TokenGone_IsA410OrAnUnregisteredOrBadToken(int code, string status, bool gone) =>
        Assert.Equal(gone, new PushResult(false, status, false, code).TokenGone);

    [Fact]
    public async Task GoneToken_IsRecordedFromPushesToTheDevicesToken()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r => r.Settings = new System.Text.Json.Nodes.JsonObject { ["badge"] = true });
        h.Readings(120, 125);
        h.Apns.Result = new PushResult(false, "410 Unregistered", true, 410);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.True(device.PushTokenGone);

        h.Apns.Result = Accepted;
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(2, h.Apns.Badges.Count);
        Assert.False(device.PushTokenGone);
    }

    [Fact]
    public async Task RejectedLiveActivityPush_LeavesTheDevicesPushHealth()
    {
        var h = new EngineHarness();
        var device = h.AddDevice(r =>
        {
            r.Settings = new System.Text.Json.Nodes.JsonObject { ["liveActivity"] = true };
            r.LaToken = "activity-token";
        });
        h.Readings(120, 125);
        h.Apns.Result = new PushResult(false, "410 Unregistered", true, 410);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Single(h.Apns.LiveActivities);
        Assert.False(device.PushTokenGone);
        Assert.Null(device.PushFailingSince);
    }
}
