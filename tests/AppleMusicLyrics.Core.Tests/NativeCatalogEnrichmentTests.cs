using System.Text.Json;

namespace AppleMusicLyrics.Core.Tests;

public sealed class NativeCatalogEnrichmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "apple-native-enrichment-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly TrackMetadata Track = new("Karitekita Neko (Do the Dance) [Japanese Ver.]", "ILLIT", "Toki Yo Tomare - EP", 188);
    private static readonly NativeCatalogTrack VerifiedTrack = new("1833184650", Track with { DurationSeconds = 188.640 });

    public NativeCatalogEnrichmentTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MissingMetadataCanBeEnrichedOnlyWithVerifiedMatchingCatalogIdentity()
    {
        await WriteLyricsAsync("1833184650");
        var before = Directory.GetFiles(_directory).ToDictionary(path => path, File.ReadAllBytes);
        var missing = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, missing.Status);
        Assert.Equal(new[] { "1833184650" }, missing.CandidateCatalogIds);
        Assert.Null(missing.Document);

        var enriched = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack]);
        Assert.Equal(NativeLyricsLookupStatus.Matched, enriched.Status);
        Assert.Equal("1833184650", enriched.CatalogId);
        Assert.Equal("Original", Assert.Single(enriched.Document!.Lines).Text);
        foreach (var entry in before) Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_directory).Order());
    }

    [Fact]
    public async Task CandidatesAreDistinctOrdinalAndExcludeAlreadyCompleteLocalMetadata()
    {
        await WriteLyricsAsync("20");
        await WriteLyricsAsync("10");
        await WriteLyricsAsync("20", filename: "syllable-lyrics[200].json");
        await WriteLyricsAsync("30");
        await WriteCatalogAsync(new("10", Track with { Title = "Other song" }));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(new[] { "20", "30" }, result.CandidateCatalogIds);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album")]
    [InlineData("duration")]
    [InlineData("version")]
    public async Task EnrichmentDoesNotRelaxAnyTrackIdentityField(string mismatch)
    {
        await WriteLyricsAsync("1833184650");
        var metadata = mismatch switch
        {
            "title" => Track with { Title = "Do the Dance" },
            "artist" => Track with { Artist = "Other artist" },
            "album" => Track with { Album = "Other album" },
            "duration" => Track with { DurationSeconds = 191.01 },
            "version" => Track with { Title = Track.Title + " (Live)" },
            _ => throw new ArgumentException(nameof(mismatch))
        };
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [new("1833184650", metadata)]);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task PartialEnrichmentCannotHideAnotherUnresolvedNativeIdentity()
    {
        await WriteLyricsAsync("1833184650");
        await WriteLyricsAsync("456");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack]);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Equal(new[] { "1833184650", "456" }, result.CandidateCatalogIds);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task FullyResolvedDifferentTrackDoesNotPreventUniqueStrictMatch()
    {
        await WriteLyricsAsync("1833184650");
        await WriteLyricsAsync("456");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog:
            [VerifiedTrack, new("456", Track with { Title = "Different recording" })]);
        Assert.Equal(NativeLyricsLookupStatus.Matched, result.Status);
        Assert.Equal("1833184650", result.CatalogId);
    }

    [Fact]
    public async Task IndistinguishableEnrichedIdentitiesRemainAmbiguous()
    {
        await WriteLyricsAsync("1833184650");
        await WriteLyricsAsync("456");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack, new("456", Track)]);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task ConflictingSameIdMetadataCannotOverrideOneAnother()
    {
        await WriteLyricsAsync("1833184650");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog:
            [VerifiedTrack, VerifiedTrack with { Metadata = Track with { Artist = "Different artist" } }]);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task ConflictingLocalMetadataCannotBeReplacedByEnrichment()
    {
        await WriteLyricsAsync("1833184650");
        await WriteCatalogAsync(VerifiedTrack with { Metadata = Track with { Title = "Different title" } });
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack]);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData("AP_1833184650")]
    [InlineData("../1833184650")]
    [InlineData("")]
    [InlineData("999")]
    public async Task AdditionalIdentityMustBeValidAndPresentInNativeResources(string id)
    {
        await WriteLyricsAsync("1833184650");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [new(id, Track)]);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task IncompleteAdditionalMetadataCannotEstablishIdentity()
    {
        await WriteLyricsAsync("1833184650");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog:
            [VerifiedTrack with { Metadata = Track with { Album = "" } }]);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task ResourceIdDisagreementStillRejectsEnrichedMatch()
    {
        await WriteLyricsAsync("1833184650", resourceId: "456");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack]);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task IncompleteCacheCannotBeRescuedByAdditionalMetadata()
    {
        await WriteLyricsAsync("1833184650");
        await File.WriteAllTextAsync(Path.Combine(_directory, "search.json"), "{\"resources\":");
        var result = await Provider().GetNativeLyricsAsync(Track, additionalCatalog: [VerifiedTrack]);
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, result.Status);
        Assert.Null(result.Document);
        Assert.Null(result.CandidateCatalogIds);
    }

    [Fact]
    public async Task CandidateLimitNeverReturnsPartialIdentityDiscovery()
    {
        for (var id = 1; id <= 129; id++) await WriteLyricsAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, result.Status);
        Assert.Null(result.CandidateCatalogIds);
        Assert.Null(result.Document);
    }

    private AppleNativeLyricsProvider Provider() => new([_directory]);

    private Task WriteLyricsAsync(string id, string? filename = null, string? resourceId = null) =>
        File.WriteAllTextAsync(Path.Combine(_directory, filename ?? "syllable-lyrics[" + id + "].json"),
            JsonSerializer.Serialize(new { data = new[] { new
            {
                id = resourceId ?? id, type = "syllable-lyrics", attributes = new
                {
                    playParams = new { catalogId = id, id = "AP_" + id },
                    ttmlLocalizations = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><body dur=\"188s\"><div><p begin=\"1s\" end=\"2s\" itunes:key=\"L1\">Original</p></div></body></tt>"
                }
            } } }));

    private Task WriteCatalogAsync(NativeCatalogTrack track) =>
        File.WriteAllTextAsync(Path.Combine(_directory, track.CatalogId + ".json"), JsonSerializer.Serialize(new
        {
            data = new[] { new
            {
                id = track.CatalogId, type = "songs", attributes = new
                {
                    name = track.Metadata.Title, artistName = track.Metadata.Artist, albumName = track.Metadata.Album,
                    durationInMillis = track.Metadata.DurationSeconds * 1000,
                    playParams = new { id = track.CatalogId }
                }
            } }
        }));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
