using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine.State;
using LFHub.Nightscout;

namespace LFHub.Engine;

/// <summary>
/// Follows each patient's Nightscout the way LoopFollow does (REST on its
/// cadence, with the Socket.IO stream as an early trigger) and drives each
/// device's badge, Live Activity and alarms. A 30-second tick refreshes what is
/// due and covers readings that stop arriving, which an event stream alone
/// cannot detect.
/// </summary>
public sealed partial class HubEngine : BackgroundService
{
    /// Same threshold LoopFollow uses to blank its own badge.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(12);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailedPushBackoff = TimeSpan.FromMinutes(5);
    /// The patient's latest reading is written to the database at most this often.
    private static readonly TimeSpan SaveReadingEvery = TimeSpan.FromMinutes(1);
    /// Pushes to a device rejected this long are logged as a warning, once per outage.
    private static readonly TimeSpan PushFailingWarnAfter = TimeSpan.FromMinutes(10);
    /// <summary>
    /// How long an alarm check that has begun may run on once shutdown begins, so its pushes go
    /// out and their outcome is recorded; well inside the host's 30-second shutdown timeout.
    /// </summary>
    private static readonly TimeSpan AlarmCheckShutdownGrace = TimeSpan.FromSeconds(10);

    /// LoopFollow's renewalThreshold: an activity is replaced before Apple's 8-hour cap.
    public static readonly TimeSpan LiveActivityLifetime = TimeSpan.FromHours(7.5);
    /// LoopFollow resends an unchanged snapshot after five minutes.
    private static readonly TimeSpan LiveActivityHeartbeat = TimeSpan.FromMinutes(5);
    /// <summary>
    /// A renewal is attempted again this long after the last attempt, whether that one
    /// failed or started an activity whose token the app has not reported yet. The
    /// deadline moves only when the app reports a new activity token.
    /// </summary>
    private static readonly TimeSpan RenewalRetry = TimeSpan.FromMinutes(30);

    /// When each device's activity renewal was last attempted. Updates to the old
    /// activity keep its last push time moving, so the retry is timed from this.
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRenewalAttempt = new();
    /// A hub-driven activity goes stale this long after its last update, so the
    /// Lock Screen shows when the hub has stopped sending.
    private static readonly TimeSpan LiveActivityStaleAfter = TimeSpan.FromMinutes(15);

    private readonly IHubStore _store;
    private readonly IApnsClient _apns;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<HubEngine> _log;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<Guid, PatientRuntime> _runtimes = new();
    private readonly Channel<KickRequest> _kicks = Channel.CreateUnbounded<KickRequest>();

    /// <param name="PatientId">Null reloads patient sources.</param>
    /// <param name="NewData">The socket announced new data: refresh what it names first.</param>
    /// <param name="Immediate">Check alarms at once, without waiting for their minute.</param>
    private sealed record KickRequest(Guid? PatientId, bool NewData, bool Immediate = false);

    public HubEngine(IHubStore store, IApnsClient apns, ILoggerFactory loggers, TimeProvider time)
    {
        _store = store;
        _apns = apns;
        _loggers = loggers;
        _log = loggers.CreateLogger<HubEngine>();
        _time = time;
    }

    /// <summary>
    /// Whether a patient's sources may be on a private, loopback or link-local address
    /// (<c>LFHUB_ALLOW_PRIVATE_NIGHTSCOUT</c>); see <see cref="SourceAddress"/>.
    /// </summary>
    public bool AllowPrivateSources { get; init; }

    private HttpClient? _sourceHttp;
    private HttpMessageInvoker? _sourceSockets;

    /// <summary>The HTTP client patient sources are fetched with.</summary>
    internal HttpClient SourceHttp
    {
        get => _sourceHttp ??= new HttpClient(SourceAddress.Handler(AllowPrivateSources))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        init => _sourceHttp = value;
    }

    /// <summary>What the Nightscout Socket.IO streams connect with, under the same address rules.</summary>
    private HttpMessageInvoker SourceSockets =>
        _sourceSockets ??= new HttpMessageInvoker(SourceAddress.Handler(AllowPrivateSources));

