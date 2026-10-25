namespace LFHub.Db;

/// <summary>The persistence the engine uses; <see cref="HubStore"/> implements it over Postgres.</summary>
public interface IHubStore
{
    Task<List<Patient>> PatientsWithSourceAsync();
    Task SaveLastReadingAsync(Guid patientId, DateTime at, int mgdl);
    Task<List<Device>> DevicesForPatientAsync(Guid patientId);
    Task<Device?> DeviceAsync(Guid deviceId);
    Task<int> PruneUnseenDevicesAsync(TimeSpan age);
    Task RecordPushAsync(Guid deviceId, int? badge, string status, bool delivered);
    Task<DateTime?> RecordPushOutcomeAsync(Guid deviceId, DateTime at, bool accepted, bool alarm, bool tokenGone);
    Task ClearLastBadgeAsync(Guid deviceId);
    Task RecordLiveActivityPushAsync(Guid deviceId, string? fingerprint, string status, bool delivered, bool tokenGone);
    Task RecordLiveActivityRenewalAsync(Guid deviceId, bool started, string status, bool pushToStartTokenGone);
    Task<bool> SaveAlarmEvaluationAsync(Guid deviceId, int readRev, string stateJson, ActiveAlarm? active,
        IReadOnlyCollection<Guid> disable);
    Task<bool> RecordAlarmPushAsync(Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at, string status);
    Task IssueReceiptAsync(string tokenHash, Guid deviceId, Guid alarmId, DateTime firedAt, DateTime at);

    /// <summary>
    /// Takes the lock that lets one hub instance run the engine, or null while another
    /// instance holds it. Disposing the handle releases it.
    /// </summary>
    Task<IEngineLock?> TryLockEngineAsync(CancellationToken ct);
}

/// <summary>The engine lock taken by <see cref="IHubStore.TryLockEngineAsync"/>.</summary>
public interface IEngineLock : IAsyncDisposable
{
    /// <summary>False once the lock is lost, e.g. when the database connection that holds it drops.</summary>
    Task<bool> IsHeldAsync(CancellationToken ct);
}
