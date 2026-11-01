# Native Apple Music lyrics — 0.2.1

The provider reads existing Apple Music for Windows cache files. It does not contact authenticated Apple endpoints, open Apple Music's lyrics panel, or operate its controls. Original lyrics, translations, and pronunciation remain Apple-supplied data. LRCLIB is a fallback for original lyrics only.

## Read-only discovery and parsing

The current Windows installation stores native lyric JSON below:

```text
%LOCALAPPDATA%\Packages\AppleInc.AppleMusicWin_*\AC\INetCache\
```

Cache buckets may be hidden/system directories. Discovery enumerates the installed package-family directory pattern and allowlisted JSON filenames, skips reparse points, and applies file/directory/read-size bounds. Native lyric files currently use `syllable-lyrics[...].json`. Identity metadata is read only from allowlisted public catalog JSON; unrelated cache files, credential stores, audio, and DRM data are not inspected. Files are opened for reading with sharing enabled; files that change during a read are rejected. Nothing is written to Apple's cache.

Observed lyric payload structure:

```text
data[]
  id, type
  attributes
    playParams: catalogId, id, kind, displayType
    ttmlLocalizations: TTML XML string
```

The typed model preserves catalog/resource/play-parameter identifiers, all language groups, original line intervals, supplemental intervals, nested timed spans, namespaced attributes, and the complete original TTML. It preserves unknown metadata rather than inventing meanings for it. Unknown Apple lyric-offset attributes are not automatically applied; the app's documented global/per-track timing offset is applied once during selection.

Original lines use `p/@begin`, optional `@end`, and `p/@itunes:key`. Supplemental translation/transliteration `text/@for` refers to that key. Source row order is not an alignment mechanism: supplemental rows can be in a different order. A translation without timing remains untimed supplemental text associated with its original line; no new timestamps are fabricated. Pronunciation spans retain any timing Apple provided.

The XML parser disables DTD/external-entity resolution and bounds payload size/complexity. Broken references, duplicate original keys, duplicate supplemental references, ambiguous attributes, invalid intervals, unsupported sequential timing, and malformed/incomplete JSON/XML are rejected. The implementation handles the observed format, not every TTML dialect. `displayType` is retained as metadata and is not interpreted as a translation/pronunciation visibility flag.

## Binding lyrics to the playing track

Windows SMTC provides the current track's title, artist, album, duration, and playback timeline; it does not provide a documented native lyric payload or the catalog ID used by these cache files.

The provider builds a conservative bridge from existing public song metadata in Apple's cache:

1. Require complete playing title, artist, album, and a valid duration.
2. Find a unique cached catalog identity whose normalized title, artist, and album match exactly, whose recording-version markers agree, and whose duration differs by no more than three seconds. The tolerance accommodates observed whole-second SMTC duration reporting.
3. Require the lyric payload's catalog/resource identity to agree with that catalog ID. `playParams.id` is preserved separately and is not assumed interchangeable with `catalogId`.
4. Reject contradictory same-ID metadata, multiple matching IDs, or differing TTML payloads for that ID. If identity discovery is incomplete, do not select from partial results.

File recency, directory order, filename numbers alone, and approximate lyric length are not track identity evidence. Strict matching prefers no native result over a doubtful result. It cannot establish more identity information than Apple exposes through cached metadata; an unseen ambiguity outside the available cache cannot be independently ruled out.

Track changes cancel obsolete work and clear the prior presentation. The app watches relevant cache changes with a debounce, so a late native payload can be applied after validation without repeatedly polling the network. A validated late result can supersede a pending LRCLIB lookup. Missing/incomplete/ambiguous native data permits original-only fallback; it does not cause translation generation. An explicit local LRC import overrides the current track until track change or Retry lyric lookup.

## Independent display settings

The overlay uses its own independent settings in the tray and Settings. Detecting or following Apple's visibility controls is outside scope by user preference:

| ShowTranslation | ShowPronunciation | Rows, in order |
| --- | --- | --- |
| false | false | Current original; next original |
| true | false | Current original; current translation |
| false | true | Current original; current pronunciation |
| true | true | Current original; current pronunciation; current translation |

The third row expands upward from the saved bottom edge. Missing requested line/language data causes a current/next-original fallback and an explicit tray limitation. No previous/next line's supplemental text is substituted.

Language tags remain intact. Empty preferences choose English translations and Latin-script pronunciation first, then a stable tag order. Explicit preferences match their tag, or a compatible neutral tag; two different explicit variants/scripts are not silently interchangeable. For example, unavailable `zh-Hant` does not silently become `zh-Hans`. Language selection does not translate or convert text.

## What was tested on Windows

