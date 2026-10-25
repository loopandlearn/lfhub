using System.Globalization;

namespace LFHub.Engine.Alarms;

/// <summary>
/// The notification and sound for one alarm firing, as Alarm.trigger() and
/// AlarmManager.sendNotification decide them.
/// </summary>
/// <param name="Title">The type's name, or the condition's phase title (e.g. "Override Ending Soon").</param>
/// <param name="Subtitle">"&lt;bg&gt; &lt;arrow&gt; &lt;delta&gt;", as LoopFollow's notification subtitle.</param>
/// <param name="Body">"CGM reported: …" for missed reading and not looping, when a sensor state is known.</param>
/// <param name="ActionTitle">"Acknowledge" when snoozeDuration is 0, otherwise "Snooze".</param>
/// <param name="PlaySound">False under Mute All or outside the alarm's playSoundOption window.</param>
/// <param name="SoundDelaySeconds">Seconds between repeats; 0 when the sound does not repeat.</param>
/// <param name="ForcedOutputVolume">The volume LoopFollow forces while playing, when overrideSystemOutputVolume is on.</param>
/// <param name="IsPhaseChange">The sounding alarm announced again with a new title; it was not newly raised.</param>
public sealed record AlarmFiring(
    Guid AlarmId,
    AlarmType Type,
    string AlarmName,
    string Title,
    string Subtitle,
    string? Body,
    string ActionTitle,
    SoundFile SoundFile,
    bool PlaySound,
    bool RepeatSound,
    int SoundDelaySeconds,
    float? ForcedOutputVolume,
    bool IsPhaseChange);

/// <summary>Result of one alarm-check tick.</summary>
/// <param name="Fire">The alarm to notify, at most one per tick.</param>
/// <param name="State">The state to persist and pass to the next tick.</param>
/// <param name="DisableAlarms">
/// Alarms to persist with isEnabled = false and snoozedUntil = null: a
/// temporary alarm turns itself off when it fires.
/// </param>
/// <param name="StoppedAlarmId">
/// The sounding alarm that was stopped this tick, because Snooze All began or
/// its condition does not hold. LoopFollow silences it and clears pending notifications.
/// </param>
public sealed record AlarmEvaluation(
    AlarmFiring? Fire,
    AlarmState State,
    IReadOnlyList<Guid> DisableAlarms,
    Guid? StoppedAlarmId);

/// <summary>
/// Facts LoopFollow puts in the notification text that are not part of
/// <see cref="AlarmData"/>.
/// </summary>
/// <param name="Direction">The latest reading's Nightscout direction, e.g. "FortyFiveUp".</param>
/// <param name="CgmSensorStateSummary">CGMSensorState.summary of the active sensor state, e.g. "Sensor warmup at 22:33".</param>
public sealed record AlarmTextContext(string? Direction = null, string? CgmSensorStateSummary = null);

public static class AlarmEvaluator
{
    private const double MgdlToMmol = 1.0 / 18.01559;
    private const int MinDisplayGlucose = 39;
    private const int MaxDisplayGlucose = 400;

    /// <summary>
    /// One alarm-check tick, as AlarmManager.checkAlarms does it. Pure: the
    /// inputs are not modified. <paramref name="timeZone"/> is the phone's and
    /// decides day and night; <paramref name="mmol"/> only affects the text.
    /// </summary>
    public static AlarmEvaluation Evaluate(IReadOnlyList<Alarm> alarms, AlarmConfiguration config,
        AlarmData data, AlarmState state, DateTime nowUtc, TimeZoneInfo timeZone, bool mmol,
        AlarmTextContext? text = null)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var s = state.Clone();
        Guid? stopped = null;
        var disable = new List<Guid>();

        void Stop()
        {
            stopped = s.CurrentAlarmId;
            s.CurrentAlarmId = null;
            s.CurrentAlarmTitleOverride = null;
        }

        if (config.SnoozeUntil is { } snoozeAll && snoozeAll > now)
        {
            if (s.CurrentAlarmId != null) Stop();
            return new AlarmEvaluation(null, s, disable, stopped);
        }

        var latest = data.BgReadings.Count > 0 ? data.BgReadings[^1] : null;
        var latestIsRecent = latest != null && (now - latest.Date).TotalSeconds <= 5 * 60;
        AlarmType? skipType = null;
        AlarmFiring? fire = null;

