using System.Text;
using System.Text.Json;

namespace LFHub.Engine.Alarms;

/// <summary>
/// Reads and writes the JSON of LoopFollow's AlarmSettingsExport.encodeToJSON()
/// (Settings/ImportExport/ExportableSettings.swift): <c>{version, alarms, alarmConfiguration}</c>.
/// That encoder is a plain JSONEncoder, so dates are seconds since
/// 2001-01-01T00:00:00Z as a JSON number, nil optionals are left out, UUIDs are
/// uppercase strings and enums are their raw values. A built-in sound is a bare
/// string; a custom sound is <c>{"kind":"custom","id":UUID}</c>.
/// Keys that Swift decodes without a default are required here too.
/// </summary>
public static class AlarmSettingsJson
{
    /// Foundation's reference date, the zero of JSONEncoder's default date strategy.
    public static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static (IReadOnlyList<Alarm> Alarms, AlarmConfiguration Config) Parse(string json) =>
        ParseLenient(json, out _);

    /// <summary>
    /// Parses the export, skipping alarms this hub does not understand (e.g. a type a
    /// newer LoopFollow added) so the others keep working; `skipped` says how many.
    /// </summary>
    public static (IReadOnlyList<Alarm> Alarms, AlarmConfiguration Config) ParseLenient(string json, out int skipped)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var alarms = new List<Alarm>();
        skipped = 0;
        foreach (var element in Required(root, "alarms").EnumerateArray())
        {
            try { alarms.Add(ParseAlarm(element)); }
            catch (JsonException) { skipped++; }
        }
        var config = ParseConfiguration(Required(root, "alarmConfiguration"));
        return (alarms, config);
    }

    public static Alarm ParseAlarm(JsonElement e) => new()
    {
        Id = Guid.Parse(Required(e, "id").GetString()!),
        Type = AlarmTypeExtensions.FromRawValue(Required(e, "type").GetString()!)
            ?? throw new JsonException($"Unknown alarm type '{e.GetProperty("type").GetString()}'"),
        Name = Required(e, "name").GetString()!,
        IsEnabled = Bool(e, "isEnabled") ?? true,
        SnoozedUntil = Date(e, "snoozedUntil"),
        AboveBG = Double(e, "aboveBG"),
        BelowBG = Double(e, "belowBG"),
        Threshold = Double(e, "threshold"),
        PredictiveMinutes = Int(e, "predictiveMinutes"),
        Delta = Double(e, "delta"),
        PersistentMinutes = Int(e, "persistentMinutes"),
        SuppressIfRising = Bool(e, "suppressIfRising") ?? false,
        SuppressIfFalling = Bool(e, "suppressIfFalling") ?? false,
        SuppressIfCharging = Bool(e, "suppressIfCharging") ?? false,
        MonitoringWindow = Int(e, "monitoringWindow"),
        SoundFile = ParseSoundFile(Required(e, "soundFile")),
        SnoozeDuration = Int(e, "snoozeDuration") ?? 5,
        PlaySoundOption = Enum(e, "playSoundOption", PlaySoundOption.Always),
        RepeatSoundOption = Enum(e, "repeatSoundOption", RepeatSoundOption.Always),
        SoundDelay = Int(e, "soundDelay") ?? 0,
        ActiveOption = Enum(e, "activeOption", ActiveOption.Always),
        MissedBolusPrebolusWindow = Int(e, "missedBolusPrebolusWindow"),
        MissedBolusIgnoreSmallBolusUnits = Double(e, "missedBolusIgnoreSmallBolusUnits"),
        MissedBolusIgnoreUnderGrams = Double(e, "missedBolusIgnoreUnderGrams"),
        MissedBolusIgnoreUnderBG = Double(e, "missedBolusIgnoreUnderBG"),
        BolusCountThreshold = Int(e, "bolusCountThreshold"),
        BolusWindowMinutes = Int(e, "bolusWindowMinutes"),
        SensorLifetimeDays = Int(e, "sensorLifetimeDays"),
    };

    /// AlarmConfiguration uses the synthesized Codable: every non-optional key is required.
    public static AlarmConfiguration ParseConfiguration(JsonElement e) => new()
    {
        SnoozeUntil = Date(e, "snoozeUntil"),
        MuteUntil = Date(e, "muteUntil"),
        DayStart = ParseTimeOfDay(Required(e, "dayStart")),
        NightStart = ParseTimeOfDay(Required(e, "nightStart")),
        OverrideSystemOutputVolume = Required(e, "overrideSystemOutputVolume").GetBoolean(),
        ForcedOutputVolume = Required(e, "forcedOutputVolume").GetSingle(),
        AudioDuringCalls = Required(e, "audioDuringCalls").GetBoolean(),
        IgnoreZeroBG = Required(e, "ignoreZeroBG").GetBoolean(),
        AutoSnoozeCGMStart = Required(e, "autoSnoozeCGMStart").GetBoolean(),
        EnableVolumeButtonSnooze = Required(e, "enableVolumeButtonSnooze").GetBoolean(),
    };

    /// <summary>Writes the same JSON LoopFollow's encoder writes.</summary>
    public static string Serialize(IReadOnlyList<Alarm> alarms, AlarmConfiguration config, string version)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("version", version);
            w.WritePropertyName("alarms");
            w.WriteStartArray();
            foreach (var a in alarms) WriteAlarm(w, a);
            w.WriteEndArray();
            w.WritePropertyName("alarmConfiguration");
            WriteConfiguration(w, config);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void WriteAlarm(Utf8JsonWriter w, Alarm a)
    {
        w.WriteStartObject();
        w.WriteString("id", a.Id.ToString().ToUpperInvariant());
        w.WriteString("type", a.Type.RawValue());
        w.WriteString("name", a.Name);
        w.WriteBoolean("isEnabled", a.IsEnabled);
        WriteDate(w, "snoozedUntil", a.SnoozedUntil);
        WriteNumber(w, "aboveBG", a.AboveBG);
        WriteNumber(w, "belowBG", a.BelowBG);
        WriteNumber(w, "threshold", a.Threshold);
        WriteNumber(w, "predictiveMinutes", a.PredictiveMinutes);
        WriteNumber(w, "delta", a.Delta);
        WriteNumber(w, "persistentMinutes", a.PersistentMinutes);
        w.WriteBoolean("suppressIfRising", a.SuppressIfRising);
        w.WriteBoolean("suppressIfFalling", a.SuppressIfFalling);
        w.WriteBoolean("suppressIfCharging", a.SuppressIfCharging);
        WriteNumber(w, "monitoringWindow", a.MonitoringWindow);
        w.WritePropertyName("soundFile");
        if (a.SoundFile.BuiltinName is { } name)
        {
            w.WriteStringValue(name);
        }
        else
        {
            w.WriteStartObject();
            w.WriteString("kind", "custom");
            w.WriteString("id", a.SoundFile.CustomId!.Value.ToString().ToUpperInvariant());
            w.WriteEndObject();
        }
        w.WriteNumber("snoozeDuration", a.SnoozeDuration);
        w.WriteString("playSoundOption", Raw(a.PlaySoundOption));
        w.WriteString("repeatSoundOption", Raw(a.RepeatSoundOption));
        w.WriteNumber("soundDelay", a.SoundDelay);
        w.WriteString("activeOption", Raw(a.ActiveOption));
        WriteNumber(w, "missedBolusPrebolusWindow", a.MissedBolusPrebolusWindow);
        WriteNumber(w, "missedBolusIgnoreSmallBolusUnits", a.MissedBolusIgnoreSmallBolusUnits);
        WriteNumber(w, "missedBolusIgnoreUnderGrams", a.MissedBolusIgnoreUnderGrams);
        WriteNumber(w, "missedBolusIgnoreUnderBG", a.MissedBolusIgnoreUnderBG);
        WriteNumber(w, "bolusCountThreshold", a.BolusCountThreshold);
        WriteNumber(w, "bolusWindowMinutes", a.BolusWindowMinutes);
        WriteNumber(w, "sensorLifetimeDays", a.SensorLifetimeDays);
        w.WriteEndObject();
    }

    public static void WriteConfiguration(Utf8JsonWriter w, AlarmConfiguration c)
    {
        w.WriteStartObject();
        WriteDate(w, "snoozeUntil", c.SnoozeUntil);
        WriteDate(w, "muteUntil", c.MuteUntil);
        WriteTimeOfDay(w, "dayStart", c.DayStart);
        WriteTimeOfDay(w, "nightStart", c.NightStart);
        w.WriteBoolean("overrideSystemOutputVolume", c.OverrideSystemOutputVolume);
        w.WriteNumber("forcedOutputVolume", c.ForcedOutputVolume);
        w.WriteBoolean("audioDuringCalls", c.AudioDuringCalls);
        w.WriteBoolean("ignoreZeroBG", c.IgnoreZeroBG);
        w.WriteBoolean("autoSnoozeCGMStart", c.AutoSnoozeCGMStart);
        w.WriteBoolean("enableVolumeButtonSnooze", c.EnableVolumeButtonSnooze);
        w.WriteEndObject();
    }

    public static DateTime FromReferenceSeconds(double seconds) =>
        ReferenceDate.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    public static double ToReferenceSeconds(DateTime utc) =>
        (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond;

    /// Accepts the legacy bare-string form and both keyed forms (SoundFile.swift).
    private static SoundFile ParseSoundFile(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return SoundFile.Builtin(e.GetString()!);
        var kind = Required(e, "kind").GetString();
        return kind switch
        {
            "builtin" => SoundFile.Builtin(Required(e, "name").GetString()!),
            "custom" => SoundFile.Custom(Guid.Parse(Required(e, "id").GetString()!)),
            _ => throw new JsonException($"Unknown SoundFile kind: {kind}"),
        };
    }

    private static TimeOfDay ParseTimeOfDay(JsonElement e) =>
        new(IntValue(Required(e, "hour"), "hour"), IntValue(Required(e, "minute"), "minute"));

    private static void WriteTimeOfDay(Utf8JsonWriter w, string key, TimeOfDay t)
    {
        w.WriteStartObject(key);
        w.WriteNumber("hour", t.Hour);
        w.WriteNumber("minute", t.Minute);
        w.WriteEndObject();
    }

    private static JsonElement Required(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : throw new JsonException($"Missing key '{key}'");

    private static JsonElement? Optional(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static bool? Bool(JsonElement e, string key) => Optional(e, key)?.GetBoolean();
    private static double? Double(JsonElement e, string key) => Optional(e, key)?.GetDouble();
    private static DateTime? Date(JsonElement e, string key) =>
        Optional(e, key) is { } v ? FromReferenceSeconds(v.GetDouble()) : null;
    private static int? Int(JsonElement e, string key) =>
        Optional(e, key) is { } v ? IntValue(v, key) : null;

    /// Swift's JSONDecoder reads an Int from any integral number, e.g. 5.0.
    private static int IntValue(JsonElement v, string key)
    {
        if (v.TryGetInt32(out var i)) return i;
        var d = v.GetDouble();
        if (d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
        throw new JsonException($"'{key}' is not an integer");
    }

    private static T Enum<T>(JsonElement e, string key, T fallback) where T : struct, System.Enum
    {
        if (Optional(e, key) is not { } v) return fallback;
        var raw = v.GetString() ?? "";
        foreach (var value in System.Enum.GetValues<T>())
            if (Raw(value) == raw) return value;
        throw new JsonException($"Unknown {key} '{raw}'");
    }

    private static string Raw<T>(T value) where T : struct, System.Enum => value.ToString().ToLowerInvariant();

    private static void WriteDate(Utf8JsonWriter w, string key, DateTime? value)
    {
        if (value is { } d) w.WriteNumber(key, ToReferenceSeconds(d));
    }

    private static void WriteNumber(Utf8JsonWriter w, string key, double? value)
    {
        if (value is { } d) w.WriteNumber(key, d);
    }

    private static void WriteNumber(Utf8JsonWriter w, string key, int? value)
    {
        if (value is { } i) w.WriteNumber(key, i);
    }
}
