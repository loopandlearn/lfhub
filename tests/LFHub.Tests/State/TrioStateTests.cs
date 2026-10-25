using System.Globalization;
using System.Text.Json.Nodes;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace LFHub.Tests.State;

/// <summary>
/// A Trio (OpenAPS) patient's Nightscout in the shape Trio uploads, with invented
/// values, through the parsers to the snapshot and AlarmData.
/// </summary>
public class TrioStateTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private static double U(string iso) =>
        (DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) - DateTime.UnixEpoch).TotalSeconds;

    private static string Entries()
    {
        var newest = U("2026-10-24T11:58:00Z");
        var list = new JsonArray();
        for (var i = 0; i < 12; i++)
            list.Add(new JsonObject
            {
                ["sgv"] = i == 0 ? 140 : 136 - i,
                ["date"] = (long)((newest - i * 300) * 1000),
                ["direction"] = i == 0 ? "FortyFiveUp" : "Flat",
                ["type"] = "sgv",
            });
        return list.ToJsonString();
    }

    private static JsonArray Series(int count, Func<int, double> value) =>
        new(Enumerable.Range(0, count).Select(i => (JsonNode)JsonValue.Create(value(i))).ToArray());

    private static string DeviceStatus(double currentTarget) => new JsonArray(new JsonObject
    {
        ["_id"] = "ds1",
        ["device"] = "Trio",
        ["created_at"] = "2026-10-24T11:59:00.000Z",
        ["openaps"] = new JsonObject
        {
            ["iob"] = new JsonObject { ["iob"] = 2.5, ["basaliob"] = -0.2, ["time"] = "2026-10-24T11:58:35.000Z" },
            ["suggested"] = new JsonObject
            {
                ["ISF"] = 50,
                ["CR"] = 10,
                ["COB"] = 20,
                ["sensitivityRatio"] = 1.1,
                ["eventualBG"] = 140,
                ["current_target"] = currentTarget,
                ["TDD"] = 30.5,
                ["IOB"] = 2.5,
                ["deliverAt"] = "2026-10-24T11:58:40.000Z",
                ["timestamp"] = "2026-10-24T11:58:40.000Z",
                ["reason"] = "COB: 20, Dev: 5, BGI: -1, ISF: 50, CR: 10, Target: 110",
                ["predBGs"] = new JsonObject
                {
                    ["IOB"] = Series(20, i => 140 - 2 * i),
                    ["ZT"] = Series(20, i => 140 - 5 * i),
                    ["UAM"] = Series(15, i => 140 + 2 * i),
                },
            },
            ["recommendedBolus"] = 0.4,
            ["version"] = "0.0.0",
        },
        ["pump"] = new JsonObject
        {
            ["clock"] = "2026-10-24T11:58:30Z",
            ["reservoir"] = 120.5,
            ["battery"] = new JsonObject { ["percent"] = 75 },
            ["status"] = new JsonObject { ["suspended"] = false, ["bolusing"] = false },
        },
        ["uploader"] = new JsonObject { ["battery"] = 64, ["isCharging"] = false },
    }).ToJsonString();

    private const string Profile = """
    [{"_id":"p1","defaultProfile":"default","units":"mg/dl","startDate":"2026-10-01T00:00:00.000Z",
      "store":{"default":{"units":"mg/dl","timezone":"Europe/Stockholm","dia":6,
        "sens":[{"time":"00:00","timeAsSeconds":0,"value":45}],
        "carbratio":[{"time":"00:00","timeAsSeconds":0,"value":12}],
        "target_low":[{"time":"00:00","timeAsSeconds":0,"value":100}],
        "target_high":[{"time":"00:00","timeAsSeconds":0,"value":100}],
        "basal":[{"time":"00:00","timeAsSeconds":0,"value":0.6}]}}}]
    """;

    private static async Task<PatientState> FollowAsync(double currentTarget = 110)
    {
        var ns = new FakeNightscout()
            .Route("/api/v1/entries.json", Entries())
            .Route("/api/v1/profiles.json", Profile)
            .Route("/api/v1/devicestatus.json", DeviceStatus(currentTarget));
        var state = new PatientState(new HttpClient(ns), "https://ns.example.org", null, NullLogger.Instance) { UtcNow = () => Now };
        await state.RefreshAsync(true, TestContext.Current.CancellationToken);
        return state;
    }

    private static double? N(JsonObject o, string k) => o[k]?.GetValue<double>();

    [Fact]
    public async Task Snapshot_OpenApsValues()
    {
        var s = (await FollowAsync()).BuildSnapshot(ClientSettings.Default, Now)!;
        Assert.Equal(140.0, N(s, "glucose"));
        Assert.Equal(5.0, N(s, "delta"));
        Assert.Equal("upSlight", s["trend"]!.ToString());
        Assert.Equal(2.5, N(s, "iob"));
        Assert.Equal(20.0, N(s, "cob"));
        Assert.Equal(140.0, N(s, "projected"));
        Assert.Equal(0.4, N(s, "recBolus"));
        Assert.Equal(1.1, N(s, "autosens"));
        Assert.Equal(30.5, N(s, "tdd"));
        Assert.Equal(64.0, N(s, "battery"));
        Assert.Equal(75.0, N(s, "pumpBattery"));
        Assert.Equal(120.5, N(s, "pumpReservoirU"));
        Assert.Equal("default", s["profileName"]?.ToString());
        Assert.False(s["isNotLooping"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Snapshot_EnactedValuesWinOverTheProfileWhenTheyDiffer()
    {
        var s = (await FollowAsync()).BuildSnapshot(ClientSettings.Default, Now)!;
        Assert.Equal(50.0, N(s, "isfMgdlPerU"));
        Assert.Equal(10.0, N(s, "carbRatio"));
        Assert.Equal(110.0, N(s, "targetLowMgdl"));
        Assert.Equal(110.0, N(s, "targetHighMgdl"));
    }

    [Fact]
    public async Task Snapshot_MmolTargetIsConverted()
    {
        var s = (await FollowAsync(currentTarget: 6.1)).BuildSnapshot(ClientSettings.Default, Now)!;
        Assert.Equal(6.1 * Swift.MgdlPerMmol, N(s, "targetLowMgdl")!.Value, 9);
    }

    [Fact]
    public async Task Snapshot_MinMaxOverThePredictionWindow()
    {
        var state = await FollowAsync();
        var s = state.BuildSnapshot(ClientSettings.Default, Now)!;
        Assert.Equal(80.0, N(s, "minBgMgdl"));
        Assert.Equal(164.0, N(s, "maxBgMgdl"));
        var half = ClientSettings.FromJson(JsonNode.Parse("""{"predictionToLoad":0.5}""")!.AsObject());
        var h = state.BuildSnapshot(half, Now)!;
        Assert.Equal(110.0, N(h, "minBgMgdl"));
        Assert.Equal(152.0, N(h, "maxBgMgdl"));
    }

    [Fact]
    public async Task AlarmData_ForecastIsTheLowestPredictionPerPoint()
    {
        var a = (await FollowAsync()).BuildAlarmData(ClientSettings.Default, Now)!;
        Assert.Equal(13, a.PredictionData.Count);
        var start = DateTime.UnixEpoch.AddSeconds(U("2026-10-24T11:58:40Z"));
        for (var i = 0; i < 13; i++)
        {
            Assert.Equal(140 - 5 * i, a.PredictionData[i].Sgv);
            Assert.Equal(start.AddSeconds(300 * i), a.PredictionData[i].Date);
        }
    }

    [Fact]
    public async Task AlarmData_LoopTimeBatteryAndReservoir()
    {
        var a = (await FollowAsync()).BuildAlarmData(ClientSettings.Default, Now)!;
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(U("2026-10-24T11:58:30Z")), a.LastLoopTime);
        Assert.Equal(2.5, a.Iob);
        Assert.Equal(20, a.Cob);
        Assert.Equal(0.4, a.RecBolus);
        Assert.Equal(120.5, a.LatestPumpVolume);
        Assert.Equal(75, a.LatestPumpBattery);
        Assert.Equal(64, a.LatestBattery);
        Assert.False(a.LatestBatteryIsCharging);
        Assert.Equal(12, a.BgReadings.Count);
    }

    [Fact]
    public async Task LatestReading_AndDirection()
    {
        var state = await FollowAsync();
        Assert.Equal((DateTime.UnixEpoch.AddSeconds(U("2026-10-24T11:58:00Z")), 140), state.LatestReading);
        Assert.Equal("FortyFiveUp", state.LatestDirection);
    }

    [Fact]
    public void NothingFetched_NoSnapshotOrAlarmData()
    {
        var ns = new FakeNightscout().Route("/api/v1/entries.json", "{\"error\":\"unauthorized\"}");
        var state = new PatientState(new HttpClient(ns), "https://ns.example.org", null, NullLogger.Instance) { UtcNow = () => Now };
        Assert.Null(state.BuildSnapshot(ClientSettings.Default, Now));
        Assert.Null(state.BuildAlarmData(ClientSettings.Default, Now));
        Assert.Null(state.LatestReading);
    }
}
