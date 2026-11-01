namespace AppleMusicLyrics.Core.Playback;

public sealed record LyricPair(LyricLine? Active, LyricLine? Upcoming, int ActiveIndex);

public static class LyricSelection
{
    /// <summary>
    /// Selects two lines from timestamp-sorted lyrics in O(log n). Positive offsets show
    /// lyrics earlier. Before the first timestamp only the upcoming line is populated.
    /// </summary>
    public static LyricPair Select(IReadOnlyList<LyricLine> lines, TimeSpan position,
        TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var adjustedSeconds = position.TotalSeconds + offset.TotalSeconds;
        var low = 0;
        var high = lines.Count - 1;
        var active = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].Time.TotalSeconds <= adjustedSeconds)
            {
                active = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return new(active >= 0 ? lines[active] : null,
            active + 1 < lines.Count ? lines[active + 1] : null, active);
    }
}
