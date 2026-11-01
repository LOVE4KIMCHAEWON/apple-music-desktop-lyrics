namespace AppleMusicLyrics.Core.Tests;

public sealed class LyricPresentationTests
{
    private static NativeLyricsDocument Document() => Assert.Single(
        AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload()));

    [Theory]
    [InlineData(false, false, 2, PresentedLyricKind.Upcoming)]
    [InlineData(true, false, 2, PresentedLyricKind.Translation)]
    [InlineData(false, true, 2, PresentedLyricKind.Pronunciation)]
    [InlineData(true, true, 3, PresentedLyricKind.Pronunciation)]
    public void IndependentFlagsProduceAllFourLayouts(bool translation, bool pronunciation,
        int count, PresentedLyricKind second)
    {
        var result = LyricPresentation.Select(Document(), TimeSpan.FromSeconds(11), translation, pronunciation);
        Assert.Equal(count, result.Lines.Count);
        Assert.Equal(PresentedLyricKind.Original, result.Lines[0].Kind);
        Assert.Equal("가상 lyric", result.Lines[0].Text);
        Assert.Equal(second, result.Lines[1].Kind);
        Assert.Null(result.Limitation);
        if (translation && pronunciation)
        {
            Assert.Equal("ga sang", result.Lines[1].Text);
            Assert.Equal("First synthetic line", result.Lines[2].Text);
            Assert.Equal(PresentedLyricKind.Translation, result.Lines[2].Kind);
        }
    }

    [Fact]
    public void ReorderedSupplementalRowsJoinByKeyAndSelectRequestedLanguage()
    {
        var result = LyricPresentation.Select(Document(), TimeSpan.FromSeconds(11), true, true, "zh-Hans", "ko-Latn");
        Assert.Equal("第一测试", result.Lines[2].Text);
        Assert.Equal("ga sang", result.Lines[1].Text);
        Assert.Equal("zh-Hans", result.TranslationLanguage);
        Assert.Equal("ko-Latn", result.PronunciationLanguage);
        var later = LyricPresentation.Select(Document(), TimeSpan.FromSeconds(21), true, true, "zh-Hans", "ko-Latn");
        Assert.Equal("次のテスト", later.Lines[0].Text);
        Assert.Equal("da eum", later.Lines[1].Text);
        Assert.Equal("第二测试", later.Lines[2].Text);
    }

    [Fact]
    public void SwitchingFlagsImmediatelyChangesContentWithoutChangingTrackOrTimestamp()
    {
        var document = Document();
        var position = TimeSpan.FromSeconds(11);
        var original = LyricPresentation.Select(document, position, false, false);
        var translated = LyricPresentation.Select(document, position, true, false);
        var pronounced = LyricPresentation.Select(document, position, false, true);
        var both = LyricPresentation.Select(document, position, true, true);
        Assert.Equal(new[] { "次のテスト", "First synthetic line", "ga sang", "ga sang" },
            new[] { original, translated, pronounced, both }.Select(result => result.Lines[1].Text));
        Assert.All(new[] { original, translated, pronounced, both }, result => Assert.Equal(0, result.ActiveIndex));
        Assert.Equal(original, LyricPresentation.Select(document, position, false, false), new PresentationComparer());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MissingRequestedDataFallsBackToOriginalPairWithExplicitLimitation(bool translation, bool pronunciation)
    {
        var document = Document() with { Translations = [], Pronunciations = [] };
        var result = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), translation, pronunciation);
        Assert.Equal(new[] { "가상 lyric", "次のテスト" }, result.Lines.Select(line => line.Text));
        Assert.NotNull(result.Limitation);
        Assert.Contains("unavailable", result.Limitation);
    }

    [Fact]
    public void MissingTranslationForOnlyCurrentLineNeverUsesPreviousOrUpcomingTranslation()
    {
        var document = Document();
        document = document with
        {
            Translations = document.Translations.Select(group => group with
            {
                Lines = group.Lines.Where(line => line.OriginalKey != "L1").ToArray()
            }).ToArray()
        };
        var result = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), true, true);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal(PresentedLyricKind.Upcoming, result.Lines[1].Kind);
        Assert.Contains("translation", result.Limitation);
    }

    [Fact]
    public void LanguagePreferenceDoesNotSilentlySelectAnotherExplicitVariant()
    {
        var document = Document();
        Assert.Equal("en-US", LyricPresentation.SelectLanguage(document.Translations, "en", false)?.Language);
        Assert.Equal("zh-Hans", LyricPresentation.SelectLanguage(document.Translations, "ZH-hans", false)?.Language);
        Assert.Null(LyricPresentation.SelectLanguage(document.Translations, "zh-Hant", false));
        Assert.Null(LyricPresentation.SelectLanguage(document.Translations, "fr", false));
        var result = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), true, false, "fr");
        Assert.NotNull(result.Limitation);
        Assert.Equal(PresentedLyricKind.Upcoming, result.Lines[1].Kind);
    }

    [Fact]
    public void AutomaticLanguageChoicePrefersEnglishAndLatinScriptDeterministically()
    {
        var document = Document();
        var pronunciation = document.Pronunciations[0];
        document = document with
        {
            Translations = document.Translations.Reverse().ToArray(),
            Pronunciations = [pronunciation with { Language = "ja-Kana" }, pronunciation with { Language = "ja-Latn" }]
        };
        var result = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), true, true);
        Assert.Equal("en-US", result.TranslationLanguage);
        Assert.Equal("ja-Latn", result.PronunciationLanguage);
    }

    [Fact]
    public void OffsetAndForwardBackwardPositionsRecomputeOriginalAndAlignedData()
    {
        var document = Document();
        var before = LyricPresentation.Select(document, TimeSpan.FromSeconds(9), true, true);
        Assert.Equal(-1, before.ActiveIndex);
        Assert.Equal("", before.Lines[0].Text);
        Assert.Equal("가상 lyric", before.Lines[1].Text);
        var first = LyricPresentation.Select(document, TimeSpan.FromSeconds(10), true, true,
            offset: TimeSpan.FromMilliseconds(125));
        Assert.Equal(0, first.ActiveIndex);
        var forward = LyricPresentation.Select(document, TimeSpan.FromSeconds(25), true, true);
        Assert.Equal(1, forward.ActiveIndex);
        Assert.Equal("Second synthetic line", forward.Lines[2].Text);
        var backward = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), true, true);
        Assert.Equal(0, backward.ActiveIndex);
        Assert.Equal("First synthetic line", backward.Lines[2].Text);
        var repeated = LyricPresentation.Select(document, TimeSpan.FromSeconds(11), true, true);
        Assert.Equal(backward.Lines, repeated.Lines); // Paused position is stable.
    }

    private sealed class PresentationComparer : IEqualityComparer<LyricPresentationResult>
    {
        public bool Equals(LyricPresentationResult? x, LyricPresentationResult? y) =>
            x?.ActiveIndex == y?.ActiveIndex && x?.Limitation == y?.Limitation &&
            x is not null && y is not null && x.Lines.SequenceEqual(y.Lines);
        public int GetHashCode(LyricPresentationResult obj) => obj.ActiveIndex;
    }
}
