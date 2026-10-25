using System.Text.Json;
using LFHub.Engine;
using LFHub.Engine.Alarms;
using static LFHub.Tests.Alarms.AlarmTestKit;

namespace LFHub.Tests.Alarms;

/// The evaluator's rules across types: snoozes, Snooze All and Mute, day and night,
/// priority, once per reading, notification text and state persistence.
public class AlarmRuleTests
{
    [Fact]
    public void SnoozedAlarm_SkipsLowerAlarmsOfItsTypeOnly()
    {
        var low80 = A(AlarmType.Low);
        var low55 = A(AlarmType.Low, a => { a.BelowBG = 55; a.SnoozedUntil = T0.AddMinutes(10); });
        Assert.Null(Eval([low80, low55], Data(T0, [60, 50]), T0).Fire);
        var drop = A(AlarmType.FastDrop);
        Assert.True(Fires(Eval([low80, low55, drop], Data(T0, [110, 90, 70, 50]), T0), drop));
    }

    [Fact]
    public void SnoozedAlarm_HigherRankedAlarmOfTheTypeStillFires()
    {
        var low55 = A(AlarmType.Low, a => a.BelowBG = 55);
        var low80 = A(AlarmType.Low, a => a.SnoozedUntil = T0.AddMinutes(10));
        Assert.True(Fires(Eval([low80, low55], Data(T0, [60, 50]), T0), low55));
    }

    [Fact]
    public void ExpiredSnooze_Fires()
    {
        var expired = A(AlarmType.Low, a => a.SnoozedUntil = T0.AddMinutes(-1));
        Assert.True(Fires(Eval([expired], Data(T0, [90, 70]), T0), expired));
    }

    [Fact]
    public void AutoSnoozeOnCgmStart_TwoHoursFromSensorStart()
    {
        var cfg = Cfg();
        cfg.AutoSnoozeCGMStart = true;
        Assert.Equal(T0.AddMinutes(90), AlarmSnooze.AutoSnoozeOnCgmStart(cfg, T0.AddMinutes(-30), T0));
        Assert.Null(AlarmSnooze.AutoSnoozeOnCgmStart(cfg, T0.AddHours(-3), T0));
        Assert.Null(AlarmSnooze.AutoSnoozeOnCgmStart(Cfg(), T0.AddMinutes(-30), T0));
    }

    [Fact]
    public void SnoozeAll_NothingFiresAndTheSoundingAlarmStops()
    {
        var low = A(AlarmType.Low);
        var cfg = Cfg();
        cfg.SnoozeUntil = T0.AddMinutes(30);
        Assert.Null(Eval([low], Data(T0, [90, 60]), T0, cfg: cfg).Fire);

        var sounding = Eval([low], Data(T0, [90, 60]), T0);
        var e = Eval([low], Data(T0.AddMinutes(1), [60, 59]), T0.AddMinutes(1), sounding.State, cfg);
        Assert.Equal(low.Id, e.StoppedAlarmId);
        Assert.Null(e.State.CurrentAlarmId);

        cfg.SnoozeUntil = T0.AddMinutes(-1);
        Assert.True(Fires(Eval([low], Data(T0, [90, 60]), T0, cfg: cfg), low));
    }

    [Fact]
    public void Mute_StillNotifiesWithoutSound()
    {
        var low = A(AlarmType.Low);
        var mute = Cfg();
        mute.MuteUntil = T0.AddMinutes(30);
        var m = Eval([low], Data(T0, [90, 60]), T0, cfg: mute);
        Assert.True(Fires(m, low));
        Assert.False(m.Fire!.PlaySound);
        Assert.True(Eval([low], Data(T0, [90, 60]), T0).Fire!.PlaySound);
    }

    [Fact]
    public void ForcedVolume_FollowsOverrideSetting()
    {
        var low = A(AlarmType.Low);
        Assert.Equal(0.5f, Eval([low], Data(T0, [90, 60]), T0).Fire!.ForcedOutputVolume);
        var off = Cfg();
        off.OverrideSystemOutputVolume = false;
        Assert.Null(Eval([low], Data(T0, [90, 60]), T0, cfg: off).Fire!.ForcedOutputVolume);
    }

