namespace AppleMusicLyrics.Core.Playback;

public sealed record GlobalOffsetPromotion(
    int GlobalMilliseconds, IReadOnlyDictionary<string, int> TrackOffsets);

/// <summary>
/// Combines the persistent all-song correction with an optional correction for one track.
/// Positive values advance lyric selection: lookup position = playback position + offset.
/// Reading an offset never changes either setting when the current song changes.
/// </summary>
public static class LyricsTimingOffsets
{
    public const int MaximumComponentMilliseconds = 60_000;

    public static int Sanitize(int milliseconds) =>
        Math.Clamp(milliseconds, -MaximumComponentMilliseconds, MaximumComponentMilliseconds);

    /// <summary>
    /// Each persisted component is limited separately; their effective sum can reach
    /// +/-120 seconds. Unknown tracks use the global correction without creating an entry.
    /// </summary>
    public static int GetEffectiveMilliseconds(int globalMilliseconds,
        IReadOnlyDictionary<string, int>? perTrackMilliseconds, string? trackKey)
    {
        var trackMilliseconds = !string.IsNullOrWhiteSpace(trackKey)
            && perTrackMilliseconds is not null
            && perTrackMilliseconds.TryGetValue(trackKey, out var value) ? Sanitize(value) : 0;
        return Sanitize(globalMilliseconds) + trackMilliseconds;
    }

    /// <summary>
    /// Uses the current song's total correction as the new all-song correction and
    /// rebases saved per-song corrections to avoid applying the same adjustment twice.
    /// Existing totals are preserved where representable by the +/-60 second component
    /// limits; otherwise the new global or per-song component is saturated at its limit.
    /// Zero corrections are omitted, and the caller's dictionary is never modified.
    /// </summary>
    public static GlobalOffsetPromotion PromoteCurrentTrack(int globalMilliseconds,
        IReadOnlyDictionary<string, int>? perTrackMilliseconds, string trackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackKey);
        var newGlobal = Sanitize(GetEffectiveMilliseconds(globalMilliseconds, perTrackMilliseconds, trackKey));
        var rebased = new Dictionary<string, int>(StringComparer.Ordinal);
        if (perTrackMilliseconds is not null)
        {
            foreach (var (key, _) in perTrackMilliseconds)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var correction = Sanitize(GetEffectiveMilliseconds(globalMilliseconds, perTrackMilliseconds, key) - newGlobal);
                if (correction != 0) rebased[key] = correction;
            }
        }
        return new(newGlobal, new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(rebased));
    }
}
