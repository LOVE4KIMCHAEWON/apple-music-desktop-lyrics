using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AppleMusicLyrics.Core;

public sealed record TrackMetadata(string Title, string Artist, string Album, double DurationSeconds)
{
    public string Key
    {
        get
        {
            var prepared = MetadataNormalizer.Prepare(this);
            var identity = string.Join('\n', MetadataNormalizer.Normalize(prepared.Title),
                MetadataNormalizer.Normalize(prepared.Artist), MetadataNormalizer.Normalize(prepared.Album),
                (double.IsFinite(prepared.DurationSeconds) && prepared.DurationSeconds > 0
                    ? Math.Round(prepared.DurationSeconds).ToString(CultureInfo.InvariantCulture) : "0"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }
    }
}

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricsResult(IReadOnlyList<LyricLine> Lines, string? PlainLyrics, bool Instrumental, string Source)
{
    public bool HasSynchronizedLyrics => Lines.Any(line => !string.IsNullOrWhiteSpace(line.Text));
}

public interface ILyricsProvider
{
    Task<LyricsResult?> GetLyricsAsync(TrackMetadata track, CancellationToken cancellationToken = default);
}

public enum LyricsProviderError { Configuration, Timeout, RateLimited, Network, InvalidResponse }

public sealed class LyricsProviderException : Exception
{
    public LyricsProviderException(LyricsProviderError error, string message, Exception? innerException = null,
        DateTimeOffset? retryAfter = null, HttpStatusCode? statusCode = null) : base(message, innerException)
    {
        Error = error;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
    }

    public LyricsProviderError Error { get; }
    public DateTimeOffset? RetryAfter { get; }
    public HttpStatusCode? StatusCode { get; }
}
