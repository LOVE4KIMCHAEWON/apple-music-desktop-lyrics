# Phase 1 feasibility findings

Test date: 1 November 2026, Australia/Sydney. These are observations from this Windows desktop, not results inferred from documentation or reference applications.

## Decision

**Feasibility established for metadata and timeline observation; real seeking remains unverified.** The official Apple Music desktop app exposes usable track titles, playback state, duration, position and events through Windows GSMTC. Pause/resume and a natural song change were observed. Remote seeking is advertised as unavailable; the application only needs to observe seeks, not control them. The user explicitly directed development to continue with simulated seek tests and defer the manual forward/backward seek check.

The probe, installed-app discovery, evidence summarizer, smoke checks and self-contained Windows x64 diagnostic are implemented. Subsequent lyrics provider, playback engine, WPF overlay and tray implementation proceeded after that explicit instruction. See `VALIDATION.md` for later results.

## Actual environment

| Item | Observed value |
| --- | --- |
| Windows | Windows 10 x64, build 19045 |
| User session | Interactive session 1; probe and Apple Music in the same session |
| SDK | .NET 8.0.427, project-local SDK |
| Initial probe runtime | .NET 8.0.21 |
| Self-contained published runtime | .NET 8.0.33 |
| Installed Apple Music package | AppleInc.AppleMusicWin, version 1.1792.39681.0 |
| Discovered packaged AUMIDs | AppleInc.AppleMusicWin_nzyj5cx40ttqa!App and !SharedHelper |
| Actual GSMTC source | **AppleMusic.exe** |
| Other session | cloudmusic.exe, independently classified as non-Apple |

Package names above are recorded evidence, not constants embedded in the implementation. Discovery used the current user's Start-menu entries and installed manifests. Bare-executable fallback correlated the exact executable name with a running process whose package family matched that discovery. This is corroborating identity evidence: GSMTC supplies no direct session PID mapping. `GetCurrentSession()` was only logged.

## Observations

| Scenario | Result and practical implication |
| --- | --- |
| Session enumeration | Two sessions found through `RequestAsync()` and `GetSessions()`. Apple Music selected independently of the other player. |
| Track metadata | Titles and Unicode text were readable. `AlbumTitle` was empty. `Artist` and `AlbumArtist` combined artist and album, e.g. `NewJeans — Supernatural - Single`. The future provider must preserve raw metadata and handle this cautiously. |
| Duration | Valid start/end ranges yielded 156, 160, 176 and 220-second tracks in captures. Duration is derived from the timeline, not metadata. |
| Position | Raw Apple Music position advanced in approximately one-second steps. Duplicate timeline events were common; the final overlay should interpolate with a monotonic clock between meaningful updates. |
| Pause | `PlaybackInfoChanged` reported Paused. A subsequent timeline update settled at 7 seconds, then remained exactly stable for 2.994 seconds across six readings. |
| Resume | `PlaybackInfoChanged` reported Playing and advancing timeline updates resumed. Status can arrive before its corresponding fresh timeline. |
| Natural track change | `MediaPropertiesChanged` observed `Right Now` becoming `Hurt`, followed by reset/advancing timeline data for the new track. |
| Track transition consistency | Metadata and timeline are not atomic: `Hurt` briefly appeared with the preceding 160-second duration, then a zero/invalid timeline, before settling at 176 seconds. The final engine must tolerate this ordering. |
| Remote seeking | `IsPlaybackPositionEnabled` was false. The optional exercise correctly skipped remote seek commands. This says nothing conclusive about events caused by a manual seek in Apple Music. |
| Manual seek | **Not yet verified.** No qualifying forward/backward discontinuity was observed after excluding pause/resume/track-transition noise. |
| API failures/event loss | No API errors or dropped events in the completed 150-second transition capture or 95-second song-change capture. |

## Local evidence

Paths are relative to the source directory. Diagnostic logs are ignored by source control because they include personal listening metadata.

- `diagnostics/apple-music-discovery.json`: discovered app identities and installed version.
- `diagnostics/initial-live-probe.jsonl`: initial enumeration exposing the packaged-AUMID versus executable-source mismatch.
- `diagnostics/transition-check.jsonl`: 150-second capture; 445 Apple snapshots, 288 timeline events, four playback-info events, one pause and one resume; no dropped events or API errors.
- `diagnostics/transition-findings.md`: readable transition evidence, including the stable paused interval.
- `diagnostics/song-change-check.jsonl`: 95-second capture; 289 Apple snapshots, two metadata events, one observed track change; no dropped events or API errors.
- `diagnostics/song-change-findings.md`: readable song-change evidence, retaining transient old/new durations.
- `diagnostics/final-published-validation.jsonl`: a 25-second capture using the self-contained executable; 76 usable Apple snapshots and no dropped events or API errors.

The published executable completed the CLI/live-capture smoke suite. Checks covered script syntax, argument validation, bounded sampling, default read-only behavior, parseable complete JSONL, monotonic log ordering, explicit unverified reliability status, and preservation of existing evidence files. A separate summarizer fixture confirmed that stable-playing forward/backward jumps are reported as candidates and that resume noise is excluded. These checks do not replace a manual Apple Music seek test.

## Remaining feasibility checks

Run `scripts\Run-Probe.ps1 -Seconds 120`, manually seek forward and backward by at least ten seconds, and inspect the raw timeline plus `TimelinePropertiesChanged` records with `scripts\Summarize-Probe.ps1`. A disabled remote seek command does not prevent this observational test.

Next/previous button actions, Apple Music closing/reopening, another player becoming current, repeated seek runs, Windows 11, mixed-DPI monitors and the eventual overlay's click-through behavior remain untested. Natural track progression is evidence of metadata change detection, not proof of every navigation/reconnection scenario.

## Implementation consequences after the gate

Use installed-app discovery plus the observed executable/package fallback; coalesce duplicate events; separate provider-reported time from monotonic interpolation; reset anchors on status/track/session changes; wait for a valid new-track timeline; cancel obsolete lyric requests. Preserve missing/combined metadata for conservative LRCLIB matching. Plain lyrics must remain untimed.

## Primary API references

- [GSMTC manager and session enumeration](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Session timeline timestamps and bounds](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties)
- [Installed-app AUMID discovery](https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid)
