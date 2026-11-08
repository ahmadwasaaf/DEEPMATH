namespace BatchStudio.Core.Workbook;

/// <summary>
/// Watches the BATCH STUDIO workbook. When Excel (or the program) saves it, the sheet copies are refreshed and
/// <see cref="SheetsChanged"/> reports the copies that changed (raised on a thread-pool thread).
/// </summary>
public sealed class WorkbookWatcher : IDisposable
{
    private readonly Workspace _ws;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _settle;
    private int _busy;

    public event Action<IReadOnlyList<string>>? SheetsChanged;
    public event Action<Exception>? Failed;

    public WorkbookWatcher(Workspace ws, TimeSpan? settle = null)
    {
        _ws = ws;
        _settle = settle ?? TimeSpan.FromMilliseconds(1200);
        _timer = new Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(ws.Folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        // Excel saves through a temp file that is renamed to the workbook name
        _watcher.Changed += (_, e) => Queue(e.FullPath);
        _watcher.Created += (_, e) => Queue(e.FullPath);
        _watcher.Renamed += (_, e) => Queue(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Queue(string path)
    {
        if (!string.Equals(Path.GetFullPath(path), _ws.WorkbookPath, StringComparison.OrdinalIgnoreCase)) return;
        _timer.Change(_settle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Checks the workbook now (also used as a periodic safety net).</summary>
    public void Poke() => _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    private void Fire()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) { _timer.Change(_settle, Timeout.InfiniteTimeSpan); return; }
        try
        {
            if (_ws.IsCurrent) return;
            var changed = _ws.Refresh();
            if (changed.Count > 0) SheetsChanged?.Invoke(changed);
        }
        catch (IOException) { _timer.Change(_settle, Timeout.InfiniteTimeSpan); }   // still being written
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
