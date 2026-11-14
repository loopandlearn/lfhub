using LFHub.Engine.State;
using NodaTime;

namespace LFHub.Tests.State;

/// Zones built from the bundled tzdb against the host's database.
public sealed class TimeZonesTests
{
    private static readonly int From = DateTime.UtcNow.Year - 3;
    private static readonly int To = DateTime.UtcNow.Year + 10;

    [Fact]
    public void BundledZonesMatchTheHostAroundEveryChange()
    {
        Assert.SkipUnless(TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out _), "no time zone database on this host");
        var mismatches = new List<string>();
        var compared = 0;
        foreach (var id in DateTimeZoneProviders.Tzdb.Ids)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var system)) continue;
            // The host's data can be older than NodaTime's; compare only where both agree on the rules today.
            var bundled = TimeZones.FromTzdb(id)!;
            var tz = DateTimeZoneProviders.Tzdb[id];
            var start = Instant.FromUtc(From, 1, 1, 0, 0);
            var end = Instant.FromUtc(To, 1, 1, 0, 0);
            var samples = new List<DateTime>();
            foreach (var interval in tz.GetZoneIntervals(start, end).Skip(1))
            {
                var t = interval.Start.ToDateTimeUtc();
                samples.Add(t.AddMinutes(-1));
                samples.Add(t.AddMinutes(1));
            }
            for (var t = start.ToDateTimeUtc(); t < end.ToDateTimeUtc(); t = t.AddDays(5).AddHours(7))
                samples.Add(t);
            compared++;
            foreach (var utc in samples)
            {
                var expected = tz.GetUtcOffset(Instant.FromDateTimeUtc(utc)).ToTimeSpan();
                if (system.GetUtcOffset(utc) != expected) continue;
                var actual = bundled.GetUtcOffset(utc);
                if (actual != expected)
                {
                    mismatches.Add($"{id} {utc:u}: {actual} != {expected}");
                    break;
                }
            }
        }
        Assert.True(compared > 300, $"only {compared} zones on this host");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Theory]
    [InlineData("Europe/Stockholm", "2026-07-01T12:00:00Z", 2)]
    [InlineData("Europe/Stockholm", "2026-12-01T12:00:00Z", 1)]
    [InlineData("Australia/Sydney", "2026-01-15T12:00:00Z", 11)]
    [InlineData("Australia/Sydney", "2026-07-15T12:00:00Z", 10)]
    [InlineData("Asia/Calcutta", "2026-07-01T12:00:00Z", 5.5)]
    [InlineData("America/Sao_Paulo", "2026-01-15T12:00:00Z", -3)]
    public void BundledZoneGivesTheLocalOffset(string id, string utc, double hours)
    {
        var zone = TimeZones.FromTzdb(id)!;
        var at = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(TimeSpan.FromHours(hours), zone.GetUtcOffset(at));
    }

    [Fact]
    public void UnknownZoneIsNotFound()
    {
        Assert.False(TimeZones.TryFind("Mars/Olympus_Mons", out _));
        Assert.False(TimeZones.TryFind("", out _));
    }

    [Fact]
    public void LocalToUtcRoundTripsThroughABundledZone()
    {
        var zone = TimeZones.FromTzdb("America/New_York")!;
        var local = new DateTime(2026, 11, 14, 8, 30, 0);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        Assert.Equal(new DateTime(2026, 11, 14, 13, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(local, TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
    }
}
