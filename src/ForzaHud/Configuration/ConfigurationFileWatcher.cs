namespace ForzaHud.Configuration;

/// <summary>
/// Watches the configuration locations used by <see cref="ConfigurationLoader"/>.
/// File-system callbacks only signal a pending change; the application consumes it on its
/// render/message thread so configuration objects are never replaced mid-draw.
/// </summary>
internal sealed class ConfigurationFileWatcher : IDisposable
{
    private const int DebounceMilliseconds = 150;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounceTimer;
    private int _reloadPending;
    private int _disposed;

    public ConfigurationFileWatcher(string? explicitPath)
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        foreach (var (directory, fileName) in WatchPaths(explicitPath))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public bool TryConsumeChange() =>
        Interlocked.Exchange(ref _reloadPending, 0) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _debounceTimer.Dispose();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args) =>
        OnFileChanged(sender, args);

    private void OnDebounceElapsed(object? state) =>
        Interlocked.Exchange(ref _reloadPending, 1);

    private static IEnumerable<(string Directory, string FileName)> WatchPaths(string? explicitPath)
    {
        var paths = string.IsNullOrWhiteSpace(explicitPath)
            ? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "hud.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "hud.json"),
            }
            : new[] { Path.GetFullPath(explicitPath) };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !seen.Add(fullPath))
            {
                continue;
            }

            yield return (directory, Path.GetFileName(fullPath));
        }
    }
}
