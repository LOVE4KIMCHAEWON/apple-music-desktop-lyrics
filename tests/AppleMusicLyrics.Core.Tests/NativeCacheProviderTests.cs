using System.Text.Json;

namespace AppleMusicLyrics.Core.Tests;

public sealed class NativeCacheProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "apple-native-lyrics-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly TrackMetadata Track = new("Song", "Artist", "Album", 180);

    public NativeCacheProviderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ReadsNativeLyricsOnlyAfterSameCatalogIdMetadataMatch()
    {
        await WriteAsync("123[1].json", Catalog("123"));
        await WriteAsync("syllable-lyrics[1].json", Lyrics("123"));
        var before = Directory.GetFiles(_directory).ToDictionary(path => path, File.ReadAllBytes);
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.Matched, result.Status);
        Assert.Equal("123", result.CatalogId);
        Assert.Equal("Original", Assert.Single(result.Document!.Lines).Text);
        Assert.Equal(1, result.LyricFilesInspected);
        Assert.Equal(1, result.CatalogFilesInspected);
        foreach (var entry in before) Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_directory).Order());
    }

    [Fact]
    public async Task FileRecencyNeverOverridesTrackIdentity()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("456.json", Catalog("456", title: "Other song"));
        await WriteAsync("syllable-lyrics[1].json", Lyrics("123", text: "Correct"));
        await WriteAsync("syllable-lyrics[2].json", Lyrics("456", text: "Wrong"));
        File.SetLastWriteTimeUtc(Path.Combine(_directory, "syllable-lyrics[1].json"), DateTime.UtcNow.AddDays(-100));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal("Correct", Assert.Single(result.Document!.Lines).Text);
    }

    [Fact]
    public async Task AmbiguousMetadataRejectsEvenWhenOnlyOneEditionHasCachedLyrics()
    {
        await WriteAsync("search.json", CatalogResources(("123", "Song"), ("456", "Song")));
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task CacheMissNeverBorrowsAnotherTracksLyrics()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", Lyrics("456"));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.MissingLyrics, result.Status);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task LyricsWithoutCatalogMetadataAreRejected()
    {
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task IncompleteWriteFailsClosedThenSucceedsAfterFileBecomesComplete()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", "{\"data\":[");
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, (await Provider().GetNativeLyricsAsync(Track)).Status);
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.Matched, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task UnknownSchemaFailsClosed()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", "{\"futureFormat\":true}");
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task OversizedPayloadFailsClosed()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", new string(' ', 4 * 1024 * 1024 + 1));
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task IncompleteCatalogCannotHideAnAmbiguousEdition()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("search.json", "{\"resources\":");
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.UnsupportedOrIncomplete, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task ChangedConflictingPayloadForSameIdIsNotResolvedByNewestFile()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics[1].json", Lyrics("123", text: "First"));
        await WriteAsync("syllable-lyrics[2].json", Lyrics("123", text: "Second"));
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task IdenticalDuplicatePayloadIsAccepted()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics[1].json", Lyrics("123"));
        await WriteAsync("syllable-lyrics[2].json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.Matched, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task ConflictingLyricResourceIdIsRejected()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", Lyrics("123", resourceId: "456"));
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task UnexpectedResourceKindIsNotSilentlyAccepted()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", Lyrics("123").Replace("syllable-lyrics", "future-lyrics", StringComparison.Ordinal));
        Assert.Equal(NativeLyricsLookupStatus.Ambiguous, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task LyricPlayIdAliasDoesNotReplaceExplicitCatalogId()
    {
        await WriteAsync("123.json", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", Lyrics("123", playId: "AP_999"));
        Assert.Equal(NativeLyricsLookupStatus.Matched, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Theory]
    [InlineData("456", "123")]
    [InlineData("123", "456")]
    public async Task MismatchedCatalogMapOrPlayIdIsRejected(string mapId, string playId)
    {
        await WriteAsync("123.json", Catalog("123", mapId: mapId, playId: playId));
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task NestedCacheDirectoriesAndHiddenFilesAreSupported()
    {
        var bucket = Path.Combine(_directory, "bucket");
        Directory.CreateDirectory(bucket);
        await File.WriteAllTextAsync(Path.Combine(bucket, "123[1].json"), Catalog("123"));
        var lyricPath = Path.Combine(bucket, "syllable-lyrics[1].json");
        await File.WriteAllTextAsync(lyricPath, Lyrics("123"));
        if (OperatingSystem.IsWindows()) File.SetAttributes(lyricPath, FileAttributes.Hidden | FileAttributes.System);
        Assert.Equal(NativeLyricsLookupStatus.Matched, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    [Fact]
    public async Task UnrelatedPrivateFilesAreNotReadAsIdentityEvidence()
    {
        await WriteAsync("profile.json", Catalog("123"));
        await WriteAsync("cookies.dat", Catalog("123"));
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        var result = await Provider().GetNativeLyricsAsync(Track);
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, result.Status);
        Assert.Equal(0, result.CatalogFilesInspected);
    }

    [Fact]
    public async Task MissingDirectoryAndCancelledRequestAreHandled()
    {
        var missing = new AppleNativeLyricsProvider([Path.Combine(_directory, "missing")]);
        Assert.Equal(NativeLyricsLookupStatus.CacheUnavailable, (await missing.GetNativeLyricsAsync(Track)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().GetNativeLyricsAsync(Track, cancellation.Token));
    }

    [Fact]
    public async Task MalformedDurationDoesNotCrashProvider()
    {
        await WriteAsync("123.json", Catalog("123").Replace("180000", "\"unknown\"", StringComparison.Ordinal));
        await WriteAsync("syllable-lyrics.json", Lyrics("123"));
        Assert.Equal(NativeLyricsLookupStatus.MissingMetadata, (await Provider().GetNativeLyricsAsync(Track)).Status);
    }

    private AppleNativeLyricsProvider Provider() => new([_directory]);
    private Task WriteAsync(string name, string text) => File.WriteAllTextAsync(Path.Combine(_directory, name), text);

    private static string Catalog(string id, string title = "Song", string? mapId = null, string? playId = null) =>
        JsonSerializer.Serialize(new { resources = new { songs = new Dictionary<string, object>
        {
            [mapId ?? id] = Song(id, title, playId)
        } } });

    private static string CatalogResources(params (string Id, string Title)[] songs) =>
        JsonSerializer.Serialize(new { resources = new { songs = songs.ToDictionary(song => song.Id,
            song => Song(song.Id, song.Title)) } });

    private static object Song(string id, string title, string? playId = null) => new
    {
        id, type = "songs", attributes = new
        {
            name = title, artistName = "Artist", albumName = "Album", durationInMillis = 180000,
            playParams = new { id = playId ?? id }
        }
    };

    private static string Lyrics(string id, string text = "Original", string? resourceId = null, string? playId = null) =>
        JsonSerializer.Serialize(new { data = new[] { new
        {
            id = resourceId ?? id, type = "syllable-lyrics", attributes = new
            {
                playParams = new { catalogId = id, id = playId ?? "AP_" + id },
                ttmlLocalizations = "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><body dur=\"180s\"><div><p begin=\"1s\" end=\"2s\" itunes:key=\"L1\">" + text + "</p></div></body></tt>"
            }
        } } });

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
    }
}
