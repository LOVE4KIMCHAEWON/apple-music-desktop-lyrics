using AppleMusicLyrics.App;
using AppleMusicLyrics.Core.Playback;

namespace AppleMusicLyrics.Core.Tests;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "apple-lyrics-settings-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsPersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void TimingSaveReportsSuccessAndCommitsWithoutWaitingForProcessExit()
    {
        var store = new SettingsStore(_directory);
        Assert.True(store.Save(new OverlaySettings { GlobalOffsetMilliseconds = 1750 }));
        Assert.Equal(1750, new SettingsStore(_directory).Load().GlobalOffsetMilliseconds);
    }

    [Fact]
    public void TimingSaveReportsFailureWhenDestinationIsNotAFile()
    {
        Directory.CreateDirectory(SettingsPath);
        Assert.False(new SettingsStore(_directory).Save(new OverlaySettings { GlobalOffsetMilliseconds = 2000 }));
        Assert.True(Directory.Exists(SettingsPath));
        Directory.Delete(SettingsPath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IndependentFlagsAndNativeLanguagePreferencesSurviveRestart(bool translation, bool pronunciation)
    {
        var settings = new OverlaySettings
        {
            ShowTranslation = translation,
            ShowPronunciation = pronunciation,
            TranslationLanguage = "zh-Hans",
            PronunciationLanguage = "ja-Kana",
            Left = 135.5,
            Top = 762.25,
            Bottom = 902.25,
            PhysicalLeft = -1400,
            PhysicalBottom = 1200,
            PositionMode = OverlayPositionMode.Custom,
            SelectedMonitor = "\\\\.\\DISPLAY2",
            GlobalOffsetMilliseconds = 125,
            TrackOffsetsMilliseconds = new() { ["track-key"] = -250 }
        };
        new SettingsStore(_directory).Save(settings);

        // A new store instance models the next process, without sharing the settings object.
        var restored = new SettingsStore(_directory).Load();
        Assert.Equal(translation, restored.ShowTranslation);
        Assert.Equal(pronunciation, restored.ShowPronunciation);
        Assert.Equal("zh-Hans", restored.TranslationLanguage);
        Assert.Equal("ja-Kana", restored.PronunciationLanguage);
        Assert.Equal(135.5, restored.Left);
        Assert.Equal(762.25, restored.Top);
        Assert.Equal(902.25, restored.Bottom);
        Assert.Equal(-1400, restored.PhysicalLeft);
        Assert.Equal(1200, restored.PhysicalBottom);
        Assert.Equal(OverlayPositionMode.Custom, restored.PositionMode);
        Assert.Equal("\\\\.\\DISPLAY2", restored.SelectedMonitor);
        Assert.Equal(125, restored.GlobalOffsetMilliseconds);
        Assert.Equal(-250, restored.TrackOffsetsMilliseconds["track-key"]);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Theory]
    [InlineData(1750)]
    [InlineData(2000)]
    public void AllSongCorrectionPersistsAcrossSongChangesAndNewSettingsStore(int global)
    {
        var store = new SettingsStore(_directory);
        store.Save(new OverlaySettings
        {
            GlobalOffsetMilliseconds = global,
            TrackOffsetsMilliseconds = new() { ["song-a"] = -250 }
        });

        var restored = new SettingsStore(_directory).Load();
        Assert.Equal(global - 250, Effective("song-a"));
        Assert.Equal(global, Effective("song-b"));
        Assert.Equal(global - 250, Effective("song-a"));
        Assert.Equal(global, restored.GlobalOffsetMilliseconds);
        Assert.Single(restored.TrackOffsetsMilliseconds);

        restored.TrackOffsetsMilliseconds.Remove("song-a");
        store.Save(restored);
        var afterResetAndRestart = new SettingsStore(_directory).Load();
        Assert.Equal(global, afterResetAndRestart.GlobalOffsetMilliseconds);
        Assert.Empty(afterResetAndRestart.TrackOffsetsMilliseconds);
        Assert.Equal(global, LyricsTimingOffsets.GetEffectiveMilliseconds(
            afterResetAndRestart.GlobalOffsetMilliseconds,
            afterResetAndRestart.TrackOffsetsMilliseconds, "song-a"));

        int Effective(string track) => LyricsTimingOffsets.GetEffectiveMilliseconds(
            restored.GlobalOffsetMilliseconds, restored.TrackOffsetsMilliseconds, track);
    }

    [Fact]
    public void VersionOneSettingsKeepPositionAndOffsetsAndDefaultNewFeaturesOff()
    {
        File.WriteAllText(SettingsPath, """
            {
              "Left": 100.5,
              "Top": 800.25,
              "Width": 1200,
              "FontFamily": "Segoe UI",
              "IsLocked": true,
              "IsVisible": false,
              "GlobalOffsetMilliseconds": -125,
              "TrackOffsetsMilliseconds": {"old-track": 375}
            }
            """);

        var restored = new SettingsStore(_directory).Load();
        Assert.False(restored.ShowTranslation);
        Assert.False(restored.ShowPronunciation);
        Assert.Equal("", restored.TranslationLanguage);
        Assert.Equal("", restored.PronunciationLanguage);
        Assert.Equal(100.5, restored.Left);
        Assert.Equal(800.25, restored.Top);
        Assert.Null(restored.Bottom); // The overlay measures the legacy height on its first load.
        Assert.Equal(OverlayPositionMode.Custom, restored.PositionMode);
        Assert.Equal(1200, restored.Width);
        Assert.True(restored.IsLocked);
        Assert.False(restored.IsVisible);
        Assert.Equal(-125, restored.GlobalOffsetMilliseconds);
        Assert.Equal(375, restored.TrackOffsetsMilliseconds["old-track"]);

        new SettingsStore(_directory).Save(restored);
        Assert.Equal(375, new SettingsStore(_directory).Load().TrackOffsetsMilliseconds["old-track"]);
    }

    [Fact]
    public void AutomaticLanguagesRoundTripAsEmptyAndTagsAreTrimmed()
    {
        var store = new SettingsStore(_directory);
        store.Save(new OverlaySettings { TranslationLanguage = "  ", PronunciationLanguage = " ko-Latn " });

        var restored = store.Load();
        Assert.Equal("", restored.TranslationLanguage);
        Assert.Equal("ko-Latn", restored.PronunciationLanguage);
        restored.PronunciationLanguage = "";
        store.Save(restored);
        Assert.Equal("", store.Load().PronunciationLanguage);
    }

    [Fact]
    public void NullLanguagePreferencesRecoverToAutomaticWithoutLosingIndependentFlags()
    {
        File.WriteAllText(SettingsPath, """
            {"ShowTranslation":true,"ShowPronunciation":false,"TranslationLanguage":null,"PronunciationLanguage":null}
            """);
        var restored = new SettingsStore(_directory).Load();
        Assert.True(restored.ShowTranslation);
        Assert.False(restored.ShowPronunciation);
        Assert.Equal("", restored.TranslationLanguage);
        Assert.Equal("", restored.PronunciationLanguage);
    }

    [Fact]
    public void MissingSettingsUseSafeDefaultsWithoutCreatingFiles()
    {
        var restored = new SettingsStore(_directory).Load();
        Assert.False(restored.ShowTranslation);
        Assert.False(restored.ShowPronunciation);
        Assert.Equal("", restored.TranslationLanguage);
        Assert.Equal("", restored.PronunciationLanguage);
        Assert.True(restored.IsLocked);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void DefaultPositionSurvivesSavedCoordinatesAndPreservesMonitorChoice()
    {
        var store = new SettingsStore(_directory);
        store.Save(new OverlaySettings
        {
            Left = 120,
            Top = 900,
            PositionMode = OverlayPositionMode.Default,
            SelectedMonitor = "\\\\.\\DISPLAY1"
        });
        var restored = store.Load();
        Assert.Equal(OverlayPositionMode.Default, restored.PositionMode);
        Assert.Equal("\\\\.\\DISPLAY1", restored.SelectedMonitor);
    }

    [Fact]
    public void LegacySettingsWithoutCoordinatesSelectDefaultPosition()
    {
        File.WriteAllText(SettingsPath, "{\"FontFamily\":\"Segoe UI\"}");
        Assert.Equal(OverlayPositionMode.Default, new SettingsStore(_directory).Load().PositionMode);
    }

    [Fact]
    public void CorruptSettingsArePreservedAndCanBeReplacedWithWorkingPreferences()
    {
        const string malformed = "{\"ShowTranslation\":true,";
        File.WriteAllText(SettingsPath, malformed);

        var store = new SettingsStore(_directory);
        var recovered = store.Load();
        Assert.False(recovered.ShowTranslation);
        Assert.False(recovered.ShowPronunciation);
        Assert.True(recovered.IsLocked);
        Assert.Equal(malformed, File.ReadAllText(SettingsPath));
        var backup = Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        Assert.Equal(malformed, File.ReadAllText(backup));

        recovered.ShowPronunciation = true;
        recovered.PronunciationLanguage = "ja-Latn";
        store.Save(recovered);
        var restored = new SettingsStore(_directory).Load();
        Assert.False(restored.ShowTranslation);
        Assert.True(restored.ShowPronunciation);
        Assert.Equal("ja-Latn", restored.PronunciationLanguage);
        Assert.Equal(malformed, File.ReadAllText(backup));
    }

    public void Dispose()
    {
        // This class creates and owns only its unique directory under the temp root.
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var ownedPath = Path.GetFullPath(_directory);
        if (ownedPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(ownedPath).StartsWith("apple-lyrics-settings-tests-", StringComparison.Ordinal)
            && Directory.Exists(ownedPath))
            Directory.Delete(ownedPath, recursive: true);
    }
}
