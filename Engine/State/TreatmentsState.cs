using System.Text.Json.Nodes;

namespace LFHub.Engine.State;

/// <summary>
/// The treatments LoopFollow last fetched and the insert times it keeps across
/// fetches. The fetch covers the widest window any device asks for; each device's
/// values are derived from its own window, as of the fetch, by <see cref="View"/>.
/// </summary>
internal sealed class TreatmentsState
{
    /// The last processed response, in Nightscout's order (newest first).
    private List<JsonObject>? _entries;
    public DateTime ProcessedAt { get; private set; }
    public bool Loaded => _entries is not null;

    /// MainViewController.currentSage/currentCage/currentIage: the created_at of the
    /// last insert seen, kept when a later window has none.
    private string? _currentSage;
    private string? _currentCage;
    private string? _currentIageWindow;
    private string? _currentIageFetched;

    /// Storage.sageInsertTime/cageInsertTime/iageInsertTime, Unix seconds, 0 when unset.
    public double SageInsertTime { get; private set; }
    public double CageInsertTime { get; private set; }
    private double _iageFromWindow;
    private double _iageFromFetch;

    /// Storage.lastBasal per formatting context, kept while a device's window has no temp basal.
    private readonly Dictionary<(int Days, string Zone, string Separator), string> _lastBasal = new();

    public double IageInsertTime(ClientSettings s) =>
        _currentIageWindow is not null ? _iageFromWindow : s.IageVisible ? _iageFromFetch : 0;

    public enum AgeFetch { Sage, Cage, Iage }

    /// <summary>
    /// Takes a treatments response (Treatments.swift WebLoadNSTreatments) and updates
    /// the insert times from it. Returns the dedicated lookups LoopFollow would make
    /// next, or null when the response is not a list of objects.
    /// </summary>
    public List<AgeFetch>? Apply(JsonNode? response, DateTime now, bool iageLookupWanted)
    {
        if (response is not JsonArray arr || arr.Any(x => x is not JsonObject)) return null;
        _entries = arr.Select(x => (JsonObject)x!).ToList();
        ProcessedAt = now;

        var sage = new List<string>();
        var cage = new List<string>();
        var iage = new List<string>();
        foreach (var e in Deduplicated(_entries))
        {
            var createdAt = Swift.Str(e, "created_at");
            switch (Swift.Str(e, "eventType"))
            {
                case "Pump Site Change" or "Site Change" when createdAt is not null: cage.Add(createdAt); break;
                case "Sensor Start" when createdAt is not null: sage.Add(createdAt); break;
                case "Insulin Change" when createdAt is not null: iage.Add(createdAt); break;
            }
        }

        var fetches = new List<AgeFetch>();
        if (sage.Count > 0) SetSage(sage[0]);
        else if (_currentSage is not null) SetSage(_currentSage);
        else fetches.Add(AgeFetch.Sage);

        if (iage.Count > 0)
        {
            _currentIageWindow = iage[0];
            if (Swift.IsoPrefix(iage[0]) is { } t) _iageFromWindow = t;
        }
        else if (_currentIageWindow is null && _currentIageFetched is null && iageLookupWanted)
        {
            fetches.Add(AgeFetch.Iage);
        }

        if (cage.Count > 0) SetCage(cage[0]);
        else if (_currentCage is not null) SetCage(_currentCage);
        else fetches.Add(AgeFetch.Cage);
        return fetches;
    }

    /// Applies a dedicated Sensor Start / Site Change / Insulin Change lookup ([{created_at}], count=1).
    public void ApplyAgeFetch(AgeFetch kind, JsonNode? response)
    {
        if (response is not JsonArray arr) return;
        var createdAts = new List<string>();
        foreach (var x in arr)
        {
            if (x is not JsonObject o || Swift.Str(o, "created_at") is not { } c) return;
            createdAts.Add(c);
        }
        if (createdAts.Count == 0) return;
        switch (kind)
        {
            case AgeFetch.Sage: SetSage(createdAts[0]); break;
            case AgeFetch.Cage: SetCage(createdAts[0]); break;
            case AgeFetch.Iage:
                _currentIageFetched = createdAts[0];
                if (Swift.IsoPrefix(createdAts[0]) is { } t) _iageFromFetch = t;
                break;
        }
    }

