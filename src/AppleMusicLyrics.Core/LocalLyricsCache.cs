using System.Text.Json;

namespace AppleMusicLyrics.Core;

public sealed class LocalLyricsCache
{
    private readonly string _directory;
    private readonly string _onlineDirectory;
    private readonly string _importedDirectory;
    private readonly long _maxOnlineCacheBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const long MaximumBytes = 4 * 1024 * 1024;

    public LocalLyricsCache(string directory, long maxOnlineCacheBytes = 64 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOnlineCacheBytes, 1);
        _directory = Path.GetFullPath(directory);
        _onlineDirectory = Path.Combine(_directory, "online");
        _importedDirectory = Path.Combine(_directory, "imported");
        _maxOnlineCacheBytes = maxOnlineCacheBytes;
    }

    public async Task<LyricsResult?> GetAsync(TrackMetadata track, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Prefer the user's import. The original root layout remains readable and is never trimmed.
            foreach (var directory in new[] { _importedDirectory, _directory, _onlineDirectory })
            {
                try
                {
                    var path = Path.Combine(directory, track.Key + ".json");
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length > MaximumBytes) continue;
                    await using var stream = File.OpenRead(path);
                    var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (entry is null || entry.Version != 1 || entry.TrackKey != track.Key || entry.Result is null ||
                        entry.Result.Lines is null || entry.Result.Source is null ||
                        entry.Result.Lines.Any(line => line is null || line.Text is null || line.Time < TimeSpan.Zero)) continue;
                    // Imported files are an explicit user choice and do not expire. Online cache refreshes after 30 days.
                    if (!IsImported(entry.Result) && DateTimeOffset.UtcNow - entry.StoredAt > TimeSpan.FromDays(30)) continue;
                    return entry.Result with { Lines = entry.Result.Lines.OrderBy(line => line.Time).ToArray() };
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                    // A corrupt legacy entry must not hide a healthy entry in the current layout.
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task PutAsync(TrackMetadata track, LyricsResult result, CancellationToken cancellationToken = default)
    {
        if (!result.Instrumental && !result.HasSynchronizedLyrics && string.IsNullOrWhiteSpace(result.PlainLyrics)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var imported = IsImported(result);
            var targetDirectory = imported ? _importedDirectory : _onlineDirectory;
            Directory.CreateDirectory(targetDirectory);
            var path = Path.Combine(targetDirectory, track.Key + ".json");
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheEntry(1, track.Key, DateTimeOffset.UtcNow, result),
                    JsonOptions, cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumBytes) throw new IOException("The serialized lyrics cache entry exceeds the 4 MB limit.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            if (!imported) TrimOnlineCache();
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    private static bool IsImported(LyricsResult result) => result.Source.StartsWith("Local LRC", StringComparison.Ordinal);

    private void TrimOnlineCache()
    {
        // Only this app's hash-named online entries are eligible. Imported files, legacy root
        // entries and unrelated files are preserved without opening or deserializing them.
        var directory = new DirectoryInfo(_onlineDirectory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
        var entries = directory.GetFiles("*.json", SearchOption.TopDirectoryOnly)
            .Where(file => IsOwnedFilename(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
        var total = entries.Sum(file => file.Length);
        foreach (var entry in entries)
        {
            if (total <= _maxOnlineCacheBytes) break;
            var length = entry.Length;
            try { entry.Delete(); total -= length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsOwnedFilename(string filename) => filename.Length == 69 &&
        filename.EndsWith(".json", StringComparison.Ordinal) &&
        filename.Take(64).All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record CacheEntry(int Version, string TrackKey, DateTimeOffset StoredAt, LyricsResult Result);
}

public static class LocalLyricsImporter
{
    public static async Task<LyricsResult> ImportAsync(string path, TrackMetadata track, LocalLyricsCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(path), ".lrc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .lrc file.", nameof(path));
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("The LRC file is larger than 4 MB.");
        // File.ReadAllText detects UTF-8 and Unicode BOMs. Chinese, Japanese and Korean remain intact.
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var lines = LrcParser.Parse(text);
        if (!lines.Any(line => !string.IsNullOrWhiteSpace(line.Text)))
            throw new FormatException("This file does not contain valid synchronized lyric lines. Plain lyrics are never assigned artificial timing.");
        var result = new LyricsResult(lines, null, false, "Local LRC: " + Path.GetFileName(path));
        if (cache is not null) await cache.PutAsync(track, result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}
