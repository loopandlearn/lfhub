using System.Text.Json.Nodes;
using LFHub.Engine.State;
using LFHub.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace LFHub.Tests.State;

/// LoopFollow's yesterday overlay needs one more day of BG history than downloadDays.
public class YesterdayLineTests
{
    private static ClientSettings Settings(string json) => ClientSettings.FromJson(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void YesterdayLine_AddsADayToTheBgFetch()
    {
        Assert.Equal(1, Settings("""{"downloadDays":1}""").BgFetchDays);
        Assert.Equal(2, Settings("""{"downloadDays":1,"showYesterdayLine":true}""").BgFetchDays);
        Assert.Equal(1, Settings("""{"downloadDays":1,"showYesterdayLine":"yes"}""").BgFetchDays);
    }

    [Fact]
    public void DownloadDays_IsAtMostWhatLoopFollowOffers()
    {
        Assert.Equal(ClientSettings.MaxDownloadDays, Settings("""{"downloadDays":1000000}""").DownloadDays);
        Assert.Equal(ClientSettings.MaxDownloadDays + 1, Settings("""{"downloadDays":1000000,"showYesterdayLine":true}""").BgFetchDays);
    }

    [Fact]
    public async Task BgFetch_CoversTheYesterdayLineOfAnyDevice()
    {
        var ns = new FakeNightscout("ns.example.org").Route("/api/v1/entries.json", "[]");
        using var http = new HttpClient(ns);
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var state = new PatientState(http, "https://ns.example.org", null, NullLogger.Instance) { UtcNow = () => now };
        state.UpdateFetchSettings([Settings("""{"downloadDays":1}"""), Settings("""{"downloadDays":1,"showYesterdayLine":true}""")]);

        await state.RefreshAsync(true, TestContext.Current.CancellationToken);

        var entries = Assert.Single(ns.Requests, r => r.StartsWith("/api/v1/entries.json", StringComparison.Ordinal));
        Assert.Contains($"count={2 * 4 * 24 * 60 / 5}", entries, StringComparison.Ordinal);
    }
}
