using System.Text.Json.Nodes;
using LFHub.Db;
using LFHub.Engine;
using LFHub.Engine.Alarms;
using LFHub.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace LFHub.Tests.Engine;

/// <summary>
/// The real engine against a fake clock, store, APNs and Nightscout. Readings are
/// invented; each one is stamped with the clock at the moment it is added.
/// </summary>
internal sealed class EngineHarness
{
    /// Noon UTC: day under the default day/night window.
    public static readonly DateTime Start = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(Start));
    public FakeStore Store { get; }
    public FakeApns Apns { get; } = new();
    public FakeNightscout Nightscout { get; } = new();
    public ListLoggerFactory Logs { get; } = new();
    public HubEngine Engine { get; private set; }
    public Patient Patient { get; }

    private readonly List<(DateTime At, int Sgv)> _readings = [];

    public EngineHarness(TimeSpan? refreshDeadline = null)
    {
        Store = new FakeStore(Time);
        Patient = new Patient(Guid.NewGuid(), "Test patient", "https://ns.example.org", null);
        Store.Patients.Add(Patient);
        Nightscout.Route("/api/v1/entries.json", _ => new JsonArray(_readings
            .OrderByDescending(r => r.At)
            .Select(r => (JsonNode)new JsonObject
            {
                ["sgv"] = r.Sgv,
                ["date"] = (long)(r.At - DateTime.UnixEpoch).TotalMilliseconds,
                ["direction"] = "Flat",
                ["type"] = "sgv",
            }).ToArray()).ToJsonString());
        _refreshDeadline = refreshDeadline ?? TimeSpan.FromSeconds(45);
        Engine = NewEngine();
    }

    private readonly TimeSpan _refreshDeadline;

    private HubEngine NewEngine() => new(Store, Apns, Logs, Time)
    {
        SourceHttp = new HttpClient(Nightscout),
        FollowStreams = false,
        RefreshDeadline = _refreshDeadline,
        EngineLockRetry = TimeSpan.FromMilliseconds(20),
    };

    /// A restarted hub: a new engine over the same store, APNs and Nightscout, with nothing in memory.
    public void Restart() => Engine = NewEngine();

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    public void Advance(TimeSpan by) => Time.Advance(by);

    /// A reading taken now.
    public void Reading(int sgv) => _readings.Add((Now, sgv));

    /// Readings five minutes apart, the last one now.
    public void Readings(params int[] sgvs)
    {
        for (var i = 0; i < sgvs.Length; i++)
            _readings.Add((Now.AddMinutes(-5 * (sgvs.Length - 1 - i)), sgvs[i]));
    }

    /// Advances five minutes and adds a reading then.
    public void NextReading(int sgv)
    {
        Advance(TimeSpan.FromMinutes(5));
        Reading(sgv);
    }

    public FakeStore.Row AddDevice(Action<FakeStore.Row>? edit = null) => Store.AddDevice(Patient.Id, edit);

    /// A device with alarms on and the given alarm list uploaded.
    public FakeStore.Row AlarmDevice(IReadOnlyList<Alarm> alarms, AlarmConfiguration? config = null,
        Action<FakeStore.Row>? edit = null)
    {
        var row = AddDevice(r =>
        {
            r.Settings = new JsonObject { ["alarms"] = true };
            edit?.Invoke(r);
        });
        Store.SetAlarms(row.Id, alarms, config ?? new AlarmConfiguration());
        return row;
    }

    public Task ReconcileAsync() => Engine.ReconcileAsync();

    /// The socket announced new data: fetch what is due and check alarms at once.
    public Task NewDataAsync() => Engine.EvaluateAsync(Patient.Id, true, true, CancellationToken.None);

    /// The 30-second tick: fetch what is due; alarms on their minute.
    public Task TickAsync() => Engine.EvaluateAsync(Patient.Id, true, false, CancellationToken.None);

    /// POST /v1/device/alarm/{id}/handled: the store's write and the engine's note of it.
    public void Handled(Guid deviceId, Guid alarmId)
    {
        Store.AlarmHandled(deviceId, alarmId);
        Engine.AlarmHandled(deviceId, alarmId);
    }

    /// Something arrived from the app (Kick with immediate).
    public Task FromAppAsync() => Engine.EvaluateAsync(Patient.Id, false, true, CancellationToken.None);

    /// Runs the 30-second tick for `duration`.
    public async Task TicksAsync(TimeSpan duration)
    {
        var end = Now + duration;
        while (Now < end)
        {
            Advance(TimeSpan.FromSeconds(30));
            await TickAsync();
        }
    }
}