        foreach (var alarm in SortByPriorityThenSpec(alarms))
        {
            var type = alarm.Type;
            if (type == skipType) continue;

            // BG-based alarms need a recent reading, except missed reading, which fires on its absence.
            if (type.IsBgBased() && type != AlarmType.MissedReading && !latestIsRecent) continue;

            // A BG-based alarm fires at most once per reading.
            if (type.IsBgBased() && s.LastBGAlarmTime is { } handled && latest != null && !(latest.Date > handled))
                continue;

            // A snoozed alarm also silences the lower alarms of its type.
            if (alarm.SnoozedUntil is { } until && until > now)
            {
                skipType = type;
                continue;
            }

            string? title = null;
            if (!AlarmConditions.Registered.TryGetValue(type, out var condition)
                || !ShouldFire(condition, alarm, data, now, config, timeZone, s, out title))
            {
                if (s.CurrentAlarmId == alarm.Id) Stop();
                continue;
            }

            if (s.CurrentAlarmId == alarm.Id)
            {
                if (title != s.CurrentAlarmTitleOverride)
                {
                    s.CurrentAlarmTitleOverride = title;
                    fire = Trigger(alarm, config, data, now, timeZone, mmol, text, title, isPhaseChange: true);
                }
                break;
            }

            s.CurrentAlarmTitleOverride = title;
            s.CurrentAlarmId = alarm.Id;
            fire = Trigger(alarm, config, data, now, timeZone, mmol, text, title, isPhaseChange: false);

            if (type.IsBgBased() && latest != null) s.LastBGAlarmTime = latest.Date;
            if (type == AlarmType.Temporary) disable.Add(alarm.Id);
            break;
        }

