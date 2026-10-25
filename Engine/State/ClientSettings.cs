using System.Text.Json;
using System.Text.Json.Nodes;

namespace LFHub.Engine.State;

/// <summary>
/// The LoopFollow settings that change the snapshot or AlarmData values, keyed as
/// LoopFollow's Storage keys (LoopFollow/Storage/Storage.swift), plus the parts of
/// the phone's locale the app's formatting reads. Missing or mistyped keys take
/// LoopFollow's defaults.
/// </summary>
public sealed record ClientSettings
{
    public static readonly ClientSettings Default = new();

    /// "units": "mg/dL" or "mmol/L"; the snapshot's `unit`.
    public bool Mmol { get; init; }

    /// "predictionToLoad", hours: the OpenAPS min/max window, the length of Loop's
    /// stored forecast, the carbs horizon and the override graph's future edge.
    public double PredictionToLoad { get; init; } = 1.0;

    /// "downloadPrediction": Loop's forecast and min/max are stored only when on.
    public bool DownloadPrediction { get; init; } = true;

    /// "downloadTreatments": off leaves every treatments-derived value unset.
    public bool DownloadTreatments { get; init; } = true;

    /// "downloadDays": the BG and treatments windows and the override/temp target graph horizon.
    public int DownloadDays { get; init; } = 1;

    /// The most days LoopFollow's graph settings offer ("Show Days Back", 1…4).
    public const int MaxDownloadDays = 4;

    /// "showYesterdayLine": the graph's overlay of the same time yesterday, for which the
    /// BG history reaches one day further back (BGData.swift bgFetchDays).
    public bool ShowYesterdayLine { get; init; }

    /// Days of BG history to fetch: <see cref="DownloadDays"/>, plus one for the yesterday overlay.
    public int BgFetchDays => DownloadDays + (ShowYesterdayLine ? 1 : 0);

    /// "bgUpdateDelay", seconds: added to the next BG poll after a fresh reading.
    public int BgUpdateDelay { get; init; } = 10;

    /// "infoDisplayItems": whether the IAGE row is visible, which is what lets
    /// LoopFollow look up an insulin change older than the treatments window.
    public bool IageVisible { get; init; }

    /// "timeZone": the phone's IANA zone. "Today" for carbsToday, local midnight for
    /// the scheduled basal, the profile's zone when it names none LoopFollow can
    /// resolve, and the time in cgmSensorState.
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// "decimalSeparator": Locale.current.decimalSeparator, used in the basal string.
    public string DecimalSeparator { get; init; } = ".";

    /// "timeFormat": the locale's short time pattern
    /// (DateFormatter.dateFormat(fromTemplate: "jmm", ...)), used in cgmSensorState.
    public string TimeFormat { get; init; } = "HH:mm";

    /// LoopFollow's "Persistent Notification": a "Latest BG" notification per new reading.
    public bool PersistentNotification { get; init; }

    /// "speakBG": LoopFollow's Speak BG, which speaks a new reading that meets a speak condition.
    public bool Speak { get; init; }

    /// "speakBGAlways": every new reading is spoken.
    public bool SpeakAlways { get; init; } = true;

    /// "speakLowBG": a reading is spoken when it or the one before it is at or below <see cref="SpeakLowLimit"/>.
    public bool SpeakLow { get; init; }

    /// <summary>
    /// "speakProactiveLowBG": a reading is spoken as for Low, when the first forecast value is at or
    /// below <see cref="SpeakLowLimit"/>, or when it is at or below <see cref="SpeakHighLimit"/> and
    /// at least <see cref="SpeakFastDropDelta"/> below the one before it.
    /// </summary>
    public bool SpeakProactiveLow { get; init; }

    /// "speakHighBG": a reading is spoken when it or the one before it is at or above <see cref="SpeakHighLimit"/>.
    public bool SpeakHigh { get; init; }

    /// "speakFastDropDelta", mg/dL.
    public double SpeakFastDropDelta { get; init; } = 10;

    /// "speakLowBGLimit", mg/dL.
    public double SpeakLowLimit { get; init; } = 72;

    /// "speakHighBGLimit", mg/dL.
    public double SpeakHighLimit { get; init; } = 180;

    /// "speakLanguage": the app's language code for the spoken text.
    public string SpeakLanguage { get; init; } = "en";

    /// InfoType.iage's raw value in LoopFollow/InfoTable/InfoType.swift.
    private const int IageInfoType = 19;

    public static ClientSettings FromJson(JsonObject? json)
    {
        if (json is null) return Default;
        var d = Default;

        var units = Swift.Str(json, "units");
        var mmol = units is not null ? units == "mmol/L" : Swift.Str(json, "unit") == "mmol";

        var iageVisible = false;
        if (json["infoDisplayItems"] is JsonArray items)
            foreach (var item in items)
                if (item is JsonObject o && Swift.Int(o["type"]) == IageInfoType)
                {
                    iageVisible = Swift.Bool(o, "isVisible") ?? false;
                    break;
                }

        var zone = ResolveTimeZone(json) ?? d.TimeZone;

        return new ClientSettings
        {
            Mmol = mmol,
            PredictionToLoad = Swift.Dbl(json["predictionToLoad"]) ?? d.PredictionToLoad,
            DownloadPrediction = StrictBool(json, "downloadPrediction") ?? d.DownloadPrediction,
            DownloadTreatments = StrictBool(json, "downloadTreatments") ?? d.DownloadTreatments,
            DownloadDays = (int?)Swift.Int(json["downloadDays"]) is { } days and >= 1
                ? Math.Min(days, MaxDownloadDays)
                : d.DownloadDays,
            ShowYesterdayLine = StrictBool(json, "showYesterdayLine") ?? false,
            BgUpdateDelay = (int?)Swift.Int(json["bgUpdateDelay"]) ?? d.BgUpdateDelay,
            IageVisible = iageVisible,
            TimeZone = zone,
            DecimalSeparator = Swift.Str(json, "decimalSeparator") is { Length: > 0 } sep ? sep : d.DecimalSeparator,
            TimeFormat = Swift.Str(json, "timeFormat") is { Length: > 0 } tf ? tf : d.TimeFormat,
            PersistentNotification = StrictBool(json, "persistentNotification") ?? false,
            Speak = StrictBool(json, "speakBG") ?? d.Speak,
            SpeakAlways = StrictBool(json, "speakBGAlways") ?? d.SpeakAlways,
            SpeakLow = StrictBool(json, "speakLowBG") ?? d.SpeakLow,
            SpeakProactiveLow = StrictBool(json, "speakProactiveLowBG") ?? d.SpeakProactiveLow,
            SpeakHigh = StrictBool(json, "speakHighBG") ?? d.SpeakHigh,
            SpeakFastDropDelta = Swift.Dbl(json["speakFastDropDelta"]) ?? d.SpeakFastDropDelta,
            SpeakLowLimit = Swift.Dbl(json["speakLowBGLimit"]) ?? d.SpeakLowLimit,
            SpeakHighLimit = Swift.Dbl(json["speakHighBGLimit"]) ?? d.SpeakHighLimit,
            SpeakLanguage = Swift.Str(json, "speakLanguage") is { Length: > 0 } lang ? lang : d.SpeakLanguage,
        };
    }

    /// <summary>
    /// The zone named by "timeZone", or null when it is missing or this host does not know it.
    /// Without one, day and night, "today" and local times are taken in UTC.
    /// </summary>
    public static TimeZoneInfo? ResolveTimeZone(JsonObject json) =>
        Swift.Str(json, "timeZone") is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : null;

    private static bool? StrictBool(JsonObject json, string key) =>
        json[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
}
