using LFHub.Engine;
using LFHub.Engine.Alarms;
using static LFHub.Tests.Alarms.AlarmTestKit;

namespace LFHub.Tests.Alarms;

/// Each alarm type against data that should and should not fire it.
public class AlarmConditionTests
{
    [Fact]
    public void Temporary_FiresBelowAndAboveItsLimits()
    {
        AssertPair(AlarmType.Temporary, A(AlarmType.Temporary), Data(T0, [80, 65]), Data(T0, [100, 101]));
        var t = A(AlarmType.Temporary);
        Assert.True(Fires(Eval([t], Data(T0, [80, 190]), T0), t));
    }

    [Fact]
    public void Temporary_DisablesItselfWhenItFires()
    {
        var t = A(AlarmType.Temporary);
        Assert.Equal([t.Id], Eval([t], Data(T0, [80, 190]), T0).DisableAlarms);
    }

    [Fact]
    public void Temporary_WithoutLimitsNeverFires()
    {
        var none = A(AlarmType.Temporary, a => { a.AboveBG = null; a.BelowBG = null; });
        Assert.Null(Eval([none], Data(T0, [40, 40]), T0).Fire);
    }

    [Fact]
    public void Temporary_NeedsARecentReading() =>
        Assert.Null(Eval([A(AlarmType.Temporary)], Data(T0.AddMinutes(10), [65, 60]), T0.AddMinutes(20)).Fire);

    [Fact]
    public void Iob_FiresAboveThreshold() =>
        AssertPair(AlarmType.Iob, A(AlarmType.Iob), Data(T0, with: d => d with { Iob = 6.5 }),
            Data(T0, with: d => d with { Iob = 2 }));

    [Fact]
    public void Iob_CountsBolusesWithinItsWindow()
    {
        var a = A(AlarmType.Iob);
        var two = Data(T0, with: d => d with { Iob = 1, RecentBoluses = [new(1.5, T0.AddMinutes(-20)), new(1.2, T0.AddMinutes(-5))] });
        Assert.True(Fires(Eval([a], two, T0), a));
        var old = Data(T0, with: d => d with { Iob = 1, RecentBoluses = [new(1.5, T0.AddMinutes(-40)), new(1.2, T0.AddMinutes(-5))] });
        Assert.Null(Eval([a], old, T0).Fire);
        var small = Data(T0, with: d => d with { Iob = 1, RecentBoluses = [new(0.5, T0.AddMinutes(-20)), new(0.5, T0.AddMinutes(-5))] });
        Assert.Null(Eval([a], small, T0).Fire);
    }

    [Fact]
    public void Cob_FiresAboveThreshold() =>
        AssertPair(AlarmType.Cob, A(AlarmType.Cob), Data(T0, with: d => d with { Cob = 25 }),
            Data(T0, with: d => d with { Cob = 10 }));

    [Fact]
    public void Cob_RefiresOnlyWhenRising()
    {
        var a = A(AlarmType.Cob);
        var e1 = Eval([a], Data(T0, with: d => d with { Cob = 25 }), T0);
        var e2 = Eval([a], Data(T0.AddMinutes(1), with: d => d with { Cob = 25 }), T0.AddMinutes(1), e1.State);
        Assert.Null(e2.Fire);
        Assert.Equal(a.Id, e2.StoppedAlarmId);
        var e3 = Eval([a], Data(T0.AddMinutes(2), with: d => d with { Cob = 30 }), T0.AddMinutes(2), e2.State);
        Assert.True(Fires(e3, a));
        var e4 = Eval([a], Data(T0.AddMinutes(3), with: d => d with { Cob = 5 }), T0.AddMinutes(3), e3.State);
        Assert.Null(e4.State.LastCOBNotified);
    }

    [Fact]
    public void Low_FiresBelowLimit() =>
        AssertPair(AlarmType.Low, A(AlarmType.Low), Data(T0, [90, 78]), Data(T0, [90, 85]));