    [Fact]
    public void ActiveOption_DayAndNight()
    {
        var night = A(AlarmType.High, a => a.ActiveOption = ActiveOption.Night);
        var day = A(AlarmType.High, a => a.ActiveOption = ActiveOption.Day);
        var late = T0.Date.AddHours(23);
        Assert.Null(Eval([night], Data(T0, [200, 210]), T0).Fire);
        Assert.True(Fires(Eval([night], Data(late, [200, 210]), late), night));
        Assert.True(Fires(Eval([day], Data(T0, [200, 210]), T0), day));
        Assert.Null(Eval([day], Data(late, [200, 210]), late).Fire);
    }

    [Fact]
    public void ActiveOption_UsesThePhonesTimeZone()
    {
        var night = A(AlarmType.High, a => a.ActiveOption = ActiveOption.Night);
        var summer = new DateTime(2026, 7, 1, 21, 30, 0, DateTimeKind.Utc);
        Assert.Null(Eval([night], Data(summer, [200, 210]), summer, tz: Utc).Fire);
        Assert.True(Fires(Eval([night], Data(summer, [200, 210]), summer, tz: Stockholm), night));
        var winter = new DateTime(2026, 12, 1, 21, 30, 0, DateTimeKind.Utc);
        Assert.True(Fires(Eval([night], Data(winter, [200, 210]), winter, tz: Stockholm), night));
    }

    [Fact]
    public void PlaySoundOption_DayOnly()
    {
        var playDay = A(AlarmType.High, a =>
        {
            a.PlaySoundOption = PlaySoundOption.Day;
            a.RepeatSoundOption = RepeatSoundOption.Day;
            a.SoundDelay = 10;
        });
        var late = T0.Date.AddHours(23);
        var atNight = Eval([playDay], Data(late, [200, 210]), late).Fire!;
        Assert.False(atNight.PlaySound);
        Assert.False(atNight.RepeatSound);
        Assert.Equal(0, atNight.SoundDelaySeconds);
        var byDay = Eval([playDay], Data(T0, [200, 210]), T0).Fire!;
        Assert.True(byDay.PlaySound);
        Assert.True(byDay.RepeatSound);
        Assert.Equal(10, byDay.SoundDelaySeconds);
    }

    [Fact]
    public void PlaySoundOption_Never()
    {
        var never = A(AlarmType.High, a =>
        {
            a.PlaySoundOption = PlaySoundOption.Never;
            a.RepeatSoundOption = RepeatSoundOption.Never;
            a.SoundDelay = 10;
        });
        var f = Eval([never], Data(T0, [200, 210]), T0).Fire!;
        Assert.False(f.PlaySound);
        Assert.False(f.RepeatSound);
        Assert.Equal(0, f.SoundDelaySeconds);
    }

    [Fact]
    public void InvertedDayNightWindow_MatchesLoopFollow()
    {
        var cfg = Cfg();
        cfg.DayStart = new TimeOfDay(20, 0);
        cfg.NightStart = new TimeOfDay(8, 0);
        var playNight = A(AlarmType.High, a => a.PlaySoundOption = PlaySoundOption.Night);
        var nightOnly = A(AlarmType.High, a => a.ActiveOption = ActiveOption.Night);
        var at10 = T0.Date.AddHours(10);
        Assert.True(Eval([playNight], Data(at10, [200, 210]), at10, cfg: cfg).Fire!.PlaySound);
        Assert.True(Fires(Eval([nightOnly], Data(at10, [200, 210]), at10, cfg: cfg), nightOnly));
        var at21 = T0.Date.AddHours(21);
        Assert.True(Eval([nightOnly], Data(at21, [200, 210]), at21, cfg: cfg).Fire?.PlaySound);
        Assert.False(Eval([playNight], Data(at21, [200, 210]), at21, cfg: cfg).Fire!.PlaySound);
    }

