using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Windows.Media.Control;

namespace AppleMusicLyrics.Probe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = ProbeOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("""
                    Apple Music Desktop Lyrics — Phase 1 media-session probe
                    --seconds N        Capture duration (default 30; 0 = until Ctrl+C).
                    --sample-ms N      Diagnostic sampling interval (default 1000; minimum 250).
                    --output PATH      JSONL log (default diagnostics/probe-<time>.jsonl).
                    --discovery PATH   Installed Apple Music discovery JSON.
                    --apple-aumid ID   Explicit exact session ID fallback (logged as user-supplied).
                    --exercise         Check pause/resume and forward/backward seeks on one
                                       verified, already-playing Apple Music session.
                    --help             Show help.

                    Read-only by default; --exercise briefly changes Apple Music playback.
                    Run scripts/Run-Probe.ps1 to discover installed Apple Music identities first.
                    """);
                return 0;
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                Console.Error.WriteLine("Requires Windows 10 build 17763 or later. No Windows integration was tested.");
                return 2;
            }

            using var stop = new CancellationTokenSource();
            if (options.Seconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                await using var probe = new MediaProbe(options);
                return await probe.RunAsync(stop.Token);
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Probe failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}

internal sealed record ProbeOptions(int Seconds, int SampleMs, string Output, string? Discovery, string? AppleAumid, bool Help, bool Exercise)
{
    public static ProbeOptions Parse(string[] args)
    {
        var seconds = 30;
        var sampleMs = 1000;
        var output = Path.Combine("diagnostics", $"probe-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        string? discovery = null, appleAumid = null;
        var help = false;
        var exercise = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}");
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--sample-ms": sampleMs = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--output": output = Value(); break;
                case "--discovery": discovery = Value(); break;
                case "--apple-aumid": appleAumid = Value(); break;
                case "--help" or "-h": help = true; break;
                case "--exercise": exercise = true; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (seconds < 0 || sampleMs < 250 || sampleMs > 60000) throw new ArgumentException("seconds must be >= 0; sample-ms must be 250–60000.");
        if (exercise && seconds is > 0 and < 25) throw new ArgumentException("--exercise requires --seconds >= 25 (or 0).");
        return new(seconds, sampleMs, output, discovery, appleAumid, help, exercise);
    }
}

