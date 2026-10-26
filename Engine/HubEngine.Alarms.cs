using System.Collections.Concurrent;
using System.Text.Json;
using LFHub.Api;
using LFHub.Apns;
using LFHub.Db;
using LFHub.Engine.Alarms;
using LFHub.Engine.State;

namespace LFHub.Engine;

public sealed partial class HubEngine
{
    /// LoopFollow checks alarms every minute, and right after new data.
    private static readonly TimeSpan AlarmCheckInterval = TimeSpan.FromSeconds(60);
    /// A sounding alarm that repeats is delivered again this often until it is snoozed.
    private static readonly TimeSpan AlarmRepeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A system alarm is not stopped for a cleared condition before this: LoopFollow
    /// re-checks a sounding alarm on its next minute, and an event alarm (an override
    /// ending, say) holds only on the check that fires it.
    /// </summary>
    private static readonly TimeSpan MinimumRing = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A non-repeating alarm plays its sound once in LoopFollow; the system alarm keeps
    /// ringing until stopped, so the hub stops it at the first check this long after the
    /// phone's delivery receipt.
    /// </summary>
    private static readonly TimeSpan SingleRing = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often a device with alarms on gets a heartbeat push. The app logs how late each
    /// one arrives; the heartbeat drives no alarm.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastHeartbeat = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastAlarmCheck = new();
    /// Devices with alarms on that cannot get them, logged once until that changes.
    private readonly ConcurrentDictionary<Guid, bool> _alarmsUnsentWarned = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _latestBgNotified = new();
    /// The time of the newest reading the speak step has taken for each device, spoken or not.
    private readonly ConcurrentDictionary<Guid, DateTime> _lastSpoken = new();
    /// When the app last handled each alarm, by device and alarm.
    private readonly ConcurrentDictionary<(Guid Device, Guid Alarm), DateTime> _handledAt = new();

    /// <summary>
    /// An alarm the app handled counts as snoozed this long, so it does not fire again
    /// before the alarm upload that carries the app's snooze has arrived.
    /// </summary>
    internal static readonly TimeSpan HandledGrace = TimeSpan.FromMinutes(2);

    /// Records that the app snoozed or acknowledged <paramref name="alarmId"/> on the device.
    public void AlarmHandled(Guid deviceId, Guid alarmId) => _handledAt[(deviceId, alarmId)] = UtcNow;
    /// Stops APNs did not accept, by device and alarm, with when the first was tried.
    private readonly ConcurrentDictionary<(Guid Device, Guid Alarm), (ActiveAlarm Alarm, AlarmStopReason Reason, DateTime Since)>
        _failedStops = new();

    /// How long a stop APNs did not accept is tried again, at each of the device's checks.
    private static readonly TimeSpan StopRetryFor = TimeSpan.FromMinutes(15);