    [Fact]
    public void Priority_OneAlarmPerCheckInTypeOrder()
    {
        var temp = A(AlarmType.Temporary);
        var low = A(AlarmType.Low);
        var drop = A(AlarmType.FastDrop);
        var iob = A(AlarmType.Iob);
        var d = Data(T0, [120, 100, 80, 60], x => x with { Iob = 8 });
        Assert.True(Fires(Eval([drop, low, iob, temp], d, T0), temp));
        Assert.True(Fires(Eval([drop, low, iob], d, T0), iob));
        Assert.True(Fires(Eval([drop, low], d, T0), low));
    }

    [Fact]
    public void Priority_SortsByTypeThenSpec()
    {
        var lows = new[] { A(AlarmType.Low, a => a.BelowBG = 80), A(AlarmType.Low, a => a.BelowBG = 55), A(AlarmType.Low, a => a.BelowBG = 70) };
        var highs = new[] { A(AlarmType.High, a => a.AboveBG = 180), A(AlarmType.High, a => a.AboveBG = 250) };
        var order = AlarmEvaluator.SortByPriorityThenSpec([.. highs, .. lows]);
        Assert.Equal(new double?[] { 55, 70, 80, 250, 180 }, order.Select(a => a.Type == AlarmType.Low ? a.BelowBG : a.AboveBG));
        Assert.True(Fires(Eval(lows, Data(T0, [60, 50]), T0), lows[1]));
        var cobs = new[] { A(AlarmType.Cob, a => a.Threshold = 20), A(AlarmType.Cob, a => a.Threshold = 40) };
        Assert.Equal(40, AlarmEvaluator.SortByPriorityThenSpec(cobs)[0].Threshold);
        var same = new[] { A(AlarmType.Pump, a => a.Name = "x"), A(AlarmType.Pump, a => a.Name = "y") };
        Assert.Equal(["x", "y"], AlarmEvaluator.SortByPriorityThenSpec(same).Select(a => a.Name));
    }

    [Fact]
    public void DisabledAlarm_NeverFires() =>
        Assert.Null(Eval([A(AlarmType.Low, a => a.IsEnabled = false)], Data(T0, [60, 50]), T0).Fire);

    [Fact]
    public void BgAlarm_FiresOncePerReadingAndStopsWhenTheConditionClears()
    {
        var low = A(AlarmType.Low);
        var e1 = Eval([low], Data(T0, [90, 70]), T0);
        Assert.True(Fires(e1, low));
        Assert.Equal(low.Id, e1.State.CurrentAlarmId);

        var e2 = Eval([low], Data(T0, [90, 70]), T0.AddSeconds(30), e1.State);
        Assert.Null(e2.Fire);
        Assert.Null(e2.StoppedAlarmId);

        var ack = e1.State.Handled();
        Assert.Null(Eval([low], Data(T0, [90, 70]), T0.AddSeconds(60), ack).Fire);

        var t2 = T0.AddMinutes(5);
        var e4 = Eval([low], Data(t2, [70, 68]), t2, ack);
        Assert.True(Fires(e4, low));

        var t3 = t2.AddMinutes(5);
        var e5 = Eval([low], Data(t3, [70, 68]), t3.AddMinutes(1), e4.State);
        Assert.Null(e5.Fire);
        Assert.Equal(low.Id, e5.State.CurrentAlarmId);

        var e6 = Eval([low], Data(t3, [68, 95]), t3, e4.State);
        Assert.Equal(low.Id, e6.StoppedAlarmId);
        Assert.Null(e6.State.CurrentAlarmId);
    }

    [Fact]
    public void NonBgAlarm_RefiresOnTheSameReadingAfterAcknowledge()
    {
        var pump = A(AlarmType.Pump);
        var p1 = Eval([pump], Data(T0, with: d => d with { LatestPumpVolume = 10 }), T0);
        var ack = p1.State.Handled();
        Assert.True(Fires(Eval([pump], Data(T0, with: d => d with { LatestPumpVolume = 10 }), T0.AddMinutes(1), ack), pump));
    }

