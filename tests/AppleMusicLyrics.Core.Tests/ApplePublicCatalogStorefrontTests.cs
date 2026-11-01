using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AppleMusicLyrics.Core;

namespace AppleMusicLyrics.Core.Tests;

public sealed class ApplePublicCatalogStorefrontTests
{
    private const string WhiplashId = "1772644601";
    private const string WhiplashAlbum = "Whiplash - The 5th Mini Album - EP";

    [Fact]
    public async Task EmptyUsLookupFallsBackToPrioritizedKoreanStorefrontForWhiplash()
    {
        using var fixture = new Fixture((request, _) => Reply(Country(request) == "KR" ? Envelope(Whiplash()) : Envelope()), region: "AU");
        var result = Assert.Single(await fixture.Resolver.ResolveAsync([WhiplashId], preferredStorefronts: Hints("KR")));
        Assert.Equal(WhiplashId, result.CatalogId);
        Assert.Equal(NativeTrackMatchStatus.Matched, NativeTrackMatcher.Match(
            new("Whiplash", "aespa", WhiplashAlbum, 183), [result]).Status);
        Assert.Equal(["US", "KR"], fixture.Countries);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(fixture.Clock.Delays));
        Assert.Contains(fixture.Diagnostics, item => item.Country == "US" && item.Outcome == "empty" && item.StatusCode == 200);
        Assert.Contains(fixture.Diagnostics, item => item.Country == "KR" && item.ResolvedCount == 1);
    }

    [Fact]
    public async Task EmptyResultsHaveAtMostFourExplicitStorefrontsAndKeepRequestSpacing()
    {
        using var fixture = new Fixture((_, _) => Reply(Envelope()), region: "AU");
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US", "AU", "KR", "JP"], fixture.Countries);
        Assert.Equal(3, fixture.Clock.Delays.Count);
        Assert.All(fixture.Clock.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(3), delay));
    }

    [Fact]
    public async Task ValidUnresolvedIdHintsPrecedeWindowsRegionAndAreDeduplicated()
    {
        using var fixture = new Fixture((_, _) => Reply(Envelope()), region: "AU");
        var hints = new Dictionary<string, IReadOnlyList<string>>
        {
            [WhiplashId] = ["bad", "kr", "KR", "JP", "TW", "CN"], ["999"] = ["GB"]
        };
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId], preferredStorefronts: hints));
        Assert.Equal(["US", "KR", "JP", "TW"], fixture.Countries);
    }

    [Fact]
    public async Task ExistingPrimarySuccessDoesNotQueryOtherStorefronts()
    {
        using var fixture = new Fixture((_, _) => Reply(Envelope(Whiplash())), region: "AU");
        Assert.Single(await fixture.Resolver.ResolveAsync([WhiplashId], preferredStorefronts: Hints("KR")));
        Assert.Equal(["US"], fixture.Countries);
    }

    [Fact]
    public async Task OnlyMissingIdsAreSentToAlternativeStorefronts()
    {
        using var fixture = new Fixture((request, _) => Reply(Country(request) == "US"
            ? Envelope(Song(123)) : Envelope(Whiplash())));
        Assert.Equal(2, (await fixture.Resolver.ResolveAsync(["123", WhiplashId])).Count);
        Assert.Equal(["US", "KR"], fixture.Countries);
        Assert.Equal(WhiplashId, QueryValue(fixture.Requests[1], "id"));
    }

    [Fact]
    public async Task PrimaryResponseResolvesIrrelevantHintsBeforeChoosingWhiplashRegionalFallback()
    {
        using var fixture = new Fixture((request, _) => Reply(Country(request) == "US"
            ? Envelope(Song(123), Song(124)) : Country(request) == "KR" ? Envelope(Whiplash()) : Envelope()), region: "AU");
        var hints = new Dictionary<string, IReadOnlyList<string>>
        {
            ["123"] = ["JP"], ["124"] = ["TW"], [WhiplashId] = ["KR"]
        };
        Assert.Equal(3, (await fixture.Resolver.ResolveAsync(["123", "124", WhiplashId], preferredStorefronts: hints)).Count);
        Assert.Equal(["US", "KR"], fixture.Countries);
        Assert.Equal(WhiplashId, QueryValue(fixture.Requests[1], "id"));
    }

    [Fact]
    public async Task RegionalCacheRetainsItsOwnProvenanceAndWorksOfflineAfterRestart()
    {
        using var fixture = new Fixture((request, _) => Reply(Country(request) == "KR" ? Envelope(Whiplash()) : Envelope()));
        Assert.Single(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "apple-public-catalog-US.json")));
        var cached = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "apple-public-catalog-KR.json"));
        Assert.Contains("\"country\":\"KR\"", cached);
        Assert.Contains("https://itunes.apple.com/lookup", cached);
        Assert.DoesNotContain("previewUrl", cached);
        using var offlineClient = new HttpClient(new Handler((_, _) => throw new HttpRequestException("offline")));
        var restarted = new ApplePublicCatalogResolver(offlineClient, fixture.Directory, "US", fixture.Coordinator,
            TimeSpan.FromSeconds(8), null);
        Assert.Single(await restarted.ResolveAsync([WhiplashId]));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("version")]
    [InlineData("duration")]
    public async Task KnownConflictingRegionalMetadataRejectsTheIdentityWithoutHidingConflict(string field)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Conflicting identity must stay rejected"));
        await fixture.WriteCache("US", new("Whiplash", "aespa", WhiplashAlbum, 183.227));
        await fixture.WriteCache("KR", new(field == "title" ? "Another Song" : field == "version" ? "Whiplash (Live)" : "Whiplash",
            field == "artist" ? "Another Artist" : "aespa", field == "album" ? "Another Album" : WhiplashAlbum,
            field == "duration" ? 200 : 183.227));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Empty(fixture.Requests);
        Assert.Contains(fixture.Diagnostics, item => item.Outcome == "cache-conflict" && item.RejectedCount == 1);
    }

    [Fact]
    public async Task CompatibleNormalizedRegionalMetadataKeepsExistingStrictIdentity()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Cached identity expected"));
        await fixture.WriteCache("US", new("Whiplash", "aespa", WhiplashAlbum, 183));
        await fixture.WriteCache("KR", new("  WHIPLASH ", "AESPA", WhiplashAlbum, 183.227));
        Assert.Single(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task PrimaryCachedIdentityCannotHideConflictingCnCacheOutsideTheNetworkPlan()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Known conflict must never be queried around"), region: "AU");
        await fixture.WriteCache("US", new("Whiplash", "aespa", WhiplashAlbum, 183.227));
        await fixture.WriteCache("CN", new("Whiplash (Live)", "aespa", WhiplashAlbum, 183.227));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId], preferredStorefronts: Hints("KR", "CN")));
        Assert.Empty(fixture.Requests);
        Assert.Contains(fixture.Diagnostics, item => item.Outcome == "cache-conflict" && item.RejectedCount == 1);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task LocalProvenanceInspectionIsBoundedAndFailsClosedWhenAnUninspectedCountryMayConflict(int fileCount, bool accepted)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("No network expected"));
        string[] countries = ["US", "AU", "KR", "JP", "TW", "CN", "HK", "GB", "CA", "NZ", "SG", "DE", "FR", "ES", "IT", "IN",
            "BR", "MX", "NL", "BE", "SE", "NO", "DK", "FI", "AT", "CH", "PL", "PT", "IE", "ZA", "TH", "MY", "ID"];
        foreach (var country in countries.Take(fileCount)) await fixture.WriteEmptyCache(country);
        await fixture.WriteCache("US", new("Whiplash", "aespa", WhiplashAlbum, 183.227));
        var resolved = await fixture.Resolver.ResolveAsync([WhiplashId]);
        Assert.Equal(accepted ? 1 : 0, resolved.Count);
        Assert.Empty(fixture.Requests);
        Assert.Equal(!accepted, fixture.Diagnostics.Any(item => item.Outcome == "cache-limit"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsNeverTriggerStorefrontSwitchRetries(HttpStatusCode code)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(code)));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[],\"results\":[]}")]
    [InlineData("{\"resultCount\":1,\"results\":[]}")]
    [InlineData("{\"results\":[null]}")]
    public async Task MalformedResponseIsNotMisclassifiedAsARegionalEmptyResult(string json)
    {
        using var fixture = new Fixture((_, _) => Reply(json));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
    }

    [Fact]
    public async Task ConflictingSuccessfulResponseIsRejectedWithoutTryingAnotherCountry()
    {
        using var fixture = new Fixture((_, _) => Reply(Envelope(Whiplash(), Song(long.Parse(WhiplashId), "Wrong recording"))));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "apple-public-catalog-US.json")));
    }

    [Fact]
    public async Task RetryAfterCooldownRemainsSharedAndCannotBeEvadedByChangingStorefront()
    {
        using var fixture = new Fixture((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        });
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Cooldown must suppress all countries")));
        var second = new ApplePublicCatalogResolver(client, fixture.Directory, "KR", fixture.Coordinator,
            TimeSpan.FromSeconds(8), "AU");
        Assert.Empty(await second.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
    }

    [Fact]
    public async Task NetworkFailureAndTimeoutDoNotTriggerRegionalRetries()
    {
        using var fixture = new Fixture((_, _) => throw new HttpRequestException("offline"));
        Assert.Empty(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
        using var timeout = new Fixture(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }, timeout: TimeSpan.FromMilliseconds(25));
        Assert.Empty(await timeout.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], timeout.Countries);
    }

    [Fact]
    public async Task CancellationDuringFallbackNetworkPropagatesWithoutCachingLateResponse()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new Fixture(async (request, token) =>
        {
            if (Country(request) == "US") return await Reply(Envelope());
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync([WhiplashId], cancel.Token));
        Assert.Equal(["US", "KR"], fixture.Countries);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task CancellationWhileWaitingForRateSpacingPreventsAlternativeRequest()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new Fixture((_, _) => Reply(Envelope()), spacing: (_, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync([WhiplashId], cancel.Token));
        Assert.Equal(["US"], fixture.Countries);
    }

    [Fact]
    public async Task CancellationWhileQueuedBehindAnotherResolverDoesNotSendARequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return await Reply(Envelope(Whiplash()));
        });
        var first = fixture.Resolver.ResolveAsync([WhiplashId]);
        await entered.Task;
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Canceled queued request must not send")));
        var second = new ApplePublicCatalogResolver(client, fixture.Directory, "US", fixture.Coordinator,
            TimeSpan.FromSeconds(8), null);
        using var cancel = new CancellationTokenSource();
        var queued = second.ResolveAsync([WhiplashId], cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult();
        Assert.Single(await first);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ThrowingDiagnosticListenerCannotChangeAValidLookup()
    {
        using var fixture = new Fixture((_, _) => Reply(Envelope(Whiplash())));
        fixture.Resolver.Diagnostic += _ => throw new InvalidOperationException("Broken log sink");
        Assert.Single(await fixture.Resolver.ResolveAsync([WhiplashId]));
        Assert.Equal(["US"], fixture.Countries);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Hints(params string[] countries) =>
        new Dictionary<string, IReadOnlyList<string>> { [WhiplashId] = countries };

    private static string Country(HttpRequestMessage request) => QueryValue(request.RequestUri!, "country");
    private static string QueryValue(Uri uri, string key) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&')
        .Single(part => part.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..]);
    private static string Whiplash() => Song(long.Parse(WhiplashId), "Whiplash", "aespa", WhiplashAlbum, 183227);
    private static string Song(long id, string title = "Song", string artist = "Artist", string album = "Album", int milliseconds = 188640) =>
        JsonSerializer.Serialize(new { wrapperType = "track", kind = "song", trackId = id, trackName = title,
            artistName = artist, collectionName = album, trackTimeMillis = milliseconds });
    private static string Envelope(params string[] songs) => "{\"resultCount\":" + songs.Length + ",\"results\":[" + string.Join(',', songs) + "]}";
    private static Task<HttpResponseMessage> Reply(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private sealed class Fixture : IDisposable
    {
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, string? region = null,
            TimeSpan? timeout = null, Func<TimeSpan, CancellationToken, Task>? spacing = null)
        {
            Directory = Path.Combine(Path.GetTempPath(), "AppleCatalogStorefrontTests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Coordinator = new(Clock, spacing ?? ((delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                Clock.Delays.Add(delay);
                Clock.Advance(delay);
                return Task.CompletedTask;
            }));
            Client = new(new Handler((request, token) =>
            {
                Requests.Add(request.RequestUri!);
                return send(request, token);
            }));
            Resolver = new(Client, Directory, "US", Coordinator, timeout ?? TimeSpan.FromSeconds(8), region);
            Resolver.Diagnostic += Diagnostics.Add;
        }

        internal string Directory { get; }
        internal List<Uri> Requests { get; } = [];
        internal string[] Countries => Requests.Select(uri => QueryValue(uri, "country")).ToArray();
        internal List<AppleCatalogLookupDiagnostic> Diagnostics { get; } = [];
        internal ManualClock Clock { get; } = new();
        internal AppleCatalogRequestCoordinator Coordinator { get; }
        internal HttpClient Client { get; }
        internal ApplePublicCatalogResolver Resolver { get; }
        internal async Task WriteCache(string country, TrackMetadata track) => await File.WriteAllBytesAsync(
            Path.Combine(Directory, $"apple-public-catalog-{country}.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, source = "https://itunes.apple.com/lookup", country,
                entries = new[] { new { catalogId = WhiplashId, title = track.Title, artist = track.Artist, album = track.Album,
                    durationSeconds = track.DurationSeconds, fetchedAt = Clock.GetUtcNow() } }
            }));
        internal async Task WriteEmptyCache(string country) => await File.WriteAllBytesAsync(
            Path.Combine(Directory, $"apple-public-catalog-{country}.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { version = 1, source = "https://itunes.apple.com/lookup", country, entries = Array.Empty<object>() }));
        public void Dispose()
        {
            Client.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 11, 1, 1, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        internal List<TimeSpan> Delays { get; } = [];
        internal void Advance(TimeSpan duration) { _utc += duration; _ticks += duration.Ticks; }
    }
}