    /// LoopFollow's isLatestReadingRecent: BG-based checks need a reading this fresh.
    private static readonly TimeSpan RecentReading = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One alarm check for a device, with the alarms and configuration it last
    /// uploaded: a new firing is delivered; a sounding alarm whose sound repeats is
    /// delivered again, as LoopFollow loops it, until it is snoozed or acknowledged
    /// in the app, Snooze All begins, or its condition clears. One whose sound does not
    /// repeat is delivered again in the same way until the phone's delivery receipt
    /// arrives. `immediate` (new data, or something from the app) skips the minute between checks.
    /// </summary>
    /// <remarks>
    /// The check reads the device's row again just before it runs, since the refresh before
    /// it may take up to <see cref="RefreshDeadline"/>. It then writes its outcome (the
    /// engine's state with its condition markers, the sounding alarm and the alarms it turned
    /// off) in one write that holds only while the alarm revision is the one it read, and
    /// sends its pushes after that write. When the app handled an alarm or uploaded its
    /// alarm list in between, the write is refused and nothing is sent; the check that the
    /// app's call starts runs from the state the app left. The outcome of the alarm push is
    /// recorded afterwards on the alarm it was sent for, if that is still the sounding one.
    /// </remarks>
    private async Task AlarmsAsync(Device listed, PatientState state, ClientSettings settings, bool immediate,
        CancellationToken ct)
    {
        if (listed.AlarmsEnabled && listed.Apns is null)
        {
            if (_alarmsUnsentWarned.TryAdd(listed.Id, true))
                _log.LogWarning("Alarms for {Device} are not evaluated: {Reason}", listed.Id,
                    listed.ApnsKeyDoesNotOpen
                        ? "its APNs registration does not open with LFHUB_MASTER_KEY; the app registers again with PUT /v1/device/apns"
                        : "it has no APNs registration");
            return;
        }
        _alarmsUnsentWarned.TryRemove(listed.Id, out _);
        if (!listed.AlarmsEnabled && listed.ActiveAlarm is null) return;
        var now = UtcNow;
        if (listed.AlarmsEnabled && !immediate && _lastAlarmCheck.TryGetValue(listed.Id, out var last) &&
            now - last < AlarmCheckInterval)
            return;

        if (await _store.DeviceAsync(listed.Id) is not { } device) return;
        if (!device.AlarmsEnabled)
        {
            if (device.ActiveAlarm is { } sounding) await StopForAlarmsOffAsync(device, sounding, ct);
            return;
        }
        if (device.Apns is null) return;
        await RetryFailedStopsAsync(device, now, ct);
        if (AlarmExport(device) is not { } export) return;
        _lastAlarmCheck[device.Id] = now;

        var config = export.Config.Clone();
        var alarms = export.Alarms.Select(a =>
        {
            var copy = a.Clone();
            if (device.HubDisabledAlarms.Contains(copy.Id)) copy.IsEnabled = false;
            if (_handledAt.TryGetValue((device.Id, copy.Id), out var handled))
            {
                var graceEnd = handled + HandledGrace;
                if (graceEnd <= now) _handledAt.TryRemove((device.Id, copy.Id), out _);
                else if (copy.SnoozedUntil is not { } until || until < graceEnd) copy.SnoozedUntil = graceEnd;
            }
            return copy;
        }).ToList();

        var alarmState = device.AlarmStateJson is { } json
            ? JsonSerializer.Deserialize<AlarmState>(json) ?? new AlarmState()
            : new AlarmState();

        var stops = new List<(ActiveAlarm Alarm, AlarmStopReason Reason)>();

        // A snooze, a disabled alarm or Snooze All from the app ends the sounding alarm
        // and clears the current one, as performSnooze does, so the condition fires
        // again once the snooze is over. Its system alarm is stopped as well, since the
        // app does not stop it for every one of these. An alarm that turned itself off when
        // it fired (Temporary) keeps sounding, as in LoopFollow, until it is snoozed or handled.
        var active = device.ActiveAlarm;
        var selfDisabled = active is { DisabledWhenFired: true } ? active.AlarmId : (Guid?)null;
        if (active is not null && IsSilenced(active.AlarmId, alarms, config, now, selfDisabled))
        {
            _log.LogInformation("Alarm {Alarm} on {Device} silenced from the app", active.AlarmId, device.Id);
            if (active.Ring) stops.Add((active, AlarmStopReason.Silenced));
            active = null;
            selfDisabled = null;
        }
        if (alarmState.CurrentAlarmId is { } current && IsSilenced(current, alarms, config, now, selfDisabled))
            alarmState.CurrentAlarmId = null;

        alarmState.LastBGChecked = state.LastBgCheck;
        alarmState.LastLoopingChecked = state.LastDeviceStatusCheck;

        AlarmEvaluation? evaluation = null;
        IReadOnlyList<Guid> disable = [];
        if (state.BuildAlarmData(settings, now) is { } data)
        {
            // LoopFollow's Auto-Snooze CGM Start: Snooze All for the first two hours of a sensor.
            if (AlarmSnooze.AutoSnoozeOnCgmStart(config, data.SageInsertTime, now) is { } until)
                config.SnoozeUntil = until;

            var cgmState = state.BuildSnapshot(settings, now)?["cgmSensorState"]?.GetValue<string>();
            // The evaluator stops a sounding alarm that is off, so one that turned itself off
            // is left out of the check and stays the current alarm.
            var evaluated = selfDisabled is { } sounding ? alarms.Where(a => a.Id != sounding).ToList() : alarms;
            evaluation = AlarmEvaluator.Evaluate(evaluated, config, data, alarmState, now, settings.TimeZone,
                settings.Mmol, new AlarmTextContext(state.LatestDirection, cgmState));
            alarmState = evaluation.State;
            disable = evaluation.DisableAlarms;

            if (evaluation.StoppedAlarmId is { } stopped && active?.AlarmId == stopped)
                active = active with { ConditionCleared = true };
        }

        ActiveAlarm? send = null;
        if (evaluation?.Fire is { } fire)
        {
            // LoopFollow sounds one alarm at a time: a different alarm firing ends the sounding one.
            if (active is not null && active.AlarmId != fire.AlarmId && active.Ring)
            {
                _log.LogInformation("Alarm {Alarm} on {Device} stopped: {Next} fired", active.AlarmId, device.Id,
                    fire.AlarmId);
                stops.Add((active, AlarmStopReason.RingDone));
            }
            _log.LogInformation(
                "Alarm {Alarm} fired on {Device}: {Type} sound={PlaySound} repeat={Repeat} phase={Phase}",
                fire.AlarmId, device.Id, fire.Type, fire.PlaySound, fire.RepeatSound, fire.IsPhaseChange);
            // Muted (or day/night says no sound): LoopFollow still shows the notification
            // with the default sound, and plays no alarm sound.
            active = send = new ActiveAlarm(fire.AlarmId, $"{fire.Type.Emoji()} {fire.Title}", fire.Subtitle, fire.Body,
                fire.PlaySound ? NotificationSoundName(fire.SoundFile) : "default", fire.Type.SymbolName(),
                fire.ActionTitle == "Acknowledge" ? "lfhub-ack" : "category", fire.PlaySound,
                fire.PlaySound && fire.RepeatSound, now, now, null,
                DisabledWhenFired: disable.Contains(fire.AlarmId));
        }
        else if (active is not null && !active.ConditionCleared && (active.Repeat || active.DeliveredAt is null) &&
                 now - active.LastPushAt >= AlarmRepeatInterval)
        {
            // A repeating alarm is delivered again; any other alarm until the phone confirms one of its pushes.
            active = send = active with { LastPushAt = now };
        }

        if (active is not null && HasRungEnough(active, now))
        {
            _log.LogInformation("Alarm {Alarm} on {Device} stopped: {Reason}", active.AlarmId, device.Id,
                active.ConditionCleared ? "condition cleared, or Snooze All began on a sensor start" : "single ring done");
            if (active.Ring)
                stops.Add((active, active.ConditionCleared ? AlarmStopReason.Cleared : AlarmStopReason.RingDone));
            active = null;
        }

        if (!await _store.SaveAlarmEvaluationAsync(device.Id, device.AlarmRev, JsonSerializer.Serialize(alarmState),
                active, disable))
        {
            _log.LogInformation("Alarm check on {Device} not saved and nothing sent: the app handled an alarm or uploaded alarms meanwhile",
                device.Id);
            return;
        }

        foreach (var (alarm, reason) in stops)
            await StopRingingAsync(device, alarm, reason, ct);
        if (send is not null)
            await DeliverAlarmAsync(device, send, ct);

        if (evaluation is not null && evaluation.Fire is null && settings.PersistentNotification)
            await LatestBgAsync(device, state, settings, now, ct);
    }

