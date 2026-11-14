using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using NodaTime;

namespace LFHub.Engine.State;

/// <summary>
/// IANA time zones by id. The host's time zone database comes first. A zone the host does not
/// know (an image without zoneinfo, or Windows with invariant globalization) is built from the
/// tzdb that NodaTime carries, as adjustment rules for the years <see cref="FirstYear"/> to
/// <see cref="LastYear"/>; outside them it keeps its current standard offset. In a year with more
/// than two changes, a change at midnight or a jump across the date line can be off on its day.
/// </summary>
public static class TimeZones
{
    internal const int FirstYear = 2000;
    internal const int LastYear = 2100;
    private static readonly TimeSpan MaxDaylight = TimeSpan.FromHours(14);

    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Bundled = new(StringComparer.Ordinal);
    private static readonly TimeZoneInfo.TransitionTime StartOfYear =
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 1, 1);
    private static readonly TimeZoneInfo.TransitionTime EndOfYear =
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 59, 999), 12, 31);

    /// <summary>The tzdb version the bundled zones come from.</summary>
    public static string BundledVersion => DateTimeZoneProviders.Tzdb.VersionId;

    public static bool TryFind(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrEmpty(id)) return false;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out zone)) return true;
        zone = Bundled.GetOrAdd(id, FromTzdb);
        return zone is not null;
    }

    internal static TimeZoneInfo? FromTzdb(string id)
    {
        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(id);
        if (tz is null) return null;
        var baseOffset = tz.GetZoneInterval(SystemClock.Instance.GetCurrentInstant()).StandardOffset.ToTimeSpan();
        var rules = new List<TimeZoneInfo.AdjustmentRule>();
        for (var year = FirstYear; year <= LastYear; year++)
            rules.AddRange(YearRules(tz, year, baseOffset));
        return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id, id, rules.ToArray());
    }

    /// <summary>
    /// The year's rules from its wall-clock offsets alone, so negative daylight saving and changes of standard
    /// offset need no special case. A year with at most two offsets and two changes gets one rule, the higher
    /// offset as its daylight time. Any other year gets a rule per change, from the day of that change.
    /// </summary>
    private static IEnumerable<TimeZoneInfo.AdjustmentRule> YearRules(DateTimeZone tz, int year, TimeSpan baseOffset)
    {
        var yearStart = tz.AtStartOfDay(new LocalDate(year, 1, 1)).ToInstant();
        var yearEnd = tz.AtStartOfDay(new LocalDate(year + 1, 1, 1)).ToInstant();
        var walls = new List<TimeSpan>();
        var changes = new List<DateTime>();
        foreach (var interval in tz.GetZoneIntervals(yearStart, yearEnd))
        {
            var wall = interval.WallOffset.ToTimeSpan();
            if (walls.Count > 0 && walls[^1] == wall) continue;
            if (walls.Count > 0) changes.Add(Local(interval.Start, walls[^1]));
            walls.Add(wall);
        }
        var first = new DateTime(year, 1, 1);
        var last = new DateTime(year, 12, 31);

        var distinct = walls.Distinct().ToList();
        if (distinct.Count == 1)
        {
            if (walls[0] != baseOffset)
                yield return Rule(first, last, TimeSpan.Zero, StartOfYear, EndOfYear, walls[0] - baseOffset);
            yield break;
        }
        if (distinct.Count == 2 && changes.Count <= 2 && distinct.Max() - distinct.Min() <= MaxDaylight)
        {
            // Daylight time starts at the change up and ends at the change down, each on the clock before it,
            // or at the year's edges when the year opens or closes in it.
            var low = distinct.Min();
            var high = distinct.Max();
            var up = Enumerable.Range(1, changes.Count).FirstOrDefault(i => walls[i] == high);
            var down = Enumerable.Range(1, changes.Count).FirstOrDefault(i => walls[i] == low);
            yield return Rule(first, last, high - low,
                up > 0 ? Fixed(changes[up - 1]) : StartOfYear,
                down > 0 ? Fixed(changes[down - 1]) : EndOfYear,
                low - baseOffset);
            yield break;
        }

        if (walls[0] != baseOffset && changes[0].Date > first)
            yield return Rule(first, changes[0].Date.AddDays(-1), TimeSpan.Zero, StartOfYear, EndOfYear,
                walls[0] - baseOffset);
        for (var i = 0; i < changes.Count; i++)
        {
            var from = changes[i].Date;
            var to = i + 1 < changes.Count ? changes[i + 1].Date.AddDays(-1) : last;
            if (to < from) continue;
            var before = walls[i];
            var after = walls[i + 1];
            // A jump across the date line is beyond what a rule's daylight time can hold; it takes effect from the day.
            if ((after - before).Duration() > MaxDaylight)
                yield return Rule(from, to, TimeSpan.Zero, StartOfYear, EndOfYear, after - baseOffset);
            else yield return after > before
                ? Rule(from, to, after - before, Fixed(changes[i]), EndOfYear, before - baseOffset)
                : Rule(from, to, before - after, StartOfYear, Fixed(changes[i]), after - baseOffset);
        }
    }

    private static TimeZoneInfo.AdjustmentRule Rule(DateTime from, DateTime to, TimeSpan daylight,
        TimeZoneInfo.TransitionTime begin, TimeZoneInfo.TransitionTime end, TimeSpan baseDelta) =>
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(from, to, daylight, begin, end, baseDelta);

    private static DateTime Local(Instant instant, TimeSpan offset) =>
        DateTime.SpecifyKind(instant.ToDateTimeUtc() + offset, DateTimeKind.Unspecified);

    private static TimeZoneInfo.TransitionTime Fixed(DateTime local) =>
        TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1) + local.TimeOfDay, local.Month, local.Day);
}
