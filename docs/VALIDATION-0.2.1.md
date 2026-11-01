# Validation — 0.2.1 synchronization update

Date: 1 November 2026, Australia/Sydney. Windows 10 build 19045 x64; Apple Music 1.1792.39681.0. Previous release archives remain unchanged.

## Scope

Translation and pronunciation remain independent app preferences by explicit user choice. Detecting or synchronizing Apple's visibility controls is outside scope; no work was performed on it in this update.

The user reported approximately 1750–2000 ms of late lyrics and a correction that did not follow song changes. Read-only inspection of this app's saved profile showed global 0 and three per-song corrections: 1750, 2000 and 1750 ms. The older tray exposed per-song adjustment; global storage already existed. This release makes global calibration explicit, immediate and persistent.

## Implemented changes

- Separate **Global timing · all songs** and **This song only** tray menus, with numeric global Settings controls, ±250 ms steps, +1750/+2000 presets and independent resets.
- Global timing writes JSON immediately. Failed persistence is reported in Settings and the tray.
- Promotion of a corrected song's total into global rebases saved entries. Existing 1750/2000/1750 totals become global 1750 with only a +250 residual on the second song, avoiding double application.
- Both native and LRC lookup share `LyricsTimingOffsets`. Playback itself and all cached timestamps remain unchanged.
- Monotonic observation capture timestamps now survive UI queuing. Delayed playing advances correctly, pause freezes at capture, resume excludes paused time, and obsolete observations are ignored by both the clock and controller.
- Visible playback selection runs every 50 ms. Text is shown at full opacity immediately, removing the old 140 ms fade.

The queue-age defect can add delay when the UI is busy. It was reproduced in regression tests; it is not established as the universal cause of the reported two-second discrepancy. Actual audio alignment still requires listening after calibration.

## Automated tests

**235/235 passed**, zero failures or skips. Application and probe builds completed with zero warnings and zero errors. The 185 previous tests were retained.

New cases cover delayed playing/pause/resume observations, playback rates, unchanged timeline timestamps, false-seek prevention, forward/backward recalibration, settling, obsolete/future/invalid timestamps, exact +1750/+2000 selection boundaries in native/LRC formats, all four aligned layouts, A→B→A global retention, actual SettingsStore save/reload, save failure, per-song resets, additive corrections, promotion/rebasing, nonmutation and bounds.

Restore used already cached dependencies with `-Offline`; network vulnerability auditing was unavailable. The self-contained probe passed its smoke suite (30 checks in that capture), including argument validation, read-only Windows session capture, monotonic logs and preservation of existing evidence.

## Actual Windows integration

The self-contained 0.2.1 EXE matched live **Hurt** to native catalog `1635470002`, with 51 original lines. Apple Music's lyrics panel was left untouched. Test instances used hidden overlays to avoid duplicating the user's working display.

| Scenario | Observed result |
| --- | --- |
| +1750 global, no song correction | Playback 79.3248394 s; lyric lookup 81.0748394 s: exactly +1.750 s. Native source matched. |
| Restart with same profile | Global/effective remained +1750 ms; native Hurt matched again; lookup delta remained +1750 ms. |
| +2000 global | Native Hurt matched; global/effective +2000 ms and lookup delta exactly +2.000 s. |
| Four layouts at +1750 | 2/2/2/3 rows with the matched native catalog and no supplemental-data limitation. |
| Idle/restart | Two isolated launches retained +1750 even without an active Apple session. |

The first live +1750 run measured about 0.154 ms between capture and UI processing at its final observation. That sample does not support claiming that this run had a two-second queue delay; synthetic delayed-dispatch tests verify that specific correction.

## Personal profile and release safety

An isolated copy of the existing personal settings was calibrated to global +1750. Original per-song totals were verified unchanged after rebasing; only one +250 residual remains. Translation/pronunciation choices and appearance/position preferences are preserved. The original working profile is not modified.

Personal settings and native/cache diagnostics are excluded from both source and portable release archives. The local 0.2.1 portable directory may receive the calibrated `data/settings.json` after packaging for convenient use on this PC; clean ZIP installs retain default global 0 and provide the explicit calibration controls.

The v0.1.0 and v0.2.0 archives remain rollback points. Publishing uses `artifacts/apple-music-desktop-lyrics-0.2.1-win-x64`; source/portable ZIP names and checksums are versioned. The packaging script excludes personal `data`, diagnostics and logs.

## Tested versus pending

| Feature | Status |
| --- | --- |
| Capture-time interpolation, queued pause/resume, seeking model | Implemented and tested |
| Persistent global offset, additive song corrections, promotion/rebasing | Implemented and tested |
| Native/LRC offset sign and single application | Implemented and tested |
| Windows native playback with +1750/+2000 and restart | Implemented and tested |
| Listening-based elimination of the reported delay | Implemented but unverified |
| Physical Settings/tray interaction and full rapid-navigation stress | Implemented but unverified |
| Mixed-DPI hardware, display removal and actual focus/click-through | Implemented but unverified; unchanged scope |

## Manual check

1. Exit the older overlay and launch the new portable instance with the calibrated profile. Play Hurt or another cached song with Apple's panel closed.
2. Listen with global +1750. If still about 250 ms late, choose +2000. Positive advances lyrics. Check the separate per-song contribution to avoid double compensation.
3. Switch songs, pause/resume and seek both ways. Confirm the global value stays unchanged and original/supplemental lines move together.
4. Exit and restart using the same `data` directory. Verify the global setting, independent display preferences and any residual song correction remain saved.

Private evidence: `diagnostics/v0.2.1/global-restart`, `portable-live`, `global-2000`, and `calibrated-profile`. It is excluded from release archives. Historical native-cache/display validation remains in `VALIDATION-0.2.0.md`.