    /// <summary>
    /// Alarms were turned off while an alarm sounds: clears it as the sounding and the current
    /// alarm, then stops its system alarm.
    /// </summary>
    private async Task StopForAlarmsOffAsync(Device device, ActiveAlarm sounding, CancellationToken ct)
    {
        var alarmState = device.AlarmStateJson is { } json
            ? JsonSerializer.Deserialize<AlarmState>(json) ?? new AlarmState()
            : new AlarmState();
        alarmState.CurrentAlarmId = null;
        alarmState.CurrentAlarmTitleOverride = null;
        if (!await _store.SaveAlarmEvaluationAsync(device.Id, device.AlarmRev, JsonSerializer.Serialize(alarmState),
                null, []))
            return;
        _log.LogInformation("Alarm {Alarm} on {Device} stopped: alarms turned off", sounding.AlarmId, device.Id);
        if (sounding.Ring && device.Apns is not null)
            await StopRingingAsync(device, sounding, AlarmStopReason.Silenced, ct);
    }

    /// <summary>
    /// Sends the device a heartbeat at its first check with alarms on and APNs registered,
    /// then every 20 minutes while that holds. The time of the last one is kept in memory.
    /// </summary>
    private async Task HeartbeatAsync(Device device, CancellationToken ct)
    {
        if (!device.AlarmsEnabled || device.Apns is null)
        {
            _lastHeartbeat.TryRemove(device.Id, out _);
            return;
        }
        var now = UtcNow;
        if (_lastHeartbeat.TryGetValue(device.Id, out var last) && now - last < HeartbeatInterval) return;
        _lastHeartbeat[device.Id] = now;
        var result = await _apns.SendHeartbeatAsync(device.Apns, now, ct);
        await TrackPushAsync(device.Id, result, PushKind.Device);
        if (!result.Delivered)
            _log.LogWarning("Heartbeat to {Device} failed: {Status}", device.Id, result.Status);
    }

