using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatchStudio.Core.Data;
using BatchStudio.Core.Templates;

namespace BatchStudio.Core.Workbook;

/// <summary>
/// One entity = one BATCH STUDIO workbook (e.g. BATCH_STUDIO_V28.1.xlsm) plus the BATCH STUDIO archive folder
/// (SUPPLIERS / CLIENTS) next to it. The workbook stays the only data store:
/// <list type="bullet">
/// <item>reading - each sheet the program needs is copied (package level, as-is) into a private cache, refreshed
/// whenever the workbook is saved; only sheets that really changed are re-copied;</item>
/// <item>writing - goes into the workbook itself through <see cref="EditorFactory"/> (Microsoft Excel in the app),
/// after a backup copy of the workbook.</item>
/// </list>
/// </summary>
public sealed class Workspace
{
    public const string ArchiveConfigSheet = "_ARCHIVE_CONFIG";
    public const string DashboardSheet = "Dashboard";

    /// <summary>Where caches, backups and template links live (%LOCALAPPDATA%\BATCH STUDIO\workspaces).</summary>
    public static string StateBase { get; set; } =
        Environment.GetEnvironmentVariable("BS_STATE_DIR") is { Length: > 0 } s ? s
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BATCH STUDIO", "workspaces");

    /// <summary>Opens the workbook for writing. The app replaces this with Microsoft Excel.</summary>
    public static Func<string, IWorkbookEditor> EditorFactory { get; set; } = ClosedXmlWorkbookEditor.Open;

    /// <summary>Sheets that are part of the system (not invoice templates).</summary>
    private static readonly string[] SystemSheets =
        ["Dashboard", "INVOICE HISTORY", "BATCH STUDIO", "Categories", "Items", "Suppliers", "Clients", "CONTROL", "REPORTS",
         "BATCH REPORT", "V11 TEMPLATE MAP", ArchiveConfigSheet, "CLIENT THEME MEMORY", "__V11_TemplateChoices", "_Lists"];

    public string WorkbookPath { get; }
    /// <summary>Folder that holds the workbook.</summary>
    public string Folder { get; }
    /// <summary>Private folder with the sheet copies, laid out like the old Database (Suppliers\Suppliers.xlsx ...).</summary>
    public string CacheRoot { get; }
    public string StateDir { get; }
    public string BackupDir => Path.Combine(StateDir, "backups");
    public string TemplatesDir => Path.Combine(CacheRoot, DatabaseLayout.TemplatesFolder);
    public string DashboardPath => Path.Combine(CacheRoot, DatabaseLayout.DashboardFolder, "Dashboard.xlsx");
    public string ReportTemplatePath => Path.Combine(CacheRoot, DatabaseLayout.DashboardFolder, "Supplier Report.xlsx");
    public string HistoryPath => DataSets.InvoiceHistory.FullPath(CacheRoot);
    public string DataPath(DataSetDefinition d) => d.FullPath(CacheRoot);

    /// <summary>The archive root: BATCH STUDIO next to the workbook, else the one set in the workbook (_ARCHIVE_CONFIG).</summary>
    public string ArchiveRoot
    {
        get
        {
            var beside = Path.Combine(Folder, DatabaseLayout.ArchiveFolder);
            if (Directory.Exists(beside)) return beside;
            var cfg = ConfiguredArchiveRoot;
            if (!string.IsNullOrWhiteSpace(cfg) && Directory.Exists(cfg)) return cfg;
            return beside;
        }
    }

    /// <summary>_ARCHIVE_CONFIG!B2 of the workbook (what the VBA uses).</summary>
    public string? ConfiguredArchiveRoot { get; private set; }

    /// <summary>Sheet names found in the workbook at the last refresh.</summary>
    public IReadOnlyList<string> Sheets { get; private set; } = [];
    public IReadOnlyList<string> TemplateSheets { get; private set; } = [];

    private readonly object _gate = new();
    private string _stamp = "";
    private Dictionary<string, string> _manifest = new(StringComparer.OrdinalIgnoreCase);

