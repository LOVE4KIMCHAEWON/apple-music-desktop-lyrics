using System.Globalization;
using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core;

public static partial class LrcParser
{
    /// <summary>Parse line timing only. Positive LRC offset advances the lyrics; plain text is never timed.</summary>
    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();
        var lines = new List<LyricLine>();
        double offsetMilliseconds = 0;
        foreach (var raw in lrc.TrimStart('\uFEFF').Split('\n'))
        {
            var text = raw.Trim();
            var offset = Offset().Match(text);
            if (offset.Success)
            {
                if (double.TryParse(offset.Groups[1].Value, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var parsed) && Math.Abs(parsed) <= 86_400_000)
                    offsetMilliseconds = parsed;
                continue;
            }
            var timings = new List<TimeSpan>();
            while (true)
            {
                var match = Timestamp().Match(text);
                if (!match.Success) break;
                if (int.TryParse(match.Groups[1].Value, out var minutes) &&
                    int.TryParse(match.Groups[2].Value, out var seconds) && seconds < 60)
                {
                    var fraction = match.Groups[3].Success
                        ? double.Parse("0." + match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                    timings.Add(TimeSpan.FromSeconds(minutes * 60d + seconds + fraction));
                }
                text = text[match.Length..].TrimStart();
            }
            if (timings.Count == 0) continue;
            text = WordTimestamp().Replace(text, "").TrimEnd();
            foreach (var time in timings) lines.Add(new LyricLine(time, text));
        }
        return lines.Select(line => line with
            { Time = TimeSpan.FromMilliseconds(Math.Max(0, line.Time.TotalMilliseconds - offsetMilliseconds)) })
            .OrderBy(line => line.Time).DistinctBy(line => (line.Time, line.Text)).ToArray();
    }

    [GeneratedRegex(@"^\[(\d{1,4}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
    [GeneratedRegex(@"<\d{1,4}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTimestamp();
}
