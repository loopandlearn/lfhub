namespace LFHub.Engine.Alarms;

/// <summary>
/// How a hub-delivered alarm shows its type: an emoji leading the title, and
/// LoopFollow's own SF Symbol for the type (AlarmType.icon in Alarm.swift) on
/// the system alarm's button.
/// </summary>
public static class AlarmTypeVisuals
{
    public static string Emoji(this AlarmType type) => type switch
    {
        AlarmType.Low => "🔻",
        AlarmType.High => "🔺",
        AlarmType.FastDrop => "⏬",
        AlarmType.FastRise => "⏫",
        AlarmType.MissedReading => "📡",
        AlarmType.Iob => "💉",
        AlarmType.Cob => "🍽️",
        AlarmType.MissedBolus => "⚠️",
        AlarmType.FutureCarbs => "⏰",
        AlarmType.RecBolus => "🧮",
        AlarmType.Battery => "🔋",
        AlarmType.BatteryDrop => "🪫",
        AlarmType.Pump => "💧",
        AlarmType.PumpBattery => "🔌",
        AlarmType.PumpChange => "🔄",
        AlarmType.SensorChange => "🩹",
        AlarmType.NotLooping => "⭕",
        AlarmType.BuildExpire => "📅",
        AlarmType.DbSize => "💾",
        AlarmType.OverrideStart => "▶️",
        AlarmType.OverrideEnd => "⏹️",
        AlarmType.TempTargetStart => "🎯",
        AlarmType.TempTargetEnd => "🏁",
        AlarmType.Temporary => "🔔",
        _ => "🔔",
    };

    public static string SymbolName(this AlarmType type) => type switch
    {
        AlarmType.Low => "arrow.down.to.line",
        AlarmType.High => "arrow.up.to.line",
        AlarmType.FastDrop => "chevron.down.2",
        AlarmType.FastRise => "chevron.up.2",
        AlarmType.MissedReading => "wifi.slash",
        AlarmType.Iob => "syringe",
        AlarmType.Cob => "fork.knife",
        AlarmType.MissedBolus => "exclamationmark.arrow.triangle.2.circlepath",
        AlarmType.FutureCarbs => "clock.arrow.circlepath",
        AlarmType.RecBolus => "bolt.horizontal",
        AlarmType.Battery => "battery.25",
        AlarmType.BatteryDrop => "battery.100.bolt",
        AlarmType.Pump => "drop",
        AlarmType.PumpBattery => "powermeter",
        AlarmType.PumpChange => "arrow.triangle.2.circlepath",
        AlarmType.SensorChange => "sensor.tag.radiowaves.forward",
        AlarmType.NotLooping => "circle.slash",
        AlarmType.BuildExpire => "calendar.badge.exclamationmark",
        AlarmType.DbSize => "externaldrive.badge.exclamationmark",
        AlarmType.OverrideStart => "play.circle",
        AlarmType.OverrideEnd => "stop.circle",
        AlarmType.TempTargetStart => "flag",
        AlarmType.TempTargetEnd => "flag.slash",
        _ => "bell",
    };
}