    private void SetSage(string createdAt)
    {
        _currentSage = createdAt;
        if (Swift.IsoPrefix(createdAt) is { } t) SageInsertTime = t;
    }

    private void SetCage(string createdAt)
    {
        _currentCage = createdAt;
        if (Swift.IsoPrefix(createdAt) is { } t) CageInsertTime = t;
    }

    public sealed record OverrideBand(double Date, double EndDate, double? ScheduledEndDate);

    public sealed record TempTargetBand(double Date, double EndDate, double Duration)
    {
        /// Nil for an indefinite target, which Trio stores with a duration of about 30 days.
        public double? ScheduledEndDate => Duration >= 7 * 24 * 3600 ? null : EndDate;
    }

    public sealed record View(
        string Basal,
        double CarbsToday,
        string? OverrideName,
        double? OverrideEndAt,
        double? TempTargetMgdl,
        double? TempTargetEndAt,
        IReadOnlyList<OverrideBand> Overrides,
        IReadOnlyList<TempTargetBand> TempTargets,
        IReadOnlyList<(double Units, double Date)> Boluses,
        IReadOnlyList<(double Grams, double Date)> Carbs,
        IReadOnlyList<(string Name, double Date)> CgmStates);

    /// <summary>
    /// The treatments-derived values a device with these settings holds after the
    /// last fetch: Treatments.swift's split and each processor, run on the device's
    /// own query window at the time of the fetch.
    /// </summary>
    public View? ViewFor(ClientSettings s, NsProfile? profile)
    {
        if (_entries is null || !s.DownloadTreatments) return null;
        var now = ProcessedAt;
        var nowSec = Swift.Unix(now);
        var nowWhole = Math.Floor(nowSec);

        var from = Swift.NsQueryTime(Swift.AddLocalDays(now, -s.DownloadDays, s.TimeZone));
        var to = Swift.NsQueryTime(now.AddHours(6));
        var window = _entries.Where(e => Swift.Str(e, "created_at") is { } c
            && string.CompareOrdinal(c, from) >= 0 && string.CompareOrdinal(c, to) <= 0);

        var tempBasal = new List<JsonObject>();
        var bolus = new List<JsonObject>();
        var carbs = new List<JsonObject>();
        var overrides = new List<JsonObject>();
        var tempTargets = new List<JsonObject>();
        var notes = new List<JsonObject>();
        foreach (var e in Deduplicated(window.ToList()))
        {
            switch (Swift.Str(e, "eventType"))
            {
                case "Temp Basal": tempBasal.Add(e); break;
                case "Correction Bolus" or "Bolus" or "External Insulin":
                    if (Swift.Bool(e, "automatic") != true) bolus.Add(e);
                    break;
                case "Meal Bolus": carbs.Add(e); bolus.Add(e); break;
                case "Carb Correction": carbs.Add(e); break;
                case "Temporary Override" or "Exercise": overrides.Add(e); break;
                case "Temporary Target": tempTargets.Add(e); break;
                case "Note": notes.Add(e); break;
            }
        }

        var key = (s.DownloadDays, s.TimeZone.Id, s.DecimalSeparator);
        if (tempBasal.Count > 0) _lastBasal[key] = Basal(tempBasal, now, nowWhole, s, profile);
        var basal = _lastBasal.GetValueOrDefault(key, "");

        var graphHorizon = nowSec - 24 * 3600.0 * s.DownloadDays;
        var (overrideBands, overrideName, overrideEnd) = Overrides(overrides, nowSec, graphHorizon, s.PredictionToLoad);
        var (ttBands, ttValue, ttEnd) = TempTargets(tempTargets, nowSec, graphHorizon);

        return new View(
            basal,
            CarbsToday(carbs, now, s.TimeZone),
            overrideName,
            overrideEnd,
            ttValue,
            ttEnd,
            overrideBands,
            ttBands,
            Dots(bolus, "insulin", nowWhole + 3600),
            Dots(carbs, "carbs", nowWhole + 3600 * s.PredictionToLoad),
            CgmStates(notes, nowWhole));
    }

