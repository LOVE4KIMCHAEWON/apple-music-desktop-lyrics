# Validation — 0.2.0

Date: 1 November 2026, Australia/Sydney. Actual tests used Windows 10 build 19045 x64, Apple Music 1.1792.39681.0, and a 1920×1080 primary display at 125% scaling. Simulations are distinguished from physical hardware tests below.

## Automated checks

**185/185 tests passed**, retaining all 70 previous tests. Release application and probe builds completed with zero warnings and zero errors.

The self-contained portable EXE ran successfully against live Hurt and passed all four display combinations. Its standalone probe passed 33 smoke checks, including read-only capture, CLI validation, monotonic logging and preservation of existing evidence.

New tests cover native JSON/TTML, safe XML and bounds, timestamp precision, declared frame/subframe timing, retained language/timing attributes and spans, explicit-key alignment, duplicate/ambiguous references, the four display combinations, missing supplements, language selection, unique catalog matching, complete metadata, recording versions, duration discrepancies, conflicting editions/payloads, hidden files, incomplete writes, read-only file preservation, cancellation, settings migration/persistence and placement arithmetic.

Placement simulations cover 1080p/1440p/4K at 100/125/150/200%, negative monitor origins, custom positions, lost-monitor coordinates, oversized windows and upward three-row expansion. They do not verify Windows display events or mixed-DPI rendering.

Normal NuGet restore failed because its vulnerability service was unreachable (NU1900). Successful validation used `Build.ps1 -Offline`, with cached dependencies and network auditing disabled for that restore only. A vulnerability audit did not complete.

## Feature checklist

| Feature | Status | Evidence and limit |
| --- | --- | --- |
| Native original/translation/pronunciation parsing | Implemented and tested | 13 actual cache payloads parsed: 753 original entries, 753 translation entries and 753 pronunciation entries. Source fixtures contain synthetic text only. |
| Native catalog matching | Implemented and tested | Same-ID cached song metadata, exact normalized title/artist/album, compatible versions and duration within 3 seconds. Live Attention and Cookie matched; ambiguous/conflicting cases rejected in tests. |
| Four independent display combinations | Implemented and tested | Live data produced Original+Upcoming, Original+Translation, Original+Pronunciation and Original+Pronunciation+Translation. |
| Language selection and timestamp/key alignment | Implemented and tested | Multilingual/key-order tests and actual zh-Hans/ko-Latn selection. Other languages/timing remain preserved. |
| Upward three-line expansion | Implemented and tested | Live two-row top 832 px, three-row top 784 px; bottom remained 962 px. |
| Flags/languages/offsets persistence and old settings migration | Implemented and tested | Actual settings/store code tested; JSON round trips and restart measurements. |
| Default/custom placement and restart on this display | Implemented and tested | Default center x=960 px. Custom three-row bounds repeated across two launches: left 400, top 622, right 1650, bottom 800 px. |
| Monitor selection, display-change recovery and mixed DPI | Implemented but unverified | Code and geometry simulations pass; secondary displays, live resolution changes and monitor removal were not exercised. |
| Transparency and locked Win32 styles | Implemented and tested | Layered/topmost/toolwindow/no-activate styles; transparent corners; passive hit testing changes between locked/edit. |
| Actual mouse click-through and focus preservation | Implemented but unverified | No mouse input sent. Foreground handles were zero; equality does not prove focus preservation. |
| Physical tray/Settings interaction | Implemented but unverified | Controls compile; mode updates exercised through the explicit app audit rather than physical menu clicks. |
| Pause/resume and timeline repositioning observation | Implemented and tested | Actual capture recorded pause/resume, 30.027 seconds at an unchanged paused position, and forward/backward discontinuities during the user's exercise. |
| Audible timing and repeated seek/scrub reliability | Implemented but unverified | Dragging can briefly produce intermediate/previous positions. No listening-based timing judgment or broad reliability verdict assigned. |
| Full-app rapid track changes and obsolete-request cancellation | Implemented but unverified | Guards and provider cancellation tests exist; probe observed Attention→Hype Boy. Rapid navigation stress remains pending. |
| LRCLIB original-only fallback | Implemented and tested | Existing HTTP/cache/timeout/429/cancellation tests pass. This run's real network lookup failed; a v0.2.0 live-network PASS is not claimed. |
| Follow Apple's own translation/pronunciation visibility settings | Not implemented | This accessibility capture path did not expose their state. The overlay uses its own independent controls. |
| Automatically populate Apple's missing cache | Not implemented | The utility does not open the panel, fetch authenticated native lyrics or modify Apple files. |
| Generate replacement translation or romanization | Not implemented | Deliberately excluded. |

## Native Windows integration

The provider reads hidden/system buckets under the installed Apple Music package's `AC\INetCache`, restricted to observed lyric filenames and allowlisted public catalog JSON. It does not read cookies, private databases, tokens, audio or DRM data. Reads are capped at 4 MiB per file and 64 MiB total, with bounded directory/file traversal. Reparse points and incomplete scans are rejected. No Apple file is written, renamed or deleted.

Catalog `resources.songs[ID].attributes` supplies title, artist, album and duration. A unique strong metadata match identifies a catalog ID that must agree with the lyric resource. Recency and lyric timing are never identity evidence. Metadata is not cryptographic proof of recording identity; indistinguishable cached editions are rejected.

