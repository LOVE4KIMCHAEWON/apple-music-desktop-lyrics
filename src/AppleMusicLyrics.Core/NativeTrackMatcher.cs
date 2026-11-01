namespace AppleMusicLyrics.Core;

/// <summary>Public song metadata read from Apple's catalog cache, bound to its explicit catalog ID.</summary>
public sealed record NativeCatalogTrack(string CatalogId, TrackMetadata Metadata);

public enum NativeTrackMatchStatus { Matched, InvalidTrack, NoMatch, Ambiguous }

public sealed record NativeTrackMatch(NativeTrackMatchStatus Status, string? CatalogId);

/// <summary>
/// Fail closed: all four metadata fields must agree and exactly one catalog identity may match.
/// Cache age, file order, lyric duration, and the mere presence of a lyric file are not identity evidence.
/// </summary>
public static class NativeTrackMatcher
{
    // Apple SMTC reports whole-second/trimmed durations: live Hurt was 176s,
    // while the same catalog recording reports 177.947s. Identity still requires
    // exact normalized title, artist and album, plus a unique catalog ID.
    public const double DurationToleranceSeconds = 3;

    public static NativeTrackMatch Match(TrackMetadata requested, IEnumerable<NativeCatalogTrack> candidates)
    {
        var track = MetadataNormalizer.Prepare(requested);
        if (!HasCompleteMetadata(track)) return new(NativeTrackMatchStatus.InvalidTrack, null);

        var identities = candidates.Where(candidate => IsCatalogId(candidate.CatalogId))
            .GroupBy(candidate => candidate.CatalogId, StringComparer.Ordinal).ToArray();
        var matching = new List<string>();
        foreach (var identity in identities)
        {
            var records = identity.Select(candidate => MetadataNormalizer.Prepare(candidate.Metadata)).ToArray();
            if (!records.Any(candidate => IsStrongMatch(track, candidate))) continue;
            // A matching record must not hide contradictory complete metadata for the same ID.
            if (records.Any(candidate => !HasCompleteMetadata(candidate) || !IsStrongMatch(track, candidate)))
                return new(NativeTrackMatchStatus.Ambiguous, null);
            matching.Add(identity.Key);
        }
        return matching.Count switch
        {
            0 => new(NativeTrackMatchStatus.NoMatch, null),
            1 => new(NativeTrackMatchStatus.Matched, matching[0]),
            _ => new(NativeTrackMatchStatus.Ambiguous, null)
        };
    }

    public static bool IsCatalogId(string? value) => value is { Length: > 0 and <= 20 } &&
        value.All(character => character is >= '0' and <= '9');

    private static bool HasCompleteMetadata(TrackMetadata track) =>
        MetadataNormalizer.Normalize(track.Title).Length > 0 &&
        MetadataNormalizer.Normalize(track.Artist).Length > 0 &&
        MetadataNormalizer.Normalize(track.Album).Length > 0 &&
        double.IsFinite(track.DurationSeconds) && track.DurationSeconds is >= 1 and <= 86400;

    private static bool IsStrongMatch(TrackMetadata requested, TrackMetadata candidate) =>
        HasCompleteMetadata(candidate) &&
        MetadataNormalizer.Normalize(requested.Title) == MetadataNormalizer.Normalize(candidate.Title) &&
        MetadataNormalizer.Normalize(requested.Artist) == MetadataNormalizer.Normalize(candidate.Artist) &&
        MetadataNormalizer.Normalize(requested.Album) == MetadataNormalizer.Normalize(candidate.Album) &&
        MetadataNormalizer.VersionSignature(requested.Title, requested.Album) ==
        MetadataNormalizer.VersionSignature(candidate.Title, candidate.Album) &&
        Math.Abs(requested.DurationSeconds - candidate.DurationSeconds) <= DurationToleranceSeconds;
}
