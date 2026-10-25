using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace LFHub.Tests.State;

/// <summary>
/// A Loop patient's Nightscout, with invented values, through the parsers to the Live
/// Activity snapshot and AlarmData. Expected values are worked out by hand from the
/// fixture as LoopFollow computes them.
/// </summary>
public class LoopStateTests
{
    private const double F = 18.015588000005408;
    private static readonly double[] Pred = [120, 110, 100, 90, 80, 70, 60, 50, 40, 30, 35, 45, 410, 130, 140, 150, 160];

    private static double U(string iso) =>
        (DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) - DateTime.UnixEpoch).TotalSeconds;

    private static DateTime D(string iso) => DateTime.UnixEpoch.AddSeconds(U(iso));

    private static double? N(JsonObject o, string k) => o[k]?.GetValue<double>();

    private static string Entries()
    {
        var newest = U("2026-10-24T11:55:00Z");
        var list = new JsonArray();
        for (var i = 0; i < 30; i++)
        {
            var sgv = i == 0 ? 101 : i == 1 ? 105 : i == 20 ? 650 : 100 + i;
            list.Add(new JsonObject
            {
                ["sgv"] = sgv,
                ["date"] = (newest - i * 300) * 1000,
                ["direction"] = i == 0 ? "FortyFiveDown" : "Flat",
                ["type"] = "sgv",
            });
            if (i == 3) list.Add(new JsonObject { ["sgv"] = 100 + i, ["date"] = (newest - i * 300 - 10) * 1000, ["type"] = "sgv" });
        }
        return list.ToJsonString();
    }

    private const string Profile = """
    [{"defaultProfile":"Default","units":"mmol/L","expirationDate":"2026-12-01T00:00:00Z","startDate":"2026-10-01T00:00:00.000Z",
      "loopSettings":{"overridePresets":[{"name":"Running","duration":3600,"targetRange":[7,8],"insulinNeedsScaleFactor":0.7,"symbol":"R"}],"dosingEnabled":true},
      "store":{"Default":{"units":"mmol/L","timezone":"Europe/Stockholm",
        "sens":[{"time":"00:00","timeAsSeconds":0,"value":3.0},{"time":"12:00","timeAsSeconds":43200,"value":2.5}],
        "carbratio":[{"time":"00:00","timeAsSeconds":0,"value":10},{"time":"10:00","timeAsSeconds":36000,"value":12}],
        "target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.5}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":6.0}],
        "basal":[{"time":"00:00","timeAsSeconds":0,"value":0.5},{"time":"08:00","timeAsSeconds":28800,"value":0.65},{"time":"14:00","timeAsSeconds":50400,"value":0.8}]}}}]
    """;

    private static readonly string Ds1 = """
    [{"created_at":"2026-10-24T11:57:40Z","device":"loop://iPhone",
      "pump":{"clock":"2026-10-24T11:57:30Z","reservoir":42.35,"pumpID":"PUMP01","manufacturer":"Insulet","model":"Dash","battery":{"percent":80}},
      "uploader":{"battery":55,"isCharging":true},
      "override":{"active":true,"name":"Running"},
      "loop":{"timestamp":"2026-10-24T11:57:35Z","iob":{"iob":1.23},"cob":{"cob":15},"recommendedBolus":0.35,
              "predicted":{"startDate":"2026-10-24T11:55:00Z","values":PRED}}}]
    """.Replace("PRED", JsonSerializer.Serialize(Pred), StringComparison.Ordinal);

    private const string Ds2 = """
    [{"created_at":"2026-10-24T12:02:40Z","device":"loop://iPhone",
      "pump":{"clock":"2026-10-24T12:02:30Z","pumpID":"PUMP01","manufacturer":"Insulet","model":"Dash"},
      "uploader":{"battery":54},
      "loop":{"timestamp":"2026-10-24T12:02:35Z","iob":{"iob":1.1},"failureReason":"pump comms",
              "predicted":{"values":[200,210]}}}]
    """;

    private const string Treatments = """
    [
     {"_id":"t1","id":"x1","eventType":"Note","created_at":"2026-10-24T11:58:00.000Z","notes":"CGM: temporarySensorIssue"},
     {"_id":"t2","eventType":"Temporary Target","created_at":"2026-10-24T11:50:00.000Z","duration":60,"targetBottom":144.6,"targetTop":144.6,"enteredBy":"Loop"},
     {"_id":"t3","eventType":"Temp Basal","created_at":"2026-10-24T11:45:00.000Z","absolute":0.45,"duration":30},
     {"_id":"t4","eventType":"Temporary Override","created_at":"2026-10-24T11:00:00.000Z","duration":120,"reason":"Running"},
     {"_id":"t5","eventType":"Temp Basal","created_at":"2026-10-24T11:00:00.000Z","absolute":1.125,"duration":60},
     {"_id":"t6","eventType":"Correction Bolus","created_at":"2026-10-24T10:00:00.000Z","insulin":1.0},
     {"_id":"t7","eventType":"Bolus","created_at":"2026-10-24T10:05:00.000Z","insulin":0.2,"automatic":true},
     {"_id":"t8","eventType":"Temporary Target","created_at":"2026-10-24T09:10:00.000Z","duration":0,"enteredBy":"Loop"},
     {"_id":"t9","eventType":"Temporary Target","created_at":"2026-10-24T09:00:00.000Z","duration":30,"targetBottom":120,"targetTop":120,"enteredBy":"Loop"},
     {"_id":"t10","eventType":"Meal Bolus","created_at":"2026-10-24T08:00:00.000Z","carbs":30,"insulin":3.0},
     {"_id":"t11","eventType":"Temporary Override","created_at":"2026-10-24T08:00:00.000Z","duration":30,"reason":"Pre-meal"},
     {"_id":"t12","eventType":"Carb Correction","created_at":"2026-10-24T13:30:00.000Z","carbs":10},
     {"_id":"t13","eventType":"Carb Correction","created_at":"2026-10-23T22:30:00.000Z","carbs":20},
     {"_id":"t14","eventType":"Site Change","created_at":"2026-10-23T18:00:00.000Z"},
     {"_id":"t15","id":"x1","eventType":"Note","created_at":"2026-10-24T11:58:00.000Z","notes":"CGM: temporarySensorIssue"}
    ]
    """;

    private static readonly ClientSettings Sv = ClientSettings.FromJson(JsonNode.Parse(
        """{"units":"mmol/L","timeZone":"Europe/Stockholm","decimalSeparator":",","timeFormat":"HH:mm","infoDisplayItems":[{"type":19,"isVisible":true}]}""")!.AsObject());
    private static readonly ClientSettings UtcMgdl = ClientSettings.FromJson(JsonNode.Parse("""{"units":"mg/dL"}""")!.AsObject());
    private static readonly ClientSettings La = ClientSettings.FromJson(JsonNode.Parse(
        """{"timeZone":"America/Los_Angeles","downloadPrediction":false,"predictionToLoad":3,"timeFormat":"h:mm a","downloadDays":2}""")!.AsObject());

    private sealed class Patient : IDisposable
    {
        public DateTime Now = D("2026-10-24T12:00:00Z");
        public readonly PatientState State;
        public readonly FakeNightscout Ns = new("loop.example.org");
        private readonly HttpClient _http;
        private int _dsCalls;

        private Patient()
        {
            Ns.Route("/api/v1/entries.json", Entries())
                .Route("/api/v1/profiles.json", Profile)
                .Route("/api/v1/devicestatus.json", _ => _dsCalls++ == 0 ? Ds1 : Ds2)
                .Route("/api/v1/treatments.json", r => FakeNightscout.Query(r, "find[eventType]") switch
                {
                    "Sensor Start" => """[{"created_at":"2026-10-20T09:15:00.123+02:00"}]""",
                    "Insulin Change" => """[{"created_at":"2026-10-22T07:00:00.000Z"}]""",
                    "Site Change" => "[]",
                    _ => Treatments,
                })
                .Route("/api/v2/properties/dbsize",
                    """{"dbsize":{"totalDataSize":120.5,"dataPercentage":24,"details":{"maxSize":496,"dataSize":100.1}}}""");
            _http = new HttpClient(Ns);
            State = new PatientState(_http, "https://loop.example.org", "token-for-tests", NullLogger.Instance)
            {
                UtcNow = () => Now,
            };
            State.UpdateFetchSettings([Sv, UtcMgdl, La]);
        }

        public static async Task<Patient> FollowAsync()
        {
            var p = new Patient();
            await p.RefreshAsync();
            return p;
        }

        public Task RefreshAsync() => State.RefreshAsync(true, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            State.Dispose();
            _http.Dispose();
        }
    }

    [Fact]
    public async Task Snapshot_BgAndLoopValues()
    {
        using var p = await Patient.FollowAsync();
        var s = p.State.BuildSnapshot(Sv, p.Now)!;
        Assert.Equal(101.0, N(s, "glucose"));
        Assert.Equal(-4.0, N(s, "delta"));
        Assert.Equal("downSlight", s["trend"]!.ToString());
        Assert.Equal(U("2026-10-24T11:55:00Z"), N(s, "updatedAt"));
        Assert.Equal("mmol", s["unit"]!.ToString());
        Assert.False(s["isNotLooping"]!.GetValue<bool>());
        Assert.False(s["showRenewalOverlay"]!.GetValue<bool>());
        Assert.Equal(1.23, N(s, "iob"));
        Assert.Equal(15.0, N(s, "cob"));
        Assert.Equal(160.0, N(s, "projected"));
        Assert.Equal(0.35, N(s, "recBolus"));
        Assert.Equal(55.0, N(s, "battery"));
        Assert.Equal(80.0, N(s, "pumpBattery"));
        Assert.Equal(42.35, N(s, "pumpReservoirU"));
        Assert.Null(s["autosens"]);
        Assert.Null(s["tdd"]);
        Assert.Equal(30.0, N(s, "minBgMgdl"));
        Assert.Equal(410.0, N(s, "maxBgMgdl"));
    }

    [Fact]
    public async Task Snapshot_ProfileValuesAtTheProfilesLocalTime()
    {
        using var p = await Patient.FollowAsync();
        var s = p.State.BuildSnapshot(Sv, p.Now)!;
        Assert.Equal(5.5 * F, N(s, "targetLowMgdl")!.Value, 9);
        Assert.Equal(6.0 * F, N(s, "targetHighMgdl")!.Value, 9);
        Assert.Equal(2.5 * F, N(s, "isfMgdlPerU")!.Value, 9);
        Assert.Equal(12.0, N(s, "carbRatio"));
        Assert.Equal("Default", s["profileName"]?.ToString());
    }

    [Fact]
    public async Task Snapshot_TreatmentValues()
    {
        using var p = await Patient.FollowAsync();
        var s = p.State.BuildSnapshot(Sv, p.Now)!;
        Assert.Equal("Temporary sensor issue at 13:58", s["cgmSensorState"]?.ToString());
        Assert.Equal("Running", s["override"]?.ToString());
        Assert.Equal(U("2026-10-24T13:00:00Z"), N(s, "overrideEndAt"));
        Assert.Equal(144.0, N(s, "tempTargetMgdl"));
        Assert.Equal(U("2026-10-24T12:50:00Z"), N(s, "tempTargetEndAt"));
        Assert.Equal("0,8 → 0,45", s["basalRate"]?.ToString());
        Assert.Equal(60.0, N(s, "carbsToday"));
        Assert.Equal(U("2026-10-20T09:15:00Z"), N(s, "sageInsertTime"));
        Assert.Equal(U("2026-10-23T18:00:00Z"), N(s, "cageInsertTime"));
        Assert.Equal(U("2026-10-22T07:00:00Z"), N(s, "iageInsertTime"));
    }

    [Fact]
    public async Task Snapshot_FollowsEachDevicesSettings()
    {
        using var p = await Patient.FollowAsync();
        var u = p.State.BuildSnapshot(UtcMgdl, p.Now)!;
        Assert.Equal("0.8 → 0.45", u["basalRate"]?.ToString());
        Assert.Equal(40.0, N(u, "carbsToday"));
        Assert.Null(u["iageInsertTime"]);
        Assert.Equal("Temporary sensor issue at 11:58", u["cgmSensorState"]?.ToString());
        Assert.Equal("mgdl", u["unit"]!.ToString());

        var l = p.State.BuildSnapshot(La, p.Now)!;
        Assert.Equal(40.0, N(l, "carbsToday"));
        Assert.Equal("Temporary sensor issue at 4:58 AM", l["cgmSensorState"]?.ToString());
        Assert.Null(l["minBgMgdl"]);
        Assert.Equal(U("2026-10-24T13:00:00Z"), N(l, "overrideEndAt"));
    }

    [Fact]
    public async Task AlarmData_BgAndForecast()
    {
        using var p = await Patient.FollowAsync();
        var a = p.State.BuildAlarmData(Sv, p.Now)!;
        Assert.Equal(24, a.BgReadings.Count);
        Assert.Equal(101, a.BgReadings[^1].Sgv);
        Assert.DoesNotContain(a.BgReadings, g => g.Sgv > 600);
        var expected = Pred.Take(13).Select((v, i) =>
            $"{Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 39, 400)}@{U("2026-10-24T11:57:30Z") + 300 * i}");
        Assert.Equal(string.Join(",", expected),
            string.Join(",", a.PredictionData.Select(x => $"{x.Sgv}@{(x.Date - DateTime.UnixEpoch).TotalSeconds}")));
        Assert.Empty(p.State.BuildAlarmData(La, p.Now)!.PredictionData);
    }

    [Fact]
    public async Task AlarmData_OverridesTempTargetsAndTimes()
    {
        using var p = await Patient.FollowAsync();
        var a = p.State.BuildAlarmData(Sv, p.Now)!;
        Assert.Equal(D("2026-12-01T00:00:00Z"), a.ExpireDate);
        Assert.Equal(D("2026-10-24T11:57:30Z"), a.LastLoopTime);
        Assert.Equal(D("2026-10-24T11:00:00Z"), a.LatestOverrideStart);
        Assert.Equal(D("2026-10-24T08:30:00Z"), a.LatestOverrideEnd);
        Assert.Equal(D("2026-10-24T13:00:00Z"), a.ActiveOverrideEnd);
        Assert.Equal(D("2026-10-24T11:50:00Z"), a.LatestTempTargetStart);
        Assert.Equal(D("2026-10-24T09:10:00Z"), a.LatestTempTargetEnd);
        Assert.Equal(D("2026-10-24T12:50:00Z"), a.ActiveTempTargetEnd);
        Assert.Equal(D("2026-10-20T09:15:00Z"), a.SageInsertTime);
        Assert.Equal(D("2026-10-23T18:00:00Z"), a.PumpInsertTime);
    }

    [Fact]
    public async Task AlarmData_TreatmentsPumpAndDatabase()
    {
        using var p = await Patient.FollowAsync();
        var a = p.State.BuildAlarmData(Sv, p.Now)!;
        Assert.Equal("3,1", string.Join(",", a.RecentBoluses.Select(b => b.Units)));
        Assert.Equal("20,30", string.Join(",", a.RecentCarbs.Select(c => c.Grams)));
        Assert.Equal("20,10,30", string.Join(",", p.State.BuildAlarmData(La, p.Now)!.RecentCarbs.Select(c => c.Grams)));
        Assert.Equal(42.35, a.LatestPumpVolume);
        Assert.True(a.LatestBatteryIsCharging);
        Assert.Equal(24.0, a.DbSizePercentage);
    }

    [Fact]
    public async Task FailedLoop_KeepsWhatLoopFollowKeeps()
    {
        using var p = await Patient.FollowAsync();
        p.Now = p.Now.AddMinutes(5);
        await p.RefreshAsync();
        var s = p.State.BuildSnapshot(Sv, p.Now)!;
        Assert.Equal(1.1, N(s, "iob"));
        Assert.Equal(15.0, N(s, "cob"));
        Assert.Equal(0.35, N(s, "recBolus"));
        Assert.Equal(160.0, N(s, "projected"));
        Assert.Equal(42.35, N(s, "pumpReservoirU"));
        Assert.Equal(80.0, N(s, "pumpBattery"));
        Assert.Equal(54.0, N(s, "battery"));

        var a = p.State.BuildAlarmData(Sv, p.Now)!;
        Assert.Null(a.LatestBatteryIsCharging);
        Assert.Equal("55,54", string.Join(",", a.BatteryHistory.Select(b => b.Level)));
        Assert.Equal(D("2026-10-24T12:02:30Z"), a.LastLoopTime);
        Assert.Equal(13, a.PredictionData.Count);
        Assert.True(p.State.BuildSnapshot(Sv, D("2026-10-24T12:17:30Z"))!["isNotLooping"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Requests_CarryTheTokenAndLoopFollowsQueries()
    {
        using var p = await Patient.FollowAsync();
        Assert.All(p.Ns.Requests, r => Assert.Contains("token=token-for-tests", r, StringComparison.Ordinal));
        Assert.Equal(1, p.Ns.Count("/api/v1/entries.json"));
        Assert.Equal(1, p.Ns.Count("/api/v1/devicestatus.json"));
        Assert.Equal(1, p.Ns.Count("/api/v1/profiles.json"));
        Assert.Equal(1, p.Ns.Count("/api/v2/properties/dbsize"));
        Assert.Contains(p.Ns.Requests, r => r.StartsWith("/api/v1/entries.json", StringComparison.Ordinal)
            && r.Contains("count=2304", StringComparison.Ordinal) && r.Contains("find%5Btype%5D%5B%24ne%5D=cal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bundle_KeepsEachPartAndVersionsChanges()
    {
        using var p = await Patient.FollowAsync();
        var bundle = p.State.Bundle();
        var parts = bundle["parts"]!.AsObject();
        foreach (var part in new[] { "entries", "profile", "devicestatus", "treatments", "sage", "iage", "dbsize" })
            Assert.True(parts.ContainsKey(part), part);
        var version = p.State.BundleVersion;
        Assert.False(parts.ContainsKey("cage"));
        Assert.Equal(version, bundle["version"]!.GetValue<long>());

        var changed = 0;
        p.State.BundleChanged += () => changed++;
        p.Now = p.Now.AddMinutes(5);
        await p.RefreshAsync();
        Assert.True(p.State.BundleVersion > version);
        Assert.Equal(p.State.BundleVersion - version, changed);
    }

    [Theory]
    [InlineData(0.125, "0,12")]
    [InlineData(0.375, "0,38")]
    [InlineData(1.005, "1")]
    [InlineData(0.115, "0,12")]
    [InlineData(2.675, "2,68")]
    [InlineData(0.045, "0,04")]
    [InlineData(0.0, "0")]
    [InlineData(0.3000000004, "0,3")]
    [InlineData(1.0, "1")]
    [InlineData(0.95, "0,95")]
    [InlineData(0.05, "0,05")]
    public void FormatDecimal_MatchesNumberFormatter(double value, string expected) =>
        Assert.Equal(expected, Swift.FormatDecimal(value, 2, 0, ","));

    [Theory]
    [InlineData("2026-10-20T10:00:00garbage", 0.0, null)]
    [InlineData("2026-10-20T10:00:00.5", 0.0, 0.0)]
    [InlineData("2026-10-20T1:00:00Z", -32400.0, -32400.0)]
    [InlineData("2026-10-20T10:00:00", 0.0, 0.0)]
    [InlineData("2026-10-20T10:00:00+0200", 0.0, null)]
    [InlineData("2026-10-20T10:00:00.000+02:00", 0.0, 0.0)]
    [InlineData("2026-10-20T10:00:00.000Z", 0.0, 0.0)]
    [InlineData("2026-10-20T10:00Z", null, null)]
    [InlineData("2026-10-20T10:00:00 +0200", 0.0, null)]
    [InlineData("2026-1-2T3:4:5Z", -25167355.0, -25167355.0)]
    [InlineData("2026-10-20T25:00:00Z", null, null)]
    public void DateParsing_MatchesFoundation(string text, double? isoPrefix, double? parseDate)
    {
        var b = U("2026-10-20T10:00:00Z");
        Assert.Equal(isoPrefix, Swift.IsoPrefix(text) - b);
        Assert.Equal(parseDate, Swift.ParseDate(text) - b);
    }

    [Theory]
    [InlineData("temporarySensorIssue", "Temporary sensor issue")]
    [InlineData("warmup", "Warmup")]
    [InlineData("sensorFailedDuetoCountsAberration", "Sensor failed due to counts aberration")]
    [InlineData("firstOfTwoBGsNeeded", "First of two BGs needed")]
    [InlineData("calibrationError8", "Calibration error 8")]
    [InlineData("sensorFailure11", "Sensor failure 11")]
    [InlineData("needCalibration7", "Need calibration 7")]
    [InlineData("questionMarks", "Question marks (???)")]
    [InlineData(".unknown(23)", "Unknown state (23)")]
    public void CgmSensorState_DisplayName(string name, string expected) =>
        Assert.Equal(expected, CgmSensorState.DisplayName(name));

    [Fact]
    public void CgmSensorState_NoteMustMatchExactly()
    {
        Assert.Null(CgmSensorState.Parse("CGM: sensor was wet"));
        Assert.Null(CgmSensorState.Parse("cgm: warmup"));
    }
}
