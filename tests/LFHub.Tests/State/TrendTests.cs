using LFHub.Engine;

namespace LFHub.Tests.State;

public class TrendTests
{
    [Theory]
    [InlineData("DoubleUp", "upFast")]
    [InlineData("TripleUp", "unknown")]
    [InlineData("rapidRise", "upFast")]
    [InlineData("FortyFiveUp", "upSlight")]
    [InlineData("SingleUp", "up")]
    [InlineData("rising", "up")]
    [InlineData("Flat", "flat")]
    [InlineData(" steady ", "flat")]
    [InlineData("NONE", "flat")]
    [InlineData("FortyFiveDown", "downSlight")]
    [InlineData("SingleDown", "down")]
    [InlineData("DoubleDown", "downFast")]
    [InlineData("NOT COMPUTABLE", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void Trend_MapsLikeGlucoseSnapshot(string? code, string expected) =>
        Assert.Equal(expected, Trend.FromDirection(code));
}