internal sealed class MediaProbe : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ProbeOptions _options;
    private readonly StreamWriter _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, string> _appleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appleFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SessionWatch> _sessions = [];
    private readonly Channel<Notice> _events = Channel.CreateBounded<Notice>(new BoundedChannelOptions(512)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _droppedEvents;
    private int _nextSessionId;
    private int _snapshots, _appleSnapshots, _usableSnapshots;
    private long _eventCount;

    public MediaProbe(ProbeOptions options)
    {
        _options = options;
        var path = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _log = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        if (!string.IsNullOrWhiteSpace(options.AppleAumid)) _appleIds[options.AppleAumid] = "explicit-user-override";
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Log("environment", new
        {
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processSessionId = Process.GetCurrentProcess().SessionId,
            Environment.UserInteractive,
            stopwatchFrequency = Stopwatch.Frequency,
            _options.SampleMs,
            _options.Seconds,
            readOnly = !_options.Exercise
        });
        LoadDiscovery();
        Console.WriteLine($"GSMTC probe ({(_options.Exercise ? "playback exercise" : "read-only")}). Log: {Path.GetFullPath(_options.Output)}");
        Console.WriteLine("Exercise Apple Music: play, pause for 3s, resume, seek forwards/backwards, next/previous.");
        Console.WriteLine(_options.Exercise ? "Exercise will pause/resume and seek a verified Apple Music session. It will not skip tracks." : "No playback commands are sent by this probe.");
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            await ReconcileAsync(cancellationToken);
            using var tickerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ticker = TickAsync(tickerStop.Token);
            var exercise = _options.Exercise ? ExerciseAsync(_sessions.Where(w => w.IsApple && w.IdentityReason != "explicit-user-override").ToArray(), tickerStop.Token) : Task.CompletedTask;
            try
            {
                await foreach (var notice in _events.Reader.ReadAllAsync(cancellationToken))
                {
                    if (notice.Reason == "sample")
                    {
                        foreach (var watch in _sessions.ToArray()) await SnapshotAsync(watch, notice, cancellationToken);
                        continue;
                    }
                    _eventCount++;
                    Log("event", new { notice.Reason, sessionId = notice.Watch?.Id, sourceAppUserModelId = notice.Watch?.SourceId, notice.ReceivedUtc, notice.MonotonicMs, notice.Payload });
                    if (notice.Reason is "SessionsChanged" or "CurrentSessionChanged") await ReconcileAsync(cancellationToken);
                    else if (notice.Watch is { Active: true } session) await SnapshotAsync(session, notice, cancellationToken);
                }
            }
            finally
            {
                tickerStop.Cancel();
                try { await ticker; } catch (OperationCanceledException) { }
                try { await exercise; } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log("fatal", Error(ex));
            throw;
        }
        finally
        {
            Detach();
            var summary = new
            {
                elapsedSeconds = _clock.Elapsed.TotalSeconds,
                snapshots = _snapshots,
                appleSnapshots = _appleSnapshots,
                usableAppleSnapshots = _usableSnapshots,
                events = _eventCount,
                droppedEvents = Interlocked.Read(ref _droppedEvents),
                gate = _usableSnapshots == 0 ? "BLOCKED: No Apple Music snapshot with metadata and a usable timeline. Do not build the overlay yet." : "PARTIAL: Data present. Review actual pause/resume/seek/song-change observations before proceeding.",
                reliabilityVerified = false
            };
            Log("summary", summary);
            Console.WriteLine(JsonSerializer.Serialize(summary, Json));
        }
        return _usableSnapshots > 0 ? 0 : 3;
    }

    private void LoadDiscovery()
    {
        if (_options.Discovery is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_options.Discovery));
            Log("discovery", doc.RootElement.Clone());
            if (doc.RootElement.TryGetProperty("apps", out var apps))
                foreach (var app in apps.EnumerateArray())
                {
                    if (app.TryGetProperty("aumid", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                        _appleIds[id.GetString()!] = "installed-app-discovery";
                    if (app.TryGetProperty("packageFamilyName", out var family) && !string.IsNullOrWhiteSpace(family.GetString()))
                        _appleFamilies.Add(family.GetString()!);
                }
        }
        Log("appleIdentityPolicy", new { exactIds = _appleIds, discoveredPackageFamilies = _appleFamilies, fallback = "Exact executable plus running-process package identity, then Windows AppInfo display identity; no current-session assumption or hardcoded package ID." });
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.SampleMs));
        while (await timer.WaitForNextTickAsync(token)) Publish("sample");
    }

    private void Publish(string reason, SessionWatch? watch = null)
    {
        if (!_events.Writer.TryWrite(new(reason, watch, DateTimeOffset.UtcNow, _clock.Elapsed.TotalMilliseconds)))
            Interlocked.Increment(ref _droppedEvents);
    }

    private async Task ExerciseAsync(SessionWatch[] candidates, CancellationToken token)
    {
        void Record(string step, object result, SessionWatch? watch = null)
        {
            if (!_events.Writer.TryWrite(new("exercise", watch, DateTimeOffset.UtcNow, _clock.Elapsed.TotalMilliseconds, new { step, result })))
                Interlocked.Increment(ref _droppedEvents);
        }
        if (candidates.Length != 1)
        {
            Record("skipped", "Requires exactly one verified Apple Music session.");
            return;
        }
        var watch = candidates[0];
        var session = watch.Session;
        var media = watch.Media;
        var pausedByProbe = false;
        async Task<bool> SameTrackAsync()
        {
            if (!watch.Active || media is null) return false;
            var current = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            return current.Title == media.Title && current.Artist == media.Artist && current.AlbumTitle == media.AlbumTitle;
        }
        try
        {
            var info = session.GetPlaybackInfo();
            if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing || !info.Controls.IsPauseEnabled)
            {
                Record("skipped", "Requires Apple Music already playing with pause control enabled.", watch);
                return;
            }
            await Task.Delay(3000, token);
            if (!await SameTrackAsync()) { Record("aborted", "Track changed before pause.", watch); return; }
            pausedByProbe = await session.TryPauseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Record("pause-command", new { accepted = pausedByProbe }, watch);
            if (!pausedByProbe) return;
            await Task.Delay(3000, token);
            var played = await session.TryPlayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            if (played) pausedByProbe = false;
            Record("resume-command", new { accepted = played }, watch);
            if (!played) return;
            await Task.Delay(3000, token);
            if (!await SameTrackAsync()) { Record("aborted", "Track changed before seek.", watch); return; }
            var timeline = session.GetTimelineProperties();
            var position = timeline.Position;
            var forward = position + TimeSpan.FromSeconds(12);
            if (!session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled || forward >= timeline.EndTime - TimeSpan.FromSeconds(5) || forward > timeline.MaxSeekTime)
            {
                Record("seek-skipped", "Seek is unavailable or too close to the end of this track.", watch);
                return;
            }
            var seek = await session.TryChangePlaybackPositionAsync(forward.Ticks).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Record("forward-seek-command", new { accepted = seek, targetSeconds = forward.TotalSeconds }, watch);
            if (!seek) return;
            await Task.Delay(3000, token);
            if (!await SameTrackAsync()) { Record("aborted", "Track changed before restoring seek.", watch); return; }
            var backward = position + TimeSpan.FromSeconds(3);
            var restored = await session.TryChangePlaybackPositionAsync(backward.Ticks).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Record("backward-seek-command", new { accepted = restored, targetSeconds = backward.TotalSeconds }, watch);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Record("error", Error(ex), watch); }
        finally
        {
            if (pausedByProbe)
            {
                try { Record("restore-playing", await session.TryPlayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), watch); }
                catch (Exception ex) { Record("restore-playing-error", Error(ex), watch); }
            }
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Publish("SessionsChanged");
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Publish("CurrentSessionChanged");

    private async Task ReconcileAsync(CancellationToken token)
    {
        if (_manager is null) return;
        string? currentId = null;
        // Log only. Never use the current session to select Apple Music.
        try { currentId = _manager.GetCurrentSession()?.SourceAppUserModelId; }
        catch (Exception ex) { Log("currentSessionReadError", Error(ex)); }
        GlobalSystemMediaTransportControlsSession[] available;
        try { available = _manager.GetSessions().ToArray(); }
        catch (Exception ex) { Log("sessionEnumerationError", Error(ex)); return; }
        foreach (var removed in _sessions.Where(w => !available.Any(s => s.Equals(w.Session))).ToArray())
        {
            removed.Dispose();
            _sessions.Remove(removed);
            Log("sessionRemoved", new { sessionId = removed.Id, sourceAppUserModelId = removed.SourceId });
        }
        foreach (var session in available)
        {
            if (_sessions.Any(w => w.Session.Equals(session))) continue;
            string sourceId;
            try { sourceId = session.SourceAppUserModelId; }
            catch (Exception ex) { Log("sourceIdError", Error(ex)); continue; }
            var identity = Identify(sourceId);
            SessionWatch watch;
            try { watch = new SessionWatch(++_nextSessionId, session, sourceId, identity.IsApple, identity.Reason, identity.DisplayName, Publish); }
            catch (Exception ex) { Log("sessionAttachError", new { sourceId, error = Error(ex) }); continue; }
            _sessions.Add(watch);
            Log("sessionAdded", new { sessionId = watch.Id, sourceAppUserModelId = sourceId, watch.IsApple, watch.IdentityReason, watch.DisplayName });
            await SnapshotAsync(watch, new("initial", watch, DateTimeOffset.UtcNow, _clock.Elapsed.TotalMilliseconds), token);
        }
        Log("sessions", new { count = available.Length, appleCount = _sessions.Count(w => w.IsApple), currentSourceAppUserModelId = currentId, sessionIds = _sessions.Select(w => w.Id).ToArray() });
        Console.WriteLine($"Sessions: {available.Length}; identified Apple Music: {_sessions.Count(w => w.IsApple)}; Windows current: {currentId ?? "(none)"}");
    }

    private (bool IsApple, string Reason, string? DisplayName) Identify(string id)
    {
        if (_appleIds.TryGetValue(id, out var reason)) return (true, reason, null);
        if (NativeAppIdentity.TryResolve(id, _appleFamilies, out var processReason))
            return (true, processReason, null);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return (false, "unmatched; AppInfo fallback requires Windows build 19041", null);
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(id);
            var display = info.DisplayInfo.DisplayName;
            if (string.Equals(display?.Trim(), "Apple Music", StringComparison.OrdinalIgnoreCase))
                return (true, "Windows.AppInfo.DisplayName", display);
            return (false, "unmatched", display);
        }
        catch { return (false, "unmatched; AppInfo unavailable", null); }
    }

    private async Task SnapshotAsync(SessionWatch watch, Notice notice, CancellationToken token)
    {
        var errors = new List<object>();
        GlobalSystemMediaTransportControlsSessionMediaProperties? media = null;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback = null;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
        if (watch.Media is null || notice.Reason is "initial" or "MediaPropertiesChanged" || _clock.Elapsed.TotalMilliseconds - watch.LastMetadataReadMs >= 5000)
        {
            try { watch.Media = await watch.Session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(new { api = "TryGetMediaPropertiesAsync", error = Error(ex) }); watch.Media = null; }
            watch.LastMetadataReadMs = _clock.Elapsed.TotalMilliseconds;
        }
        media = watch.Media;
        try { playback = watch.Session.GetPlaybackInfo(); } catch (Exception ex) { errors.Add(new { api = "GetPlaybackInfo", error = Error(ex) }); }
        try { timeline = watch.Session.GetTimelineProperties(); } catch (Exception ex) { errors.Add(new { api = "GetTimelineProperties", error = Error(ex) }); }
        var now = DateTimeOffset.UtcNow;
        var monotonicMs = _clock.Elapsed.TotalMilliseconds;
        var status = playback?.PlaybackStatus.ToString();
        var rate = playback?.PlaybackRate;
        var start = timeline?.StartTime.TotalSeconds;
        var end = timeline?.EndTime.TotalSeconds;
        var duration = end > start ? end - start : null;
        var position = timeline?.Position.TotalSeconds;
        var age = timeline is null ? (double?)null : (now - timeline.LastUpdatedTime).TotalSeconds;
        var usableTimeline = duration > 0 && position >= start && position <= end && timeline?.LastUpdatedTime > DateTimeOffset.UnixEpoch;
        var identity = JsonSerializer.Serialize(new { media?.Title, media?.Artist, media?.AlbumTitle });
        var sameTrack = watch.LastTrackIdentity == identity;
        var previousStatus = watch.LastStatus;
        var statusChanged = previousStatus is not null && previousStatus != status;
        if (!sameTrack || statusChanged) watch.LastTransitionMs = monotonicMs;
        double? prediction = watch.LastPosition is double oldPosition
            ? oldPosition + (previousStatus == "Playing" ? (monotonicMs - watch.LastMonotonicMs) / 1000 * (watch.LastRate ?? 1) : 0)
            : null;
        var freshTimeline = timeline is not null && timeline.LastUpdatedTime != watch.LastTimelineUpdate;
        // Translate fresh provider timestamps once, then use monotonic elapsed time.
        // A resume event may precede a fresh timeline: never extrapolate its old
        // timestamp across the paused interval.
        var estimatedAtRead = position;
        if (status == "Playing" && !statusChanged)
        {
            if ((!sameTrack || freshTimeline) && age is >= 0 and <= 30)
                estimatedAtRead += age * (rate ?? 1);
            else if (sameTrack && prediction is not null)
                estimatedAtRead = prediction;
        }
        if (estimatedAtRead is double estimate && start is double low && end is double high && high > low)
            estimatedAtRead = Math.Clamp(estimate, low, high);
        double? discontinuity = sameTrack && freshTimeline && estimatedAtRead is double p && prediction is double predicted ? p - predicted : null;
        // This diagnostic calls discontinuities candidates, not verified seeks: timestamps and
        // event ordering can also change when playback status or metadata updates arrive separately.
        var settling = monotonicMs - watch.LastTransitionMs < 2000;
        var seekCandidate = sameTrack && previousStatus == status && !settling && Math.Abs(discontinuity ?? 0) >= 2;
        if (watch.LastTrackIdentity is not null && !sameTrack) watch.TrackChanges++;
        if (previousStatus is not null && status != previousStatus) watch.StatusChanges++;
        if (seekCandidate) watch.SeekCandidates++;
        _snapshots++;
        if (watch.IsApple)
        {
            _appleSnapshots++;
            if (!string.IsNullOrWhiteSpace(media?.Title) && !string.IsNullOrWhiteSpace(media?.Artist) && usableTimeline) _usableSnapshots++;
        }
        Log("snapshot", new
        {
            sessionId = watch.Id,
            sourceAppUserModelId = watch.SourceId,
            watch.IsApple,
            watch.IdentityReason,
            reason = notice.Reason,
            eventReceivedUtc = notice.ReceivedUtc,
            eventMonotonicMs = notice.MonotonicMs,
            readMonotonicMs = monotonicMs,
            processingDelayMs = monotonicMs - notice.MonotonicMs,
            title = media?.Title,
            artist = media?.Artist,
            albumArtist = media?.AlbumArtist,
            album = media?.AlbumTitle,
            trackNumber = media?.TrackNumber,
            playbackStatus = status,
            playbackRate = rate,
            controls = playback is null ? null : new { playback.Controls.IsPlayEnabled, playback.Controls.IsPauseEnabled, playback.Controls.IsNextEnabled, playback.Controls.IsPreviousEnabled, playback.Controls.IsPlaybackPositionEnabled },
            durationSeconds = duration,
            timeline = timeline is null ? null : new
            {
                startSeconds = start,
                endSeconds = end,
                positionSeconds = position,
                minSeekSeconds = timeline.MinSeekTime.TotalSeconds,
                maxSeekSeconds = timeline.MaxSeekTime.TotalSeconds,
                timeline.LastUpdatedTime,
                updateAgeSeconds = age,
                estimatedAtReadSeconds = estimatedAtRead,
                usable = usableTimeline,
                freshTimeline,
                transitionSettling = settling,
                predictedFromPreviousReadSeconds = prediction,
                discontinuitySeconds = discontinuity,
                seekCandidate
            },
            observations = new { watch.TrackChanges, watch.StatusChanges, watch.SeekCandidates },
            errors
        });
        Console.WriteLine($"[{_clock.Elapsed.ToString(@"hh\:mm\:ss")}] #{watch.Id} {(watch.IsApple ? "APPLE" : "other")} {notice.Reason}: {status ?? "unknown"} {position?.ToString("F2", CultureInfo.InvariantCulture) ?? "?"}/{duration?.ToString("F2", CultureInfo.InvariantCulture) ?? "?"}s | {media?.Artist} — {media?.Title}");
        watch.LastTrackIdentity = identity;
        watch.LastStatus = status;
        // Do not move the interpolation anchor on repeated reads of the same stale timeline.
        if (freshTimeline || watch.LastPosition is null || !sameTrack || previousStatus != status)
        {
            watch.LastPosition = estimatedAtRead;
            watch.LastMonotonicMs = monotonicMs;
            watch.LastRate = rate;
            watch.LastTimelineUpdate = timeline?.LastUpdatedTime;
        }
    }

    private void Log(string type, object data) => _log.WriteLine(JsonSerializer.Serialize(new { type, utc = DateTimeOffset.UtcNow, monotonicMs = _clock.Elapsed.TotalMilliseconds, data }, Json));
    private static object Error(Exception ex) => new { type = ex.GetType().FullName, ex.Message, hresult = $"0x{ex.HResult:X8}" };

    private void Detach()
    {
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        foreach (var session in _sessions) session.Dispose();
        _sessions.Clear();
        _events.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        await _log.DisposeAsync();
    }
}

