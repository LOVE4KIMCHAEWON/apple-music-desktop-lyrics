# Apple Music Desktop Lyrics

A portable Windows desktop utility that follows Apple's official Apple Music app and displays synchronized lyrics on a transparent desktop overlay. Built with C#, .NET 8, WPF, Windows Media Control, and Win32.

Version **0.2.3** provides separate English and Simplified Chinese portable editions, with localized Settings, tray menus, status messages, validation and edit controls. Both editions use the same binaries and lyrics engine. It retains startup recovery, persistent global timing calibration and capture-time playback interpolation. Native originals, translations and pronunciation still come from Apple's existing cache; LRCLIB remains an original-only fallback. Translation and pronunciation controls are independent by design. Following Apple's visibility switches is outside this app's scope. Previous release archives remain preserved.

[简体中文说明](README.zh-CN.md)

## Run

1. Extract the complete Windows x64 portable folder. Keep its files together.
2. Double-click `Run-Portable.cmd`. It opens **Lyrics settings** and keeps settings/cache in `data` beside the executable. In this development workspace, the same launcher finds the versioned published folder automatically. The local Chinese shortcut `启动桌面歌词` also opens Settings. A console flashing closed is normal for this launcher; the Settings window should remain visible.
3. Open Apple Music for Windows and play a song. When safely matched synchronized lyrics are available, the overlay appears automatically near the bottom of the selected display; the primary display is the default.
4. Use the pink two-line system tray icon for all controls. Double-click it to unlock and recover the overlay.

Choose `AppleMusicDesktopLyrics-0.2.3-win-x64.zip` for English or `AppleMusicDesktopLyrics-0.2.3-win-x64-zh-CN.zip` for Chinese. Extract editions into separate folders. The Chinese folder includes `ui-language.txt`, so even a direct EXE launch defaults to Chinese. Its launcher also passes `--language zh-CN`; `--language en-US` explicitly selects English. Interface language is saved separately from native lyric language preferences. Exit the running instance before switching a shared profile's interface language; repeat-launch recovery opens the existing instance using its current interface language.

The published build is self-contained; no separate .NET installation is required. Source builds require the .NET 8 SDK. Windows 10 build 19041 or later, or Windows 11, x64 is required for the app. The standalone diagnostic also supports build 17763. Run in the same interactive user session as Apple Music without administrator elevation.

Running `AppleMusicLyrics.exe` directly starts the tray/overlay without opening Settings. It reuses adjacent `data/settings.json` if present; otherwise it uses the default local application-data folder. Use `--settings` to request Settings explicitly. Launching again with the same data directory opens/restores the running instance's Settings. Settings has a taskbar button; the transparent overlay still has none. Closing Settings leaves the tray app running; use **Exit** in the tray to quit. Startup failures now show a message and log path instead of disappearing silently.

## Overlay and tray

Locked mode has a completely transparent background, white 28-DIP original text, dimmed 23-DIP additional text, and a restrained dark shadow. The window is topmost, has no taskbar button, and uses Win32 layered click-through and non-activating styles. Long lines are ellipsized so each lyric row stays on one line.

**Show translation** and **Show pronunciation** are independent settings in Settings and the tray. Tray changes apply immediately; Settings changes apply with **Apply** or **Save**. Both values and language preferences persist.

| Translation | Pronunciation | Overlay rows |
| --- | --- | --- |
| Off | Off | Current original; next original |
| On | Off | Current original; translation of that original |
| Off | On | Current original; pronunciation of that original |
| On | On | Current original; pronunciation; translation |

The three-row layout expands upward while retaining its bottom edge. If any requested supplemental text is unavailable for the current line or selected language, the overlay returns to current/next originals and the tray reports the limitation. It never borrows text from another lyric line. Before the first timestamp, the original upcoming line is shown. Empty upcoming lyrics leave the second row empty.

Leave language fields empty for automatic selection: English translation is preferred when available, and Latin-script pronunciation is preferred when available. Otherwise selection follows a stable language-tag order. Enter an available tag such as `zh-Hans`, `en-US`, `ko-Latn`, `ja-Latn`, or `ja-Kana` to choose it explicitly. An unavailable explicit language does not silently select an unrelated language.

Unlock from the tray to drag the overlay. An edit outline and lock button appear only while unlocked. Settings control fonts, colors, row sizes, width, opacity, and global timing offset. Default placement follows the selected monitor's bottom center. Custom placement is retained where it remains visible and clamped into a visible work area after display changes. Resolution, DPI changes, and monitor removal require the hardware checks listed below; a single-display test does not validate mixed-DPI behavior.

The tray provides Show/Hide, Lock/Unlock, Settings, separate global and per-song timing adjustments, Reset position, local LRC import, untimed lyric viewing, lookup retry, Start with Windows, data folder access, and Exit. Startup is **off by default** and changes only through a user action.

## Correct lyrics that appear late

