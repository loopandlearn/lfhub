namespace LFHub.Engine.Alarms;

/// A wall-clock time in the phone's time zone.
public sealed record TimeOfDay(int Hour, int Minute)
{
    public int MinutesSinceMidnight => Hour * 60 + Minute;
}

/// <summary>The global alarm settings (Alarm/AlarmConfiguration.swift).</summary>
public sealed class AlarmConfiguration
{
    /// "Snooze All": no alarm is evaluated before this time.
    public DateTime? SnoozeUntil { get; set; }
    /// "Mute All": alarms are evaluated and notified, but play no sound before this time.
    public DateTime? MuteUntil { get; set; }
    public TimeOfDay DayStart { get; set; } = new(6, 0);
    public TimeOfDay NightStart { get; set; } = new(22, 0);
    public bool OverrideSystemOutputVolume { get; set; } = true;
    /// 0 … 1.
    public float ForcedOutputVolume { get; set; } = 0.5f;
    public bool AudioDuringCalls { get; set; } = true;
    public bool IgnoreZeroBG { get; set; } = true;
    public bool AutoSnoozeCGMStart { get; set; }
    public bool EnableVolumeButtonSnooze { get; set; }

    public AlarmConfiguration Clone() => (AlarmConfiguration)MemberwiseClone();
}
