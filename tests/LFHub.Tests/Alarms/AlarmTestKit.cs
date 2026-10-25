using LFHub.Engine;
using LFHub.Engine.Alarms;

namespace LFHub.Tests.Alarms;

/// Builders for alarm-engine inputs. Readings are invented, at 5-minute spacing.
internal static class AlarmTestKit
{
    /// Noon UTC, which is day under the default day/night window.
    public static readonly DateTime T0 = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    public static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    /// Readings oldest first at 5-minute spacing, the newest one minute before `now`.
    public static List<GlucoseValue> Bg(DateTime now, params int[] sgvs) =>
        sgvs.Select((v, i) => new GlucoseValue(v, now.AddMinutes(-1 - 5 * (sgvs.Length - 1 - i)))).ToList();

    public static AlarmData Data(DateTime now, int[]? bg = null, Func<AlarmData, AlarmData>? with = null)
    {
        var d = new AlarmData
        {
            BgReadings = Bg(now, bg ?? [120, 121]),
            PredictionData = [],
            RecentBoluses = [],
            BatteryHistory = [],
            RecentCarbs = [],
        };
        return with?.Invoke(d) ?? d;
    }

    public static AlarmConfiguration Cfg() => new();

    /// State as after a BG and devicestatus fetch a minute ago.
    public static AlarmState Fresh(DateTime now) =>
        new() { LastBGChecked = now.AddMinutes(-1), LastLoopingChecked = now.AddMinutes(-1) };

    public static AlarmEvaluation Eval(IEnumerable<Alarm> alarms, AlarmData data, DateTime now, AlarmState? state = null,
        AlarmConfiguration? cfg = null, TimeZoneInfo? tz = null, bool mmol = false, AlarmTextContext? text = null) =>
        AlarmEvaluator.Evaluate(alarms.ToList(), cfg ?? Cfg(), data, state ?? Fresh(now), now, tz ?? Utc, mmol, text);

    public static Alarm A(AlarmType t, Action<Alarm>? edit = null)
    {
        var a = Alarm.Create(t);
        edit?.Invoke(a);
        return a;
    }

    public static bool Fires(AlarmEvaluation e, Alarm a) => e.Fire?.AlarmId == a.Id;

    /// The state as the hub leaves it after refreshing its fetch timestamps before a check.
    public static AlarmState Checked(this AlarmState s, DateTime now)
    {
        var c = s.Clone();
        c.LastBGChecked = now.AddSeconds(-30);
        c.LastLoopingChecked = now.AddSeconds(-30);
        return c;
    }

    /// The state as the app's `handled` (snooze or acknowledge) leaves it: no current alarm.
    public static AlarmState Handled(this AlarmState s)
    {
        var c = s.Clone();
        c.CurrentAlarmId = null;
        return c;
    }

    /// Asserts that the alarm fires on `yes`, titled with its type, and stays silent on `no`.
    public static void AssertPair(AlarmType type, Alarm alarm, AlarmData yes, AlarmData no, DateTime? now = null)
    {
        var n = now ?? T0;
        var ey = Eval([alarm], yes, n);
        Assert.True(Fires(ey, alarm), $"{type} fires");
        Assert.Equal(type.RawValue(), ey.Fire!.Title);
        var en = Eval([alarm], no, n);
        Assert.Null(en.Fire);
    }
}
