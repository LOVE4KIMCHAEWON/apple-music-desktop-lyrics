# Apple Music Desktop Lyrics v0.2.4

Fixes a missing-lyrics case where Apple's synchronized lyric file exists locally but its catalog identity metadata is absent. English and Simplified Chinese portable Windows x64 editions are included.

## Downloads / 下载

- `AppleMusicDesktopLyrics-0.2.4-win-x64.zip` — English interface.
- `AppleMusicDesktopLyrics-0.2.4-win-x64-zh-CN.zip` — 简体中文界面。
- `AppleMusicDesktopLyrics-0.2.4-source.zip` — source code, documentation and validation reports.
- `AppleMusicDesktopLyrics-0.2.4-SHA256SUMS.txt` — SHA-256 checksums.

Extract the complete ZIP into a writable folder and run `Run-Portable.cmd`. No separate .NET installation is needed. Requires Windows 10 19041+ or Windows 11 x64. Keep editions in separate folders; exit the running tray instance before switching a shared profile's interface language. Previous release archives are preserved.

完整解压后双击 `Run-Portable.cmd`。旧版可以保留回退。新目录默认使用独立配置；如需保留校正和外观，先退出程序，再自行把旧目录的 `data/settings.json` 复制到新目录的 `data` 中。请勿把个人配置或歌词缓存上传到 GitHub。

## What changed

- Read-only native lyric lookup still prefers Apple's existing local catalog metadata.
- If identity metadata is missing, a bounded unauthenticated iTunes ID lookup can resolve only IDs already exposed by native lyric cache candidates. It retrieves public song metadata, never network lyric text, credentials or audio.
- Exact title, artist, album, recording version and compatible duration remain required, as do unique identity and agreement with the lyric resource ID. Incomplete or ambiguous results withhold native output.
- Successful public metadata is cached in the app's own data directory for 30 days. Requests use candidate/batch bounds, three-second spacing, eight-second timeouts, cancellation and 429 `Retry-After` handling.
- Native original lyrics, translations, pronunciation, language tags and timing remain Apple cache data. Independent translation/pronunciation controls, persistent global/per-song calibration and English/Chinese interfaces are retained.

本次修复针对“原生歌词文件已经存在，但缺少能确认歌名、歌手、专辑的身份信息”。它补充的是 Apple 公开歌曲元数据，不是从第三方生成翻译或注音，也不能取得尚未缓存的 Apple 歌词。程序不会自动打开或操作 Apple Music 歌词面板。

## Validation and limits

See the included [v0.2.4 validation report](VALIDATION-0.2.4.md) for final automated test and actual Windows integration results. The investigation identified this failure pattern on ILLIT's **Karitekita Neko (Do the Dance) [Japanese Ver.]**: cached native lyric resource `1833184650` existed without local song identity metadata, and LRCLIB fallback `36451680` was untimed. Apple's public lookup returned the exact recording metadata with a 188,640 ms duration in the US and Australian catalogs.

This is not a guarantee of native cache availability for fresh tracks while Apple's lyrics panel is closed. Missing payloads, localized catalog names, unavailable IDs, network restrictions and conservative ambiguity checks can still prevent native lyrics. LRCLIB remains an original-only fallback; plain lyrics use a separate untimed window. Physical click-through/focus, mixed-DPI hardware and subjective listening alignment retain their documented manual acceptance requirements. See [known limitations](KNOWN-LIMITATIONS.md).

No credentials, personal profiles, listening caches, logs or development SDKs are included in release assets.

Public project: [LOVE4KIMCHAEWON/apple-music-desktop-lyrics](https://github.com/LOVE4KIMCHAEWON/apple-music-desktop-lyrics).
