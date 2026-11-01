using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppleMusicLyrics.Core;
using AppleMusicLyrics.Core.Playback;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AppleMusicLyrics.App;

internal sealed class MainController : IDisposable
{
    private readonly string _directory;
    private readonly SettingsStore _store;
    private readonly OverlaySettings _settings;
    private readonly OverlayWindow _overlay;
    private readonly MediaObserver _observer;
    private readonly PlaybackClock _clock = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly LocalLyricsCache _cache;
    private readonly AppleNativeLyricsProvider _nativeProvider = new();
    private readonly ApplePublicCatalogResolver _publicCatalog;
    private readonly NativeCacheMonitor _nativeMonitor;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _nativeRefreshTimer;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _statusItem = new(UiText.T("Waiting for Apple Music")) { Enabled = false };
    private readonly Forms.ToolStripMenuItem _showItem = new(UiText.T("Show lyrics"));
    private readonly Forms.ToolStripMenuItem _lockItem = new(UiText.T("Lock position"));
    private readonly Forms.ToolStripMenuItem _offsetItem = new(UiText.T("Track timing offset"));
    private readonly Forms.ToolStripMenuItem _globalOffsetItem = new(UiText.T("Global timing offset · all songs"));
    private readonly Forms.ToolStripMenuItem _promoteOffsetItem = new(UiText.T("Use this song's total for all songs"));
    private readonly Forms.ToolStripMenuItem _plainItem = new(UiText.T("View untimed lyrics…"));
    private readonly Forms.ToolStripMenuItem _startupItem = new(UiText.T("Start with Windows"));
    private readonly Forms.ToolStripMenuItem _translationItem = new(UiText.T("Show translation"));
    private readonly Forms.ToolStripMenuItem _pronunciationItem = new(UiText.T("Show pronunciation"));
    private readonly Forms.ToolStripMenuItem _nativeStatusItem = new(UiText.T("Native cache: waiting")) { Enabled = false };
    private CancellationTokenSource? _lyricsCancellation;
    private CancellationTokenSource? _nativeProbeCancellation;
    private TrackMetadata? _track;
    private string? _trackKey;
    private LyricsResult? _lyrics;
    private NativeLyricsDocument? _nativeLyrics;
    private NativeLyricsLookup? _nativeLookup;
    private LyricPresentationResult? _presentation;
    private string? _presentationSignature;
    private string? _displayLimitation;
    private long _lookupVersion;
    private bool _lookupRunning;
    private bool _nativeRefreshPending;
    private bool _manualImport;
    private string _status = "Waiting for Apple Music";
    private bool _disposed;
    private bool _preview;
    private int _lastLine = int.MinValue;
    private SettingsWindow? _settingsWindow;
    private readonly System.Collections.Generic.Queue<object> _playbackEvents = new();
    private bool _timingSaved = true;
    private double _lastObservationQueueSeconds;