Use **Global timing · all songs** in the tray, or **Synchronization · all songs** at the top of Settings. **+1750 ms** advances every lyric by 1.75 seconds; **+2000 ms** advances it by two seconds. Negative values show lyrics later. Global adjustments apply and save immediately, remain active through song changes, and survive restarts in the same data directory. Saving failure is shown explicitly.

**This song only** is an additional correction. If you already calibrated individual songs, choose **Use this song's total for all songs** on a corrected song. This promotes its total into the global setting and rebases the other saved song corrections to preserve their existing totals, avoiding double compensation. For example, song totals 1750/2000/1750 become global 1750 with only a +250 correction on the second song. Normal global presets remain additive to any existing song corrections. See [synchronization details](docs/SYNCHRONIZATION.md).

When there is no Apple Music session, valid timeline, or synchronized lyric sheet, locked mode hides the overlay. Status remains available in the tray. Plain lyrics open in a separate window labelled **Untimed lyrics**; the app never assigns them fabricated timestamps. To import an LRC file, play its corresponding track first, then choose **Import local .lrc** in the tray. This explicit import overrides native lyrics for the current playback track until the track changes or **Retry lyric lookup** is chosen; the imported sheet is also retained in the app's local cache.

## Playback and matching

The app enumerates `GetSessions()` and identifies Apple Music independently of `GetCurrentSession()`. Installed AUMIDs are discovered using the current user's Start menu and package manifests. Builds that report an executable source are matched against the executable leaf and discovered running-process package identity. No fixed package ID is embedded. Other players and browser sessions are excluded.

The engine retains each observation's monotonic capture time through dispatcher queuing. Playing advances through that queue delay; pausing freezes at capture time; resuming excludes the paused interval. Older observations cannot restore an obsolete track or status. Forward/backward discontinuities recalibrate immediately, and lyric selection uses binary search. The visible playback timer runs every 50 ms and text appears at full opacity immediately. Remote seek control is neither required nor attempted. Metadata/timeline updates may arrive separately; obsolete track requests are cancelled.

The native provider reads only allowlisted lyric and public catalog JSON from Apple Music's existing `AC\INetCache` directories. It associates the playing metadata with a unique cached catalog ID using exact normalized title, artist, and album, compatible recording-version markers, and duration within three seconds. That ID must agree with the native lyric resource. It does not choose the newest file or guess from lyric duration. Missing metadata, multiple matching IDs, conflicting payloads, partial writes, and unsupported formats withhold native output.

Native TTML preserves all language tracks, original timestamps, timed spans, raw attributes, and explicit key references. Supplemental text joins the original through `text/@for` and `p/@itunes:key`, not row position. Unknown Apple offset metadata is retained without applying undocumented semantics. The app watches for later cache arrivals, cancels obsolete requests, and can replace a pending original-only fallback after a validated native match arrives.

**The app never opens or operates Apple Music's lyrics panel.** Whether Apple populates native lyrics for an uncached song while that panel is closed remains unverified. A live check found current-song catalog metadata without a corresponding native lyric payload. Consequently native translation/pronunciation availability cannot be promised for every playback session. See [native integration details and manual checks](docs/NATIVE-LYRICS.md).

When native matching is unavailable, LRCLIB lookup starts with `/api/get` using title, artist, album, and duration, then uses `/api/search` where needed. A server-unavailable exact lookup can fall back to search after respecting retries. Matching preserves version qualifiers and rejects incompatible live/remix/cover/alternate versions. Synchronized sheets are preferred. LRCLIB supplies originals only; it does not replace missing Apple translations or pronunciation. Strict matching can return no result rather than choose a doubtful recording.

The approved default HTTP identifier is:

```text
AppleMusicDesktopLyrics/0.2.3 (https://github.com/LOVE4KIMCHAEWON)
```

The public profile remains in this identifier even if the source repository is private. It is editable in Settings. Requests are serialized with at least one second between starts, bounded timeouts, at most two retries per endpoint, and full `Retry-After` handling. A 429 does not trigger an extra search request to evade the limit. A failed lookup waits for a track change or tray retry rather than polling the service.

## Data and privacy

By default, configuration, per-track offsets, bounded logs, app discovery, and lyrics cache are stored in `%LOCALAPPDATA%\AppleMusicDesktopLyrics`. `Run-Portable.cmd` uses `data` beside the executable; a direct EXE launch reuses that profile once `data/settings.json` exists. Advanced use: `AppleMusicLyrics.exe --data-dir "C:\some\writable\folder"`.

Configuration is JSON, saved atomically with range validation and recovery from malformed files. Online cache entries expire after 30 days and have a 64 MB quota. Explicitly imported local LRC sheets are retained separately. Network responses and individual cache entries are bounded to 4 MB. These app-owned cache limits do not delete or alter Apple's cache. Native scans also have file, directory, and total-read limits and reject incomplete scans.

