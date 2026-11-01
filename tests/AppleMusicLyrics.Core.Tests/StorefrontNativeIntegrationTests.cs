using System.Net;
using System.Text;
using System.Text.Json;
using AppleMusicLyrics.App;

namespace AppleMusicLyrics.Core.Tests;

/// <summary>
/// Public identity fields reproduce the Whiplash storefront failure; every lyric
/// string is synthetic. These tests do not include Apple cache files or lyrics.
/// </summary>
public sealed class StorefrontNativeIntegrationTests : IDisposable
{
    private const string CatalogId = "1772644601";
    private static readonly TrackMetadata Whiplash = new("Whiplash", "aespa", "Whiplash - The 5th Mini Album - EP", 183);
    private static readonly NativeCatalogTrack VerifiedWhiplash = new(CatalogId, Whiplash with { DurationSeconds = 183.227 });
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "storefront-native-tests-" + Guid.NewGuid().ToString("N"));

    public StorefrontNativeIntegrationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false, false, 2, PresentedLyricKind.Upcoming)]
    [InlineData(true, false, 2, PresentedLyricKind.Translation)]
    [InlineData(false, true, 2, PresentedLyricKind.Pronunciation)]
    [InlineData(true, true, 3, PresentedLyricKind.Pronunciation)]
    public async Task WhiplashUsEmptyKrExactMetadataRestoresNativeAndAllFourLayouts(
        bool translation, bool pronunciation, int lineCount, PresentedLyricKind secondKind)
    {
        await WriteLyricsAsync(CatalogId);
        var provider = Provider();
        var before = await provider.GetNativeLyricsAsync(Whiplash);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, before.Status);
        Assert.Equal(new[] { CatalogId }, before.CandidateCatalogIds);
        Assert.Equal(new[] { "KR", "CN" }, before.CandidateStorefrontHints![CatalogId]);

        using var fixture = ResolverFixture.KoreanOnly(VerifiedWhiplash, Path.Combine(_directory, "identity-cache"));
        var catalog = await fixture.Resolver.ResolveAsync(before.CandidateCatalogIds!,
            preferredStorefronts: before.CandidateStorefrontHints);
        Assert.Equal(new[] { "US", "KR" }, fixture.Countries);
        Assert.Equal(VerifiedWhiplash, Assert.Single(catalog));
        var native = await provider.GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.Matched, native.Status);
        Assert.Equal(CatalogId, native.CatalogId);
        Assert.Equal("Apple Music native cache", native.Document!.ToLyricsResult().Source);

        var presentation = LyricPresentation.Select(native.Document, TimeSpan.FromSeconds(2), translation,
            pronunciation, "zh-Hans", "ko-Latn");
        Assert.Equal(lineCount, presentation.Lines.Count);
        Assert.Equal("Synthetic original one", presentation.Lines[0].Text);
        Assert.Equal(secondKind, presentation.Lines[1].Kind);
        Assert.Null(presentation.Limitation);
        if (translation) Assert.Equal("测试翻译一", presentation.Lines.Last().Text);
        if (pronunciation) Assert.Equal("synthetic pronunciation one", presentation.Lines[1].Text);
        Assert.Equal(new[] { 1.125, 4.25 }, native.Document.Lines.Select(line => line.Begin.TotalSeconds));
        Assert.Equal(new[] { "L1", "L2" }, native.Document.Lines.Select(line => line.Key));
        Assert.Equal("zh-Hans", Assert.Single(native.Document.Translations).Language);
        Assert.Equal("ko-Latn", Assert.Single(native.Document.Pronunciations).Language);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("version")]
    [InlineData("duration")]
    public async Task RegionalRecoveryStillRejectsEachIncorrectIdentityField(string field)
    {
        await WriteLyricsAsync(CatalogId);
        var wrong = field switch
        {
            "id" => VerifiedWhiplash with { CatalogId = "1772644602" },
            "title" => VerifiedWhiplash with { Metadata = Whiplash with { Title = "Different song" } },
            "artist" => VerifiedWhiplash with { Metadata = Whiplash with { Artist = "Different artist" } },
            "album" => VerifiedWhiplash with { Metadata = Whiplash with { Album = "Different album" } },
            "version" => VerifiedWhiplash with { Metadata = Whiplash with { Title = "Whiplash (Live)" } },
            "duration" => VerifiedWhiplash with { Metadata = Whiplash with { DurationSeconds = 186.001 } },
            _ => throw new ArgumentException(nameof(field))
        };
        using var fixture = ResolverFixture.KoreanOnly(wrong);
        var catalog = await fixture.Resolver.ResolveAsync([CatalogId]);
        var result = await Provider().GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task RecoveringWhiplashCannotIgnoreAnotherUnresolvedNativeCandidate()
    {
        await WriteLyricsAsync(CatalogId);
        await WriteLyricsAsync("999");
        var provider = Provider();
        var unresolved = await provider.GetNativeLyricsAsync(Whiplash);
        Assert.Equal(new[] { CatalogId, "999" }, unresolved.CandidateCatalogIds);
        using var fixture = ResolverFixture.KoreanOnly(VerifiedWhiplash);
        var catalog = await fixture.Resolver.ResolveAsync(unresolved.CandidateCatalogIds!);
        var result = await provider.GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Contains("remain unresolved", result.Message);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task ExistingCompleteLocalMatchStillNeedsNoPublicResolver()
    {
        await WriteLyricsAsync(CatalogId);
        await WriteCatalogAsync(VerifiedWhiplash);
        var result = await Provider().GetNativeLyricsAsync(Whiplash);
        Assert.Equal(NativeLyricsLookupStatus.Matched, result.Status);
        Assert.Null(result.CandidateCatalogIds);
        Assert.Null(result.CandidateStorefrontHints);
        Assert.Equal(CatalogId, result.CatalogId);
    }

    [Fact]
    public async Task IndistinguishableRegionalCandidatesAndConflictingLocalMetadataRemainRejected()
    {
        await WriteLyricsAsync(CatalogId);
        await WriteLyricsAsync("999");
        using var fixture = new ResolverFixture((country, _) => country == "US"
            ? Response("{\"results\":[]}")
            : Response(Envelope(VerifiedWhiplash, VerifiedWhiplash with { CatalogId = "999" })));
        var catalog = await fixture.Resolver.ResolveAsync([CatalogId, "999"]);
        var result = await Provider().GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
        await WriteCatalogAsync(VerifiedWhiplash with { Metadata = Whiplash with { Artist = "Conflicting artist" } });
        var localConflict = await Provider().GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, localConflict.Status);
        Assert.Null(localConflict.Document);
    }

    [Fact]
    public async Task ReprobeAfterSyntheticLateCacheArrivalCanUpgradeFallbackToNative()
    {
        var provider = Provider();
        var initial = await provider.GetNativeLyricsAsync(Whiplash);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, initial.Status);
        Assert.Empty(initial.CandidateCatalogIds!);
        Assert.Null(initial.Document);

        using var monitor = new NativeCacheMonitor(provider.CacheRoots);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += () => changed.TrySetResult();
        // Exercise the actual app watcher against a test-owned cache. Its signal
        // triggers a new provider probe; this does not exercise WPF UI dispatch.
        await WriteLyricsAsync(CatalogId);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var late = await provider.GetNativeLyricsAsync(Whiplash);
        using var fixture = ResolverFixture.KoreanOnly(VerifiedWhiplash);
        var catalog = await fixture.Resolver.ResolveAsync(late.CandidateCatalogIds!,
            preferredStorefronts: late.CandidateStorefrontHints);
        var upgraded = await provider.GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.Matched, upgraded.Status);
        Assert.Equal(CatalogId, upgraded.CatalogId);
    }

    [Fact]
    public async Task CancellationDuringRegionalLookupCannotReturnAnObsoleteNativeDocument()
    {
        await WriteLyricsAsync(CatalogId);
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ResolverFixture(async (country, token) =>
        {
            if (country == "US") return await Response("{\"results\":[]}");
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Resolver.ResolveAsync([CatalogId], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider().GetNativeLyricsAsync(Whiplash, cancellation.Token, [VerifiedWhiplash]));

        // A later current-track probe remains usable after the obsolete request was cancelled.
        using var current = ResolverFixture.KoreanOnly(VerifiedWhiplash);
        var catalog = await current.Resolver.ResolveAsync([CatalogId]);
        var result = await Provider().GetNativeLyricsAsync(Whiplash, additionalCatalog: catalog);
        Assert.Equal(NativeLyricsLookupStatus.Matched, result.Status);
    }

    [Theory]
    [InlineData("ko", "KR")]
    [InlineData("ko-Latn", "KR")]
    [InlineData("ja", "JP")]
    [InlineData("ZH-hant", "TW")]
    [InlineData("zh-Hans", "CN")]
    [InlineData("en-GB", "GB")]
    [InlineData("en-AU", "AU")]
    [InlineData("en", null)]
    [InlineData("zh", null)]
    [InlineData("en-ZZ", null)]
    [InlineData("unknown", null)]
    [InlineData("ko;country=KR", null)]
    public void LanguageHintsAreSuggestionsFromValidatedTags(string language, string? expected)
    {
        var document = Document() with { Translations = [], Pronunciations = [] };
        document = document with { Lines = document.Lines.Select(line => line with { Language = language }).ToArray() };
        var hints = AppleCatalogStorefrontHints.Build([document], [CatalogId]);
        if (expected is null) Assert.Empty(hints);
        else Assert.Equal(new[] { expected }, hints[CatalogId]);
    }

    [Fact]
    public void LanguageHintsPrioritizeOriginalAndPronunciationOverTranslationAndAreBounded()
    {
        var document = Document();
        var line = document.Lines[0];
        var translation = document.Translations[0];
        document = document with
        {
            Lines = [line with { Language = "ja" }, line with { Language = "ja" }, line with { Language = "en" }],
            Translations = [translation with { Language = "zh-Hans" }, translation with { Language = "zh-Hant" },
                translation with { Language = "en-AU" }, translation with { Language = "en-GB" }]
        };
        Assert.Equal(new[] { "JP", "KR", "CN", "TW" }, AppleCatalogStorefrontHints.Build([document], [CatalogId])[CatalogId]);
    }

    [Fact]
    public void LanguageHintsCannotCreateNewOrConflictingNativeResourceIdentities()
    {
        var document = Document();
        Assert.Empty(AppleCatalogStorefrontHints.Build([document], ["999"]));
        Assert.Empty(AppleCatalogStorefrontHints.Build([document with { ResourceId = "999" }], [CatalogId]));
        Assert.Empty(AppleCatalogStorefrontHints.Build([document with { ResourceType = "songs" }], [CatalogId]));
        Assert.Empty(AppleCatalogStorefrontHints.Build([document with { CatalogId = "../1772644601" }], ["../1772644601"]));
    }

    private AppleNativeLyricsProvider Provider() => new([_directory]);
    private static NativeLyricsDocument Document() => Assert.Single(AppleNativeLyricsParser.Parse(Payload(CatalogId)));
    private Task WriteLyricsAsync(string id) => File.WriteAllTextAsync(Path.Combine(_directory, $"syllable-lyrics[{id}].json"), Payload(id));
    private static string Payload(string id) => JsonSerializer.Serialize(new
    {
        data = new[] { new { id, type = "syllable-lyrics", attributes = new
        {
            playParams = new { catalogId = id, id = "AP_" + id },
            ttmlLocalizations = """
                <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xml:lang="en">
                  <head><metadata><itunes:iTunesMetadata>
                    <itunes:translations><itunes:translation type="subtitle" xml:lang="zh-Hans">
                      <itunes:text for="L2">测试翻译二</itunes:text><itunes:text for="L1">测试翻译一</itunes:text>
                    </itunes:translation></itunes:translations>
                    <itunes:transliterations><itunes:transliteration xml:lang="ko-Latn">
                      <itunes:text for="L2">synthetic pronunciation two</itunes:text><itunes:text for="L1">synthetic pronunciation one</itunes:text>
                    </itunes:transliteration></itunes:transliterations>
                  </itunes:iTunesMetadata></metadata></head>
                  <body dur="183.227s"><div>
                    <p begin="1.125s" end="3s" itunes:key="L1">Synthetic original one</p>
                    <p begin="4.25s" end="6s" itunes:key="L2">Synthetic original two</p>
                  </div></body>
                </tt>
                """
        } } }
    });

    private Task WriteCatalogAsync(NativeCatalogTrack track) => File.WriteAllTextAsync(Path.Combine(_directory, track.CatalogId + ".json"),
        JsonSerializer.Serialize(new { data = new[] { new { id = track.CatalogId, type = "songs", attributes = new
        {
            name = track.Metadata.Title, artistName = track.Metadata.Artist, albumName = track.Metadata.Album,
            durationInMillis = track.Metadata.DurationSeconds * 1000, playParams = new { id = track.CatalogId }
        } } } }));

    private static string Envelope(params NativeCatalogTrack[] tracks) => JsonSerializer.Serialize(new
    {
        resultCount = tracks.Length,
        results = tracks.Select(track => new { wrapperType = "track", kind = "song", trackId = long.Parse(track.CatalogId),
            trackName = track.Metadata.Title, artistName = track.Metadata.Artist,
            collectionName = track.Metadata.Album, trackTimeMillis = track.Metadata.DurationSeconds * 1000 })
    });
    private static Task<HttpResponseMessage> Response(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private sealed class ResolverFixture : IDisposable
    {
        private readonly HttpClient _client;
        internal ResolverFixture(Func<string, CancellationToken, Task<HttpResponseMessage>> send, string? cacheDirectory = null)
        {
            var clock = new ManualClock();
            var coordinator = new AppleCatalogRequestCoordinator(clock, (delay, token) =>
            { token.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; });
            _client = new HttpClient(new Handler((request, token) =>
            {
                var country = request.RequestUri!.Query.Split('&').Single(part => part.StartsWith("country=", StringComparison.Ordinal))[8..];
                Countries.Add(country);
                return send(country, token);
            }));
            Resolver = new(_client, cacheDirectory, "US", coordinator, TimeSpan.FromSeconds(8), null, ["US", "KR"]);
        }
        internal static ResolverFixture KoreanOnly(NativeCatalogTrack track, string? cacheDirectory = null) =>
            new((country, _) => Response(country == "US" ? "{\"results\":[]}" : Envelope(track)), cacheDirectory);
        internal List<string> Countries { get; } = [];
        internal ApplePublicCatalogResolver Resolver { get; }
        public void Dispose() => _client.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(TimeSpan value) => _ticks += value.Ticks;
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
