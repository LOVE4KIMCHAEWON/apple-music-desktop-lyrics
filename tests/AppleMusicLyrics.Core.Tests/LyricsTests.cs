using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AppleMusicLyrics.Core.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void ParsesFractionsRepeatedTimestampsAndSorts()
    {
        var result = LrcParser.Parse("[00:20.123]第三行\r\n[00:01.2][00:03.45]Hello 世界\n[00:02]한국어 日本語\n[00:01.2]Hello 世界");
        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { 1.2, 2, 3.45, 20.123 }, result.Select(line => line.Time.TotalSeconds));
        Assert.Equal("Hello 世界", result[0].Text);
        Assert.Equal("한국어 日本語", result[1].Text);
    }

    [Fact]
    public void MetadataOffsetAdvancesLyricsAndPreservesInstrumentalBlank()
    {
        var result = LrcParser.Parse("\uFEFF[ti:Title]\n[00:00.10]Start\n[00:05.000]\n[offset:+250]\n[ar:Artist]");
        Assert.Equal(TimeSpan.Zero, result[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(4750), result[1].Time);
        Assert.Equal("", result[1].Text);
    }

    [Fact]
    public void NegativeOffsetDelaysLyricsAndWordTagsDoNotAppear()
    {
        var result = Assert.Single(LrcParser.Parse("[offset:-125]\n[00:10.250]<00:10.250>Hello <00:10.700>world"));
        Assert.Equal(TimeSpan.FromMilliseconds(10375), result.Time);
        Assert.Equal("Hello world", result.Text);
    }

    [Fact]
    public void InvalidAndPlainLyricsNeverAcquireTiming()
    {
        Assert.Empty(LrcParser.Parse("Plain lyrics\n[bad]bad\n[00:99.12]invalid\n[00:01.1234]invalid\n[ar:Artist]"));
    }
}

public sealed class MetadataTests
{
    [Fact]
    public void ApplesEmDashRepresentationUsesAlbumOnlyWhenMissing()
    {
        var raw = new TrackMetadata("Supernatural", "NewJeans — Supernatural - Single", "", 191);
        var result = MetadataNormalizer.Prepare(raw);
        Assert.Equal("NewJeans", result.Artist);
        Assert.Equal("Supernatural - Single", result.Album);
        Assert.Equal(raw.Key, result.Key);
        Assert.Equal("Artist — Band", MetadataNormalizer.Prepare(raw with { Artist = "Artist — Band", Album = "Real album" }).Artist);
        Assert.Equal("Jay-Z", MetadataNormalizer.Prepare(raw with { Artist = "Jay-Z" }).Artist);
    }

    [Theory]
    [InlineData("  Don’t   Stop! ", "dont stop")]
    [InlineData("ＳＯＮＧ 你好 日本語 한국어", "song 你好 日本語 한국어")]
    public void NormalizesTypographyWithoutDestroyingUnicode(string input, string expected) =>
        Assert.Equal(expected, MetadataNormalizer.Normalize(input));

    [Theory]
    [InlineData("Song (Live)", "Album")]
    [InlineData("Song - Remix", "Album")]
    [InlineData("Song (Cover)", "Album")]
    [InlineData("Song", "Live at the Arena")]
    [InlineData("Song (Acoustic Version)", "Album")]
    public void RejectsAlternateVersions(string title, string album)
    {
        var rank = LyricsCandidateRanker.Score(new TrackMetadata("Song", "Artist", "Album", 180),
            Candidate() with { TrackName = title, AlbumName = album });
        Assert.True(double.IsNegativeInfinity(rank));
    }

    [Theory]
    [InlineData("现场版")]
    [InlineData("現場版")]
    [InlineData("ライブ")]
    [InlineData("라이브")]
    [InlineData("翻唱")]
    [InlineData("カバー")]
    [InlineData("커버")]
    [InlineData("混音")]
    [InlineData("リミックス")]
    [InlineData("리믹스")]
    [InlineData("伴奏")]
    [InlineData("纯音乐")]
    [InlineData("インストゥルメンタル")]
    [InlineData("연주곡")]
    public void RejectsUnrequestedCjkAlbumVersions(string album)
    {
        Assert.True(double.IsNegativeInfinity(LyricsCandidateRanker.Score(new TrackMetadata("Song", "Artist", "Album", 180),
            Candidate() with { AlbumName = album })));
    }

