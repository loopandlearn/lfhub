using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LFHub.Engine.State;

/// <summary>
/// What LoopFollow keeps from the devicestatus documents it fetches (count=1),
/// field by field as DeviceStatus.swift, DeviceStatusLoop.swift and
/// DeviceStatusOpenAPS.swift write it. A field keeps its value until a document
/// replaces or clears it.
/// </summary>
internal sealed partial class DeviceStatusState
{
    private const int BatteryHistoryLength = 30;

    /// Storage.device: "Loop", or the OpenAPS document's `device`.
    public string Device { get; private set; } = "";
    /// Observable.alertLastLoopTime (Storage.lastLoopTime moves with it).
    public double? LastLoopTime { get; private set; }

    public double? Iob { get; private set; }
    public double? Cob { get; private set; }
    public double? Projected { get; private set; }
    public double? RecBolus { get; private set; }
    public double? Battery { get; private set; }
    public bool? BatteryIsCharging { get; private set; }
    public List<(double Level, DateTime At)> BatteryHistory { get; } = [];
    public double? PumpBattery { get; private set; }
    /// latestPumpBatteryDate; only the info row's one-hour hold reads it.
    public DateTime? PumpBatteryAt { get; private set; }
    /// Storage.lastPumpReservoirU: the snapshot's reservoir, nil above a pump's reporting limit.
    public double? ReservoirU { get; private set; }
    /// latestPumpVolume: the alarm's reservoir, 50 above a pump's reporting limit.
    public double? PumpVolume { get; private set; }
    public double? Autosens { get; private set; }
    public double? Tdd { get; private set; }
    public double? TargetLowMgdl { get; private set; }
    public double? TargetHighMgdl { get; private set; }
    public double? IsfMgdl { get; private set; }
    public double? CarbRatio { get; private set; }

    /// Loop's forecast as MainViewController.predictionData holds it with downloadPrediction on:
    /// the raw values and the loop time the first point is placed at.
    public (List<double> Values, double Start)? LoopForecast { get; private set; }
    /// Storage.lastMinBgMgdl/lastMaxBgMgdl as Loop last set them (downloadPrediction on), with a write sequence.
    public (double Min, double Max, long Seq)? LoopMinMax { get; private set; }
    /// The predBGs OpenAPS last set lastMinBg/lastMaxBg from; the window depends on predictionToLoad.
    public (Dictionary<string, List<double>> PredBGs, long Seq)? OpenApsMinMax { get; private set; }
    /// openAPSPredBGs and openAPSPredUpdatedTime, the alarm's OpenAPS forecasts.
    public Dictionary<string, List<double>>? OpenApsPredBGs { get; private set; }
    public double? OpenApsPredUpdatedTime { get; private set; }

    private PumpReservoirCache? _reservoirCache;
    private long _seq;

    public static readonly string[] PredictionTypes = ["ZT", "IOB", "COB", "UAM"];

    /// <summary>
    /// Processes one devicestatus response. Returns false for a response LoopFollow
    /// treats as a failed fetch, null for an empty one, true otherwise.
    /// </summary>
    public bool? Apply(JsonNode? response, DateTime now, NsProfile? profile, TimeZoneInfo profileFallbackZone)
    {
        if (response is not JsonArray arr || arr.Any(x => x is not JsonObject)) return false;
        if (arr.Count == 0) return null;

        var ds = (JsonObject)arr[0]!;
        var previousDeviceWasLoop = Device == "Loop";
        var previousLoopTime = LastLoopTime;
        // A pump or loop time from a clock running ahead is not taken as the latest loop.
        var latestPlausible = Swift.Unix(now) + PatientState.FutureToleranceSeconds;

        var pump = Swift.Obj(ds, "pump");
        if (pump is not null)
        {
            if (Swift.IsoPrefix(Swift.Str(pump, "clock")) is { } pumpTime)
            {
                if (pumpTime > (LastLoopTime ?? 0) && pumpTime <= latestPlausible) LastLoopTime = pumpTime;

                var resolution = PumpReservoirResolver.Resolve(Swift.Dbl(pump, "reservoir"), Swift.Str(pump, "pumpID"),
                    Swift.Str(pump, "manufacturer"), Swift.Str(pump, "model"), _reservoirCache, now);
                _reservoirCache = resolution.Cache;
                switch (resolution.State)
                {
                    case ReservoirState.Units:
                        PumpVolume = resolution.Units;
                        ReservoirU = resolution.Units;
                        break;
                    case ReservoirState.AboveReportingLimit:
                        PumpVolume = 50.0;
                        ReservoirU = null;
                        break;
                    default:
                        PumpVolume = null;
                        ReservoirU = null;
                        break;
                }
            }

            if (Swift.Dbl(Swift.Obj(pump, "battery"), "percent") is { } percent)
            {
                PumpBattery = percent;
                PumpBatteryAt = now;
            }

            if (Swift.Obj(ds, "uploader") is { } uploader && Swift.Dbl(uploader, "battery") is { } upbat)
            {
                Battery = upbat;
                BatteryIsCharging = Swift.Bool(uploader, "isCharging");
                // `uploader["timestamp"] as? Date` never matches JSON, so the sample is stamped now.
                BatteryHistory.Add((upbat, now));
                if (BatteryHistory.Count > BatteryHistoryLength) BatteryHistory.RemoveAt(0);
            }
        }

        if (Swift.Obj(ds, "loop") is { } loop)
        {
            if (!(pump is not null && Swift.Has(pump, "clock"))
                && Swift.IsoPrefix(Swift.Str(loop, "timestamp")) is { } loopTime
                && loopTime > (LastLoopTime ?? 0) && loopTime <= latestPlausible)
                LastLoopTime = loopTime;

            ApplyLoop(loop, previousLoopTime ?? 0, LastLoopTime ?? 0, now, profile, profileFallbackZone);
        }

        if (Swift.Obj(ds, "openaps") is { } openaps)
            ApplyOpenAps(ds, openaps, now, profile, profileFallbackZone);

        var currentDeviceIsLoop = Device == "Loop";
        if (currentDeviceIsLoop != previousDeviceWasLoop)
        {
            if (currentDeviceIsLoop)
            {
                OpenApsPredBGs = null;
                OpenApsPredUpdatedTime = null;
            }
            else
            {
                LoopForecast = null;
            }
        }
        return true;
    }

