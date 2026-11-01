# Validation — 0.2.2 startup recovery

Date: 1 November 2026, Australia/Sydney. Windows 10 build 19045 x64.

## Observed problem

The previous instance was still running in the tray. A second launch exited silently at the single-instance mutex, and the CMD launcher closed immediately after starting the EXE. That looked like a crash. The development-root launcher also expected an adjacent EXE although published executables are in versioned artifact directories.

## Implemented changes

- The portable launcher resolves the published folder and passes `--settings`. Missing files leave a readable error rather than immediately closing the console.
- Settings has a taskbar button. Repeated explicit activation restores an existing minimized Settings window or opens Settings in the existing process.
- A session/data-directory-scoped named pipe accepts only a bounded `SETTINGS` command, with a deadline and acknowledgement. Both ends retain `CurrentUserOnly`; the listener never waits on the UI thread.
- A direct EXE launch reuses adjacent `data/settings.json` when present. This prevents accidentally abandoning the portable global-offset profile.
- Startup errors and failed handoff to an older/unreachable instance display an explanation and data/log location.
- A local `启动桌面歌词` shortcut points to this version's portable profile and explicitly opens Settings. This machine-specific shortcut is not packaged in source or portable ZIPs.

The transparent overlay's ordinary non-activating behavior is unchanged. Opening Settings is an explicit user action. Closing Settings leaves the tray application running; Exit quits it.

## Verification

**247/247 tests passed**, including all 235 previous tests and 12 new instance-activation cases. The new cases cover repeat/concurrent requests, Unicode directory identity, session separation, timeout/cancellation, unsupported/oversized commands, stalled clients, nonblocking disposal, and callback failure. No failures or skips.

The test sandbox denies local named-pipe client connections, even with the default ACL. The full suite therefore ran outside the sandbox in the normal Windows environment; no pipe permission checks were weakened. Cached dependency restore used `-Offline`; the network vulnerability audit was unavailable. The application and diagnostic were then published self-contained with zero warnings or errors.

The published 0.2.2 EXE was launched with an isolated profile, `--settings`, and a 12-second smoke lifetime. Its Windows process reported a nonzero window handle and title **Lyrics settings**. A second EXE returned code 0 while the original process and Settings window remained present. The smoke instance exited code 0 on schedule. Its read-only observer also matched live **Hype Boy** to native catalog `1635469851`, with 66 synchronized lines. No Apple Music controls were operated.

These checks establish actual Windows startup and IPC handoff; they do not establish physical minimize/restore interaction, mouse click-through, mixed-DPI hardware behavior, or listening-based elimination of the reported delay. See [0.2.1 synchronization validation](VALIDATION-0.2.1.md) and [0.2.0 native/display validation](VALIDATION-0.2.0.md).

## Rollback and data

SHA-256 checks confirmed the v0.1.0, v0.2.0 and v0.2.1 source/portable archives are unchanged. The new output is `artifacts/apple-music-desktop-lyrics-0.2.2-win-x64`. Personal data, caches and logs are excluded from release archives. The local calibrated profile keeps global +1750 ms and the existing per-track residual correction; clean ZIP installs use default global 0.

## Manual check

1. Double-click `启动桌面歌词` in the project folder, or `Run-Portable.cmd`. Confirm **Lyrics settings** remains visible after the console closes.
2. Close Settings, then launch again. Confirm Settings reopens in the same process. Minimize Settings and launch again to check physical restore.
3. Confirm global offset +1750 ms. Listen while playing a cached track with Apple's lyrics panel closed; use +2000 if another 250 ms advance is needed. Switch songs and restart to check persistence.
4. Close Settings to return to the transparent lyrics/tray. Use the tray's Lock control for click-through, and Exit to stop the app.

Private evidence is under `diagnostics/startup-repair`; it is excluded from archives.
