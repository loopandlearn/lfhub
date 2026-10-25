using LFHub.Engine.State;

namespace LFHub.Tests.State;

/// Readings uploaded twice, by Trio and a Dexcom bridge about 10 s apart, count once.
public class DedupTests
{
    private static PatientState.Reading R(int sgv, double date) => new(sgv, date, "Flat");

    [Fact]
    public void SecondUploadersCopy_IsDroppedAndTheEarliestKept()
    {
        var kept = PatientState.Dedup([R(130, 611), R(130, 600), R(125, 311), R(125, 300)]);
        Assert.Equal([R(130, 600), R(125, 300)], kept);
    }

    [Fact]
    public void CopyRoundedDifferently_IsDropped()
    {
        var kept = PatientState.Dedup([R(39, 611), R(40, 600)]);
        Assert.Equal([R(40, 600)], kept);
    }

    [Fact]
    public void ReadingsAMinuteApart_AreKept()
    {
        var kept = PatientState.Dedup([R(102, 720), R(101, 660), R(100, 600)]);
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void ReadingThirtySecondsLater_IsKept()
    {
        var kept = PatientState.Dedup([R(101, 630), R(100, 600)]);
        Assert.Equal(2, kept.Count);
    }
}
