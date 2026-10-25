using System.Text.Json.Nodes;
using LFHub.Apns;
using LFHub.Engine;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;

namespace LFHub.Tests.Engine;

/// <summary>
/// Speak BG in hub mode: which new readings a device gets a speak push for, the push
/// itself, and the client settings it reads.
/// </summary>
public class SpeakTests
{
    /// A device with alarms on and Speak BG on, with these client settings on top.
    private static FakeStore.Row SpeakDevice(EngineHarness h, JsonObject? clientSettings = null,
        Action<FakeStore.Row>? edit = null) =>
        h.AddDevice(r =>
        {
            r.Settings = new JsonObject { ["alarms"] = true, ["features"] = new JsonArray("receipts", "speak") };
            r.ClientSettings = clientSettings ?? new JsonObject();
            r.ClientSettings["speakBG"] ??= true;
            edit?.Invoke(r);
        });

    /// Starts the hub on these readings, the last one now; the device starts from that reading.
    private static async Task StartAsync(EngineHarness h, params int[] sgvs)
    {
        h.Readings(sgvs);
        await h.ReconcileAsync();
        await h.NewDataAsync();
    }

    private static async Task NextReadingAsync(EngineHarness h, int sgv)
    {
        h.NextReading(sgv);
        await h.NewDataAsync();
    }

    private static ClientSettings Settings(string json) => ClientSettings.FromJson(JsonNode.Parse(json)!.AsObject());

    private static readonly IReadOnlyList<GlucoseValue> NoForecast = [];

    [Fact]
    public async Task Always_SpeaksEachNewReadingOnce()
    {
        var h = new EngineHarness();
        var device = SpeakDevice(h);
        await StartAsync(h, 120, 125);
        Assert.Empty(h.Apns.Speeches);

        await NextReadingAsync(h, 130);
        Assert.Equal([new FakeApns.SpeakPush("130 → +5", 130, 125, "en", false, h.Now)], h.Apns.Speeches);
        Assert.Equal([$"Information: Speech sent to {device.Id}: Always"],
            h.Logs.Logger.Lines.Where(l => l.Contains("Speech", StringComparison.Ordinal)));

        await h.TicksAsync(TimeSpan.FromMinutes(4));
        await h.FromAppAsync();
        Assert.Single(h.Apns.Speeches);

        await NextReadingAsync(h, 128);
        Assert.Equal([130, 128], h.Apns.Speeches.Select(s => s.Current));
        Assert.Equal(h.Now, device.LastPushOkAt);
    }

    [Fact]
    public async Task SameReadingFromAnotherUploader_IsNotSpokenAgain()
    {
        var h = new EngineHarness();
        SpeakDevice(h);
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        Assert.Single(h.Apns.Speeches);

        h.Advance(TimeSpan.FromSeconds(11));
        h.Reading(130);
        await h.TicksAsync(TimeSpan.FromMinutes(4.5));
        Assert.Single(h.Apns.Speeches);

        await NextReadingAsync(h, 135);
        Assert.Equal(2, h.Apns.Speeches.Count);
    }

    [Fact]
    public async Task AppWithoutSpeech_IsNotSentSpeech()
    {
        var h = new EngineHarness();
        SpeakDevice(h, edit: r => r.Settings["features"] = new JsonArray("receipts"));
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        Assert.Empty(h.Apns.Speeches);
    }

    [Fact]
    public async Task Restart_DoesNotRepeatTheLatestReading()
    {
        var h = new EngineHarness();
        SpeakDevice(h);
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        Assert.Single(h.Apns.Speeches);

        h.Restart();
        await h.ReconcileAsync();
        await h.NewDataAsync();
        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.Speeches);

