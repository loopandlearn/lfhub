using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Db;

namespace LFHub.Tests.Fakes;

/// Records every push the engine sends and answers each with <see cref="Result"/>.
internal sealed class FakeApns : IApnsClient
{
    public sealed record AlarmPush(Guid AlarmId, string Title, string Subtitle, string? Body, string? Sound,
        string? Symbol, string Category, bool Critical, bool Ring, bool RingOnce, string Receipt);

    public sealed record LiveActivityPush(string Token, JsonObject Aps, IReadOnlyDictionary<string, string>? Headers);

    public List<int> Badges { get; } = [];
    public List<AlarmPush> Alarms { get; } = [];
    public sealed record StopPush(Guid AlarmId, string Title, string Subtitle, string? Body, AlarmStopReason Reason);

    public List<StopPush> Stops { get; } = [];
    public sealed record SpeakPush(string Title, int Current, int Previous, string Language, bool Mmol, DateTime At);

    public List<string> LatestBg { get; } = [];
    public List<SpeakPush> Speeches { get; } = [];
    public List<DateTime> Heartbeats { get; } = [];
    public List<LiveActivityPush> LiveActivities { get; } = [];

    public PushResult Result { get; set; } = new(true, "200", false, 200);

    /// Makes Live Activity sends throw, as an unexpected failure would.
    public bool ThrowOnLiveActivity { get; set; }

    /// Runs as an alarm push goes out; the push then fails as HttpClient does when its token is cancelled.
    public Action? OnAlarm { get; set; }

    public Task<PushResult> SendBadgeAsync(ApnsConfig cfg, int badge, CancellationToken ct)
    {
        Badges.Add(badge);
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendAlarmAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle, string? body,
        string? sound, string? symbol, string category, bool critical, bool ring, bool ringOnce, string receipt, CancellationToken ct)
    {
        Alarms.Add(new AlarmPush(alarmId, title, subtitle, body, sound, symbol, category, critical, ring, ringOnce, receipt));
        OnAlarm?.Invoke();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendAlarmStopAsync(ApnsConfig cfg, Guid alarmId, string title, string subtitle,
        string? body, AlarmStopReason reason, CancellationToken ct)
    {
        Stops.Add(new StopPush(alarmId, title, subtitle, body, reason));
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendHeartbeatAsync(ApnsConfig cfg, DateTime nowUtc, CancellationToken ct)
    {
        Heartbeats.Add(nowUtc);
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendLatestBgAsync(ApnsConfig cfg, string subtitle, CancellationToken ct)
    {
        LatestBg.Add(subtitle);
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendSpeakAsync(ApnsConfig cfg, string title, int current, int previous, string language,
        bool mmol, DateTime nowUtc, CancellationToken ct)
    {
        Speeches.Add(new SpeakPush(title, current, previous, language, mmol, nowUtc));
        return Task.FromResult(Result);
    }

    public Task<PushResult> SendLiveActivityAsync(ApnsConfig cfg, string token, JsonObject aps, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (ThrowOnLiveActivity) throw new InvalidOperationException("Live Activity send failed");
        LiveActivities.Add(new LiveActivityPush(token, (JsonObject)aps.DeepClone(), headers));
        return Task.FromResult(Result);
    }
}
