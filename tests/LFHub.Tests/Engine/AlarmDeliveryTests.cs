using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine;
using LFHub.Engine.Alarms;
using LFHub.Tests.Fakes;

namespace LFHub.Tests.Engine;

/// <summary>
/// The engine around the alarm evaluator: what is pushed, repeated, stopped and
/// persisted for a device, check by check.
/// </summary>
public class AlarmDeliveryTests
{
    private static Alarm Low(Action<Alarm>? edit = null)
    {
        var a = Alarm.Create(AlarmType.Low);
        edit?.Invoke(a);
        return a;
    }

    /// A device with a Low alarm (below 80) and a low reading that fires it.
    private static async Task<(EngineHarness H, FakeStore.Row Device, Alarm Alarm)> FiredLowAsync(
        AlarmConfiguration? config = null, Action<Alarm>? edit = null)
    {
        var h = new EngineHarness();
        var low = Low(edit);
        var device = h.AlarmDevice([low], config);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        return (h, device, low);
    }

    [Fact]
    public async Task Firing_DeliversTheAlarmAndPersistsIt()
    {
        var (h, device, low) = await FiredLowAsync();
        var push = Assert.Single(h.Apns.Alarms);
        Assert.Equal(low.Id, push.AlarmId);
        Assert.Equal($"{AlarmType.Low.Emoji()} {AlarmType.Low.RawValue()}", push.Title);
        Assert.Equal("70 → -20", push.Subtitle);
        Assert.Equal($"lfhub-{low.SoundFile.BuiltinName}.caf", push.Sound);
        Assert.Equal(AlarmType.Low.SymbolName(), push.Symbol);
        Assert.Equal("category", push.Category);
        Assert.True(push.Ring);
        Assert.False(push.RingOnce);
        Assert.False(push.Critical);

        var active = device.ActiveAlarm!;
        Assert.Equal(low.Id, active.AlarmId);
        Assert.True(active.Repeat);
        Assert.Equal(h.Now, active.FiredAt);
        Assert.Equal("200", active.LastPushStatus);
        Assert.Equal(low.Id, device.AlarmState!.CurrentAlarmId);
    }

    [Fact]
    public async Task RepeatingAlarm_IsDeliveredAgainEachCheckUntilSnoozed()
    {
        var (h, device, low) = await FiredLowAsync();

        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Single(h.Apns.Alarms);

        // One repeat per minute's check, for as long as nothing silences it.
        await h.TicksAsync(TimeSpan.FromMinutes(40).Add(TimeSpan.FromSeconds(-30)));
        Assert.Equal(41, h.Apns.Alarms.Count);
        Assert.All(h.Apns.Alarms, p => Assert.Equal(low.Id, p.AlarmId));

        // The app uploads the alarm snoozed: the next check stops its system alarm and clears it.
        var snoozed = low.Clone();
        snoozed.SnoozedUntil = h.Now.AddMinutes(10);
        h.Store.SetAlarms(device.Id, [snoozed], new AlarmConfiguration());
        await h.FromAppAsync();
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal((low.Id, AlarmStopReason.Silenced), (stop.AlarmId, stop.Reason));
        Assert.Equal((h.Apns.Alarms[0].Title, h.Apns.Alarms[0].Subtitle), (stop.Title, stop.Subtitle));
        Assert.Null(device.ActiveAlarm);
        Assert.Null(device.AlarmState!.CurrentAlarmId);
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(41, h.Apns.Alarms.Count);
    }