internal sealed record Notice(string Reason, SessionWatch? Watch, DateTimeOffset ReceivedUtc, double MonotonicMs, object? Payload = null);

internal sealed class SessionWatch : IDisposable
{
    private readonly Action<string, SessionWatch?> _publish;
    public int Id { get; }
    public GlobalSystemMediaTransportControlsSession Session { get; }
    public string SourceId { get; }
    public bool IsApple { get; }
    public string IdentityReason { get; }
    public string? DisplayName { get; }
    public bool Active { get; private set; } = true;
    public GlobalSystemMediaTransportControlsSessionMediaProperties? Media { get; set; }
    public double LastMetadataReadMs { get; set; }
    public string? LastTrackIdentity { get; set; }
    public string? LastStatus { get; set; }
    public double? LastPosition { get; set; }
    public double LastMonotonicMs { get; set; }
    public double LastTransitionMs { get; set; }
    public double? LastRate { get; set; }
    public DateTimeOffset? LastTimelineUpdate { get; set; }
    public int TrackChanges { get; set; }
    public int StatusChanges { get; set; }
    public int SeekCandidates { get; set; }

    public SessionWatch(int id, GlobalSystemMediaTransportControlsSession session, string sourceId, bool isApple, string identityReason, string? displayName, Action<string, SessionWatch?> publish)
    {
        (Id, Session, SourceId, IsApple, IdentityReason, DisplayName, _publish) = (id, session, sourceId, isApple, identityReason, displayName, publish);
        session.MediaPropertiesChanged += OnMedia;
        session.PlaybackInfoChanged += OnPlayback;
        session.TimelinePropertiesChanged += OnTimeline;
    }

    private void OnMedia(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _publish("MediaPropertiesChanged", this);
    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _publish("PlaybackInfoChanged", this);
    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _publish("TimelinePropertiesChanged", this);

    public void Dispose()
    {
        if (!Active) return;
        Active = false;
        Session.MediaPropertiesChanged -= OnMedia;
        Session.PlaybackInfoChanged -= OnPlayback;
        Session.TimelinePropertiesChanged -= OnTimeline;
    }
}
