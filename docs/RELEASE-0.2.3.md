# Apple Music Desktop Lyrics v0.2.3

English and Simplified Chinese portable Windows x64 editions of the transparent Apple Music lyrics overlay.

## Downloads / 下载

- `AppleMusicDesktopLyrics-0.2.3-win-x64.zip` — English interface.
- `AppleMusicDesktopLyrics-0.2.3-win-x64-zh-CN.zip` — 简体中文界面。
- `AppleMusicDesktopLyrics-0.2.3-source.zip` — complete source, English/Chinese README and validation reports.
- `AppleMusicDesktopLyrics-0.2.3-SHA256SUMS.txt` — SHA-256 checksums.

Extract the complete ZIP into a writable folder and run `Run-Portable.cmd`. No separate .NET installation is needed. Requires Windows 10 19041+ or Windows 11 x64. Use separate folders for the two editions. Exit the current tray instance before switching a shared profile's UI language.

完整解压后双击 `Run-Portable.cmd` 即可。中文版的设置、托盘、提示和错误信息已汉化；工程日志及独立诊断工具保留英文。旧版便携目录及归档可以继续保留作为回退点。

## Included

- Native Apple Music cached originals, translations and pronunciation, with strict track matching and timestamp alignment.
- Independent translation/pronunciation preferences; two rows normally, three when both are enabled and available.
- Persistent global correction, additional per-song offsets, +1750/+2000 ms presets, and capture-time playback interpolation.
- Transparent, topmost overlay, tray recovery, positioning settings, and repeated-launch Settings recovery.
- 254 passing automated tests; actual Windows Settings startup, repeat-launch and saved-language checks for both editions.

## Limits

The utility never opens or operates Apple's lyrics panel. Native cache availability for every previously uncached song with that panel closed is not guaranteed. LRCLIB supplies original-only fallback; it does not generate missing Apple translations or pronunciation. Mixed-DPI hardware, every physical click/focus scenario, and subjective audio alignment still require manual checks. Native cache format changes can affect compatibility. See the included validation report for tested versus unverified behavior.

No credentials, personal profiles, listening caches, logs or development SDKs are included.
