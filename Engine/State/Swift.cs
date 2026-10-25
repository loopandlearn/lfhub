using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LFHub.Engine.State;

/// <summary>
/// The value semantics LoopFollow's Swift code applies to Nightscout JSON: the
/// bridging casts on JSONSerialization output (`as? Double` accepts any number
/// or bool), the strict JSONDecoder types, its two date parsers, and its
/// number and time formatting.
/// </summary>
internal static partial class Swift
{
    /// HealthKit's mmol/L to mg/dL factor for blood glucose (molar mass 180.15588 g/mol).
    public const double MgdlPerMmol = 18.015588000005408;

    // MARK: JSONSerialization bridging (`x as? T`)

    public static JsonObject? Obj(JsonNode? n) => n as JsonObject;

    public static JsonObject? Obj(JsonObject? o, string key) => o?[key] as JsonObject;

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static string? Str(JsonObject? o, string key) => o is null ? null : Str(o[key]);

    /// `as? Double`: any number, and true/false as 1/0.
    public static double? Dbl(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    public static double? Dbl(JsonObject? o, string key) => o is null ? null : Dbl(o[key]);

    /// `as? Int`: an integral number, or a bool.
    public static long? Int(JsonNode? n)
    {
        if (Dbl(n) is not { } d) return null;
        return d == Math.Floor(d) && Math.Abs(d) < 9.2e18 ? (long)d : null;
    }

    /// `as? Bool`: true/false, or the numbers 0 and 1.
    public static bool? Bool(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetValue<double>() switch { 0 => false, 1 => true, _ => null },
            _ => null,
        };
    }

    public static bool? Bool(JsonObject? o, string key) => o is null ? null : Bool(o[key]);

    /// `as? [Double]`: an array whose every element bridges to Double.
    public static List<double>? Dbls(JsonNode? n)
    {
        if (n is not JsonArray a) return null;
        var list = new List<double>(a.Count);
        foreach (var x in a)
        {
            if (Dbl(x) is not { } d) return null;
            list.Add(d);
        }
        return list;
    }

    /// `dict[key] != nil`: present, a JSON null included (NSNull).
    public static bool Has(JsonObject? o, string key) => o is not null && o.ContainsKey(key);

    // MARK: JSONDecoder (strict)

    public sealed class DecodeException(string message) : Exception(message);

    public static JsonObject DecObj(JsonNode? n, string what) =>
        n as JsonObject ?? throw new DecodeException(what);

    public static double DecDouble(JsonNode? n, string what) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : throw new DecodeException(what);

    public static string DecString(JsonNode? n, string what) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw new DecodeException(what);

    public static long DecInt(JsonNode? n, string what)
    {
        var d = DecDouble(n, what);
        return d == Math.Floor(d) && Math.Abs(d) < 9.2e18 ? (long)d : throw new DecodeException(what);
    }

    /// decodeIfPresent: absent or null is nil, any other mismatch throws.
    public static T? Opt<T>(JsonObject o, string key, Func<JsonNode?, string, T> decode) where T : class =>
        o[key] is null ? null : decode(o[key], key);

    public static T? OptV<T>(JsonObject o, string key, Func<JsonNode?, string, T> decode) where T : struct =>
        o[key] is null ? null : decode(o[key], key);