    [Fact]
    public void Low_PredictiveLooksAheadWithinItsHorizon()
    {
        var pred = A(AlarmType.Low, a => a.PredictiveMinutes = 15);
        var p = new List<GlucoseValue>
        {
            new(100, T0), new(92, T0.AddMinutes(5)), new(84, T0.AddMinutes(10)), new(79, T0.AddMinutes(15)),
            new(60, T0.AddMinutes(20)),
        };
        Assert.True(Fires(Eval([pred], Data(T0, [105, 100], d => d with { PredictionData = p }), T0), pred));
        var far = p.Select((g, i) => i < 4 ? g with { Sgv = 100 } : g).ToList();
        Assert.Null(Eval([pred], Data(T0, [105, 100], d => d with { PredictionData = far }), T0).Fire);
    }

    [Fact]
    public void Low_SuppressIfRising()
    {
        var rising = A(AlarmType.Low, a => a.SuppressIfRising = true);
        Assert.Null(Eval([rising], Data(T0, [65, 70]), T0).Fire);
        Assert.True(Fires(Eval([rising], Data(T0, [70, 70]), T0), rising));
    }

    [Fact]
    public void Low_PersistentNeedsTheWholeWindowLow()
    {
        var persist = A(AlarmType.Low, a => a.PersistentMinutes = 15);
        Assert.Null(Eval([persist], Data(T0, [70, 85, 75, 70]), T0).Fire);
        Assert.True(Fires(Eval([persist], Data(T0, [85, 75, 72, 70]), T0), persist));
    }

    [Fact]
    public void Low_ZeroReadingNeverFires() =>
        Assert.Null(Eval([A(AlarmType.Low)], Data(T0, [70, 0]), T0).Fire);

    [Fact]
    public void High_FiresAboveLimit() =>
        AssertPair(AlarmType.High, A(AlarmType.High), Data(T0, [170, 185]), Data(T0, [170, 175]));

    [Fact]
    public void High_SuppressIfFalling()
    {
        var falling = A(AlarmType.High, a => a.SuppressIfFalling = true);
        Assert.Null(Eval([falling], Data(T0, [220, 210]), T0).Fire);
        Assert.True(Fires(Eval([falling], Data(T0, [200, 210]), T0), falling));
    }

    [Fact]
    public void High_PersistentNeedsEveryReadingHigh()
    {
        var persist = A(AlarmType.High, a => a.PersistentMinutes = 10);
        Assert.Null(Eval([persist], Data(T0, [170, 200]), T0).Fire);
        Assert.True(Fires(Eval([persist], Data(T0, [190, 200]), T0), persist));
    }

    [Fact]
    public void FastDrop_FiresOnConsecutiveDrops() =>
        AssertPair(AlarmType.FastDrop, A(AlarmType.FastDrop), Data(T0, [160, 140, 120, 100]), Data(T0, [160, 140, 130, 100]));

    [Fact]
    public void FastDrop_BelowBgActivationLimit()
    {
        var limited = A(AlarmType.FastDrop, a => a.BelowBG = 110);
        Assert.Null(Eval([limited], Data(T0, [190, 170, 150, 130]), T0).Fire);
        Assert.True(Fires(Eval([limited], Data(T0, [160, 140, 120, 100]), T0), limited));
    }

    [Fact]
    public void FastDrop_NeedsEnoughReadings() =>
        Assert.Null(Eval([A(AlarmType.FastDrop)], Data(T0, [140, 120, 100]), T0).Fire);

    [Fact]
    public void FastRise_FiresOnConsecutiveRises() =>
        AssertPair(AlarmType.FastRise, A(AlarmType.FastRise), Data(T0, [100, 110, 120, 130]), Data(T0, [100, 110, 115, 130]));

    private static AlarmData StaleBg() =>
        Data(T0, with: d => d with { BgReadings = [new(120, T0.AddMinutes(-25)), new(121, T0.AddMinutes(-20))] });

