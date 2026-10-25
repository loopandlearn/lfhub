using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace LFHub.Tests.State;

/// When each Nightscout endpoint is fetched again.
public sealed class CadenceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private DateTime _now = Start;
    private readonly FakeNightscout _ns = new();
    private readonly PatientState _state;

    public CadenceTests()
    {
        // The newest reading is always 60 s older than the clock at the first fetch.
        _ns.Route("/api/v1/entries.json", _ =>
        {
            var newest = (Start.AddSeconds(-60) - DateTime.UnixEpoch).TotalMilliseconds;
            return new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject
            {
                ["sgv"] = 120 + i,
                ["date"] = (long)(newest - i * 300_000),
                ["direction"] = "Flat",
                ["type"] = "sgv",
            }).ToArray()).ToJsonString();
        });
        _state = new PatientState(new HttpClient(_ns), "https://ns.example.org", null, NullLogger.Instance) { UtcNow = () => _now };
    }

    public void Dispose()
    {
        _state.Dispose();
        _ns.Dispose();
    }

    private int TreatmentFetches() => _ns.Requests.Count(r =>
        r.StartsWith("/api/v1/treatments.json", StringComparison.Ordinal) && !r.Contains("eventType", StringComparison.Ordinal));

    private async Task At(int seconds)
    {
        _now = Start.AddSeconds(seconds);
        await _state.RefreshAsync(false, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FreshReading_NextBgFetchAfterTheReadingIntervalPlusDelay()
    {
        await At(0);
        Assert.Equal(Start, _state.LastBgCheck);
        await At(240);
        Assert.Equal(1, _ns.Count("/api/v1/entries.json"));
        await At(250);
        Assert.Equal(2, _ns.Count("/api/v1/entries.json"));
        Assert.Equal(Start.AddSeconds(250), _state.LastBgCheck);
    }

    [Fact]
    public async Task LiveSocket_StretchesPollingButStaysWithinTheMissedReadingWindow()
    {
        _state.SocketLive = true;
        await At(0);
        await At(260);
        Assert.Equal(1, _ns.Count("/api/v1/entries.json"));
        await At(270);
        Assert.Equal(2, _ns.Count("/api/v1/entries.json"));
    }

    [Fact]
    public async Task EmptyDeviceStatus_CheckedAgainWithinTheNotLoopingWindow()
    {
        await At(0);
        Assert.Equal(Start, _state.LastDeviceStatusCheck);
        await At(260);
        Assert.Equal(1, _ns.Count("/api/v1/devicestatus.json"));
        await At(270);
        Assert.Equal(2, _ns.Count("/api/v1/devicestatus.json"));
    }

    [Fact]
    public async Task DataUpdate_MakesTheNamedEndpointsDueAtOnce()
    {
        await At(0);
        using var delta = JsonDocument.Parse("""{"delta":true,"sgvs":[{}]}""");
        _state.NotifyDataUpdate(delta.RootElement);
        await At(30);
        Assert.Equal(2, _ns.Count("/api/v1/entries.json"));
        Assert.Equal(1, _ns.Count("/api/v1/devicestatus.json"));
        Assert.Equal(1, TreatmentFetches());

        using var full = JsonDocument.Parse("""{"sgvs":[]}""");
        _state.NotifyDataUpdate(full.RootElement);
        await At(60);
        Assert.Equal(3, _ns.Count("/api/v1/entries.json"));
        Assert.Equal(2, _ns.Count("/api/v1/devicestatus.json"));
        Assert.Equal(1, _ns.Count("/api/v2/properties/dbsize"));
    }

    [Fact]
    public async Task TreatmentsAndProfileOnTheirOwnCadence()
    {
        await At(0);
        await At(570);
        Assert.Equal(1, TreatmentFetches());
        await At(600);
        Assert.Equal(2, TreatmentFetches());
        Assert.Equal(1, _ns.Count("/api/v1/profiles.json"));
        await At(1800);
        Assert.Equal(2, _ns.Count("/api/v1/profiles.json"));
    }
}
