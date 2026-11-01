using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("AppleMusicLyrics.Core.Tests")]

namespace AppleMusicLyrics.Core;

/// <summary>
/// Resolves explicit catalog IDs using Apple's documented, unauthenticated public lookup endpoint.
/// Only identity metadata is read or cached; lyrics, previews, artwork and authentication are never requested.
/// </summary>
public sealed class ApplePublicCatalogResolver
{
    private const int MaximumIds = 128;
    private const int BatchSize = 20;
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumCacheEntries = 256;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly AppleCatalogRequestCoordinator SharedRequests = new(TimeProvider.System);
    private static readonly SemaphoreSlim CacheWriteGate = new(1, 1);
    private readonly HttpClient _client;
    private readonly string? _cachePath;
    private readonly string _country;
    private readonly AppleCatalogRequestCoordinator _requests;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _lookupGate = new(1, 1);

    public ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory = null, string country = "AU")
        : this(client, cacheDirectory, country, SharedRequests, TimeSpan.FromSeconds(8)) { }

    internal ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory, string country,
        AppleCatalogRequestCoordinator requests, TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(country);
        if (country.Length != 2 || country.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')))
            throw new ArgumentException("A two-letter ASCII storefront country is required.", nameof(country));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(8))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _client = client;
        _country = country.ToUpperInvariant();
        _cachePath = string.IsNullOrWhiteSpace(cacheDirectory) ? null :
            Path.Combine(Path.GetFullPath(cacheDirectory), $"apple-public-catalog-{_country}.json");
        _requests = requests;
        _requestTimeout = requestTimeout;
    }

    /// <summary>
    /// Invalid IDs are ignored. More than 128 distinct valid IDs is a caller error.
    /// Network, HTTP, timeout, malformed-response and cache failures return only the validated partial results.
    /// Caller cancellation always propagates. No search or fuzzy metadata selection is performed here.
    /// </summary>
    public async Task<IReadOnlyList<NativeCatalogTrack>> ResolveAsync(IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > 4096) throw new ArgumentException("The catalog ID input exceeds the bounded limit.", nameof(ids));
            if (!IsCatalogId(id)) continue;
            requested.Add(id);
            if (requested.Count > MaximumIds)
                throw new ArgumentException("At most 128 distinct catalog IDs may be resolved at once.", nameof(ids));
        }
        if (requested.Count == 0) return [];

        await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            var resolved = cache.Where(pair => requested.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Track, StringComparer.Ordinal);
            var added = new List<CachedTrack>();
            foreach (var batch in requested.Where(id => !resolved.ContainsKey(id)).Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var records = await FetchAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var record in records)
                {
                    resolved[record.CatalogId] = record;
                    added.Add(new(record, _requests.Clock.GetUtcNow()));
                }
            }
            if (added.Count > 0) await SaveCacheAsync(added, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return requested.Where(resolved.ContainsKey).Select(id => resolved[id]).ToArray();
        }
        finally { _lookupGate.Release(); }
    }

    private async Task<IReadOnlyList<NativeCatalogTrack>> FetchAsync(string[] ids, CancellationToken cancellationToken)
    {
        if (!await _requests.EnterAsync(cancellationToken).ConfigureAwait(false)) return [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://itunes.apple.com/lookup?id=" + Uri.EscapeDataString(string.Join(',', ids)) +
                "&country=" + Uri.EscapeDataString(_country) + "&lang=en_us");
            // The caller must supply a dedicated unauthenticated HttpClient. No credentials are copied from Apple Music.
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _requests.SetRetryAfter(response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
                return [];
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, timeout.Token).ConfigureAwait(false);
            return ParseResponse(bytes, ids);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
        catch (HttpRequestException) { return []; }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
        finally { _requests.Exit(); }
    }

    private static IReadOnlyList<NativeCatalogTrack> ParseResponse(byte[] bytes, IReadOnlyCollection<string> ids)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (!IsUniqueObject(root) || !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 256) return [];
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        var records = new Dictionary<string, NativeCatalogTrack>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!IsUniqueObject(item))
            {
                foreach (var property in item.EnumerateObject().Where(property => property.Name == "trackId"))
                    if (TryNumericCatalogId(property.Value, out var duplicateId) && requested.Contains(duplicateId))
                        rejected.Add(duplicateId);
                continue;
            }
            if (!item.TryGetProperty("trackId", out var idValue) || !TryNumericCatalogId(idValue, out var id) ||
                !requested.Contains(id)) continue;
            if (!IsString(item, "wrapperType", "track") || !IsString(item, "kind", "song") ||
                !TryText(item, "trackName", out var title) || !TryText(item, "artistName", out var artist) ||
                !TryText(item, "collectionName", out var album) ||
                !item.TryGetProperty("trackTimeMillis", out var duration) || duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds) || milliseconds is < 1000 or > 86400000)
            { rejected.Add(id); continue; }
            var track = new NativeCatalogTrack(id, new(title, artist, album, milliseconds / 1000));
            if (records.TryGetValue(id, out var previous) && previous != track) rejected.Add(id);
            else records[id] = track;
        }
        return records.Where(pair => !rejected.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
    }

    private async Task<Dictionary<string, CachedTrack>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, CachedTrack>(StringComparer.Ordinal);
        if (_cachePath is null) return cache;
        try
        {
            if (!IsOrdinaryFile(_cachePath)) return cache;
            await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!IsUniqueObject(root) || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
                !IsString(root, "source", "https://itunes.apple.com/lookup") || !IsString(root, "country", _country) ||
                !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() > MaximumCacheEntries) return cache;
            var now = _requests.Clock.GetUtcNow();
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (!IsUniqueObject(entry) || !TryText(entry, "catalogId", out var id) || !IsCatalogId(id) ||
                    !TryText(entry, "title", out var title) || !TryText(entry, "artist", out var artist) ||
                    !TryText(entry, "album", out var album) || !entry.TryGetProperty("durationSeconds", out var duration) ||
                    duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var seconds) ||
                    !double.IsFinite(seconds) || seconds is < 1 or > 86400 ||
                    !TryText(entry, "fetchedAt", out var fetchedText) || !DateTimeOffset.TryParse(fetchedText,
                        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetched) ||
                    fetched > now.AddMinutes(5) || now - fetched > CacheLifetime) continue;
                var record = new CachedTrack(new(id, new(title, artist, album, seconds)), fetched);
                if (cache.TryGetValue(id, out var previous) && previous.Track != record.Track) rejected.Add(id);
                else cache[id] = record;
            }
            foreach (var id in rejected) cache.Remove(id);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return cache;
    }

    private async Task SaveCacheAsync(IReadOnlyCollection<CachedTrack> added, CancellationToken cancellationToken)
    {
        if (_cachePath is null) return;
        await CacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.Exists(_cachePath) && !IsOrdinaryFile(_cachePath))) return;
            var cache = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in added) cache[item.Track.CatalogId] = item;
            var entries = cache.Values.OrderByDescending(item => item.FetchedAt).Take(MaximumCacheEntries).ToArray();
            byte[] bytes;
            do
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1, source = "https://itunes.apple.com/lookup", country = _country,
                    entries = entries.Select(item => new
                    {
                        catalogId = item.Track.CatalogId, title = item.Track.Metadata.Title,
                        artist = item.Track.Metadata.Artist, album = item.Track.Metadata.Album,
                        durationSeconds = item.Track.Metadata.DurationSeconds, fetchedAt = item.FetchedAt
                    })
                });
                if (bytes.Length > MaximumBytes) entries = entries.Take(entries.Length / 2).ToArray();
            } while (bytes.Length > MaximumBytes);
            temporary = Path.Combine(directory, ".apple-public-catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _cachePath, true);
            temporary = null;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            CacheWriteGate.Release();
        }
    }

    private static bool IsCatalogId(string? id) => id is { Length: > 0 and <= 19 } && id[0] != '0' &&
        id.All(character => character is >= '0' and <= '9') &&
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;

    private static bool TryNumericCatalogId(JsonElement element, out string id)
    {
        id = element.ValueKind == JsonValueKind.Number ? element.GetRawText() : "";
        return IsCatalogId(id);
    }

    private static bool TryText(JsonElement element, string name, out string text)
    {
        text = "";
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString() ?? "";
        return text.Length is > 0 and <= 2048 && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    }

    private static bool IsString(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool IsUniqueObject(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() ==
        element.EnumerateObject().Count();

    private static bool IsOrdinaryFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length <= MaximumBytes &&
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > MaximumBytes) throw new InvalidDataException("The public metadata payload exceeds the bounded limit.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private sealed record CachedTrack(NativeCatalogTrack Track, DateTimeOffset FetchedAt);
}

/// <summary>Process-wide request serialization, monotonic spacing and server Retry-After cooldown.</summary>
internal sealed class AppleCatalogRequestCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _hasRequest;
    private long _lastRequest;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    internal AppleCatalogRequestCoordinator(TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Clock = clock;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, clock, token));
    }

    internal TimeProvider Clock { get; }

    internal async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cooldown = _retryAfter - Clock.GetUtcNow();
            // Long server cooldowns must not stall track changes. Subsequent calls remain suppressed until it expires.
            if (cooldown > TimeSpan.FromSeconds(8)) { _gate.Release(); return false; }
            var interval = _hasRequest ? TimeSpan.FromSeconds(3) - Clock.GetElapsedTime(_lastRequest) : TimeSpan.Zero;
            var delay = interval > cooldown ? interval : cooldown;
            if (delay > TimeSpan.Zero) await _delay(delay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lastRequest = Clock.GetTimestamp();
            _hasRequest = true;
            return true;
        }
        catch { _gate.Release(); throw; }
    }

    internal void SetRetryAfter(TimeSpan? delta, DateTimeOffset? date)
    {
        var now = Clock.GetUtcNow();
        var next = now;
        if (delta is { } duration && duration > TimeSpan.Zero)
        {
            var remaining = DateTimeOffset.MaxValue - now;
            next = duration >= remaining ? DateTimeOffset.MaxValue : now + duration;
        }
        else if (date is { } value) next = value;
        if (next > _retryAfter) _retryAfter = next;
    }

    internal void Exit() => _gate.Release();
}