On Windows 10 build 19045 with Apple Music 1.1792.39681.0:

- The implemented parser read 13 existing native cache payloads successfully: 753 timed original entries, 753 translation entries, and 753 pronunciation entries. All supplemental references resolved to original keys. This is a point-in-time sample, not a coverage claim for the whole catalog.
- A live **Attention** lookup matched catalog ID `1635469694`, 68 original lines, native `zh-Hans` translation, and native `ko-Latn` pronunciation.
- The final self-contained portable build matched live **Hurt**, catalog `1635470002`, and passed the same four display combinations. A live **Cookie** run matched `1635469854` with 89 original lines.
- An explicit audit of this app's display flags produced 2/2/2/3 rows. The overlay's bottom edge stayed at physical pixel 962 on the tested 1080p primary display at 125% scaling. The audit did not operate Apple Music.
- A live **HOT** observation found matching catalog metadata (`1799278617`) without a matching lyric payload. It did not establish whether the built-in panel's closed state caused that absence.

These results confirm parsing, strong matching, and native text selection for observed data. The passive playback exercise also recorded pause/resume and real timeline repositioning. Fresh-cache availability with the panel closed, audible/repeated seek accuracy, physical click-through, focus preservation, mixed-DPI hardware, and rapid track-change races remain unverified. See [VALIDATION.md](VALIDATION.md) for final results and remaining acceptance items.

## Manual acceptance checklist

Use the separate v0.2.1 folder and an isolated QA data directory when checking settings. Keep earlier release archives unchanged. Global timing calibration and promotion of old per-song corrections are described in [SYNCHRONIZATION.md](SYNCHRONIZATION.md).

1. **Native data and four combinations:** Play a track whose native lyrics are cached. Check the tray reports a matched native cache result. Toggle both app settings through off/off, on/off, off/on, on/on. Expect current/next originals; original/translation; original/pronunciation; original/pronunciation/translation. Confirm the third row expands upward without moving the bottom edge. Select an unavailable language and confirm original fallback plus a limitation. Restart and confirm saved settings.
2. **Panel-closed cache availability:** Manually close Apple's lyrics panel. Play a track not already represented by a native payload and allow playback to continue. Capture native status. Manually open the panel only when you choose, capture again, and compare the same verified catalog identity. Do not delete or modify Apple cache files. Repeat with more than one fresh track; one missing payload does not establish a universal panel requirement.
3. **Synchronization:** Pause and resume in Apple Music; the selected lyric should freeze and continue. Seek forward/backward, including while paused, and confirm lyrics move to the reported position. Change tracks rapidly and confirm no old-track supplemental text appears on the new track. These are manual playback actions, not commands issued by the overlay.
4. **Click-through and focus:** Lock the overlay, place it over an ordinary app, click a control underneath the visible text, and confirm that app receives the click. Type in that app while tracks/lyrics change and confirm focus stays there. Unlock through the tray, drag, lock again, hide/show, and use Reset position to recover it.
5. **Display changes:** Test default bottom-center placement and a separate dragged custom position. Change resolution/scaling in Windows Settings; default placement should follow the selected monitor's bottom center, while custom placement should remain where possible and clamp only when needed. Move between monitors with different DPI, disconnect the selected monitor, and verify recovery. Test 1080p/1440p/4K and 100/125/150/200% scaling where hardware supports them. Geometry simulations are separate from these hardware checks.
6. **Missing data and cancellation:** Play a track with no native payload. Confirm original-only fallback or a clear no-lyrics status. Change tracks while lookup is pending; obsolete results must not replace the new track. Import a local/synthetic LRC for the current track, confirm its explicit override, then retry/change tracks to resume provider selection.

For a passive live snapshot plus this app's explicit display audit:

```powershell
AppleMusicLyrics.exe --data-dir "C:\some\qa-folder" --smoke-seconds 15 --display-audit
```

This writes `smoke-result.json`, `display-audit.json`, and bounded app logs to the chosen directory. It briefly changes/restores only this app's display flags; it does not manipulate Apple Music or playback. Review listening metadata before sharing diagnostics. Use existing `Run-Probe.cmd` or `scripts\Run-Probe.ps1 -Seconds 120` for event capture while performing manual playback checks; default capture is read-only. The optional probe `-Exercise` is a separate explicit control test and is unnecessary for observing user seeks.

## References

- [Microsoft SMTC media properties](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmediaproperties): documented metadata does not include lyric text or these display flags.
- [floating-lyrics-wam](https://github.com/BeteixZ/floating-lyrics-wam): its Windows cache approach informed discovery. Its older `ttmlLyrics` filename/format is not assumed to match the current `syllable-lyrics` payload.
