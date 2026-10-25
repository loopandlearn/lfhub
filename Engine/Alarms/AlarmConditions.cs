namespace LFHub.Engine.Alarms;

/// <summary>
/// Per-type alarm logic (Alarm/AlarmCondition/). Evaluate may update the
/// markers in <see cref="AlarmState"/>, exactly where LoopFollow writes Storage.
/// </summary>
internal interface IAlarmCondition
{
    AlarmType Type { get; }

    /// True when belowBG/aboveBG are this condition's own trigger threshold,
    /// checked in Evaluate; false treats them as activation limits.
    bool ChecksOwnBGLimits => false;

    /// Returns whether the alarm fires. <paramref name="title"/> replaces the
    /// type's name in the notification when set.
    bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title);
}

internal static class AlarmConditions
{
    /// The conditions AlarmManager registers (AlarmManager.swift:14-39). All 24 are registered.
    public static readonly IReadOnlyDictionary<AlarmType, IAlarmCondition> Registered =
        new IAlarmCondition[]
        {
            new BuildExpireCondition(),
            new LowBGCondition(),
            new HighBGCondition(),
            new FastDropCondition(),
            new NotLoopingCondition(),
            new OverrideStartCondition(),
            new OverrideEndCondition(),
            new TempTargetStartCondition(),
            new TempTargetEndCondition(),
            new RecBolusCondition(),
            new COBCondition(),
            new MissedReadingCondition(),
            new FastRiseCondition(),
            new TemporaryCondition(),
            new SensorAgeCondition(),
            new PumpChangeCondition(),
            new PumpVolumeCondition(),
            new PumpBatteryCondition(),
            new IOBCondition(),
            new BatteryCondition(),
            new BatteryDropCondition(),
            new FutureCarbsCondition(),
            new MissedBolusCondition(),
            new DBSizeCondition(),
        }.ToDictionary(c => c.Type);

    /// The Swift side stores these as seconds since 1970 and treats 0 as unset.
    public static bool IsSet(DateTime? t) => t is { } v && v > DateTime.UnixEpoch;
}

internal sealed class BuildExpireCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.BuildExpire;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (data.ExpireDate is not { } expiry || alarm.Threshold is not { } days) return false;
        return now >= expiry.AddSeconds(-days * 86400);
    }
}

/// Fires on a low latest BG (all readings in persistentMinutes low, when set),
/// or on any predicted BG within predictiveMinutes at or below belowBG.
internal sealed class LowBGCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Low;
    public bool ChecksOwnBGLimits => true;

    /// The first forecast point is the current value; ceil(minutes / 5) more reach the horizon.
    public static int ForecastPoints(int minutes) => (int)Math.Ceiling(minutes / 5.0) + 1;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.BelowBG is not { } below) return false;
        if (data.BgReadings.Count == 0 || data.BgReadings[^1].Sgv <= 0) return false;
        var latest = data.BgReadings[^1];
        bool IsLow(GlucoseValue g) => g.Sgv > 0 && g.Sgv <= below;

        var predictive = false;
        if (alarm.PredictiveMinutes is > 0 and var minutes && data.PredictionData.Count > 0)
            predictive = data.PredictionData.Take(ForecastPoints(minutes)).Any(IsLow);

        var persistentOk = true;
        if (alarm.PersistentMinutes is > 0 and var pm)
        {
            var window = (int)Math.Ceiling(pm / 5.0);
            persistentOk = data.BgReadings.Count >= window && data.BgReadings.TakeLast(window).All(IsLow);
        }

        if (alarm.SuppressIfRising && data.BgReadings.Count >= 2)
        {
            var last = data.BgReadings[^1];
            var previous = data.BgReadings[^2];
            if (last.Sgv > 0 && previous.Sgv > 0 && last.Sgv > previous.Sgv) return false;
        }

        return (IsLow(latest) && persistentOk) || predictive;
    }
}

/// The latest BG is already at or above aboveBG (passesBGLimits); this adds
/// persistence and the falling-BG suppression.
internal sealed class HighBGCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.High;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.AboveBG is not { } high) return false;
        bool IsHigh(GlucoseValue g) => g.Sgv > 0 && g.Sgv >= high;

        var persistentOk = true;
        if (alarm.PersistentMinutes is > 0 and var pm)
        {
            var window = (int)Math.Ceiling(pm / 5.0);
            persistentOk = data.BgReadings.Count >= window && data.BgReadings.TakeLast(window).All(IsHigh);
        }

        if (alarm.SuppressIfFalling && data.BgReadings.Count >= 2)
        {
            var last = data.BgReadings[^1];
            var previous = data.BgReadings[^2];
            if (last.Sgv > 0 && previous.Sgv > 0 && last.Sgv < previous.Sgv) return false;
        }

        return persistentOk;
    }
}

