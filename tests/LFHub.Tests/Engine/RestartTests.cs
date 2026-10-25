using LFHub.Engine.Alarms;

namespace LFHub.Tests.Engine;

/// What a restarted hub starts from: the latest reading it kept in the database.
public class RestartTests
{
    [Fact]
    public async Task LatestReading_IsSavedWhenItChangesAtMostOnceAMinute()
    {
        // A reading eight minutes old makes BG due every 30 seconds.
        var h = new EngineHarness();
        h.Reading(120);
        h.Advance(TimeSpan.FromMinutes(8));
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal((EngineHarness.Start, 120), h.Store.Patients[0].LastReading);

        h.Advance(TimeSpan.FromSeconds(10));
        h.Reading(125);
        var second = h.Now;
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(second, h.Engine.Status(h.Patient.Id)?.LastReadingAt);
        Assert.Equal((EngineHarness.Start, 120), h.Store.Patients[0].LastReading);

        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((second, 125), h.Store.Patients[0].LastReading);
    }

    [Fact]
    public async Task RestartWhileNightscoutIsDown_MissedReadingFiresAfterItsThreshold()
    {
        var h = new EngineHarness();
        var missed = Alarm.Create(AlarmType.MissedReading);
        var device = h.AlarmDevice([missed]);
        h.Readings(120, 121);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        var lastReading = h.Now;

        h.Nightscout.Route("/api/v1/entries.json", "not json");
        h.Advance(TimeSpan.FromMinutes(2));
        h.Restart();
        await h.ReconcileAsync();
        await h.TickAsync();
        Assert.Equal(lastReading, h.Engine.Status(h.Patient.Id)?.LastReadingAt);

        var threshold = TimeSpan.FromMinutes(missed.Threshold!.Value);
        await h.TicksAsync(lastReading + threshold - h.Now - TimeSpan.FromSeconds(30));
        Assert.Empty(h.Apns.Alarms);
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(missed.Id, Assert.Single(h.Apns.Alarms).AlarmId);
        Assert.Equal(missed.Id, device.ActiveAlarm?.AlarmId);
    }
}