        await NextReadingAsync(h, 135);
        Assert.Equal([130, 135], h.Apns.Speeches.Select(s => s.Current));
    }

    [Fact]
    public async Task NeedsAPreviousReading()
    {
        var h = new EngineHarness();
        SpeakDevice(h);
        await h.ReconcileAsync();
        await h.NewDataAsync();
        await NextReadingAsync(h, 120);
        Assert.Empty(h.Apns.Speeches);

        await NextReadingAsync(h, 125);
        Assert.Equal(120, Assert.Single(h.Apns.Speeches).Previous);
    }

    [Fact]
    public async Task StaleReading_IsNotSpoken()
    {
        var h = new EngineHarness();
        SpeakDevice(h);
        await StartAsync(h, 120, 125);

        h.NextReading(130);
        h.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await h.NewDataAsync();
        Assert.Empty(h.Apns.Speeches);

        await NextReadingAsync(h, 135);
        Assert.Equal(130, Assert.Single(h.Apns.Speeches).Previous);
    }

    [Fact]
    public async Task NoConditionHolds_NothingIsSpoken()
    {
        var h = new EngineHarness();
        SpeakDevice(h, new JsonObject
        {
            ["speakBGAlways"] = false,
            ["speakLowBG"] = true,
            ["speakProactiveLowBG"] = true,
            ["speakHighBG"] = true,
        });
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        await NextReadingAsync(h, 125);
        Assert.Empty(h.Apns.Speeches);

        await NextReadingAsync(h, 72);
        Assert.Equal(72, Assert.Single(h.Apns.Speeches).Current);
        Assert.Contains(h.Logs.Logger.Lines, l => l.StartsWith("Information: Speech sent to") && l.EndsWith(": Low"));
    }

    [Fact]
    public async Task ProactiveLow_SpeaksOnTheFirstValueOfTheAlarmForecast()
    {
        var h = new EngineHarness();
        var forecast = new JsonArray(80, 120, 130);
        h.Nightscout.Route("/api/v1/devicestatus.json", _ => new JsonArray(new JsonObject
        {
            ["created_at"] = h.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["device"] = "loop://iPhone",
            ["loop"] = new JsonObject
            {
                ["timestamp"] = h.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["predicted"] = new JsonObject { ["values"] = forecast.DeepClone() },
            },
        }).ToJsonString());
        SpeakDevice(h, new JsonObject { ["speakBGAlways"] = false, ["speakProactiveLowBG"] = true });
        await StartAsync(h, 120, 125);

        await NextReadingAsync(h, 126);
        Assert.Empty(h.Apns.Speeches);

        forecast = new JsonArray(72, 120, 130);
        await NextReadingAsync(h, 127);
        Assert.Equal(127, Assert.Single(h.Apns.Speeches).Current);
        Assert.Contains(h.Logs.Logger.Lines, l => l.EndsWith(": Proactive Low"));
    }

    [Fact]
    public async Task SpeakBgOff_AlarmsOffOrNoApns_NothingIsSpoken()
    {
        var h = new EngineHarness();
        SpeakDevice(h, new JsonObject { ["speakBG"] = false });
        SpeakDevice(h, edit: r => r.Settings["alarms"] = false);
        SpeakDevice(h, edit: r => r.Apns = null);
        h.AddDevice(r => r.ClientSettings = new JsonObject { ["speakBG"] = true });
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        await NextReadingAsync(h, 135);
        Assert.Empty(h.Apns.Speeches);
    }

    [Fact]
    public async Task TurnedOn_StartsFromTheCurrentReading()
    {
        var h = new EngineHarness();
        var device = SpeakDevice(h, new JsonObject { ["speakBG"] = false });
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);

        device.ClientSettings["speakBG"] = true;
        await h.FromAppAsync();
        Assert.Empty(h.Apns.Speeches);

        await NextReadingAsync(h, 135);
        Assert.Equal(135, Assert.Single(h.Apns.Speeches).Current);

        device.ClientSettings["speakBG"] = false;
        await NextReadingAsync(h, 140);
        device.ClientSettings["speakBG"] = true;
        await h.FromAppAsync();
        Assert.Single(h.Apns.Speeches);
    }

    [Fact]
    public async Task Speech_CarriesTheDevicesUnitAndLanguage()
    {
        var h = new EngineHarness();
        SpeakDevice(h, new JsonObject { ["units"] = "mmol/L", ["speakLanguage"] = "sv" });
        await StartAsync(h, 120, 125);
        await NextReadingAsync(h, 130);
        var push = Assert.Single(h.Apns.Speeches);
        Assert.Equal(new FakeApns.SpeakPush("7.2 → +0.3", 130, 125, "sv", true, h.Now), push);
    }

    [Fact]
    public async Task FailedSpeech_IsLoggedAndCountsTowardPushHealth()
    {
        var h = new EngineHarness();
        var device = SpeakDevice(h);
        await StartAsync(h, 120, 125);
        h.Apns.Result = new PushResult(false, "400 BadDeviceToken", true, 400);
        await NextReadingAsync(h, 130);
        Assert.Single(h.Apns.Speeches);
        Assert.Contains($"Warning: Speech to {device.Id} failed: 400 BadDeviceToken", h.Logs.Logger.Lines);
        Assert.NotNull(device.PushFailingSince);
        Assert.True(device.PushTokenGone);

        await h.TicksAsync(TimeSpan.FromMinutes(2));
        Assert.Single(h.Apns.Speeches);
    }

    [Theory]
    [InlineData(72, 100, "Low")]
    [InlineData(100, 72, "Low")]
    [InlineData(73, 73, null)]
    public void Low_CurrentOrPreviousAtOrBelowTheLimit(int current, int previous, string? reason) =>
        Assert.Equal(reason, HubEngine.SpeakReason(Settings("""{"speakBGAlways":false,"speakLowBG":true}"""),
            current, previous, NoForecast));

    [Theory]
    [InlineData(180, 100, "High")]
    [InlineData(100, 180, "High")]
    [InlineData(179, 179, null)]
    public void High_CurrentOrPreviousAtOrAboveTheLimit(int current, int previous, string? reason) =>
        Assert.Equal(reason, HubEngine.SpeakReason(Settings("""{"speakBGAlways":false,"speakHighBG":true}"""),
            current, previous, NoForecast));

    [Fact]
    public void Limits_AreTruncatedToWholeMgdl()
    {
        var s = Settings("""{"speakBGAlways":false,"speakHighBG":true,"speakHighBGLimit":180.9}""");
        Assert.Equal("High", HubEngine.SpeakReason(s, 180, 170, NoForecast));
        var drop = Settings("""{"speakBGAlways":false,"speakProactiveLowBG":true,"speakFastDropDelta":10.9}""");
        Assert.Equal("Proactive Low", HubEngine.SpeakReason(drop, 150, 160, NoForecast));
    }

    [Theory]
    [InlineData(72, 100, "Proactive Low")]
    [InlineData(100, 72, "Proactive Low")]
    [InlineData(150, 160, "Proactive Low")]
    [InlineData(151, 160, null)]
    [InlineData(180, 190, "Proactive Low")]
    [InlineData(181, 200, null)]
    [InlineData(73, 73, null)]
    public void ProactiveLow_LowOrFastDropAtOrBelowTheHighLimit(int current, int previous, string? reason) =>
        Assert.Equal(reason, HubEngine.SpeakReason(Settings("""{"speakBGAlways":false,"speakProactiveLowBG":true}"""),
            current, previous, NoForecast));

    [Theory]
    [InlineData(new[] { 72, 200 }, "Proactive Low")]
    [InlineData(new[] { 73, 40 }, null)]
    [InlineData(new int[0], null)]
    public void ProactiveLow_FirstForecastValueAtOrBelowTheLowLimit(int[] forecast, string? reason)
    {
        var s = Settings("""{"speakBGAlways":false,"speakProactiveLowBG":true}""");
        var values = forecast.Select((v, i) => new GlucoseValue(v, EngineHarness.Start.AddMinutes(5 * i))).ToList();
        Assert.Equal(reason, HubEngine.SpeakReason(s, 120, 120, values));
    }

    [Fact]
    public void Conditions_AreTakenInTheAppsOrder()
    {
        var all = Settings("""{"speakLowBG":true,"speakProactiveLowBG":true,"speakHighBG":true}""");
        Assert.Equal("Always", HubEngine.SpeakReason(all, 120, 120, NoForecast));
        var conditions = Settings("""{"speakBGAlways":false,"speakLowBG":true,"speakProactiveLowBG":true,"speakHighBG":true}""");
        Assert.Equal("Low", HubEngine.SpeakReason(conditions, 70, 190, NoForecast));
        Assert.Equal("Proactive Low", HubEngine.SpeakReason(conditions, 170, 190, NoForecast));
        Assert.Equal("High", HubEngine.SpeakReason(conditions, 185, 190, NoForecast));
        Assert.Null(HubEngine.SpeakReason(Settings("""{"speakBGAlways":false}"""), 40, 400,
            [new GlucoseValue(40, EngineHarness.Start)]));
    }

    [Fact]
    public void Payload_IsAnActiveAlertWithTheReadingsForTheExtension()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var (payload, headers) = ApnsClient.Speak("7.2 → +0.3", 130, 125, "sv", true, now);
        Assert.Equal(
            """{"aps":{"alert":{"title":"7.2 → +0.3","body":""},"sound":"default","interruption-level":"active","thread-id":"lfhub-speak","mutable-content":1},"lfhubSpeak":{"current":130,"previous":125,"language":"sv","mmol":true}}""",
            payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(2, headers.Count);
        Assert.Equal("lfhub-speak", headers["apns-collapse-id"]);
        Assert.Equal((unix + 300).ToString(System.Globalization.CultureInfo.InvariantCulture), headers["apns-expiration"]);
    }

    [Fact]
    public void ClientSettings_DefaultsAreTheApps()
    {
        var s = Settings("{}");
        Assert.False(s.Speak);
        Assert.True(s.SpeakAlways);
        Assert.False(s.SpeakLow);
        Assert.False(s.SpeakProactiveLow);
        Assert.False(s.SpeakHigh);
        Assert.Equal(10, s.SpeakFastDropDelta);
        Assert.Equal(72, s.SpeakLowLimit);
        Assert.Equal(180, s.SpeakHighLimit);
        Assert.Equal("en", s.SpeakLanguage);
    }

    [Fact]
    public void ClientSettings_ReadsTheSpeakKeys()
    {
        var s = Settings("""
            {"speakBG":true,"speakBGAlways":false,"speakLowBG":true,"speakProactiveLowBG":true,"speakHighBG":true,
             "speakFastDropDelta":15.5,"speakLowBGLimit":65,"speakHighBGLimit":250.5,"speakLanguage":"sv"}
            """);
        Assert.True(s.Speak);
        Assert.False(s.SpeakAlways);
        Assert.True(s.SpeakLow);
        Assert.True(s.SpeakProactiveLow);
        Assert.True(s.SpeakHigh);
        Assert.Equal(15.5, s.SpeakFastDropDelta);
        Assert.Equal(65, s.SpeakLowLimit);
        Assert.Equal(250.5, s.SpeakHighLimit);
        Assert.Equal("sv", s.SpeakLanguage);
    }

    [Fact]
    public void ClientSettings_MistypedSpeakKeysTakeTheDefaults()
    {
        var s = Settings("""
            {"speakBG":1,"speakBGAlways":"false","speakLowBG":"true","speakProactiveLowBG":null,"speakHighBG":[true],
             "speakFastDropDelta":"15","speakLowBGLimit":{},"speakHighBGLimit":null,"speakLanguage":""}
            """);
        var d = ClientSettings.Default;
        Assert.Equal(d.Speak, s.Speak);
        Assert.Equal(d.SpeakAlways, s.SpeakAlways);
        Assert.Equal(d.SpeakLow, s.SpeakLow);
        Assert.Equal(d.SpeakProactiveLow, s.SpeakProactiveLow);
        Assert.Equal(d.SpeakHigh, s.SpeakHigh);
        Assert.Equal(d.SpeakFastDropDelta, s.SpeakFastDropDelta);
        Assert.Equal(d.SpeakLowLimit, s.SpeakLowLimit);
        Assert.Equal(d.SpeakHighLimit, s.SpeakHighLimit);
        Assert.Equal(d.SpeakLanguage, s.SpeakLanguage);
    }
}
