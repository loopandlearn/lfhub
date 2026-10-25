using LFHub.Db;

namespace LFHub.Apns;

/// <summary>Why the hub stops a ringing system alarm.</summary>
public enum AlarmStopReason
{
    /// The alarm's condition cleared, or Snooze All began on a sensor start (Auto-Snooze CGM Start).
    Cleared,
    /// The alarm rang for as long as it rings: a sound that does not repeat.
    RingDone,
    /// <summary>
    /// The app silenced the alarm (snoozed, acknowledged, disabled or removed it, or began
    /// Snooze All), or alarms were turned off.
    /// </summary>
    Silenced,
}

/// <summary>The pushes the engine sends; <see cref="ApnsClient"/> sends them to APNs.</summary>
public interface IApnsClient
{
    Task<PushResult> SendBadgeAsync(ApnsConfig cfg, int badge, CancellationToken ct);

    Task<PushResult> SendAlarmAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle, string? body,
        string? sound, string? symbol, string category, bool critical, bool ring, bool ringOnce, string receipt,
        CancellationToken ct);

    Task<PushResult> SendAlarmStopAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle, string? body,
        AlarmStopReason reason, CancellationToken ct);

    Task<PushResult> SendHeartbeatAsync(ApnsConfig cfg, DateTime nowUtc, CancellationToken ct);

    Task<PushResult> SendLatestBgAsync(ApnsConfig cfg, string subtitle, CancellationToken ct);

    Task<PushResult> SendSpeakAsync(ApnsConfig cfg, string title, int current, int previous, string language,
        bool mmol, DateTime nowUtc, CancellationToken ct);

    Task<PushResult> SendLiveActivityAsync(ApnsConfig cfg, string token, System.Text.Json.Nodes.JsonObject aps,
        CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null);
}
