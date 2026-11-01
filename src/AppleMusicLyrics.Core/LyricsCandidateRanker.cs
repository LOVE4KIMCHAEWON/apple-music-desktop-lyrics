using System.Text.Json.Serialization;

namespace AppleMusicLyrics.Core;

public sealed record LyricsCandidate
{
    public long Id { get; init; }
    public string TrackName { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public double Duration { get; init; }
    public bool Instrumental { get; init; }
    public string? PlainLyrics { get; init; }
    public string? SyncedLyrics { get; init; }

    [JsonIgnore]
    public bool HasLyrics => Instrumental || !string.IsNullOrWhiteSpace(PlainLyrics) ||
        LrcParser.Parse(SyncedLyrics).Any(line => !string.IsNullOrWhiteSpace(line.Text));
}

public static class LyricsCandidateRanker
{
    public static double Score(TrackMetadata requested, LyricsCandidate candidate)
    {
        var track = MetadataNormalizer.Prepare(requested);
        if (!candidate.HasLyrics || string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return double.NegativeInfinity;
        if (MetadataNormalizer.Normalize(track.Title) != MetadataNormalizer.Normalize(candidate.TrackName) ||
            MetadataNormalizer.Normalize(track.Artist) != MetadataNormalizer.Normalize(candidate.ArtistName) ||
            MetadataNormalizer.VersionSignature(track.Title, track.Album) !=
            MetadataNormalizer.VersionSignature(candidate.TrackName, candidate.AlbumName)) return double.NegativeInfinity;

        double score = 100;
        if (double.IsFinite(track.DurationSeconds) && track.DurationSeconds > 0 &&
            double.IsFinite(candidate.Duration) && candidate.Duration > 0)
        {
            var difference = Math.Abs(track.DurationSeconds - candidate.Duration);
            if (difference > 2.5) return double.NegativeInfinity;
            score += 10 - difference;
        }
        else if (track.DurationSeconds > 0) score -= 20;
        if (MetadataNormalizer.Normalize(track.Album) == MetadataNormalizer.Normalize(candidate.AlbumName)) score += 8;
        if (LrcParser.Parse(candidate.SyncedLyrics).Any(line => !string.IsNullOrWhiteSpace(line.Text))) score += 20;
        return score;
    }

    public static LyricsCandidate? Select(TrackMetadata requested, IEnumerable<LyricsCandidate> candidates) => candidates
        .Select(candidate => (Candidate: candidate, Score: Score(requested, candidate)))
        .Where(item => !double.IsNegativeInfinity(item.Score)).OrderByDescending(item => item.Score)
        .ThenBy(item => item.Candidate.Id).Select(item => item.Candidate).FirstOrDefault();

    public static LyricsResult ToResult(LyricsCandidate candidate) => new(LrcParser.Parse(candidate.SyncedLyrics),
        candidate.PlainLyrics, candidate.Instrumental, $"LRCLIB #{candidate.Id}");
}
