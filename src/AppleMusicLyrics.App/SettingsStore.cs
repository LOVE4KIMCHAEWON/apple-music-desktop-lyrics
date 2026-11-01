using System;
using System.IO;
using System.Text.Json;

namespace AppleMusicLyrics.App;

internal sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public SettingsStore(string directory) => _path = Path.Combine(directory, "settings.json");

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new OverlaySettings();
            var settings = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(_path), Options) ?? new OverlaySettings();
            settings.Sanitize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Write("Settings load: " + ex.Message);
            try { File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), false); }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException) { }
            return new OverlaySettings();
        }
    }

    public bool Save(OverlaySettings settings)
    {
        try
        {
            settings.Sanitize();
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, _path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { AppLog.Write("Settings save: " + ex.Message); return false; }
    }
}

internal static class AppLog
{
    private static string? _path;
    private static readonly object Gate = new();
    public static void Initialize(string directory) => _path = Path.Combine(directory, "app.log");
    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 262144) File.Move(_path, _path + ".old", true);
                File.AppendAllText(_path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
