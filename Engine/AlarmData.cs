namespace LFHub.Engine;

// Mirrors LoopFollow's alarm input (Alarm/AlarmData.swift and its element
// types). All times are UTC; series are oldest first.

public sealed record GlucoseValue(int Sgv, DateTime Date);

public sealed record BolusEntry(double Units, DateTime Date);

public sealed record CarbSample(double Grams, DateTime Date);

public sealed record BatterySample(double Level, DateTime Timestamp);

public sealed record AlarmData
{
    /// The latest 24 readings.
    public required IReadOnlyList<GlucoseValue> BgReadings { get; init; }
    /// The forward series the low alarm looks ahead in, at 5-minute spacing.
    public required IReadOnlyList<GlucoseValue> PredictionData { get; init; }
    public DateTime? ExpireDate { get; init; }
    public DateTime? LastLoopTime { get; init; }
    public DateTime? LatestOverrideStart { get; init; }
    public DateTime? LatestOverrideEnd { get; init; }
    public DateTime? LatestTempTargetStart { get; init; }
    public DateTime? LatestTempTargetEnd { get; init; }
    /// Scheduled end of the active override, when known in advance.
    public DateTime? ActiveOverrideEnd { get; init; }
    /// Scheduled end of the active temp target, when known in advance.
    public DateTime? ActiveTempTargetEnd { get; init; }
    public double? RecBolus { get; init; }
    public double? Cob { get; init; }
    public DateTime? SageInsertTime { get; init; }
    public DateTime? PumpInsertTime { get; init; }
    public double? LatestPumpVolume { get; init; }
    public double? Iob { get; init; }
    public required IReadOnlyList<BolusEntry> RecentBoluses { get; init; }
    public double? LatestBattery { get; init; }
    public bool? LatestBatteryIsCharging { get; init; }
    public double? LatestPumpBattery { get; init; }
    public required IReadOnlyList<BatterySample> BatteryHistory { get; init; }
    public required IReadOnlyList<CarbSample> RecentCarbs { get; init; }
    public double? DbSizePercentage { get; init; }
}