- **Attention**, live: catalog `1635469694`, 68 originals, zh-Hans translation, ko-Latn pronunciation; all four combinations passed.
- **Cookie**, live: catalog `1635469854`, 89 originals; final adaptive-position audit passed all four combinations with the same 962-pixel bottom edge.
- **Hurt**, final self-contained portable live run: catalog `1635470002`, 51 originals, zh-Hans/ko-Latn; all four display combinations passed at the same bottom anchor. Live SMTC duration was 176 seconds versus catalog 177.947 seconds. Regression tests cover this discrepancy; delivered tolerance is 3 seconds with other identity checks unchanged.
- **GODS**, direct actual-cache provider check: catalog `1833057828`, 57 originals, zh-Hans/ja-Kana. Unexpected Apple language metadata is preserved. This was not a live-playing test.
- **HOT**, user-confirmed playing with Apple's panel closed: SMTC reported a 143-second timeline. Catalog metadata uniquely matched `1799278617`, but no matching supported native lyric payload was present. Before/after lyric inventories remained at 13 files during the initial passive capture.

HOT establishes a cache miss during the observed window. It does not establish why the payload was absent or whether opening the panel is always required. Existing cached lyrics are usable without this utility opening the panel. Fresh-track cache population with the panel closed remains unresolved; LRCLIB can provide originals when available.

The real LRCLIB HTTP request was blocked by the validation environment's refused proxy at 127.0.0.1:9. This is not evidence of a LRCLIB service outage. Automated HTTP/error/cache tests passed; real networking must be checked in the user's ordinary desktop environment.

Supplemental `text/@for` joins original `p/@itunes:key`, including out-of-order entries. Raw TTML retains all language/timing metadata, including unknown offset attributes whose semantics are not applied. Some Apple metadata says `automaticallyCreated="true"`; Apple-supplied does not necessarily mean human authored.

## Playback observations

A 120-second read-only capture produced 675 valid records, zero parse errors and zero dropped events. It recorded 331 usable Apple metadata/timeline snapshots, one pause transition, one resume transition and one track metadata change. A paused position remained unchanged for 30.027 seconds across 34 snapshots. The capture identified backward paused repositioning and a forward playing discontinuity. These are observed timeline changes, not remote-control command results; no commands were sent.

The native overlay app also ran during the exercise, logged discontinuity candidates and resynchronized selection. Its final native Attention selection was index 55 at approximately 145.694 seconds. This does not certify every intermediate position while dragging, audible alignment, or repeated-run reliability. Diagnostics assign no automatic reliability PASS.

## Window and performance checks

Final live placement: left 335, right 1585, center 960 pixels. Two-row top/bottom: 832/962. Three-row top/bottom: 784/962. Working-area bottom: 1030. Two custom-position restarts retained left 400 and bottom 800 with three rows.

An original demonstration-text preview was visually checked for three single-line rows and CJK rendering. Locked corner alpha was zero. Locked extended style was `0x80800A8`, edit style `0x8080088`; passive WindowFromPoint returned the overlay only in edit mode. No physical clicks were sent. Foreground handles were zero, so focus remains unverified.

One 90-second native run used approximately 177 MiB working set and 3.375 CPU seconds including startup, discovery and lookup. This is not an all-day benchmark; soak testing remains pending.

## Release preservation

The working directory is the v0.2.0 development copy. Existing v0.1.0 source/portable archives and its published directory remain the rollback snapshot; no Git repository existed. New publishing uses `artifacts/apple-music-desktop-lyrics-0.2.0-win-x64`.

Preserved baseline SHA-256:

```text
AppleMusicDesktopLyrics-0.1.0-win-x64.zip
FF2D5413C269BF914EEEAE27922564D6E18EA9B37AE6B874220BAC9AFB290A5D
AppleMusicDesktopLyrics-0.1.0-source.zip
AB658CC520C31F1D3C1222E4966518439CDEE9D4801E5398141F163ACB4836C5
```

The published v0.1.0 EXE/DLL hashes are checked separately. Release source archives exclude diagnostics, caches, SDKs, credentials, tokens and build output. Historical validation remains in `VALIDATION-0.1.0.md`.

## Manual acceptance

1. With Apple's panel closed, play a known cached song. Toggle our settings through off/off, on/off, off/on and on/on; expect 2/2/2/3 rows. Try available/unavailable language tags; missing data must report a limitation and show original/current-next.
2. Pause, resume, seek both directions and listen for timing. Repeat while paused and dragging. Run `Run-Probe.cmd -Seconds 120` for evidence. Remote seeking support is unnecessary.
3. Click a harmless underlying button through the locked overlay. Type in an editor while lyrics update; focus should remain there. Unlock/drag/relock and recover through tray Reset.
4. Restart with both flags and a custom position. Verify preferences, offsets and bottom anchor; repeat 3→2→3 rows.
5. In Default mode select a monitor and change resolution/scaling; expect bottom-center. Custom mode should preserve its location where visible. Exercise 1080p/1440p/4K, 100/125/150/200%, mixed DPI and monitor removal.
6. Rapidly change tracks and restart Apple Music. Old lyrics must clear; ambiguous sheets must not appear. Verify cancellation, late native arrivals and network-failure recovery.
7. For fresh-cache testing, inventory before playing an uncached track with Apple's panel closed. Do not delete its cache. Any panel-open comparison is a separate user action; this utility never opens it.

Private evidence under `diagnostics/v0.2-native/` includes inventories, HOT capture, four-mode audits, acceptance JSONL/reports, custom restart snapshots and preview/native-window audits. It is excluded from releases.