    /// <summary>
    /// The persistent "Latest BG" notification, sent once per new reading when no
    /// alarm fired, as AlarmManager.checkAlarms does it.
    /// </summary>
    private async Task LatestBgAsync(Device device, PatientState state, ClientSettings settings, DateTime now,
        CancellationToken ct)
    {
        if (state.LatestReading is not { } reading || now - reading.At > RecentReading) return;
        if (_latestBgNotified.TryGetValue(device.Id, out var last) && reading.At <= last) return;
        if (state.BuildAlarmData(settings, now) is not { } data) return;

        _latestBgNotified[device.Id] = reading.At;
        var result = await _apns.SendLatestBgAsync(device.Apns!,
            AlarmEvaluator.Subtitle(data, settings.Mmol, state.LatestDirection), ct);
        await TrackPushAsync(device.Id, result, PushKind.Device);
        if (!result.Delivered)
            _log.LogWarning("Latest BG notification to {Device} failed: {Status}", device.Id, result.Status);
    }

    /// <summary>
    /// LoopFollow's Speak BG for a device with alarms on and APNs registered: each new, recent
    /// reading that meets a speak condition is sent once as a speak push. The newest reading a
    /// device has had is kept in memory; a device seen for the first time since the hub started,
    /// or whose speech was off, starts from the current reading without speaking it, so a
    /// restarted hub repeats none.
    /// </summary>
    private async Task SpeakAsync(Device device, PatientState state, ClientSettings settings, CancellationToken ct)
    {
        if (!device.AlarmsEnabled || device.Apns is null || !settings.Speak || !device.HasFeature(HubInfo.Speak))
        {
            _lastSpoken.TryRemove(device.Id, out _);
            return;
        }
        if (state.LatestReading is not { } reading) return;
        if (!_lastSpoken.TryGetValue(device.Id, out var last))
        {
            _lastSpoken[device.Id] = reading.At;
            return;
        }
        if (reading.At <= last) return;
        _lastSpoken[device.Id] = reading.At;

        var now = UtcNow;
        if (now - reading.At > RecentReading) return;
        if (state.BuildAlarmData(settings, now) is not { BgReadings: [.., var previous, var current] } data) return;
        if (SpeakReason(settings, current.Sgv, previous.Sgv, data.PredictionData) is not { } reason) return;

        var result = await _apns.SendSpeakAsync(device.Apns,
            AlarmEvaluator.Subtitle(data, settings.Mmol, state.LatestDirection), current.Sgv, previous.Sgv,
            settings.SpeakLanguage, settings.Mmol, now, ct);
        await TrackPushAsync(device.Id, result, PushKind.Device);
        if (result.Delivered)
            _log.LogInformation("Speech sent to {Device}: {Reason}", device.Id, reason);
        else
            _log.LogWarning("Speech to {Device} failed: {Status}", device.Id, result.Status);
    }

