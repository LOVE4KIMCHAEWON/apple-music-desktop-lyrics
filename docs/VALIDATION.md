# Validation — 0.2.5 bounded storefront recovery

Date: 1 November 2026, Australia/Sydney. Windows 10 build 19045 x64; Apple Music 1.1792.39681.0. This report describes the final v0.2.5 binaries, rather than just the earlier root-cause diagnostic.

## Scope and preserved behavior

The patch extends the existing unauthenticated public Apple catalog metadata resolver. It does not retrieve network Apple lyrics, add a lyrics provider, generate translations/pronunciation, operate Apple's lyrics panel or change Apple's files. Native text still comes from read-only local cache files.

The primary storefront remains US. Only a well-formed HTTP 200 response with missing IDs permits alternatives. After primary resolution, language/pronunciation hints from the remaining unresolved candidates precede the Windows region and bounded KR/JP/TW defaults. At most four storefronts are queried. HTTP failures, timeouts, malformed/conflicting responses and rate limits cannot be bypassed by switching countries.

App-owned regional metadata keeps separate filenames and validated source/country provenance, with the existing 30-day lifetime. Local provenance checking is separate from the network list: up to 32 canonical regional cache files are inspected for known identity conflicts; a 33rd file or incomplete inventory withholds resolution. Existing valid US cache entries remain compatible. Original strict catalog ID, normalized title/artist/album, recording-version, duration, uniqueness and complete-candidate requirements remain in force.

Read-only comparison with the preserved v0.2.4 source archive confirmed unchanged overlay/settings UI, settings schema, lyric presentation, strict native matcher, playback clock and timing-offset implementation. The personal v0.2.4 settings file remained byte-for-byte unchanged. The live QA profile copied its existing **+1250 ms** global correction and independent translation-on/pronunciation-off settings; no new calibration was applied. Clean-install defaults are unchanged.

## Complete automated suite

**369/369 passed; zero failures or skips.** All 306 previous cases were retained, with 63 additional cases. Coverage includes:

- Whiplash `1772644601`: US empty, KR exact public identity, strict native selection, preserved timestamps/language tags/explicit line keys and all four display combinations.
- Rejection for each incorrect ID, title, artist, album, recording version or duration; unresolved extra candidates; indistinguishable resource IDs; local/regional conflicts and conflicting duplicate responses.
- Relevant-region ordering, resolved candidates not consuming fallback slots, only missing IDs in later requests and the four-storefront bound.
- Separate storefront cache provenance, restart/offline reuse, compatible normalization, known conflicting CN metadata outside the network plan, and the 32/33-file inventory boundary.
- Existing metadata/parser/cache regressions, rate spacing, shared Retry-After cooldown, timeout, malformed envelopes and HTTP errors including non-200 successful status codes.
- Cancellation during regional requests, coordinator queuing and spacing, plus a later current-track request remaining usable.
- A real `NativeCacheMonitor` watching a test-owned directory: late synthetic cache arrival emits a signal and a subsequent provider/resolver probe obtains native lyrics. This does not simulate every WPF dispatcher/LRCLIB interleaving.

Test lyric strings are synthetic. No Apple cache payload or private diagnostic log was added as a fixture.

Command: `scripts/Build.ps1 -Publish -Offline`. Core tests, the WPF application and standalone diagnostic built successfully, with no reported warnings or errors. The full suite ran outside the filesystem sandbox because Windows named-pipe clients used by the existing IPC tests are denied inside it. Restore used cached dependencies; online dependency vulnerability auditing was not performed in offline mode. Both portable editions are self-contained Windows x64 builds using identical application binaries.

## Actual Windows verification: Whiplash

The final Chinese portable EXE passively observed live **Whiplash — aespa**, album **Whiplash - The 5th Mini Album - EP**, duration 183 seconds. The user had already opened Apple's lyrics panel during the investigation. No panel or playback control was operated by the diagnostic. The app used an isolated QA profile with no preloaded public catalog cache.