    [Fact]
    public void Text_SubtitleInMgdlAndMmol()
    {
        var high = A(AlarmType.High);
        var f = Eval([high], Data(T0, [195, 200]), T0, text: new AlarmTextContext("FortyFiveUp")).Fire!;
        Assert.Equal("200 ↗ +5", f.Subtitle);
        var fm = Eval([high], Data(T0, [195, 200]), T0, mmol: true, text: new AlarmTextContext("FortyFiveDown")).Fire!;
        Assert.Equal("11.1 ↘︎ +0.3", fm.Subtitle);
    }

    [Fact]
    public void Text_LowAndHighOutsideTheDisplayRange()
    {
        Assert.Equal("LOW  -6", Eval([A(AlarmType.Low)], Data(T0, [45, 39]), T0).Fire!.Subtitle);
        var fh = Eval([A(AlarmType.High)], Data(T0, [380, 401]), T0, mmol: true, text: new AlarmTextContext("DoubleUp")).Fire!;
        Assert.Equal("HIGH ↑↑ +1.2", fh.Subtitle);
    }

    [Fact]
    public void Text_ActionTitle()
    {
        Assert.Equal("Snooze", Eval([A(AlarmType.High)], Data(T0, [195, 200]), T0).Fire!.ActionTitle);
        Assert.Equal("Acknowledge", Eval([A(AlarmType.Temporary)], Data(T0, [90, 60]), T0).Fire!.ActionTitle);
    }

    [Fact]
    public void Text_BodyCarriesTheCgmStateForMissedReadingOnly()
    {
        var mr = A(AlarmType.MissedReading);
        var stale = Data(T0, with: d => d with { BgReadings = [new(120, T0.AddMinutes(-25)), new(121, T0.AddMinutes(-20))] });
        var fb = Eval([mr], stale, T0, text: new AlarmTextContext(null, "Sensor warmup at 22:33")).Fire!;
        Assert.Equal("CGM reported: Sensor warmup at 22:33", fb.Body);
        Assert.Null(Eval([A(AlarmType.High)], Data(T0, [195, 200]), T0, text: new AlarmTextContext(null, "x")).Fire!.Body);
    }

    [Fact]
    public void Firing_CarriesAlarmNameAndSound()
    {
        var f = Eval([A(AlarmType.High)], Data(T0, [195, 200]), T0).Fire!;
        Assert.Equal("High BG Alert", f.AlarmName);
        Assert.Equal(SoundFile.Builtin("Time_Has_Come"), f.SoundFile);
    }

    [Fact]
    public void Evaluate_LeavesItsInputStateUntouched()
    {
        var low = A(AlarmType.Low);
        var st = Fresh(T0);
        st.PendingFutureCarbs.Add(new PendingFutureCarb(T0.AddMinutes(10), 20, T0));
        var before = JsonSerializer.Serialize(st);
        var e = Eval([low, A(AlarmType.FutureCarbs)], Data(T0, [90, 70]), T0, st);
        Assert.Equal(before, JsonSerializer.Serialize(st));
        Assert.Equal(low.Id, e.State.CurrentAlarmId);
    }

    [Fact]
    public void State_RoundTripsThroughJsonWithUtcDates()
    {
        var st = Fresh(T0);
        st.PendingFutureCarbs.Add(new PendingFutureCarb(T0.AddMinutes(10), 20, T0));
        var e = Eval([A(AlarmType.Low), A(AlarmType.FutureCarbs)], Data(T0, [90, 70]), T0, st);
        var json = JsonSerializer.Serialize(e.State);
        var back = JsonSerializer.Deserialize<AlarmState>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Equal(DateTimeKind.Utc, back.LastBGAlarmTime?.Kind);
        Assert.Equal(DateTimeKind.Utc, back.PendingFutureCarbs[0].CarbDate.Kind);
    }
}