    /// Basals.swift processNSBasals: the last temp basal's rate, or the scheduled rate
    /// once it has ended, prefixed "profile → " when the profile's rate differs.
    private static string Basal(List<JsonObject> entries, DateTime now, double nowWhole, ClientSettings s, NsProfile? profile)
    {
        string Fmt(double v) => Swift.FormatDecimal(v, 2, 0, s.DecimalSeparator);
        static double? Start(JsonObject? e) => e is null ? null : Swift.ParseDate(Swift.Str(e, "timestamp") ?? Swift.Str(e, "created_at"));

        var temp = Enumerable.Reverse(entries).ToList();
        var latest = "";
        var lastEndDot = 0.0;
        for (var i = 0; i < temp.Count; i++)
        {
            if (Start(temp[i]) is not { } start) continue;
            if (Swift.Dbl(temp[i], "absolute") is not { } rate) continue;
            var duration = Swift.Dbl(temp[i], "duration") ?? 0.0;

            var lastDot = start + duration * 60;
            if (i == temp.Count - 1 && duration == 0.0) lastDot = start + 30 * 60;
            latest = Fmt(rate);

            if (i < temp.Count - 1 && Start(temp[i + 1]) is { } next && next < start + duration * 60)
                lastDot = next;
            lastEndDot = lastDot;
        }

        if (lastEndDot <= nowWhole)
        {
            var scheduled = 0.0;
            var midnight = Swift.Unix(Swift.LocalDay(now, s.TimeZone).Start);
            foreach (var b in profile?.DefaultStoreBasal ?? [])
                if (lastEndDot >= b.TimeAsSeconds + midnight)
                    scheduled = b.Value;
            latest = Fmt(scheduled);
        }

        if (profile?.BasalAt(now, s.TimeZone) is { } profileRate && Fmt(profileRate) is var profileBasal && profileBasal != latest)
            latest = $"{profileBasal} → {latest}";
        return latest;
    }

    /// Carbs.swift updateTodaysCarbsFromEntries: carbs dated today in the phone's zone.
    private static double CarbsToday(List<JsonObject> entries, DateTime now, TimeZoneInfo zone)
    {
        var (dayStart, dayEnd) = Swift.LocalDay(now, zone);
        var total = 0.0;
        foreach (var e in entries)
        {
            var raw = Swift.Str(e, "timestamp") ?? Swift.Str(e, "created_at");
            if (Swift.ParseDate(raw) is not { } date) continue;
            var at = Swift.FromUnix(date);
            if (at >= dayStart && at < dayEnd && Swift.Dbl(e, "carbs") is { } grams) total += grams;
        }
        return total;
    }

