using System.Text.Json;
using System.Text.Json.Nodes;
using LFHub.Engine;
using LFHub.Engine.Alarms;

namespace LFHub.Tests.Alarms;

/// <summary>
/// The alarm export against JSON written by LoopFollow's own Swift encoder
/// (Fixtures/AlarmSettings: every type with its defaults and fixed ids, plus a
/// low with every option changed and a missed-bolus alarm with its extras).
/// </summary>
public class AlarmSettingsJsonTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AlarmSettings", name));

    private static readonly string Export = Fixture("export.json");

    [Fact]
    public void Parse_ReadsEveryTypeInSwiftOrder()
    {
        var (alarms, _) = AlarmSettingsJson.Parse(Export);
        Assert.Equal(26, alarms.Count);
        Assert.Equal(Enum.GetValues<AlarmType>(), alarms.Take(24).Select(a => a.Type));
    }

    [Fact]
    public void Create_DefaultsMatchSwiftInit()
    {
        var (alarms, cfg) = AlarmSettingsJson.Parse(Export);
        var types = Enum.GetValues<AlarmType>();
        for (var i = 0; i < types.Length; i++)
        {
            var expected = Alarm.Create(types[i]);
            expected.Id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}");
            Assert.Equal(AlarmSettingsJson.Serialize([expected], cfg, "x"), AlarmSettingsJson.Serialize([alarms[i]], cfg, "x"));
        }
    }

    [Fact]
    public void Parse_ReadsEveryChangedOption()
    {
        var low = AlarmSettingsJson.Parse(Export).Alarms[24];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_125).UtcDateTime, low.SnoozedUntil);
        Assert.Equal(Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"), low.SoundFile.CustomId);
        Assert.Equal("custom:AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", low.SoundFile.Id);
        Assert.Equal(PlaySoundOption.Night, low.PlaySoundOption);
        Assert.Equal(RepeatSoundOption.Day, low.RepeatSoundOption);
        Assert.Equal(ActiveOption.Night, low.ActiveOption);
        Assert.False(low.IsEnabled);
        Assert.Equal(55.5, low.BelowBG);
        Assert.Equal(10, low.PersistentMinutes);
        Assert.Equal(20, low.PredictiveMinutes);
        Assert.True(low.SuppressIfRising);
        Assert.Equal(15, low.SoundDelay);
        Assert.Equal(20, low.SnoozeDuration);
        Assert.Equal("Urgent låg ⚠️ \"quoted\" / slash", low.Name);
    }

    [Fact]
    public void Parse_ReadsMissedBolusExtras()
    {
        var mb = AlarmSettingsJson.Parse(Export).Alarms[25];
        Assert.Equal(20, mb.MissedBolusPrebolusWindow);
        Assert.Equal(0.15, mb.MissedBolusIgnoreSmallBolusUnits);
        Assert.Equal(4, mb.MissedBolusIgnoreUnderGrams);
        Assert.Equal(70, mb.MissedBolusIgnoreUnderBG);
        Assert.Equal(3, mb.BolusCountThreshold);
        Assert.Equal(45, mb.BolusWindowMinutes);
    }

    [Fact]
    public void Parse_ReadsTheConfiguration()
    {
        var cfg = AlarmSettingsJson.Parse(Export).Config;
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_793_003_600), cfg.SnoozeUntil);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_793_001_800.5), cfg.MuteUntil);
        Assert.Equal(new TimeOfDay(7, 30), cfg.DayStart);
        Assert.Equal(new TimeOfDay(21, 45), cfg.NightStart);
        Assert.Equal(0.7f, cfg.ForcedOutputVolume);
        Assert.True(cfg.OverrideSystemOutputVolume);
        Assert.True(cfg.AudioDuringCalls);
        Assert.True(cfg.IgnoreZeroBG);
        Assert.False(cfg.AutoSnoozeCGMStart);
        Assert.False(cfg.EnableVolumeButtonSnooze);
    }

    [Fact]
    public void Serialize_WritesWhatTheSwiftEncoderWrites()
    {
        var (alarms, cfg) = AlarmSettingsJson.Parse(Export);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AlarmSettingsJson.Serialize(alarms, cfg, "5.0.0")), JsonNode.Parse(Export)));
    }

    [Fact]
    public void Parse_DefaultExportHasNoSnoozeOrMute()
    {
        var (alarms, cfg) = AlarmSettingsJson.Parse(Fixture("export-default.json"));
        Assert.Null(cfg.SnoozeUntil);
        Assert.Null(cfg.MuteUntil);
        Assert.Equal(new TimeOfDay(6, 0), cfg.DayStart);
        Assert.Equal(AlarmType.High, alarms[0].Type);
        Assert.Null(alarms[0].SnoozedUntil);
    }

    [Fact]
    public void ParseLenient_SkipsAnUnknownTypeAndKeepsTheRest()
    {
        var node = JsonNode.Parse(Export)!;
        node["alarms"]![0]!["type"] = "Some Future Alert";
        var (alarms, _) = AlarmSettingsJson.ParseLenient(node.ToJsonString(), out var skipped);
        Assert.Equal(1, skipped);
        Assert.Equal(25, alarms.Count);
        Assert.DoesNotContain(alarms, a => a.Id == Guid.Parse("00000000-0000-0000-0000-000000000001"));
    }

    [Fact]
    public void ParseLenient_SkipsAnAlarmWithAnUnknownOptionOrMissingSound()
    {
        var node = JsonNode.Parse(Export)!;
        node["alarms"]![1]!["playSoundOption"] = "sometimes";
        node["alarms"]![2]!.AsObject().Remove("soundFile");
        var (alarms, _) = AlarmSettingsJson.ParseLenient(node.ToJsonString(), out var skipped);
        Assert.Equal(2, skipped);
        Assert.Equal(24, alarms.Count);
    }

    [Fact]
    public void Parse_IsLenientToo()
    {
        var node = JsonNode.Parse(Export)!;
        node["alarms"]![0]!["type"] = "Some Future Alert";
        Assert.Equal(25, AlarmSettingsJson.Parse(node.ToJsonString()).Alarms.Count);
    }

    [Fact]
    public void TryParseAlarms_ReportsSkippedAlarms()
    {
        var node = JsonNode.Parse(Export)!;
        node["alarms"]![3]!["type"] = "Some Future Alert";
        Assert.True(HubEngine.TryParseAlarms(node.ToJsonString(), out var parsed, out var skipped, out var error));
        Assert.Equal(1, skipped);
        Assert.Equal(25, parsed.Alarms.Count);
        Assert.Equal("", error);
    }

    [Fact]
    public void TryParseAlarms_RejectsABrokenExport()
    {
        Assert.False(HubEngine.TryParseAlarms("{\"alarms\":[]}", out _, out _, out var error));
        Assert.StartsWith("alarm export does not parse", error);
        Assert.False(HubEngine.TryParseAlarms("not json", out _, out _, out _));
    }

    [Fact]
    public void Parse_MissingConfigurationKeyIsRejected()
    {
        var node = JsonNode.Parse(Export)!;
        node["alarmConfiguration"]!.AsObject().Remove("ignoreZeroBG");
        Assert.Throws<JsonException>(() => AlarmSettingsJson.Parse(node.ToJsonString()));
    }

    [Fact]
    public void Parse_DefaultsKeysSwiftDefaultsAndReadsKeyedBuiltinSound()
    {
        var node = JsonNode.Parse(Export)!;
        var a0 = node["alarms"]![0]!.AsObject();
        foreach (var k in new[] { "isEnabled", "snoozeDuration", "playSoundOption", "repeatSoundOption", "soundDelay", "activeOption", "suppressIfRising" })
            a0.Remove(k);
        a0["soundFile"] = JsonNode.Parse("""{"kind":"builtin","name":"Indeed"}""");
        var first = AlarmSettingsJson.Parse(node.ToJsonString()).Alarms[0];
        Assert.True(first.IsEnabled);
        Assert.Equal(5, first.SnoozeDuration);
        Assert.Equal(0, first.SoundDelay);
        Assert.Equal(PlaySoundOption.Always, first.PlaySoundOption);
        Assert.Equal(SoundFile.Builtin("Indeed"), first.SoundFile);
    }

    [Fact]
    public void ReferenceSeconds_RoundTrip()
    {
        var t = new DateTime(2026, 10, 24, 12, 0, 0, 125, DateTimeKind.Utc);
        Assert.Equal(t, AlarmSettingsJson.FromReferenceSeconds(AlarmSettingsJson.ToReferenceSeconds(t)));
        Assert.Equal(0, AlarmSettingsJson.ToReferenceSeconds(AlarmSettingsJson.ReferenceDate));
    }
}
