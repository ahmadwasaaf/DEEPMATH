using System.Security.Cryptography;

namespace BatchStudio.Core.Data;

public enum ExternalChangeOutcome { Ignored, Reloaded, Merged, Conflicts }

/// <summary>
/// One open data set (e.g. Suppliers) with two-way sync to its Excel file.
/// UI-agnostic: the WPF view model edits <see cref="Current"/> and calls Save / OnFileChanged.
/// </summary>
public sealed class DataSetSession
{
    public DataSetDefinition Definition { get; }
    public string Root { get; }
    public string FilePath => Definition.FullPath(Root);

    /// <summary>Table as last read from / written to Excel.</summary>
    public TableData Snapshot { get; private set; } = new();
    /// <summary>Working copy edited in the program.</summary>
    public TableData Current { get; private set; } = new();
    public MergeResult? PendingMerge { get; private set; }
    public bool SavePending { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }

    private string _fingerprint = "";

    public DataSetSession(string root, DataSetDefinition definition)
    {
        Root = root;
        Definition = definition;
    }

    public bool HasLocalChanges => Current.ContentHash() != Snapshot.ContentHash();

    public void Load()
    {
        if (!File.Exists(FilePath)) DatabaseLayout.CreateEmptyTable(FilePath, Definition);
        Snapshot = ExcelTableStore.Load(FilePath, Definition.SheetName);
        Current = Snapshot.Clone();
        _fingerprint = Fingerprint(FilePath);
        PendingMerge = null;
        LastSyncUtc = DateTime.UtcNow;
    }

    /// <summary>Replaces the working copy (called by the UI after edits).</summary>
    public void SetCurrent(TableData table) => Current = table;

    /// <summary>Backs up the file, then writes the working copy. Throws <see cref="FileLockedException"/> if Excel has it open.</summary>
    public void Save(int keepBackups = 30)
    {
        if (PendingMerge != null)
            throw new InvalidOperationException("Resolve the sync conflicts before saving.");
        try
        {
            BackupService.Backup(Root, Definition, keepBackups);
            ExcelTableStore.Save(FilePath, Definition.SheetName, Current);
            SavePending = false;
        }
        catch (FileLockedException)
        {
            SavePending = true;
            throw;
        }
        Snapshot = Current.Clone();
        _fingerprint = Fingerprint(FilePath);
        LastSyncUtc = DateTime.UtcNow;
    }

    /// <summary>Called when the watcher reports a change to this file.</summary>
    public ExternalChangeOutcome OnFileChanged()
    {
        if (!File.Exists(FilePath)) return ExternalChangeOutcome.Ignored;
        string fp;
        try { fp = Fingerprint(FilePath); }
        catch (IOException) { return ExternalChangeOutcome.Ignored; } // still being written; watcher will fire again
        if (fp == _fingerprint) return ExternalChangeOutcome.Ignored;  // our own save

        var external = ExcelTableStore.Load(FilePath, Definition.SheetName);
        _fingerprint = fp;
        LastSyncUtc = DateTime.UtcNow;
        if (!HasLocalChanges)
        {
            Snapshot = external;
            Current = external.Clone();
            return ExternalChangeOutcome.Reloaded;
        }

        var merge = SyncMerger.Merge(Snapshot, Current, external);
        Snapshot = external;
        if (merge.Conflicts.Count == 0)
        {
            Current = merge.Build();
            return ExternalChangeOutcome.Merged;
        }
        PendingMerge = merge;
        return ExternalChangeOutcome.Conflicts;
    }

    /// <summary>Applies the user's choices for the pending conflicts.</summary>
    public void ResolveConflicts()
    {
        if (PendingMerge == null) return;
        Current = PendingMerge.Build();
        PendingMerge = null;
    }

    public static string Fingerprint(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}

public static class BackupService
{
    /// <summary>Copies the data file to .batchstudio\backups\&lt;set&gt;\yyyyMMdd-HHmmss.xlsx and keeps the newest N.</summary>
    public static string? Backup(string root, DataSetDefinition def, int keep = 30)
    {
        var src = def.FullPath(root);
        if (!File.Exists(src)) return null;
        var dir = Path.Combine(DatabaseLayout.Backups(root), def.Key);
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.xlsx");
        using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var o = File.Create(dst))
            i.CopyTo(o);
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.xlsx").OrderByDescending(f => f.Name).Skip(keep))
        {
            try { old.Delete(); } catch { }
        }
        return dst;
    }
}
