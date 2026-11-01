using AppleMusicLyrics.App;

namespace AppleMusicLyrics.Core.Tests;

public sealed class UiLanguageTests : IDisposable
{
    public void Dispose() => UiText.Language = "en-US";

    [Theory]
    [InlineData("zh-CN", "en-US", "en-US", "zh-CN")]
    [InlineData(null, "zh-CN", "en-US", "zh-CN")]
    [InlineData(null, "", "zh-CN", "zh-CN")]
    [InlineData(null, "invalid", "invalid", "en-US")]
    [InlineData("en-US", "zh-CN", "zh-CN", "en-US")]
    public void ExplicitLanguageThenSavedPreferenceThenPortableDefault(string? requested, string? saved, string? marker, string expected)
        => Assert.Equal(expected, UiText.ResolveLanguage(requested, saved, marker));

    [Fact]
    public void CatalogLocalizesSettingsTrayAndErrorTemplatesWithoutTouchingUnknownText()
    {
        UiText.Language = "zh-CN";
        Assert.Equal("桌面歌词设置", UiText.T("Lyrics settings"));
        Assert.Equal("显示翻译", UiText.T("Show translation"));
        Assert.Equal("显示发音", UiText.T("Show pronunciation"));
        Assert.Equal("退出", UiText.T("Exit"));
        Assert.Contains("1750", UiText.Format("Global timing · all songs ({0:+0;-0;0} ms)", 1750));
        Assert.Contains("全局偏移", UiText.Format("Global timing · all songs ({0:+0;-0;0} ms)", 1750));
        Assert.Equal("Hurt · 가사 日本語 原文", UiText.T("Hurt · 가사 日本語 原文"));
        UiText.Language = "en-US";
        Assert.Equal("Lyrics settings", UiText.T("Lyrics settings"));
        Assert.Equal("Exit", UiText.T("Exit"));
    }

    [Fact]
    public void InterfaceLanguagePersistsWithoutChangingTimingOrNativeLanguagePreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apple-lyrics-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            Assert.True(store.Save(new OverlaySettings
            {
                UiLanguage = "zh-Hans", GlobalOffsetMilliseconds = 1750,
                TranslationLanguage = "en-US", PronunciationLanguage = "ko-Latn",
                ShowTranslation = true, ShowPronunciation = true,
                TrackOffsetsMilliseconds = new() { ["song"] = 250 }
            }));
            var loaded = new SettingsStore(directory).Load();
            Assert.Equal("zh-CN", loaded.UiLanguage);
            Assert.Equal(1750, loaded.GlobalOffsetMilliseconds);
            Assert.Equal(250, loaded.TrackOffsetsMilliseconds["song"]);
            Assert.Equal("en-US", loaded.TranslationLanguage);
            Assert.Equal("ko-Latn", loaded.PronunciationLanguage);
            Assert.True(loaded.ShowTranslation && loaded.ShowPronunciation);
        }
        finally { Directory.Delete(directory, true); }
    }
}
