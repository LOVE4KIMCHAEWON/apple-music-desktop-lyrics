# Synchronization and global timing — 0.2.1

Translation and pronunciation remain independent app preferences. No detection or synchronization of Apple's visibility switches is planned.

## Calibrate all songs

If lyrics are about 1.75–2 seconds late, start with **Global timing · all songs → Set +1750 ms**. Use +2000 if the display is still late. Positive values advance lyric selection; negative values delay it. Settings provides the same presets, ±250 ms controls and a numeric **Apply global offset** button. These actions immediately update the overlay and save JSON; no restart or network request is required.

The global value applies even when no song is playing, persists across songs and survives restarting with the same data directory. The app reports a failed save instead of silently claiming persistence. A different portable `data` folder is a different preference profile.

The correction affects lyric lookup only:

```text
lookup position = estimated playback position + global offset + this song's correction
```

It never changes Apple Music playback, cached timestamps, original/supplemental alignment, or pronunciation/translation text. All four layouts share the same corrected selection time.

## Keep existing per-song calibration

The older tray adjustment affected only the current song. A profile with global 0 and individual +1750/+2000 values consequently lost its correction on other songs.

On a song you have already corrected, choose **Use this song's total for all songs**. The app promotes that song's effective correction to global and rebases existing entries to preserve their total corrections. Example:

| Setting | Before | After promoting song A |
| --- | --- | --- |
| Global | 0 | +1750 |
| Song A correction | +1750 | 0 (entry removed) |
| Song B correction | +2000 | +250 |
| Song C correction | +1750 | 0 (entry removed) |
| Any new song's total | 0 | +1750 |

Existing totals remain 1750/2000/1750. Each component is limited to ±60000 ms; extreme promoted values may saturate at those bounds. Ordinary global presets are additive and do not rebase per-song entries. Resetting a song retains global; resetting global retains individual corrections.

## Timing correction in this release

Before 0.2.1, timeline age was calculated before dispatcher scheduling, then anchored to the later UI execution time. A queued observation could therefore make the display late by the queue wait. The fix carries monotonic capture time into the playback clock:

- Playing observations advance through the queue interval.
- Delayed pause freezes at observation time rather than UI execution time.
- Resume advances only the interval since resume, excluding paused time.
- Old observations cannot restore an older track, status or position.
- Forward/backward seek comparisons and transition settling use observation time.

The render interval is 50 ms while visible playback is active. New lyric text appears immediately at full opacity; the previous 140 ms fade is removed. This reduces avoidable presentation latency, but neither change proves that all reported 1750–2000 ms discrepancies came from queueing. A persistent user calibration remains appropriate when the player's reported position and supplied lyric timing differ from audible playback.

Read-only cache investigation found native `lyricOffset` values of 0.046–0.715 in ten of eighteen sampled payloads. Units/sign remain undocumented, and these values do not establish a universal two-second correction. They remain preserved without guessing their semantics. No audio, credentials or DRM data was accessed.

## Verification

Automated tests cover +1750/+2000 boundaries for native and LRC lyrics, aligned supplemental lines in every layout, A→B→A song changes, additive/reset behavior, promotion without double application, actual settings-store round trips, save failures, delayed playing/pause/resume observations, seek recalibration and obsolete observations.

Live diagnostics expose `globalOffsetMs`, `trackOffsetMs`, `effectiveOffsetMs`, `timingSaved`, `lastObservationQueueMs`, playback position and corrected lyric lookup position. Run a separate instance with `--data-dir <QA directory> --smoke-seconds 15 --display-audit` to capture these without operating Apple Music.

Manual check: apply a global preset, switch through several songs, pause/resume/seek, then restart using the same profile. The global value should remain unchanged and lyrics should follow the corrected time. Listen to confirm the actual delay is resolved; API positions and automated tests cannot establish audible accuracy alone.