/// Fires when each of the last monitoringWindow deltas is a drop of at least delta mg/dL.
internal sealed class FastDropCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.FastDrop;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Delta is not (> 0 and var drop) || alarm.MonitoringWindow is not (> 0 and var n)
            || data.BgReadings.Count < n + 1) return false;
        var r = data.BgReadings.TakeLast(n + 1).ToList();
        for (var i = 1; i <= n; i++)
            if (r[i - 1].Sgv - r[i].Sgv < drop) return false;
        return true;
    }
}

/// Fires when each of the last monitoringWindow deltas is a rise of at least delta mg/dL.
internal sealed class FastRiseCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.FastRise;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Delta is not (> 0 and var rise) || alarm.MonitoringWindow is not (> 0 and var n)
            || data.BgReadings.Count < n + 1) return false;
        var r = data.BgReadings.TakeLast(n + 1).ToList();
        for (var i = 1; i <= n; i++)
            if (r[i].Sgv - r[i - 1].Sgv < rise) return false;
        return true;
    }
}

internal sealed class NotLoopingCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.NotLooping;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var minutes)) return false;
        if (!AlarmConditions.IsSet(data.LastLoopTime)) return false;
        // A stale check means the data is unreliable.
        if (state.LastLoopingChecked is not { } checkedAt || (now - checkedAt).TotalSeconds > 360) return false;
        return (now - data.LastLoopTime!.Value).TotalSeconds >= minutes * 60;
    }
}

/// Fires when the newest reading is threshold minutes old or more.
internal sealed class MissedReadingCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.MissedReading;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var minutes)) return false;
        if (data.BgReadings.Count == 0) return false;
        if (state.LastBGChecked is not { } checkedAt || (now - checkedAt).TotalSeconds > 360) return false;
        return (now - data.BgReadings[^1].Date).TotalSeconds >= minutes * 60;
    }
}

/// Fires once per override, within 15 minutes of its start.
internal sealed class OverrideStartCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.OverrideStart;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (!AlarmConditions.IsSet(data.LatestOverrideStart)) return false;
        var start = data.LatestOverrideStart!.Value;
        if ((now - start).TotalSeconds > 15 * 60) return false;
        if (!(start > (state.LastOverrideStartNotified ?? DateTime.UnixEpoch))) return false;
        state.LastOverrideStartNotified = start;
        return true;
    }
}

/// Fires once per temp target, within 15 minutes of its start.
internal sealed class TempTargetStartCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.TempTargetStart;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (!AlarmConditions.IsSet(data.LatestTempTargetStart)) return false;
        var start = data.LatestTempTargetStart!.Value;
        if ((now - start).TotalSeconds > 15 * 60) return false;
        if (!(start > (state.LastTempTargetStartNotified ?? DateTime.UnixEpoch))) return false;
        state.LastTempTargetStartNotified = start;
        return true;
    }
}

/// <summary>
/// Two-phase evaluation shared by the override and temp target end alarms
/// (EndAlarmPhases.swift): "ended" once per end within 15 minutes, and
/// "ending soon" once per scheduled end, predictiveMinutes before it. Ended
/// wins when both are due in the same tick.
/// </summary>
internal static class EndAlarmPhases
{
    public enum Phase { Ended, EndingSoon }

    private const double EndedGraceSeconds = 15 * 60;

    public static Phase? Fire(DateTime now, DateTime? latestStart, DateTime? latestEnd, DateTime? activeEnd,
        int? leadMinutes, ref DateTime? endedMarker, ref DateTime? warnedMarker)
    {
        if (AlarmConditions.IsSet(latestEnd) && (now - latestEnd!.Value).TotalSeconds <= EndedGraceSeconds
            && latestEnd.Value > (endedMarker ?? DateTime.UnixEpoch))
        {
            endedMarker = latestEnd;
            return Phase.Ended;
        }

        if (leadMinutes is not (> 0 and var lead) || activeEnd is not { } end) return null;
        var opensAt = end.AddSeconds(-lead * 60.0);
        // An event shorter than the lead time is covered by its start alarm.
        if (latestStart is { } start && opensAt <= start) return null;
        if (now < opensAt || warnedMarker == end) return null;
        warnedMarker = end;
        return Phase.EndingSoon;
    }
}

