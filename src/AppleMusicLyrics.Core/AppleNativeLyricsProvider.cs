using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppleMusicLyrics.Core;

public enum NativeLyricsLookupStatus
{
    Matched, CacheUnavailable, MissingLyrics, MissingMetadata, InvalidTrack, Ambiguous, UnsupportedOrIncomplete
}

public sealed record NativeLyricsLookup(NativeLyricsLookupStatus Status, NativeLyricsDocument? Document,
    string? CatalogId, int LyricFilesInspected, int CatalogFilesInspected, string Message,
    IReadOnlyList<string>? CandidateCatalogIds = null);

/// <summary>
/// Reads only lyric files and allowlisted public catalog JSON from Apple's existing HTTP cache.
/// Never writes to Apple's directories, opens private databases, or reads credentials.
/// </summary>
public sealed partial class AppleNativeLyricsProvider : ILyricsProvider
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumFiles = 4096;
    private const int MaximumDirectories = 256;
    private const int MaximumCatalogCandidates = 128;
    private const long MaximumLookupBytes = 64L * 1024 * 1024;
    private readonly IReadOnlyList<string> _cacheRoots;

    public AppleNativeLyricsProvider(IEnumerable<string>? cacheRoots = null)
    {
        _cacheRoots = (cacheRoots ?? DiscoverCacheRoots()).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Roots that the app may watch read-only for late-arriving catalog or lyric data.</summary>
    public IReadOnlyList<string> CacheRoots => _cacheRoots;

    public async Task<LyricsResult?> GetLyricsAsync(TrackMetadata track, CancellationToken cancellationToken = default)
    {
        var result = await GetNativeLyricsAsync(track, cancellationToken).ConfigureAwait(false);
        return result.Document?.ToLyricsResult();
    }

    public async Task<NativeLyricsLookup> GetNativeLyricsAsync(TrackMetadata track,
        CancellationToken cancellationToken = default, IReadOnlyList<NativeCatalogTrack>? additionalCatalog = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lyricFiles = 0;
        var catalogFiles = 0;
        if (NativeTrackMatcher.Match(track, []).Status == NativeTrackMatchStatus.InvalidTrack)
            return Result(NativeLyricsLookupStatus.InvalidTrack, "Complete title, artist, album and duration are required.");

        var documents = new List<NativeLyricsDocument>();
        var catalog = new List<NativeCatalogTrack>();
        var recognizedRoot = false;
        var incomplete = false;
        var scanTruncated = false;
        long lookupBytes = 0;
        foreach (var root in _cacheRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOrdinaryDirectory(root)) continue;
            recognizedRoot = true;
            var scan = EnumerateCacheFiles(root, cancellationToken);
            scanTruncated |= scan.Truncated;
            foreach (var path in scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lyric = IsLyricsFilename(Path.GetFileName(path));
                if (lyric) lyricFiles++; else catalogFiles++;
                var json = await ReadStableTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (json is null) { incomplete = true; continue; }
                lookupBytes += Encoding.UTF8.GetByteCount(json);
                if (lookupBytes > MaximumLookupBytes)
                    return Result(NativeLyricsLookupStatus.UnsupportedOrIncomplete, "The native cache exceeds the bounded read limit.");
                if (lyric)
                {
                    if (!AppleNativeLyricsParser.TryParse(json, out var parsed, out _))
                    { incomplete = true; continue; }
                    documents.AddRange(parsed);
                }
                else
                {
                    try { catalog.AddRange(ParseCatalog(json)); }
                    catch (JsonException) { incomplete = true; }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!recognizedRoot) return Result(NativeLyricsLookupStatus.CacheUnavailable, "Apple Music's lyric cache is unavailable.");
        // A partial scan could omit another indistinguishable catalog identity. Never choose from a truncated set.
        if (scanTruncated) return Result(NativeLyricsLookupStatus.UnsupportedOrIncomplete, "The native cache exceeds the bounded scan limit.");
        if (incomplete) return Result(NativeLyricsLookupStatus.UnsupportedOrIncomplete,
            "A native cache payload is incomplete, unreadable or unsupported; no native lyrics were selected.");
        // A cached lyric's explicit resource identity is a lookup candidate, never proof
        // that it belongs to the playing song. Enrichment supplies public song metadata
        // only; all original strict matching rules below still apply.
        var nativeIds = documents.Where(HasValidResourceIdentity).Select(document => document.CatalogId!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unresolvedIds = nativeIds.Where(id => !catalog.Any(candidate => candidate.CatalogId == id && HasCompleteMetadata(candidate)))
            .ToArray();
        if (additionalCatalog is not null)
        {
            if (documents.Any(document => !HasValidResourceIdentity(document)))
                return Result(NativeLyricsLookupStatus.Ambiguous,
                    "Native lyric resource identities are invalid or conflicting; metadata enrichment was not applied.");
            if (unresolvedIds.Length > MaximumCatalogCandidates || additionalCatalog.Count > MaximumCatalogCandidates)
                return Result(NativeLyricsLookupStatus.UnsupportedOrIncomplete,
                    "Native catalog enrichment exceeds the bounded candidate limit.");
            if (additionalCatalog.Any(candidate => !nativeIds.Contains(candidate.CatalogId, StringComparer.Ordinal) || !HasCompleteMetadata(candidate)))
                return MissingMetadata("Additional catalog metadata is incomplete or does not identify a cached lyric resource.");
            // Resolving only the apparent best ID could hide an indistinguishable
            // second recording. Every previously unresolved native identity must be
            // covered before an enriched match can be selected.
            if (unresolvedIds.Any(id => !additionalCatalog.Any(candidate => candidate.CatalogId == id && HasCompleteMetadata(candidate))))
                return MissingMetadata("Some native lyric catalog identities remain unresolved; no enriched lyrics were selected.");
            catalog.AddRange(additionalCatalog);
        }
        var match = NativeTrackMatcher.Match(track, catalog);
        if (match.Status == NativeTrackMatchStatus.Ambiguous)
            return Result(NativeLyricsLookupStatus.Ambiguous, "Native catalog metadata is ambiguous; no native lyrics were selected.");
        if (match.Status != NativeTrackMatchStatus.Matched)
            return unresolvedIds.Length > MaximumCatalogCandidates
                ? Result(NativeLyricsLookupStatus.UnsupportedOrIncomplete, "Native catalog enrichment exceeds the bounded candidate limit.")
                : MissingMetadata("No unique native catalog identity matches this track.");
        var selected = documents.Where(document => document.CatalogId == match.CatalogId).ToArray();
        if (selected.Length == 0)
            return Result(incomplete ? NativeLyricsLookupStatus.UnsupportedOrIncomplete : NativeLyricsLookupStatus.MissingLyrics,
                "No complete native lyric payload matches this catalog identity.", match.CatalogId);
        if (selected.Any(document => !NativeTrackMatcher.IsCatalogId(document.CatalogId) ||
            document.ResourceType != "syllable-lyrics" || document.ResourceId != document.CatalogId) ||
            selected.Select(document => document.RawTtml).Distinct(StringComparer.Ordinal).Skip(1).Any())
            return Result(NativeLyricsLookupStatus.Ambiguous, "Conflicting native lyric payloads were found for the matched catalog identity.", match.CatalogId);

        cancellationToken.ThrowIfCancellationRequested();
        return new(NativeLyricsLookupStatus.Matched, selected[0], match.CatalogId, lyricFiles, catalogFiles,
            "Native lyrics matched by catalog ID and complete track metadata.");

        NativeLyricsLookup Result(NativeLyricsLookupStatus status, string message, string? id = null) =>
            new(status, null, id, lyricFiles, catalogFiles, message);

        NativeLyricsLookup MissingMetadata(string message) =>
            new(NativeLyricsLookupStatus.MissingMetadata, null, null, lyricFiles, catalogFiles, message, unresolvedIds);
    }

    private static bool HasValidResourceIdentity(NativeLyricsDocument document) =>
        NativeTrackMatcher.IsCatalogId(document.CatalogId) && document.ResourceType == "syllable-lyrics" &&
        document.ResourceId == document.CatalogId;

    private static bool HasCompleteMetadata(NativeCatalogTrack candidate) =>
        NativeTrackMatcher.IsCatalogId(candidate.CatalogId) &&
        NativeTrackMatcher.Match(candidate.Metadata, [candidate]).Status == NativeTrackMatchStatus.Matched;

    public static IReadOnlyList<string> DiscoverCacheRoots()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        try
        {
            if (!IsOrdinaryDirectory(packages)) return [];
            return Directory.EnumerateDirectories(packages, "AppleInc.AppleMusicWin_*")
                .Where(IsOrdinaryDirectory).Take(16)
                .Select(package => Path.Combine(package, "AC", "INetCache")).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static (IReadOnlyList<string> Files, bool Truncated) EnumerateCacheFiles(string root,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var files = new List<string>();
        var visited = 0;
        var seenFiles = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > MaximumDirectories) return (files, true);
            var current = queue.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(current.Path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (current.Depth < 5) queue.Enqueue((entry.FullName, current.Depth + 1));
                        else return (files, true);
                    }
                    else
                    {
                        if (++seenFiles > MaximumFiles) return (files, true);
                        if (IsLyricsFilename(entry.Name) || CatalogFilename().IsMatch(entry.Name)) files.Add(entry.FullName);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable part of a root makes identity discovery incomplete.
                return (files, true);
            }
        }
        return (files, false);
    }

    private static bool IsLyricsFilename(string name) => LyricsFilename().IsMatch(name);

    private static bool IsOrdinaryDirectory(string path)
    {
        try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static async Task<string?> ReadStableTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || before.Length is <= 0 or > MaximumFileBytes ||
                (before.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var length = before.Length;
            var written = before.LastWriteTimeUtc;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var memory = new MemoryStream((int)length);
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (memory.Length + read > MaximumFileBytes) return null;
                memory.Write(buffer, 0, read);
            }
            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written || memory.Length != length) return null;
            memory.Position = 0;
            using var reader = new StreamReader(memory, new UTF8Encoding(false, true), true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { return null; }
    }

    /// <summary>Inspect only public song identity fields; all artwork, offers, URLs and other resources are ignored.</summary>
    private static IEnumerable<NativeCatalogTrack> ParseCatalog(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var result = new List<NativeCatalogTrack>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return result;
        if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object &&
            resources.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Object)
        {
            if (songs.EnumerateObject().Take(10001).Count() > 10000) throw new JsonException("Catalog row limit exceeded.");
            foreach (var item in songs.EnumerateObject()) AddSong(item.Value, item.Name);
        }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            if (data.GetArrayLength() > 10000) throw new JsonException("Catalog row limit exceeded.");
            foreach (var item in data.EnumerateArray()) AddSong(item, null);
        }
        return result;

        void AddSong(JsonElement song, string? mapId)
        {
            if (song.ValueKind != JsonValueKind.Object || String(song, "type") != "songs") return;
            var id = String(song, "id");
            if (!NativeTrackMatcher.IsCatalogId(id) || (mapId is not null && mapId != id) ||
                !song.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object) return;
            var title = String(attributes, "name");
            var artist = String(attributes, "artistName");
            var album = String(attributes, "albumName");
            if (title is null || artist is null || album is null ||
                !attributes.TryGetProperty("durationInMillis", out var duration) ||
                duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds) || milliseconds <= 0) return;
            if (attributes.TryGetProperty("playParams", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                var playId = String(parameters, "id");
                var catalogId = String(parameters, "catalogId");
                if ((playId is not null && playId != id) || (catalogId is not null && catalogId != id)) return;
            }
            result.Add(new NativeCatalogTrack(id!, new(title, artist, album, milliseconds / 1000)));
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^syllable-lyrics(?:\[\d+\])?\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LyricsFilename();

    [GeneratedRegex(@"^(?:\d+|search|albums)(?:\[\d+\])?\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatalogFilename();
}
