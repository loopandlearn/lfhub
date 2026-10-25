namespace LFHub.Engine.Alarms;

/// A future-dated carb entry seen before its time (FutureCarbsCondition).
public sealed record PendingFutureCarb(DateTime CarbDate, double Grams, DateTime ObservedAt);

/// <summary>
/// Everything LoopFollow's alarm engine remembers between ticks: the
/// last*Notified markers and pendingFutureCarbs from Storage, currentAlarm and
/// its title override from Observable, and AlarmManager.lastBGAlarmTime.
/// Markers are shared by all alarms of a type, as in LoopFollow.
/// Plain properties, so System.Text.Json round-trips it.
/// </summary>
public sealed class AlarmState
{
    /// The alarm that is sounding, until snoozed or until it stops meeting its condition.
    public Guid? CurrentAlarmId { get; set; }
    public string? CurrentAlarmTitleOverride { get; set; }
    /// Date of the reading a BG-based alarm last fired on.
    public DateTime? LastBGAlarmTime { get; set; }

    /// <summary>
    /// When the hub last ran a BG fetch, successful or not. Set by the caller,
    /// not by the engine: LoopFollow sets it in its fetch code, and the missed
    /// reading alarm stays silent when it is more than 6 minutes old.
    /// </summary>
    public DateTime? LastBGChecked { get; set; }
    /// When the hub last ran a device status fetch, successful or not. Set by the
    /// caller; the not looping alarm stays silent when it is more than 6 minutes old.
    public DateTime? LastLoopingChecked { get; set; }

    public double? LastCOBNotified { get; set; }
    public double? LastRecBolusNotified { get; set; }
    public DateTime? LastMissedBolusNotified { get; set; }
    public List<PendingFutureCarb> PendingFutureCarbs { get; set; } = [];
    public DateTime? LastOverrideStartNotified { get; set; }
    public DateTime? LastOverrideEndNotified { get; set; }
    public DateTime? LastOverridePreEndNotified { get; set; }
    public DateTime? LastTempTargetStartNotified { get; set; }
    public DateTime? LastTempTargetEndNotified { get; set; }
    public DateTime? LastTempTargetPreEndNotified { get; set; }

    public AlarmState Clone()
    {
        var copy = (AlarmState)MemberwiseClone();
        copy.PendingFutureCarbs = [.. PendingFutureCarbs];
        return copy;
    }
}
