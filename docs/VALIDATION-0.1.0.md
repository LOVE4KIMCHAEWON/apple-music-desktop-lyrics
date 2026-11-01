# Validation — 0.1.0

Date: 1 November 2026, Australia/Sydney. Results below distinguish live observation, simulated tests, passive Win32 audits, and manual checks still pending.

## Automated core tests

**70/70 passed**, .NET 8 on this Windows x64 machine, with zero warnings. Tests cover:

- LRC decimal precision, multiple timestamps, signed file offsets, malformed timestamps, duplicate lines, Unicode, untimed input.
- Apple metadata normalization, stable identity keys, conservative title/artist/version ranking, live/remix/cover/alternate and CJK markers, synchronized preference.
- GET parameters and approved User-Agent; search fallback; timeout versus caller cancellation; HTTP 429 delta/date `Retry-After`; 503 fallback without bypassing cooldown; malformed and oversized/chunked responses.
- Cache roundtrip, imported Unicode LRC, offline reuse, invalid JSON, size bounds, online quota/oldest eviction, retained imports and legacy files.
- Monotonic interpolation, forward/backward seeks, seeks while paused, stale pause/resume reports, transition settling, playback rate, duration clamping, missing timelines, reconnects.
- Binary active/upcoming selection and per-track/global offsets with JSON persistence.

The diagnostic smoke suite passed against the self-contained probe. It checks script syntax, argument handling, read-only defaults, complete parseable logs, monotonic ordering, honest reliability status, and existing-log preservation. The portable discovery/launcher/summarizer path ran successfully.

## Live Windows and Apple Music

Windows 10 build 19045, x64, primary display 1920×1080 at 125% scaling; Apple Music 1.1792.39681.0.

| Check | Evidence |
| --- | --- |
| All-session discovery | AppleMusic.exe and cloudmusic.exe enumerated independently. Apple identified from discovered installed package identity; other player excluded. |
| Playback timeline | Advancing raw positions and approximately one-second position updates; duplicate timeline callbacks observed. |
| Pause/resume | Probe observed Paused/Playing events and a stable 2.994-second paused interval. A later published probe repeated pause/resume observation. |
| Natural song change | Metadata changed from Right Now to Hurt; timeline reset and resumed. Non-atomic old-duration/invalid/new-duration stages recorded. |
| Real LRCLIB provider | Cool With You request found 39 synchronized lines from record #2763685. Exact GET returned 503; Retry-After was respected and search returned a strict match. |
| App end-to-end path | App followed the current Apple Music track Hype Boy, normalized its artist/album, fetched 48 synchronized lines from LRCLIB #17603381, and displayed a locked overlay. At the diagnostic snapshot its clock was 24.606 seconds, selected index 2, valid timeline true, and overlay visible true. |
| Shutdown during pending lookup | After fixing dispatcher-context disposal, an 8-second live smoke run exited normally while a lyric request was pending; log ends with Stopped. |
| Final portable live run | The self-contained bundle ran for 60 seconds, loaded 48 synchronized lines for How Sweet from LRCLIB #10626113, displayed selected index 30 at 153.509 seconds, and exited normally. |

No audible timing accuracy or real seek success is inferred from these observations. The main application never sends playback or seek commands.

## Overlay render and native audit

The explicit preview used original demonstration text with Korean, Chinese, and Japanese. A rendered PNG showed exactly two centered rows, restrained shadow, and no locked toolbar/frame.

At 125% scaling the fresh locked window measured left 335, right 1585 (center 960), top 832, bottom 962 on a 1920-pixel-wide primary monitor with work-area bottom 1030. This verifies initial centering and a 68-physical-pixel bottom margin, approximately 54 DIP.

Passive Win32 measurements confirmed:

- Topmost, layered, toolwindow, and no-activate extended styles.
- `WS_EX_TRANSPARENT` present while locked and absent while editing.
- All four rendered corner alpha values were zero.
- `WindowFromPoint` at an opaque lyric pixel returned an underlying/no-window result while locked and the overlay HWND while editing.

No mouse clicks were sent. `GetForegroundWindow` returned zero before and after, so this environment does **not** establish interactive focus preservation by that equality. Manual clicking, dragging, tray recovery, and keyboard-focus checks remain useful acceptance tests.

## Performance scope

The engine selects lyrics with binary search and renders only when the line changes. Its 100 ms local position timer runs only for visible synchronized playback. Media fallback checks are every five seconds; metadata fallback refresh is every fifteen seconds. Network lookup occurs on track changes or explicit retry, with cancellation and bounded cache/log growth.

One 25-second cold live run measured approximately 155 MiB working set (including shared .NET/WPF pages) and 1.80 CPU seconds including startup and lookup. This is not an all-day resource benchmark. A long idle/playback soak test remains pending; no stronger resource claim is made.

## Evidence paths in this workspace

- `diagnostics/transition-check.jsonl` and `transition-findings.md`
- `diagnostics/song-change-check.jsonl` and `song-change-findings.md`
- `diagnostics/final-published-validation.jsonl`
- `diagnostics/live-app/app.log` and `smoke-result.json`
- `diagnostics/window-audit-fixed/window-audit.json` and `overlay-preview.png`
- `diagnostics/shutdown-check/app.log`
- `diagnostics/portable-live-qa/app.log` and `smoke-result.json`
- `diagnostics/portable-window-qa/window-audit.json`

Diagnostic data and listening metadata are excluded from source control and source archives.

## Pending manual acceptance

Real Apple Music forward/backward seeking, repeated rapid navigation, previous-track actions, app restart/reconnect, a competing player becoming current, interactive tray/settings/drag/focus checks, opt-in startup, Windows 11, mixed-DPI monitors and monitor removal remain unverified. Simulated tests and implementation are complete for these modeled cases; they are not a substitute for hardware/user-session tests.

The user explicitly directed development to continue while real seeking validation is deferred. To verify later, run `Run-Probe.cmd -Seconds 120`, seek forward/backward inside Apple Music, and inspect timestamped timeline discontinuities with the summarizer. Remote seek command support is unnecessary.