        return new AlarmEvaluation(fire, s, disable, stopped);
    }

    /// <summary>
    /// Alarms in the order checkAlarms visits them: type priority, then the
    /// type's main value (Alarm+byPriorityThenSpec.swift), keeping input order on ties.
    /// </summary>
    public static IReadOnlyList<Alarm> SortByPriorityThenSpec(IReadOnlyList<Alarm> alarms)
    {
        // Stable insertion sort on the Swift comparator, which is not a total order for missing keys.
        var list = alarms.ToList();
        for (var i = 1; i < list.Count; i++)
        {
            var x = list[i];
            var j = i;
            while (j > 0 && Less(x, list[j - 1]))
            {
                list[j] = list[j - 1];
                j--;
            }
            list[j] = x;
        }
        return list;
    }

    private static bool Less(Alarm l, Alarm r)
    {
        if (l.Type != r.Type) return l.Type.Priority() < r.Type.Priority();
        switch (l.Type)
        {
            case AlarmType.Low:
                return (l.BelowBG ?? double.NaN) < (r.BelowBG ?? double.NaN);
            case AlarmType.High:
                return (l.AboveBG ?? double.NegativeInfinity) > (r.AboveBG ?? double.NegativeInfinity);
            case AlarmType.FastDrop or AlarmType.FastRise:
                return (l.Delta is { } ld ? Math.Abs(ld) : double.NegativeInfinity)
                       > (r.Delta is { } rd ? Math.Abs(rd) : double.NegativeInfinity);
            case AlarmType.MissedReading or AlarmType.NotLooping or AlarmType.MissedBolus or AlarmType.BuildExpire
                or AlarmType.SensorChange or AlarmType.DbSize:
                return (l.Threshold ?? double.PositiveInfinity) < (r.Threshold ?? double.PositiveInfinity);
            case AlarmType.Iob or AlarmType.Cob:
                return (l.Threshold ?? double.NegativeInfinity) > (r.Threshold ?? double.NegativeInfinity);
            default:
                return false;
        }
    }

    /// The per-alarm guards of AlarmCondition.shouldFire, then the type's own logic.
    private static bool ShouldFire(IAlarmCondition condition, Alarm alarm, AlarmData data, DateTime now,
        AlarmConfiguration config, TimeZoneInfo timeZone, AlarmState state, out string? title)
    {
        title = null;
        if (!alarm.IsEnabled) return false;
        if (alarm.SnoozedUntil is { } until && until > now) return false;
        if (!PassesBGLimits(condition, alarm, data)) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var nowMin = local.Hour * 60 + local.Minute;
        // This guard does not handle a night that starts before the day (AlarmCondition.swift:77); trigger() does.
        var isNight = nowMin < config.DayStart.MinutesSinceMidnight || nowMin >= config.NightStart.MinutesSinceMidnight;
        if (alarm.ActiveOption == ActiveOption.Day && isNight) return false;
        if (alarm.ActiveOption == ActiveOption.Night && !isNight) return false;

        return condition.Evaluate(alarm, data, now, state, out title);
    }

    /// <summary>
    /// belowBG/aboveBG as activation limits: below one, above the other, or
    /// inside the band when belowBG ≥ aboveBG. BG-based alarms need a reading.
    /// </summary>
    private static bool PassesBGLimits(IAlarmCondition condition, Alarm alarm, AlarmData data)
    {
        var reading = data.BgReadings.Count > 0 ? data.BgReadings[^1].Sgv : 0;
        var haveBG = reading > 0;
        double bg = reading;

        if (alarm.Type.IsBgBased() && !haveBG) return false;
        if (condition.ChecksOwnBGLimits) return true;
        if (alarm.BelowBG == null && alarm.AboveBG == null) return true;
        if (!haveBG) return false;

        return (alarm.BelowBG, alarm.AboveBG) switch
        {
            ({ } lo, { } hi) => lo < hi ? bg <= lo || bg >= hi : hi <= bg && bg <= lo,
            ({ } lo, null) => bg <= lo,
            (null, { } hi) => bg >= hi,
            _ => true,
        };
    }

    private static AlarmFiring Trigger(Alarm alarm, AlarmConfiguration config, AlarmData data, DateTime now,
        TimeZoneInfo timeZone, bool mmol, AlarmTextContext? text, string? titleOverride, bool isPhaseChange)
    {
        var playSound = !(config.MuteUntil is { } mute && mute > now);

        var local = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone).TimeOfDay;
        var dayStart = new TimeSpan(config.DayStart.Hour, config.DayStart.Minute, 0);
        var nightStart = new TimeSpan(config.NightStart.Hour, config.NightStart.Minute, 0);
        var isNight = nightStart >= dayStart
            ? local >= nightStart || local < dayStart
            : local >= nightStart && local < dayStart;
        var isDay = !isNight;

        switch (alarm.PlaySoundOption)
        {
            case PlaySoundOption.Never:
            case PlaySoundOption.Day when !isDay:
            case PlaySoundOption.Night when !isNight:
                playSound = false;
                break;
        }

        var repeat = alarm.RepeatSoundOption switch
        {
            RepeatSoundOption.Always => true,
            RepeatSoundOption.Never => false,
            RepeatSoundOption.Day => isDay,
            _ => isNight,
        };

        string? body = null;
        if (alarm.Type is AlarmType.MissedReading or AlarmType.NotLooping && text?.CgmSensorStateSummary is { } summary)
            body = $"CGM reported: {summary}";

        return new AlarmFiring(
            alarm.Id,
            alarm.Type,
            alarm.Name,
            titleOverride ?? alarm.Type.RawValue(),
            Subtitle(data, mmol, text?.Direction),
            body,
            alarm.SnoozeDuration == 0 ? "Acknowledge" : "Snooze",
            alarm.SoundFile,
            playSound,
            repeat,
            repeat ? alarm.SoundDelay : 0,
            config.OverrideSystemOutputVolume ? config.ForcedOutputVolume : null,
            isPhaseChange);
    }

    /// <summary>
    /// The notification subtitle, "bgText directionText deltaText", from the
    /// last two readings as BGData.viewUpdateNSBG builds those texts.
    /// </summary>
    public static string Subtitle(AlarmData data, bool mmol, string? direction)
    {
        var readings = data.BgReadings;
        if (readings.Count == 0) return "";
        var latest = readings[^1].Sgv;
        var bgText = latest <= MinDisplayGlucose ? "LOW"
            : latest >= MaxDisplayGlucose ? "HIGH"
            : DisplayUnits(latest, mmol);
        var deltaText = "";
        if (readings.Count >= 2)
        {
            var delta = latest - readings[^2].Sgv;
            deltaText = delta < 0 ? DisplayUnits(delta, mmol) : "+" + DisplayUnits(delta, mmol);
        }
        return $"{bgText} {DirectionArrow(direction)} {deltaText}";
    }

    /// Localizer.toDisplayUnits: mg/dL whole, mmol/L to one decimal (NumberFormatter rounds half to even).
    private static string DisplayUnits(int mgdl, bool mmol) => mmol
        ? Math.Round(mgdl * MgdlToMmol, 1, MidpointRounding.ToEven).ToString("0.0", CultureInfo.InvariantCulture)
        : mgdl.ToString(CultureInfo.InvariantCulture);

    /// MainViewController.bgDirectionGraphic.
    private static string DirectionArrow(string? direction) => direction switch
    {
        null => "",
        "Flat" => "→",
        "DoubleUp" => "↑↑",
        "SingleUp" => "↑",
        "FortyFiveUp" => "↗",
        "FortyFiveDown" => "↘︎",
        "SingleDown" => "↓",
        "DoubleDown" => "↓↓",
        _ => "-",
    };
}