    /// <summary>
    /// The speak condition a reading meets, in SpeakBG.evaluateSpeakConditions' order, or null.
    /// The limits are truncated to whole mg/dL as Swift's Int() does; the first forecast value
    /// is compared with the low limit as is.
    /// </summary>
    internal static string? SpeakReason(ClientSettings settings, int current, int previous,
        IReadOnlyList<GlucoseValue> forecast)
    {
        var low = (int)settings.SpeakLowLimit;
        var high = (int)settings.SpeakHighLimit;
        if (settings.SpeakAlways) return "Always";
        if (settings.SpeakLow && (current <= low || previous <= low)) return "Low";
        if (settings.SpeakProactiveLow &&
            ((forecast.Count > 0 && forecast[0].Sgv <= settings.SpeakLowLimit) ||
             current <= low || previous <= low ||
             (current <= high && previous - current >= (int)settings.SpeakFastDropDelta)))
            return "Proactive Low";
        if (settings.SpeakHigh && (current >= high || previous >= high)) return "High";
        return null;
    }

    /// <summary>
    /// Whether the sounding alarm is done: its condition cleared and it has rung for the minimum,
    /// or its sound does not repeat and the phone received it at least <see cref="SingleRing"/> ago.
    /// </summary>
    internal static bool HasRungEnough(ActiveAlarm alarm, DateTime now) =>
        (alarm.ConditionCleared && now - alarm.FiredAt >= MinimumRing) ||
        (!alarm.Repeat && alarm.DeliveredAt is { } delivered && now - delivered >= SingleRing);

    /// <param name="selfDisabled">An alarm that turned itself off when it fired; being off does not silence it.</param>
    private static bool IsSilenced(Guid alarmId, IReadOnlyList<Alarm> alarms, AlarmConfiguration config, DateTime now,
        Guid? selfDisabled)
    {
        if (config.SnoozeUntil > now) return true;
        var alarm = alarms.FirstOrDefault(a => a.Id == alarmId);
        return alarm is null || (!alarm.IsEnabled && alarmId != selfDisabled) || alarm.SnoozedUntil > now;
    }

    /// <summary>
    /// Sends the alarm push and records its outcome on the alarm. When the device's sounding
    /// alarm is not this one by then (the app handled it while the push was on its way), its
    /// system alarm is stopped again, since the push may have started it anew.
    /// </summary>
    private async Task DeliverAlarmAsync(Device device, ActiveAlarm alarm, CancellationToken ct)
    {
        var receipt = await NewReceiptAsync(device.Id, alarm.AlarmId, alarm.FiredAt);
        var result = await _apns.SendAlarmAsync(device.Apns!, alarm.AlarmId, alarm.Title, alarm.Subtitle, alarm.Body,
            alarm.Sound, alarm.Symbol, alarm.Category, device.CriticalAlerts, alarm.Ring, !alarm.Repeat,
            receipt, ct);
        await TrackPushAsync(device.Id, result, PushKind.Alarm);
        if (result.Delivered)
            _log.LogInformation("Alarm {Alarm} delivered to {Device}", alarm.AlarmId, device.Id);
        else
            _log.LogWarning("Alarm {Alarm} delivery to {Device} failed: {Status}", alarm.AlarmId, device.Id, result.Status);

        if (await _store.RecordAlarmPushAsync(device.Id, alarm.AlarmId, alarm.FiredAt, UtcNow, result.Status)) return;
        _log.LogInformation("Alarm {Alarm} on {Device} was handled while its push was sent", alarm.AlarmId, device.Id);
        if (alarm.Ring && result.Delivered)
            await StopRingingAsync(device, alarm, AlarmStopReason.Silenced, ct);
    }

