namespace LFHub.Engine;

/// <summary>The Live Activity's trend values.</summary>
public static class Trend
{
    /// Maps a Nightscout or Dexcom direction string, e.g. "FortyFiveUp", like GlucoseSnapshot.Trend(code:).
    public static string FromDirection(string? code)
    {
        var raw = code?.Trim().ToLowerInvariant() ?? "";
        if (raw.Contains("doubleup") || raw.Contains("rapidrise") || raw is "up2" or "upfast") return "upFast";
        if (raw.Contains("fortyfiveup")) return "upSlight";
        if (raw.Contains("singleup") || raw is "up" or "up1" or "rising") return "up";
        if (raw.Contains("flat") || raw is "steady" or "none") return "flat";
        if (raw.Contains("doubledown") || raw.Contains("rapidfall") || raw is "down2" or "downfast") return "downFast";
        if (raw.Contains("fortyfivedown")) return "downSlight";
        if (raw.Contains("singledown") || raw is "down" or "down1" or "falling") return "down";
        return "unknown";
    }
}
