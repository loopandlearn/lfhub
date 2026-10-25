using System.Text.Json.Nodes;
using LFHub.Engine.State;

namespace LFHub.Tests.State;

/// A reading or loop time from a clock running ahead is dropped, so it cannot hold off alarms.
public class FutureTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly double NowUnix = (Now - DateTime.UnixEpoch).TotalSeconds;

    private static PatientState.Reading R(int sgv, double date) => new(sgv, date, "Flat");

    [Fact]
    public void ReadingsAheadOfTheTolerance_AreDropped()
    {
        var ahead = NowUnix + PatientState.FutureToleranceSeconds + 1;
        var edge = NowUnix + PatientState.FutureToleranceSeconds;
        var kept = PatientState.NotFuture([R(200, ahead), R(120, edge), R(110, NowUnix - 300)], NowUnix);
        Assert.Equal([R(120, edge), R(110, NowUnix - 300)], kept);
    }

    private static double? LoopTime(string deviceStatus)
    {
        var state = new DeviceStatusState();
        state.Apply(JsonNode.Parse(deviceStatus), Now, null, TimeZoneInfo.Utc);
        return state.LastLoopTime;
    }

    [Fact]
    public void PumpClockAhead_IsNotTheLatestLoop()
    {
        Assert.Null(LoopTime("""[{"pump":{"clock":"2026-10-25T13:00:00Z"}}]"""));
        Assert.Equal(NowUnix - 60, LoopTime("""[{"pump":{"clock":"2026-10-25T11:59:00Z"}}]"""));
    }

    [Fact]
    public void LoopTimestampAhead_IsNotTheLatestLoop()
    {
        Assert.Null(LoopTime("""[{"loop":{"timestamp":"2026-10-25T13:00:00Z"}}]"""));
        Assert.Equal(NowUnix - 60, LoopTime("""[{"loop":{"timestamp":"2026-10-25T11:59:00Z"}}]"""));
    }
}