    [Theory]
    [InlineData("现场版", "现场版")]
    [InlineData("ライブ", "라이브")]
    [InlineData("混音", "リミックス")]
    [InlineData("翻唱", "カバー")]
    [InlineData("伴奏", "연주곡")]
    public void PreservesRequestedCjkVersionCategory(string requestedAlbum, string candidateAlbum)
    {
        Assert.True(double.IsFinite(LyricsCandidateRanker.Score(new TrackMetadata("Song", "Artist", requestedAlbum, 180),
            Candidate() with { AlbumName = candidateAlbum })));
    }

    [Fact]
    public void DifferentCjkVersionCategoriesRemainMismatched()
    {
        Assert.True(double.IsNegativeInfinity(LyricsCandidateRanker.Score(new TrackMetadata("Song", "Artist", "现场版", 180),
            Candidate() with { AlbumName = "混音" })));
    }

    [Fact]
    public void PrefersSyncedButRejectsWrongArtistAndDuration()
    {
        var track = new TrackMetadata("Song", "Artist", "Album", 180);
        var plain = Candidate() with { Id = 1, SyncedLyrics = null };
        var synced = Candidate() with { Id = 2 };
        Assert.Equal(synced, LyricsCandidateRanker.Select(track,
            [plain, synced, synced with { Id = 3, ArtistName = "Cover Band" }, synced with { Id = 4, Duration = 200 }]));
    }

    private static LyricsCandidate Candidate() => new()
    {
        Id = 10, TrackName = "Song", ArtistName = "Artist", AlbumName = "Album", Duration = 180,
        SyncedLyrics = "[00:01]Hello", PlainLyrics = "Hello"
    };
}

[Collection("Sequential HTTP")]
public sealed class LyricsProviderTests
{
    private static readonly TrackMetadata Track = new("Song", "Artist", "Album", 180);
    private static readonly LrclibOptions Options = new() { ClientContact = "https://example.com/lyrics-test", MaximumRetries = 0 };

