# Validation — 0.2.4 native catalog identity recovery

Date: 1 November 2026, Australia/Sydney. Windows 10 build 19045 x64; Apple Music 1.1792.39681.0. Previous release archives remain available unchanged.

## Confirmed failure

The user reported that the Chinese desktop overlay had disappeared. The running application was not crashed or hidden by preference: `IsVisible=true`, `IsLocked=true`, and global correction was +1250 ms.

A passive SMTC capture found ILLIT's **Karitekita Neko (Do the Dance) [Japanese Ver.]**, album **Toki Yo Tomare - EP**, actively playing with a fresh usable timeline and duration 188 seconds. All 15 sampled timeline observations were usable. The native lyric resource `1833184650` existed locally, but allowlisted local catalog metadata could not associate it with the playing track. The original-only LRCLIB fallback `36451680` contained plain lyrics and no synchronized lines. Locked mode therefore hid the overlay rather than fabricate timing or guess the native resource identity.

Apple's documented unauthenticated public ID lookup returned the exact title, artist and album in US/Australian metadata, with duration 188.640 seconds. The Japanese storefront returned localized Japanese names; those are not treated as interchangeable with SMTC names without exact normalized matching.

## Fix

- Local catalog matching remains the first choice.
- Only unresolved catalog IDs already exposed by valid native lyric resource identities are sent to `https://itunes.apple.com/lookup` for public identity metadata. No lyric endpoint, preview, artwork, credentials or authenticated Apple API is used.
- The resolver bounds IDs, batches, response size and cache size; serializes requests with monotonic three-second spacing; uses an eight-second HTTP timeout; respects server Retry-After; and propagates cancellation after track changes.
- Validated public metadata is cached for 30 days in this app's own `data/public-catalog`. Apple's cache remains read-only.
- Every unresolved native identity must have complete additional metadata before enrichment is used. Original strict title, artist, album, recording-version, duration and unique-resource-ID checks still apply. Partial or contradictory metadata cannot select a likely-looking lyric file.
- The same path is used for initial lookup and late-cache refresh. LRCLIB remains original-only fallback when native identity or data cannot be safely established.

This fixes missing identity metadata when a usable lyric payload already exists. It cannot obtain an uncached Apple lyric payload or guarantee cache population with Apple's panel closed.

## Automated verification

**306/306 tests passed**, zero failures or skips. All 254 previous cases were retained. New cases cover strict native enrichment (20) and the public metadata resolver (32): four-field/version mismatches, conflicting and unresolved IDs, bounded candidates, incomplete writes, ID validation, irrelevant/wrong-ID responses, malformed JSON, duplicate fields, response limits, batch requests, local cache/restart/expiration, rate limiting, Retry-After, timeout and cancellation.

Core, WPF application and diagnostic builds completed with zero warnings and errors. The full suite ran outside the test sandbox because local named-pipe clients are denied inside it. Restore used cached dependencies with `-Offline`; online dependency vulnerability auditing was unavailable.

## Actual Windows integration

The self-contained Chinese 0.2.4 EXE observed the same live ILLIT song without any Apple Music playback command or lyrics-panel operation:

| Check | Observed result |
| --- | --- |
| Public candidate identity resolution | 3/3 resolved from the official public API |
| Native match | `1833184650`, source Apple Music native cache |
| Synchronized original entries | 81 |
| Native pronunciation | `ja-Latn` |
| Native translation | None in the observed cached payload |
| Global timing correction | +1250 ms, retained |
| Restart with public metadata cache | 3/3 metadata entries resolved from the app cache; same native match |

An isolated display audit returned original/upcoming for neither flag, original/upcoming plus a translation-unavailable limitation for translation only, original/pronunciation for pronunciation only, and original/upcoming with a limitation when both were requested. That is the expected fallback for a payload containing pronunciation but no translation; it is not evidence of missing row alignment.

The personal profile was copied to the new version after backup. A visible verification run reported both WPF `IsVisible=true` and native `IsWindowVisible=true`; it rendered an opaque lyric pixel in the configured pink color. Bounds were `(795,1699)–(3045,1948)` on the observed 3840×2160 primary display at 225% DPI scaling. The four outer pixels remained fully transparent; layered, topmost, click-through and non-activating styles were set. Passive WindowFromPoint at a rendered lyric pixel identified the underlying window, not the overlay. The normal instance was then restarted and again matched the same native song using cached public metadata.

These are actual Windows rendering/style observations. They do not establish physical mouse clicks, every focus scenario, monitor disconnection, mixed-DPI behavior, all requested resolution/scaling combinations or subjective audio alignment. The Computer Use screenshot interface timed out on both capture attempts; no claim is made that a full desktop screenshot was visually inspected during this repair.

## Manual confirmation

1. Use the updated Chinese shortcut while the cached ILLIT song plays. Confirm original lyrics are visible and follow playback.
2. Enable pronunciation alone to show the native Japanese romanization. Translation is absent in this observed payload, so requests for it correctly fall back to originals with a tray explanation.
3. Pause/resume, seek forward/backward, change songs and restart; confirm +1250 ms remains saved and obsolete requests cannot restore another song's lyrics.
4. Test a song with native lyric data but missing local identity metadata, then repeat offline using the app's public metadata cache. A song with no native lyric payload still needs an original-only fallback or local synchronized LRC.

Private evidence: `diagnostics/v0.2.4`, plus the new portable profile's local diagnostic files. None is included in source or release ZIPs. Historical validation remains in the versioned reports.
