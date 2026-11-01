using System.Text.Json;

namespace AppleMusicLyrics.Core.Tests;

public sealed class NativeLyricsParserTests
{
    [Fact]
    public void ParsesNativeIdentityLanguageTimingAndExplicitAlignment()
    {
        var document = Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload()));
        Assert.Equal("123456", document.CatalogId);
        Assert.Equal("resource-7", document.ResourceId);
        Assert.Equal("syllable-lyrics", document.ResourceType);
        Assert.Equal("separate-play-id", document.PlayParameters["id"]);
        Assert.Equal(TimeSpan.FromSeconds(30), document.Duration);
        Assert.Equal(new[] { "L1", "L2" }, document.Lines.Select(line => line.Key));
        Assert.Equal(TimeSpan.FromMilliseconds(10125), document.Lines[0].Begin);
        Assert.Equal(TimeSpan.FromSeconds(12), document.Lines[0].End);
        Assert.Equal("가상 lyric", document.Lines[0].Text);
        Assert.Equal("ko", document.Lines[0].Language);
        Assert.Equal("ja", document.Lines[1].Language);
        Assert.Equal(new[] { "zh-Hans", "en-US" }, document.Translations.Select(group => group.Language));
        Assert.Equal("L2", document.Translations[0].Lines[0].OriginalKey);
        Assert.Equal("L1", document.Translations[0].Lines[1].OriginalKey);
        Assert.Equal("ko-Latn", Assert.Single(document.Pronunciations).Language);
        Assert.True(document.Pronunciations[0].AutomaticallyCreated);
    }

    [Fact]
    public void RetainsRawTtmlNamespacedMetadataAndNestedSyllableTiming()
    {
        var document = Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload()));
        Assert.Equal(NativeLyricsFixtures.Ttml, document.RawTtml);
        Assert.Equal("125", document.Metadata["tt/{http://music.apple.com/lyric-ttml-internal}lyricOffset"]);
        Assert.Equal("v1", document.Lines[0].Metadata["{http://www.w3.org/ns/ttml#metadata}agent"]);
        var outer = Assert.Single(document.Lines[0].Spans);
        Assert.Equal(TimeSpan.FromMilliseconds(10125), outer.Begin);
        var child = Assert.Single(outer.Children);
        Assert.Equal("가상", child.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(10250), child.End);
        var pronunciation = document.Pronunciations[0].Lines.Single(line => line.OriginalKey == "L1");
        Assert.Null(pronunciation.Begin); // No fabricated line timing from the original or a timed child.
        Assert.Equal(TimeSpan.FromMilliseconds(10125), Assert.Single(pronunciation.Spans).Begin);
    }

    [Theory]
    [InlineData("01:02:03.125", 3723.125)]
    [InlineData("02:03.125", 123.125)]
    [InlineData("10.125", 10.125)]
    [InlineData("125ms", 0.125)]
    [InlineData("1.5s", 1.5)]
    [InlineData("1.5m", 90)]
    [InlineData("1.5h", 5400)]
    [InlineData("30f", 1)]
    [InlineData("00:00:01:15", 1.5)]
    public void ParsesTimestampFormsWithoutRoundingToCentiseconds(string stamp, double seconds)
    {
        var xml = $"<tt><body><div><p begin='{stamp}' key='L1'>Synthetic</p></div></body></tt>";
        var result = Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload(xml)));
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(result.Lines).Begin);
    }

    [Fact]
    public void RespectsFrameAndTickParametersAndRetainsRawDurations()
    {
        const string xml = "<tt xmlns:ttp='http://www.w3.org/ns/ttml#parameter' ttp:frameRate='60' ttp:frameRateMultiplier='1000 1001' ttp:tickRate='1000'><body><p begin='60f' end='2000t' dur='999ms' key='L1'>Synthetic</p></body></tt>";
        var line = Assert.Single(Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload(xml))).Lines);
        Assert.Equal(1.001, line.Begin.TotalSeconds, 6);
        Assert.Equal(TimeSpan.FromSeconds(2), line.End);
        Assert.Equal("999ms", line.Metadata["dur"]);
    }

    [Fact]
    public void ClockSubframesUseDeclaredSubFrameRate()
    {
        const string xml = "<tt xmlns:ttp='http://www.w3.org/ns/ttml#parameter' ttp:frameRate='30' ttp:subFrameRate='2'><body><p begin='00:00:01:15.1' key='L1'>Synthetic</p></body></tt>";
        var line = Assert.Single(Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload(xml))).Lines);
        Assert.Equal(TimeSpan.FromSeconds(1 + 15.5 / 30), line.Begin);
    }

    [Theory]
    [InlineData("01:99")]
    [InlineData("00:61:00")]
    [InlineData("NaN")]
    [InlineData("-2")]
    [InlineData("1e300")]
    [InlineData("00:00:01:31")]
    [InlineData("00:00:01:15.1")]
    [InlineData("5t")]
    public void RejectsMalformedOrUnsupportedTimeInsteadOfInventingTiming(string stamp)
    {
        var xml = $"<tt><body><p begin='{stamp}' key='L1'>Synthetic</p></body></tt>";
        Assert.False(AppleNativeLyricsParser.TryParse(NativeLyricsFixtures.Payload(xml), out var docs, out var error));
        Assert.Empty(docs);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("<tt><body><p begin='1' key='L1'>One</p><p begin='2' key='L1'>Two</p></body></tt>")]
    [InlineData("<tt><head><translation><text for='missing'>Supplement</text></translation></head><body><p begin='1' key='L1'>One</p></body></tt>")]
    [InlineData("<tt><head><translation><text for='L1'>A</text><text for='L1'>B</text></translation></head><body><p begin='1' key='L1'>One</p></body></tt>")]
    [InlineData("<tt><body><p begin='2' end='1' key='L1'>Invalid</p></body></tt>")]
    [InlineData("<tt><body><p key='L1'>Untimed</p></body></tt>")]
    [InlineData("<tt><body timeContainer='seq'><p begin='1' key='L1'>Unsupported</p></body></tt>")]
    [InlineData("<tt xmlns:x='urn:ambiguous'><body><p begin='1' key='L1' x:key='L2'>Ambiguous</p></body></tt>")]
    [InlineData("<tt><body><p begin='1' key='L1'>One</p></body><body><p begin='2' key='L2'>Two</p></body></tt>")]
    public void RejectsBrokenAlignmentAmbiguityAndUnsupportedTiming(string xml) =>
        Assert.False(AppleNativeLyricsParser.TryParse(NativeLyricsFixtures.Payload(xml), out _, out _));

    [Fact]
    public void RejectsDtdExternalEntitiesIncompleteWritesAndOversizeInput()
    {
        const string xml = "<!DOCTYPE tt [<!ENTITY external SYSTEM 'file:///C:/Windows/win.ini'>]><tt><body><p begin='1' key='L1'>&external;</p></body></tt>";
        Assert.False(AppleNativeLyricsParser.TryParse(NativeLyricsFixtures.Payload(xml), out _, out _));
        Assert.False(AppleNativeLyricsParser.TryParse("{\"data\":[", out _, out _));
        Assert.False(AppleNativeLyricsParser.TryParse("{\"data\":[]}", out _, out _));
        Assert.False(AppleNativeLyricsParser.TryParse(new string(' ', AppleNativeLyricsParser.MaximumPayloadCharacters + 1), out _, out _));
        Assert.False(AppleNativeLyricsParser.TryParse(NativeLyricsFixtures.Payload("<tt><body>"), out _, out _));
    }

    [Fact]
    public void OriginalLyricsConversionUsesOnlyOriginalTimestamps()
    {
        var result = Assert.Single(AppleNativeLyricsParser.Parse(NativeLyricsFixtures.Payload())).ToLyricsResult();
        Assert.Equal(new[] { 10.125, 20.5 }, result.Lines.Select(line => line.Time.TotalSeconds));
        Assert.Null(result.PlainLyrics);
        Assert.Equal("Apple Music native cache", result.Source);
    }
}

