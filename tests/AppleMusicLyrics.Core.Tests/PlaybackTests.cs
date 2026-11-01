using AppleMusicLyrics.Core.Playback;

namespace AppleMusicLyrics.Core.Tests;

public sealed class PlaybackTests
{
    [Fact]
    public void InterpolatesUsingMonotonicClockAndPlaybackRate()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), rate: 1.5);
        time.Advance(4);
        Assert.Equal(Seconds(16), playback.CurrentPosition);
    }

    [Fact]
    public void PauseFreezesAndResumeDoesNotIncludePausedInterval()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10));
        time.Advance(5);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(15));
        time.Advance(40);
        Assert.Equal(Seconds(15), playback.CurrentPosition);
        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(15));
        Assert.Equal(PlaybackUpdateReason.Resumed, update.Reason);
        time.Advance(2);
        Assert.Equal(Seconds(17), playback.CurrentPosition);
    }

    [Theory]
    [InlineData(80, PlaybackUpdateReason.SeekForward)]
    [InlineData(5, PlaybackUpdateReason.SeekBackward)]
    public void DetectsObservedForwardAndBackwardSeeks(double reported, PlaybackUpdateReason reason)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(30));
        time.Advance(5);
        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(reported));
        Assert.True(update.IsSeek);
        Assert.Equal(reason, update.Reason);
        Assert.Equal(Seconds(reported), playback.CurrentPosition);
        time.Advance(1);
        Assert.Equal(Seconds(reported + 1), playback.CurrentPosition);
    }

    [Fact]
    public void DetectsSeekWhilePausedAndKeepsPositionFrozen()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(30));
        time.Advance(5);
        var update = playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(90));
        Assert.True(update.IsSeek);
        time.Advance(40);
        Assert.Equal(Seconds(90), playback.CurrentPosition);
    }

    [Fact]
    public void SmallTimelineCorrectionRecalibratesWithoutDeclaringSeek()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10));
        time.Advance(5);
        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(15.25));
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(15.25), playback.CurrentPosition);
    }

    [Fact]
    public void ReconnectRecalibratesWithoutMistakingNewPositionForSeek()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10));
        time.Advance(20);
        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(90), reconnected: true);
        Assert.False(update.IsSeek);
        Assert.Equal(PlaybackUpdateReason.Reconnected, update.Reason);
        Assert.Equal(Seconds(90), playback.CurrentPosition);
    }

    [Fact]
    public void NewTrackResetsPositionAndUnknownDuration()
    {
        var playback = new PlaybackClock(new FakeClock());
        playback.Update("old", PlaybackStatus.Playing, Seconds(200), Seconds(100));
        var update = playback.Update("new", PlaybackStatus.Playing, null, Seconds(0));
        Assert.Equal(PlaybackUpdateReason.TrackChanged, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Null(playback.State.Duration);
        Assert.Equal(Seconds(0), playback.CurrentPosition);
    }

    [Fact]
    public void MissingTimelineStopsInterpolationUntilValidReportReturns()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(5);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), null);
        Assert.False(playback.State.HasTimeline);
        time.Advance(20);
        Assert.Equal(Seconds(15), playback.CurrentPosition);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(25));
        Assert.True(playback.State.HasTimeline);
        Assert.Equal(Seconds(35), playback.CurrentPosition);
    }

    [Fact]
    public void InitialMissingTimelineDoesNotInventProgress()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), null);
        time.Advance(100);
        Assert.False(playback.State.HasTimeline);
        Assert.Equal(TimeSpan.Zero, playback.CurrentPosition);
    }

    [Fact]
    public void PositionsAreClampedToStartAndDuration()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(20), Seconds(-5));
        Assert.Equal(TimeSpan.Zero, playback.CurrentPosition);
        time.Advance(100);
        Assert.Equal(Seconds(20), playback.CurrentPosition);
        playback.Update("track", PlaybackStatus.Paused, Seconds(20), Seconds(90));
        Assert.Equal(Seconds(20), playback.CurrentPosition);
    }

    [Fact]
    public void RepeatedOldTimelineDoesNotRewindOrAddPausedTimeOnResume()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(2));
        Assert.Equal(Seconds(12), playback.CurrentPosition);
        time.Advance(5);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(7));
        Assert.Equal(Seconds(17), playback.CurrentPosition);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(10), reportAge: Seconds(7));
        time.Advance(40);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(10), reportAge: Seconds(47));
        Assert.Equal(Seconds(17), playback.CurrentPosition);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(47));
        Assert.Equal(Seconds(17), playback.CurrentPosition);
        time.Advance(2);
        Assert.Equal(Seconds(19), playback.CurrentPosition);
    }

    [Fact]
    public void PausedTimelineCanSettleAfterPlaybackStatusEvent()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(0), reportAge: Seconds(0));
        time.Advance(6);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(6), reportAge: Seconds(0));
        time.Advance(.013);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(7), reportAge: Seconds(0));
        time.Advance(40);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(7), reportAge: Seconds(40));
        Assert.Equal(Seconds(7), playback.CurrentPosition);
    }

    [Fact]
    public void DelayedTimelineAfterPauseOrResumeRecalibratesWithoutFalseSeek()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(5);
        var pause = playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(10), reportAge: Seconds(5));
        Assert.False(pause.IsSeek);
        Assert.Equal(Seconds(15), playback.CurrentPosition);
        time.Advance(.1);
        var settled = playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(20), reportAge: Seconds(0));
        Assert.False(settled.IsSeek);
        Assert.Equal(Seconds(20), playback.CurrentPosition);
        time.Advance(30);
        var resume = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(20), reportAge: Seconds(30));
        Assert.False(resume.IsSeek);
        time.Advance(.1);
        var fresh = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(25), reportAge: Seconds(0));
        Assert.False(fresh.IsSeek);
        Assert.Equal(Seconds(25), playback.CurrentPosition);
        time.Advance(3);
        var seek = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(80), reportAge: Seconds(0));
        Assert.True(seek.IsSeek);
        Assert.Equal(PlaybackUpdateReason.SeekForward, seek.Reason);
    }

    [Fact]
    public void TimelineTimestampJitterWithinFiftyMillisecondsIsNotANewReport()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(5);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(10), reportAge: Seconds(4.98));
        Assert.Equal(Seconds(15), playback.CurrentPosition);
    }

    [Theory]
    [InlineData(1, 12.5)]
    [InlineData(1.5, 13.75)]
    public void DelayedPlayingObservationIncludesProviderAgeAndDispatchDelay(double rate, double expected)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        time.Advance(2);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10),
            rate: rate, reportAge: Seconds(.5), observationTimestamp: 0);

        Assert.Equal(Seconds(expected), playback.CurrentPosition);
        Assert.Equal(playback.CurrentPosition, update.Position);
        Assert.Equal(0, playback.State.LastRefreshTimestamp);
    }

    [Fact]
    public void DelayedPauseWithUnchangedTimelineFreezesAtCaptureTime()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(8);

        var update = playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(10),
            reportAge: Seconds(5), observationTimestamp: 5);

        Assert.Equal(PlaybackUpdateReason.Paused, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(15), update.Position);
        Assert.Equal(5, playback.State.LastRefreshTimestamp);
        time.Advance(40);
        Assert.Equal(Seconds(15), playback.CurrentPosition);
    }

    [Fact]
    public void DelayedResumeAddsDispatchDelayWithoutAddingPausedInterval()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Paused, Seconds(200), Seconds(15), reportAge: Seconds(0));
        time.Advance(43);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(15),
            reportAge: Seconds(40), observationTimestamp: 40);

        Assert.Equal(PlaybackUpdateReason.Resumed, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(18), update.Position);
        time.Advance(2);
        Assert.Equal(Seconds(20), playback.CurrentPosition);
    }

    [Fact]
    public void QueuedUnchangedTimelineRemainsUnchangedDespiteDispatchDelay()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(2));
        time.Advance(8);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10),
            reportAge: Seconds(7), observationTimestamp: 5);

        Assert.Equal(PlaybackUpdateReason.None, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(20), update.Position);
        Assert.Equal(Seconds(20), playback.CurrentPosition);
    }

    [Fact]
    public void DispatchDelayDoesNotTurnAccurateTimelineIntoBackwardSeek()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(10);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(15),
            reportAge: Seconds(0), observationTimestamp: 5);

        Assert.Equal(PlaybackUpdateReason.TimelineUpdated, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(20), update.Position);
    }

    [Theory]
    [InlineData(80, PlaybackUpdateReason.SeekForward)]
    [InlineData(5, PlaybackUpdateReason.SeekBackward)]
    public void DelayedSeekRecalibratesAtCaptureAndAdvancesThroughDispatchDelay(
        double reported, PlaybackUpdateReason reason)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(30), reportAge: Seconds(0));
        time.Advance(10);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(reported),
            reportAge: Seconds(0), observationTimestamp: 5);

        Assert.True(update.IsSeek);
        Assert.Equal(reason, update.Reason);
        Assert.Equal(Seconds(reported + 5), update.Position);
        Assert.Equal(update.Position, playback.CurrentPosition);
    }

    [Fact]
    public void TransitionSettlingUsesCaptureTimeRatherThanDispatchTime()
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        time.Advance(10);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10),
            reportAge: Seconds(0), observationTimestamp: 0);
        time.Advance(2);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(60),
            reportAge: Seconds(0), observationTimestamp: 3);

        Assert.True(update.IsSeek);
        Assert.Equal(PlaybackUpdateReason.SeekForward, update.Reason);
        Assert.Equal(Seconds(69), update.Position);
    }

    [Theory]
    [InlineData("track", PlaybackStatus.Paused)]
    [InlineData("obsolete-track", PlaybackStatus.Playing)]
    public void OutOfOrderObservationCannotRestoreAnOlderStatusOrTrack(string trackKey, PlaybackStatus status)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10), reportAge: Seconds(0));
        time.Advance(10);
        playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(18),
            reportAge: Seconds(0), observationTimestamp: 8);
        var previousState = playback.State;
        time.Advance(1);

        var update = playback.Update(trackKey, status, Seconds(100), Seconds(99),
            reportAge: Seconds(0), observationTimestamp: 5);

        Assert.Same(previousState, playback.State);
        Assert.Equal(PlaybackUpdateReason.None, update.Reason);
        Assert.False(update.IsSeek);
        Assert.Equal(Seconds(21), update.Position);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1000)]
    public void InvalidOrFutureCaptureTimeUsesProcessingTime(double timestamp)
    {
        var time = new FakeClock();
        var playback = new PlaybackClock(time);
        time.Advance(2);

        var update = playback.Update("track", PlaybackStatus.Playing, Seconds(200), Seconds(10),
            reportAge: Seconds(0), observationTimestamp: timestamp);

        Assert.Equal(2, playback.State.LastRefreshTimestamp);
        Assert.Equal(Seconds(10), update.Position);
        time.Advance(1);
        Assert.Equal(Seconds(11), playback.CurrentPosition);
    }

    [Fact]
    public void BinarySearchSelectsActiveAndUpcomingLinesAndAppliesOffset()
    {
        LyricLine[] lines = [new(Seconds(10), "一"), new(Seconds(20), "二"), new(Seconds(30), "三")];
        var before = LyricSelection.Select(lines, Seconds(5));
        Assert.Null(before.Active);
        Assert.Equal("一", before.Upcoming?.Text);
        var current = LyricSelection.Select(lines, Seconds(19), Seconds(1));
        Assert.Equal("二", current.Active?.Text);
        Assert.Equal("三", current.Upcoming?.Text);
        Assert.Equal(1, current.ActiveIndex);
        var after = LyricSelection.Select(lines, Seconds(100));
        Assert.Equal("三", after.Active?.Text);
        Assert.Null(after.Upcoming);
        Assert.Null(LyricSelection.Select([], Seconds(10)).Active);
    }

    [Fact]
    public void PerTrackOffsetsAreIndependentAndPersistWithGlobalOffset()
    {
        var path = Path.Combine(Path.GetTempPath(), "aml-offset-test-" + Guid.NewGuid().ToString("N"), "offsets.json");
        try
        {
            var store = new OffsetStore(path) { GlobalOffset = Seconds(.25) };
            store.SetTrackOffset("first", Seconds(-.75));
            store.SetTrackOffset("second", Seconds(2));
            store.Save();
            var loaded = new OffsetStore(path);
            Assert.Equal(Seconds(-.5), loaded.GetEffectiveOffset("first"));
            Assert.Equal(Seconds(2.25), loaded.GetEffectiveOffset("second"));
            Assert.Equal(Seconds(.25), loaded.GetEffectiveOffset("unknown"));
            loaded.SetTrackOffset("first", TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, loaded.GetTrackOffset("first"));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void MalformedOffsetFileDoesNotPreventStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), "aml-offset-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "invalid JSON");
            var store = new OffsetStore(path);
            Assert.Equal(TimeSpan.Zero, store.GlobalOffset);
            Assert.Equal(TimeSpan.Zero, store.GetTrackOffset("track"));
        }
        finally { File.Delete(path); }
    }

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private sealed class FakeClock : IMonotonicClock
    {
        public double NowSeconds { get; private set; }
        public void Advance(double seconds) => NowSeconds += seconds;
    }
}
