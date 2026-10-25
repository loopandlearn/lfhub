using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LFHub.Engine.State;

/// <summary>
/// The profile as LoopFollow holds it after a fetch: ProfileManager's schedules
/// for the default profile, the "default" store's basal list that Basals.swift
/// falls back to, the profile name and the build's expiration date.
/// </summary>
internal sealed partial class NsProfile
{
    public sealed record Entry(int TimeAsSeconds, double Value);

    /// Store[defaultProfile] was present, so ProfileManager loaded the schedules below.
    public bool Loaded { get; private init; }
    public bool Mmol { get; private init; }
    /// The store's zone; null when LoopFollow falls back to TimeZone.current.
    public TimeZoneInfo? Zone { get; private init; }
    public IReadOnlyList<Entry> Isf { get; private init; } = [];
    public IReadOnlyList<Entry> Basal { get; private init; } = [];
    public IReadOnlyList<Entry> CarbRatio { get; private init; } = [];
    public IReadOnlyList<Entry> TargetLow { get; private init; } = [];
    public IReadOnlyList<Entry> TargetHigh { get; private init; } = [];
    /// The "default" (or "Default") store's basal: MainViewController.basalProfile.
    public IReadOnlyList<Entry> DefaultStoreBasal { get; private init; } = [];
    public string DefaultProfile { get; private init; } = "";
    public double? ExpirationDate { get; private init; }

    /// <summary>
    /// Applies a profiles.json response to the profile held before it, the way
    /// Profile.swift and ProfileManager.loadProfile do. Returns `previous` when
    /// LoopFollow would leave everything unchanged.
    /// </summary>
    public static NsProfile? Apply(NsProfile? previous, JsonNode? response)
    {
        if (response is not JsonArray list) return previous;
        JsonObject first;
        Dictionary<string, JsonObject> stores;
        try
        {
            // JSONDecoder decodes the whole array; one bad record fails the fetch.
            foreach (var item in list) Validate(item);
            if (list.Count == 0) return previous;
            first = (JsonObject)list[0]!;
            stores = ((JsonObject)first["store"]!).ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value!);
        }
        catch (Swift.DecodeException)
        {
            return previous;
        }

        if (!stores.TryGetValue("default", out var defaultStore) && !stores.TryGetValue("Default", out defaultStore))
            return previous;

        var name = Swift.Str(first, "defaultProfile")!;
        var basalStore = Entries(defaultStore, "basal");
        if (!stores.TryGetValue(name, out var store))
        {
            // loadProfile returns early; the name and the default store's basal still update.
            var keep = previous ?? new NsProfile();
            return new NsProfile
            {
                Loaded = keep.Loaded,
                Mmol = keep.Mmol,
                Zone = keep.Zone,
                Isf = keep.Isf,
                Basal = keep.Basal,
                CarbRatio = keep.CarbRatio,
                TargetLow = keep.TargetLow,
                TargetHigh = keep.TargetHigh,
                ExpirationDate = keep.ExpirationDate,
                DefaultProfile = name,
                DefaultStoreBasal = basalStore,
            };
        }