    /// Overrides.swift processNSOverrides.
    private static (List<OverrideBand>, string?, double?) Overrides(List<JsonObject> entries, double now,
        double graphHorizon, double predictionToLoad)
    {
        var bands = new List<OverrideBand>();
        if (entries.Count == 0) return (bands, null, null);

        static double? Start(JsonObject e) => Swift.ParseDate(Swift.Str(e, "timestamp") ?? Swift.Str(e, "created_at"));
        var sorted = entries.ToList();
        StableSort(sorted, (l, r) => Start(l) is { } ld && Start(r) is { } rd && ld < rd);

        var maxEndDate = now + Math.Max(predictionToLoad, 0.25) * 3600;
        string? activeNote = null;
        double? activeEnd = null;
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            if (Start(e) is not { } startDate) continue;
            var start = Math.Max(startDate, graphHorizon);
            double? nextStart = i + 1 < sorted.Count ? Start(sorted[i + 1]) : null;

            var durationSeconds = (Swift.Dbl(e, "duration") ?? 5) * 60;
            var indefinite = Swift.Str(e, "durationType") == "indefinite" || durationSeconds >= 7 * 24 * 3600;
            var end = indefinite ? maxEndDate : start + durationSeconds;
            double? trueEnd = indefinite ? null : startDate + durationSeconds;
            if (nextStart is { } ns)
            {
                end = Math.Min(end, ns - 60);
                trueEnd = trueEnd is { } te ? Math.Min(te, ns - 60) : null;
            }
            end = Math.Min(end, maxEndDate);
            if (end - start < 300) continue;

            bands.Add(new OverrideBand(start, end, trueEnd));
            if (now >= start && now < end)
            {
                activeNote = Swift.Str(e, "notes") ?? Swift.Str(e, "reason");
                activeEnd = trueEnd;
            }
        }
        return (bands, activeNote, activeEnd);
    }

    /// TemporaryTarget.swift processNSTemporaryTarget.
    private static (List<TempTargetBand>, double?, double?) TempTargets(List<JsonObject> entries, double now, double graphHorizon)
    {
        var bands = new List<TempTargetBand>();
        if (entries.Count == 0) return (bands, null, null);

        long? active = null;
        double? activeEnd = null;
        foreach (var e in Enumerable.Reverse(entries))
        {
            if (Swift.ParseDate(Swift.Str(e, "timestamp") ?? Swift.Str(e, "created_at")) is not { } parsed) continue;
            var date = Math.Max(parsed, graphHorizon);
            var duration = (Swift.Dbl(e, "duration") ?? 5.0) * 60;

            if (duration == 0)
            {
                var index = bands.FindLastIndex(b => b.EndDate > date);
                if (index >= 0)
                {
                    bands[index] = bands[index] with { EndDate = date };
                    active = null;
                    activeEnd = null;
                }
                continue;
            }
            if (duration < 300) continue;
            if (Swift.Str(e, "enteredBy") is null) continue;
            if ((Swift.Dbl(e, "targetBottom") ?? Swift.Dbl(e, "targetTop")) is not { } target) continue;

            var band = new TempTargetBand(date, date + duration, duration);
            bands.Add(band);
            if (now < band.EndDate)
            {
                active = (long)target;
                activeEnd = band.ScheduledEndDate;
            }
        }
        return (bands, active is { } a ? a : null, activeEnd);
    }

    /// Bolus.swift / Carbs.swift dots, oldest first.
    private static List<(double, double)> Dots(List<JsonObject> entries, string key, double before)
    {
        var dots = new List<(double, double)>();
        foreach (var e in Enumerable.Reverse(entries))
        {
            if (Swift.TreatmentDate(e) is not { } date || Swift.Dbl(e, key) is not { } value) continue;
            if (date < before) dots.Add((value, date));
        }
        return dots;
    }

    /// Notes.swift: the CGM states among the notes.
    private static List<(string, double)> CgmStates(List<JsonObject> entries, double nowWhole)
    {
        var states = new List<(string, double)>();
        foreach (var e in Enumerable.Reverse(entries))
        {
            if (Swift.TreatmentDate(e) is not { } date) continue;
            if (Swift.Str(e, "notes") is not { } note || date >= nowWhole + 3600) continue;
            if (CgmSensorState.Parse(note) is { } name) states.Add((name, date));
        }
        return states;
    }

    /// Treatments.swift deduplicatedTreatmentEntries: one entry per (id, eventType, time).
    private static List<JsonObject> Deduplicated(List<JsonObject> entries)
    {
        var seen = new HashSet<(string, string?, double?)>();
        var result = new List<JsonObject>();
        foreach (var e in entries)
        {
            if (Swift.Str(e, "id") is not { Length: > 0 } id)
            {
                result.Add(e);
                continue;
            }
            var eventType = Swift.Str(e, "eventType");
            var raw = eventType is "Pump Site Change" or "Site Change" or "Sensor Start" or "Insulin Change"
                ? Swift.Str(e, "created_at")
                : Swift.Str(e, "timestamp") ?? Swift.Str(e, "created_at");
            if (seen.Add((id, eventType, Swift.ParseDate(raw)))) result.Add(e);
        }
        return result;
    }

    /// Stable insertion sort on a "less than"; Swift's sort keeps equal elements in order too.
    private static void StableSort<T>(List<T> list, Func<T, T, bool> less)
    {
        for (var i = 1; i < list.Count; i++)
        {
            var x = list[i];
            var j = i - 1;
            while (j >= 0 && less(x, list[j]))
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = x;
        }
    }
}
