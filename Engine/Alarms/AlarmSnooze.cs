namespace LFHub.Engine.Alarms;

/// <summary>
/// The snooze LoopFollow applies on its own: Snooze All after a sensor start. The
/// app applies a user's snooze and uploads it with its alarm export.
/// </summary>
public static class AlarmSnooze
{
    /// <summary>
    /// The global snooze LoopFollow sets when it reads a sensor start younger
    /// than 2 hours and autoSnoozeCGMStart is on (Controllers/Nightscout/SAge.swift:56):
    /// Snooze All until 2 hours after the start. Null when nothing changes.
    /// The hub calls it when it refreshes the sensor start time.
    /// </summary>
    public static DateTime? AutoSnoozeOnCgmStart(AlarmConfiguration config, DateTime? sageInsertTime, DateTime nowUtc)
    {
        if (!config.AutoSnoozeCGMStart || sageInsertTime is not { } start) return null;
        return (nowUtc - start).TotalSeconds < 7200 ? start.AddHours(2) : null;
    }
}
