using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AppleMusicLyrics.Core;

namespace AppleMusicLyrics.Core.Tests;

public sealed class ApplePublicCatalogResolverTests
{
    [Fact]
    public async Task LookupUsesOnlyExplicitValidIdsAndPublicMetadataWithoutAuthentication()
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123, "借りてきた猫 / 한글"))));
        var result = await fixture.Resolver.ResolveAsync(["123", "123", "", "../123", "-1", "0123", "１２３",
            "9223372036854775808", "123&token=secret"]);
        var track = Assert.Single(result);
        Assert.Equal("123", track.CatalogId);
        Assert.Equal("借りてきた猫 / 한글", track.Metadata.Title);
        Assert.Equal(188.64, track.Metadata.DurationSeconds, 4);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("https://itunes.apple.com/lookup?id=123&country=AU&lang=en_us", request.Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.False(request.HadCredentials);
    }

    [Fact]
    public async Task BatchesAtTwentyIdsAndSpacesRequestsByThreeMonotonicSeconds()
    {
        using var fixture = new Fixture((request, _) =>
        {
            var ids = Uri.UnescapeDataString(request.RequestUri!.Query.Split('&')[0][4..]).Split(',');
            return Response(Envelope(ids.Select(id => Song(long.Parse(id))).ToArray()));
        });
        var result = await fixture.Resolver.ResolveAsync(Enumerable.Range(1, 21).Select(value => value.ToString()));
        Assert.Equal(21, result.Count);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(fixture.Clock.Delays));
        Assert.Equal(20, Uri.UnescapeDataString(fixture.Requests[0].Uri.Query.Split('&')[0]).Split(',').Length);
    }

    [Fact]
    public async Task InvalidAndExcessiveInputsNeverReachTheNetwork()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        Assert.Empty(await fixture.Resolver.ResolveAsync(["", "0", "001", "+1", "-1", " 1", "1.0", "1e1"]));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Resolver.ResolveAsync(
            Enumerable.Range(1, 129).Select(value => value.ToString())));
        Assert.Empty(fixture.Requests);
        Assert.Throws<ArgumentException>(() => new ApplePublicCatalogResolver(fixture.Client, null, "US&token=x"));
    }

    [Theory]
    [InlineData("\"trackId\":123", "\"trackId\":\"123\"")]
    [InlineData("\"trackId\":123", "\"trackId\":123.0")]
    [InlineData("\"trackId\":123", "\"trackId\":1.23e2")]
    [InlineData("\"trackId\":123", "\"trackId\":124")]
    [InlineData("\"wrapperType\":\"track\"", "\"wrapperType\":\"collection\"")]
    [InlineData("\"kind\":\"song\"", "\"kind\":\"music-video\"")]
    [InlineData("\"trackName\":\"Song\"", "\"trackName\":null")]
    [InlineData("\"artistName\":\"Artist\"", "\"artistName\":\" \"")]
    [InlineData("\"collectionName\":\"Album\"", "\"collectionName\":3")]
    [InlineData("\"trackTimeMillis\":188640", "\"trackTimeMillis\":999")]
    [InlineData("\"trackTimeMillis\":188640", "\"trackTimeMillis\":86400001")]
    [InlineData("\"trackTimeMillis\":188640", "\"trackTimeMillis\":1e999")]
    [InlineData("\"trackName\":\"Song\"", "\"trackName\":\"Wrong\",\"trackName\":\"Song\"")]
    public async Task RejectsIncompleteWrongTypeNonCanonicalAndDuplicateIdentityFields(string find, string replacement)
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123).Replace(find, replacement, StringComparison.Ordinal))));
        Assert.Empty(await fixture.Resolver.ResolveAsync(["123"]));
    }

    [Fact]
    public async Task ConflictingDuplicateIdIsNotReturnedOrCachedButUnrelatedRecordsAreIgnored()
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123), Song(123, "Different"), Song(124), Song(999))));
        var result = await fixture.Resolver.ResolveAsync(["123", "124"]);
        Assert.Equal("124", Assert.Single(result).CatalogId);
        var json = await File.ReadAllTextAsync(fixture.CachePath);
        Assert.DoesNotContain("\"catalogId\":\"123\"", json);
        using var second = fixture.NewResolver((_, _) => Response("{\"results\":[]}"));
        Assert.Equal("124", Assert.Single(await second.Resolver.ResolveAsync(["124"])).CatalogId);
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task IdenticalDuplicatesRemainOneIdentity()
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123), Song(123))));
        Assert.Single(await fixture.Resolver.ResolveAsync(["123"]));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":[],\"results\":[]}")]
    public async Task MalformedResponsesFailGracefully(string json)
    {
        using var fixture = new Fixture((_, _) => Response(json));
        Assert.Empty(await fixture.Resolver.ResolveAsync(["123"]));
    }

    [Fact]
    public async Task BoundedBodyAndNetworkFailuresReturnEmpty()
    {
        using var fixture = new Fixture((_, _) => Response(new string(' ', 1024 * 1024 + 1)));
        Assert.Empty(await fixture.Resolver.ResolveAsync(["123"]));
        using var failure = new Fixture((_, _) => throw new HttpRequestException("offline"));
        Assert.Empty(await failure.Resolver.ResolveAsync(["123"]));
    }

    [Fact]
    public async Task PerRequestTimeoutReturnsEmptyAndCallerCancellationPropagates()
    {
        using var fixture = new Fixture(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }, timeout: TimeSpan.FromMilliseconds(25));
        Assert.Empty(await fixture.Resolver.ResolveAsync(["123"]));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(["123"], cancel.Token));
    }

    [Fact]
    public async Task CancellationDuringNetworkIsNotMistakenForTimeout()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new Fixture(async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.ResolveAsync(["123"], cancel.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitRetainsRetryAfterAcrossResolverInstancesWithoutAutomaticRetry(bool date)
    {
        using var fixture = new Fixture((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = date
                ? new RetryConditionHeaderValue(new DateTimeOffset(2026, 11, 1, 1, 1, 0, TimeSpan.Zero))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        });
        Assert.Empty(await fixture.Resolver.ResolveAsync(["123"]));
        using var second = fixture.NewResolver((_, _) => Response(Envelope(Song(123))));
        Assert.Empty(await second.Resolver.ResolveAsync(["123"]));
        Assert.Empty(second.Requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await second.Resolver.ResolveAsync(["123"]));
        Assert.Single(fixture.Requests);
        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task CacheRetainsProvenanceAndExpiresAfterThirtyDays()
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123))));
        Assert.Single(await fixture.Resolver.ResolveAsync(["123"]));
        var json = await File.ReadAllTextAsync(fixture.CachePath);
        Assert.Contains("https://itunes.apple.com/lookup", json);
        Assert.Contains("\"country\":\"AU\"", json);
        Assert.Contains("\"fetchedAt\":", json);
        Assert.DoesNotContain("previewUrl", json);
        using var cached = fixture.NewResolver((_, _) => throw new InvalidOperationException("Cache should be used"));
        Assert.Single(await cached.Resolver.ResolveAsync(["123"]));
        fixture.Clock.Advance(TimeSpan.FromDays(31));
        using var expired = fixture.NewResolver((_, _) => Response("{\"results\":[]}"));
        Assert.Empty(await expired.Resolver.ResolveAsync(["123"]));
        Assert.Single(expired.Requests);
    }

    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"country\":\"AU\"", "\"country\":\"US\"")]
    [InlineData("\"catalogId\":\"123\"", "\"catalogId\":\"0123\"")]
    [InlineData("https://itunes.apple.com/lookup", "https://example.com/lookup")]
    public async Task UnsupportedOrPoisonedCacheDoesNotSupplyAnIdentity(string find, string replacement)
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123))));
        Assert.Single(await fixture.Resolver.ResolveAsync(["123"]));
        var json = (await File.ReadAllTextAsync(fixture.CachePath)).Replace(find, replacement, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.CachePath, json);
        using var second = fixture.NewResolver((_, _) => Response("{\"results\":[]}"));
        Assert.Empty(await second.Resolver.ResolveAsync(["123"]));
        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task CacheIsOptionalAndReadFailuresDoNotHideValidatedMetadata()
    {
        using var fixture = new Fixture((_, _) => Response(Envelope(Song(123))));
        var obstructed = Path.Combine(fixture.Directory, "file-instead-of-directory");
        await File.WriteAllTextAsync(obstructed, "owned test file");
        var resolver = new ApplePublicCatalogResolver(fixture.Client, obstructed, "au", fixture.Coordinator, TimeSpan.FromSeconds(8));
        Assert.Single(await resolver.ResolveAsync(["123"]));
    }

    private static string Song(long id, string title = "Song") => JsonSerializer.Serialize(new
    {
        wrapperType = "track", kind = "song", trackId = id, trackName = title, artistName = "Artist",
        collectionName = "Album", trackTimeMillis = 188640, previewUrl = "https://unused.invalid/audio"
    });

    private static string Envelope(params string[] songs) => "{\"resultCount\":" + songs.Length + ",\"results\":[" +
        string.Join(',', songs) + "]}";

    private static Task<HttpResponseMessage> Response(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private sealed record CapturedRequest(Uri Uri, HttpMethod Method, bool HadCredentials);

    private sealed class Fixture : IDisposable
    {
        private readonly bool _ownsDirectory;
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
            string? directory = null, ManualClock? clock = null, AppleCatalogRequestCoordinator? coordinator = null,
            TimeSpan? timeout = null)
        {
            Directory = directory ?? Path.Combine(Path.GetTempPath(), "ApplePublicCatalogResolverTests-" + Guid.NewGuid().ToString("N"));
            _ownsDirectory = directory is null;
            System.IO.Directory.CreateDirectory(Directory);
            Clock = clock ?? new ManualClock();
            Coordinator = coordinator ?? new AppleCatalogRequestCoordinator(Clock, (delay, token) =>
            {
                token.ThrowIfCancellationRequested();
                Clock.Delays.Add(delay);
                Clock.Advance(delay);
                return Task.CompletedTask;
            });
            Client = new HttpClient(new FakeHandler((request, token) =>
            {
                Requests.Add(new(request.RequestUri!, request.Method, request.Headers.Authorization is not null || request.Headers.Contains("Cookie")));
                return send(request, token);
            }));
            Resolver = new(Client, Directory, "AU", Coordinator, timeout ?? TimeSpan.FromSeconds(8));
        }

        internal string Directory { get; }
        internal string CachePath => Path.Combine(Directory, "apple-public-catalog-AU.json");
        internal HttpClient Client { get; }
        internal ApplePublicCatalogResolver Resolver { get; }
        internal ManualClock Clock { get; }
        internal AppleCatalogRequestCoordinator Coordinator { get; }
        internal List<CapturedRequest> Requests { get; } = [];
        internal Fixture NewResolver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            new(send, Directory, Clock, Coordinator);
        public void Dispose()
        {
            Client.Dispose();
            if (_ownsDirectory && System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
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