Only track title, artist, album, and duration are sent to LRCLIB. Native lyric lookup makes no authenticated Apple network requests. The app does not require or extract Apple IDs, passwords, cookies, or private tokens. It observes Windows media state and does not read audio, access DRM content, inject into Apple Music, or modify Apple Music files. The Apple cache schema is an implementation detail, not a supported public Apple lyrics API. Logs and app-owned cache may contain listening metadata and should be reviewed before sharing.

## Build and test

From the repository root in PowerShell:

```powershell
.\scripts\Build.ps1
.\scripts\Build.ps1 -Publish
# With dependencies already cached:
.\scripts\Build.ps1 -Offline
.\scripts\Build.ps1 -Publish -Offline
```

`Build.ps1` restores dependencies, runs tests, and builds the WPF application and diagnostic. `-Publish` uses the project version; currently it produces `artifacts\apple-music-desktop-lyrics-0.2.3-win-x64` and `artifacts\apple-music-desktop-lyrics-0.2.3-win-x64-zh-CN`, each containing both executables and the self-contained runtime. Existing release archives/directories are preserved. `scripts\Package-Release.ps1` packages both portable editions, source and checksums and refuses to overwrite archives. Build caches stay in `.tools`/`.nuget`. The local SDK is preferred when available. Initial dependencies need Internet access. `-Offline` uses cached packages and skips the network vulnerability audit for that restore only. Publishing disables trimming for WPF/WinRT compatibility.

For just the feasibility diagnostic:

```powershell
.\scripts\Build-Probe.ps1
.\scripts\Test-Probe.ps1
.\scripts\Run-Probe.ps1 -Seconds 120
```

In the published folder, double-click `Run-Probe.cmd` or run `scripts\Run-Probe.ps1`. Discovery precedes capture. Default captures are read-only. The optional `-Exercise` briefly tests pause/resume and seeks only if advertised as supported; the main app never sends those commands. Use `Summarize-Probe.ps1 -LogPath <log> -OutputPath <report.md>` to inspect evidence.

The unit suite covers native JSON/TTML parsing, safe XML handling, identifier ambiguity, strict catalog matching, language selection, timestamp/key alignment, all four display combinations, and fallback behavior. Existing LRC, HTTP, local cache, cancellation, playback interpolation, simulated seeking, pause/resume, and offset tests are retained. Preview QA can run without network or music:

```powershell
AppleMusicLyrics.exe --preview --smoke-seconds 2 --data-dir "C:\some\qa-folder"
```

This explicit QA mode writes an original demonstration-text PNG, passive native window audit, and smoke state. It sends no mouse input or playback commands.

For a live native lookup and an audit of this app's four display combinations, play a track normally and run a separate test instance:

```powershell
AppleMusicLyrics.exe --data-dir "C:\some\qa-folder" --smoke-seconds 15 --display-audit
```

The explicit audit briefly changes and restores this app's own display flags and writes `smoke-result.json` and `display-audit.json` to that data directory. It does not open Apple Music's panel, toggle its controls, or send playback commands. It is a diagnostic, not proof of real mouse click-through or real seeking.

## Validation and limitations

See [validation](docs/VALIDATION.md), [known limitations](docs/KNOWN-LIMITATIONS.md), [native lyrics](docs/NATIVE-LYRICS.md), and [Phase 1 findings](docs/PHASE1-FINDINGS.md).

All **254 tests passed** for 0.2.3, retaining all prior tests and adding interface-language precedence, resource loading, translated numeric templates and persistence without changing timing/native language choices. See [bilingual validation](docs/VALIDATION-0.2.3.md) and [startup validation](docs/VALIDATION-0.2.2.md). Previous Windows captures matched live Hurt with +1750/+2000 ms and retained calibration across restart. Listening-based accuracy, physical click-through/focus, mixed-DPI hardware, fresh uncached-song availability and networking restrictions remain documented limits. The standalone diagnostic and engineering logs remain in English.

## Source layout

- `src/AppleMusicLyrics.Core`: lyrics providers, parsing, ranking, cache, and playback logic.
- `src/AppleMusicLyrics.App`: WPF overlay/settings, media observer, tray, persistence, startup.
- `src/AppleMusicLyrics.Probe`: independent all-session GSMTC diagnostic.
- `tests/AppleMusicLyrics.Core.Tests`: unit tests with simulated clocks and HTTP responses.
- `scripts`: repeatable Windows build and diagnostics.

The intended repository is `LOVE4KIMCHAEWON/apple-music-desktop-lyrics`, private by default. This workspace contains no credentials. SDKs, caches, build output, logs, portable data, and test artifacts are excluded by `.gitignore`.

## References

- [Microsoft GSMTC API](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Windows timeline properties](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessiontimelineproperties)
- [Installed AUMID discovery](https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid)
- [LRCLIB documentation](https://lrclib.net/docs)
- [floating-lyrics-wam inspiration](https://github.com/BeteixZ/floating-lyrics-wam)
- [LyricsOnTheGo inspiration](https://github.com/The-MuffinDev/LyricsOnTheGo)
