using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("AppleMusicLyrics.Core.Tests")]

namespace AppleMusicLyrics.Core;

/// <summary>Bounded metadata lookup diagnostics; contains no lyrics, credentials or song metadata.</summary>
public sealed record AppleCatalogLookupDiagnostic(string Country, string Outcome, int? StatusCode,
    int RequestedCount, int ResolvedCount, int MissingCount, int RejectedCount);

/// <summary>
/// Resolves explicit catalog IDs using Apple's documented, unauthenticated public lookup endpoint.
/// Only identity metadata is read or cached; lyrics, previews, artwork and authentication are never requested.
/// </summary>
public sealed class ApplePublicCatalogResolver
{
    private const int MaximumIds = 128;
    private const int MaximumStorefronts = 4;
    private const int BatchSize = 20;
    private const int MaximumBytes = 1024 * 1024;
    private const int MaximumCacheEntries = 256;
    private const int MaximumCacheStorefronts = 32;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly AppleCatalogRequestCoordinator SharedRequests = new(TimeProvider.System);
    private static readonly SemaphoreSlim CacheWriteGate = new(1, 1);
    private readonly HttpClient _client;
    private readonly string? _cacheDirectory;
    private readonly string _country;
    private readonly string? _regionalCountry;
    private readonly IReadOnlyList<string>? _storefrontsForTests;
    private readonly bool _storefrontFallback;
    private readonly AppleCatalogRequestCoordinator _requests;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _lookupGate = new(1, 1);

