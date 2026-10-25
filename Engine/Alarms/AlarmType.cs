namespace LFHub.Engine.Alarms;

/// <summary>
/// LoopFollow's alarm types (Alarm/AlarmType/AlarmType.swift). Declaration
/// order is priority order: a lower value is checked first.
/// </summary>
public enum AlarmType
{
    Temporary,
    Iob,
    Cob,
    Low,
    High,
    FastDrop,
    FastRise,
    MissedReading,
    NotLooping,
    MissedBolus,
    FutureCarbs,
    SensorChange,
    PumpChange,
    Pump,
    PumpBattery,
    Battery,
    BatteryDrop,
    RecBolus,
    OverrideStart,
    OverrideEnd,
    TempTargetStart,
    TempTargetEnd,
    BuildExpire,
    DbSize,
}

public static class AlarmTypeExtensions
{
    private static readonly (AlarmType Type, string Raw)[] RawValues =
    [
        (AlarmType.Temporary, "Temporary Alert"),
        (AlarmType.Iob, "IOB Alert"),
        (AlarmType.Cob, "COB Alert"),
        (AlarmType.Low, "Low BG Alert"),
        (AlarmType.High, "High BG Alert"),
        (AlarmType.FastDrop, "Fast Drop Alert"),
        (AlarmType.FastRise, "Fast Rise Alert"),
        (AlarmType.MissedReading, "Missed Reading Alert"),
        (AlarmType.NotLooping, "Not Looping Alert"),
        (AlarmType.MissedBolus, "Missed Bolus Alert"),
        (AlarmType.FutureCarbs, "Future Carbs Alert"),
        (AlarmType.SensorChange, "Sensor Change Alert"),
        (AlarmType.PumpChange, "Pump Change Alert"),
        (AlarmType.Pump, "Pump Insulin Alert"),
        (AlarmType.PumpBattery, "Pump Battery Alert"),
        (AlarmType.Battery, "Low Battery"),
        (AlarmType.BatteryDrop, "Battery Drop"),
        (AlarmType.RecBolus, "Rec. Bolus"),
        (AlarmType.OverrideStart, "Override Started"),
        (AlarmType.OverrideEnd, "Override Ended"),
        (AlarmType.TempTargetStart, "Temp Target Started"),
        (AlarmType.TempTargetEnd, "Temp Target Ended"),
        (AlarmType.BuildExpire, "Looping app expiration"),
        (AlarmType.DbSize, "Nightscout Database Size"),
    ];

    /// The Swift raw value: the JSON wire value and the default notification title.
    public static string RawValue(this AlarmType type) => RawValues[(int)type].Raw;

    public static AlarmType? FromRawValue(string raw)
    {
        foreach (var (type, value) in RawValues)
            if (value == raw) return type;
        return null;
    }

    public static int Priority(this AlarmType type) => (int)type;

    /// Alarms whose trigger is a BG value or its rate of change.
    public static bool IsBgBased(this AlarmType type) => type is
        AlarmType.Low or AlarmType.High or AlarmType.FastDrop or AlarmType.FastRise
        or AlarmType.MissedReading or AlarmType.Temporary;
}