    public static bool DecBool(JsonNode? n, string what) => n is JsonValue v && v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new DecodeException(what),
    };

    // MARK: Dates

    public static double Unix(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    public static DateTime FromUnix(double seconds) => DateTime.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// ISO8601DateFormatter with [.withFullDate, .withTime, .withDashSeparatorInDate,
    /// .withColonSeparatorInTime], as DeviceStatus.swift and S/C/IAge.swift use it:
    /// reads the leading date and time as UTC and ignores whatever follows, a zone
    /// offset and fractional seconds included.
    public static double? IsoPrefix(string? s)
    {
        if (s is null) return null;
        var m = IsoPrefixPattern().Match(s);
        return m.Success ? Compose(m) : null;
    }

    /// NightscoutUtils.parseDate: drops a trailing Z or ±hh:mm and every fractional
    /// part, then requires exactly yyyy-MM-dd'T'HH:mm:ss, read as UTC.
    public static double? ParseDate(string? s)
    {
        if (s is null) return null;
        if (s.EndsWith('Z')) s = s[..^1];
        else s = TrailingOffsetPattern().Replace(s, "");
        s = FractionPattern().Replace(s, "");
        var m = IsoExactPattern().Match(s);
        return m.Success ? Compose(m) : null;
    }

    /// NightscoutUtils.parseDateValue: a string as parseDate, a number as epoch
    /// seconds or milliseconds; a bool is never a date.
    public static double? ParseDateValue(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                return ParseDate(v.GetValue<string>());
            case JsonValueKind.Number:
                var raw = v.GetValue<double>();
                return raw > 1e11 ? raw / 1000 : raw;
            default:
                return null;
        }
    }

    /// NightscoutUtils.treatmentDate: `timestamp` when present and non-null, else `created_at`.
    public static double? TreatmentDate(JsonObject e)
    {
        foreach (var key in (ReadOnlySpan<string>)["timestamp", "created_at"])
        {
            if (e[key] is null) continue;
            return ParseDateValue(e[key]);
        }
        return null;
    }

    private static double? Compose(Match m)
    {
        int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        var (y, mo, d, h, mi, s) = (G(1), G(2), G(3), G(4), G(5), G(6));
        if (y < 1 || mo is < 1 or > 12 || h > 23 || mi > 59 || s > 59) return null;
        if (d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        return Unix(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc));
    }

    [GeneratedRegex(@"^(\d{1,4})-(\d{1,2})-(\d{1,2})T(\d{1,2}):(\d{1,2}):(\d{1,2})")]
    private static partial Regex IsoPrefixPattern();

    [GeneratedRegex(@"^(\d{1,4})-(\d{1,2})-(\d{1,2})T(\d{1,2}):(\d{1,2}):(\d{1,2})$")]
    private static partial Regex IsoExactPattern();

    [GeneratedRegex(@"[\+\-]\d{2}:\d{2}$")]
    private static partial Regex TrailingOffsetPattern();

    [GeneratedRegex(@"\.\d+")]
    private static partial Regex FractionPattern();

    /// The local calendar day of `utc` in `zone`, as UTC instants [start, end).
    public static (DateTime Start, DateTime End) LocalDay(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date;
        return (LocalToUtc(local, zone), LocalToUtc(local.AddDays(1), zone));
    }

    /// Wall-clock time in `zone` to UTC; a skipped hour resolves forward, a repeated one to its first pass.
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// Calendar.current.date(byAdding: .day, value: days): same wall-clock time `days` later.
    public static DateTime AddLocalDays(DateTime utc, int days, TimeZoneInfo zone) =>
        LocalToUtc(TimeZoneInfo.ConvertTimeFromUtc(utc, zone).AddDays(days), zone);

    /// dateTimeUtils.getDateTimeString: yyyy-MM-dd'T'HH:mm:ss in UTC.
    public static string NsQueryTime(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    // MARK: Formatting

    /// NumberFormatter(.decimal) as Localizer.formatToLocalizedString uses it: the
    /// shortest decimal form of the double rounded half-even, no grouping.
    public static string FormatDecimal(double value, int maxFractionDigits, int minFractionDigits, string decimalSeparator)
    {
        if (!decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var dec))
            return value.ToString(CultureInfo.InvariantCulture);
        dec = Math.Round(dec, maxFractionDigits, MidpointRounding.ToEven);
        var text = Math.Abs(dec).ToString("F" + maxFractionDigits, CultureInfo.InvariantCulture);
        if (maxFractionDigits > 0)
        {
            var dot = text.IndexOf('.');
            var keep = text.Length;
            while (keep > dot + 1 + minFractionDigits && text[keep - 1] == '0') keep--;
            if (keep == dot + 1) keep = dot;
            text = text[..keep].Replace(".", decimalSeparator);
        }
        return (value < 0 || (value == 0 && double.IsNegative(value)) ? "-" : "") + text;
    }

    /// Renders a Unicode date pattern's time fields (H, HH, h, hh, m, mm, a; quoted literals).
    public static string FormatTime(DateTime local, string pattern)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                var close = pattern.IndexOf('\'', i + 1);
                if (close < 0) close = pattern.Length;
                sb.Append(close == i + 1 ? "'" : pattern[(i + 1)..close]);
                i = close + 1;
                continue;
            }
            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            var h12 = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
            sb.Append(c switch
            {
                'H' => local.Hour.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                'h' => h12.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                'm' => local.Minute.ToString(run >= 2 ? "00" : "0", CultureInfo.InvariantCulture),
                'a' => local.Hour < 12 ? "AM" : "PM",
                _ => new string(c, run),
            });
            i += run;
        }
        return sb.ToString();
    }

    /// Swift's `rounded()` and `round()`: half away from zero.
    public static int RoundToInt(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