    public ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory = null, string country = "AU",
        string? regionalCountry = null)
        : this(client, cacheDirectory, country, SharedRequests, TimeSpan.FromSeconds(8), regionalCountry) { }

    internal ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory, string country,
        AppleCatalogRequestCoordinator requests, TimeSpan requestTimeout)
        : this(client, cacheDirectory, country, requests, requestTimeout, null, null, false) { }

    internal ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory, string country,
        AppleCatalogRequestCoordinator requests, TimeSpan requestTimeout, string? regionalCountry,
        IReadOnlyList<string>? storefrontsForTests = null)
        : this(client, cacheDirectory, country, requests, requestTimeout, regionalCountry, storefrontsForTests, true) { }

    private ApplePublicCatalogResolver(HttpClient client, string? cacheDirectory, string country,
        AppleCatalogRequestCoordinator requests, TimeSpan requestTimeout, string? regionalCountry,
        IReadOnlyList<string>? storefrontsForTests, bool storefrontFallback)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(country);
        if (country.Length != 2 || country.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')))
            throw new ArgumentException("A two-letter ASCII storefront country is required.", nameof(country));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromSeconds(8))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _client = client;
        _country = country.ToUpperInvariant();
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? null : Path.GetFullPath(cacheDirectory);
        _regionalCountry = NormalizeCountry(regionalCountry);
        _storefrontsForTests = storefrontsForTests;
        _storefrontFallback = storefrontFallback;
        _requests = requests;
        _requestTimeout = requestTimeout;
    }

    public event Action<AppleCatalogLookupDiagnostic>? Diagnostic;

    /// <summary>
    /// Invalid IDs are ignored. More than 128 distinct valid IDs is a caller error.
    /// Network, HTTP, timeout, malformed-response and cache failures return only the validated partial results.
    /// Caller cancellation always propagates. No search or fuzzy metadata selection is performed here.
    /// At most four storefronts are queried over the network; only valid empty results permit regional fallback.
    /// Existing own metadata caches are checked separately across at most 32 countries to reject known conflicts.
    /// </summary>
    public async Task<IReadOnlyList<NativeCatalogTrack>> ResolveAsync(IEnumerable<string> ids,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? preferredStorefronts = null)
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
            var inventory = FindCacheCountries(cancellationToken);
            if (!inventory.Complete)
            {
                // The uninspected file could contradict an otherwise valid identity. Do not hide that uncertainty.
                Report(_country, inventory.Failure!, null, requested.Count, 0, 0, requested.Count);
                return [];
            }
            var known = new Dictionary<string, List<NativeCatalogTrack>>(StringComparer.Ordinal);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var country in inventory.Countries)
            {
                var cache = await LoadCacheAsync(country, cancellationToken).ConfigureAwait(false);
                rejected.UnionWith(cache.Rejected.Where(requested.Contains));
                var cached = cache.Records.Where(pair => requested.Contains(pair.Key)).Select(pair => pair.Value.Track).ToArray();
                foreach (var record in cached)
                {
                    if (!known.TryGetValue(record.CatalogId, out var records)) known[record.CatalogId] = records = [];
                    records.Add(record);
                }
                if (cached.Length > 0) Report(country, "cache-hit", null, requested.Count, cached.Length, 0, 0);
            }
            var resolved = new Dictionary<string, NativeCatalogTrack>(StringComparer.Ordinal);
            foreach (var (id, records) in known)
            {
                // A known record from another storefront must never be discarded to hide a disagreement.
                if (rejected.Contains(id) || NativeTrackMatcher.Match(records[0].Metadata, records).Status != NativeTrackMatchStatus.Matched)
                    rejected.Add(id);
                else resolved[id] = records[0];
            }
            if (rejected.Count > 0) Report(_country, "cache-conflict", null, requested.Count, resolved.Count, 0, rejected.Count);

            async Task FetchCountryAsync(string country)
            {
                var added = new List<CachedTrack>();
                foreach (var batch in requested.Where(id => !resolved.ContainsKey(id) && !rejected.Contains(id)).Chunk(BatchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fetched = await FetchAsync(country, batch, cancellationToken).ConfigureAwait(false);
                    rejected.UnionWith(fetched.Rejected);
                    // Only IDs absent from a well-formed successful response remain eligible for another storefront.
                    foreach (var record in fetched.Records)
                    {
                        resolved[record.CatalogId] = record;
                        added.Add(new(record, _requests.Clock.GetUtcNow()));
                    }
                }
                if (added.Count > 0) await SaveCacheAsync(country, added, cancellationToken).ConfigureAwait(false);
            }

            // Resolve the primary storefront before choosing alternatives: already-resolved candidates' language
            // hints must not consume the small regional budget needed by IDs genuinely absent from the primary.
            await FetchCountryAsync(_country).ConfigureAwait(false);
            var stillMissing = requested.Where(id => !resolved.ContainsKey(id) && !rejected.Contains(id)).ToArray();
            foreach (var country in StorefrontPlan(stillMissing, preferredStorefronts).Skip(1))
                await FetchCountryAsync(country).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return requested.Where(resolved.ContainsKey).Select(id => resolved[id]).ToArray();
        }
        finally { _lookupGate.Release(); }
    }

    private CacheInventory FindCacheCountries(CancellationToken cancellationToken)
    {
        if (_cacheDirectory is null || !Directory.Exists(_cacheDirectory)) return new([], true, null);
        const string prefix = "apple-public-catalog-";
        const string suffix = ".json";
        var countries = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if ((File.GetAttributes(_cacheDirectory) & FileAttributes.ReparsePoint) != 0)
                return new([], false, "cache-unavailable");
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, prefix + "??" + suffix, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.Length != prefix.Length + 2 + suffix.Length ||
                    !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var country = name.Substring(prefix.Length, 2);
                if (country.Any(character => character is not (>= 'A' and <= 'Z'))) continue;
                countries.Add(country);
                if (countries.Count > MaximumCacheStorefronts) return new([], false, "cache-limit");
            }
            return new(countries.OrderBy(country => country == _country ? 0 : 1)
                .ThenBy(country => country, StringComparer.Ordinal).ToArray(), true, null);
        }
        catch (IOException) { return new([], false, "cache-unavailable"); }
        catch (UnauthorizedAccessException) { return new([], false, "cache-unavailable"); }
    }

    private IReadOnlyList<string> StorefrontPlan(IEnumerable<string> unresolved,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? preferredStorefronts)
    {
        var countries = new List<string> { _country };
        if (!_storefrontFallback) return countries;
        void Add(string? country)
        {
            var valid = NormalizeCountry(country);
            if (valid is not null && countries.Count < MaximumStorefronts && !countries.Contains(valid, StringComparer.Ordinal)) countries.Add(valid);
        }
        if (_storefrontsForTests is not null)
        {
            foreach (var country in _storefrontsForTests.Take(16)) Add(country);
            return countries;
        }
        if (preferredStorefronts is not null)
            foreach (var id in unresolved)
                if (preferredStorefronts.TryGetValue(id, out var hints))
                    foreach (var country in hints.Take(16)) Add(country);
        Add(_regionalCountry);
        Add("KR"); Add("JP"); Add("TW");
        return countries;
    }

    private async Task<FetchResult> FetchAsync(string country, string[] ids, CancellationToken cancellationToken)
    {
        FetchResult Blocked(string outcome, int? status = null)
        {
            Report(country, outcome, status, ids.Length, 0, 0, ids.Length);
            return new([], ids.ToHashSet(StringComparer.Ordinal));
        }
        if (!await _requests.EnterAsync(cancellationToken).ConfigureAwait(false)) return Blocked("cooldown");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://itunes.apple.com/lookup?id=" + Uri.EscapeDataString(string.Join(',', ids)) +
                "&country=" + Uri.EscapeDataString(country) + "&lang=en_us");
            // The caller must supply a dedicated unauthenticated HttpClient. No credentials are copied from Apple Music.
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _requests.SetRetryAfter(response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
                return Blocked("retry-after", (int)response.StatusCode);
            }
            if (response.StatusCode != HttpStatusCode.OK) return Blocked("http-error", (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumBytes) return Blocked("invalid-response", (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(stream, timeout.Token).ConfigureAwait(false);
            var parsed = ParseResponse(bytes, ids);
            Report(country, parsed.Rejected.Count > 0 ? "rejected" : parsed.Records.Count == 0 ? "empty" : "resolved",
                (int)response.StatusCode, ids.Length, parsed.Records.Count,
                ids.Length - parsed.Records.Count - parsed.Rejected.Count, parsed.Rejected.Count);
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Blocked("timeout"); }
        catch (HttpRequestException) { return Blocked("network-error"); }
        catch (IOException) { return Blocked("invalid-response"); }
        catch (JsonException) { return Blocked("invalid-response"); }
        finally { _requests.Exit(); }
    }

    private static FetchResult ParseResponse(byte[] bytes, IReadOnlyCollection<string> ids)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (!IsUniqueObject(root) || !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 256 ||
            (root.TryGetProperty("resultCount", out var count) &&
             (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var total) || total != results.GetArrayLength())))
            return new([], ids.ToHashSet(StringComparer.Ordinal));
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        var records = new Dictionary<string, NativeCatalogTrack>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return new([], requested);
            if (!IsUniqueObject(item))
            {
                var identified = false;
                foreach (var property in item.EnumerateObject().Where(property => property.Name == "trackId"))
                    if (TryNumericCatalogId(property.Value, out var duplicateId) && requested.Contains(duplicateId))
                    {
                        rejected.Add(duplicateId);
                        identified = true;
                    }
                if (!identified) return new([], requested);
                continue;
            }
            if (!item.TryGetProperty("trackId", out var idValue) || !TryNumericCatalogId(idValue, out var id))
                return new([], requested);
            if (!requested.Contains(id)) continue;
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
        return new(records.Where(pair => !rejected.Contains(pair.Key)).Select(pair => pair.Value).ToArray(), rejected);
    }

    private async Task<CacheResult> LoadCacheAsync(string country, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, CachedTrack>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var cachePath = CachePath(country);
        if (cachePath is null) return new(cache, rejected);
        try
        {
            if (!IsOrdinaryFile(cachePath)) return new(cache, rejected);
            await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!IsUniqueObject(root) || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
                !IsString(root, "source", "https://itunes.apple.com/lookup") || !IsString(root, "country", country) ||
                !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() > MaximumCacheEntries) return new(cache, rejected);
            var now = _requests.Clock.GetUtcNow();
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
        return new(cache, rejected);
    }

    private async Task SaveCacheAsync(string country, IReadOnlyCollection<CachedTrack> added, CancellationToken cancellationToken)
    {
        var cachePath = CachePath(country);
        if (cachePath is null) return;
        await CacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.Exists(cachePath) && !IsOrdinaryFile(cachePath))) return;
            var cache = (await LoadCacheAsync(country, cancellationToken).ConfigureAwait(false)).Records;
            foreach (var item in added) cache[item.Track.CatalogId] = item;
            var entries = cache.Values.OrderByDescending(item => item.FetchedAt).Take(MaximumCacheEntries).ToArray();
            byte[] bytes;
            do
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = 1, source = "https://itunes.apple.com/lookup", country,
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
            File.Move(temporary, cachePath, true);
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

    private string? CachePath(string country) => _cacheDirectory is null ? null :
        Path.Combine(_cacheDirectory, $"apple-public-catalog-{country}.json");

    private static string? NormalizeCountry(string? country) => country is { Length: 2 } &&
        country.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z') ? country.ToUpperInvariant() : null;

    private void Report(string country, string outcome, int? status, int requested, int resolved, int missing, int rejected)
    {
        var diagnostic = new AppleCatalogLookupDiagnostic(country, outcome, status, requested, resolved, missing, rejected);
        foreach (var handler in Diagnostic?.GetInvocationList() ?? [])
        {
            try { ((Action<AppleCatalogLookupDiagnostic>)handler)(diagnostic); }
            catch { /* Diagnostic listeners must never affect matching, cancellation or request limits. */ }
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
    private sealed record CacheResult(Dictionary<string, CachedTrack> Records, HashSet<string> Rejected);
    private sealed record CacheInventory(IReadOnlyList<string> Countries, bool Complete, string? Failure);
    private sealed record FetchResult(IReadOnlyList<NativeCatalogTrack> Records, HashSet<string> Rejected);
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
