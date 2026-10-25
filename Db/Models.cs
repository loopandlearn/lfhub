using System.Text.Json.Nodes;

namespace LFHub.Db;

/// <summary>An admin manages the patient's sources and devices and invites; a member follows.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Member = "member";

    public static bool IsValid(string? role) => role is Admin or Member;
}

/// <param name="LastReading">The latest reading the hub kept for the patient, from any earlier run.</param>
public sealed record Patient(
    Guid Id,
    string Name,
    string? NightscoutUrl,
    string? NightscoutToken,
    LFHub.Engine.State.DexcomCredentials? Dexcom = null,
    (DateTime At, int Mgdl)? LastReading = null,
    int SourceRevision = 0)
{
    public bool HasSource => NightscoutUrl is not null || Dexcom is not null;

    /// Names the patient by id only, so a logged record carries no token.
    public override string ToString() => $"Patient {Id}";
}

public sealed record ApnsConfig(
    string Token,
    string Environment,
    string BundleId,
    string TeamId,
    string KeyId,
    string Key)
{
    /// Leaves out the push token and the .p8, so a logged record carries neither.
    public override string ToString() => $"ApnsConfig {{ Environment = {Environment}, BundleId = {BundleId}, TeamId = {TeamId}, KeyId = {KeyId} }}";
}

public sealed class Device
{
    public Guid Id { get; init; }
    public Guid PatientId { get; init; }
    public required string Name { get; init; }
    public required JsonObject Settings { get; init; }
    public ApnsConfig? Apns { get; init; }
    /// The device registered for APNs, but its stored key does not open under the master key.
    public bool ApnsKeyDoesNotOpen { get; init; }
    public int? LastBadge { get; init; }
    public DateTime? LastPushAt { get; init; }
    public string? LastPushStatus { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Role { get; init; } = Roles.Admin;
    public bool IsAdmin => Role == Roles.Admin;
    /// The device pushes with an admin device's APNs key.
    public bool ApnsKeyShared { get; init; }
    public LiveActivityState LiveActivity { get; init; } = new();
    public JsonObject ClientSettings { get; init; } = new();
    public string? AlarmsJson { get; init; }
    public string? AlarmStateJson { get; init; }
    public ActiveAlarm? ActiveAlarm { get; init; }
    public IReadOnlyList<Guid> HubDisabledAlarms { get; init; } = [];
    /// Moves each time the app handles an alarm or uploads its alarms; see HubStore.SaveAlarmEvaluationAsync.
    public int AlarmRev { get; init; }
    /// When a push of any kind to the device was last accepted.
    public DateTime? LastPushOkAt { get; init; }
    /// When pushes to the device began to be rejected; null while the last one was accepted.
    public DateTime? PushFailingSince { get; init; }
    /// Whether the last alarm push was accepted; null before any alarm push.
    public bool? LastAlarmPushOk { get; init; }

    public bool BadgeEnabled => Flag("badge");
    public bool LiveActivityEnabled => Flag("liveActivity");
    public bool AlarmsEnabled => Flag("alarms");
    /// Set once the app's build carries Apple's critical alerts entitlement.
    public bool CriticalAlerts => Flag("criticalAlerts");
    public bool Mmol => Settings["unit"] is JsonValue v && v.TryGetValue<string>(out var unit) && unit == "mmol";

    /// The app version the device reported, if any.
    public string? AppVersion => Settings["appVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// Whether the app on the device reported it supports <paramref name="feature"/> (see <see cref="LFHub.Api.HubInfo"/>).
    public bool HasFeature(string feature) =>
        Settings["features"] is JsonArray all && all.Any(f => f is JsonValue v && v.TryGetValue<string>(out var s) && s == feature);

    /// A hub setting that is true; anything else, including a value of another type, is off.
    private bool Flag(string key) => Settings[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;
}

/// <summary>
/// The device's Live Activity as the hub knows it. The app reports the
/// activity's push token and renewal deadline; the hub records what it sent.
/// </summary>
public sealed class LiveActivityState
{
    public string? Token { get; init; }
    public string? PushToStartToken { get; init; }
    public DateTime? RenewBy { get; init; }
    public bool RenewalFailed { get; init; }
    public string? LastFingerprint { get; init; }
    public DateTime? LastPushAt { get; init; }
    public string? LastPushStatus { get; init; }
}

/// <summary>
/// An alarm the hub has delivered. One that repeats is delivered again until it
/// is snoozed or acknowledged in the app, Snooze All begins, or its condition clears.
/// </summary>
/// <param name="Category">The notification category: "category" (Snooze) or "lfhub-ack" (Acknowledge).</param>
/// <param name="Ring">Ring it as a system alarm too; false while muted.</param>
/// <param name="LastPushAt">When its push was last sent.</param>
/// <param name="LastPushStatus">APNs' answer to that push; null until it is known.</param>
/// <param name="ConditionCleared">The condition does not hold; the ringing stops once it has rung long enough.</param>
/// <param name="DeliveredAt">When the phone's first delivery receipt for this firing arrived; null until then.</param>
/// <param name="DisabledWhenFired">
/// The alarm turned itself off when it fired (Temporary); it sounds until it is snoozed or handled.
/// </param>
public sealed record ActiveAlarm(
    Guid AlarmId,
    string Title,
    string Subtitle,
    string? Body,
    string? Sound,
    string? Symbol,
    string Category,
    bool Ring,
    bool Repeat,
    DateTime FiredAt,
    DateTime LastPushAt,
    string? LastPushStatus,
    bool ConditionCleared = false,
    bool DisabledWhenFired = false,
    DateTime? DeliveredAt = null);