| Check | Observed result |
| --- | --- |
| Primary US response | HTTP 200, four of five candidate IDs resolved; Whiplash remained missing |
| Next storefront | KR, HTTP 200, remaining ID `1772644601` resolved |
| Recording metadata | Same ID/title/artist/album, 183.227 seconds; unchanged strict validation passed |
| Native source | Apple Music native cache, `Matched`, ID `1772644601` |
| Synchronized original entries | 82 |
| Native translation | `zh-Hans` |
| Native pronunciation | `ko-Latn` |
| Both flags off | Original + upcoming, two rows |
| Translation only | Original + translation, two rows |
| Pronunciation only | Original + pronunciation, two rows |
| Both flags on | Original + pronunciation + translation, three rows |
| Supplemental limitation | None for any audited combination |
| Bottom anchoring | Physical bottom remained 1948 px across all combinations; the third row expanded upward |
| Restart | Same native match; four US identities and one KR identity loaded from their separate caches, no HTTP catalog request |
| Persisted preferences | Global +1250 ms, translation on, pronunciation off retained |

The real WPF/Win32 audit reported a visible window, opaque lyric glyph pixels in the configured color, transparent corner pixels, topmost/layered/click-through/non-activating styles, and an underlying window at the sampled glyph position. The overlay was not the foreground window. The observed primary display was 3840×2160 at 225% scaling. These passive observations do not verify physical mouse clicks, every focus scenario, other resolutions/scaling combinations, mixed-DPI monitors or subjective listening alignment. No desktop screenshot inspection is claimed.

An earlier pre-final candidate also recovered Whiplash through TW after a redundant JP query. That run prompted the final ordering regression: alternatives now use only IDs still missing after the primary response. The final published binaries were rebuilt, the complete 369-case suite rerun, and the US→KR live check above performed afterwards.

## Release and rollback

English and Chinese portable folders, source code, English/Chinese READMEs, versioned validation and release notes are included in the release artifacts. Packaging excludes personal data, logs, diagnostics, Apple cache files, credentials, development tools and build intermediates. Source staging uses a separate `release/v0.2.5` branch based on the preserved v0.2.4 source commit.

The English portable, Chinese portable and source archives for v0.2.4 were checked against their existing SHA-256 manifest and remained unchanged. Older releases are retained. Private evidence stays outside the release allowlist; only metadata/count summaries are reproduced here.

## Unresolved and unverified

**Fresh-track native cache availability with Apple's lyrics panel closed remains unresolved.** This patch identifies an existing payload; it cannot fetch an absent Apple lyric payload or establish whether Apple writes one for every new song without opening the panel. A cache miss does not mean the official service has no lyrics. The app never opens the panel automatically. LRCLIB remains an explicitly original-only fallback and cannot supply Apple's translation/pronunciation.

Live pause/resume, physical seek input, rapid track changes during network activity and a new real Apple cache write were not induced during this patch's final passive Whiplash run. Their existing logic is preserved; simulated cancellation/seeking and the synthetic late-file watcher are covered by tests. Full WPF race interleavings, physical click-through/focus and mixed-DPI/display-change acceptance remain manual items. The finite storefront list and strict localized metadata checks can still leave a valid recording unresolved; no partial identity is guessed.

## Short manual checklist

1. Exit the old tray instance, extract the new portable ZIP completely, and retain a copy of the old profile. Copy `data/settings.json` into the new folder if preserving personal calibration. Run `Run-Portable.cmd`; check the saved global correction and independent flags before changing anything.
2. Play cached Whiplash. Confirm the tray identifies Apple native cache and exercise off/off, on/off, off/on and on/on. Expect 2/2/2/3 rows and a fixed bottom edge. Restart and confirm settings/native matching persist.
3. Pause/resume, seek forward/backward and switch songs rapidly while a lookup is pending. Confirm lyrics follow the current track and old supplemental text never returns. Recheck the saved global correction.
4. Lock the overlay and click/type in an app underneath it; confirm physical click-through and focus. Test custom/default positions across available resolution/DPI/monitor changes; use tray Reset position for recovery.
5. Separately test a genuinely fresh track with Apple's panel closed. Record whether its payload appears; open the panel only manually if desired and compare afterwards. Keep that availability result separate from this regional identity fix; do not delete or edit Apple's cache.

See [native integration notes](NATIVE-LYRICS.md), [known limitations](KNOWN-LIMITATIONS.md) and [release notes](RELEASE-0.2.5.md).