    private Workspace(string workbookPath, string? stateBase)
    {
        WorkbookPath = Path.GetFullPath(workbookPath);
        Folder = Path.GetDirectoryName(WorkbookPath)!;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WorkbookPath.ToUpperInvariant())))[..12];
        var name = InvoiceSafe(Path.GetFileNameWithoutExtension(WorkbookPath));
        StateDir = Path.Combine(stateBase ?? StateBase, $"{name}-{id}");
        CacheRoot = Path.Combine(StateDir, "cache");
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(BackupDir);
        try
        {
            var m = Path.Combine(StateDir, "manifest.json");
            if (File.Exists(m)) _manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(m)) ?? _manifest;
            _manifest = new Dictionary<string, string>(_manifest, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    /// <summary>Opens the workbook and brings the sheet copies up to date.</summary>
    public static Workspace Open(string workbookPath, string? stateBase = null)
    {
        if (!File.Exists(workbookPath)) throw new FileNotFoundException("The BATCH STUDIO workbook was not found.", workbookPath);
        var w = new Workspace(workbookPath, stateBase);
        w.Refresh(force: true);
        return w;
    }

    /// <summary>
    /// The BATCH STUDIO workbook inside a folder: an .xlsm/.xlsx that has an INVOICE HISTORY sheet (newest first).
    /// Accepts a workbook path as well.
    /// </summary>
    public static string? FindWorkbook(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return IsBatchStudioWorkbook(path) ? Path.GetFullPath(path) : null;
        if (!Directory.Exists(path)) return null;
        return new DirectoryInfo(path).EnumerateFiles("*.xls?")
            .Where(f => f.Extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Name.StartsWith("~$") && !f.Name.StartsWith(".~"))
            .OrderByDescending(f => f.Extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => IsBatchStudioWorkbook(f.FullName))?.FullName;
    }

    public static bool IsBatchStudioWorkbook(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            var e = zip.GetEntry("xl/workbook.xml");
            if (e == null) return false;
            using var r = new StreamReader(e.Open());
            var xml = r.ReadToEnd();
            return xml.Contains("name=\"INVOICE HISTORY\"", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ reading (sheet copies)
    private static string StampOf(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? $"{fi.Length}-{fi.LastWriteTimeUtc.Ticks}" : "";
    }

    public bool IsCurrent => StampOf(WorkbookPath) == _stamp;

    /// <summary>
    /// Re-copies the sheets that changed since the last refresh. Returns the cache files that were updated
    /// (empty when the workbook did not change).
    /// </summary>
    public IReadOnlyList<string> Refresh(bool force = false)
    {
        lock (_gate)
        {
            var stamp = StampOf(WorkbookPath);
            if (!force && stamp == _stamp) return [];
            byte[] bytes = [];
            for (int attempt = 0; ; attempt++)
            {
                try { bytes = PackageRepair.ReadAllShared(WorkbookPath); break; }
                catch (IOException) when (attempt < 8) { Thread.Sleep(250); }
            }
            var pkg = SheetExtractor.Load(bytes);
            Sheets = pkg.Sheets.Select(s => s.Name).ToList();

            var plan = new List<(string Sheet, string Rel)>();
            foreach (var d in DataSets.All)
                if (pkg.Find(d.SheetName) is { } s) plan.Add((s.Name, d.RelativePath));
            if (pkg.Find(DashboardSheet) is { } dash) plan.Add((dash.Name, Path.Combine(DatabaseLayout.DashboardFolder, "Dashboard.xlsx")));
            var report = pkg.Sheets.FirstOrDefault(s => s.Name.StartsWith("Supplier Re", StringComparison.OrdinalIgnoreCase));
            if (report != null) plan.Add((report.Name, Path.Combine(DatabaseLayout.DashboardFolder, "Supplier Report.xlsx")));
            if (pkg.Find(ArchiveConfigSheet) is { } cfg) plan.Add((cfg.Name, Path.Combine("System", "ArchiveConfig.xlsx")));
            var templates = pkg.Sheets
                .Where(s => s.Visible && s != report && !SystemSheets.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                .Select(s => s.Name).ToList();
            foreach (var t in templates) plan.Add((t, Path.Combine(DatabaseLayout.TemplatesFolder, InvoiceSafe(t) + ".xlsx")));
            TemplateSheets = templates;

            var changed = new List<string>();
            bool allWritten = true;
            foreach (var (sheet, rel) in plan)
            {
                var full = Path.Combine(CacheRoot, rel);
                var fp = SheetExtractor.Fingerprint(pkg, sheet);
                if (!force && _manifest.TryGetValue(rel, out var old) && old == fp && File.Exists(full)) continue;
                if (force && _manifest.TryGetValue(rel, out var same) && same == fp && File.Exists(full)) continue;
                try
                {
                    WriteAtomic(full, SheetExtractor.Extract(pkg, sheet));
                    _manifest[rel] = fp;
                    changed.Add(full);
                    if (rel.StartsWith(DatabaseLayout.TemplatesFolder + Path.DirectorySeparatorChar)) KeepTemplateLink(full);
                }
                catch (IOException) { allWritten = false; }
            }
            // templates removed from the workbook
            if (Directory.Exists(TemplatesDir))
            {
                var wanted = templates.Select(t => InvoiceSafe(t) + ".xlsx").ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(TemplatesDir, "*.xlsx"))
                    if (!wanted.Contains(Path.GetFileName(f)))
                    {
                        try { File.Delete(f); changed.Add(f); } catch { }
                        _manifest.Remove(Path.Combine(DatabaseLayout.TemplatesFolder, Path.GetFileName(f)));
                    }
            }
            ConfiguredArchiveRoot = ReadArchiveConfig();
            _stamp = allWritten ? stamp : "";
            try { File.WriteAllText(Path.Combine(StateDir, "manifest.json"), JsonSerializer.Serialize(_manifest)); } catch { }
            return changed;
        }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, data);
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// New template sheets are linked automatically when detection finds all main fields.
    /// A template sheet was changed in the workbook: its saved field links stay valid when detection still finds the
    /// same cells for the main fields; otherwise the template shows "changed - review".
    /// </summary>
    private static void KeepTemplateLink(string templatePath)
    {
        var map = TemplateMap.LoadFor(templatePath);
        if (map == null)
        {
            // new template sheet: link it right away when every main field was found
            try
            {
                var detected = TemplateDetector.DetectFile(templatePath);
                string[] essential = [TemplateFields.PartyName, TemplateFields.InvoiceNo, TemplateFields.IssueDate, TemplateFields.Total];
                if (detected.Items == null || !essential.All(f => detected.Fields.TryGetValue(f, out var m) && m.Cell != null)) return;
                foreach (var f in detected.Fields.Values) f.Confirmed = f.Cell != null;
                detected.FileHash = TemplateMap.HashFile(templatePath);
                detected.Save(templatePath);
            }
            catch { }
            return;
        }
        try
        {
            var now = TemplateDetector.DetectFile(templatePath);
            bool same = TemplateFields.Core.All(f =>
            {
                map.Fields.TryGetValue(f, out var a);
                now.Fields.TryGetValue(f, out var b);
                return a?.Confirmed == true && a.Method == "set by user" ? true : string.Equals(a?.Cell, b?.Cell, StringComparison.OrdinalIgnoreCase);
            });
            if (!same) return;
            map.FileHash = TemplateMap.HashFile(templatePath);
            map.Save(templatePath);
        }
        catch { }
    }

    private string? ReadArchiveConfig()
    {
        var p = Path.Combine(CacheRoot, "System", "ArchiveConfig.xlsx");
        if (!File.Exists(p)) return null;
        try
        {
            using var wb = PackageRepair.OpenWorkbook(p);
            var ws = wb.Worksheet(1);
            for (int r = 1; r <= 20; r++)
                if (string.Equals(ws.Cell(r, 1).GetString().Trim(), "ArchiveRoot", StringComparison.OrdinalIgnoreCase))
                    return ws.Cell(r, 2).GetString().Trim();
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ writing
    /// <summary>Backs up the workbook, then opens it for writing. Call <see cref="Refresh"/> after saving.</summary>
    public IWorkbookEditor Edit(int keepBackups = 30)
    {
        Backup(keepBackups);
        return EditorFactory(WorkbookPath);
    }

    /// <summary>Runs a change against the workbook (backup → edit → save → refresh the sheet copies).</summary>
    public T Change<T>(Func<IWorkbookEditor, T> change, int keepBackups = 30)
    {
        T result;
        using (var ed = Edit(keepBackups))
        {
            result = change(ed);
            ed.Save();
        }
        Refresh(force: false);
        return result;
    }

    public void Change(Action<IWorkbookEditor> change, int keepBackups = 30) =>
        Change<bool>(ed => { change(ed); return true; }, keepBackups);

    /// <summary>Points _ARCHIVE_CONFIG!B2 (used by the workbook's VBA) at <see cref="ArchiveRoot"/> when it differs.</summary>
    public bool SyncArchiveConfig(IWorkbookEditor ed)
    {
        var root = ArchiveRoot;
        if (!Directory.Exists(root) || string.Equals(ConfiguredArchiveRoot?.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!ed.HasSheet(ArchiveConfigSheet)) return false;
        var sh = ed.Sheet(ArchiveConfigSheet);
        var src = new BlockSource(sh.Read(1, 1, 20, 2));
        for (int r = 1; r <= 20; r++)
            if (string.Equals(src.Text(r, 1).Trim(), "ArchiveRoot", StringComparison.OrdinalIgnoreCase))
            {
                sh.Set(r, 2, root);
                ConfiguredArchiveRoot = root;
                return true;
            }
        return false;
    }

    /// <summary>Copies the workbook to backups\yyyyMMdd-HHmmss.xlsm and keeps the newest <paramref name="keep"/>.</summary>
    public string Backup(int keep = 30)
    {
        Directory.CreateDirectory(BackupDir);
        var dst = Path.Combine(BackupDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}{Path.GetExtension(WorkbookPath)}");
        File.WriteAllBytes(dst, PackageRepair.ReadAllShared(WorkbookPath));
        foreach (var old in new DirectoryInfo(BackupDir).GetFiles("*.xls?").OrderByDescending(f => f.Name).Skip(Math.Max(1, keep)))
        {
            try { old.Delete(); } catch { }
        }
        return dst;
    }

    private static string InvoiceSafe(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Trim().Length == 0 ? "Sheet" : s.Trim();
    }
}