    private void ApplyLoop(JsonObject loop, double previousLoopTime, double lastLoopTime, DateTime now,
        NsProfile? profile, TimeZoneInfo fallback)
    {
        Device = "Loop";

        if (Swift.Dbl(Swift.Obj(loop, "iob"), "iob") is { } iob) Iob = iob;
        if (Swift.Dbl(Swift.Obj(loop, "cob"), "cob") is { } cob) Cob = cob;

        if (Swift.Has(loop, "failureReason")) return;

        if (profile?.IsfMgdl(now, fallback) is { } isf) IsfMgdl = isf;
        if (profile?.CarbRatioAt(now, fallback) is { } cr) CarbRatio = cr;
        TargetLowMgdl = profile?.TargetLowMgdl(now, fallback);
        TargetHighMgdl = profile?.TargetHighMgdl(now, fallback);

        var values = Swift.Dbls(Swift.Obj(loop, "predicted")?["values"]);
        if (values is { Count: > 0 })
        {
            if (previousLoopTime < lastLoopTime || LoopForecast is null)
            {
                LoopForecast = (values, lastLoopTime);
                LoopMinMax = (values.Min(), values.Max(), ++_seq);
            }
        }
        else
        {
            LoopForecast = null;
        }

        RecBolus = Swift.Dbl(loop, "recommendedBolus");
        Projected = values is { Count: > 0 } ? values[^1] : null;
    }

    private void ApplyOpenAps(JsonObject ds, JsonObject openaps, DateTime now, NsProfile? profile, TimeZoneInfo fallback)
    {
        Device = Swift.Str(ds, "device") ?? "";
        if (Swift.Has(openaps, "failureReason")) return;

        var suggested = Swift.Obj(openaps, "suggested");
        var enacted = Swift.Obj(openaps, "enacted");
        if ((suggested ?? enacted) is not { } eos) return;

        var updatedTime = Swift.IsoPrefix(Swift.Str(eos, "deliverAt") ?? Swift.Str(eos, "timestamp"));
        var reason = Swift.Str(eos, "reason");

        var profileIsf = profile?.IsfMgdl(now, fallback);
        var enactedIsf = Swift.Dbl(eos, "ISF") is { } isfValue && isfValue != 0 ? isfValue : (double?)null;
        if (profileIsf is { } pIsf && enactedIsf is { } eIsf && pIsf != eIsf) IsfMgdl = eIsf;
        else if (profileIsf is not null) IsfMgdl = profileIsf;

        var profileCr = profile?.CarbRatioAt(now, fallback);
        double? enactedCr = Swift.Dbl(eos, "CR") is { } crValue && crValue != 0 ? crValue : FirstMatch(CarbRatioPattern(), reason);
        if (profileCr is { } pCr && enactedCr is { } eCr && pCr != eCr) CarbRatio = eCr;
        else if (profileCr is not null) CarbRatio = profileCr;

        if (Swift.Dbl(Swift.Obj(openaps, "iob"), "iob") is { } iob) Iob = iob;

        if (Swift.Dbl(eos, "COB") is { } cob) Cob = cob;
        else if (FirstMatch(CobPattern(), reason) is { } cobFromReason) Cob = cobFromReason;

        if (Swift.Dbl(eos, "sensitivityRatio") is { } sens) Autosens = sens;

        RecBolus = Swift.Dbl(openaps, "recommendedBolus");
        Projected = Swift.Dbl(eos, "eventualBG");

        var profileTargetHigh = profile?.TargetHighMgdl(now, fallback);
        double? enactedTarget = Swift.Dbl(eos, "current_target") is { } t ? (t < 40 ? t * Swift.MgdlPerMmol : t) : null;
        if (profileTargetHigh is not null && enactedTarget is { } et)
        {
            TargetLowMgdl = et;
            TargetHighMgdl = et;
        }
        else if (profileTargetHigh is { } pt)
        {
            TargetLowMgdl = pt;
            TargetHighMgdl = pt;
        }

        if ((Swift.Dbl(eos, "TDD") ?? Swift.Dbl(enacted, "TDD")) is { } tdd) Tdd = tdd;

        var predBGs = Swift.Obj(suggested, "predBGs") ?? Swift.Obj(enacted, "predBGs");
        if (predBGs is not null)
        {
            var raw = new Dictionary<string, List<double>>();
            foreach (var type in PredictionTypes)
                if (Swift.Dbls(predBGs[type]) is { Count: > 0 } arr)
                    raw[type] = arr;
            OpenApsPredBGs = raw.Count == 0 ? null : raw;
            OpenApsPredUpdatedTime = updatedTime;
            if (raw.Count > 0) OpenApsMinMax = (raw, ++_seq);
        }
        else
        {
            OpenApsPredBGs = null;
            OpenApsPredUpdatedTime = null;
        }
    }

