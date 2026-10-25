using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Nightscout;

namespace LFHub.Engine.State;

/// <summary>
/// One patient's state from Dexcom Share, Nightscout, or both, fetched on LoopFollow's
/// per-endpoint cadence and kept the way LoopFollow keeps it, so that <see cref="BuildSnapshot"/>
/// and <see cref="BuildAlarmData"/> give what the app itself would build for a
/// device's settings. Fetches cover what every following device needs together
/// (<see cref="UpdateFetchSettings"/>); settings are applied at build time.
/// </summary>
/// <remarks>
/// BG comes from Dexcom when there are Dexcom credentials, otherwise from Nightscout
/// (BGTask.swift); with both, Nightscout fills in when Dexcom fails, is empty or is
/// stale, and supplies the history Dexcom's 24 hours do not cover (BGData.swift
/// webLoadDexShare). Devicestatus, treatments, profile and database size come from
/// Nightscout only, so without it they stay empty.
///
/// Cadences are LoopFollow's with its socket authenticated, which the hub's always is
/// when there is a Nightscout: BG and devicestatus polls stretched to 3× (at least a
/// minute), treatments every 10 minutes, the profile every 30, the database size every
/// 6 hours. Without a Nightscout there is no socket and the BG poll is not stretched.
/// A socket dataUpdate makes the endpoints it names due at once (<see cref="NotifyDataUpdate"/>).
/// </remarks>
public sealed class PatientState : IDisposable
{
    private enum Endpoint { Bg, Profile, DeviceStatus, Treatments, DbSize }

    private static readonly Endpoint[] FetchOrder =
        [Endpoint.Bg, Endpoint.Profile, Endpoint.DeviceStatus, Endpoint.Treatments, Endpoint.DbSize];

    private const int MaxExpectedUploaders = 4;
    private const int AlarmBgReadings = 24;
    private const int AlarmForecastPointCap = 13;
    private static readonly TimeSpan NotLoopingAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest wait between BG or devicestatus checks. The missed-reading and
    /// not-looping alarms stay silent when the last check is older than six minutes;
    /// this, plus the 30 s tick, stays below that, and so caps LoopFollow's socket
    /// stretch, which reaches fifteen.
    /// </summary>
    private static readonly TimeSpan MaxCheckInterval = TimeSpan.FromSeconds(270);

    private readonly NightscoutRest? _rest;
    private readonly DexcomShare? _dexcom;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<Endpoint, DateTime> _due = new();

    private FetchNeeds _needs = FetchNeeds.From([ClientSettings.Default]);

    // BG: the last non-empty response (bgData's source) and the last one with two
    // readings to show, since LoopFollow's display keeps its values otherwise.
    private Bg? _bg;
    private Bg? _bgShown;

    private NsProfile? _profile;
    private readonly DeviceStatusState _status = new();
    private bool _statusLoaded;
    private readonly TreatmentsState _treatments = new();
    private double? _dbSizePercentage;

    public PatientState(HttpClient http, string nightscoutBaseUrl, string? token, ILogger log)
        : this(http, nightscoutBaseUrl, token, null, log)
    {
    }

    /// <summary>
    /// Follows Dexcom Share, Nightscout, or both. A blank Nightscout URL means no
    /// Nightscout; Dexcom credentials count only with both username and password,
    /// as in LoopFollow. At least one source is required.
    /// </summary>
    public PatientState(HttpClient http, string? nightscoutBaseUrl, string? nightscoutToken, DexcomCredentials? dexcom, ILogger log)
    {
        _log = log;
        if (!string.IsNullOrWhiteSpace(nightscoutBaseUrl))
            _rest = new NightscoutRest(http, nightscoutBaseUrl, nightscoutToken);
        if (dexcom is { Username.Length: > 0, Password.Length: > 0 })
            _dexcom = new DexcomShare(http, dexcom.Username, dexcom.Password, dexcom.Server, () => UtcNow(), log);
        if (_rest is null && _dexcom is null)
            throw new ArgumentException("A patient needs a Nightscout URL or Dexcom credentials.");
        foreach (var e in Enum.GetValues<Endpoint>()) _due[e] = DateTime.MinValue;
    }

    public void Dispose() => _refresh.Dispose();

    public bool HasNightscout => _rest is not null;

    public bool HasDexcom => _dexcom is not null;

    /// When the next Dexcom login may be attempted, while a rejected login is holding it back.
    public DateTime? DexcomLoginBlockedUntil => _dexcom?.LoginBlockedUntil;

