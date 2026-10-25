using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Db;
using LFHub.Engine.Alarms;
using LFHub.Engine.State;

namespace LFHub.Tests.Db;

/// HubStore's SQL against a real Postgres (see PostgresFixture).
public class HubStoreTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Deadline = new(2026, 10, 24, 20, 0, 0, DateTimeKind.Utc);

    private async Task<(HubStore Store, Guid PatientId, Guid DeviceId, string Key)> DeviceAsync()
    {
        var store = db.Store;
        var patientId = (await store.PatientAsync())?.Id ?? await store.CreatePatientAsync("Test patient");
        var (deviceId, key) = await store.CreateDeviceAsync(patientId, "Test phone", Roles.Admin);
        return (store, patientId, deviceId, key);
    }

    private static async Task<Device> ReadAsync(HubStore store, Guid patientId, Guid deviceId) =>
        (await store.DevicesForPatientAsync(patientId)).Single(d => d.Id == deviceId);

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        db.Store.Migrate();
        db.Store.Migrate();
        Assert.Equal(HubStore.SchemaVersion, await db.ScalarAsync<int>("SELECT max(version) FROM lfhub.schema_version"));
        Assert.Equal(HubStore.SchemaVersion, (int)await db.ScalarAsync<long>("SELECT count(*) FROM lfhub.schema_version"));
    }

    [Fact]
    public async Task DeviceKey_IsStoredOnlyAsItsHash()
    {
        var (store, _, deviceId, key) = await DeviceAsync();
        Assert.StartsWith("lfh_", key, StringComparison.Ordinal);
        var stored = await db.ScalarAsync<string>("SELECT key_hash FROM lfhub.devices WHERE id = @id", ("id", deviceId));
        Assert.NotEqual(key, stored);
        Assert.Equal(64, stored!.Length);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM lfhub.devices WHERE key_hash LIKE '%' || @k || '%'", ("k", key)));
    }

    [Fact]
    public async Task Authenticate_ResolvesTheKeyAndStampsTheDeviceSeen()
    {
        var (store, patientId, deviceId, key) = await DeviceAsync();
        Assert.Null(await store.AuthenticateAsync(key + "x"));
        var device = await store.AuthenticateAsync(key);
        Assert.Equal(deviceId, device?.Id);
        Assert.Equal(patientId, device?.PatientId);
        Assert.NotNull(device?.LastSeenAt);
    }

    [Fact]
    public async Task Sources_AreSealedAtRest()
    {
        var (store, patientId, _, _) = await DeviceAsync();
        await store.SetSourceAsync(patientId, "https://ns.example.org", "plain-token-value",
            new DexcomCredentials("dexcom-user", "dexcom-secret", "US"));
        var tokenColumn = await db.ScalarAsync<string>("SELECT ns_token_sealed FROM lfhub.patients WHERE id = @id", ("id", patientId));
        var dexcomColumn = await db.ScalarAsync<string>("SELECT dexcom_sealed FROM lfhub.patients WHERE id = @id", ("id", patientId));
        Assert.DoesNotContain("plain-token-value", tokenColumn, StringComparison.Ordinal);
        Assert.DoesNotContain("dexcom-secret", dexcomColumn, StringComparison.Ordinal);
        Assert.DoesNotContain("dexcom-user", dexcomColumn, StringComparison.Ordinal);

        var patient = await store.GetPatientAsync(patientId);
        Assert.Equal("plain-token-value", patient?.NightscoutToken);
        Assert.Equal(new DexcomCredentials("dexcom-user", "dexcom-secret", "US"), patient?.Dexcom);
        Assert.Contains(await store.PatientsWithSourceAsync(), p => p.Id == patientId);

        await store.SetSourceAsync(patientId, null, "ignored", null);
        Assert.DoesNotContain(await store.PatientsWithSourceAsync(), p => p.Id == patientId);
        Assert.Null((await store.GetPatientAsync(patientId))?.NightscoutToken);
    }

    [Fact]
    public async Task LastReading_IsKeptWithThePatient()
    {
        var (store, patientId, _, _) = await DeviceAsync();
        await store.SetSourceAsync(patientId, "https://ns.example.org", null, null);
        await store.SaveLastReadingAsync(patientId, Deadline, 121);
        Assert.Equal((Deadline, 121), (await store.GetPatientAsync(patientId))?.LastReading);
        await store.SaveLastReadingAsync(patientId, Deadline.AddMinutes(5), 125);
        Assert.Equal((Deadline.AddMinutes(5), 125), (await store.PatientsWithSourceAsync()).Single(p => p.Id == patientId).LastReading);
        Assert.Equal((Deadline.AddMinutes(5), 125), (await store.PatientAsync())?.LastReading);
    }

    [Fact]
    public async Task Apns_KeyIsSealedAndKeptWhenNotResent()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var token = $"apns-{Guid.NewGuid():N}";
        await store.SetApnsAsync(deviceId, token, "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "p8-key-body");
        var sealedKey = await db.ScalarAsync<string>("SELECT apns_key_sealed FROM lfhub.devices WHERE id = @id", ("id", deviceId));
        Assert.DoesNotContain("p8-key-body", sealedKey, StringComparison.Ordinal);

        await store.SetApnsAsync(deviceId, token + "-new", "production", "org.example.app", "TEAM000000", "KEY0000000", null);
        var device = await ReadAsync(store, patientId, deviceId);
        Assert.Equal("p8-key-body", device.Apns?.Key);
        Assert.Equal(token + "-new", device.Apns?.Token);
        Assert.Equal("production", device.Apns?.Environment);
    }

    [Fact]
    public async Task Apns_RegistrationRemovesOtherDevicesWithTheSameToken()
    {
        var (store, patientId, oldDevice, _) = await DeviceAsync();
        var (otherBundle, _) = await store.CreateDeviceAsync(patientId, "Other app", Roles.Admin);
        var (newDevice, _) = await store.CreateDeviceAsync(patientId, "Reinstalled", Roles.Admin);
        var token = $"apns-{Guid.NewGuid():N}";
        await store.SetApnsAsync(oldDevice, token, "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "k");
        await store.SetApnsAsync(otherBundle, token, "sandbox", "org.example.other", "TEAM000000", "KEY0000000", "k");
        await store.SetApnsAsync(newDevice, token, "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "k");

        var ids = (await store.DevicesForPatientAsync(patientId)).Select(d => d.Id).ToList();
        Assert.DoesNotContain(oldDevice, ids);
        Assert.Contains(newDevice, ids);
        Assert.Contains(otherBundle, ids);
    }

    [Fact]
    public async Task EngineLock_IsHeldByOneStoreAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = new HubStore(db.DataSource, new SecretBox(Convert.ToBase64String(new byte[32])));
        var first = await db.Store.TryLockEngineAsync(ct);
        Assert.NotNull(first);
        Assert.True(await first.IsHeldAsync(ct));
        Assert.Null(await other.TryLockEngineAsync(ct));

        await first.DisposeAsync();
        await using var second = await other.TryLockEngineAsync(ct);
        Assert.NotNull(second);
        Assert.Null(await db.Store.TryLockEngineAsync(ct));
    }

    [Fact]
    public async Task Prune_RemovesOnlyDevicesUnseenForTheGivenAgeWhoseTokenIsGone()
    {
        var (store, patientId, gone, _) = await DeviceAsync();
        var (reachable, _) = await store.CreateDeviceAsync(patientId, "Reachable", Roles.Admin);
        var (seen, _) = await store.CreateDeviceAsync(patientId, "Seen", Roles.Admin);
        var (reregistered, _) = await store.CreateDeviceAsync(patientId, "Reregistered", Roles.Admin);
        var at = DateTime.UtcNow;
        await store.RecordPushOutcomeAsync(gone, at, false, false, true);
        await store.RecordPushOutcomeAsync(reachable, at, false, false, true);
        await store.RecordPushOutcomeAsync(reachable, at, true, false, false);
        await store.RecordPushOutcomeAsync(seen, at, false, false, true);
        await store.RecordPushOutcomeAsync(reregistered, at, false, false, true);
        await store.SetApnsAsync(reregistered, $"apns-{Guid.NewGuid():N}", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "k");
        foreach (var id in new[] { gone, reachable, reregistered })
            await db.ExecuteAsync("UPDATE lfhub.devices SET last_seen_at = now() - interval '31 days' WHERE id = @id", ("id", id));

        Assert.Equal(1, await store.PruneUnseenDevicesAsync(TimeSpan.FromDays(30)));
        var ids = (await store.DevicesForPatientAsync(patientId)).Select(d => d.Id).ToList();
        Assert.DoesNotContain(gone, ids);
        Assert.Contains(reachable, ids);
        Assert.Contains(seen, ids);
        Assert.Contains(reregistered, ids);
    }

    [Fact]
    public async Task Settings_AreMerged()
    {
        var (store, _, deviceId, _) = await DeviceAsync();
        await store.MergeSettingsAsync(deviceId, new JsonObject { ["badge"] = true, ["unit"] = "mmol" });
        var merged = await store.MergeSettingsAsync(deviceId, new JsonObject { ["unit"] = "mgdl" });
        Assert.True(merged["badge"]!.GetValue<bool>());
        Assert.Equal("mgdl", merged["unit"]!.GetValue<string>());
    }

    [Fact]
    public async Task AlarmHandled_ClearsTheActiveAlarmAndTheCurrentAlarmWithItsTitle()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var alarmId = Guid.NewGuid();
        var state = new AlarmState
        {
            CurrentAlarmId = alarmId,
            CurrentAlarmTitleOverride = "Low until 14:00",
            LastBGAlarmTime = Deadline,
            LastCOBNotified = 12,
        };
        var active = new ActiveAlarm(alarmId, "Low", "70", null, "default", "bell", "category", true, true, Deadline, Deadline, "200");
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, JsonSerializer.Serialize(state), active, []));

        await store.AlarmHandledAsync(deviceId, Guid.NewGuid());
        var untouched = await ReadAsync(store, patientId, deviceId);
        Assert.Equal(active, untouched.ActiveAlarm);
        Assert.Equal(alarmId, JsonSerializer.Deserialize<AlarmState>(untouched.AlarmStateJson!)!.CurrentAlarmId);

        await store.AlarmHandledAsync(deviceId, alarmId);
        Assert.False(await store.SaveAlarmEvaluationAsync(deviceId, 1, JsonSerializer.Serialize(state), active, []));
        var handled = await ReadAsync(store, patientId, deviceId);
        Assert.Equal(2, handled.AlarmRev);
        Assert.Null(handled.ActiveAlarm);
        var after = JsonSerializer.Deserialize<AlarmState>(handled.AlarmStateJson!)!;
        Assert.Null(after.CurrentAlarmId);
        Assert.Null(after.CurrentAlarmTitleOverride);
        Assert.Equal(Deadline, after.LastBGAlarmTime);
        Assert.Equal(12, after.LastCOBNotified);
    }

    [Fact]
    public async Task AlarmHandled_WithoutStoredStateIsHarmless()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        await store.AlarmHandledAsync(deviceId, Guid.NewGuid());
        var device = await ReadAsync(store, patientId, deviceId);
        Assert.Null(device.ActiveAlarm);
        Assert.Null(device.AlarmStateJson);
    }

    [Fact]
    public async Task HubDisabledAlarms_LeaveTheListOnceTheExportShowsThemDisabled()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", null, [a]));
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", null, [a, b]));
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", null, []));
        Assert.Equal(new[] { a, b }.Order(), (await ReadAsync(store, patientId, deviceId)).HubDisabledAlarms.Order());

        await store.SetAlarmsAsync(deviceId, "{}", [a]);
        var device = await ReadAsync(store, patientId, deviceId);
        Assert.Equal([b], device.HubDisabledAlarms);
        Assert.Equal("{}", device.AlarmsJson);
        await store.SetAlarmsAsync(deviceId, "{}", [b]);
        Assert.Empty((await ReadAsync(store, patientId, deviceId)).HubDisabledAlarms);
    }

    [Fact]
    public async Task AlarmUpload_MovesTheRevisionSoAnEarlierEvaluationIsNotSaved()
    {
        var (store, _, deviceId, _) = await DeviceAsync();
        var read = (await store.DeviceAsync(deviceId))!;
        await store.SetAlarmsAsync(deviceId, "{}", []);
        Assert.False(await store.SaveAlarmEvaluationAsync(deviceId, read.AlarmRev, "{}", null, [Guid.NewGuid()]));
        var device = (await store.DeviceAsync(deviceId))!;
        Assert.Equal(read.AlarmRev + 1, device.AlarmRev);
        Assert.Null(device.AlarmStateJson);
        Assert.Empty(device.HubDisabledAlarms);
        Assert.Null(await store.DeviceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AlarmPush_IsRecordedOnTheFiringItWasSentFor()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var alarmId = Guid.NewGuid();
        var firedAt = new DateTime(2026, 10, 24, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        var active = new ActiveAlarm(alarmId, "Low", "70", null, "default", "bell", "category", true, true, firedAt, firedAt, null);
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", active, []));

        Assert.False(await store.RecordAlarmPushAsync(deviceId, Guid.NewGuid(), firedAt, Deadline, "200"));
        Assert.False(await store.RecordAlarmPushAsync(deviceId, alarmId, firedAt.AddSeconds(-60), Deadline, "200"));
        Assert.Equal(active, (await ReadAsync(store, patientId, deviceId)).ActiveAlarm);

        Assert.True(await store.RecordAlarmPushAsync(deviceId, alarmId, firedAt, Deadline, "200"));
        Assert.Equal(active with { LastPushAt = Deadline, LastPushStatus = "200" },
            (await ReadAsync(store, patientId, deviceId)).ActiveAlarm);

        await store.AlarmHandledAsync(deviceId, alarmId);
        Assert.False(await store.RecordAlarmPushAsync(deviceId, alarmId, firedAt, Deadline, "200"));
        Assert.Null((await ReadAsync(store, patientId, deviceId)).ActiveAlarm);
    }

    [Fact]
    public async Task Receipt_RecordsDeliveryOnceOnTheFiringItWasSentForAndIsKeptByALaterSave()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var alarmId = Guid.NewGuid();
        var firedAt = new DateTime(2026, 10, 24, 12, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);
        var active = new ActiveAlarm(alarmId, "Low", "70", null, "default", "bell", "category", true, false, firedAt, firedAt, null);
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", active, []));
        var (earlier, first, second) = ("e".PadLeft(64, '0'), "1".PadLeft(64, '0'), "2".PadLeft(64, '0'));
        await store.IssueReceiptAsync(earlier, deviceId, alarmId, firedAt.AddMinutes(-5), firedAt);
        await store.IssueReceiptAsync(first, deviceId, alarmId, firedAt, firedAt);
        await store.IssueReceiptAsync(second, deviceId, alarmId, firedAt, firedAt.AddMinutes(1));

        Assert.Equal((deviceId, alarmId, false), await store.RedeemReceiptAsync(earlier, firedAt.AddSeconds(5)));
        Assert.Null((await ReadAsync(store, patientId, deviceId)).ActiveAlarm!.DeliveredAt);
        Assert.Equal((deviceId, alarmId, true), await store.RedeemReceiptAsync(first, firedAt.AddSeconds(6)));
        Assert.Null(await store.RedeemReceiptAsync(first, firedAt.AddSeconds(7)));
        Assert.Equal((deviceId, alarmId, false), await store.RedeemReceiptAsync(second, firedAt.AddMinutes(2)));
        Assert.Equal(firedAt.AddSeconds(6), (await ReadAsync(store, patientId, deviceId)).ActiveAlarm!.DeliveredAt);

        // A check that read the alarm before the receipt keeps it; a new firing does not inherit it.
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", active with { LastPushAt = firedAt.AddMinutes(1) }, []));
        Assert.Equal(firedAt.AddSeconds(6), (await ReadAsync(store, patientId, deviceId)).ActiveAlarm!.DeliveredAt);
        Assert.True(await store.SaveAlarmEvaluationAsync(deviceId, 0, "{}", active with { FiredAt = firedAt.AddMinutes(2) }, []));
        Assert.Null((await ReadAsync(store, patientId, deviceId)).ActiveAlarm!.DeliveredAt);
    }

    [Fact]
    public async Task Receipt_ExpiresAndExpiredOnesAreRemovedWhenANewOneIsKept()
    {
        var (store, _, deviceId, _) = await DeviceAsync();
        var at = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var (old, fresh) = ("a".PadLeft(64, '0'), "b".PadLeft(64, '0'));
        await store.IssueReceiptAsync(old, deviceId, Guid.NewGuid(), at, at);
        Assert.Null(await store.RedeemReceiptAsync(old, at + LFHub.Engine.AlarmReceipts.Lifetime));
        await store.IssueReceiptAsync(fresh, deviceId, Guid.NewGuid(), at, at + LFHub.Engine.AlarmReceipts.Lifetime);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM lfhub.alarm_receipts WHERE token_hash = @h", ("h", old)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM lfhub.alarm_receipts WHERE token_hash = @h", ("h", fresh)));
    }

    [Fact]
    public async Task Badge_PushRecordsKeepTheLastDeliveredValue()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        await store.RecordPushAsync(deviceId, 120, "200", true);
        await store.RecordPushAsync(deviceId, 130, "500 InternalServerError", false);
        var device = await ReadAsync(store, patientId, deviceId);
        Assert.Equal(120, device.LastBadge);
        Assert.Equal("500 InternalServerError", device.LastPushStatus);
        await store.ClearLastBadgeAsync(deviceId);
        Assert.Null((await ReadAsync(store, patientId, deviceId)).LastBadge);
    }

    [Fact]
    public async Task PushOutcomes_TrackTheLastAcceptedPushAndWhenFailuresBegan()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var device = await ReadAsync(store, patientId, deviceId);
        Assert.Null(device.LastPushOkAt);
        Assert.Null(device.PushFailingSince);
        Assert.Null(device.LastAlarmPushOk);

        Assert.Null(await store.RecordPushOutcomeAsync(deviceId, Deadline, true, false, false));
        Assert.Equal(Deadline.AddMinutes(1), await store.RecordPushOutcomeAsync(deviceId, Deadline.AddMinutes(1), false, true, false));
        Assert.Equal(Deadline.AddMinutes(1), await store.RecordPushOutcomeAsync(deviceId, Deadline.AddMinutes(2), false, false, false));
        device = await ReadAsync(store, patientId, deviceId);
        Assert.Equal(Deadline, device.LastPushOkAt);
        Assert.Equal(Deadline.AddMinutes(1), device.PushFailingSince);
        Assert.False(device.LastAlarmPushOk);

        Assert.Null(await store.RecordPushOutcomeAsync(deviceId, Deadline.AddMinutes(3), true, false, false));
        device = await ReadAsync(store, patientId, deviceId);
        Assert.Equal(Deadline.AddMinutes(3), device.LastPushOkAt);
        Assert.Null(device.PushFailingSince);
        Assert.False(device.LastAlarmPushOk);

        await store.RecordPushOutcomeAsync(deviceId, Deadline.AddMinutes(4), true, true, false);
        Assert.True((await ReadAsync(store, patientId, deviceId)).LastAlarmPushOk);
    }

    [Fact]
    public async Task ReportLiveActivity_TakesRenewByOnlyWithANewToken()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        await store.ReportLiveActivityAsync(deviceId, "activity-1", "start-1", Deadline);
        var la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Equal("activity-1", la.Token);
        Assert.Equal("start-1", la.PushToStartToken);
        Assert.Equal(Deadline, la.RenewBy);

        // The same token, or none, with a stale deadline leaves the stored one.
        await store.ReportLiveActivityAsync(deviceId, "activity-1", null, Deadline.AddHours(-8));
        await store.ReportLiveActivityAsync(deviceId, null, null, Deadline.AddHours(-8));
        la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Equal(Deadline, la.RenewBy);
        Assert.Equal("activity-1", la.Token);
        Assert.Equal("start-1", la.PushToStartToken);

        await store.ReportLiveActivityAsync(deviceId, "activity-2", "", Deadline.AddHours(7.5));
        la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Equal("activity-2", la.Token);
        Assert.Equal(Deadline.AddHours(7.5), la.RenewBy);
        Assert.Equal("start-1", la.PushToStartToken);
    }

    [Fact]
    public async Task ReportLiveActivity_NewTokenWithoutRenewByMovesAPassedDeadline()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        var future = DateTime.UtcNow.AddHours(3);
        await store.ReportLiveActivityAsync(deviceId, "activity-1", "start-1", future);
        await store.ReportLiveActivityAsync(deviceId, "activity-2", null, null);
        Assert.Equal(future, (await ReadAsync(store, patientId, deviceId)).LiveActivity.RenewBy!.Value, TimeSpan.FromMilliseconds(1));

        await store.ReportLiveActivityAsync(deviceId, "activity-3", null, Deadline.AddYears(-1));
        var before = DateTime.UtcNow;
        await store.ReportLiveActivityAsync(deviceId, "activity-4", null, null);
        var renewBy = (await ReadAsync(store, patientId, deviceId)).LiveActivity.RenewBy!.Value;
        Assert.InRange(renewBy, before.AddHours(7.5).AddSeconds(-5), DateTime.UtcNow.AddHours(7.5).AddSeconds(5));
    }

    [Fact]
    public async Task ReportLiveActivity_NewTokenClearsFailureAndFingerprint_EmptyTokenEndsIt()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        await store.ReportLiveActivityAsync(deviceId, "activity-1", "start-1", Deadline);
        await store.RecordLiveActivityPushAsync(deviceId, "fingerprint", "200", true, false);
        await store.RecordLiveActivityRenewalAsync(deviceId, false, "renewal: no push-to-start token", false);
        var la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.True(la.RenewalFailed);
        Assert.Null(la.LastFingerprint);
        Assert.Equal(Deadline, la.RenewBy);

        await store.RecordLiveActivityPushAsync(deviceId, "fingerprint", "200", true, false);
        await store.ReportLiveActivityAsync(deviceId, "activity-1", null, null);
        Assert.Equal("fingerprint", (await ReadAsync(store, patientId, deviceId)).LiveActivity.LastFingerprint);

        await store.ReportLiveActivityAsync(deviceId, "activity-2", null, null);
        la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.False(la.RenewalFailed);
        Assert.Null(la.LastFingerprint);

        await store.ReportLiveActivityAsync(deviceId, "", null, null);
        Assert.Null((await ReadAsync(store, patientId, deviceId)).LiveActivity.Token);
    }

    [Fact]
    public async Task LiveActivityPushes_RecordStatusAndForgetGoneTokens()
    {
        var (store, patientId, deviceId, _) = await DeviceAsync();
        await store.ReportLiveActivityAsync(deviceId, "activity-1", "start-1", Deadline);
        await store.RecordLiveActivityPushAsync(deviceId, "f1", "200", true, false);
        await store.RecordLiveActivityPushAsync(deviceId, "f2", "500 InternalServerError", false, false);
        var la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Equal("f1", la.LastFingerprint);
        Assert.Equal("500 InternalServerError", la.LastPushStatus);
        Assert.NotNull(la.LastPushAt);

        await store.RecordLiveActivityRenewalAsync(deviceId, true, "start 200", false);
        la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Equal(Deadline, la.RenewBy);
        Assert.Equal("activity-1", la.Token);
        Assert.False(la.RenewalFailed);

        await store.RecordLiveActivityRenewalAsync(deviceId, false, "start 410 Unregistered", true);
        await store.RecordLiveActivityPushAsync(deviceId, null, "410 Unregistered", false, true);
        la = (await ReadAsync(store, patientId, deviceId)).LiveActivity;
        Assert.Null(la.PushToStartToken);
        Assert.Null(la.Token);
    }

    [Fact]
    public async Task Patients_TheHubHoldsOne()
    {
        await DeviceAsync();
        Assert.NotNull(await db.Store.PatientAsync());
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Store.CreatePatientAsync("Second patient"));
    }

    [Fact]
    public async Task DeleteDevice_RemovesIt()
    {
        var (store, patientId, deviceId, key) = await DeviceAsync();
        await store.DeleteDeviceAsync(deviceId);
        Assert.Null(await store.AuthenticateAsync(key));
        Assert.DoesNotContain(await store.DevicesForPatientAsync(patientId), d => d.Id == deviceId);
    }

    [Fact]
    public async Task SecretsSealedUnderAnotherKey_ReadAsUnsetAndCanBeSentAgain()
    {
        var (store, patientId, deviceId, key) = await DeviceAsync();
        await store.SetSourceAsync(patientId, "https://ns.example.org", "plain-token-value",
            new DexcomCredentials("dexcom-user", "dexcom-secret", "US"));
        await store.SetApnsAsync(deviceId, $"apns-{Guid.NewGuid():N}", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "p8-key-body");

        var otherKey = new HubStore(db.DataSource, new SecretBox(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        var device = await otherKey.AuthenticateAsync(key);
        Assert.NotNull(device);
        Assert.Null(device.Apns);
        var patient = await otherKey.GetPatientAsync(patientId);
        Assert.Equal("https://ns.example.org", patient?.NightscoutUrl);
        Assert.Null(patient?.NightscoutToken);
        Assert.Null(patient?.Dexcom);
        Assert.Contains(await otherKey.PatientsWithSourceAsync(), p => p.Id == patientId);

        await otherKey.SetApnsAsync(deviceId, $"apns-{Guid.NewGuid():N}", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "p8-key-body");
        Assert.Equal("p8-key-body", (await otherKey.AuthenticateAsync(key))?.Apns?.Key);
    }

    [Fact]
    public async Task SecretThatDoesNotOpen_IsLoggedOncePerRowAndCounted()
    {
        var (store, patientId, deviceId, key) = await DeviceAsync();
        await store.SetSourceAsync(patientId, "https://ns.example.org", "plain-token-value", null);
        await store.SetApnsAsync(deviceId, "a1b2c3d4", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "p8-key-body");
        // Other tests in this database leave secrets sealed under keys of their own.
        var foreign = await store.UnopenedSecretsAsync(TestContext.Current.CancellationToken);

        var logs = new Fakes.ListLoggerFactory();
        var otherKey = new HubStore(db.DataSource,
            new SecretBox(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            new Microsoft.Extensions.Logging.Logger<HubStore>(logs));
        for (var i = 0; i < 3; i++)
        {
            var device = await otherKey.AuthenticateAsync(key);
            Assert.True(device!.ApnsKeyDoesNotOpen);
            Assert.Null(device.Apns);
            await otherKey.GetPatientAsync(patientId);
        }
        Assert.Single(logs.Logger.Lines, l => l.Contains("APNs key", StringComparison.Ordinal) && l.Contains(deviceId.ToString(), StringComparison.Ordinal));
        Assert.Single(logs.Logger.Lines, l => l.Contains("Nightscout token", StringComparison.Ordinal) && l.Contains(patientId.ToString(), StringComparison.Ordinal));
        Assert.True(await otherKey.UnopenedSecretsAsync(TestContext.Current.CancellationToken) >= foreign + 2);
        Assert.False((await store.AuthenticateAsync(key))!.ApnsKeyDoesNotOpen);
    }

    [Fact]
    public async Task DexcomOnlyPatientWhoseSecretDoesNotOpen_IsSkippedAndTheEngineKeepsRunning()
    {
        var (store, patientId, _, _) = await DeviceAsync();
        await store.SetSourceAsync(patientId, null, null, new DexcomCredentials("dexcom-user", "dexcom-secret", "US"));
        var otherKey = new HubStore(db.DataSource,
            new SecretBox(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        var logs = new Fakes.ListLoggerFactory();
        var engine = new LFHub.Engine.HubEngine(otherKey, new Fakes.FakeApns(), logs, TimeProvider.System)
        {
            FollowStreams = false,
        };

        await engine.ReconcileAsync();
        Assert.Null(engine.Status(patientId));
        Assert.Contains(logs.Logger.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) &&
                                               l.Contains("is not followed", StringComparison.Ordinal));

        var ct = TestContext.Current.CancellationToken;
        await engine.StartAsync(ct);
        for (var i = 0; i < 100 && engine.LastTickAt is null; i++) await Task.Delay(50, ct);
        Assert.NotNull(engine.LastTickAt);
        Assert.False(engine.ExecuteTask?.IsFaulted);
        await engine.StopAsync(ct);
        engine.Dispose();
    }
}