    [Fact]
    public async Task SnoozeFromTheApp_TakesEffectInsideTheMinuteBetweenChecks()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Advance(TimeSpan.FromSeconds(10));
        var snoozed = low.Clone();
        snoozed.SnoozedUntil = h.Now.AddMinutes(10);
        h.Store.SetAlarms(device.Id, [snoozed], new AlarmConfiguration());
        await h.FromAppAsync();
        Assert.Null(device.ActiveAlarm);
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task SnoozeAllFromTheApp_SilencesTheSoundingAlarm()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Store.SetAlarms(device.Id, [low], new AlarmConfiguration { SnoozeUntil = h.Now.AddMinutes(30) });
        await h.FromAppAsync();
        Assert.Null(device.ActiveAlarm);
        Assert.Equal(AlarmStopReason.Silenced, Assert.Single(h.Apns.Stops).Reason);
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Alarms);
        Assert.Single(h.Apns.Stops);
    }

    [Fact]
    public async Task SilencedMutedAlarm_SendsNoStop()
    {
        var (h, device, low) = await FiredLowAsync(new AlarmConfiguration { MuteUntil = EngineHarness.Start.AddMinutes(30) });
        var snoozed = low.Clone();
        snoozed.SnoozedUntil = h.Now.AddMinutes(10);
        h.Store.SetAlarms(device.Id, [snoozed], new AlarmConfiguration { MuteUntil = EngineHarness.Start.AddMinutes(30) });
        await h.FromAppAsync();
        Assert.Null(device.ActiveAlarm);
        Assert.Empty(h.Apns.Stops);
    }

    [Fact]
    public async Task HandledWhileItsPushIsSent_StopsTheSystemAlarmTheRepeatStarted()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Store.BeforeRecordAlarmPush = () =>
        {
            h.Store.AlarmHandled(device.Id, low.Id);
            h.Store.BeforeRecordAlarmPush = null;
        };
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Apns.Alarms.Count);
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal((low.Id, AlarmStopReason.Silenced), (stop.AlarmId, stop.Reason));
        Assert.Null(device.ActiveAlarm);
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(2, h.Apns.Alarms.Count);
    }

    [Fact]
    public async Task CheckWhoseSaveIsRefused_SendsNothingAndTheNextCheckFiresOnce()
    {
        var h = new EngineHarness();
        var low = Low();
        var device = h.AlarmDevice([low]);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        // The app uploads its alarms while the check runs.
        h.Store.BeforeSave = () =>
        {
            h.Store.SetAlarms(device.Id, [low], new AlarmConfiguration());
            h.Store.BeforeSave = null;
        };
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Alarms);
        Assert.Null(device.ActiveAlarm);

        await h.FromAppAsync();
        await h.TicksAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(low.Id, Assert.Single(h.Apns.Alarms).AlarmId);
        Assert.Equal("200", device.ActiveAlarm?.LastPushStatus);
    }

    [Fact]
    public async Task Handled_ClearsTheAlarmSoTheConditionFiresAgainOnTheNextReading()
    {
        // Acknowledge (snooze 0) leaves the alarm unsnoozed; only "handled" tells the hub.
        var (h, device, low) = await FiredLowAsync();
        h.Store.AlarmHandled(device.Id, low.Id);
        Assert.Null(device.ActiveAlarm);
        Assert.Null(device.AlarmState!.CurrentAlarmId);
        await h.FromAppAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.Alarms);

        h.NextReading(68);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.Equal(low.Id, device.ActiveAlarm?.AlarmId);
    }

    [Fact]
    public async Task WithoutHandled_TheSoundingAlarmRepeatsRatherThanFiringAnew()
    {
        var (h, device, low) = await FiredLowAsync();
        h.NextReading(68);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.Equal(device.ActiveAlarm!.FiredAt, EngineHarness.Start);
        Assert.Equal(low.Id, device.AlarmState!.CurrentAlarmId);
    }

    [Fact]
    public async Task Handled_ThenSnooze_FiresAgainOnceTheSnoozeEnds()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Store.AlarmHandled(device.Id, low.Id);
        var snoozed = low.Clone();
        snoozed.SnoozedUntil = h.Now.AddMinutes(10);
        h.Store.SetAlarms(device.Id, [snoozed], new AlarmConfiguration());
        await h.FromAppAsync();

        h.NextReading(66);
        await h.NewDataAsync();
        Assert.Single(h.Apns.Alarms);

        h.NextReading(64);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.Alarms.Count);
    }

    [Fact]
    public async Task ConditionClearing_SendsAStopPush()
    {
        var (h, device, low) = await FiredLowAsync();
        h.NextReading(95);
        await h.NewDataAsync();
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal(low.Id, stop.AlarmId);
        Assert.Equal(h.Apns.Alarms[0].Title, stop.Title);
        Assert.Equal(AlarmStopReason.Cleared, stop.Reason);
        Assert.Null(device.ActiveAlarm);
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.Alarms);
        Assert.Single(h.Apns.Stops);
    }

    [Fact]
    public async Task HigherPriorityAlarm_StopsTheSoundingOne()
    {
        var h = new EngineHarness();
        var low = Low();
        var temporary = Alarm.Create(AlarmType.Temporary);
        var device = h.AlarmDevice([temporary, low]);
        h.Readings(90, 75);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(low.Id, Assert.Single(h.Apns.Alarms).AlarmId);

        h.NextReading(65);
        await h.NewDataAsync();
        Assert.Equal(temporary.Id, h.Apns.Alarms[^1].AlarmId);
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal(low.Id, stop.AlarmId);
        Assert.Equal(AlarmStopReason.RingDone, stop.Reason);
        Assert.Equal(temporary.Id, device.ActiveAlarm!.AlarmId);
    }

    [Fact]
    public async Task StopThatApnsRefuses_IsSentAgainAtTheNextChecks()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Apns.Result = new PushResult(false, "500 InternalServerError", false, 500);
        h.NextReading(95);
        await h.NewDataAsync();
        Assert.Single(h.Apns.Stops);
        Assert.Null(device.ActiveAlarm);

        h.Apns.Result = new PushResult(true, "200", false, 200);
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Apns.Stops.Count);
        Assert.All(h.Apns.Stops, s => Assert.Equal(low.Id, s.AlarmId));
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(2, h.Apns.Stops.Count);
    }

    [Fact]
    public async Task StopThatApnsKeepsRefusing_IsGivenUpAfterFifteenMinutes()
    {
        var (h, _, _) = await FiredLowAsync();
        h.Apns.Result = new PushResult(false, "500 InternalServerError", false, 500);
        h.NextReading(95);
        await h.NewDataAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(20));
        var tried = h.Apns.Stops.Count;
        Assert.InRange(tried, 2, 20);
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(tried, h.Apns.Stops.Count);
    }

    [Theory]
    [InlineData(true, true, null, 59, false)]   // a cleared condition rings out the minute, received or not
    [InlineData(true, true, null, 60, true)]
    [InlineData(false, false, null, 600, false)] // a single ring lasts 30 s from its delivery receipt
    [InlineData(false, false, 10, 39, false)]
    [InlineData(false, false, 10, 40, true)]
    [InlineData(true, false, 0, 600, false)]  // a repeating alarm rings until it is handled
    public void HasRungEnough(bool repeat, bool cleared, int? deliveredAfter, int secondsSinceFiring, bool expected)
    {
        var firedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var alarm = new ActiveAlarm(Guid.NewGuid(), "Low", "", null, "lfhub-Indeed.caf", "bell", "category",
            Ring: true, Repeat: repeat, FiredAt: firedAt, LastPushAt: firedAt, LastPushStatus: "200",
            ConditionCleared: cleared, DeliveredAt: deliveredAfter is { } s ? firedAt.AddSeconds(s) : null);
        Assert.Equal(expected, HubEngine.HasRungEnough(alarm, firedAt.AddSeconds(secondsSinceFiring)));
    }

    [Fact]
    public async Task NonRepeatingAlarm_RingsOnceItsReceiptArrivesThenStops()
    {
        var (h, device, low) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        Assert.True(h.Apns.Alarms[0].RingOnce);
        h.Advance(TimeSpan.FromSeconds(5));
        Assert.True(h.Store.Receipt(h.Apns.Alarms[0].Receipt));
        Assert.Equal(h.Now, device.ActiveAlarm!.DeliveredAt);
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Stops);
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        var alarm = Assert.Single(h.Apns.Alarms);
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal(low.Id, stop.AlarmId);
        Assert.Equal(AlarmStopReason.RingDone, stop.Reason);
        Assert.Equal((alarm.Title, alarm.Subtitle, alarm.Body), (stop.Title, stop.Subtitle, stop.Body));
        Assert.Null(device.ActiveAlarm);
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task NonRepeatingAlarm_WithoutAReceipt_IsSentEveryMinuteAndNotStopped()
    {
        var (h, device, low) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(4, h.Apns.Alarms.Count);
        Assert.All(h.Apns.Alarms, p => Assert.Equal(low.Id, p.AlarmId));
        Assert.Equal(4, h.Apns.Alarms.Select(p => p.Receipt).Distinct().Count());
        Assert.Empty(h.Apns.Stops);
        Assert.Equal(low.Id, device.ActiveAlarm?.AlarmId);

        // A receipt for any of its pushes counts; the alarm then rings out.
        Assert.True(h.Store.Receipt(h.Apns.Alarms[1].Receipt));
        Assert.False(h.Store.Receipt(h.Apns.Alarms[1].Receipt));
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(4, h.Apns.Alarms.Count);
        Assert.Equal(AlarmStopReason.RingDone, Assert.Single(h.Apns.Stops).Reason);
    }

    [Fact]
    public async Task NonRepeatingAlarmWithoutAReceipt_IsStoppedWhenItsConditionClears()
    {
        var (h, device, low) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        h.NextReading(95);
        await h.NewDataAsync();
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal((low.Id, AlarmStopReason.Cleared), (stop.AlarmId, stop.Reason));
        Assert.Null(device.ActiveAlarm);
    }

    [Fact]
    public async Task RepeatingAlarm_RecordsItsReceiptAndKeepsRepeating()
    {
        var (h, device, _) = await FiredLowAsync();
        h.Advance(TimeSpan.FromSeconds(3));
        var receivedAt = h.Now;
        Assert.True(h.Store.Receipt(h.Apns.Alarms[0].Receipt));
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(3, h.Apns.Alarms.Count);
        Assert.Equal(receivedAt, device.ActiveAlarm!.DeliveredAt);
        Assert.True(h.Store.Receipt(h.Apns.Alarms[2].Receipt));
        Assert.Equal(receivedAt, device.ActiveAlarm!.DeliveredAt);
        Assert.Empty(h.Apns.Stops);
    }

    [Fact]
    public async Task ReceiptForAnEarlierFiring_DoesNotCountForTheSoundingOne()
    {
        var (h, device, low) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        var first = h.Apns.Alarms[0].Receipt;
        h.Store.AlarmHandled(device.Id, low.Id);
        h.NextReading(65);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.True(h.Store.Receipt(first));
        Assert.Null(device.ActiveAlarm!.DeliveredAt);
        Assert.True(h.Store.Receipt(h.Apns.Alarms[1].Receipt));
        Assert.Equal(h.Now, device.ActiveAlarm!.DeliveredAt);
    }

    [Fact]
    public async Task ReceiptThatArrivesDuringACheck_IsKeptByThatChecksWrite()
    {
        var (h, device, _) = await FiredLowAsync();
        h.Store.BeforeSave = () =>
        {
            h.Store.Receipt(h.Apns.Alarms[0].Receipt);
            h.Store.BeforeSave = null;
        };
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.Equal(h.Now, device.ActiveAlarm!.DeliveredAt);
    }

    [Fact]
    public async Task ReceiptThatCannotBeKept_StillLetsTheAlarmGoOut()
    {
        var h = new EngineHarness();
        var low = Low();
        var device = h.AlarmDevice([low]);
        h.Readings(90, 70);
        h.Store.BeforeIssueReceipt = () => throw new InvalidOperationException("database down");
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(low.Id, Assert.Single(h.Apns.Alarms).AlarmId);
        Assert.Equal("200", device.ActiveAlarm?.LastPushStatus);
    }

    [Fact]
    public async Task Receipt_ExpiresAfterThirtyMinutes()
    {
        var (h, device, _) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        var token = h.Apns.Alarms[0].Receipt;
        h.Advance(AlarmReceipts.Lifetime);
        Assert.False(h.Store.Receipt(token));
        Assert.Null(device.ActiveAlarm!.DeliveredAt);
    }

    [Fact]
    public async Task AutoSnoozeOnCgmStart_StopsTheRingingAlarm()
    {
        var (h, device, _) = await FiredLowAsync(new AlarmConfiguration { AutoSnoozeCGMStart = true });
        h.Nightscout.Route("/api/v1/treatments.json", _ =>
            $$"""[{"_id":"s1","eventType":"Sensor Start","created_at":"{{h.Now.AddMinutes(-10):yyyy-MM-ddTHH:mm:ss.fffZ}}"}]""");
        h.Advance(TimeSpan.FromMinutes(10));
        await h.NewDataAsync();
        Assert.Single(h.Apns.Stops);
        Assert.Null(device.ActiveAlarm);
    }

    [Fact]
    public async Task Mute_DeliversWithTheDefaultSoundAndNoRing()
    {
        var (h, device, _) = await FiredLowAsync(new AlarmConfiguration { MuteUntil = EngineHarness.Start.AddMinutes(30) });
        var push = Assert.Single(h.Apns.Alarms);
        Assert.Equal("default", push.Sound);
        Assert.False(push.Ring);
        Assert.False(device.ActiveAlarm!.Repeat);
        Assert.True(h.Store.Receipt(push.Receipt));
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Alarms);
        Assert.Empty(h.Apns.Stops);
        Assert.Null(device.ActiveAlarm);
    }

    [Fact]
    public async Task SoundNever_DeliversWithTheDefaultSoundAndNoRing()
    {
        var (h, _, _) = await FiredLowAsync(edit: a => a.PlaySoundOption = PlaySoundOption.Never);
        var push = Assert.Single(h.Apns.Alarms);
        Assert.Equal("default", push.Sound);
        Assert.False(push.Ring);
    }

    [Fact]
    public async Task RepeatNever_IsDeliveredOnceWhenItsReceiptArrives()
    {
        var (h, _, _) = await FiredLowAsync(edit: a => a.RepeatSoundOption = RepeatSoundOption.Never);
        var push = Assert.Single(h.Apns.Alarms);
        Assert.True(push.Ring);
        Assert.True(h.Store.Receipt(push.Receipt));
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task CustomSound_UsesTheAppsConvertedFileName()
    {
        var id = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");
        var (h, _, _) = await FiredLowAsync(edit: a => a.SoundFile = SoundFile.Custom(id));
        Assert.Equal("lfhub-0F0E0D0C-0B0A-0908-0706-050403020100.caf", h.Apns.Alarms[0].Sound);
    }

    [Fact]
    public async Task AcknowledgeAlarm_UsesTheAckCategory()
    {
        var h = new EngineHarness();
        var temporary = Alarm.Create(AlarmType.Temporary);
        var device = h.AlarmDevice([temporary]);
        h.Readings(90, 60);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        var push = Assert.Single(h.Apns.Alarms);
        Assert.Equal("lfhub-ack", push.Category);
        Assert.Equal([temporary.Id], device.HubDisabledAlarms);
    }

    [Fact]
    public async Task HubDisabledAlarm_DoesNotFireAgain()
    {
        var h = new EngineHarness();
        var temporary = Alarm.Create(AlarmType.Temporary);
        var device = h.AlarmDevice([temporary]);
        h.Readings(90, 60);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        h.Store.AlarmHandled(device.Id, temporary.Id);
        h.NextReading(55);
        await h.NewDataAsync();
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task TemporaryAlarm_KeepsSoundingAfterTurningItselfOffUntilHandled()
    {
        var h = new EngineHarness();
        var temporary = Alarm.Create(AlarmType.Temporary);
        var device = h.AlarmDevice([temporary]);
        h.Readings(90, 60);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.True(device.ActiveAlarm!.DisabledWhenFired);

        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(3, h.Apns.Alarms.Count);
        Assert.Equal(temporary.Id, device.AlarmState!.CurrentAlarmId);

        // The app's export shows it off too: it still sounds.
        var off = temporary.Clone();
        off.IsEnabled = false;
        h.Store.SetAlarms(device.Id, [off], new AlarmConfiguration());
        Assert.Empty(device.HubDisabledAlarms);
        await h.FromAppAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(4, h.Apns.Alarms.Count);
        Assert.Empty(h.Apns.Stops);
        Assert.Equal(temporary.Id, device.ActiveAlarm?.AlarmId);

        h.Store.AlarmHandled(device.Id, temporary.Id);
        await h.FromAppAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(4, h.Apns.Alarms.Count);
        Assert.Null(device.ActiveAlarm);
    }

    [Fact]
    public async Task TemporaryAlarm_IsSilencedBySnoozeAll()
    {
        var h = new EngineHarness();
        var temporary = Alarm.Create(AlarmType.Temporary);
        var device = h.AlarmDevice([temporary]);
        h.Readings(90, 60);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        h.Store.SetAlarms(device.Id, [temporary], new AlarmConfiguration { SnoozeUntil = h.Now.AddMinutes(30) });
        await h.FromAppAsync();
        Assert.Null(device.ActiveAlarm);
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task CriticalAlerts_FollowTheDeviceSetting()
    {
        var h = new EngineHarness();
        h.AlarmDevice([Low()], edit: r => r.Settings["criticalAlerts"] = true);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.True(Assert.Single(h.Apns.Alarms).Critical);
    }

    [Fact]
    public async Task AlarmsOff_NothingIsChecked()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Low()], edit: r => r.Settings["alarms"] = false);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Alarms);
        Assert.Null(device.AlarmStateJson);
    }

    [Fact]
    public async Task AlarmsTurnedOff_StopTheSoundingAlarmAndClearIt()
    {
        var (h, device, low) = await FiredLowAsync();
        device.Settings["alarms"] = false;
        h.Advance(TimeSpan.FromSeconds(5));
        await h.TickAsync();
        var stop = Assert.Single(h.Apns.Stops);
        Assert.Equal(low.Id, stop.AlarmId);
        Assert.Equal(AlarmStopReason.Silenced, stop.Reason);
        Assert.Null(device.ActiveAlarm);
        Assert.Null(device.AlarmState!.CurrentAlarmId);

        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Single(h.Apns.Stops);
        Assert.Single(h.Apns.Alarms);

        // Turned back on, the condition fires anew on the next reading.
        device.Settings["alarms"] = true;
        h.NextReading(68);
        await h.NewDataAsync();
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.Equal(h.Now, device.ActiveAlarm?.FiredAt);
    }

    [Fact]
    public async Task AlarmExport_IsParsedOnceForEachUpload()
    {
        var h = new EngineHarness();
        var low = Low();
        var row = h.AlarmDevice([low]);
        var first = h.Engine.AlarmExport(row.ToDevice())!.Value;
        Assert.Same(first.Alarms, h.Engine.AlarmExport(row.ToDevice())!.Value.Alarms);

        var higher = low.Clone();
        higher.BelowBG = 90;
        h.Store.SetAlarms(row.Id, [higher], new AlarmConfiguration());
        var second = h.Engine.AlarmExport(row.ToDevice())!.Value;
        Assert.NotSame(first.Alarms, second.Alarms);
        Assert.Equal(90, Assert.Single(second.Alarms).BelowBG);
        Assert.Null(h.Engine.AlarmExport(h.AddDevice().ToDevice()));
    }

    [Fact]
    public async Task AlarmsOnWithoutAnApnsKeyThatOpens_AreLoggedOnceUntilItOpens()
    {
        var h = new EngineHarness();
        var apns = new ApnsConfig("a1b2c3d4", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "unused");
        var device = h.AlarmDevice([Low()], edit: r =>
        {
            r.Apns = null;
            r.ApnsKeyDoesNotOpen = true;
        });
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        bool Warning(string l) => l.StartsWith("Warning", StringComparison.Ordinal) &&
                                  l.Contains("does not open with LFHUB_MASTER_KEY", StringComparison.Ordinal);
        Assert.Single(h.Logs.Logger.Lines, Warning);
        Assert.Empty(h.Apns.Alarms);

        device.Apns = apns;
        device.ApnsKeyDoesNotOpen = false;
        await h.NewDataAsync();
        Assert.Single(h.Apns.Alarms);
        device.Apns = null;
        device.ApnsKeyDoesNotOpen = true;
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Logs.Logger.Lines.Count(Warning));
    }

    [Fact]
    public async Task Checks_RunOncePerMinuteUnlessSomethingArrives()
    {
        var h = new EngineHarness();
        var low = Low();
        var device = h.AlarmDevice([low]);
        h.Readings(90, 85);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Alarms);

        // The app's alarm list changes: an immediate check sees it.
        var higher = low.Clone();
        higher.BelowBG = 90;
        h.Advance(TimeSpan.FromSeconds(5));
        h.Store.SetAlarms(device.Id, [higher], new AlarmConfiguration());
        await h.TickAsync();
        Assert.Empty(h.Apns.Alarms);
        await h.FromAppAsync();
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task FailedDelivery_IsRecordedAndRetriedOnTheNextRepeat()
    {
        var h = new EngineHarness();
        var device = h.AlarmDevice([Low()]);
        h.Readings(90, 70);
        h.Apns.Result = new PushResult(false, "transport: timeout", false);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal("transport: timeout", device.ActiveAlarm!.LastPushStatus);
        h.Apns.Result = new PushResult(true, "200", false, 200);
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Apns.Alarms.Count);
        Assert.Equal("200", device.ActiveAlarm!.LastPushStatus);
    }

    [Fact]
    public async Task LatestBg_SentOncePerReadingWhenNoAlarmFired()
    {
        var h = new EngineHarness();
        h.AlarmDevice([Low()], edit: r => r.ClientSettings["persistentNotification"] = true);
        h.Readings(120, 125);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(["125 → +5"], h.Apns.LatestBg);
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.LatestBg);
        h.NextReading(70);
        await h.NewDataAsync();
        Assert.Single(h.Apns.LatestBg);
        Assert.Single(h.Apns.Alarms);
    }

    [Fact]
    public async Task MissedReading_FiresFromTheHubWhenReadingsStop()
    {
        var h = new EngineHarness();
        var missed = Alarm.Create(AlarmType.MissedReading);
        h.AlarmDevice([missed]);
        h.Readings(120, 121);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Alarms);
        await h.TicksAsync(TimeSpan.FromMinutes(25));
        Assert.Equal(missed.Id, Assert.Single(h.Apns.Alarms.DistinctBy(a => a.AlarmId)).AlarmId);
    }

    [Fact]
    public async Task MistypedClientSettingsOnOneDevice_DoNotStopAnotherDevicesAlarms()
    {
        var h = new EngineHarness();
        h.AddDevice(r =>
        {
            r.Settings = new System.Text.Json.Nodes.JsonObject { ["badge"] = "yes", ["alarms"] = 1, ["unit"] = true };
            r.ClientSettings = new System.Text.Json.Nodes.JsonObject
            {
                ["timeZone"] = 5,
                ["units"] = 5,
                ["downloadDays"] = "x",
                ["infoDisplayItems"] = "x",
                ["predictionToLoad"] = "a",
                ["decimalSeparator"] = 1,
                ["persistentNotification"] = "yes",
            };
        });
        var low = Low();
        h.AlarmDevice([low]);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(low.Id, Assert.Single(h.Apns.Alarms).AlarmId);
    }

    [Fact]
    public async Task FailingLiveActivity_DoesNotStopAlarms()
    {
        var h = new EngineHarness();
        var low = Low();
        h.AlarmDevice([low], edit: r =>
        {
            r.Settings["liveActivity"] = true;
            r.LaToken = "activity-token";
        });
        h.Apns.ThrowOnLiveActivity = true;
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Equal(low.Id, Assert.Single(h.Apns.Alarms).AlarmId);
        Assert.Contains(h.Logs.Logger.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal)
            && l.Contains("Live Activity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonRepeatingAlarm_IsRetriedUntilAPushIsAcceptedAndReceived()
    {
        var h = new EngineHarness();
        var low = Low(a => a.RepeatSoundOption = RepeatSoundOption.Never);
        var device = h.AlarmDevice([low]);
        h.Readings(90, 70);
        h.Apns.Result = new PushResult(false, "503 ServiceUnavailable", false, 503);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Apns.Alarms.Count);
        h.Apns.Result = new PushResult(true, "200", false, 200);
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(3, h.Apns.Alarms.Count);
        Assert.Equal("200", device.ActiveAlarm!.LastPushStatus);
        Assert.True(h.Store.Receipt(h.Apns.Alarms[2].Receipt));
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(3, h.Apns.Alarms.Count);
    }

    [Fact]
    public async Task HandledDuringACheck_IsNotOverwrittenByThatCheck()
    {
        var (h, device, low) = await FiredLowAsync();
        h.Store.BeforeSave = () =>
        {
            h.Store.AlarmHandled(device.Id, low.Id);
            h.Store.BeforeSave = null;
        };
        await h.TicksAsync(TimeSpan.FromMinutes(1));
        Assert.Null(device.ActiveAlarm);
        Assert.Null(device.AlarmState!.CurrentAlarmId);
        var sent = h.Apns.Alarms.Count;
        await h.TicksAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(sent, h.Apns.Alarms.Count);
    }

    [Fact]
    public async Task UnansweringNightscout_DoesNotHoldUpTheAlarmCheck()
    {
        var h = new EngineHarness(TimeSpan.FromMilliseconds(300));
        var low = Low();
        h.AlarmDevice([low]);
        h.Readings(90, 70);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        Assert.Single(h.Apns.Alarms);

        h.Nightscout.Hang.Add("/api/v1/devicestatus.json");
        h.Nightscout.Hang.Add("/api/v1/entries.json");
        var started = DateTime.UtcNow;
        await h.TicksAsync(TimeSpan.FromMinutes(5));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        Assert.True(h.Apns.Alarms.Count >= 5);
    }
}