    /// <summary>
    /// The longest one patient's fetches may take in one evaluation, so a Nightscout
    /// that accepts connections but does not answer cannot hold up the alarm checks or
    /// the other patients for long.
    /// </summary>
    internal TimeSpan RefreshDeadline { get; init; } = TimeSpan.FromSeconds(45);

    /// How often a standing-by instance tries to take the engine lock.
    internal TimeSpan EngineLockRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// True while this instance waits for another instance to release the engine lock.
    public bool StandingBy { get; private set; }

    /// <summary>Whether a patient's Nightscout Socket.IO stream is followed.</summary>
    internal bool FollowStreams { get; init; } = true;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Re-evaluates one patient soon, e.g. after a device changed its settings.
    /// `immediate` also checks its alarms at once, for a snooze or new alarms from the app.
    /// </summary>
    public void Kick(Guid patientId, bool immediate = false) =>
        _kicks.Writer.TryWrite(new KickRequest(patientId, false, immediate));

    /// <summary>Reloads patient sources soon, e.g. after a source was changed.</summary>
    public void Reload() => _kicks.Writer.TryWrite(new KickRequest(null, false));

    /// <summary>When the engine loop last finished a tick, for liveness.</summary>
    public bool IsTicking => LastTickAt is { } tick && UtcNow - tick < Tick * 4;

    public LiveStatus? Status(Guid patientId) =>
        _runtimes.TryGetValue(patientId, out var rt) ? rt.Status() : null;

    public IReadOnlyList<LiveStatus> AllStatuses() => _runtimes.Values.Select(rt => rt.Status()).ToList();

    /// <summary>The patient's bundle (see PatientState.Bundle), or null before the patient is followed.</summary>
    public JsonObject? Bundle(Guid patientId) =>
        _runtimes.TryGetValue(patientId, out var rt) ? rt.State.Bundle() : null;

    public long BundleVersion(Guid patientId) =>
        _runtimes.TryGetValue(patientId, out var rt) ? rt.State.BundleVersion : 0;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<long>>> _bundleListeners = new();

