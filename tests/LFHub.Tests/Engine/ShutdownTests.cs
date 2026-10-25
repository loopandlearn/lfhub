using LFHub.Engine.Alarms;

namespace LFHub.Tests.Engine;

/// What the engine finishes and closes when the host stops it.
public class ShutdownTests
{
    [Fact]
    public async Task ShutdownDuringAnAlarmPush_LetsTheCheckRecordItsOutcome()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Alarm.Create(AlarmType.Low)]);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        using var stopping = new CancellationTokenSource();
        h.Apns.OnAlarm = stopping.Cancel;
        try
        {
            await h.Engine.EvaluateAsync(h.Patient.Id, true, true, stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Assert.Single(h.Apns.Alarms);
        Assert.Equal("200", device.ActiveAlarm?.LastPushStatus);

        h.Apns.OnAlarm = null;
        h.Restart();
        await h.ReconcileAsync();
        await h.FromAppAsync();
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task ShutdownBeforeAnAlarmCheck_DoesNotBeginIt()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Alarm.Create(AlarmType.Low)], edit: r => r.Settings["alarms"] = false);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        device.Settings["alarms"] = true;
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Engine.EvaluateAsync(h.Patient.Id, false, true, stopping.Token));
        Assert.Empty(h.Apns.Alarms);
        Assert.Null(device.ActiveAlarm);
    }

    [Fact]
    public async Task Stop_EndsTheLoopAndClosesThePatientsRuntimes()
    {
        var h = new EngineHarness();
        h.Readings(120, 125);
        var ct = TestContext.Current.CancellationToken;
        await h.Engine.StartAsync(ct);
        for (var i = 0; i < 100 && (h.Engine.LastTickAt is null || h.Engine.AllStatuses().Count == 0); i++)
            await Task.Delay(20, ct);
        Assert.Single(h.Engine.AllStatuses());

        await h.Engine.StopAsync(ct);
        Assert.True(h.Engine.ExecuteTask?.IsCompletedSuccessfully);
        Assert.Empty(h.Engine.AllStatuses());
        h.Engine.Dispose();
    }
}
