using System.Collections.Concurrent;

namespace BatchStudio.Core.Data;

/// <summary>
/// Watches an entity's Database folder for Excel changes (Excel saves via temp file + rename,
/// so Created/Renamed/Changed are all handled) and reports each file once after it settles.
/// </summary>
public sealed class DatabaseWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settle;

    /// <summary>Full path of a changed .xlsx/.xlsm/.json file. Raised on a thread-pool thread.</summary>
    public event Action<string>? FileChanged;

    public DatabaseWatcher(string root, TimeSpan? settle = null)
    {
        _settle = settle ?? TimeSpan.FromMilliseconds(900);
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += (_, e) => Queue(e.FullPath);
        _watcher.Created += (_, e) => Queue(e.FullPath);
        _watcher.Renamed += (_, e) => Queue(e.FullPath);
        _watcher.Deleted += (_, e) => Queue(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Queue(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("~$") || name.StartsWith(".~") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        if (path.Contains(Path.DirectorySeparatorChar + DatabaseLayout.SystemFolder + Path.DirectorySeparatorChar)) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".xlsx" or ".xlsm" or ".json")) return;

        _pending.AddOrUpdate(path,
            p => new Timer(Fire, p, _settle, Timeout.InfiniteTimeSpan),
            (p, t) => { t.Change(_settle, Timeout.InfiniteTimeSpan); return t; });
    }

    private void Fire(object? state)
    {
        var path = (string)state!;
        if (_pending.TryRemove(path, out var t)) t.Dispose();
        try { FileChanged?.Invoke(path); } catch { /* handlers report their own errors */ }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        foreach (var t in _pending.Values) t.Dispose();
        _pending.Clear();
    }
}