    /// <summary>
    /// A fresh delivery receipt token for one alarm push, kept by its hash with the device and the
    /// firing (<paramref name="alarmId"/> fired at <paramref name="firedAt"/>). A failure to keep it
    /// is logged and the token is sent all the same: the push matters more, and a token the hub
    /// does not know leaves the alarm without a receipt, so it is sent again.
    /// </summary>
    internal async Task<string> NewReceiptAsync(Guid deviceId, Guid alarmId, DateTime firedAt)
    {
        var token = AlarmReceipts.NewToken();
        try
        {
            await _store.IssueReceiptAsync(AlarmReceipts.Hash(token), deviceId, alarmId, firedAt, UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Keeping a delivery receipt for alarm {Alarm} on {Device} failed: {Message}", alarmId,
                deviceId, ex.Message);
        }
        return token;
    }

    /// <summary>
    /// Stops the system alarm still ringing on the phone; the notification service
    /// extension acts on the push.
    /// </summary>
    private async Task StopRingingAsync(Device device, ActiveAlarm alarm, AlarmStopReason reason, CancellationToken ct)
    {
        var result = await _apns.SendAlarmStopAsync(device.Apns!, alarm.AlarmId, alarm.Title, alarm.Subtitle,
            alarm.Body, reason, ct);
        await TrackPushAsync(device.Id, result, PushKind.Device);
        if (result.Delivered)
        {
            _failedStops.TryRemove((device.Id, alarm.AlarmId), out _);
            return;
        }
        _log.LogWarning("Alarm {Alarm} stop to {Device} failed: {Status}", alarm.AlarmId, device.Id, result.Status);
        _failedStops.AddOrUpdate((device.Id, alarm.AlarmId), (alarm, reason, UtcNow),
            (_, earlier) => earlier with { Alarm = alarm, Reason = reason });
    }

    /// <summary>
    /// Sends again each stop APNs did not accept for the device, for <see cref="StopRetryFor"/>;
    /// one whose alarm is sounding again is dropped.
    /// </summary>
    private async Task RetryFailedStopsAsync(Device device, DateTime now, CancellationToken ct)
    {
        foreach (var (key, failed) in _failedStops.Where(f => f.Key.Device == device.Id).ToList())
        {
            if (device.ActiveAlarm?.AlarmId == key.Alarm || now - failed.Since > StopRetryFor)
            {
                if (now - failed.Since > StopRetryFor)
                    _log.LogWarning("Alarm {Alarm} stop to {Device} given up after {Minutes} minutes", key.Alarm,
                        device.Id, StopRetryFor.TotalMinutes);
                _failedStops.TryRemove(key, out _);
                continue;
            }
            await StopRingingAsync(device, failed.Alarm, failed.Reason, ct);
        }
    }

    /// The file name the app gives its converted copy of an alarm sound (HubAlarmSounds.notificationName).
    private static string NotificationSoundName(SoundFile sound) => sound.BuiltinName is { } name
        ? $"lfhub-{name}.caf"
        : $"lfhub-{sound.CustomId!.Value.ToString().ToUpperInvariant()}.caf";
}
