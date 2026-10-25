using System.Text;

namespace LFHub.Engine.State;

/// <summary>LoopFollow's CGMSensorState: a "CGM: &lt;state&gt;" note from Loop or Trio.</summary>
internal static class CgmSensorState
{
    private const string Prefix = "CGM: ";

    /// The kit's state name, or null when the note is not a sensor-state note.
    public static string? Parse(string note)
    {
        if (!note.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var name = note[Prefix.Length..];
        return name.Length == 0 || name.Any(char.IsWhiteSpace) ? null : name;
    }

    /// The newest state, unless a reading at or after it shows the CGM delivering again.
    public static (string Name, double Date)? Active(IReadOnlyList<(string Name, double Date)> states, double? latestBgDate)
    {
        if (states.Count == 0) return null;
        var newest = states[0];
        foreach (var s in states)
            if (s.Date > newest.Date) newest = s;
        return latestBgDate is { } bg && newest.Date <= bg ? null : newest;
    }

    /// "Temporary sensor issue at 22:33".
    public static string Summary(string name, double date, ClientSettings s)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(Swift.FromUnix(date), s.TimeZone);
        return $"{DisplayName(name)} at {Swift.FormatTime(local, s.TimeFormat)}";
    }

    public static string DisplayName(string name)
    {
        if (name.StartsWith(".unknown(", StringComparison.Ordinal) && name.EndsWith(')'))
            return $"Unknown state ({name[".unknown(".Length..^1]})";
        if (name == "questionMarks") return "Question marks (???)";

        var identifier = name.Replace("Dueto", "DueTo");
        var words = new List<string>();
        var current = new StringBuilder();
        char? previous = null;
        foreach (var c in identifier)
        {
            if (previous is { } p)
            {
                var startsWord = (char.IsUpper(c) && (char.IsLower(p) || char.IsDigit(p)))
                    || (char.IsDigit(c) && char.IsLetter(p));
                if (startsWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            current.Append(c);
            previous = c;
        }
        words.Add(current.ToString());

        var normalized = words.Select(w => w.Skip(1).Any(char.IsUpper) ? w : w.ToLowerInvariant()).ToList();
        var first = normalized[0];
        if (first.Length == 0) return name;
        return string.Join(" ", [char.ToUpperInvariant(first[0]) + first[1..], .. normalized.Skip(1)]);
    }
}
