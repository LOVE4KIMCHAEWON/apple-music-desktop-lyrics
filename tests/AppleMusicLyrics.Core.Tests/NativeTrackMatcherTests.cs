namespace AppleMusicLyrics.Core.Tests;

public sealed class NativeTrackMatcherTests
{
    private static readonly TrackMetadata Track = new("Song", "Artist", "Album", 180);

    [Fact]
    public void RequiresOneCatalogIdentityAndAllFourMetadataFields()
    {
        var result = NativeTrackMatcher.Match(Track, [new("123", Track)]);
        Assert.Equal(NativeTrackMatchStatus.Matched, result.Status);
        Assert.Equal("123", result.CatalogId);
        Assert.Equal(NativeTrackMatchStatus.NoMatch,
            NativeTrackMatcher.Match(Track, [new("123", Track with { Album = "Other" })]).Status);
    }

    [Fact]
    public void NormalizesTypographyAndObservedSmtcArtistAlbumRepresentation()
    {
        var result = NativeTrackMatcher.Match(new("ＳＯＮＧ", "Artist — Album", "", 180.8), [new("123", Track)]);
        Assert.Equal("123", result.CatalogId);
    }

    [Theory]
    [InlineData("Song (Live)", "Album")]
    [InlineData("Song", "Album (Remastered)")]
    [InlineData("Song - Remix", "Album")]
    [InlineData("Song (Cover)", "Album")]
    public void RejectsAlternateVersions(string title, string album) =>
        Assert.Equal(NativeTrackMatchStatus.NoMatch,
            NativeTrackMatcher.Match(Track, [new("123", Track with { Title = title, Album = album })]).Status);

    [Theory]
    [InlineData(176.99)]
    [InlineData(183.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsDurationDisagreementOrInvalidDuration(double duration) =>
        Assert.Equal(NativeTrackMatchStatus.NoMatch,
            NativeTrackMatcher.Match(Track, [new("123", Track with { DurationSeconds = duration })]).Status);

    [Fact]
    public void AcceptsObservedAppleSmtcDurationDifferenceForSameRecording()
    {
        var smtc = new TrackMetadata("Hurt", "NewJeans", "NewJeans 1st EP 'New Jeans'", 176);
        var catalog = smtc with { DurationSeconds = 177.947 };
        var result = NativeTrackMatcher.Match(smtc, [new("1635470002", catalog)]);
        Assert.Equal(NativeTrackMatchStatus.Matched, result.Status);
        Assert.Equal("1635470002", result.CatalogId);
    }

    [Theory]
    [InlineData(176, 179.01)]
    [InlineData(179.01, 176)]
    public void RejectsDurationDifferenceBeyondThreeSeconds(double smtcDuration, double catalogDuration)
    {
        var result = NativeTrackMatcher.Match(Track with { DurationSeconds = smtcDuration },
            [new("123", Track with { DurationSeconds = catalogDuration })]);
        Assert.Equal(NativeTrackMatchStatus.NoMatch, result.Status);
    }

    [Fact]
    public void MissingRequestedAlbumOrDurationDoesNotAcquireIdentity()
    {
        Assert.Equal(NativeTrackMatchStatus.InvalidTrack,
            NativeTrackMatcher.Match(Track with { Album = "" }, [new("123", Track)]).Status);
        Assert.Equal(NativeTrackMatchStatus.InvalidTrack,
            NativeTrackMatcher.Match(Track with { DurationSeconds = 0 }, [new("123", Track)]).Status);
    }

    [Fact]
    public void IndistinguishableCatalogEditionsAreAmbiguous()
    {
        var result = NativeTrackMatcher.Match(Track, [new("123", Track), new("456", Track)]);
        Assert.Equal(NativeTrackMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.CatalogId);
    }

    [Fact]
    public void ConflictingMetadataForSameIdIsAmbiguous()
    {
        var result = NativeTrackMatcher.Match(Track,
            [new("123", Track), new("123", Track with { Artist = "Different" })]);
        Assert.Equal(NativeTrackMatchStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void RepeatedConsistentCatalogMetadataIsSafe()
    {
        Assert.Equal("123", NativeTrackMatcher.Match(Track, [new("123", Track), new("123", Track)]).CatalogId);
    }

    [Theory]
    [InlineData("../123")]
    [InlineData("AP_123")]
    [InlineData("")]
    public void RejectsNonCatalogIds(string id) =>
        Assert.Equal(NativeTrackMatchStatus.NoMatch, NativeTrackMatcher.Match(Track, [new(id, Track)]).Status);
}
