using BatchStudio.Core.Data;

namespace BatchStudio.Core.Templates;

public enum TemplateStatus
{
    /// <summary>File is in Templates but has no map yet.</summary>
    NotLinked,
    /// <summary>Map exists and the template has not changed since.</summary>
    Linked,
    /// <summary>Template file changed after it was linked: re-detect before use.</summary>
    ChangedSinceLinked,
}

public sealed class TemplateEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public TemplateMap? Map { get; init; }
    public TemplateStatus Status { get; init; }
    public DateTime Modified { get; init; }
}

/// <summary>Lists the templates in &lt;Database&gt;\Templates and their link status.</summary>
public static class TemplateRegistry
{
    public static IReadOnlyList<TemplateEntry> Scan(string databaseRoot)
    {
        var dir = DatabaseLayout.Templates(databaseRoot);
        if (!Directory.Exists(dir)) return [];
        var list = new List<TemplateEntry>();
        foreach (var file in Directory.EnumerateFiles(dir)
                     .Where(p => p.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
                     .Where(p => !System.IO.Path.GetFileName(p).StartsWith("~$"))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var map = TemplateMap.LoadFor(file);
            var status = TemplateStatus.NotLinked;
            if (map != null)
            {
                string hash;
                try { hash = TemplateMap.HashFile(file); }
                catch (IOException) { hash = map.FileHash; }
                status = string.Equals(hash, map.FileHash, StringComparison.OrdinalIgnoreCase)
                    ? TemplateStatus.Linked : TemplateStatus.ChangedSinceLinked;
            }
            list.Add(new TemplateEntry
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(file),
                Path = file,
                Map = map,
                Status = status,
                Modified = File.GetLastWriteTime(file),
            });
        }
        return list;
    }

    /// <summary>Copies a template into the Templates folder (keeps an existing file unless overwrite is set).</summary>
    public static string Import(string databaseRoot, string sourceFile, bool overwrite = false)
    {
        var dir = DatabaseLayout.Templates(databaseRoot);
        Directory.CreateDirectory(dir);
        var dst = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(sourceFile));
        if (!string.Equals(System.IO.Path.GetFullPath(sourceFile), System.IO.Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceFile, dst, overwrite);
        return dst;
    }
}
