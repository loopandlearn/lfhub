namespace LFHub.Engine.Alarms;

public enum PlaySoundOption { Always, Day, Night, Never }

public enum RepeatSoundOption { Always, Day, Night, Never }

public enum ActiveOption { Always, Day, Night }

/// <summary>
/// An alarm sound: a sound bundled with the app (file name without ".caf") or a
/// sound the user imported, by id.
/// </summary>
public sealed record SoundFile(string? BuiltinName, Guid? CustomId)
{
    public static SoundFile Builtin(string name) => new(name, null);
    public static SoundFile Custom(Guid id) => new(null, id);

    /// LoopFollow's SoundFile.id: "builtin:Indeed" or "custom:&lt;UUID&gt;".
    public string Id => BuiltinName is { } name
        ? $"builtin:{name}"
        : $"custom:{CustomId!.Value.ToString().ToUpperInvariant()}";

    /// The sound LoopFollow plays when a referenced sound cannot be found.
    public static readonly SoundFile Fallback = Builtin("Indeed");
}

/// <summary>
/// One user-defined alarm, as LoopFollow stores and exports it (Alarm/Alarm.swift).
/// Field meaning depends on the type; see each condition.
/// </summary>
public sealed class Alarm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AlarmType Type { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime? SnoozedUntil { get; set; }
    public double? AboveBG { get; set; }
    public double? BelowBG { get; set; }
    public double? Threshold { get; set; }
    public int? PredictiveMinutes { get; set; }
    public double? Delta { get; set; }
    public int? PersistentMinutes { get; set; }
    public bool SuppressIfRising { get; set; }
    public bool SuppressIfFalling { get; set; }
    public bool SuppressIfCharging { get; set; }
    public int? MonitoringWindow { get; set; }
    public SoundFile SoundFile { get; set; } = SoundFile.Fallback;
    public int SnoozeDuration { get; set; } = 5;
    public PlaySoundOption PlaySoundOption { get; set; } = PlaySoundOption.Always;
    public RepeatSoundOption RepeatSoundOption { get; set; } = RepeatSoundOption.Always;
    /// Seconds between repeats of the sound.
    public int SoundDelay { get; set; }
    public ActiveOption ActiveOption { get; set; } = ActiveOption.Always;
    public int? MissedBolusPrebolusWindow { get; set; }
    public double? MissedBolusIgnoreSmallBolusUnits { get; set; }
    public double? MissedBolusIgnoreUnderGrams { get; set; }
    public double? MissedBolusIgnoreUnderBG { get; set; }
    public int? BolusCountThreshold { get; set; }
    public int? BolusWindowMinutes { get; set; }
    public int? SensorLifetimeDays { get; set; }

    public Alarm Clone() => (Alarm)MemberwiseClone();

    /// <summary>A new alarm with the defaults LoopFollow gives each type (Alarm.init(type:)).</summary>
    public static Alarm Create(AlarmType type)
    {
        var a = new Alarm { Type = type, Name = type.RawValue() };
        switch (type)
        {
            case AlarmType.BuildExpire:
                a.Threshold = 7; a.SoundFile = SoundFile.Builtin("Wrong_Answer"); a.SnoozeDuration = 1;
                break;
            case AlarmType.Low:
                a.SoundFile = SoundFile.Builtin("Indeed"); a.BelowBG = 80; a.PersistentMinutes = 0; a.PredictiveMinutes = 0;
                break;
            case AlarmType.Iob:
                a.SoundFile = SoundFile.Builtin("Alert_Tone_Ringtone_1");
                a.Delta = 1; a.MonitoringWindow = 2; a.PredictiveMinutes = 30; a.Threshold = 6;
                break;
            case AlarmType.Cob:
                a.SoundFile = SoundFile.Builtin("Alert_Tone_Ringtone_2"); a.Threshold = 20;
                break;
            case AlarmType.High:
                a.SoundFile = SoundFile.Builtin("Time_Has_Come"); a.AboveBG = 180; a.PersistentMinutes = 0;
                break;
            case AlarmType.FastDrop:
                a.SoundFile = SoundFile.Builtin("Big_Clock_Ticking"); a.Delta = 15; a.MonitoringWindow = 3;
                break;
            case AlarmType.FastRise:
                a.SoundFile = SoundFile.Builtin("Cartoon_Fail_Strings_Trumpet"); a.Delta = 10; a.MonitoringWindow = 3;
                break;
            case AlarmType.MissedReading:
                a.SoundFile = SoundFile.Builtin("Cartoon_Tip_Toe_Sneaky_Walk"); a.Threshold = 16;
                break;
            case AlarmType.NotLooping:
                a.SoundFile = SoundFile.Builtin("Sci-Fi_Engine_Shut_Down"); a.Threshold = 16;
                break;
            case AlarmType.MissedBolus:
                a.SoundFile = SoundFile.Builtin("Dhol_Shuffleloop");
                a.MonitoringWindow = 15; a.PredictiveMinutes = 15; a.Delta = 0.1; a.Threshold = 4;
                break;
            case AlarmType.FutureCarbs:
                a.SoundFile = SoundFile.Builtin("Alert_Tone_Ringtone_1");
                a.Threshold = 45; a.Delta = 5; a.SnoozeDuration = 0; a.RepeatSoundOption = RepeatSoundOption.Never;
                break;
            case AlarmType.SensorChange:
                a.SoundFile = SoundFile.Builtin("Wake_Up_Will_You"); a.Threshold = 12; a.SensorLifetimeDays = 10;
                break;
            case AlarmType.PumpChange:
                a.SoundFile = SoundFile.Builtin("Wake_Up_Will_You"); a.Threshold = 12;
                break;
            case AlarmType.Pump:
                a.SoundFile = SoundFile.Builtin("Marimba_Descend"); a.Threshold = 20;
                break;
            case AlarmType.PumpBattery:
            case AlarmType.Battery:
                a.SoundFile = SoundFile.Builtin("Machine_Charge"); a.Threshold = 20;
                break;
            case AlarmType.BatteryDrop:
                a.SoundFile = SoundFile.Builtin("Machine_Charge"); a.Delta = 10; a.MonitoringWindow = 15;
                break;
            case AlarmType.RecBolus:
                a.SoundFile = SoundFile.Builtin("Dhol_Shuffleloop"); a.Threshold = 1;
                break;
            case AlarmType.OverrideStart:
            case AlarmType.TempTargetStart:
                a.SoundFile = SoundFile.Builtin("Ending_Reached"); a.RepeatSoundOption = RepeatSoundOption.Never;
                break;
            case AlarmType.OverrideEnd:
            case AlarmType.TempTargetEnd:
                a.SoundFile = SoundFile.Builtin("Alert_Tone_Busy"); a.RepeatSoundOption = RepeatSoundOption.Never;
                break;
            case AlarmType.Temporary:
                a.SoundFile = SoundFile.Builtin("Indeed"); a.SnoozeDuration = 0; a.AboveBG = 180; a.BelowBG = 70;
                break;
            case AlarmType.DbSize:
                a.SoundFile = SoundFile.Builtin("Wrong_Answer"); a.Threshold = 75; a.SnoozeDuration = 1;
                a.ActiveOption = ActiveOption.Day; a.PlaySoundOption = PlaySoundOption.Day; a.RepeatSoundOption = RepeatSoundOption.Day;
                break;
        }
        return a;
    }
}