    /// <summary>
    /// The Nightscout socket is connected and authorized. LoopFollow stretches its
    /// polling ×3 only then, since the socket's events trigger fetches.
    /// </summary>
    public bool SocketLive { get; set; }

    /// <summary>
    /// The latest reading from an earlier run of the hub. Until a fetch returns readings it
    /// is the latest reading and the alarm check's only reading, so missed-reading can fire
    /// while the sources are down after a restart.
    /// </summary>
    public (DateTime At, int Mgdl)? LastKnownReading { get; init; }

    /// The clock fetches are processed against; replaceable for tests.
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The settings of every device following this patient; fetches cover all of them.</summary>
    public void UpdateFetchSettings(IEnumerable<ClientSettings> all)
    {
        var list = all.ToList();
        lock (_lock) _needs = FetchNeeds.From(list.Count > 0 ? list : [ClientSettings.Default]);
    }

    /// <summary>
    /// Makes due what LoopFollow's NightscoutSocketDataHandler fetches for a dataUpdate:
    /// everything but the database size for a full update, otherwise the types it carries.
    /// </summary>
    public void NotifyDataUpdate(JsonElement data)
    {
        var now = UtcNow();
        bool Present(string key) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out _);
        var delta = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.True;
        lock (_lock)
        {
            if (!delta || Present("sgvs") || Present("mbgs")) _due[Endpoint.Bg] = now;
            if (!delta || Present("devicestatus")) _due[Endpoint.DeviceStatus] = now;
            if (!delta || Present("treatments")) _due[Endpoint.Treatments] = now;
            if (!delta || Present("profiles")) _due[Endpoint.Profile] = now;
        }
    }

    /// <summary>Fetches whatever is due on LoopFollow's per-endpoint cadence; `force` fetches all.</summary>
    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            // Profile before devicestatus: the devicestatus values read the profile in effect.
            foreach (var endpoint in FetchOrder)
            {
                if (endpoint != Endpoint.Bg && _rest is null) continue;
                bool due;
                lock (_lock) due = force || UtcNow() >= _due[endpoint];
                if (!due) continue;
                if (endpoint == Endpoint.Bg) LastBgCheck = UtcNow();
                if (endpoint == Endpoint.DeviceStatus) LastDeviceStatusCheck = UtcNow();
                await (endpoint switch
                {
                    Endpoint.Bg => FetchBgAsync(ct),
                    Endpoint.Profile => FetchProfileAsync(ct),
                    Endpoint.DeviceStatus => FetchDeviceStatusAsync(ct),
                    Endpoint.Treatments => FetchTreatmentsAsync(ct),
                    _ => FetchDbSizeAsync(ct),
                });
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// When a BG fetch last ran, successful or not (LoopFollow's lastBGChecked).
    public DateTime? LastBgCheck { get; private set; }

    /// When a devicestatus fetch last ran, successful or not (LoopFollow's lastLoopingChecked).
    public DateTime? LastDeviceStatusCheck { get; private set; }

    /// The latest reading's Nightscout direction, e.g. "FortyFiveUp".
    public string? LatestDirection
    {
        get
        {
            lock (_lock)
            {
                var readings = _bg?.Window(_needs.DownloadDays);
                return readings is { Count: > 0 } ? readings[^1].Direction : null;
            }
        }
    }

    // MARK: Bundle

    private readonly Dictionary<string, (DateTime FetchedAt, JsonNode? Data, string Json)> _bundle = new();

    /// Increases whenever a bundle part changes.
    public long BundleVersion { get; private set; }

    /// Raised after a bundle part changed, outside the state's lock.
    public event Action? BundleChanged;

    /// <summary>
    /// The responses LoopFollow would otherwise fetch itself, as last fetched:
    /// entries (the BG LoopFollow ends up with, Dexcom and Nightscout merged, in
    /// entries.json shape), profile, devicestatus, treatments, the sage/cage/iage
    /// lookups and dbsize. The app feeds them to its own parsers.
    /// </summary>
    public JsonObject Bundle()
    {
        lock (_lock)
        {
            var parts = new JsonObject();
            foreach (var (name, part) in _bundle)
                parts[name] = new JsonObject { ["fetchedAt"] = part.FetchedAt, ["data"] = part.Data?.DeepClone() };
            return new JsonObject { ["version"] = BundleVersion, ["generatedAt"] = UtcNow(), ["parts"] = parts };
        }
    }

    private void KeepBundlePart(string name, JsonNode? data)
    {
        var json = data?.ToJsonString() ?? "null";
        bool changed;
        lock (_lock)
        {
            changed = !_bundle.TryGetValue(name, out var old) || old.Json != json;
            _bundle[name] = (UtcNow(), data?.DeepClone(), json);
            if (changed) BundleVersion++;
        }
        if (changed) BundleChanged?.Invoke();
    }

    /// The latest reading: the newest of the last fetch with readings, or else <see cref="LastKnownReading"/>.
    public (DateTime At, int Mgdl)? LatestReading
    {
        get
        {
            lock (_lock)
            {
                var readings = _bg?.Window(_needs.DownloadDays);
                if (readings is not { Count: > 0 }) return _bg is null ? LastKnownReading : null;
                var latest = readings[^1];
                return (Swift.FromUnix(latest.Date), latest.Sgv);
            }
        }
    }

    // MARK: Fetches

    /// <param name="part">The bundle part the response is kept as, or null.</param>
    private async Task<JsonNode?> GetAsync(string what, string endpoint, IEnumerable<KeyValuePair<string, string>> query,
        CancellationToken ct, string? part = null)
    {
        try
        {
            var response = await _rest!.GetAsync(endpoint, query, ct);
            if (part is not null) KeepBundlePart(part, response);
            return response;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Nightscout {What} fetch failed: {Error}", what, ex.GetType().Name);
            throw new FetchFailed();
        }
    }

    private sealed class FetchFailed : Exception;

    /// BGTask.swift bgTaskAction: Dexcom when configured, otherwise Nightscout.
    private async Task FetchBgAsync(CancellationToken ct)
    {
        var start = UtcNow();
        int days;
        int bgUpdateDelay;
        TimeZoneInfo zone;
        lock (_lock)
        {
            _due[Endpoint.Bg] = start.AddSeconds(60);
            days = _needs.BgFetchDays;
            bgUpdateDelay = _needs.BgUpdateDelay;
            zone = _needs.Zone;
        }

        if (_dexcom is null)
            await FetchNsBgAsync([], days, bgUpdateDelay, zone, ct);
        else
            await FetchDexcomBgAsync(_dexcom, days, bgUpdateDelay, zone, ct);
    }

    /// <summary>
    /// BGData.swift webLoadDexShare: falls back to Nightscout when Dexcom fails, returns nothing,
    /// or its newest reading is more than 330 s old; Dexcom merged over Nightscout's older
    /// readings when Dexcom does not reach back over the whole window.
    /// </summary>
    private async Task FetchDexcomBgAsync(DexcomShare dexcom, int days, int bgUpdateDelay, TimeZoneInfo zone, CancellationToken ct)
    {
        var graphHours = 24 * days;
        var result = await dexcom.FetchAsync(graphHours * 12, ct);
        if (result.Status != DexcomStatus.Ok)
        {
            if (result.Status == DexcomStatus.LoginBackoff)
                _log.LogDebug("Dexcom login backing off: {Detail}", result.Detail);
            else
                _log.LogWarning("Dexcom fetch failed: {Status} ({Detail})", result.Status, result.Detail);
            await FetchNsBgAsync([], days, bgUpdateDelay, zone, ct);
            return;
        }
        if (result.Readings is not { Count: > 0 } data)
        {
            _log.LogDebug("Dexcom returned no readings");
            await FetchNsBgAsync([], days, bgUpdateDelay, zone, ct);
            return;
        }

        var now = Swift.Unix(UtcNow());
        if (data[0].Date + 330 < now && _rest is not null)
        {
            _log.LogDebug("Dexcom data is old, loading BG from Nightscout");
            await FetchNsBgAsync([], days, bgUpdateDelay, zone, ct);
            return;
        }

        var dexReadings = data.Select(r => new Reading(r.Sgv, r.Date, r.Direction)).ToList();
        var current = NotFuture(dexReadings, now);
        LogFuture(dexReadings.Count, current, dexReadings);
        var deduped = Dedup(current);
        var dexCutoff = Swift.Unix(UtcNow()) - graphHours * 3600.0;
        var coversFull = deduped.Count > 0 && deduped[^1].Date <= dexCutoff;
        if (!coversFull && _rest is not null)
            await FetchNsBgAsync(deduped, days, bgUpdateDelay, zone, ct);
        else
            ProcessBg(deduped, days, bgUpdateDelay);
    }

    /// BGData.swift webLoadNSBGData: Nightscout's readings, under `dexData` when there is any.
    private async Task FetchNsBgAsync(List<Reading> dexData, int days, int bgUpdateDelay, TimeZoneInfo zone, CancellationToken ct)
    {
        if (_rest is null) return;

        var from = Swift.AddLocalDays(UtcNow(), -days, zone);
        var query = new Dictionary<string, string>
        {
            ["count"] = (days * MaxExpectedUploaders * 24 * 60 / 5).ToString(CultureInfo.InvariantCulture),
            ["find[date][$gte]"] = ((long)(Swift.Unix(from) * 1000)).ToString(CultureInfo.InvariantCulture),
            ["find[type][$ne]"] = "cal",
        };
        List<Reading>? entries;
        try
        {
            entries = DecodeEntries(await GetAsync("sgv", "/api/v1/entries.json", query, ct));
        }
        catch (FetchFailed)
        {
            entries = null;
        }

        if (entries is null)
        {
            lock (_lock) _due[Endpoint.Bg] = UtcNow().AddSeconds(10);
            if (dexData.Count > 0) ProcessBg(dexData, days, bgUpdateDelay);
            return;
        }

        var nowUnix = Swift.Unix(UtcNow());
        var currentEntries = NotFuture(entries, nowUnix);
        LogFuture(entries.Count, currentEntries, entries);
        var nsData = Dedup(currentEntries);
        if (dexData.Count > 0)
        {
            var oldestDexDate = dexData[^1].Date;
            nsData = [.. dexData, .. nsData.SkipWhile(r => r.Date >= oldestDexDate)];
        }
        ProcessBg(nsData, days, bgUpdateDelay);
    }

    /// BGData.swift ProcessDexBGData: keeps the readings and schedules the next poll by the newest one's age.
    private void ProcessBg(List<Reading> data, int days, int bgUpdateDelay)
    {
        if (data.Count == 0) return;
        var now = UtcNow();
        var bg = new Bg(data, now);
        KeepBundlePart("entries", new JsonArray(data.Select(r => (JsonNode)new JsonObject
        {
            ["sgv"] = r.Sgv,
            ["date"] = (long)(r.Date * 1000),
            ["direction"] = r.Direction,
            ["type"] = "sgv",
        }).ToArray()));
        lock (_lock)
        {
            _bg = bg;
            if (bg.Window(days).Count >= 2) _bgShown = bg;

            var secondsAgo = Swift.Unix(now) - data[0].Date;
            double delay = secondsAgo switch
            {
                >= 20 * 60 => 5 * 60,
                >= 10 * 60 => 60,
                >= 7 * 60 => 30,
                >= 5 * 60 => 5,
                _ => 300 - secondsAgo + bgUpdateDelay,
            };
            if (_rest is not null && SocketLive) delay = Math.Max(delay * 3, 60);
            _due[Endpoint.Bg] = now.AddSeconds(Math.Min(delay, MaxCheckInterval.TotalSeconds));
        }
    }

    /// Profile.swift webLoadNSProfile.
    private async Task FetchProfileAsync(CancellationToken ct)
    {
        var start = UtcNow();
        lock (_lock) _due[Endpoint.Profile] = start.AddMinutes(30);
        var query = new Dictionary<string, string>
        {
            ["count"] = "1",
            ["find[startDate][$lte]"] = start.AddSeconds(60).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        try
        {
            var response = await GetAsync("profile", "/api/v1/profiles.json", query, ct, "profile");
            lock (_lock) _profile = NsProfile.Apply(_profile, response);
        }
        catch (FetchFailed)
        {
        }
    }

    /// DeviceStatus.swift webLoadNSDeviceStatus.
    private async Task FetchDeviceStatusAsync(CancellationToken ct)
    {
        JsonNode? response;
        try
        {
            response = await GetAsync("devicestatus", "/api/v1/devicestatus.json",
                new Dictionary<string, string> { ["count"] = "1" }, ct, "devicestatus");
        }
        catch (FetchFailed)
        {
            lock (_lock) _due[Endpoint.DeviceStatus] = UtcNow().AddSeconds(10);
            return;
        }

        var now = UtcNow();
        lock (_lock)
        {
            switch (_status.Apply(response, now, _profile, _needs.Zone))
            {
                case false:
                    _due[Endpoint.DeviceStatus] = now.AddSeconds(10);
                    break;
                case null:
                    _due[Endpoint.DeviceStatus] = now + MaxCheckInterval;
                    break;
                default:
                    _statusLoaded = true;
                    var next = _status.NextPoll(now, SocketLive);
                    _due[Endpoint.DeviceStatus] = now + (next < MaxCheckInterval ? next : MaxCheckInterval);
                    break;
            }
        }
    }

    /// Treatments.swift WebLoadNSTreatments, then the insert-time lookups
    /// SensorStart.swift, SiteChange.swift and InsulinCartridgeChange.swift make.
    private async Task FetchTreatmentsAsync(CancellationToken ct)
    {
        var start = UtcNow();
        FetchNeeds needs;
        lock (_lock)
        {
            _due[Endpoint.Treatments] = start.AddMinutes(_needs.DownloadTreatments ? 10 : 1);
            needs = _needs;
        }
        if (!needs.DownloadTreatments) return;

        // The widest of the devices' windows; a day boundary moves with each zone's
        // DST, so an hour more covers every device's own query.
        var query = new Dictionary<string, string>
        {
            ["find[created_at][$gte]"] = Swift.NsQueryTime(start.AddDays(-needs.DownloadDays).AddHours(-1)),
            ["find[created_at][$lte]"] = Swift.NsQueryTime(start.AddHours(6)),
            ["count"] = Math.Max(needs.DownloadDays * 100, 5000).ToString(CultureInfo.InvariantCulture),
        };
        List<TreatmentsState.AgeFetch>? lookups;
        try
        {
            var response = await GetAsync("treatments", "/api/v1/treatments.json", query, ct, "treatments");
            lock (_lock) lookups = _treatments.Apply(response, UtcNow(), needs.IageVisible);
        }
        catch (FetchFailed)
        {
            return;
        }

        foreach (var lookup in lookups ?? [])
        {
            var now = UtcNow();
            var (eventType, sinceDays) = lookup switch
            {
                TreatmentsState.AgeFetch.Sage => ("Sensor Start", (int?)60),
                TreatmentsState.AgeFetch.Iage => ("Insulin Change", 60),
                _ => ("Site Change", null),
            };
            var ageQuery = new Dictionary<string, string> { ["find[eventType]"] = eventType };
            if (sinceDays is { } days) ageQuery["find[created_at][$gte]"] = Swift.NsQueryTime(Swift.AddLocalDays(now, -days, needs.Zone));
            ageQuery["find[created_at][$lte]"] = Swift.NsQueryTime(now);
            ageQuery["count"] = "1";
            try
            {
                var response = await GetAsync(eventType, "/api/v1/treatments.json", ageQuery, ct,
                    lookup.ToString().ToLowerInvariant());
                lock (_lock) _treatments.ApplyAgeFetch(lookup, response);
            }
            catch (FetchFailed)
            {
            }
        }
    }

    /// DBSize.swift webLoadNSDBSize.
    private async Task FetchDbSizeAsync(CancellationToken ct)
    {
        lock (_lock) _due[Endpoint.DbSize] = UtcNow().AddHours(6);
        try
        {
            var response = await GetAsync("dbsize", "/api/v2/properties/dbsize", [], ct, "dbsize");
            if (TryDbSize(response, out var percentage)) lock (_lock) _dbSizePercentage = percentage;
        }
        catch (FetchFailed)
        {
        }
    }

    /// The DBSizeProperties decode and updateDBSize: false when the decode fails (no change),
    /// a null percentage when there is no usable reading.
    private static bool TryDbSize(JsonNode? response, out double? percentage)
    {
        percentage = null;
        try
        {
            var root = Swift.DecObj(response, "dbsize response");
            if (Swift.Opt(root, "dbsize", Swift.DecObj) is not { } data) return true;
            var used = Swift.OptV(data, "totalDataSize", Swift.DecDouble);
            var reported = Swift.OptV(data, "dataPercentage", Swift.DecInt);
            if (Swift.Opt(data, "details", Swift.DecObj) is { } details)
            {
                Swift.OptV(details, "maxSize", Swift.DecDouble);
                Swift.OptV(details, "dataSize", Swift.DecDouble);
            }
            if (used is > 0) percentage = reported;
            return true;
        }
        catch (Swift.DecodeException)
        {
            return false;
        }
    }

    // MARK: Build

    /// <summary>
    /// The `snapshot` object of the Live Activity content state, as LoopFollow's
    /// StorageCurrentGlucoseStateProvider → GlucoseSnapshotBuilder → APNSClient
    /// contentStateDictionary produce it for these settings. Null when LoopFollow
    /// has none. `showRenewalOverlay` is false; the caller sets it.
    /// </summary>
    public JsonObject? BuildSnapshot(ClientSettings settings, DateTime nowUtc)
    {
        lock (_lock)
        {
            // viewUpdateNSBG publishes only from two readings and keeps its values otherwise.
            var bg = _bg?.Window(settings.DownloadDays) is { Count: >= 2 } current ? current : _bgShown?.Window(settings.DownloadDays);
            if (bg is not { Count: >= 2 }) return null;
            var latest = bg[^1];
            if (latest.Sgv <= 0) return null;

            var view = _treatments.ViewFor(settings, _profile);
            var s = _status;

            var snapshot = new JsonObject
            {
                ["glucose"] = (double)latest.Sgv,
                ["delta"] = (double)(latest.Sgv - bg[^2].Sgv),
                ["trend"] = Trend.FromDirection(latest.Direction),
                ["updatedAt"] = latest.Date,
                ["unit"] = settings.Mmol ? "mmol" : "mgdl",
                ["isNotLooping"] = s.LastLoopTime is { } loopTime && loopTime > 0
                    && Swift.Unix(nowUtc) - loopTime >= NotLoopingAfter.TotalSeconds,
            };

            if (CgmState(view, settings) is { } cgm) snapshot["cgmSensorState"] = CgmSensorState.Summary(cgm.Name, cgm.Date, settings);
            snapshot["showRenewalOverlay"] = false;
            Put(snapshot, "iob", s.Iob);
            Put(snapshot, "cob", s.Cob);
            Put(snapshot, "projected", s.Projected);
            if (view?.OverrideName is { } overrideName) snapshot["override"] = overrideName;
            Put(snapshot, "overrideEndAt", view?.OverrideEndAt);
            Put(snapshot, "tempTargetMgdl", view?.TempTargetMgdl);
            Put(snapshot, "tempTargetEndAt", view?.TempTargetEndAt);
            Put(snapshot, "recBolus", s.RecBolus);
            Put(snapshot, "battery", s.Battery);
            Put(snapshot, "pumpBattery", s.PumpBattery);
            if (view is { Basal.Length: > 0 }) snapshot["basalRate"] = view.Basal;
            Put(snapshot, "pumpReservoirU", s.ReservoirU);
            Put(snapshot, "autosens", s.Autosens);
            Put(snapshot, "tdd", s.Tdd);
            Put(snapshot, "targetLowMgdl", s.TargetLowMgdl);
            Put(snapshot, "targetHighMgdl", s.TargetHighMgdl);
            Put(snapshot, "isfMgdlPerU", s.IsfMgdl);
            Put(snapshot, "carbRatio", s.CarbRatio);
            Put(snapshot, "carbsToday", view?.CarbsToday);
            if (_profile is { DefaultProfile.Length: > 0 } p) snapshot["profileName"] = p.DefaultProfile;
            if (settings.DownloadTreatments)
            {
                PutPositive(snapshot, "sageInsertTime", _treatments.SageInsertTime);
                PutPositive(snapshot, "cageInsertTime", _treatments.CageInsertTime);
                PutPositive(snapshot, "iageInsertTime", _treatments.IageInsertTime(settings));
            }
            if (s.MinMax(settings) is { } minMax)
            {
                snapshot["minBgMgdl"] = minMax.Min;
                snapshot["maxBgMgdl"] = minMax.Max;
            }
            return snapshot;
        }
    }

    /// <summary>
    /// The AlarmData LoopFollow's AlarmTask builds for these settings, times in UTC.
    /// Null before anything has been fetched, unless there is a <see cref="LastKnownReading"/>.
    /// </summary>
    public AlarmData? BuildAlarmData(ClientSettings settings, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_bg is null && LastKnownReading is null && !_statusLoaded && _profile is null && !_treatments.Loaded)
                return null;

            var now = Swift.Unix(nowUtc);
            var s = _status;
            var view = _treatments.ViewFor(settings, _profile);
            var overrides = view?.Overrides ?? [];
            var tempTargets = view?.TempTargets ?? [];
            var bg = _bg?.Window(settings.DownloadDays) ?? [];
            List<GlucoseValue> bgReadings = _bg is null && LastKnownReading is { } known
                ? [new GlucoseValue(known.Mgdl, known.At)]
                : bg.TakeLast(AlarmBgReadings).Select(r => new GlucoseValue(r.Sgv, Swift.FromUnix(r.Date))).ToList();

            return new AlarmData
            {
                BgReadings = bgReadings,
                PredictionData = Forecast(settings, now),
                ExpireDate = Utc(_profile?.ExpirationDate),
                LastLoopTime = Utc(s.LastLoopTime),
                LatestOverrideStart = Utc(overrides.LastOrDefault(o => o.Date <= now)?.Date),
                LatestOverrideEnd = Utc(overrides.LastOrDefault(o => o.EndDate <= now)?.EndDate),
                LatestTempTargetStart = Utc(tempTargets.LastOrDefault(t => t.Date <= now)?.Date),
                LatestTempTargetEnd = Utc(tempTargets.LastOrDefault(t => t.EndDate <= now)?.EndDate),
                ActiveOverrideEnd = Utc(overrides.LastOrDefault(o => o.Date <= now && (o.ScheduledEndDate ?? 0) > now)?.ScheduledEndDate),
                ActiveTempTargetEnd = Utc(tempTargets.LastOrDefault(t => t.Date <= now && t.EndDate > now)?.ScheduledEndDate),
                RecBolus = s.RecBolus,
                Cob = s.Cob,
                SageInsertTime = settings.DownloadTreatments ? Utc(_treatments.SageInsertTime) : null,
                PumpInsertTime = settings.DownloadTreatments ? Utc(_treatments.CageInsertTime) : null,
                LatestPumpVolume = s.PumpVolume,
                Iob = s.Iob,
                RecentBoluses = (view?.Boluses ?? []).Select(b => new BolusEntry(b.Units, Swift.FromUnix(b.Date))).ToList(),
                LatestBattery = s.Battery,
                LatestBatteryIsCharging = s.BatteryIsCharging,
                LatestPumpBattery = s.PumpBattery,
                BatteryHistory = s.BatteryHistory.Select(b => new BatterySample(b.Level, b.At)).ToList(),
                RecentCarbs = (view?.Carbs ?? []).Select(c => new CarbSample(c.Grams, Swift.FromUnix(c.Date))).ToList(),
                DbSizePercentage = _dbSizePercentage,
            };
        }
    }

    /// AlarmTask.alarmPredictionData: Loop's stored forecast, or Trio's four collapsed to their lowest.
    private List<GlucoseValue> Forecast(ClientSettings settings, double now)
    {
        var s = _status;
        if (s.Device == "Loop")
        {
            if (!settings.DownloadPrediction || s.LoopForecast is not { } f) return [];
            // DeviceStatusLoop.swift: predictionToLoad × 12 points past the first, clamped to 39…400.
            var toLoad = (int)(settings.PredictionToLoad * 12);
            var points = new List<GlucoseValue>();
            for (var i = 0; i <= toLoad && i < f.Values.Count && points.Count < AlarmForecastPointCap; i++)
                points.Add(new GlucoseValue(Math.Clamp(Swift.RoundToInt(f.Values[i]), 39, 400), Swift.FromUnix(f.Start + 300.0 * i)));
            return points;
        }

        if (s.OpenApsPredBGs is not { } predBGs) return [];
        var forecasts = DeviceStatusState.PredictionTypes.Where(predBGs.ContainsKey).Select(t => predBGs[t]).ToList();
        var start = s.OpenApsPredUpdatedTime ?? now;
        var count = Math.Min(forecasts.Max(f => f.Count), AlarmForecastPointCap);
        var result = new List<GlucoseValue>();
        for (var i = 0; i < count; i++)
        {
            var lowest = forecasts.Where(f => i < f.Count).Min(f => f[i]);
            result.Add(new GlucoseValue(Swift.RoundToInt(lowest), Swift.FromUnix(start + i * 300.0)));
        }
        return result;
    }

    /// Notes.swift updateCGMSensorState: the newest CGM state after the newest reading.
    private (string Name, double Date)? CgmState(TreatmentsState.View? view, ClientSettings settings)
    {
        if (view is null) return null;
        var latestBg = _bg?.Window(settings.DownloadDays) is { Count: > 0 } readings ? readings[^1].Date : (double?)null;
        return CgmSensorState.Active(view.CgmStates, latestBg);
    }

    private static DateTime? Utc(double? seconds) => seconds is { } s && s != 0 ? Swift.FromUnix(s) : null;

    private static void Put(JsonObject o, string key, double? value)
    {
        if (value is { } v) o[key] = v;
    }

    private static void PutPositive(JsonObject o, string key, double value)
    {
        if (value > 0) o[key] = value;
    }

    // MARK: Types

    /// What the fetch has to cover for every device following the patient.
    private sealed record FetchNeeds(int DownloadDays, int BgFetchDays, bool DownloadTreatments, bool IageVisible,
        int BgUpdateDelay, TimeZoneInfo Zone)
    {
        public static FetchNeeds From(List<ClientSettings> all) => new(
            all.Max(s => s.DownloadDays),
            all.Max(s => s.BgFetchDays),
            all.Any(s => s.DownloadTreatments),
            all.Any(s => s.IageVisible),
            all.Min(s => s.BgUpdateDelay),
            all[0].TimeZone);
    }

    internal sealed record Reading(int Sgv, double Date, string? Direction);

    /// <summary>
    /// One entries.json response as ShareGlucoseData decodes it, dates in seconds rounded
    /// half to even: sgv, else mbg, else glucose, rounded; a required numeric date; an
    /// optional string direction. Null when it is not an array or one entry fails.
    /// </summary>
    private static List<Reading>? DecodeEntries(JsonNode? response)
    {
        if (response is not JsonArray arr) return null;
        var readings = new List<Reading>(arr.Count);
        foreach (var x in arr)
        {
            if (x is not JsonObject o) return null;
            double? value = null;
            foreach (var key in (ReadOnlySpan<string>)["sgv", "mbg", "glucose"])
                if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                {
                    value = v.GetValue<double>();
                    break;
                }
            if (value is not { } sgv) return null;
            if (o["date"] is not JsonValue dv || dv.GetValueKind() != JsonValueKind.Number) return null;
            string? direction = null;
            if (o["direction"] is { } dir)
            {
                if (Swift.Str(dir) is not { } d) return null;
                direction = d;
            }
            var seconds = Math.Round(dv.GetValue<double>() / 1000, MidpointRounding.ToEven);
            readings.Add(new Reading(Swift.RoundToInt(sgv), seconds, direction));
        }
        return readings;
    }

    /// <summary>
    /// A reading this many seconds after the one before it is the same reading from another
    /// uploader (Trio and a Dexcom bridge write the same reading about 10 s apart, and may
    /// round it differently). A 1-minute CGM's readings stay apart.
    /// </summary>
    internal const double SameReadingSeconds = 30;

    /// <summary>
    /// A reading or loop time further ahead of the hub's clock than this comes from a clock that
    /// is wrong and is dropped: taken as the latest, it would hold off the BG and loop alarms
    /// until real time caught up with it.
    /// </summary>
    internal const double FutureToleranceSeconds = 5 * 60;

    /// <summary>The readings that are not more than <see cref="FutureToleranceSeconds"/> ahead of <paramref name="nowUnix"/>.</summary>
    internal static List<Reading> NotFuture(IEnumerable<Reading> readings, double nowUnix) =>
        readings.Where(r => r.Date <= nowUnix + FutureToleranceSeconds).ToList();

    /// Logs dropped future readings once per newest dropped reading, since every fetch sees them again.
    private void LogFuture(int fetched, List<Reading> kept, IEnumerable<Reading> all)
    {
        if (kept.Count == fetched) return;
        var newest = all.Max(r => r.Date);
        if (newest == _lastFutureLogged) return;
        _lastFutureLogged = newest;
        _log.LogWarning("{Count} BG readings dated more than {Minutes} minutes ahead were dropped (newest {At:u})",
            fetched - kept.Count, FutureToleranceSeconds / 60, Swift.FromUnix(newest));
    }

    private double _lastFutureLogged;

    /// <summary>
    /// Drops every reading within <see cref="SameReadingSeconds"/> of the one kept before it,
    /// whatever its value, and keeps the earliest copy, so the latest reading's time stays put
    /// when a second uploader's copy arrives. Newest first in and out, as BGData.swift's
    /// deduplicateBGReadings, which drops only a repeat of the same value.
    /// </summary>
    internal static List<Reading> Dedup(IEnumerable<Reading> readings)
    {
        var kept = new List<Reading>();
        foreach (var r in readings.Reverse())
            if (kept.Count == 0 || r.Date - kept[^1].Date >= SameReadingSeconds)
                kept.Add(r);
        kept.Reverse();
        return kept;
    }

    /// <summary>The readings ProcessDexBGData was given, newest first.</summary>
    private sealed record Bg(List<Reading> Readings, DateTime ProcessedAt)
    {
        /// ProcessDexBGData's bgData: oldest first, within downloadDays of the fetch, at most 600.
        public List<Reading> Window(int downloadDays)
        {
            var from = Swift.Unix(ProcessedAt) - downloadDays * 24 * 3600.0;
            var list = new List<Reading>();
            for (var i = Readings.Count - 1; i >= 0; i--)
                if (Readings[i].Date >= from && Readings[i].Sgv <= 600) list.Add(Readings[i]);
            return list;
        }
    }
}