        return new NsProfile
        {
            Loaded = true,
            Mmol = !string.Equals(Swift.Str(store, "units"), "mg/dl", StringComparison.OrdinalIgnoreCase),
            Zone = ResolveZone(Swift.Str(store, "timezone")!),
            Isf = Entries(store, "sens"),
            Basal = Entries(store, "basal"),
            CarbRatio = Entries(store, "carbratio"),
            TargetLow = Entries(store, "target_low"),
            TargetHigh = Entries(store, "target_high"),
            DefaultStoreBasal = basalStore,
            DefaultProfile = name,
            ExpirationDate = Swift.ParseDate(Swift.Str(first, "expirationDate")),
        };
    }

    /// ISF in mg/dL/U at `at`.
    public double? IsfMgdl(DateTime at, TimeZoneInfo fallback) => ToMgdl(Lookup(Isf, at, fallback));

    public double? CarbRatioAt(DateTime at, TimeZoneInfo fallback) => Lookup(CarbRatio, at, fallback);

    public double? TargetLowMgdl(DateTime at, TimeZoneInfo fallback) => ToMgdl(Lookup(TargetLow, at, fallback));

    public double? TargetHighMgdl(DateTime at, TimeZoneInfo fallback) => ToMgdl(Lookup(TargetHigh, at, fallback));

    public double? BasalAt(DateTime at, TimeZoneInfo fallback) => Lookup(Basal, at, fallback);

    private double? ToMgdl(double? v) => v is { } x ? (Mmol ? x * Swift.MgdlPerMmol : x) : null;

    /// ProfileManager.value(from:at:): the last entry starting at or before the clock time in the profile's zone.
    private double? Lookup(IReadOnlyList<Entry> schedule, DateTime at, TimeZoneInfo fallback)
    {
        if (schedule.Count == 0) return null;
        var local = TimeZoneInfo.ConvertTimeFromUtc(at, Zone ?? fallback);
        var seconds = local.Hour * 3600 + local.Minute * 60 + local.Second;
        double? last = null;
        foreach (var e in schedule)
        {
            if (seconds >= e.TimeAsSeconds) last = e.Value;
            else break;
        }
        return last;
    }

    /// ProfileManager.getTimeZone, with null for its TimeZone.current fallback.
    private static TimeZoneInfo? ResolveZone(string id)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id.Replace("ETC/", "Etc/"), out tz)) return tz;
        var upper = id.ToUpperInvariant();
        if (upper.Contains("GMT"))
        {
            var parts = upper.Split("GMT");
            if (parts.Length > 1 && SwiftIntPattern().IsMatch(parts[1]) && int.TryParse(parts[1], out var hours))
            {
                var offset = TimeSpan.FromHours(-hours);
                if (offset.Duration() <= TimeSpan.FromHours(18))
                    return TimeZoneInfo.CreateCustomTimeZone($"GMT{-hours:+0;-0;+0}", offset, null, null);
            }
        }
        return null;
    }

    [GeneratedRegex(@"^[+-]?\d+$")]
    private static partial Regex SwiftIntPattern();

    private static List<Entry> Entries(JsonObject store, string key)
    {
        if (store[key] is not JsonArray arr) return [];
        return arr.Select(e => new Entry((int)Swift.Dbl(((JsonObject)e!)["timeAsSeconds"])!.Value, Swift.Dbl(e!["value"])!.Value)).ToList();
    }

    /// The decode NSProfile.swift's Decodable types perform; throws where JSONDecoder would.
    private static void Validate(JsonNode? item)
    {
        var p = Swift.DecObj(item, "profile");
        var stores = Swift.DecObj(p["store"], "store");
        foreach (var (_, node) in stores)
        {
            var s = Swift.DecObj(node, "store entry");
            Schedule(s["basal"], "basal", required: true);
            Schedule(s["sens"], "sens", required: true);
            Schedule(s["carbratio"], "carbratio", required: true);
            Schedule(s["target_high"], "target_high", required: false);
            Schedule(s["target_low"], "target_low", required: false);
            Swift.DecString(s["timezone"], "timezone");
            Swift.DecString(s["units"], "units");
        }
        Swift.DecString(p["defaultProfile"], "defaultProfile");
        Swift.DecString(p["units"], "units");
        foreach (var key in (ReadOnlySpan<string>)["bundleIdentifier", "deviceToken", "teamID", "expirationDate", "startDate", "created_at"])
            Swift.Opt(p, key, Swift.DecString);
        Swift.OptV(p, "isAPNSProduction", Swift.DecBool);
        Swift.Opt(p, "remoteCommands", (n, w) => DecArray(n, w, x => Swift.DecString(x, w)));
        Swift.Opt(p, "overridePresets", (n, w) => DecArray(n, w, x =>
        {
            var o = Swift.DecObj(x, w);
            Swift.DecString(o["name"], "name");
            Swift.OptV(o, "duration", Swift.DecDouble);
            Swift.OptV(o, "percentage", Swift.DecDouble);
            Swift.OptV(o, "target", Swift.DecDouble);
            return 0;
        }));
        Swift.Opt(p, "loopSettings", (n, w) =>
        {
            var ls = Swift.DecObj(n, w);
            Swift.Opt(ls, "deviceToken", Swift.DecString);
            Swift.Opt(ls, "bundleIdentifier", Swift.DecString);
            Swift.Opt(ls, "dosingStrategy", Swift.DecString);
            foreach (var key in (ReadOnlySpan<string>)["minimumBGGuard", "maximumBolus", "maximumBasalRatePerHour"])
                Swift.OptV(ls, key, Swift.DecDouble);
            Swift.OptV(ls, "dosingEnabled", Swift.DecBool);
            Swift.Opt(ls, "scheduleOverride", (so, w2) =>
            {
                var o = Swift.DecObj(so, w2);
                Swift.Opt(o, "symbol", Swift.DecString);
                Swift.Opt(o, "name", Swift.DecString);
                Swift.OptV(o, "duration", Swift.DecInt);
                Swift.OptV(o, "insulinNeedsScaleFactor", Swift.DecDouble);
                return o;
            });
            Swift.Opt(ls, "overridePresets", (op, w2) => DecArray(op, w2, x =>
            {
                var o = Swift.DecObj(x, w2);
                Swift.DecString(o["name"], "name");
                Swift.OptV(o, "duration", Swift.DecInt);
                Swift.Opt(o, "targetRange", (tr, w3) => DecArray(tr, w3, y => Swift.DecDouble(y, w3)));
                Swift.OptV(o, "insulinNeedsScaleFactor", Swift.DecDouble);
                Swift.Opt(o, "symbol", Swift.DecString);
                return 0;
            }));
            return ls;
        });
    }

    private static void Schedule(JsonNode? n, string what, bool required)
    {
        if (n is null && !required) return;
        if (n is not JsonArray arr) throw new Swift.DecodeException(what);
        foreach (var e in arr)
        {
            var o = Swift.DecObj(e, what);
            Swift.DecDouble(o["value"], what);
            Swift.DecString(o["time"], what);
            Swift.DecDouble(o["timeAsSeconds"], what);
        }
    }

    private static List<T> DecArray<T>(JsonNode? n, string what, Func<JsonNode?, T> each) =>
        n is JsonArray arr ? arr.Select(each).ToList() : throw new Swift.DecodeException(what);
}
