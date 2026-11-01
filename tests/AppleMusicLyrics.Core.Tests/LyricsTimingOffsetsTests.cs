using AppleMusicLyrics.Core.Playback;

namespace AppleMusicLyrics.Core.Tests;

public sealed class LyricsTimingOffsetsTests
{
    [Theory]
    [InlineData(1750)]
    [InlineData(2000)]
    public void GlobalCorrectionSurvivesSongAToBToAWithoutCreatingTrackEntries(int global)
    {
        var perTrack = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.Equal(global, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-a"));
        Assert.Equal(global, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-b"));
        Assert.Equal(global, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-a"));
        Assert.Empty(perTrack);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-song")]
    public void MissingTrackUsesGlobalCorrection(string? track)
    {
        var perTrack = new Dictionary<string, int> { ["known-song"] = -250 };
        Assert.Equal(1750, LyricsTimingOffsets.GetEffectiveMilliseconds(1750, perTrack, track));
        Assert.Equal(2000, LyricsTimingOffsets.GetEffectiveMilliseconds(2000, null, track));
    }

    [Fact]
    public void TrackCorrectionIsAdditiveAndResetKeepsAllSongCorrection()
    {
        const int global = 1750;
        var perTrack = new Dictionary<string, int> { ["song-a"] = -250, ["song-b"] = 500 };

        Assert.Equal(1500, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-a"));
        Assert.Equal(2250, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-b"));
        perTrack.Remove("song-a");
        Assert.Equal(1750, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-a"));
        Assert.Equal(2250, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song-b"));
        Assert.Equal(500, perTrack["song-b"]);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, 120000)]
    [InlineData(int.MinValue, int.MinValue, -120000)]
    [InlineData(int.MaxValue, int.MinValue, 0)]
    public void CorruptOrExtremeComponentsAreLimitedBeforeAdding(int global, int track, int expected)
    {
        var perTrack = new Dictionary<string, int> { ["song"] = track };
        Assert.Equal(expected, LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "song"));
        Assert.Equal(track, perTrack["song"]); // Lookup does not silently rewrite persisted data.
    }

    [Theory]
    [InlineData(1750)]
    [InlineData(2000)]
    public void PositiveGlobalCorrectionAdvancesLrcAtTheExactBoundary(int global)
    {
        var lines = LrcParser.Parse("[00:10.000]first fixture line\n[00:20.000]second fixture line");
        var correction = TimeSpan.FromMilliseconds(LyricsTimingOffsets.GetEffectiveMilliseconds(global, null, "song"));
        var boundary = TimeSpan.FromSeconds(20) - correction;

        Assert.Equal("first fixture line", LyricSelection.Select(lines, boundary).Active?.Text);
        Assert.Equal("first fixture line", LyricSelection.Select(lines, boundary - TimeSpan.FromTicks(1), correction).Active?.Text);
        Assert.Equal("second fixture line", LyricSelection.Select(lines, boundary, correction).Active?.Text);
        Assert.Equal(TimeSpan.FromSeconds(20), lines[1].Time); // Timing metadata stays intact.
    }

    [Theory]
    [InlineData(1750, false, false)]
    [InlineData(1750, true, false)]
    [InlineData(1750, false, true)]
    [InlineData(1750, true, true)]
    [InlineData(2000, true, true)]
    public void GlobalCorrectionAdvancesNativeOriginalAndAlignedSupplementalTextTogether(
        int global, bool translation, bool pronunciation)
    {
        var document = Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload()));
        var correction = TimeSpan.FromMilliseconds(LyricsTimingOffsets.GetEffectiveMilliseconds(global, null, "song"));
        var boundary = document.Lines[1].Begin - correction;

        var before = LyricPresentation.Select(document, boundary - TimeSpan.FromTicks(1), translation,
            pronunciation, offset: correction);
        var after = LyricPresentation.Select(document, boundary, translation, pronunciation, offset: correction);

        Assert.Equal(0, before.ActiveIndex);
        Assert.Equal(1, after.ActiveIndex);
        Assert.Equal("次のテスト", after.Lines[0].Text);
        if (pronunciation)
            Assert.Equal("da eum", Assert.Single(after.Lines, line => line.Kind == PresentedLyricKind.Pronunciation).Text);
        if (translation)
            Assert.Equal("Second synthetic line", Assert.Single(after.Lines, line => line.Kind == PresentedLyricKind.Translation).Text);
        Assert.Equal(TimeSpan.FromSeconds(20.5), document.Lines[1].Begin);
    }

    [Fact]
    public void NegativeGlobalCorrectionDelaysLyricsAndTrackCorrectionCanCounteractIt()
    {
        var lines = LrcParser.Parse("[00:10.000]first fixture line\n[00:20.000]second fixture line");
        var perTrack = new Dictionary<string, int> { ["song"] = 2000 };
        var delayed = TimeSpan.FromMilliseconds(LyricsTimingOffsets.GetEffectiveMilliseconds(-2000, null, "song"));
        var corrected = TimeSpan.FromMilliseconds(LyricsTimingOffsets.GetEffectiveMilliseconds(-2000, perTrack, "song"));

        Assert.Equal("first fixture line", LyricSelection.Select(lines, TimeSpan.FromSeconds(20), delayed).Active?.Text);
        Assert.Equal("second fixture line", LyricSelection.Select(lines, TimeSpan.FromSeconds(20), corrected).Active?.Text);
    }

    [Theory]
    [InlineData(1750)]
    [InlineData(2000)]
    public void CorrectionDoesNotChangePlaybackClockAndRemainsAfterPauseResumeAndSeeks(int global)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        var lines = LrcParser.Parse("[00:10.000]first fixture line\n[00:20.000]second fixture line\n[01:00.000]third fixture line");
        var correction = TimeSpan.FromMilliseconds(LyricsTimingOffsets.GetEffectiveMilliseconds(global, null, "song"));
        var reported = TimeSpan.FromSeconds(20) - correction;
        playback.Update("song", PlaybackStatus.Paused, TimeSpan.FromSeconds(200), reported);
        Assert.Equal("second fixture line", Selected());
        Assert.Equal("first fixture line", LyricSelection.Select(lines, playback.CurrentPosition).Active?.Text);
        time.NowSeconds += 40;
        Assert.Equal(reported, playback.CurrentPosition);
        Assert.Equal("second fixture line", Selected());

        playback.Update("song", PlaybackStatus.Playing, TimeSpan.FromSeconds(200), reported);
        time.NowSeconds += 3;
        playback.Update("song", PlaybackStatus.Playing, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(60), playback.CurrentPosition);
        Assert.Equal("third fixture line", Selected());
        playback.Update("song", PlaybackStatus.Playing, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), playback.CurrentPosition);
        Assert.Equal("first fixture line", Selected());

        string? Selected() => LyricSelection.Select(lines, playback.CurrentPosition, correction).Active?.Text;
    }

    [Fact]
    public void PromotingCurrentTrackRebasesOtherSongsWithoutDoubleApplyingLegacyOffsets()
    {
        var perTrack = new Dictionary<string, int> { ["song-a"] = 1750, ["song-b"] = 2000, ["song-c"] = 1750 };
        var promoted = LyricsTimingOffsets.PromoteCurrentTrack(0, perTrack, "song-a");

        Assert.Equal(1750, promoted.GlobalMilliseconds);
        var remaining = Assert.Single(promoted.TrackOffsets);
        Assert.Equal("song-b", remaining.Key);
        Assert.Equal(250, remaining.Value);
        Assert.Equal(1750, Effective("song-a"));
        Assert.Equal(2000, Effective("song-b"));
        Assert.Equal(1750, Effective("song-c"));
        Assert.Equal(1750, Effective("new-song"));
        Assert.Equal(3, perTrack.Count);
        Assert.Equal(1750, perTrack["song-a"]);
        Assert.Equal(2000, perTrack["song-b"]);

        int Effective(string track) => LyricsTimingOffsets.GetEffectiveMilliseconds(
            promoted.GlobalMilliseconds, promoted.TrackOffsets, track);
    }

    [Theory]
    [InlineData(250, 1500, 1750)]
    [InlineData(-250, -1500, -1750)]
    [InlineData(2000, -2000, 0)]
    public void PromotionIncludesExistingGlobalAndSupportsNegativeOffsets(int global, int currentTrack, int expected)
    {
        var perTrack = new Dictionary<string, int> { ["current"] = currentTrack, ["other"] = 500 };
        var oldOtherTotal = LyricsTimingOffsets.GetEffectiveMilliseconds(global, perTrack, "other");
        var promoted = LyricsTimingOffsets.PromoteCurrentTrack(global, perTrack, "current");

        Assert.Equal(expected, promoted.GlobalMilliseconds);
        Assert.False(promoted.TrackOffsets.ContainsKey("current"));
        Assert.Equal(expected, LyricsTimingOffsets.GetEffectiveMilliseconds(
            promoted.GlobalMilliseconds, promoted.TrackOffsets, "current"));
        Assert.Equal(oldOtherTotal, LyricsTimingOffsets.GetEffectiveMilliseconds(
            promoted.GlobalMilliseconds, promoted.TrackOffsets, "other"));
        Assert.Equal(currentTrack, perTrack["current"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void PromotionRejectsMissingTrackIdentity(string? trackKey)
    {
        Assert.ThrowsAny<ArgumentException>(() => LyricsTimingOffsets.PromoteCurrentTrack(1750, null, trackKey!));
    }

    [Fact]
    public void PromotingUnknownTrackKeepsGlobalAndExistingTotals()
    {
        var perTrack = new Dictionary<string, int> { ["known"] = -250, ["zero"] = 0 };
        var promoted = LyricsTimingOffsets.PromoteCurrentTrack(1750, perTrack, "unknown");
        Assert.Equal(1750, promoted.GlobalMilliseconds);
        Assert.Equal(-250, Assert.Single(promoted.TrackOffsets).Value);
        Assert.Equal(1500, LyricsTimingOffsets.GetEffectiveMilliseconds(
            promoted.GlobalMilliseconds, promoted.TrackOffsets, "known"));
        Assert.Empty(LyricsTimingOffsets.PromoteCurrentTrack(1750, null, "unknown").TrackOffsets);
    }

    [Theory]
    [InlineData(60000, 60000, -60000, 60000, 60000, -60000)]
    [InlineData(-60000, -60000, 60000, -60000, -60000, 60000)]
    [InlineData(60000, -60000, 60000, 0, 0, 60000)]
    [InlineData(-60000, 60000, -60000, 0, 0, -60000)]
    public void PromotionClampsComponentsAndOnlySaturatesUnrepresentableRebasedTotals(
        int global, int currentTrack, int otherTrack, int expectedGlobal, int expectedCurrent, int expectedOther)
    {
        var perTrack = new Dictionary<string, int> { ["current"] = currentTrack, ["other"] = otherTrack };
        var promoted = LyricsTimingOffsets.PromoteCurrentTrack(global, perTrack, "current");

        Assert.Equal(expectedGlobal, promoted.GlobalMilliseconds);
        Assert.Equal(expectedCurrent, promoted.TrackOffsets.GetValueOrDefault("current"));
        Assert.Equal(expectedOther, promoted.TrackOffsets.GetValueOrDefault("other"));
        Assert.All(promoted.TrackOffsets.Values, value => Assert.InRange(value, -60000, 60000));
        Assert.DoesNotContain(0, promoted.TrackOffsets.Values);
        Assert.Equal(currentTrack, perTrack["current"]);
        Assert.Equal(otherTrack, perTrack["other"]);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public double NowSeconds { get; set; }
    }
}