    /// The min/max the snapshot shows for a device with these prediction settings.
    public (double Min, double Max)? MinMax(ClientSettings settings)
    {
        var loop = settings.DownloadPrediction ? LoopMinMax : null;
        var openAps = OpenApsMinMax;
        if (loop is { } l && (openAps is null || l.Seq > openAps.Value.Seq)) return (l.Min, l.Max);
        if (openAps is not { } o) return null;

        var toLoad = (int)(settings.PredictionToLoad * 12);
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var type in PredictionTypes)
        {
            if (!o.PredBGs.TryGetValue(type, out var arr)) continue;
            for (var i = 0; i <= Math.Min(toLoad, arr.Count - 1); i++)
            {
                min = Math.Min(min, arr[i]);
                max = Math.Max(max, arr[i]);
            }
        }
        return double.IsInfinity(min) || double.IsInfinity(max) ? null : (min, max);
    }

    /// The next devicestatus poll after a processed document (DeviceStatus.swift); stretched ×3 while the socket is live.
    public TimeSpan NextPoll(DateTime now, bool socketLive)
    {
        var secondsAgo = Swift.Unix(now) - (LastLoopTime ?? 0);
        double interval = secondsAgo switch
        {
            >= 20 * 60 => 5 * 60,
            >= 10 * 60 => 60,
            >= 7 * 60 => 30,
            >= 5 * 60 => 10,
            _ => 310 - secondsAgo,
        };
        return socketLive ? TimeSpan.FromSeconds(Math.Max(interval * 3, 60)) : TimeSpan.FromSeconds(interval);
    }

    private static double? FirstMatch(Regex pattern, string? text)
    {
        if (text is null) return null;
        var m = pattern.Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"CR: (\d+(?:\.\d+)?)")]
    private static partial Regex CarbRatioPattern();

    [GeneratedRegex(@"COB: (\d+(?:\.\d+)?)")]
    private static partial Regex CobPattern();
}

internal enum ReservoirState { Units, AboveReportingLimit, Unknown }

internal sealed record PumpReservoirCache(string PumpId, DateTime PumpSince, (double Units, DateTime Date)? Reading);

/// <summary>LoopFollow's PumpReservoirResolver (PumpReservoir.swift).</summary>
internal static class PumpReservoirResolver
{
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PumpSettleTime = TimeSpan.FromMinutes(15);

    public sealed record Resolution(ReservoirState State, double? Units, PumpReservoirCache? Cache);

    public static Resolution Resolve(double? reservoir, string? pumpId, string? manufacturer, string? model,
        PumpReservoirCache? stored, DateTime now)
    {
        var withoutReading = ReportsVolumeOnlyWhenLow(manufacturer, model)
            ? ReservoirState.AboveReportingLimit
            : ReservoirState.Unknown;

        if (IdentifiedPump(pumpId) is not { } id)
            return reservoir is { } r ? new(ReservoirState.Units, r, null) : new(withoutReading, null, null);

        var cache = stored?.PumpId == id ? stored : new PumpReservoirCache(id, now, null);

        if (reservoir is { } units)
            return new(ReservoirState.Units, units, cache with { Reading = (units, now) });

        if (cache.Reading is { } reading)
        {
            var age = now - reading.Date;
            if (age >= TimeSpan.Zero && age <= MaxReadingAge) return new(ReservoirState.Units, reading.Units, cache);
            if (reading.Date - cache.PumpSince >= PumpSettleTime) return new(ReservoirState.Unknown, null, cache);
            cache = cache with { Reading = null };
        }
        return new(withoutReading, null, cache);
    }

    private static string? IdentifiedPump(string? pumpId)
    {
        var trimmed = pumpId?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed == "Unknown" ? null : trimmed;
    }

    private static bool ReportsVolumeOnlyWhenLow(string? manufacturer, string? model)
    {
        var pump = string.Join(" ", new[] { manufacturer, model }.Where(x => x is not null)).ToLowerInvariant();
        if (pump.Length == 0) return true;
        return pump.Contains("insulet") || pump.Contains("omnipod") || pump.Contains("dash");
    }
}