internal sealed class OverrideEndCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.OverrideEnd;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        DateTime? ended = state.LastOverrideEndNotified, warned = state.LastOverridePreEndNotified;
        var phase = EndAlarmPhases.Fire(now, data.LatestOverrideStart, data.LatestOverrideEnd,
            data.ActiveOverrideEnd, alarm.PredictiveMinutes, ref ended, ref warned);
        state.LastOverrideEndNotified = ended;
        state.LastOverridePreEndNotified = warned;
        title = phase == EndAlarmPhases.Phase.EndingSoon ? "Override Ending Soon" : null;
        return phase != null;
    }
}

internal sealed class TempTargetEndCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.TempTargetEnd;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        DateTime? ended = state.LastTempTargetEndNotified, warned = state.LastTempTargetPreEndNotified;
        var phase = EndAlarmPhases.Fire(now, data.LatestTempTargetStart, data.LatestTempTargetEnd,
            data.ActiveTempTargetEnd, alarm.PredictiveMinutes, ref ended, ref warned);
        state.LastTempTargetEndNotified = ended;
        state.LastTempTargetPreEndNotified = warned;
        title = phase == EndAlarmPhases.Phase.EndingSoon ? "Temp Target Ending Soon" : null;
        return phase != null;
    }
}

/// Fires when the recommended bolus reaches threshold, again only on a rise of more
/// than 5 % over the last value seen, and re-arms when it drops below threshold.
internal sealed class RecBolusCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.RecBolus;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var threshold)) return false;
        if (data.RecBolus is not { } rec || rec < threshold)
        {
            state.LastRecBolusNotified = null;
            return false;
        }
        var alert = state.LastRecBolusNotified is not { } last || rec > last * 1.05;
        state.LastRecBolusNotified = rec;
        return alert;
    }
}

/// Fires when COB reaches threshold, again only when it rises further, and
/// re-arms when it drops below threshold.
internal sealed class COBCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Cob;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var threshold)) return false;
        if (data.Cob is not { } cob || cob < threshold)
        {
            state.LastCOBNotified = null;
            return false;
        }
        if (state.LastCOBNotified is { } last && !(cob > last)) return false;
        state.LastCOBNotified = cob;
        return true;
    }
}

/// A one-shot BG-limit alarm; the limits are checked by passesBGLimits.
internal sealed class TemporaryCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Temporary;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        return alarm.BelowBG != null || alarm.AboveBG != null;
    }
}

/// Fires from threshold hours before the end of the sensor lifetime (default 10 days).
internal sealed class SensorAgeCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.SensorChange;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var hours) || !AlarmConditions.IsSet(data.SageInsertTime)) return false;
        var lifetimeDays = alarm.SensorLifetimeDays ?? 10;
        var trigger = data.SageInsertTime!.Value.AddSeconds(lifetimeDays * 86400.0 - hours * 3600);
        return now >= trigger;
    }
}

/// Fires from threshold hours before 3 days after the pump site change.
internal sealed class PumpChangeCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.PumpChange;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var hours) || !AlarmConditions.IsSet(data.PumpInsertTime)) return false;
        var trigger = data.PumpInsertTime!.Value.AddSeconds(3 * 86400.0 - hours * 3600);
        return now >= trigger;
    }
}

internal sealed class PumpVolumeCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Pump;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        return alarm.Threshold is > 0 and var threshold && data.LatestPumpVolume is { } vol && vol <= threshold;
    }
}

internal sealed class PumpBatteryCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.PumpBattery;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        return alarm.Threshold is > 0 and var limit && data.LatestPumpBattery is { } level && level <= limit;
    }
}

/// <summary>
/// Fires when IOB reaches threshold, or when within the last predictiveMinutes
/// the boluses of at least delta units number monitoringWindow or more, or sum
/// to threshold or more.
/// </summary>
internal sealed class IOBCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Iob;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var iobMax) || alarm.Delta is not (> 0 and var minBolus)
            || alarm.MonitoringWindow is not (> 0 and var countNeeded)
            || alarm.PredictiveMinutes is not (> 0 and var lookback)) return false;

        if (data.Iob is { } iob && iob >= iobMax) return true;

        var cutoff = now.AddSeconds(-lookback * 60.0);
        var count = 0;
        var total = 0.0;
        foreach (var b in data.RecentBoluses)
        {
            if (b.Date < cutoff || b.Units < minBolus) continue;
            count++;
            total += b.Units;
            if (count >= countNeeded || total >= iobMax) return true;
        }
        return false;
    }
}

