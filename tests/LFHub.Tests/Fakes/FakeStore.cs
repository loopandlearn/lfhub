using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Db;
using LFHub.Engine.Alarms;

namespace LFHub.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IHubStore"/> with the semantics of HubStore's SQL, plus the
/// writes the API makes (alarm upload, alarm handled). HubStoreTests checks the SQL
/// itself against Postgres.
/// </summary>
internal sealed class FakeStore(TimeProvider time) : IHubStore
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public List<Patient> Patients { get; } = [];
    public Dictionary<Guid, Row> Devices { get; } = [];
    public int Prunes { get; private set; }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// One device as its database row holds it.
    public sealed class Row
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Guid PatientId { get; init; }
        public JsonObject Settings { get; set; } = new();
        public ApnsConfig? Apns { get; set; }
        public bool ApnsKeyDoesNotOpen { get; set; }
        public int? LastBadge { get; set; }
        public DateTime? LastPushAt { get; set; }
        public string? LastPushStatus { get; set; }
        public string? LaToken { get; set; }
        public string? LaPushToStartToken { get; set; }
        public DateTime? LaRenewBy { get; set; }
        public bool LaRenewalFailed { get; set; }
        public string? LaLastFingerprint { get; set; }
        public DateTime? LaLastPushAt { get; set; }
        public string? LaLastPushStatus { get; set; }
        public JsonObject ClientSettings { get; set; } = new();
        public string? AlarmsJson { get; set; }
        public string? AlarmStateJson { get; set; }
        public string? ActiveAlarmJson { get; set; }
        public List<Guid> HubDisabledAlarms { get; set; } = [];
        public int AlarmRev { get; set; }
        public DateTime? LastPushOkAt { get; set; }
        public DateTime? PushFailingSince { get; set; }
        public bool? LastAlarmPushOk { get; set; }
        public bool PushTokenGone { get; set; }

        public AlarmState? AlarmState => AlarmStateJson is { } j ? JsonSerializer.Deserialize<AlarmState>(j) : null;

        public ActiveAlarm? ActiveAlarm => ActiveAlarmJson is { } j ? JsonSerializer.Deserialize<ActiveAlarm>(j, Web) : null;

        public Device ToDevice() => new()
        {
            Id = Id,
            PatientId = PatientId,
            Name = "Test phone",
            Settings = (JsonObject)Settings.DeepClone(),
            Apns = Apns,
            ApnsKeyDoesNotOpen = ApnsKeyDoesNotOpen,
            LastBadge = LastBadge,
            LastPushAt = LastPushAt,
            LastPushStatus = LastPushStatus,
            LiveActivity = new LiveActivityState
            {
                Token = LaToken,
                PushToStartToken = LaPushToStartToken,
                RenewBy = LaRenewBy,
                RenewalFailed = LaRenewalFailed,
                LastFingerprint = LaLastFingerprint,
                LastPushAt = LaLastPushAt,
                LastPushStatus = LaLastPushStatus,
            },
            ClientSettings = (JsonObject)ClientSettings.DeepClone(),
            AlarmsJson = AlarmsJson,
            AlarmStateJson = AlarmStateJson,
            ActiveAlarm = ActiveAlarm,
            HubDisabledAlarms = [.. HubDisabledAlarms],
            AlarmRev = AlarmRev,
            LastPushOkAt = LastPushOkAt,
            PushFailingSince = PushFailingSince,
            LastAlarmPushOk = LastAlarmPushOk,
        };
    }

    public Row AddDevice(Guid patientId, Action<Row>? edit = null)
    {
        var row = new Row
        {
            PatientId = patientId,
            Apns = new ApnsConfig("device-token", "sandbox", "org.example.app", "TEAM000000", "KEY0000000", "unused"),
        };
        edit?.Invoke(row);
        Devices[row.Id] = row;
        return row;
    }

    /// <summary>
    /// PUT /v1/device/alarms: the export as uploaded; hub-disabled alarms the export shows disabled
    /// leave the list, and the alarm revision moves.
    /// </summary>
    public void SetAlarms(Guid deviceId, IReadOnlyList<Alarm> alarms, AlarmConfiguration config)
    {
        var row = Devices[deviceId];
        row.AlarmsJson = AlarmSettingsJson.Serialize(alarms, config, "test");
        row.AlarmRev++;
        var disabled = alarms.Where(a => !a.IsEnabled).Select(a => a.Id).ToHashSet();
        row.HubDisabledAlarms = row.HubDisabledAlarms.Where(id => !disabled.Contains(id)).ToList();
    }

    /// PUT /v1/device/liveactivity, as HubStore.ReportLiveActivityAsync writes it.
    public void ReportLiveActivity(Guid deviceId, string? activityToken, string? pushToStartToken, DateTime? renewBy)
    {
        var row = Devices[deviceId];
        var newToken = activityToken is { Length: > 0 } && activityToken != row.LaToken;
        if (activityToken is not null && activityToken != (row.LaToken ?? "")) row.LaLastFingerprint = null;
        if (newToken)
        {
            row.LaRenewalFailed = false;
            row.LaRenewBy = renewBy ?? (row.LaRenewBy <= Now ? Now + LFHub.Engine.HubEngine.LiveActivityLifetime : row.LaRenewBy);
        }
        if (activityToken is not null) row.LaToken = activityToken.Length == 0 ? null : activityToken;
        if (pushToStartToken is { Length: > 0 }) row.LaPushToStartToken = pushToStartToken;
    }

    /// POST /v1/device/alarm/{id}/handled, as HubStore.AlarmHandledAsync writes it.
    public void AlarmHandled(Guid deviceId, Guid alarmId)
    {
        var row = Devices[deviceId];
        row.AlarmRev++;
        if (row.ActiveAlarm?.AlarmId == alarmId) row.ActiveAlarmJson = null;
        if (row.AlarmState is { } state && state.CurrentAlarmId == alarmId)
        {
            state.CurrentAlarmId = null;
            state.CurrentAlarmTitleOverride = null;
            row.AlarmStateJson = JsonSerializer.Serialize(state);
        }
    }

    public Task<List<Patient>> PatientsWithSourceAsync() => Task.FromResult(Patients.Where(p => p.HasSource).ToList());

    public Task SaveLastReadingAsync(Guid patientId, DateTime at, int mgdl)
    {
        var i = Patients.FindIndex(p => p.Id == patientId);
        if (i >= 0) Patients[i] = Patients[i] with { LastReading = (at, mgdl) };
        return Task.CompletedTask;
    }

    public Task<List<Device>> DevicesForPatientAsync(Guid patientId) =>
        Task.FromResult(Devices.Values.Where(d => d.PatientId == patientId).Select(d => d.ToDevice()).ToList());

    public Task<Device?> DeviceAsync(Guid deviceId) =>
        Task.FromResult(Devices.TryGetValue(deviceId, out var row) ? row.ToDevice() : null);

    public Task<int> PruneUnseenDevicesAsync(TimeSpan age)
    {
        Prunes++;
        return Task.FromResult(0);
    }

    public Task RecordPushAsync(Guid deviceId, int? badge, string status, bool delivered)
    {
        var row = Devices[deviceId];
        if (delivered) row.LastBadge = badge;
        row.LastPushAt = Now;
        row.LastPushStatus = status;
        return Task.CompletedTask;
    }

    public Task<DateTime?> RecordPushOutcomeAsync(Guid deviceId, DateTime at, bool accepted, bool alarm, bool tokenGone)
    {
        var row = Devices[deviceId];
        row.PushTokenGone = tokenGone;
        if (accepted) row.LastPushOkAt = at;
        row.PushFailingSince = accepted ? null : row.PushFailingSince ?? at;
        if (alarm) row.LastAlarmPushOk = accepted;
        return Task.FromResult(row.PushFailingSince);
    }

    public Task ClearLastBadgeAsync(Guid deviceId)
    {
        Devices[deviceId].LastBadge = null;
        return Task.CompletedTask;
    }

    public Task RecordLiveActivityPushAsync(Guid deviceId, string? fingerprint, string status, bool delivered, bool tokenGone)
    {
        var row = Devices[deviceId];
        row.LaLastPushAt = Now;
        row.LaLastPushStatus = status;
        if (delivered) row.LaLastFingerprint = fingerprint;
        if (tokenGone) row.LaToken = null;
        return Task.CompletedTask;
    }

    public Task RecordLiveActivityRenewalAsync(Guid deviceId, bool started, string status, bool pushToStartTokenGone)
    {
        var row = Devices[deviceId];
        row.LaLastPushAt = Now;
        row.LaLastPushStatus = status;
        row.LaRenewalFailed = !started;
        row.LaLastFingerprint = null;
        if (pushToStartTokenGone) row.LaPushToStartToken = null;
        return Task.CompletedTask;
    }

    /// Runs just before an evaluation is saved, e.g. to handle an alarm meanwhile.
    public Action? BeforeSave { get; set; }

    /// Runs just before the outcome of an alarm push is recorded.
    public Action? BeforeRecordAlarmPush { get; set; }

    public Task<bool> SaveAlarmEvaluationAsync(Guid deviceId, int readRev, string stateJson, ActiveAlarm? active,
        IReadOnlyCollection<Guid> disable)
    {
        BeforeSave?.Invoke();
        var row = Devices[deviceId];
        if (row.AlarmRev != readRev) return Task.FromResult(false);
        row.AlarmStateJson = stateJson;
        if (active is { DeliveredAt: null } && row.ActiveAlarm is { DeliveredAt: { } delivered } stored &&
            stored.AlarmId == active.AlarmId && stored.FiredAt == active.FiredAt)
            active = active with { DeliveredAt = delivered };
        row.ActiveAlarmJson = active is null ? null : JsonSerializer.Serialize(active, Web);
        row.HubDisabledAlarms = row.HubDisabledAlarms.Union(disable).ToList();
        return Task.FromResult(true);
    }

    public Task<bool> RecordAlarmPushAsync(Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at, string status)
    {
        BeforeRecordAlarmPush?.Invoke();
        var row = Devices[deviceId];
        if (row.ActiveAlarm is not { } active || active.AlarmId != alarmId || active.FiredAt != firedAt)
            return Task.FromResult(false);
        row.ActiveAlarmJson = JsonSerializer.Serialize(active with { LastPushAt = at, LastPushStatus = status }, Web);
        return Task.FromResult(true);
    }

    /// Another instance holds the engine lock.
    public bool EngineLockedElsewhere { get; set; }

    /// The engine lock this store handed out, if any.
    public FakeEngineLock? EngineLock { get; private set; }

    public Task<IEngineLock?> TryLockEngineAsync(CancellationToken ct)
    {
        if (EngineLockedElsewhere || EngineLock is { Held: true }) return Task.FromResult<IEngineLock?>(null);
        EngineLock = new FakeEngineLock();
        return Task.FromResult<IEngineLock?>(EngineLock);
    }

    internal sealed class FakeEngineLock : IEngineLock
    {
        public bool Held { get; set; } = true;
        public Task<bool> IsHeldAsync(CancellationToken ct) => Task.FromResult(Held);

        public ValueTask DisposeAsync()
        {
            Held = false;
            return ValueTask.CompletedTask;
        }
    }

    /// Delivery receipt tokens by hash, as HubStore.IssueReceiptAsync keeps them.
    public Dictionary<string, (Guid DeviceId, Guid AlarmId, DateTime FiredAt, DateTime ExpiresAt)> Receipts { get; } = [];

    /// Runs just before a receipt is kept.
    public Action? BeforeIssueReceipt { get; set; }

    public Task IssueReceiptAsync(string tokenHash, Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at)
    {
        BeforeIssueReceipt?.Invoke();
        foreach (var expired in Receipts.Where(r => r.Value.ExpiresAt <= at).Select(r => r.Key).ToList())
            Receipts.Remove(expired);
        Receipts[tokenHash] = (deviceId, alarmId, firedAt, at + LFHub.Engine.AlarmReceipts.Lifetime);
        return Task.CompletedTask;
    }

    /// <summary>
    /// POST /v1/receipt/{token}, as HubStore.RedeemReceiptAsync writes it: false for a token that
    /// is unknown, used or expired.
    /// </summary>
    public bool Receipt(string token)
    {
        var hash = LFHub.Engine.AlarmReceipts.Hash(token);
        if (!Receipts.Remove(hash, out var receipt) || receipt.ExpiresAt <= Now) return false;
        var row = Devices[receipt.DeviceId];
        if (row.ActiveAlarm is { DeliveredAt: null } active && active.AlarmId == receipt.AlarmId &&
            active.FiredAt == receipt.FiredAt)
            row.ActiveAlarmJson = JsonSerializer.Serialize(active with { DeliveredAt = Now }, Web);
        return true;
    }
}
