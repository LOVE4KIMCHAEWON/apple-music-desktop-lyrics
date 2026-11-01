# Apple Music Desktop Lyrics v0.2.5

Fixes native lyric matching when Apple's public catalog has no record in the primary US storefront but the same verified recording is available in a relevant regional storefront. This is a bounded extension of the existing public metadata resolver; all lyric text still comes from Apple's read-only local cache.

## Downloads / 下载

- `AppleMusicDesktopLyrics-0.2.5-win-x64.zip` — English interface.
- `AppleMusicDesktopLyrics-0.2.5-win-x64-zh-CN.zip` — 简体中文界面。
- `AppleMusicDesktopLyrics-0.2.5-source.zip` — source code, documentation and validation reports.
- `AppleMusicDesktopLyrics-0.2.5-SHA256SUMS.txt` — SHA-256 checksums.

Extract the complete portable ZIP into a writable folder and run `Run-Portable.cmd`. No separate .NET installation is needed. Requires Windows 10 19041+ or Windows 11 x64. Keep English and Chinese editions in separate folders. Previous release archives, including v0.2.4, remain available for rollback.

完整解压后双击 `Run-Portable.cmd`。新版本使用独立便携目录；如需保留已有校正和外观，先从托盘退出旧实例，再自行将旧目录的 `data/settings.json` 复制到新目录的 `data` 中。本补丁不改变已有全局/单曲时间校正、翻译/注音开关或界面。请勿上传个人配置或歌词缓存。

## What changed

- A valid successful lookup that leaves a candidate ID unresolved can try other storefronts: primary US plus at most three network alternatives. Priorities are chosen after the primary response, using only still-unresolved candidates. Available native lyric language/pronunciation tags prioritize relevant regions, followed by the Windows region and bounded KR/JP/TW defaults. These hints are query priorities only.
- Fallback uses the existing unauthenticated public Apple metadata endpoint and only numeric catalog IDs already found in native lyric candidates. It does not fetch network lyrics, use private authenticated Apple APIs or add another lyric provider.
- Requests retain the existing candidate/batch limits, shared minimum three-second spacing, eight-second timeout, `Retry-After` handling and cancellation. HTTP errors, timeouts, malformed responses, conflicts and rate limits do not trigger switching countries.
- Successful metadata retains storefront provenance in separate regional cache entries; existing primary-US metadata remains usable. Separate local provenance inspection covers up to 32 canonical regional metadata-cache files, including regions outside the network plan. Known conflicting identities, a 33rd canonical file or an incomplete inventory prevents native selection.
- Catalog ID agreement, exact normalized title/artist/album, compatible recording-version markers, duration within three seconds, complete candidate resolution and unique identity remain required. No partial candidate is guessed or ignored to force a match.
- Native originals, translations, pronunciation, explicit alignment, language tags and timing remain sourced from Apple's existing cache. The existing late-cache refresh and obsolete-request cancellation behavior remain in place. The UI, independent display controls and timing calibration are unchanged.

本次修复针对“Apple 官方歌词文件已在本机，但美国目录没有对应元数据”。地区回退只补充歌曲身份，不能替代严格匹配，也不会生成翻译或罗马音。不同地区的目录结果不会互相覆盖；元数据冲突或候选信息不完整时仍拒绝显示可能属于其他歌曲的原生歌词。

## Verification and limits

The motivating recording is aespa — **Whiplash**, catalog ID `1772644601`. Read-only investigation found an existing native payload with 82 original entries, 82 `zh-Hans` translation entries and 82 `ko-Latn` pronunciation entries. Primary US lookup returned HTTP 200 with zero records. KR lookup returned the same ID, matching title, artist and album, and 183,227 ms duration, compatible with the whole-second 183-second media timeline.

See [v0.2.5 validation](VALIDATION-0.2.5.md) for the complete regression-suite result and actual Windows verification of the v0.2.5 build, including the four display combinations. Investigation of the older build alone is not evidence that the new build has passed. The report distinguishes automated cases from real playback and remaining manual/hardware acceptance items.

The final build passed **369/369 tests**, zero failures or skips. On actual Windows playback, a fresh QA profile resolved four of five native candidate identities in US, then recovered Whiplash through KR. It matched the native 82-line document, selected `zh-Hans` translation and `ko-Latn` pronunciation, passed the four 2/2/2/3-row combinations with a fixed bottom edge, and reused separate US/KR metadata caches after restart. Existing timing calibration and independent display preferences were retained.

**Unresolved: fresh-track cache availability while Apple's lyrics panel is closed.** This patch cannot retrieve an absent native lyric payload or read process memory. It does not establish that Apple writes every new song's lyrics without opening its panel. The app never automatically opens or operates the panel. Missing payloads, catalog IDs outside the finite storefront list, localized name mismatches, network failures and strict ambiguity checks can still prevent native lyrics. LRCLIB remains an explicitly original-only fallback; it cannot supply Apple's translation or pronunciation.

**仍未解决：全新歌曲在 Apple 歌词面板关闭时是否会生成原生缓存。** 本次地区目录修复只能识别已经存在的歌词文件，不能获取未缓存的 Apple 歌词，不能据此承诺每首新歌都有原生翻译和注音。程序不会自动打开或操作 Apple 歌词面板。

Physical click-through/focus, mixed-DPI hardware and subjective listening alignment retain their documented manual acceptance requirements. See [known limitations](KNOWN-LIMITATIONS.md) and [native integration notes](NATIVE-LYRICS.md).

Release assets exclude personal settings, diagnostic logs, Apple Music cache files, credentials and development SDKs. No private investigation payload or raw copyrighted lyric text belongs in the release documentation.

Public project: [LOVE4KIMCHAEWON/apple-music-desktop-lyrics](https://github.com/LOVE4KIMCHAEWON/apple-music-desktop-lyrics).
