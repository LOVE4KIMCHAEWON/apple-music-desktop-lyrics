using System.Text.Json;

namespace AppleMusicLyrics.Core.Playback;

/// <summary>Small JSON offset store, independent of UI settings. Values are milliseconds.</summary>
public sealed class OffsetStore
{
    private const double MaximumOffsetMilliseconds = 10 * 60 * 1000;
    private readonly string _path;
    private readonly Dictionary<string, double> _trackOffsets = new(StringComparer.Ordinal);
    private TimeSpan _globalOffset;

    public OffsetStore(string path)
    {
        _path = path;
        Load();
    }

    public TimeSpan GlobalOffset
    {
        get => _globalOffset;
        set => _globalOffset = Normalize(value.TotalMilliseconds);
    }

    public TimeSpan GetTrackOffset(string? trackKey) =>
        trackKey is not null && _trackOffsets.TryGetValue(trackKey, out var value)
            ? Normalize(value) : TimeSpan.Zero;

    public TimeSpan GetEffectiveOffset(string? trackKey) => GlobalOffset + GetTrackOffset(trackKey);

    public void SetTrackOffset(string trackKey, TimeSpan offset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackKey);
        if (offset == TimeSpan.Zero) _trackOffsets.Remove(trackKey);
        else _trackOffsets[trackKey] = Normalize(offset.TotalMilliseconds).TotalMilliseconds;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var data = new OffsetData { GlobalOffsetMilliseconds = GlobalOffset.TotalMilliseconds,
            TrackOffsets = new(_trackOffsets, StringComparer.Ordinal) };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<OffsetData>(File.ReadAllText(_path));
            if (data is null) return;
            GlobalOffset = Normalize(data.GlobalOffsetMilliseconds);
            if (data.TrackOffsets is null) return;
            foreach (var (key, value) in data.TrackOffsets)
                if (!string.IsNullOrWhiteSpace(key) && double.IsFinite(value))
                    _trackOffsets[key] = Normalize(value).TotalMilliseconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unavailable preferences must not prevent playback observation.
        }
    }

    private static TimeSpan Normalize(double milliseconds) => TimeSpan.FromMilliseconds(
        double.IsFinite(milliseconds) ? Math.Clamp(milliseconds,
            -MaximumOffsetMilliseconds, MaximumOffsetMilliseconds) : 0);

    private sealed class OffsetData
    {
        public double GlobalOffsetMilliseconds { get; set; }
        public Dictionary<string, double>? TrackOffsets { get; set; }
    }
}