    [Fact]
    public void MissedReading_FiresWhenReadingsStop() =>
        AssertPair(AlarmType.MissedReading, A(AlarmType.MissedReading), StaleBg(), Data(T0, [120, 121]));

    [Fact]
    public void MissedReading_SilentWithoutARecentCheck()
    {
        var a = A(AlarmType.MissedReading);
        Assert.Null(Eval([a], StaleBg(), T0, new AlarmState()).Fire);
        Assert.Null(Eval([a], StaleBg(), T0, new AlarmState { LastBGChecked = T0.AddMinutes(-7) }).Fire);
    }

    [Fact]
    public void MissedReading_SilentWithoutReadings() =>
        Assert.Null(Eval([A(AlarmType.MissedReading)], Data(T0, with: d => d with { BgReadings = [] }), T0).Fire);

    [Fact]
    public void MissedReading_AcknowledgedGapDoesNotRefireOnTheSameReading()
    {
        var a = A(AlarmType.MissedReading);
        var e1 = Eval([a], StaleBg(), T0);
        var e2 = Eval([a], StaleBg(), T0.AddMinutes(2), e1.State.Handled());
        Assert.Null(e2.Fire);
    }

    [Fact]
    public void NotLooping_FiresAfterItsThreshold()
    {
        var a = A(AlarmType.NotLooping);
        AssertPair(AlarmType.NotLooping, a, Data(T0, with: d => d with { LastLoopTime = T0.AddMinutes(-20) }),
            Data(T0, with: d => d with { LastLoopTime = T0.AddMinutes(-5) }));
    }

    [Fact]
    public void NotLooping_SilentWithoutRecentCheckOrLoopTime()
    {
        var a = A(AlarmType.NotLooping);
        Assert.Null(Eval([a], Data(T0, with: d => d with { LastLoopTime = T0.AddMinutes(-20) }), T0,
            new AlarmState { LastLoopingChecked = T0.AddMinutes(-10) }).Fire);
        Assert.Null(Eval([a], Data(T0), T0).Fire);
    }

    private static AlarmData Carbs(double grams, int minutesAgo, params BolusEntry[] boluses) =>
        Data(T0, with: d => d with { RecentCarbs = [new(grams, T0.AddMinutes(-minutesAgo))], RecentBoluses = boluses });

    [Fact]
    public void MissedBolus_FiresForCarbsWithoutBolus() =>
        AssertPair(AlarmType.MissedBolus, A(AlarmType.MissedBolus), Carbs(20, 20), Carbs(20, 20, new BolusEntry(2, T0.AddMinutes(-25))));

    [Fact]
    public void MissedBolus_PrebolusBeforeWindowDoesNotCount()
    {
        var a = A(AlarmType.MissedBolus);
        Assert.True(Fires(Eval([a], Carbs(20, 20, new BolusEntry(2, T0.AddMinutes(-40))), T0), a));
    }

    [Fact]
    public void MissedBolus_IgnoresSmallTooRecentAndOldCarbs()
    {
        var a = A(AlarmType.MissedBolus);
        Assert.Null(Eval([a], Carbs(3, 20), T0).Fire);
        Assert.Null(Eval([a], Carbs(20, 10), T0).Fire);
        Assert.Null(Eval([a], Carbs(20, 61), T0).Fire);
    }

    [Fact]
    public void MissedBolus_OncePerCarbEntry()
    {
        var a = A(AlarmType.MissedBolus);
        var e1 = Eval([a], Carbs(20, 20), T0);
        var s = e1.State.Handled();
        Assert.Null(Eval([a], Carbs(20, 20), T0.AddMinutes(1), s).Fire);
    }

    [Fact]
    public void MissedBolus_AboveBgActivationLimit() =>
        Assert.Null(Eval([A(AlarmType.MissedBolus, x => x.AboveBG = 150)], Carbs(20, 20), T0).Fire);

