using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AppleMusicLyrics.Core;
using AppleMusicLyrics.Core.Playback;
using AppleMusicLyrics.Probe;
using Windows.Media.Control;

namespace AppleMusicLyrics.App;

internal sealed record MediaObservation(TrackMetadata? Track, PlaybackStatus Status, TimeSpan? Duration, TimeSpan? Position, double? Rate, TimeSpan? ReportAge, bool Reconnected, string? Source, string? Error, double? CapturedAtTimestamp = null);

internal sealed class MediaObserver : IAsyncDisposable
{
    private readonly string _directory;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _events = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _families = new(StringComparer.OrdinalIgnoreCase);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSessionMediaProperties? _media;
    private DateTimeOffset _lastMetadataRead;
    private Task? _run;
    private Task? _ticker;
    private bool _reconnected;
    public event Action<MediaObservation>? Changed;

    public MediaObserver(string directory) => _directory = directory;

    public Task StartAsync()
    {
        _run = Task.Run(RunAsync);
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        try { await DiscoverAsync(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        catch (Exception ex) { AppLog.Write("Identity discovery: " + ex.Message); }
        while (!_stop.IsCancellationRequested)
        {
            await ObserveConnectionAsync();
            if (_stop.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ObserveConnectionAsync()
    {
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
            _manager.SessionsChanged += OnSessions;
            _events.Writer.TryWrite("sessions");
            _ticker = TickAsync(connectionStop.Token);
            var consecutiveFailures = 0;
            await foreach (var reason in _events.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    if (reason is "sessions" or "fallback") Reconcile();
                    await ReadAsync(reason, _stop.Token);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    AppLog.Write("Media session read: " + ex.Message);
                    Changed?.Invoke(new(null, PlaybackStatus.Unknown, null, null, null, null, false, null, "Media session unavailable"));
                    DetachSession();
                    if (++consecutiveFailures >= 3) break; // Re-request a disconnected manager.
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLog.Write("Media observer: " + ex);
            Changed?.Invoke(new(null, PlaybackStatus.Unknown, null, null, null, null, false, null, "Windows media control unavailable"));
        }
        finally
        {
            connectionStop.Cancel();
            if (_ticker is not null) { try { await _ticker; } catch (OperationCanceledException) { } _ticker = null; }
            if (_manager is not null) _manager.SessionsChanged -= OnSessions;
            DetachSession();
            _manager = null;
        }
    }

    private async Task DiscoverAsync(CancellationToken token)
    {
        var output = Path.Combine(_directory, "apple-music-discovery.json");
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "Discover-AppleMusic.ps1");
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-OutputPath", output }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Cannot start installed-app discovery.");
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            await stdout;
            if (process.ExitCode != 0) AppLog.Write("Discovery: " + await stderr);
            else await stderr;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException) { AppLog.Write("App discovery: " + ex.Message); }
        try
        {
            if (!File.Exists(output)) return;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            foreach (var app in json.RootElement.GetProperty("apps").EnumerateArray())
            {
                if (app.TryGetProperty("aumid", out var id) && id.GetString() is { Length: > 0 } value) _ids.Add(value);
                if (app.TryGetProperty("packageFamilyName", out var family) && family.GetString() is { Length: > 0 } name) _families.Add(name);
            }
            AppLog.Write($"Apple identity discovery: {_ids.Count} app IDs; {_families.Count} package families.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { AppLog.Write("Discovery report: " + ex.Message); }
    }

    private bool IsApple(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var id = session.SourceAppUserModelId;
            if (_ids.Contains(id) || NativeAppIdentity.TryResolve(id, _families, out _)) return true;
            try { return string.Equals(Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(id).DisplayInfo.DisplayName?.Trim(), "Apple Music", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        catch { return false; }
    }

    private void Reconcile()
    {
        var available = _manager?.GetSessions().ToArray() ?? [];
        bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try { return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }
        var appleSessions = available.Where(IsApple).ToArray();
        var existing = _session is not null && appleSessions.Any(s => s.Equals(_session));
        var candidate = existing && (IsPlaying(_session!) || !appleSessions.Any(IsPlaying))
            ? _session : appleSessions.OrderByDescending(IsPlaying).FirstOrDefault();
        if (_session is not null && candidate?.Equals(_session) == true) return;
        DetachSession();
        _session = candidate;
        if (_session is null) return;
        _session.MediaPropertiesChanged += OnMedia;
        _session.PlaybackInfoChanged += OnPlayback;
        _session.TimelinePropertiesChanged += OnTimeline;
        _reconnected = true;
        AppLog.Write("Tracking Apple Music session: " + _session.SourceAppUserModelId);
    }

    private async Task ReadAsync(string reason, CancellationToken token)
    {
        if (_session is null)
        {
            Changed?.Invoke(new(null, PlaybackStatus.Stopped, null, null, null, null, false, null, "Apple Music is not playing"));
            return;
        }
        if (_media is null || reason == "media" || DateTimeOffset.UtcNow - _lastMetadataRead > TimeSpan.FromSeconds(15))
        {
            _media = await _session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            _lastMetadataRead = DateTimeOffset.UtcNow;
        }
        var playback = _session.GetPlaybackInfo();
        var timeline = _session.GetTimelineProperties();
        // Same monotonic clock domain as PlaybackClock. Retain capture time through
        // dispatcher scheduling so the UI never anchors an old snapshot as current.
        var capturedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var capturedUtc = DateTimeOffset.UtcNow;
        var status = playback.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Stopped,
            _ => PlaybackStatus.Unknown
        };
        var valid = timeline.EndTime > timeline.StartTime && timeline.Position >= timeline.StartTime && timeline.Position <= timeline.EndTime && timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch;
        TimeSpan? duration = valid ? timeline.EndTime - timeline.StartTime : null;
        TimeSpan? position = valid ? timeline.Position - timeline.StartTime : null;
        TimeSpan? age = valid ? capturedUtc - timeline.LastUpdatedTime : null;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age > TimeSpan.FromSeconds(30) && status == PlaybackStatus.Playing) { position = null; age = null; }
        TrackMetadata? track = null;
        if (!string.IsNullOrWhiteSpace(_media?.Title) && !string.IsNullOrWhiteSpace(_media?.Artist))
            track = MetadataNormalizer.Prepare(new(_media.Title, _media.Artist, _media.AlbumTitle ?? "", duration?.TotalSeconds ?? 0));
        Changed?.Invoke(new(track, status, duration, position, playback.PlaybackRate, age, _reconnected, _session.SourceAppUserModelId, null, capturedAt));
        _reconnected = false;
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token)) _events.Writer.TryWrite("fallback");
    }
    private void OnSessions(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => _events.Writer.TryWrite("sessions");
    private void OnMedia(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _events.Writer.TryWrite("media");
    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _events.Writer.TryWrite("playback");
    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _events.Writer.TryWrite("timeline");

    private void DetachSession()
    {
        if (_session is not null)
        {
            try { _session.MediaPropertiesChanged -= OnMedia; } catch { }
            try { _session.PlaybackInfoChanged -= OnPlayback; } catch { }
            try { _session.TimelinePropertiesChanged -= OnTimeline; } catch { }
        }
        _session = null;
        _media = null;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _events.Writer.TryComplete();
        if (_run is not null) await _run.ConfigureAwait(false);
        if (_ticker is not null) { try { await _ticker.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _stop.Dispose();
    }
}