internal sealed class BatteryCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.Battery;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Threshold is not (> 0 and var limit) || data.LatestBattery is not { } level) return false;
        if (alarm.SuppressIfCharging && data.LatestBatteryIsCharging == true) return false;
        return level <= limit;
    }
}

/// Fires when the phone battery fell by at least delta % since the sample
/// closest to monitoringWindow minutes before the latest one.
internal sealed class BatteryDropCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.BatteryDrop;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.Delta is not (> 0 and var drop) || alarm.MonitoringWindow is not (> 0 and var minutes)
            || data.BatteryHistory.Count == 0) return false;

        var latest = data.BatteryHistory[^1];
        var target = latest.Timestamp.AddSeconds(-minutes * 60.0);
        var earlier = data.BatteryHistory[0];
        foreach (var s in data.BatteryHistory)
            if (Math.Abs((s.Timestamp - target).Ticks) < Math.Abs((earlier.Timestamp - target).Ticks)) earlier = s;

        // A drop from 100 % is usually the phone just being unplugged.
        if (earlier.Level >= 100) return false;
        return earlier.Level - latest.Level >= drop;
    }
}

/// <summary>
/// Fires once when a future-dated carb entry's time arrives (FutureCarbsCondition.swift).
/// Every future entry of at least delta grams is tracked when first seen; it fires
/// when due if it still exists and was first seen at most threshold minutes ahead.
/// </summary>
internal sealed class FutureCarbsCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.FutureCarbs;

    private const double ToleranceSeconds = 5;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        var maxLookaheadSec = (alarm.Threshold ?? 45) * 60;
        var minGrams = alarm.Delta ?? 5;
        var pending = state.PendingFutureCarbs.ToList();

        bool Same(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) < ToleranceSeconds;

        foreach (var carb in data.RecentCarbs)
        {
            if (!(carb.Date > now) || carb.Grams < minGrams) continue;
            if (!pending.Any(p => Same(p.CarbDate, carb.Date) && p.Grams == carb.Grams))
                pending.Add(new PendingFutureCarb(carb.Date, carb.Grams, now));
        }

        var fired = false;
        pending.RemoveAll(entry =>
        {
            var stillExists = data.RecentCarbs.Any(c => Same(c.Date, entry.CarbDate) && c.Grams == entry.Grams);
            if ((now - entry.ObservedAt).TotalSeconds > 7200 && !stillExists) return true;
            if (entry.CarbDate > now) return false;
            if (!stillExists) return true;
            if ((entry.CarbDate - entry.ObservedAt).TotalSeconds > maxLookaheadSec) return true;
            if (!fired)
            {
                fired = true;
                return true;
            }
            return false;
        });

        state.PendingFutureCarbs = pending;
        return fired;
    }
}

/// <summary>
/// Fires once per carb entry when the newest past entry above threshold grams,
/// between monitoringWindow and 60 minutes old, has no bolus above delta units
/// from predictiveMinutes before it onwards.
/// </summary>
internal sealed class MissedBolusCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.MissedBolus;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        if (alarm.MonitoringWindow is not (> 0 and var delayMin) || alarm.PredictiveMinutes is not { } prebolusMin
            || alarm.Delta is not { } minBolusU || alarm.Threshold is not { } minCarbGr) return false;

        var carb = data.RecentCarbs.LastOrDefault(c => c.Date <= now);
        if (carb is null) return false;
        if (!(carb.Date > now.AddSeconds(-3600) && carb.Date < now.AddSeconds(-delayMin * 60.0))) return false;
        if (!(carb.Grams > minCarbGr)) return false;
        if (state.LastMissedBolusNotified is { } lastFired && carb.Date <= lastFired) return false;

        var windowStart = carb.Date.AddSeconds(-prebolusMin * 60.0);
        if (data.RecentBoluses.Any(b => b.Date >= windowStart && b.Units > minBolusU)) return false;

        state.LastMissedBolusNotified = carb.Date;
        return true;
    }
}

/// Fires when the Nightscout database is at least threshold percent full.
internal sealed class DBSizeCondition : IAlarmCondition
{
    public AlarmType Type => AlarmType.DbSize;

    public bool Evaluate(Alarm alarm, AlarmData data, DateTime now, AlarmState state, out string? title)
    {
        title = null;
        return alarm.Threshold is > 0 and var limit && data.DbSizePercentage is { } used && used >= limit;
    }
}