    [Fact]
    public void FutureCarbs_FiresWhenATrackedEntryIsDue()
    {
        var a = A(AlarmType.FutureCarbs);
        var carbAt = T0.AddMinutes(30);
        var e1 = Eval([a], Data(T0, with: d => d with { RecentCarbs = [new(30, carbAt)] }), T0);
        Assert.Null(e1.Fire);
        Assert.Single(e1.State.PendingFutureCarbs);
        var t1 = T0.AddMinutes(31);
        var e2 = Eval([a], Data(t1, with: d => d with { RecentCarbs = [new(30, carbAt)] }), t1, e1.State.Checked(t1));
        Assert.True(Fires(e2, a));
        Assert.Empty(e2.State.PendingFutureCarbs);
    }

    [Fact]
    public void FutureCarbs_SilentBeyondLookaheadOrWhenDeleted()
    {
        var a = A(AlarmType.FutureCarbs);
        var far = T0.AddMinutes(60);
        var f1 = Eval([a], Data(T0, with: d => d with { RecentCarbs = [new(30, far)] }), T0);
        var t2 = T0.AddMinutes(61);
        var f2 = Eval([a], Data(t2, with: d => d with { RecentCarbs = [new(30, far)] }), t2, f1.State.Checked(t2));
        Assert.Null(f2.Fire);
        Assert.Empty(f2.State.PendingFutureCarbs);

        var e1 = Eval([a], Data(T0, with: d => d with { RecentCarbs = [new(30, T0.AddMinutes(30))] }), T0);
        var t1 = T0.AddMinutes(31);
        var g2 = Eval([a], Data(t1), t1, e1.State.Checked(t1));
        Assert.Null(g2.Fire);
        Assert.Empty(g2.State.PendingFutureCarbs);
    }

    [Fact]
    public void FutureCarbs_SmallEntriesNotTracked() =>
        Assert.Empty(Eval([A(AlarmType.FutureCarbs)], Data(T0, with: d => d with { RecentCarbs = [new(3, T0.AddMinutes(30))] }), T0)
            .State.PendingFutureCarbs);

    [Fact]
    public void SensorChange_FiresNearSensorEnd()
    {
        AssertPair(AlarmType.SensorChange, A(AlarmType.SensorChange),
            Data(T0, with: d => d with { SageInsertTime = T0.AddDays(-10).AddHours(6) }),
            Data(T0, with: d => d with { SageInsertTime = T0.AddDays(-2) }));
        var longer = A(AlarmType.SensorChange, a => a.SensorLifetimeDays = 15);
        Assert.Null(Eval([longer], Data(T0, with: d => d with { SageInsertTime = T0.AddDays(-10).AddHours(6) }), T0).Fire);
    }

    [Fact]
    public void PumpChange_FiresNearSiteEnd() =>
        AssertPair(AlarmType.PumpChange, A(AlarmType.PumpChange),
            Data(T0, with: d => d with { PumpInsertTime = T0.AddDays(-3).AddHours(6) }),
            Data(T0, with: d => d with { PumpInsertTime = T0.AddDays(-1) }));

    [Fact]
    public void Pump_FiresOnLowReservoir() =>
        AssertPair(AlarmType.Pump, A(AlarmType.Pump), Data(T0, with: d => d with { LatestPumpVolume = 15 }),
            Data(T0, with: d => d with { LatestPumpVolume = 80 }));

    [Fact]
    public void PumpBattery_FiresWhenLow() =>
        AssertPair(AlarmType.PumpBattery, A(AlarmType.PumpBattery), Data(T0, with: d => d with { LatestPumpBattery = 10 }),
            Data(T0, with: d => d with { LatestPumpBattery = 60 }));

    [Fact]
    public void Battery_FiresWhenLow() =>
        AssertPair(AlarmType.Battery, A(AlarmType.Battery), Data(T0, with: d => d with { LatestBattery = 15 }),
            Data(T0, with: d => d with { LatestBattery = 60 }));