    public MainController(string directory)
    {
        _directory = directory;
        _store = new(directory);
        _settings = _store.Load();
        _settings.UiLanguage = UiText.Language;
        _cache = new(Path.Combine(directory, "lyrics-cache"));
        _publicCatalog = new(_http, Path.Combine(directory, "public-catalog"), "US", RegionInfo.CurrentRegion.TwoLetterISORegionName);
        _publicCatalog.Diagnostic += diagnostic => AppLog.Write("Public Apple catalog storefront: " + JsonSerializer.Serialize(diagnostic));
        _overlay = new(_settings);
        _overlay.SettingsChanged += () => { ScheduleSave(); RefreshVisibility(); UpdateTray(); };
        _observer = new(directory);
        _observer.Changed += observation =>
        {
            if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(() => OnObservation(observation), DispatcherPriority.Background);
        };
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _renderTimer.Tick += (_, _) => Render();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(_settings); };
        _nativeRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _nativeRefreshTimer.Tick += (_, _) =>
        {
            _nativeRefreshTimer.Stop();
            if (_lookupRunning) { _nativeRefreshPending = true; _ = ProbeNativeWhileLoadingAsync(); return; }
            BeginLookup(nativeOnly: true);
        };
        _nativeMonitor = new(_nativeProvider.CacheRoots);
        _nativeMonitor.Changed += () =>
        {
            if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed && !_preview && !_manualImport && _track is not null && !_nativeRefreshTimer.IsEnabled)
                        _nativeRefreshTimer.Start();
                });
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_nativeStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _showItem.Click += (_, _) => { _settings.IsVisible = !_settings.IsVisible; RefreshVisibility(); ScheduleSave(); UpdateTray(); };
        _lockItem.Click += (_, _) => { _overlay.SetLocked(!_settings.IsLocked); RefreshVisibility(); UpdateTray(); ScheduleSave(); };
        menu.Items.Add(_showItem);
        menu.Items.Add(_lockItem);
        menu.Items.Add(UiText.T("Settings…"), null, (_, _) => ShowSettings());
        _translationItem.Click += (_, _) => { _settings.ShowTranslation = !_settings.ShowTranslation; DisplaySettingsChanged(); };
        _pronunciationItem.Click += (_, _) => { _settings.ShowPronunciation = !_settings.ShowPronunciation; DisplaySettingsChanged(); };
        menu.Items.Add(_translationItem);
        menu.Items.Add(_pronunciationItem);
        _globalOffsetItem.DropDownItems.Add(UiText.T("Lyrics earlier (+250 ms) · all songs"), null, (_, _) => SetGlobalOffset(_settings.GlobalOffsetMilliseconds + 250));
        _globalOffsetItem.DropDownItems.Add(UiText.T("Lyrics later (−250 ms) · all songs"), null, (_, _) => SetGlobalOffset(_settings.GlobalOffsetMilliseconds - 250));
        _globalOffsetItem.DropDownItems.Add(new Forms.ToolStripSeparator());
        _globalOffsetItem.DropDownItems.Add(UiText.T("Set +1750 ms · lyrics earlier"), null, (_, _) => SetGlobalOffset(1750));
        _globalOffsetItem.DropDownItems.Add(UiText.T("Set +2000 ms · lyrics earlier"), null, (_, _) => SetGlobalOffset(2000));
        _promoteOffsetItem.Click += (_, _) => PromoteTrackOffset();
        _globalOffsetItem.DropDownItems.Add(_promoteOffsetItem);
        _globalOffsetItem.DropDownItems.Add(new Forms.ToolStripSeparator());
        _globalOffsetItem.DropDownItems.Add(UiText.T("Reset global to 0 (keep song corrections)"), null, (_, _) => SetGlobalOffset(0));
        menu.Items.Add(_globalOffsetItem);
        _offsetItem.DropDownItems.Add(UiText.T("Lyrics earlier (+250 ms)"), null, (_, _) => AdjustOffset(250));
        _offsetItem.DropDownItems.Add(UiText.T("Lyrics later (−250 ms)"), null, (_, _) => AdjustOffset(-250));
        _offsetItem.DropDownItems.Add(UiText.T("Reset this track"), null, (_, _) => ResetOffset());
        menu.Items.Add(_offsetItem);
        menu.Items.Add(UiText.T("Reset position"), null, (_, _) => { _overlay.ResetPosition(); _settings.IsVisible = true; RefreshVisibility(); ScheduleSave(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(UiText.T("Import local .lrc…"), null, async (_, _) => await ImportAsync());
        _plainItem.Click += (_, _) => ShowPlainLyrics();
        menu.Items.Add(_plainItem);
        menu.Items.Add(UiText.T("Retry lyric lookup"), null, (_, _) => BeginLookup());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem.Click += (_, _) => { _settings.StartWithWindows = !_settings.StartWithWindows; ApplyStartup(); ScheduleSave(); UpdateTray(); };
        menu.Items.Add(_startupItem);
        menu.Items.Add(UiText.T("Open data folder"), null, (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_directory) { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(UiText.T("Exit"), null, (_, _) => Application.Current.Shutdown());
        _tray = new Forms.NotifyIcon { Icon = CreateTrayIcon(), Text = UiText.T("Apple Music Desktop Lyrics"), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => { _overlay.SetLocked(false); _settings.IsVisible = true; RefreshVisibility(); UpdateTray(); ScheduleSave(); };
        UpdateTray();
    }

    public async Task StartAsync(bool preview)
    {
        _preview = preview;
        var foregroundBefore = WindowDiagnostics.GetForegroundWindow();
        _overlay.Show();
        RefreshVisibility();
        if (preview)
        {
            // Explicit QA mode uses original demonstration text, never invented song timing.
            _overlay.SetLyrics("A little music above the everyday", "두 줄의 가사 · 两行歌词 · 二行の歌詞",
                _settings.ShowTranslation && _settings.ShowPronunciation ? "Original demonstration text · Preview only" : null);
            _overlay.Show();
            await Task.Delay(300);
            await CapturePreviewAsync(foregroundBefore);
        }
        else await _observer.StartAsync();
        AppLog.Write("Started. Observes Windows media timelines; never sends playback or seek commands.");
    }

    private void OnObservation(MediaObservation observation)
    {
        if (_disposed || _preview) return;
        if (observation.CapturedAtTimestamp is { } observedAt && double.IsFinite(observedAt)
            && observedAt < _clock.State.LastRefreshTimestamp) return;
        var key = observation.Track?.Key;
        var now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastObservationQueueSeconds = observation.CapturedAtTimestamp is { } captured ? Math.Max(0, now - captured) : 0;
        var update = _clock.Update(key, observation.Status, observation.Duration, observation.Position, observation.Rate, observation.Reconnected, observation.ReportAge, observation.CapturedAtTimestamp);
        if (update.IsSeek) AppLog.Write($"Observed timeline discontinuity candidate: {update.Reason} at {update.Position.TotalSeconds:F2}s.");
        if (_trackKey != key)
        {
            _track = observation.Track;
            _trackKey = key;
            _lyrics = null;
            _nativeLyrics = null;
            _nativeLookup = null;
            _presentation = null;
            _presentationSignature = null;
            _displayLimitation = null;
            _manualImport = false;
            _lookupVersion++;
            _lookupRunning = false;
            _nativeRefreshPending = false;
            _nativeRefreshTimer.Stop();
            _lastLine = int.MinValue;
            _lyricsCancellation?.Cancel();
            _status = observation.Track is null ? observation.Error ?? "Waiting for Apple Music" : "Waiting for a valid track timeline";
            if (observation.Track is not null && observation.Duration is { Ticks: > 0 }) BeginLookup();
        }
        else if (_track is not null && _lyrics is null && _lyricsCancellation is null && observation.Duration is { Ticks: > 0 }) BeginLookup();
        if (_track is null) _status = observation.Error ?? "Waiting for Apple Music";
        Render();
        UpdateTray();
        if (update.IsSeek || update.Reason is PlaybackUpdateReason.TrackChanged or PlaybackUpdateReason.Paused or PlaybackUpdateReason.Resumed or PlaybackUpdateReason.Reconnected)
        {
            _playbackEvents.Enqueue(new { reason = update.Reason.ToString(), trackKey = key,
                status = observation.Status.ToString(), positionSeconds = _clock.CurrentPosition.TotalSeconds,
                activeLineIndex = _lastLine, nativeCatalogId = _nativeLookup?.CatalogId });
            while (_playbackEvents.Count > 64) _playbackEvents.Dequeue();
        }
    }

    private void BeginLookup(bool nativeOnly = false)
    {
        if (_disposed || _preview || _track is null || _track.DurationSeconds <= 0 || (nativeOnly && _manualImport)) return;
        if (!nativeOnly) _manualImport = false;
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        var cancellation = _lyricsCancellation = new();
        var track = _track;
        var key = _trackKey;
        var version = ++_lookupVersion;
        _lookupRunning = true;
        if (!nativeOnly) _status = "Looking up native Apple lyrics…";
        UpdateTray();
        _ = LoadAsync(track, key, version, cancellation.Token, nativeOnly);
    }

    private bool IsCurrent(string? key, long version, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && key == _trackKey && version == _lookupVersion;

    private async Task ProbeNativeWhileLoadingAsync()
    {
        if (_track is null || _lyricsCancellation is null || _manualImport) return;
        _nativeProbeCancellation?.Cancel();
        var source = _nativeProbeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lyricsCancellation.Token);
        var token = source.Token;
        var track = _track;
        var key = _trackKey;
        var version = _lookupVersion;
        try
        {
            var native = await ReadNativeLyricsAsync(track, token);
            if (!IsCurrent(key, version, token) || native.Document is null) return;
            // A validated native result supersedes a slow network fallback. A negative
            // local probe leaves that fallback running, so a cache event cannot lose it.
            _lyricsCancellation.Cancel();
            _lookupVersion++;
            _lookupRunning = false;
            _nativeRefreshPending = false;
            _nativeLookup = native;
            _nativeLyrics = native.Document;
            _lyrics = native.Document.ToLyricsResult();
            _presentationSignature = null;
            _status = "Synchronized lyrics · Apple Music native cache";
            AppLog.Write("Late native cache match applied: catalogId=" + native.CatalogId);
            Render(); UpdateTray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (IsCurrent(key, version, token)) AppLog.Write("Native cache refresh: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_nativeProbeCancellation, source)) _nativeProbeCancellation = null;
            source.Dispose();
        }
    }

    private async Task LoadAsync(TrackMetadata track, string? key, long version, CancellationToken token, bool nativeOnly)
    {
        try
        {
            // Apple metadata and timeline are not atomic. Debounce rapid/new-track snapshots.
            await Task.Delay(350, token);
            var native = await ReadNativeLyricsAsync(track, token);
            if (!IsCurrent(key, version, token)) return;
            _nativeLookup = native;
            var wasNative = _nativeLyrics is not null;
            _nativeLyrics = native.Document;
            LyricsResult? result = native.Document?.ToLyricsResult();
            AppLog.Write($"Native lookup: {native.Status}; catalogId={native.CatalogId ?? "none"}; lyricFiles={native.LyricFilesInspected}; catalogFiles={native.CatalogFilesInspected}; {native.Message}");
            if (result is null)
            {
                if (nativeOnly && !wasNative) { Render(); return; }
                // Remove a previously matched sheet before looking up a fallback if cache validation now fails.
                if (wasNative) { _lyrics = null; _presentationSignature = null; RefreshVisibility(); }
                var provider = new LrclibLyricsProvider(_http, _cache, new LrclibOptions { ClientContact = _settings.LyricsClientContact });
                result = await provider.GetLyricsAsync(track, token);
            }
            if (!IsCurrent(key, version, token)) return;
            _lyrics = result;
            _presentationSignature = null;
            AppLog.Write($"Lyrics result: source={result?.Source ?? "none"}; synchronizedLines={result?.Lines.Count ?? 0}; instrumental={result?.Instrumental ?? false}.");
            _status = result is null ? "No matching lyrics" : result.Instrumental ? "Instrumental track" : result.HasSynchronizedLyrics ? "Synchronized lyrics · " + result.Source : "Untimed lyrics available in tray";
            _lastLine = int.MinValue;
            Render();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (LyricsProviderException ex)
        {
            if (!IsCurrent(key, version, token)) return;
            _status = ex.Error switch
            {
                LyricsProviderError.Configuration => "Set LRCLIB contact in Settings, or import LRC",
                LyricsProviderError.RateLimited => "LRCLIB rate limit · retry later",
                LyricsProviderError.Timeout => "Lyrics request timed out · retry from tray",
                LyricsProviderError.Network => "Network unavailable · retry from tray",
                _ => "Lyrics provider returned invalid data"
            };
            AppLog.Write("Lyrics lookup: " + ex.Message);
        }
        catch (Exception ex)
        {
            if (!IsCurrent(key, version, token)) return;
            _status = "Lyric lookup failed · retry from tray";
            AppLog.Write("Lyrics lookup: " + ex);
        }
        finally
        {
            // Keep the completed request as a marker: missing lyrics must not trigger
            // a network retry for every timeline update. Tray retry/track change resets it.
            if (IsCurrent(key, version, token))
            {
                _lookupRunning = false;
                RefreshVisibility(); UpdateTray();
                if (_nativeRefreshPending) { _nativeRefreshPending = false; _nativeRefreshTimer.Start(); }
            }
        }
    }

    private async Task<NativeLyricsLookup> ReadNativeLyricsAsync(TrackMetadata track, CancellationToken token)
    {
        var local = await Task.Run(() => _nativeProvider.GetNativeLyricsAsync(track, token), token);
        if (local.Status != NativeLyricsLookupStatus.MissingMetadata || local.CandidateCatalogIds is not { Count: > 0 } ids)
            return local;
        AppLog.Write($"Public Apple catalog: resolving {ids.Count} native cache identities; no lyrics or authentication requested.");
        var metadata = await _publicCatalog.ResolveAsync(ids, token, local.CandidateStorefrontHints);
        token.ThrowIfCancellationRequested();
        AppLog.Write($"Public Apple catalog: resolved {metadata.Count}/{ids.Count} identities.");
        if (metadata.Count == 0) return local;
        // The provider still requires complete, unique title/artist/album/duration
        // evidence for the native resource ID. Partial resolution never guesses.
        return await Task.Run(() => _nativeProvider.GetNativeLyricsAsync(track, token, metadata), token);
    }

    private void Render()
    {
        if (_preview || _disposed) return;
        if (_lyrics?.HasSynchronizedLyrics == true && _clock.State.HasTimeline)
        {
            string[] rows;
            if (_nativeLyrics is not null)
            {
                _presentation = LyricPresentation.Select(_nativeLyrics, _clock.CurrentPosition,
                    _settings.ShowTranslation, _settings.ShowPronunciation, _settings.TranslationLanguage,
                    _settings.PronunciationLanguage, TimeSpan.FromMilliseconds(EffectiveOffset()));
                rows = _presentation.Lines.Select(line => line.Text).ToArray();
                _lastLine = _presentation.ActiveIndex;
                _displayLimitation = _presentation.Limitation;
            }
            else
            {
                _presentation = null;
                var pair = LyricSelection.Select(_lyrics.Lines, _clock.CurrentPosition, TimeSpan.FromMilliseconds(EffectiveOffset()));
                _lastLine = pair.ActiveIndex;
                rows = [pair.Active?.Text ?? "", pair.Upcoming?.Text ?? ""];
                _displayLimitation = _settings.ShowTranslation || _settings.ShowPronunciation
                    ? "Native supplemental lyrics unavailable; showing originals" : null;
            }
            var signature = string.Join('\n', rows);
            if (_presentationSignature != signature)
            {
                _presentationSignature = signature;
                _overlay.SetLyrics(rows.ElementAtOrDefault(0) ?? "", rows.ElementAtOrDefault(1) ?? "", rows.ElementAtOrDefault(2));
            }
            UpdateTray();
        }
        RefreshVisibility();
        if (_clock.State.Status == PlaybackStatus.Playing && _lyrics?.HasSynchronizedLyrics == true && _clock.State.HasTimeline && _settings.IsVisible)
            _renderTimer.Start();
        else _renderTimer.Stop();
    }

    private void RefreshVisibility()
    {
        var hasTimedLyrics = _lyrics?.HasSynchronizedLyrics == true && _clock.State.HasTimeline && _clock.State.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        var show = _preview || (_settings.IsVisible && (hasTimedLyrics || !_settings.IsLocked));
        if (!_settings.IsLocked && !hasTimedLyrics && !_preview)
        {
            _overlay.SetLyrics(UiText.T("Drag to reposition lyrics"), UiText.T("Lock here, or use the tray menu"));
            _lastLine = int.MinValue;
        }
        if (show && !_overlay.IsVisible) _overlay.Show();
        else if (!show && _overlay.IsVisible) _overlay.Hide();
    }

    private int EffectiveOffset() => LyricsTimingOffsets.GetEffectiveMilliseconds(
        _settings.GlobalOffsetMilliseconds, _settings.TrackOffsetsMilliseconds, _trackKey);
    private int TrackOffset() => _trackKey is not null && _settings.TrackOffsetsMilliseconds.TryGetValue(_trackKey, out var value) ? value : 0;

    private bool SaveTimingSettings()
    {
        // Timing calibration is committed immediately, rather than waiting for a
        // window-move debounce or for process exit.
        _saveTimer.Stop();
        _timingSaved = _store.Save(_settings);
        _presentationSignature = null;
        Render(); UpdateTray();
        _settingsWindow?.RefreshGlobalOffset(_timingSaved);
        AppLog.Write($"Timing: global={_settings.GlobalOffsetMilliseconds}ms; track={TrackOffset()}ms; total={EffectiveOffset()}ms; saved={_timingSaved}.");
        return _timingSaved;
    }

    private void SetGlobalOffset(int milliseconds)
    {
        _settings.GlobalOffsetMilliseconds = LyricsTimingOffsets.Sanitize(milliseconds);
        SaveTimingSettings();
    }

    private void PromoteTrackOffset()
    {
        if (_trackKey is null) return;
        var promotion = LyricsTimingOffsets.PromoteCurrentTrack(_settings.GlobalOffsetMilliseconds,
            _settings.TrackOffsetsMilliseconds, _trackKey);
        _settings.GlobalOffsetMilliseconds = promotion.GlobalMilliseconds;
        _settings.TrackOffsetsMilliseconds = promotion.TrackOffsets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        SaveTimingSettings();
    }
    private void AdjustOffset(int delta)
    {
        if (_trackKey is null) return;
        _settings.TrackOffsetsMilliseconds.TryGetValue(_trackKey, out var current);
        _settings.TrackOffsetsMilliseconds[_trackKey] = LyricsTimingOffsets.Sanitize(current + delta);
        _lastLine = int.MinValue;
        SaveTimingSettings();
    }
    private void ResetOffset()
    {
        if (_trackKey is not null) _settings.TrackOffsetsMilliseconds.Remove(_trackKey);
        _lastLine = int.MinValue;
        SaveTimingSettings();
    }
    private void UpdateTray()
    {
        _showItem.Checked = _settings.IsVisible;
        _lockItem.Checked = _settings.IsLocked;
        _plainItem.Enabled = !string.IsNullOrWhiteSpace(_lyrics?.PlainLyrics);
        _offsetItem.Enabled = _trackKey is not null;
        _offsetItem.Text = UiText.Format("This song only ({0:+0;-0;0} ms; total {1:+0;-0;0} ms)", TrackOffset(), EffectiveOffset());
        _globalOffsetItem.Text = UiText.Format("Global timing · all songs ({0:+0;-0;0} ms)", _settings.GlobalOffsetMilliseconds) + (_timingSaved ? "" : UiText.T(" · NOT SAVED"));
        _globalOffsetItem.ToolTipText = UiText.Format("Global {0:+0;-0;0} ms + this song {1:+0;-0;0} ms = {2:+0;-0;0} ms. Positive shows lyrics earlier. Saved across songs and restarts.", _settings.GlobalOffsetMilliseconds, TrackOffset(), EffectiveOffset());
        _promoteOffsetItem.Enabled = _trackKey is not null && TrackOffset() != 0;
        _startupItem.Checked = _settings.StartWithWindows;
        _translationItem.Checked = _settings.ShowTranslation;
        _pronunciationItem.Checked = _settings.ShowPronunciation;
        var status = LocalizeStatus(_displayLimitation ?? _status);
        _statusItem.Text = status.Length > 110 ? status[..107] + "…" : status;
        _statusItem.ToolTipText = LocalizeStatus(_status) + (_displayLimitation is null ? "" : " · " + UiText.T(_displayLimitation));
        _nativeStatusItem.Text = UiText.T("Native cache: ") + UiText.T(_nativeLookup?.Status.ToString() ?? "waiting");
        _nativeStatusItem.ToolTipText = UiText.IsChinese && _nativeLookup is not null
            ? UiText.T(_nativeLookup.Document is not null ? "Native lyrics matched by catalog ID and complete track metadata." : "Native cache unavailable or not safely matched; original lyrics may use LRCLIB.")
            : _nativeLookup?.Message ?? UiText.T("Reads Apple's existing cache without opening its lyrics panel.");
    }
    private static string LocalizeStatus(string status)
    {
        const string synchronized = "Synchronized lyrics · ";
        if (status.StartsWith(synchronized, StringComparison.Ordinal))
        {
            var source = status[synchronized.Length..];
            return UiText.T(synchronized) + (source.StartsWith("Local LRC: ", StringComparison.Ordinal)
                ? UiText.T("Local LRC: ") + source[11..] : UiText.T(source));
        }
        return UiText.T(status);
    }
    private void DisplaySettingsChanged()
    {
        _presentationSignature = null;
        ScheduleSave(); Render(); UpdateTray();
    }
    private void ScheduleSave() { if (_disposed) return; _saveTimer.Stop(); _saveTimer.Start(); }

    public void ShowSettings()
    {
        if (_disposed) return;
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new(_settings);
        _settingsWindow.GlobalOffsetApplied += SaveTimingSettings;
        _settingsWindow.SettingsApplied += () =>
        {
            _overlay.ApplySettings();
            _overlay.SetLocked(_settings.IsLocked);
            ApplyStartup();
            _lastLine = int.MinValue;
            _presentationSignature = null;
            SaveTimingSettings();
            if (_lyrics is null) BeginLookup();
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        AppLog.Write("Settings opened by explicit user activation.");
        _settingsWindow.ShowDialog();
    }

    private async Task ImportAsync()
    {
        var track = _track;
        var key = _trackKey;
        long? importVersion = null;
        if (track is null) { _status = "Play an Apple Music track before importing"; UpdateTray(); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = UiText.Format("Import lyrics for {0}", track.Title), Filter = UiText.T("Synchronized lyrics (*.lrc)|*.lrc"), CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        if (_disposed || _trackKey != key) return;
        try
        {
            _lyricsCancellation?.Cancel();
            _lookupVersion++;
            importVersion = _lookupVersion;
            _lookupRunning = false;
            _nativeRefreshTimer.Stop();
            _nativeRefreshPending = false;
            var result = await LocalLyricsImporter.ImportAsync(picker.FileName, track, _cache);
            if (_disposed || _trackKey != key || _lookupVersion != importVersion) return;
            _lyricsCancellation?.Cancel();
            _manualImport = true;
            _lyrics = result;
            _nativeLyrics = null;
            _presentationSignature = null;
            _lastLine = int.MinValue;
            _status = "Synchronized lyrics · " + result.Source;
            Render();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            if (_disposed || _trackKey != key || (importVersion.HasValue && _lookupVersion != importVersion.Value)) return;
            _status = "LRC import failed; check the file and try again"; AppLog.Write("LRC import: " + ex.Message);
        }
        if (!_disposed) UpdateTray();
    }

    private void ShowPlainLyrics()
    {
        if (string.IsNullOrWhiteSpace(_lyrics?.PlainLyrics)) return;
        new Window
        {
            Title = (_track?.Title ?? UiText.T("Lyrics")) + UiText.T(" · Untimed lyrics"),
            Width = 560, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBox { Text = _lyrics.PlainLyrics, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 18, Padding = new Thickness(24), BorderThickness = new Thickness(0) }
        }.Show();
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (_settings.StartWithWindows)
            {
                var exe = Environment.ProcessPath ?? throw new IOException("Cannot find application executable.");
                key.SetValue("AppleMusicDesktopLyrics", '"' + exe + "\" --data-dir \"" + _directory + '"');
            }
            else key.DeleteValue("AppleMusicDesktopLyrics", false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { _settings.StartWithWindows = false; _status = "Startup setting could not be saved"; AppLog.Write(ex.Message); }
    }

    private async Task CapturePreviewAsync(IntPtr foregroundBefore)
    {
        var wasLocked = _settings.IsLocked;
        _overlay.SetLocked(true);
        await Task.Delay(150);
        _overlay.UpdateLayout();
        var scale = VisualTreeHelper.GetDpi(_overlay);
        var image = new RenderTargetBitmap((int)Math.Ceiling(_overlay.ActualWidth * scale.DpiScaleX), (int)Math.Ceiling(_overlay.ActualHeight * scale.DpiScaleY), scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(_overlay);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(_directory, "overlay-preview.png"))) png.Save(file);
        var locked = WindowDiagnostics.Capture(_overlay);
        _overlay.SetLocked(false);
        await Task.Delay(150);
        var edit = WindowDiagnostics.Capture(_overlay);
        _overlay.SetLocked(wasLocked);
        File.WriteAllText(Path.Combine(_directory, "window-audit.json"), JsonSerializer.Serialize(new
        {
            foregroundBefore = $"0x{foregroundBefore.ToInt64():X}",
            foregroundAfter = $"0x{WindowDiagnostics.GetForegroundWindow().ToInt64():X}",
            foregroundUnchanged = foregroundBefore == WindowDiagnostics.GetForegroundWindow(),
            locked, edit,
            test = "Read-only Win32 style and hit-testing check; no clicks or external application input sent."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void WriteDiagnostics()
    {
        if (_settingsWindow is { IsVisible: true } settingsWindow)
        {
            settingsWindow.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(settingsWindow);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(settingsWindow.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(settingsWindow.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(settingsWindow);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(_directory, "settings-preview.png")); encoder.Save(file);
        }
        object[] MenuItems(Forms.ToolStripItemCollection items) => items.Cast<Forms.ToolStripItem>()
            .Where(item => item is not Forms.ToolStripSeparator)
            .Select(item => (object)new { text = item.Text, children = item is Forms.ToolStripMenuItem menu ? MenuItems(menu.DropDownItems) : [] }).ToArray();
        File.WriteAllText(Path.Combine(_directory, "ui-audit.json"), JsonSerializer.Serialize(new
        {
            language = UiText.Language, settingsTitle = _settingsWindow?.Title, settingsVisible = _settingsWindow?.IsVisible ?? false,
            tray = _tray.ContextMenuStrip is { } contextMenu ? MenuItems(contextMenu.Items) : []
        }, new JsonSerializerOptions { WriteIndented = true }));
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var position = _clock.CurrentPosition;
        File.WriteAllText(Path.Combine(_directory, "smoke-result.json"), JsonSerializer.Serialize(new
        {
            status = _status,
            track = _track,
            clock = _clock.State,
            positionSeconds = position.TotalSeconds,
            synchronizedLines = _lyrics?.Lines.Count ?? 0,
            source = _lyrics?.Source,
            nativeStatus = _nativeLookup?.Status.ToString(),
            nativeMessage = _nativeLookup?.Message,
            nativeCatalogId = _nativeLookup?.CatalogId,
            nativeTranslationLanguages = _nativeLyrics?.Translations.Select(item => item.Language).ToArray(),
            nativePronunciationLanguages = _nativeLyrics?.Pronunciations.Select(item => item.Language).ToArray(),
            showTranslation = _settings.ShowTranslation,
            showPronunciation = _settings.ShowPronunciation,
            selectedTranslationLanguage = _presentation?.TranslationLanguage,
            selectedPronunciationLanguage = _presentation?.PronunciationLanguage,
            displayedRows = _presentation?.Lines.Count ?? 2,
            displayedKinds = _presentation?.Lines.Select(item => item.Kind.ToString()).ToArray(),
            displayLimitation = _displayLimitation,
            window = WindowDiagnostics.Capture(_overlay),
            activeLineIndex = _lastLine,
            overlayVisible = _overlay.IsVisible,
            locked = _settings.IsLocked,
            effectiveOffsetMs = EffectiveOffset(),
            globalOffsetMs = _settings.GlobalOffsetMilliseconds,
            trackOffsetMs = TrackOffset(),
            timingSaved = _timingSaved,
            lastObservationQueueMs = _lastObservationQueueSeconds * 1000,
            lyricLookupPositionSeconds = position.TotalSeconds + EffectiveOffset() / 1000.0,
            dataDirectory = _directory,
            workingSetBytes = process.WorkingSet64,
            cpuSeconds = process.TotalProcessorTime.TotalSeconds,
            playbackEvents = _playbackEvents.ToArray(),
            automaticReliabilityVerdict = "Not assigned; timeline discontinuities may include provider corrections."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Explicit QA only: changes our own display flags briefly; never operates Apple Music.</summary>
    public async Task CaptureDisplayAuditAsync()
    {
        var translation = _settings.ShowTranslation;
        var pronunciation = _settings.ShowPronunciation;
        var observations = new System.Collections.Generic.List<object>();
        try
        {
            foreach (var mode in new[] { (false, false), (true, false), (false, true), (true, true), (false, false) })
            {
                _settings.ShowTranslation = mode.Item1;
                _settings.ShowPronunciation = mode.Item2;
                _presentationSignature = null;
                Render();
                await Task.Delay(250);
                _overlay.UpdateLayout();
                observations.Add(new
                {
                    showTranslation = mode.Item1, showPronunciation = mode.Item2,
                    trackKey = _trackKey, nativeCatalogId = _nativeLookup?.CatalogId,
                    rows = _presentation?.Lines.Count,
                    kinds = _presentation?.Lines.Select(line => line.Kind.ToString()).ToArray(),
                    limitation = _displayLimitation,
                    window = WindowDiagnostics.Capture(_overlay)
                });
            }
        }
        finally
        {
            _settings.ShowTranslation = translation;
            _settings.ShowPronunciation = pronunciation;
            _presentationSignature = null;
            Render(); UpdateTray();
        }
        await Task.Delay(250);
        _overlay.UpdateLayout();
        File.WriteAllText(Path.Combine(_directory, "display-audit.json"), JsonSerializer.Serialize(observations,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var background = new SolidBrush(System.Drawing.Color.FromArgb(246, 66, 100));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var stroke = new System.Drawing.Pen(System.Drawing.Color.White, 3) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        graphics.DrawLine(stroke, 8, 12, 24, 12); graphics.DrawLine(stroke, 10, 20, 22, 20);
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer.Stop(); _saveTimer.Stop(); _nativeRefreshTimer.Stop();
        _nativeMonitor.Dispose();
        _lyricsCancellation?.Cancel();
        _nativeProbeCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _observer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.Save(_settings);
        _settingsWindow?.Close();
        _overlay.Close();
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _http.Dispose();
        AppLog.Write("Stopped.");
    }
}