    /// <summary>
    /// Bundle versions for one patient as they change, for an app listening while
    /// it is open. Dispose the subscription when the listener goes away.
    /// </summary>
    public (ChannelReader<long> Versions, IDisposable Subscription) ListenForBundles(Guid patientId)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _bundleListeners.GetOrAdd(patientId, _ => new())[id] = channel;
        return (channel.Reader, new Unsubscribe(() =>
        {
            if (_bundleListeners.TryGetValue(patientId, out var listeners)) listeners.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }));
    }

    private void PublishBundle(Guid patientId, long version)
    {
        if (!_bundleListeners.TryGetValue(patientId, out var listeners)) return;
        foreach (var channel in listeners.Values) channel.Writer.TryWrite(version);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>When the evaluation loop last completed a tick; null before the first.</summary>
    public DateTime? LastTickAt { get; private set; }

    public static TimeSpan TickInterval => Tick;

    /// <summary>
    /// The engine loop. On shutdown it ends after the check in progress (see
    /// <see cref="AlarmCheckShutdownGrace"/>) and closes every patient's runtime.
    /// </summary>
    /// <summary>
    /// Runs the engine while this instance holds the engine lock, so one instance alone follows
    /// the patients and pushes to the devices, however many share the database. The others serve
    /// the API and stand by; an instance that loses the lock stops its engine and stands by.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var engineLock = await LockEngineAsync(stoppingToken);
                try
                {
                    await RunAsync(engineLock, stoppingToken);
                }
                finally
                {
                    foreach (var (id, rt) in _runtimes)
                    {
                        _runtimes.TryRemove(id, out _);
                        await rt.DisposeAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Waits until this instance holds the engine lock, ticking as it waits.</summary>
    private async Task<IEngineLock> LockEngineAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                if (await _store.TryLockEngineAsync(stoppingToken) is { } engineLock)
                {
                    if (StandingBy) _log.LogInformation("Engine lock taken; running the engine");
                    StandingBy = false;
                    return engineLock;
                }
                if (!StandingBy)
                    _log.LogWarning("Another hub instance runs the engine; this one serves the API and stands by");
                StandingBy = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Taking the engine lock failed: {Message}", ex.Message);
            }
            LastTickAt = UtcNow;
            await Task.Delay(EngineLockRetry, stoppingToken);
        }
    }

    private async Task RunAsync(IEngineLock engineLock, CancellationToken stoppingToken)
    {
        await ReconcileAsync();
        LastTickAt = UtcNow;
        var lastReload = UtcNow;
        var nextTick = UtcNow + Tick;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await engineLock.IsHeldAsync(stoppingToken))
            {
                _log.LogError("Engine lock lost; stopping the engine until the lock is taken again");
                return;
            }
            var wait = nextTick - UtcNow;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (wait > TimeSpan.Zero) timeout.CancelAfter(wait);

            try
            {
                if (wait > TimeSpan.Zero && await _kicks.Reader.WaitToReadAsync(timeout.Token))
                {
                    while (_kicks.Reader.TryRead(out var kick))
                    {
                        if (kick.PatientId is { } patientId)
                            await EvaluateAsync(patientId, kick.NewData, kick.NewData || kick.Immediate, stoppingToken);
                        else await ReconcileAsync();
                    }
                    continue;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
            }

            if (UtcNow - lastReload >= ReloadEvery)
            {
                await ReconcileAsync();
                lastReload = UtcNow;
                try
                {
                    if (await _store.PruneUnseenDevicesAsync(TimeSpan.FromDays(30)) is > 0 and var pruned)
                        _log.LogInformation("Removed {Count} devices not seen for 30 days whose push token is gone", pruned);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Device pruning failed: {Message}", ex.Message);
                }
            }

            foreach (var patientId in _runtimes.Keys)
                await EvaluateAsync(patientId, true, false, stoppingToken);
            LastTickAt = UtcNow;
            nextTick = UtcNow + Tick;
        }
    }

    /// <summary>
    /// Starts, keeps or stops each patient's runtime to match the database. Never throws:
    /// a failure is logged as an error, and a patient that cannot start is skipped until
    /// the next reconcile, so the engine loop keeps running for the others.
    /// </summary>
    internal async Task ReconcileAsync()
    {
        try
        {
            await ReconcilePatientsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError("Reconciling patients failed: {Message}", ex.Message);
        }
    }

    private async Task ReconcilePatientsAsync()
    {
        List<Patient> patients;
        try
        {
            patients = await _store.PatientsWithSourceAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Patient load failed: {Message}", ex.Message);
            return;
        }

        var wanted = patients.ToDictionary(p => p.Id);
        foreach (var (id, rt) in _runtimes)
        {
            // Only a changed source replaces a patient. The stream reconnects on its own, and a
            // patient kept running keeps the readings the missed-reading alarm needs.
            if (wanted.TryGetValue(id, out var p) && SourceKey(p) == rt.SourceKey)
                continue;
            _log.LogInformation("Stopping patient {Patient}", id);
            _runtimes.TryRemove(id, out _);
            await rt.DisposeAsync();
        }

        foreach (var p in patients)
        {
            if (_runtimes.ContainsKey(p.Id)) continue;
            if (!p.HasSource)
            {
                // A stored source whose secret does not open reads as unset.
                _log.LogError("Patient {Patient} is not followed: it has no Nightscout URL and no Dexcom account that opens",
                    p.Id);
                continue;
            }

            PatientRuntime rt;
            try
            {
                _log.LogInformation("Starting patient {Patient}: Nightscout {Nightscout}, Dexcom {Dexcom}", p.Id,
                    p.NightscoutUrl is { } u ? new Uri(u).Host : "none", p.Dexcom?.Server ?? "none");
                rt = new PatientRuntime(p, SourceKey(p), this,
                    () => _kicks.Writer.TryWrite(new KickRequest(p.Id, true)));
            }
            catch (Exception ex)
            {
                _log.LogError("Patient {Patient} is not followed: starting it failed: {Message}", p.Id, ex.Message);
                continue;
            }
            var patientId = p.Id;
            rt.State.BundleChanged += () => PublishBundle(patientId, rt.State.BundleVersion);
            _runtimes[p.Id] = rt;
            if (rt.Stream is { } stream) await stream.StartAsync();
        }
    }

    /// <param name="refresh">Fetch whatever is due from Nightscout first.</param>
    /// <param name="newData">New data arrived: alarms are checked now, not on their minute.</param>
    internal async Task EvaluateAsync(Guid patientId, bool refresh, bool newData, CancellationToken ct)
    {
        try
        {
            if (!_runtimes.TryGetValue(patientId, out var rt)) return;
            var devices = await _store.DevicesForPatientAsync(patientId);
            var settings = devices.ToDictionary(d => d.Id, ClientSettingsFor);
            rt.State.UpdateFetchSettings(settings.Values);
            if (refresh)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(RefreshDeadline);
                    await rt.State.RefreshAsync(false, deadline.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning("Nightscout refresh for patient {Patient} failed: {Message}", patientId, ex.Message);
                }
                await SaveLastReadingAsync(patientId, rt);
            }

            // An alarm check that has begun is not cancelled with the engine: it runs to its end
            // within the grace, so the state it wrote matches the pushes it sent.
            using var alarmCheck = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _time);
            using var onStop = ct.Register(() => alarmCheck.CancelAfter(AlarmCheckShutdownGrace));

            // Each device's badge, Live Activity, alarms, speech and heartbeat run on their own, so a
            // failure in one never holds back another.
            foreach (var device in devices)
            {
                await GuardAsync("Badge", device.Id, () => BadgeAsync(device, rt.State.LatestReading, settings[device.Id].Mmol, ct), ct);
                await GuardAsync("Live Activity", device.Id, () => LiveActivityAsync(device, rt.State, settings[device.Id], ct), ct);
                ct.ThrowIfCancellationRequested();
                await GuardAsync("Alarm check", device.Id,
                    () => AlarmsAsync(device, rt.State, settings[device.Id], newData, alarmCheck.Token), alarmCheck.Token);
                await GuardAsync("Speak", device.Id, () => SpeakAsync(device, rt.State, settings[device.Id], ct), ct);
                await GuardAsync("Heartbeat", device.Id, () => HeartbeatAsync(device, ct), ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Evaluation for patient {Patient} failed: {Message}", patientId, ex.Message);
        }
    }

    /// <summary>
    /// Writes the patient's latest reading to the database when it changed, at most once a
    /// minute, so a restarted hub starts from it (<see cref="PatientState.LastKnownReading"/>).
    /// </summary>
    private async Task SaveLastReadingAsync(Guid patientId, PatientRuntime rt)
    {
        if (rt.State.LatestReading is not { } reading || reading == rt.SavedReading) return;
        if (rt.SavedReadingAt is { } at && UtcNow - at < SaveReadingEvery) return;
        rt.SavedReadingAt = UtcNow;
        try
        {
            await _store.SaveLastReadingAsync(patientId, reading.At, reading.Mgdl);
            rt.SavedReading = reading;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Saving the latest reading of patient {Patient} failed: {Message}", patientId, ex.Message);
        }
    }

    private async Task GuardAsync(string what, Guid deviceId, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("{What} for {Device} failed: {Message}", what, deviceId, ex.Message);
        }
    }

    /// <summary>
    /// The device's uploaded settings; its display unit falls back to the unit
    /// the device reported with its hub settings. A device whose settings name no time zone
    /// this host knows, and that names one or has alarms on, is logged once until it does.
    /// </summary>
    private ClientSettings ClientSettingsFor(Device device)
    {
        var json = (JsonObject)device.ClientSettings.DeepClone();
        json["unit"] ??= device.Mmol ? "mmol" : "mgdl";
        var settings = ClientSettings.FromJson(json);
        var zone = json["timeZone"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null;
        if (ClientSettings.ResolveTimeZone(json) is not null)
            _zoneWarned.TryRemove(device.Id, out _);
        else if ((zone is { Length: > 0 } || device.AlarmsEnabled) && _zoneWarned.TryAdd(device.Id, true))
            _log.LogWarning(
                "Time zone {Zone} from {Device} is unknown here; its day and night, and the local times it is shown, use UTC",
                zone is { Length: > 0 } ? zone : "(none)", device.Id);
        return settings;
    }

    private readonly ConcurrentDictionary<Guid, bool> _zoneWarned = new();
    private readonly ConcurrentDictionary<Guid, bool> _pushFailingWarned = new();

    /// <summary>
    /// What a push to the device's own token carried, for <see cref="TrackPushAsync"/>. Pushes
    /// to a Live Activity token are not tracked: a rejected one means the activity ended.
    /// </summary>
    internal enum PushKind
    {
        /// A badge, alarm stop, Latest BG, speech or heartbeat.
        Device,
        /// An alarm.
        Alarm,
    }

    /// <summary>
    /// Records the outcome of a push to the device's own token for its push health
    /// (<c>/v1/me</c>), and whether APNs rejected that token as gone. Logs a warning
    /// once when pushes to the device have been rejected for 10 minutes. A failure to
    /// record is logged and does not reach the caller, so it never holds back a push.
    /// </summary>
    internal async Task TrackPushAsync(Guid deviceId, PushResult result, PushKind kind)
    {
        try
        {
            var since = await _store.RecordPushOutcomeAsync(deviceId, UtcNow, result.Delivered, kind == PushKind.Alarm,
                result.TokenGone);
            if (result.Delivered)
                _pushFailingWarned.TryRemove(deviceId, out _);
            else if (since is { } failingSince && UtcNow - failingSince >= PushFailingWarnAfter &&
                     _pushFailingWarned.TryAdd(deviceId, true))
                _log.LogWarning("Pushes to {Device} have been rejected since {Since:u}; last: {Status}", deviceId,
                    failingSince, result.Status);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Recording a push outcome for {Device} failed: {Message}", deviceId, ex.Message);
        }
    }

    private async Task BadgeAsync(Device device, (DateTime At, int Mgdl)? reading, bool mmol, CancellationToken ct)
    {
        if (!device.BadgeEnabled)
        {
            if (device.LastBadge is not null) await _store.ClearLastBadgeAsync(device.Id);
            return;
        }
        if (device.Apns is null) return;

        var fresh = reading is { } r && UtcNow - r.At < StaleAfter;
        var desired = fresh ? BadgeValue(reading!.Value.Mgdl, mmol) : 0;
        if (desired == device.LastBadge) return;
        if (device.LastPushStatus is { } s && s != "200" &&
            device.LastPushAt is { } at && UtcNow - at < FailedPushBackoff)
            return;

        var result = await _apns.SendBadgeAsync(device.Apns, desired, ct);
        await _store.RecordPushAsync(device.Id, desired, result.Status, result.Delivered);
        await TrackPushAsync(device.Id, result, PushKind.Device);
        if (result.Delivered)
            _log.LogInformation("Badge pushed to {Device}", device.Id);
        else
            _log.LogWarning("Badge push to {Device} failed: {Status}", device.Id, result.Status);
    }

    /// <summary>
    /// Sends the snapshot to the device's activity when it changed or the
    /// heartbeat is due, and replaces the activity through push-to-start once
    /// the renewal deadline passes.
    /// </summary>
    private async Task LiveActivityAsync(Device device, PatientState state, ClientSettings settings,
        CancellationToken ct)
    {
        if (!device.LiveActivityEnabled || device.Apns is null) return;

        var la = device.LiveActivity;
        var now = UtcNow;
        if (la.LastPushStatus is { } s && !s.EndsWith("200") && la.LastPushAt is { } failedAt &&
            now - failedAt < FailedPushBackoff)
            return;

        if (state.BuildSnapshot(settings, now) is not { } snapshot) return;

        if (la.RenewBy is { } renewBy && now >= renewBy &&
            (!_lastRenewalAttempt.TryGetValue(device.Id, out var lastTry) || now - lastTry >= RenewalRetry))
        {
            _lastRenewalAttempt[device.Id] = now;
            await RenewLiveActivityAsync(device, snapshot, ct);
            return;
        }

        if (la.Token is null) return;

        snapshot["showRenewalOverlay"] = la.RenewalFailed;
        var fingerprint = snapshot.ToJsonString();
        if (fingerprint == la.LastFingerprint && la.LastPushAt is { } last && now - last < LiveActivityHeartbeat)
            return;

        var aps = new JsonObject
        {
            ["timestamp"] = Unix(now),
            ["event"] = "update",
            ["stale-date"] = Unix(now + LiveActivityStaleAfter),
            ["content-state"] = ContentState(snapshot, now),
        };
        var result = await _apns.SendLiveActivityAsync(device.Apns, la.Token, aps, ct);
        await _store.RecordLiveActivityPushAsync(device.Id, fingerprint, result.Status, result.Delivered,
            result.TokenInvalid);
        if (result.Delivered)
            _log.LogInformation("Live Activity updated on {Device}", device.Id);
        else
            _log.LogWarning("Live Activity update to {Device} failed: {Status}", device.Id, result.Status);
    }

    /// <summary>
    /// Starts a fresh activity with the payload LoopFollow's push-to-start uses.
    /// The old activity keeps receiving updates until the app reports the new
    /// one's token; the app ends the old one when it adopts the new. The deadline
    /// stays where it is until then, so the renewal is retried every 30 minutes.
    /// </summary>
    private async Task RenewLiveActivityAsync(Device device, JsonObject snapshot, CancellationToken ct)
    {
        var la = device.LiveActivity;
        if (la.PushToStartToken is null)
        {
            await _store.RecordLiveActivityRenewalAsync(device.Id, false, "renewal: no push-to-start token", false);
            _log.LogWarning("Live Activity renewal for {Device} impossible: no push-to-start token", device.Id);
            return;
        }

        var now = UtcNow;
        snapshot["showRenewalOverlay"] = false;
        var aps = new JsonObject
        {
            ["timestamp"] = Unix(now),
            ["event"] = "start",
            ["stale-date"] = Unix(now + LiveActivityStaleAfter),
            ["attributes-type"] = "GlucoseLiveActivityAttributes",
            ["attributes"] = new JsonObject { ["title"] = "LoopFollow" },
            ["content-state"] = ContentState(snapshot, now),
            // iOS drops a start without an alert; empty and passive keeps it silent.
            ["alert"] = new JsonObject { ["title"] = "", ["body"] = "" },
            ["interruption-level"] = "passive",
        };
        var headers = new Dictionary<string, string>
        {
            ["apns-expiration"] = Unix(now.AddMinutes(10)).ToString(),
            ["apns-collapse-id"] = $"{device.Apns!.BundleId}.la.start",
        };
        var result = await _apns.SendLiveActivityAsync(device.Apns, la.PushToStartToken, aps, ct, headers);
        await _store.RecordLiveActivityRenewalAsync(device.Id, result.Delivered, $"start {result.Status}",
            result.TokenInvalid);
        if (result.Delivered)
            _log.LogInformation("Live Activity renewed on {Device} via push-to-start", device.Id);
        else
            _log.LogWarning("Live Activity renewal for {Device} failed: {Status}", device.Id, result.Status);
    }

    private static JsonObject ContentState(JsonObject snapshot, DateTime now) => new()
    {
        ["snapshot"] = snapshot.DeepClone(),
        // Unix seconds: always ahead of the app's own counter, so the extension's
        // ordering holds when the hub takes over an activity the app started.
        ["seq"] = Unix(now),
        ["reason"] = "hub",
        ["producedAt"] = (now - DateTime.UnixEpoch).TotalSeconds,
    };

    private static long Unix(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

    private sealed record ParsedExport(string Hash, IReadOnlyList<Alarms.Alarm> Alarms, Alarms.AlarmConfiguration Config);

    /// The latest parsed alarm export of each device, with the SHA-256 of its JSON.
    private readonly ConcurrentDictionary<Guid, ParsedExport> _exports = new();

    /// <summary>
    /// The device's alarm export, parsed once for each JSON it uploads; null when it has none
    /// that parses. The alarms and configuration are shared: a caller clones what it changes.
    /// </summary>
    internal (IReadOnlyList<Alarms.Alarm> Alarms, Alarms.AlarmConfiguration Config)? AlarmExport(Device device)
    {
        if (device.AlarmsJson is not { } json) return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (_exports.TryGetValue(device.Id, out var cached) && cached.Hash == hash) return (cached.Alarms, cached.Config);
        if (!TryParseAlarms(json, out var parsed, out _, out _)) return null;
        _exports[device.Id] = new ParsedExport(hash, parsed.Alarms, parsed.Config);
        return parsed;
    }

    /// <summary>Parses the app's alarm export; on failure, the reason.</summary>
    public static bool TryParseAlarms(string json,
        out (IReadOnlyList<Alarms.Alarm> Alarms, Alarms.AlarmConfiguration Config) parsed, out int skipped,
        out string error)
    {
        try
        {
            parsed = Alarms.AlarmSettingsJson.ParseLenient(json, out skipped);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            parsed = default;
            skipped = 0;
            error = $"alarm export does not parse: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The badge LoopFollow shows: mg/dL as is, mmol/L to one decimal with the
    /// separator removed (5.4 → 54).
    /// </summary>
    public static int BadgeValue(int mgdl, bool mmol) =>
        mmol ? (int)Math.Round(Math.Round(mgdl / 18.01559, 1, MidpointRounding.ToEven) * 10) : mgdl;

    private static string SourceKey(Patient p) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{p.NightscoutUrl}\n{p.NightscoutToken}\n{p.Dexcom?.Username}\n{p.Dexcom?.Password}\n{p.Dexcom?.Server}")));

    /// <param name="Connected">The Nightscout stream is up; true for a Dexcom-only patient, which has none.</param>
    /// <param name="Healthy">Connected, or reconnecting within the grace window.</param>
    public sealed record LiveStatus(bool Connected, DateTime? LastReadingAt, bool Healthy);

    private sealed class PatientRuntime : IAsyncDisposable
    {
        private readonly HubEngine _engine;
        private readonly StreamHealth _health;

        /// A Dexcom-only patient has no Nightscout stream; the tick alone drives its polling.
        public PatientRuntime(Patient p, string sourceKey, HubEngine engine, Action onNewData)
        {
            _engine = engine;
            _health = new StreamHealth(engine.UtcNow);
            SourceKey = sourceKey;
            State = new PatientState(engine.SourceHttp, p.NightscoutUrl, p.NightscoutToken, p.Dexcom,
                engine._loggers.CreateLogger<PatientState>())
            {
                UtcNow = () => engine.UtcNow,
                LastKnownReading = p.LastReading,
            };
            SavedReading = p.LastReading;
            if (p.NightscoutUrl is null || !engine.FollowStreams) return;
            Stream = new NightscoutStream(p.NightscoutUrl, p.NightscoutToken, $"patient {p.Id}",
                engine._loggers.CreateLogger<NightscoutStream>(), engine.SourceSockets,
                onDataUpdate: data =>
                {
                    State.NotifyDataUpdate(data);
                    onNewData();
                },
                onAuthorized: () =>
                {
                    State.SocketLive = true;
                    _health.Authorized();
                },
                onClosed: () =>
                {
                    State.SocketLive = false;
                    _health.Closed(engine.UtcNow);
                });
        }

        public string SourceKey { get; }
        /// The latest reading as the database holds it, and when it was last written.
        public (DateTime At, int Mgdl)? SavedReading { get; set; }
        public DateTime? SavedReadingAt { get; set; }
        public NightscoutStream? Stream { get; }
        public PatientState State { get; }

        public bool IsHealthy(TimeSpan grace) => Stream is null || _health.IsHealthy(_engine.UtcNow, grace);

        public async ValueTask DisposeAsync()
        {
            if (Stream is { } stream) await stream.DisposeAsync();
            State.Dispose();
        }

        public LiveStatus Status() =>
            new(Stream is null || (Stream.Connected && State.SocketLive), State.LatestReading?.At, IsHealthy(DisconnectGrace));
    }
}
