using System;
using System.Collections.Generic;
using System.IO;

namespace AppleMusicLyrics.App;

/// <summary>Watches filenames only. The provider independently bounds and validates every read.</summary>
internal sealed class NativeCacheMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    public event Action? Changed;

    public NativeCacheMonitor(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var watcher = new FileSystemWatcher(root, "*.json")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += OnChange;
                watcher.Changed += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnRename;
                watcher.Error += (_, _) => Changed?.Invoke();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { AppLog.Write("Native cache watcher: " + ex.Message); }
        }
    }

    private static bool Relevant(string? path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        return name.StartsWith("syllable-lyrics", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ttmlLyrics", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("search", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("albums", StringComparison.OrdinalIgnoreCase)
            || char.IsAsciiDigit(name[0]);
    }

    private void OnChange(object sender, FileSystemEventArgs args)
    { if (Relevant(args.Name)) Changed?.Invoke(); }
    private void OnRename(object sender, RenamedEventArgs args)
    { if (Relevant(args.Name) || Relevant(args.OldName)) Changed?.Invoke(); }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
    }
}