    [Fact]
    public async Task GetIncludesCompleteMetadataAndClientIdentifier()
    {
        using var handler = new StubHandler((request, _) =>
        {
            Assert.Contains("track_name=Song", request.RequestUri!.Query);
            Assert.Contains("artist_name=Artist", request.RequestUri.Query);
            Assert.Contains("album_name=Album", request.RequestUri.Query);
            Assert.Contains("duration=180", request.RequestUri.Query);
            Assert.Contains("AppleMusicDesktopLyrics/0.2.4", string.Join(' ', request.Headers.GetValues("User-Agent")));
            return Task.FromResult(Json(Candidate()));
        });
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track);
        Assert.True(result!.HasSynchronizedLyrics);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FallbackSearchRejectsRemixAndChoosesSynced()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(new[] { Candidate() with { TrackName = "Song (Remix)" }, Candidate() with { Id = 42 } })));
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track);
        Assert.Equal("LRCLIB #42", result!.Source);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task BusyLookupCanFallBackToAvailableSearchAfterCooldown()
    {
        DateTimeOffset? busyAt = null;
        DateTimeOffset? searchAt = null;
        using var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal))
            {
                busyAt = DateTimeOffset.UtcNow;
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return Task.FromResult(busy);
            }
            searchAt = DateTimeOffset.UtcNow;
            return Task.FromResult(Json(new[] { Candidate() }));
        });
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track);
        Assert.True(result!.HasSynchronizedLyrics);
        Assert.True(searchAt - busyAt >= TimeSpan.FromSeconds(1));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PlainLyricsRemainUntimedWhenFallbackHasNoSyncedMatch()
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal)
            ? Json(Candidate() with { SyncedLyrics = null }) : Json(Array.Empty<LyricsCandidate>())));
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track);
        Assert.Empty(result!.Lines);
        Assert.Equal("Hello", result.PlainLyrics);
    }

    [Fact]
    public async Task ReportsMissingIdentificationBeforeAnyNetworkCall()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No network expected."));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<LyricsProviderException>(() => new LrclibLyricsProvider(client).GetLyricsAsync(Track));
        Assert.Equal(LyricsProviderError.Configuration, exception.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TimeoutIsDistinguishedFromTrackCancellation()
    {
        using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(Candidate()); });
        using var client = new HttpClient(handler);
        var provider = new LrclibLyricsProvider(client, options: Options with { RequestTimeout = TimeSpan.FromMilliseconds(30) });
        var exception = await Assert.ThrowsAsync<LyricsProviderException>(() => provider.GetLyricsAsync(Track));
        Assert.Equal(LyricsProviderError.Timeout, exception.Error);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetLyricsAsync(Track, cancellation.Token));
    }

    [Fact]
    public async Task CancelsObsoleteInFlightRequestWithoutRetryOrTimeoutError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(Candidate());
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var request = new LrclibLyricsProvider(client, options: Options with { MaximumRetries = 2 })
            .GetLyricsAsync(Track, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("text/html", "<html>Error</html>")]
    [InlineData("application/json", "not JSON")]
    public async Task MalformedServerResponsesHaveAnExplicitError(string contentType, string body)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, contentType) }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<LyricsProviderException>(() =>
            new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track));
        Assert.Equal(LyricsProviderError.InvalidResponse, exception.Error);
    }

    [Fact]
    public async Task ChunkedResponseCannotExceedFourMegabyteLimit()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"plainLyrics\":\"" + new string('x', 4 * 1024 * 1024) + "\"}");
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new UnknownLengthContent(bytes) }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<LyricsProviderException>(() =>
            new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track));
        Assert.Equal(LyricsProviderError.InvalidResponse, exception.Error);
        Assert.Contains("large response", exception.Message);
    }

    [Fact]
    public async Task SuccessfulOnlineLyricsAreCachedAndAvailableOffline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyricsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalLyricsCache(directory);
            using var handler = new StubHandler((_, _) => Task.FromResult(Json(Candidate())));
            using var client = new HttpClient(handler);
            await new LrclibLyricsProvider(client, cache, Options).GetLyricsAsync(Track);
            using var offline = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("No network expected.")));
            var result = await new LrclibLyricsProvider(offline, cache).GetLyricsAsync(Track);
            Assert.Equal("LRCLIB #1", result!.Source);
            Assert.Single(result.Lines);
            Assert.Single(handler.Requests);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OnlineQuotaEvictsOldestAndPreservesImportsLegacyAndUnrelatedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyricsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalLyricsCache(directory, maxOnlineCacheBytes: 1024);
            var online = new LyricsResult([], new string('x', 500), false, "LRCLIB #1");
            var imported = new LyricsResult([new(TimeSpan.FromSeconds(1), new string('y', 1200))], null, false, "Local LRC: user.lrc");
            var first = Track with { Title = "First" };
            var second = Track with { Title = "Second" };
            var userTrack = Track with { Title = "User import" };
            var legacyTrack = Track with { Title = "Legacy import" };
            await cache.PutAsync(userTrack, imported);
            await cache.PutAsync(legacyTrack, imported);
            var legacyPath = Path.Combine(directory, legacyTrack.Key + ".json");
            File.Move(Path.Combine(directory, "imported", legacyTrack.Key + ".json"), legacyPath);
            await cache.PutAsync(first, online);
            var firstPath = Path.Combine(directory, "online", first.Key + ".json");
            File.SetLastWriteTimeUtc(firstPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var unrelated = Path.Combine(directory, "online", "user-notes.json");
            await File.WriteAllTextAsync(unrelated, new string('z', 2048));
            await cache.PutAsync(second, online);
            Assert.False(File.Exists(firstPath));
            Assert.Null(await cache.GetAsync(first));
            Assert.NotNull(await cache.GetAsync(second));
            Assert.NotNull(await cache.GetAsync(userTrack));
            Assert.NotNull(await cache.GetAsync(legacyTrack));
            Assert.True(File.Exists(legacyPath));
            Assert.True(File.Exists(unrelated));
            Assert.True(new FileInfo(Path.Combine(directory, "imported", userTrack.Key + ".json")).Length > 1024);
            Assert.True(Directory.GetFiles(Path.Combine(directory, "online"), "*.json")
                .Where(path => Path.GetFileName(path).Length == 69).Sum(path => new FileInfo(path).Length) <= 1024);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OversizedSerializedCacheEntryIsNotRetained()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyricsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalLyricsCache(directory);
            var oversized = new LyricsResult([], new string('x', 4 * 1024 * 1024), false, "LRCLIB #1");
            await Assert.ThrowsAsync<IOException>(() => cache.PutAsync(Track, oversized));
            Assert.Empty(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CorruptLegacyEntryDoesNotHideCurrentOnlineCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyricsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new LocalLyricsCache(directory);
            await cache.PutAsync(Track, new LyricsResult([], "Hello", false, "LRCLIB #1"));
            await File.WriteAllTextAsync(Path.Combine(directory, Track.Key + ".json"), "malformed");
            Assert.Equal("LRCLIB #1", (await cache.GetAsync(Track))!.Source);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitHonorsDeltaAndDateRetryAfter(bool useDate)
    {
        var calls = 0;
        DateTimeOffset? firstTime = null;
        DateTimeOffset? secondTime = null;
        DateTimeOffset required = default;
        using var handler = new StubHandler((_, _) =>
        {
            if (calls++ > 0) { secondTime = DateTimeOffset.UtcNow; return Task.FromResult(Json(Candidate())); }
            firstTime = DateTimeOffset.UtcNow;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            required = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            response.Headers.RetryAfter = useDate ? new RetryConditionHeaderValue(required) : new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await new LrclibLyricsProvider(client, options: Options with { MaximumRetries = 1 }).GetLyricsAsync(Track);
        Assert.NotNull(firstTime);
        Assert.NotNull(secondTime);
        Assert.True(secondTime >= required);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExhaustedRateLimitReportsRetryTime()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<LyricsProviderException>(() => new LrclibLyricsProvider(client, options: Options).GetLyricsAsync(Track));
        Assert.Equal(LyricsProviderError.RateLimited, exception.Error);
        Assert.NotNull(exception.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CorruptCacheIsIgnoredAndImportedCacheNeedsNoNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppleMusicLyricsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new LocalLyricsCache(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, Track.Key + ".json"), "broken");
            Assert.Null(await cache.GetAsync(Track));
            var lrc = Path.Combine(directory, "测试.lrc");
            await File.WriteAllTextAsync(lrc, "[00:01.25]你好 日本語 한국어");
            var imported = await LocalLyricsImporter.ImportAsync(lrc, Track, cache);
            using var client = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("No network expected.")));
            var result = await new LrclibLyricsProvider(client, cache).GetLyricsAsync(Track);
            Assert.Equal(imported.Lines, result!.Lines);
            Assert.StartsWith("Local LRC", result.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ImportRejectsPlainLyricsRatherThanInventingTimestamps()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            await File.WriteAllTextAsync(path, "These are plain lyrics");
            await Assert.ThrowsAsync<FormatException>(() => LocalLyricsImporter.ImportAsync(path, Track));
        }
        finally { File.Delete(path); }
    }

    private static LyricsCandidate Candidate() => new()
    {
        Id = 1, TrackName = Track.Title, ArtistName = Track.Artist, AlbumName = Track.Album,
        Duration = 180, PlainLyrics = "Hello", SyncedLyrics = "[00:01.000]Hello"
    };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return action(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UnknownLengthContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes));
    }
}

[CollectionDefinition("Sequential HTTP", DisableParallelization = true)]
public sealed class SequentialHttpCollection;
