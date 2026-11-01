using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json;

namespace AppleMusicLyrics.Core;

public sealed record LrclibOptions
{
    /// <summary>LRCLIB requires the real application homepage/project URL or a contact email in User-Agent.</summary>
    public string? ClientContact { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumRetries { get; init; } = 2;
}

public sealed class LrclibLyricsProvider : ILyricsProvider
{
    private readonly HttpClient _client;
    private readonly LocalLyricsCache? _cache;
    private readonly LrclibOptions _options;
    // Process-wide sequential requests and cooldown also cover track changes and cancellation of an old request.
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _nextRequest = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public LrclibLyricsProvider(HttpClient client, LocalLyricsCache? cache = null, LrclibOptions? options = null)
    {
        _client = client;
        _cache = cache;
        _options = options ?? new LrclibOptions();
    }

    public async Task<LyricsResult?> GetLyricsAsync(TrackMetadata track, CancellationToken cancellationToken = default)
    {
        track = MetadataNormalizer.Prepare(track);
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist)) return null;
        if (_cache is not null)
        {
            var cached = await _cache.GetAsync(track, cancellationToken).ConfigureAwait(false);
            if (cached is not null) return cached;
        }
        var userAgent = CreateUserAgent();
        var query = new Dictionary<string, string>
        {
            ["track_name"] = track.Title, ["artist_name"] = track.Artist, ["album_name"] = track.Album
        };
        if (double.IsFinite(track.DurationSeconds) && track.DurationSeconds is >= 1 and <= 3600)
            query["duration"] = track.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        LyricsCandidate? direct;
        try
        {
            direct = await FetchAsync<LyricsCandidate>("get", query, userAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (LyricsProviderException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // The lookup endpoint can be busy while the indexed search endpoint is available.
            // Its full Retry-After cooldown is retained by RequestGate before the fallback request.
            direct = null;
        }
        var selected = direct is null ? null : LyricsCandidateRanker.Select(track, [direct]);
        if (selected is null || (!selected.Instrumental && !LrcParser.Parse(selected.SyncedLyrics).Any(line => !string.IsNullOrWhiteSpace(line.Text))))
        {
            try
            {
                var candidates = await FetchAsync<LyricsCandidate[]>("search", new Dictionary<string, string>
                {
                    ["track_name"] = track.Title, ["artist_name"] = track.Artist
                }, userAgent, cancellationToken).ConfigureAwait(false);
                selected = LyricsCandidateRanker.Select(track, (candidates ?? []).Concat(selected is null ? [] : new[] { selected }));
            }
            catch (LyricsProviderException) when (selected is not null)
            {
                // A valid untimed sheet remains useful if the optional synchronized fallback fails.
            }
        }
        if (selected is null) return null;
        var result = LyricsCandidateRanker.ToResult(selected);
        if (_cache is not null)
        {
            try { await _cache.PutAsync(track, result, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private string CreateUserAgent()
    {
        var contact = _options.ClientContact?.Trim();
        var validUrl = Uri.TryCreate(contact, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
        var emailText = contact?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true ? contact[7..] : contact;
        var validEmail = MailAddress.TryCreate(emailText, out var email) && email.Address == emailText;
        if (string.IsNullOrWhiteSpace(contact) || contact.Contains('\r') || contact.Contains('\n') ||
            (!validUrl && !validEmail))
            throw new LyricsProviderException(LyricsProviderError.Configuration,
                "LRCLIB requires a real project homepage URL or contact email in the client identifier. Add it in Settings, or import a local .lrc file.");
        return $"AppleMusicDesktopLyrics/0.2.4 ({contact})";
    }

    private async Task<T?> FetchAsync<T>(string endpoint, IReadOnlyDictionary<string, string> parameters, string userAgent,
        CancellationToken cancellationToken) where T : class
    {
        var uri = new Uri("https://lrclib.net/api/" + endpoint + "?" + string.Join('&', parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        var retries = Math.Clamp(_options.MaximumRetries, 0, 2);
        for (var attempt = 0; ; attempt++)
        {
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var remaining = _nextRequest - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.FromSeconds(30))
                    throw new LyricsProviderException(LyricsProviderError.RateLimited,
                        "LRCLIB requests are cooling down. Try again after the server's Retry-After time.", retryAfter: _nextRequest);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeoutValue = _options.RequestTimeout > TimeSpan.Zero && _options.RequestTimeout <= TimeSpan.FromSeconds(30)
                    ? _options.RequestTimeout : TimeSpan.FromSeconds(10);
                timeout.CancelAfter(timeoutValue);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.Accept.ParseAdd("application/json");
                _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable || (int)response.StatusCode >= 500)
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    // Honor the full server delay; never cap Retry-After and accidentally send early.
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(50, 251));
                    var retryAt = DateTimeOffset.UtcNow + delay + jitter;
                    if (retryAt > _nextRequest) _nextRequest = retryAt;
                    if (attempt >= retries || delay > TimeSpan.FromSeconds(30))
                        throw new LyricsProviderException(response.StatusCode == HttpStatusCode.TooManyRequests
                            ? LyricsProviderError.RateLimited : LyricsProviderError.Network,
                            $"LRCLIB returned HTTP {(int)response.StatusCode}. Try again after {retryAt:HH:mm:ss} UTC.", retryAfter: retryAt,
                            statusCode: response.StatusCode);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new LyricsProviderException(LyricsProviderError.Network, $"LRCLIB returned HTTP {(int)response.StatusCode}.",
                        statusCode: response.StatusCode);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType is null || (!contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
                    !contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                    throw new LyricsProviderException(LyricsProviderError.InvalidResponse, "LRCLIB returned an unexpected content type.");
                if (response.Content.Headers.ContentLength > 4 * 1024 * 1024)
                    throw new LyricsProviderException(LyricsProviderError.InvalidResponse, "LRCLIB returned an unexpectedly large response.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var bounded = new ResponseSizeLimitedStream(stream, 4 * 1024 * 1024);
                return await JsonSerializer.DeserializeAsync<T>(bounded, JsonOptions, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= retries) throw new LyricsProviderException(LyricsProviderError.Timeout, "LRCLIB request timed out.", exception);
                _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(attempt + 2);
            }
            catch (HttpRequestException exception)
            {
                if (attempt >= retries) throw new LyricsProviderException(LyricsProviderError.Network, "LRCLIB could not be reached.", exception);
                _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(attempt + 2);
            }
            catch (IOException exception)
            {
                if (attempt >= retries) throw new LyricsProviderException(LyricsProviderError.Network, "The LRCLIB connection was interrupted.", exception);
                _nextRequest = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(attempt + 2);
            }
            catch (JsonException exception)
            {
                throw new LyricsProviderException(LyricsProviderError.InvalidResponse, "LRCLIB returned malformed lyrics data.", exception);
            }
            finally
            {
                // Leave a full quiet interval after completion, including slow responses and failed attempts.
                var quietUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                if (quietUntil > _nextRequest) _nextRequest = quietUntil;
                RequestGate.Release();
            }
        }
    }
}

/// <summary>Enforce the limit while reading, including chunked responses with no Content-Length header.</summary>
internal sealed class ResponseSizeLimitedStream(Stream inner, long maximumBytes) : Stream
{
    private long _read;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Check(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Check(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Check(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));
    private int Check(int count)
    {
        _read += count;
        if (_read > maximumBytes)
            throw new LyricsProviderException(LyricsProviderError.InvalidResponse, "LRCLIB returned an unexpectedly large response.");
        return count;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