internal static class NativeLyricsFixtures
{
    public const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xml:lang="ko" itunes:lyricOffset="125">
          <head><metadata><itunes:iTunesMetadata>
            <itunes:translations>
              <itunes:translation type="subtitle" xml:lang="zh-Hans"><itunes:text for="L2">第二测试</itunes:text><itunes:text for="L1">第一测试</itunes:text></itunes:translation>
              <itunes:translation type="subtitle" xml:lang="en-US"><itunes:text for="L1">First synthetic line</itunes:text><itunes:text for="L2">Second synthetic line</itunes:text></itunes:translation>
            </itunes:translations>
            <itunes:transliterations><itunes:transliteration xml:lang="ko-Latn" automaticallyCreated="true"><itunes:text for="L2">da eum</itunes:text><itunes:text for="L1"><span begin="10.125" end="12">ga sang</span></itunes:text></itunes:transliteration></itunes:transliterations>
          </itunes:iTunesMetadata></metadata></head>
          <body dur="0:30"><div begin="10.125" end="22">
            <p begin="20.5" end="22" itunes:key="L2" xml:lang="ja">次のテスト</p>
            <p begin="10.125" end="12" itunes:key="L1" ttm:agent="v1"><span begin="10.125" end="12"><span begin="10.125" end="10.250">가상</span> lyric</span></p>
          </div></body>
        </tt>
        """;

    public static string Payload(string? xml = null) => JsonSerializer.Serialize(new
    {
        data = new[]
        {
            new { id = "resource-7", type = "syllable-lyrics", attributes = new
            {
                playParams = new { catalogId = "123456", id = "separate-play-id", kind = "song", displayType = "normal" },
                ttmlLocalizations = xml ?? Ttml
            } }
        }
    });
}
