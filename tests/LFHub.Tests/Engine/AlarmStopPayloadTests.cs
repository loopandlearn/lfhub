using LFHub.Apns;

namespace LFHub.Tests.Engine;

/// The stop push's text: "Cleared" for a cleared condition, the alarm's own text for an alarm that rang out or was silenced.
public class AlarmStopPayloadTests
{
    private static readonly Guid Id = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");

    [Fact]
    public void Cleared_SaysCleared()
    {
        var payload = ApnsClient.AlarmStop(Id, "Low", "70 → -20", "CGM reported: ok", AlarmStopReason.Cleared);
        Assert.Equal(
            """{"aps":{"alert":{"title":"Low","subtitle":"Cleared"},"thread-id":"lfhub-alarm","interruption-level":"passive","mutable-content":1},"lfhubStopAlarmId":"0F0E0D0C-0B0A-0908-0706-050403020100"}""",
            payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Theory]
    [InlineData(AlarmStopReason.RingDone)]
    [InlineData(AlarmStopReason.Silenced)]
    public void RingDoneAndSilenced_KeepTheAlarmsOwnText(AlarmStopReason reason)
    {
        var payload = ApnsClient.AlarmStop(Id, "Low", "70 → -20", "CGM reported: ok", reason);
        var alert = payload["aps"]!["alert"]!;
        Assert.Equal("Low", alert["title"]!.GetValue<string>());
        Assert.Equal("70 → -20", alert["subtitle"]!.GetValue<string>());
        Assert.Equal("CGM reported: ok", alert["body"]!.GetValue<string>());
        Assert.Equal("passive", payload["aps"]!["interruption-level"]!.GetValue<string>());
        Assert.Null(payload["aps"]!["sound"]);
        Assert.Equal("0F0E0D0C-0B0A-0908-0706-050403020100", payload["lfhubStopAlarmId"]!.GetValue<string>());
    }
}