    [Fact]
    public void Battery_SuppressIfCharging()
    {
        var a = A(AlarmType.Battery, x => x.SuppressIfCharging = true);
        Assert.Null(Eval([a], Data(T0, with: d => d with { LatestBattery = 15, LatestBatteryIsCharging = true }), T0).Fire);
        Assert.True(Fires(Eval([a], Data(T0, with: d => d with { LatestBattery = 15, LatestBatteryIsCharging = false }), T0), a));
    }

    private static List<BatterySample> History(params (int MinAgo, double Level)[] s) =>
        s.Select(x => new BatterySample(x.Level, T0.AddMinutes(-x.MinAgo))).ToList();

    [Fact]
    public void BatteryDrop_FiresOnAFastDrop() =>
        AssertPair(AlarmType.BatteryDrop, A(AlarmType.BatteryDrop),
            Data(T0, with: d => d with { BatteryHistory = History((20, 85), (15, 80), (5, 74), (0, 69)) }),
            Data(T0, with: d => d with { BatteryHistory = History((20, 85), (15, 80), (5, 76), (0, 75)) }));

    [Fact]
    public void BatteryDrop_IgnoresADropFromFull() =>
        Assert.Null(Eval([A(AlarmType.BatteryDrop)], Data(T0, with: d => d with { BatteryHistory = History((15, 100), (0, 80)) }), T0).Fire);

    [Fact]
    public void RecBolus_FiresAboveThreshold() =>
        AssertPair(AlarmType.RecBolus, A(AlarmType.RecBolus), Data(T0, with: d => d with { RecBolus = 1.5 }),
            Data(T0, with: d => d with { RecBolus = 0.5 }));

    [Fact]
    public void RecBolus_RefiresOnlyOnARiseOverFivePercent()
    {
        var a = A(AlarmType.RecBolus);
        var e1 = Eval([a], Data(T0, with: d => d with { RecBolus = 1.5 }), T0);
        var s1 = e1.State.Handled();
        var e2 = Eval([a], Data(T0, with: d => d with { RecBolus = 1.55 }), T0.AddMinutes(1), s1.Checked(T0.AddMinutes(1)));
        Assert.Null(e2.Fire);
        Assert.Equal(1.55, e2.State.LastRecBolusNotified);
        var e3 = Eval([a], Data(T0, with: d => d with { RecBolus = 1.7 }), T0.AddMinutes(2), e2.State.Checked(T0.AddMinutes(2)));
        Assert.True(Fires(e3, a));
    }

    [Fact]
    public void OverrideStart_FiresForARecentStartOnce()
    {
        AssertPair(AlarmType.OverrideStart, A(AlarmType.OverrideStart),
            Data(T0, with: d => d with { LatestOverrideStart = T0.AddMinutes(-5) }),
            Data(T0, with: d => d with { LatestOverrideStart = T0.AddMinutes(-20) }));
        var a = A(AlarmType.OverrideStart);
        var d = Data(T0, with: x => x with { LatestOverrideStart = T0.AddMinutes(-5) });
        var e1 = Eval([a], d, T0);
        var e2 = Eval([a], d, T0.AddMinutes(1), e1.State.Handled().Checked(T0.AddMinutes(1)));
        Assert.Null(e2.Fire);
    }

    [Fact]
    public void TempTargetStart_FiresForARecentStart() =>
        AssertPair(AlarmType.TempTargetStart, A(AlarmType.TempTargetStart),
            Data(T0, with: d => d with { LatestTempTargetStart = T0.AddMinutes(-5) }),
            Data(T0, with: d => d with { LatestTempTargetStart = T0.AddMinutes(-20) }));

    private static AlarmData Ended(AlarmType type, DateTime now, DateTime? start, DateTime? end, DateTime? activeEnd) =>
        type == AlarmType.OverrideEnd
            ? Data(now, with: d => d with { LatestOverrideStart = start, LatestOverrideEnd = end, ActiveOverrideEnd = activeEnd })
            : Data(now, with: d => d with { LatestTempTargetStart = start, LatestTempTargetEnd = end, ActiveTempTargetEnd = activeEnd });

