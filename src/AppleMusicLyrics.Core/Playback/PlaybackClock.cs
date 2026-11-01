using System.Diagnostics;

namespace AppleMusicLyrics.Core.Playback;

public enum PlaybackStatus
{
    Unknown,
    Stopped,
    Playing,
    Paused
}

public interface IMonotonicClock
{
    double NowSeconds { get; }
}

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

public sealed record PlaybackState(
    string? TrackKey,
    PlaybackStatus Status,
    TimeSpan? Duration,
    TimeSpan? ReportedPosition,
    double LastRefreshTimestamp,
    TimeSpan AnchorPosition,
    double PlaybackRate,
    bool HasTimeline);

public enum PlaybackUpdateReason
{
    None,
    TrackChanged,
    Reconnected,
    Started,
    Paused,
    Resumed,
    SeekForward,
    SeekBackward,
    TimelineUpdated,
    PlaybackStateChanged
}

public sealed record PlaybackUpdate(PlaybackUpdateReason Reason, TimeSpan Position, bool IsSeek);

/// <summary>
/// Interpolates observed media positions using a monotonic clock. It never controls playback.
/// Calls and reads should be serialized by the owner (for example, on the UI dispatcher).
/// </summary>
public sealed class PlaybackClock
{
    private readonly IMonotonicClock _clock;
    private double? _lastTimelineReportTimestamp;
    private double _seekDetectionAllowedAfter;

    public PlaybackClock(IMonotonicClock? clock = null)
    {
        _clock = clock ?? new StopwatchMonotonicClock();
        State = new(null, PlaybackStatus.Unknown, null, null, _clock.NowSeconds,
            TimeSpan.Zero, 1, false);
    }

    public PlaybackState State { get; private set; }
    public TimeSpan SeekThreshold { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan TransitionSettlingTime { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan CurrentPosition => PositionAt(_clock.NowSeconds);

    /// <param name="reportAge">
    /// Age of the Windows timeline's LastUpdatedTime at observation. Supplying it also
    /// identifies unchanged timeline reports. Elapsed paused time is never added on resume.
    /// Omit it only when reportedPosition is already fresh at observation time.
    /// </param>
    /// <param name="observationTimestamp">
    /// Monotonic capture time in the same clock domain as this instance. Passing it prevents
    /// dispatcher delays from becoming lyric delays. Defaults to the time of this call.
    /// </param>
    public PlaybackUpdate Update(string? trackKey, PlaybackStatus status, TimeSpan? duration,
        TimeSpan? reportedPosition, double? rate = 1, bool reconnected = false,
        TimeSpan? reportAge = null, double? observationTimestamp = null)
    {
        var now = _clock.NowSeconds;
        var observedAt = observationTimestamp is { } timestamp && double.IsFinite(timestamp)
            ? Math.Min(timestamp, now) : now;
        // Older queued observations must not restore an obsolete track, status, or position.
        if (observedAt < State.LastRefreshTimestamp)
            return new(PlaybackUpdateReason.None, PositionAt(now), false);
        var previous = State;
        var trackChanged = !StringComparer.Ordinal.Equals(previous.TrackKey, trackKey);
        var expected = trackChanged ? TimeSpan.Zero : PositionAt(observedAt);
        var effectiveDuration = duration is { Ticks: > 0 } ? duration :
            trackChanged ? null : previous.Duration;
        var effectiveRate = rate is > 0 && double.IsFinite(rate.Value) ? rate.Value : 1;

        if (trackChanged || reconnected)
            _lastTimelineReportTimestamp = null;
        if (trackChanged || reconnected || previous.Status != status)
            _seekDetectionAllowedAfter = observedAt + Math.Max(0, TransitionSettlingTime.TotalSeconds);

        var hasTimeline = reportedPosition.HasValue && !trackChanged && !reconnected && previous.HasTimeline;
        var position = expected;
        var freshReport = false;
        var ageSeconds = Math.Max(0, reportAge?.TotalSeconds ?? 0);
        if (reportedPosition.HasValue)
        {
            var reportTimestamp = observedAt - ageSeconds;
            // LastUpdatedTime can remain unchanged through status and metadata events.
            // A changed position is still useful if the provider has coarse timestamps.
            freshReport = !reportAge.HasValue || !previous.HasTimeline || !_lastTimelineReportTimestamp.HasValue ||
                reportTimestamp > _lastTimelineReportTimestamp.Value + 0.05 ||
                previous.ReportedPosition != reportedPosition;
            if (freshReport)
            {
                position = reportedPosition.Value;
                var stablePlayback = status == PlaybackStatus.Playing &&
                    (previous.Status == PlaybackStatus.Playing || trackChanged || reconnected);
                if (stablePlayback && ageSeconds > 0)
                    position = AddSecondsSafely(position, ageSeconds * effectiveRate);
                _lastTimelineReportTimestamp = reportTimestamp;
                hasTimeline = true;
            }
        }
        else if (trackChanged || reconnected)
        {
            position = TimeSpan.Zero;
        }

        if (status == PlaybackStatus.Stopped && !reportedPosition.HasValue)
            position = TimeSpan.Zero;

        position = Clamp(position, effectiveDuration);
        var difference = position - Clamp(expected, effectiveDuration);
        var isSeek = !trackChanged && !reconnected && previous.Status == status &&
            observedAt >= _seekDetectionAllowedAfter && previous.HasTimeline && freshReport &&
            Math.Abs(difference.TotalSeconds) >= Math.Max(0.05, SeekThreshold.TotalSeconds);
        var reason = trackChanged ? PlaybackUpdateReason.TrackChanged :
            reconnected ? PlaybackUpdateReason.Reconnected :
            isSeek ? difference > TimeSpan.Zero ? PlaybackUpdateReason.SeekForward : PlaybackUpdateReason.SeekBackward :
            previous.Status == PlaybackStatus.Playing && status == PlaybackStatus.Paused ? PlaybackUpdateReason.Paused :
            previous.Status == PlaybackStatus.Paused && status == PlaybackStatus.Playing ? PlaybackUpdateReason.Resumed :
            previous.Status != PlaybackStatus.Playing && status == PlaybackStatus.Playing ? PlaybackUpdateReason.Started :
            previous.Status != status ? PlaybackUpdateReason.PlaybackStateChanged :
            freshReport ? PlaybackUpdateReason.TimelineUpdated : PlaybackUpdateReason.None;

        State = new(trackKey, status, effectiveDuration,
            reportedPosition ?? (trackChanged || reconnected ? null : previous.ReportedPosition),
            observedAt, position, effectiveRate, hasTimeline);
        return new(reason, PositionAt(now), isSeek);
    }

    private TimeSpan PositionAt(double now)
    {
        var position = State.AnchorPosition;
        if (State.Status == PlaybackStatus.Playing && State.HasTimeline)
            position = AddSecondsSafely(position,
                Math.Max(0, now - State.LastRefreshTimestamp) * State.PlaybackRate);
        return Clamp(position, State.Duration);
    }

    private static TimeSpan AddSecondsSafely(TimeSpan position, double seconds)
    {
        var ticks = position.Ticks + seconds * TimeSpan.TicksPerSecond;
        if (ticks >= long.MaxValue) return TimeSpan.MaxValue;
        if (ticks <= long.MinValue) return TimeSpan.MinValue;
        return TimeSpan.FromTicks((long)ticks);
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan? duration)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return duration is { Ticks: > 0 } && position > duration.Value ? duration.Value : position;
    }
}