    [Theory]
    [InlineData(AlarmType.OverrideEnd)]
    [InlineData(AlarmType.TempTargetEnd)]
    public void End_FiresForARecentEnd(AlarmType type)
    {
        AssertPair(type, A(type), Ended(type, T0, T0.AddHours(-1), T0.AddMinutes(-5), null),
            Ended(type, T0, T0.AddHours(-1), T0.AddMinutes(-20), null));
    }

    [Theory]
    [InlineData(AlarmType.OverrideEnd, "Override Ending Soon")]
    [InlineData(AlarmType.TempTargetEnd, "Temp Target Ending Soon")]
    public void End_EarlyWarningThenEnd(AlarmType type, string soonTitle)
    {
        var a = A(type, x => x.PredictiveMinutes = 15);
        var e1 = Eval([a], Ended(type, T0, T0.AddHours(-1), null, T0.AddMinutes(10)), T0);
        Assert.True(Fires(e1, a));
        Assert.Equal(soonTitle, e1.Fire!.Title);

        var t1 = T0.AddMinutes(1);
        var e2 = Eval([a], Ended(type, t1, T0.AddHours(-1), null, T0.AddMinutes(10)), t1, e1.State.Checked(t1));
        Assert.Null(e2.Fire);
        Assert.Equal(a.Id, e2.StoppedAlarmId);

        var t2 = T0.AddMinutes(11);
        var e3 = Eval([a], Ended(type, t2, T0.AddHours(-1), T0.AddMinutes(10), null), t2, e2.State.Checked(t2));
        Assert.True(Fires(e3, a));
        Assert.Equal(type.RawValue(), e3.Fire!.Title);
        Assert.False(e3.Fire.IsPhaseChange);
    }

    [Theory]
    [InlineData(AlarmType.OverrideEnd)]
    [InlineData(AlarmType.TempTargetEnd)]
    public void End_NoEarlyWarningForAnEventShorterThanTheLeadTime(AlarmType type)
    {
        var a = A(type, x => x.PredictiveMinutes = 15);
        Assert.Null(Eval([a], Ended(type, T0, T0.AddMinutes(-2), null, T0.AddMinutes(10)), T0).Fire);
    }

    [Theory]
    [InlineData(AlarmType.OverrideEnd)]
    [InlineData(AlarmType.TempTargetEnd)]
    public void End_PhaseChangeReannouncesTheSoundingAlarm(AlarmType type)
    {
        var a = A(type, x => x.PredictiveMinutes = 15);
        var t2 = T0.AddMinutes(11);
        var p1 = Eval([a], Ended(type, T0, T0.AddHours(-1), null, T0.AddMinutes(10)), T0);
        var p2 = Eval([a], Ended(type, t2, T0.AddHours(-1), T0.AddMinutes(10), null), t2, p1.State.Checked(t2));
        Assert.True(Fires(p2, a));
        Assert.True(p2.Fire!.IsPhaseChange);
        Assert.Equal(type.RawValue(), p2.Fire.Title);
    }

    [Fact]
    public void BuildExpire_FiresNearExpiry() =>
        AssertPair(AlarmType.BuildExpire, A(AlarmType.BuildExpire),
            Data(T0, with: d => d with { ExpireDate = T0.AddDays(3) }), Data(T0, with: d => d with { ExpireDate = T0.AddDays(10) }));

    [Fact]
    public void DbSize_FiresAboveThresholdByDayOnly()
    {
        AssertPair(AlarmType.DbSize, A(AlarmType.DbSize),
            Data(T0, with: d => d with { DbSizePercentage = 80 }), Data(T0, with: d => d with { DbSizePercentage = 50 }));
        var night = T0.Date.AddHours(23);
        Assert.Null(Eval([A(AlarmType.DbSize)], Data(night, with: d => d with { DbSizePercentage = 80 }), night).Fire);
    }
}
