using System.Globalization;
using BatchStudio.Core.Data;
using BatchStudio.Core.Workbook;
using ClosedXML.Excel;

namespace BatchStudio.Core.Invoices;

/// <summary>Where things are on the INVOICE HISTORY sheet.</summary>
public sealed class HistoryLayout
{
    public int HeaderRow { get; init; }
    public int ColNo { get; init; }
    public int ColName { get; init; }
    public int ColAmount { get; init; }
    public int ColDate { get; init; }
    public int ColInvoice { get; init; }
    public int ColStatus { get; init; }
    public int ColType { get; init; }
    public int ColRecordId { get; init; }
    public int ColPath { get; init; }
    public int ColTemplate { get; init; }
    /// <summary>Columns holding fixed action labels ("Open Invoice", "Remove", "Export PDF", …) in the V28 sheet.</summary>
    public IReadOnlyList<(int Col, string Default)> Actions { get; init; } = [];
    /// <summary>True for the original BATCH STUDIO V28 sheet (title area, count in C3, unlabeled K..N).</summary>
    public bool IsV28 { get; init; }
    public int FirstDataRow => HeaderRow + 1;
    public int LastCol => new[] { ColNo, ColName, ColAmount, ColDate, ColInvoice, ColStatus, ColType, ColRecordId, ColPath, ColTemplate }
        .Concat(Actions.Select(a => a.Col)).Max();
}

public sealed class HistoryRecord
{
    public int Row { get; init; }
    public string No { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal? Amount { get; init; }
    public DateTime? Date { get; init; }
    public string InvoiceNo { get; init; } = "";
    public string Status { get; init; } = "";
    public string PartyType { get; init; } = "";
    public string RecordId { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Template { get; init; } = "";
}

/// <summary>
/// Reads and appends to INVOICE HISTORY in the layout the workbook already has - the original V28 sheet
/// (headers in row 8 from column B, data from row 9, party type / record id / path / template in K..N)
/// or a simple table with headers in row 1. The sheet's formatting is never changed.
/// </summary>
public static class InvoiceHistoryBook
{
    public static IXLWorksheet Sheet(XLWorkbook wb) =>
        wb.Worksheets.TryGetWorksheet(DataSets.InvoiceHistory.SheetName, out var ws) ? ws
        : wb.Worksheets.FirstOrDefault(w => w.Name.Contains("HISTORY", StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheet(1);

    public static HistoryLayout Detect(IXLWorksheet ws) => Detect(new XLSource(ws));

    public static HistoryLayout Detect(ICellSource ws)
    {
        for (int r = 1; r <= 40; r++)
        {
            var row = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 1; c <= 40; c++)
            {
                var t = ws.Text(r, c).Trim();
                if (t.Length > 0 && !row.ContainsKey(t)) row[t] = c;
            }
            int Find(params string[] keys) => keys.Select(k => row.TryGetValue(k, out var c) ? c : 0).FirstOrDefault(c => c > 0);
            int inv = Find("INVOICE NO", "Invoice No", "Invoice Number");
            if (inv == 0) continue;
            int name = Find("Name", "Client / Supplier", "Party");
            int status = Find("STATUS", "Status");
            int type = Find("Party Type", "Type");
            bool v28 = type == 0 && status > 0;   // V28: K..N have no headers
            return new HistoryLayout
            {
                HeaderRow = r,
                ColNo = Find("#", "No", "No."),
                ColName = name,
                ColAmount = Find("Amount", "Total"),
                ColDate = Find("Date", "Issue Date"),
                ColInvoice = inv,
                ColStatus = status,
                ColType = v28 ? status + 4 : type,
                ColRecordId = v28 ? status + 5 : Find("Record ID", "ID"),
                ColPath = v28 ? status + 6 : Find("File Path", "Path"),
                ColTemplate = v28 ? status + 7 : Find("Template"),
                IsV28 = v28,
                Actions = v28
                    ? [(status + 1, "Open Invoice"), (status + 2, "Remove"), (status + 3, "Export PDF"),
                       (status + 8, "View Full Report"), (status + 9, "Export Full Report")]
                    : [],
            };
        }
        throw new InvalidDataException("INVOICE HISTORY: header row with \"INVOICE NO\" not found.");
    }

    public static List<HistoryRecord> Read(string path)
    {
        using var wb = PackageRepair.OpenWorkbook(path);
        var ws = Sheet(wb);
        var l = Detect(ws);
        return Read(ws, l);
    }

    public static List<HistoryRecord> Read(IXLWorksheet ws, HistoryLayout l) => Read(new XLSource(ws), l);

    public static List<HistoryRecord> Read(ICellSource ws, HistoryLayout l)
    {
        var list = new List<HistoryRecord>();
        int last = LastDataRow(ws, l);
        string T(int r, int c) => c > 0 ? ws.Text(r, c).Trim() : "";
        for (int r = l.FirstDataRow; r <= last; r++)
        {
            var inv = T(r, l.ColInvoice);
            var name = T(r, l.ColName);
            if (inv.Length == 0 && name.Length == 0) continue;
            decimal? amount = null;
            if (l.ColAmount > 0 && decimal.TryParse(T(r, l.ColAmount), NumberStyles.Float, CultureInfo.InvariantCulture, out var a)) amount = a;
            DateTime? date = null;
            if (l.ColDate > 0)
            {
                switch (ws.Raw(r, l.ColDate))
                {
                    case DateTime dt: date = dt; break;
                    case double d when d > 0 && d < 2958466: date = DateTime.FromOADate(d); break;
                    case decimal m when m > 0 && m < 2958466: date = DateTime.FromOADate((double)m); break;
                    case string str when DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd): date = pd; break;
                }
            }
            list.Add(new HistoryRecord
            {
                Row = r, No = T(r, l.ColNo), Name = name, Amount = amount, Date = date, InvoiceNo = inv,
                Status = T(r, l.ColStatus), PartyType = T(r, l.ColType), RecordId = T(r, l.ColRecordId),
                FilePath = T(r, l.ColPath), Template = T(r, l.ColTemplate),
            });
        }
        return list;
    }

    public static HashSet<string> InvoiceNumbers(string path) =>
        new(Read(path).Select(r => r.InvoiceNo).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static int LastDataRow(ICellSource ws, HistoryLayout l)
    {
        int a = ws.LastRow(l.ColInvoice);
        int b = l.ColName > 0 ? ws.LastRow(l.ColName) : 0;
        return Math.Max(Math.Max(a, b), l.HeaderRow);
    }

    // ------------------------------------------------------------------ writing (into the workbook through an editor)
    public static ISheetEditor Sheet(IWorkbookEditor ed)
    {
        if (ed.HasSheet(DataSets.InvoiceHistory.SheetName)) return ed.Sheet(DataSets.InvoiceHistory.SheetName);
        throw new InvalidOperationException("The workbook has no INVOICE HISTORY sheet.");
    }

    private static (ISheetEditor Sheet, BlockSource Src, HistoryLayout Layout, List<HistoryRecord> Records) Open(IWorkbookEditor ed)
    {
        var sh = Sheet(ed);
        var src = BlockSource.ReadAll(sh, 40);
        var l = Detect(src);
        return (sh, src, l, Read(src, l));
    }

    /// <summary>Adds one invoice row below the last record, formatted like the row above (V28 ArchiveCurrentInvoice).</summary>
    public static void Append(IWorkbookEditor ed, HistoryRecord rec)
    {
        var (ws, src, l, records) = Open(ed);
        if (records.Any(x => string.Equals(x.InvoiceNo, rec.InvoiceNo, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Invoice number {rec.InvoiceNo} already exists in INVOICE HISTORY.");

        int r = LastDataRow(src, l) + 1;
        int firstCol = new[] { l.ColNo, l.ColName, l.ColAmount, l.ColDate, l.ColInvoice, l.ColStatus }.Where(c => c > 0).Min();
        int lastCol = l.LastCol;
        if (r > l.FirstDataRow) ws.CopyRow(r - 1, r, firstCol, lastCol);
        ws.ShowRow(r);

        int maxNo = records.Select(x => x.No)
            .Select(v => v.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[4..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        void Put(int c, object? v) { if (c > 0) ws.Set(r, c, v); }
        Put(l.ColNo, $"INV-{maxNo + 1}");
        Put(l.ColName, rec.Name);
        Put(l.ColAmount, rec.Amount is { } amt ? (double)amt : null);
        if (l.ColDate > 0)
        {
            Put(l.ColDate, rec.Date);
            if (rec.Date != null) ws.SetNumberFormat(r, l.ColDate, "dd/mm/yyyy");
        }
        Put(l.ColInvoice, rec.InvoiceNo);
        Put(l.ColStatus, string.IsNullOrEmpty(rec.Status) ? "File available" : rec.Status);
        if (l.ColStatus > 0) ws.SetFontColor(r, l.ColStatus, 0, 128, 0);
        Put(l.ColType, rec.PartyType);
        Put(l.ColRecordId, rec.RecordId);
        Put(l.ColPath, rec.FilePath);
        Put(l.ColTemplate, rec.Template);
        foreach (var (col, def) in l.Actions)
        {
            var above = r > l.FirstDataRow ? src.Text(r - 1, col).Trim() : "";
            ws.Set(r, col, above.Length > 0 ? above : def);
        }

        // the print area follows the last record (as V28 UpdateHistoryPrintArea did)
        if (ws.GetPrintArea() is { } pa && pa.R2 < r) ws.SetPrintArea(pa.R1, pa.C1, r, pa.C2);

        // V28: C3 shows the number of invoices ("84 Invoice")
        if (l.IsV28 && src.Raw(3, 3) is double or decimal) ws.Set(3, 3, (double)(records.Count + 1));
    }

    private static HistoryRecord Find(List<HistoryRecord> recs, string invoiceNo) =>
        recs.FirstOrDefault(r => string.Equals(r.InvoiceNo, invoiceNo, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Invoice {invoiceNo} is no longer in INVOICE HISTORY.");

    /// <summary>Week start chosen on the V28 sheet (H5), if any.</summary>
    public static string? ReadWeekStart(string path)
    {
        try
        {
            using var wb = PackageRepair.OpenWorkbook(path);
            var ws = Sheet(wb);
            var t = ExcelTableStore.CellText(ws.Cell("H5")).Trim();
            return Enum.GetNames<DayOfWeek>().Any(d => d.Equals(t, StringComparison.OrdinalIgnoreCase)) ? t : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes an invoice's history row (remaining rows are renumbered). With <paramref name="deleteFile"/> the archived
    /// file is checked first (it must be inside the BATCH STUDIO archive, like V28 "Remove") and its path is returned:
    /// delete it with <see cref="DeleteArchivedFile"/> once the workbook was saved.
    /// </summary>
    public static string? Remove(IWorkbookEditor ed, string archiveRoot, string invoiceNo, bool deleteFile)
    {
        var (ws, _, l, recs) = Open(ed);
        var rec = Find(recs, invoiceNo);
        string? file = null;
        if (deleteFile)
        {
            file = ResolvePath(archiveRoot, rec.FilePath);
            if (!File.Exists(file)) throw new FileNotFoundException("The archived file is missing; remove the record only.", file);
            if (!Path.GetFullPath(file).StartsWith(ArchivePrefix(archiveRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The invoice file is outside the BATCH STUDIO archive; it was not deleted.");
        }
        ws.DeleteRow(rec.Row);
        // renumber INV-1..n in sheet order (V28 ArchiveReindexHistory), only where the number changes
        int n = 0;
        foreach (var r in recs.Where(x => x.Row != rec.Row))
        {
            int row = r.Row > rec.Row ? r.Row - 1 : r.Row;
            n++;
            if (l.ColNo > 0 && r.No != $"INV-{n}") ws.Set(row, l.ColNo, $"INV-{n}");
        }
        if (l.IsV28 && ws.Read(3, 3, 3, 3)[0, 0] is double or decimal) ws.Set(3, 3, (double)n);
        // print area ends at the last remaining record
        if (ws.GetPrintArea() is { } pa)
        {
            int lastRow = recs.Where(x => x.Row != rec.Row).Select(x => x.Row > rec.Row ? x.Row - 1 : x.Row).DefaultIfEmpty(l.FirstDataRow).Max();
            ws.SetPrintArea(pa.R1, pa.C1, Math.Max(pa.R1, lastRow), pa.C2);
        }
        return file;
    }

    private static string ArchivePrefix(string archiveRoot) =>
        Path.GetFullPath(archiveRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>Deletes an archived invoice file and its party folder when that became empty (never SUPPLIERS / CLIENTS).</summary>
    public static void DeleteArchivedFile(string file, string archiveRoot)
    {
        var archive = ArchivePrefix(archiveRoot);
        if (!Path.GetFullPath(file).StartsWith(archive, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(file);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var parent = Path.GetDirectoryName(dir)!;
            bool isTypeFolder = string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, archive, StringComparison.OrdinalIgnoreCase);
            bool isArchive = string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, archive, StringComparison.OrdinalIgnoreCase);
            if (!isTypeFolder && !isArchive && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Points a record at a moved invoice file (V28 "Reassign file").</summary>
    public static void Relink(IWorkbookEditor ed, string invoiceNo, string newFile)
    {
        var (ws, _, l, recs) = Open(ed);
        var rec = Find(recs, invoiceNo);
        if (l.ColPath > 0) ws.Set(rec.Row, l.ColPath, newFile);
        SetStatus(ws, l, rec, true);
    }

    /// <summary>Statuses that differ from the files on disk (cheap check before opening the workbook).</summary>
    public static int StatusChangesNeeded(string archiveRoot, IEnumerable<HistoryRecord> recs) =>
        recs.Count(r => (File.Exists(ResolvePath(archiveRoot, r.FilePath)) ? "File available" : "File missing") != r.Status);

    /// <summary>Writes "File available" / "File missing" (green / red) where it changed. Returns how many rows changed.</summary>
    public static int UpdateStatuses(IWorkbookEditor ed, string archiveRoot)
    {
        var (ws, _, l, recs) = Open(ed);
        int changed = 0;
        foreach (var r in recs)
            if (SetStatus(ws, l, r, File.Exists(ResolvePath(archiveRoot, r.FilePath)))) changed++;
        return changed;
    }

    private static bool SetStatus(ISheetEditor ws, HistoryLayout l, HistoryRecord rec, bool available)
    {
        if (l.ColStatus == 0) return false;
        var text = available ? "File available" : "File missing";
        if (rec.Status == text) return false;
        ws.Set(rec.Row, l.ColStatus, text);
        if (available) ws.SetFontColor(rec.Row, l.ColStatus, 0, 128, 0);
        else ws.SetFontColor(rec.Row, l.ColStatus, 210, 0, 0);
        return true;
    }

    // ------------------------------------------------------------------ file-based wrappers (stand-alone .xlsx)
    private static void OnFile(string root, string path, int keepBackups, Action<IWorkbookEditor> change)
    {
        using var ed = ClosedXmlWorkbookEditor.Open(path);
        change(ed);
        BackupService.Backup(root, DataSets.InvoiceHistory, keepBackups);
        ed.Save();
    }

    public static void Append(string root, string path, HistoryRecord rec, int keepBackups = 30) =>
        OnFile(root, path, keepBackups, ed => Append(ed, rec));

    public static void Remove(string root, string path, string invoiceNo, bool deleteFile, int keepBackups = 30)
    {
        var archive = Path.Combine(root, DatabaseLayout.ArchiveFolder);
        string? file = null;
        OnFile(root, path, keepBackups, ed => file = Remove(ed, archive, invoiceNo, deleteFile));
        if (file != null) DeleteArchivedFile(file, archive);
    }

    public static void Relink(string root, string path, string invoiceNo, string newFile, int keepBackups = 30) =>
        OnFile(root, path, keepBackups, ed => Relink(ed, invoiceNo, newFile));

    public static int UpdateStatuses(string root, string path, int keepBackups = 30)
    {
        var archive = Path.Combine(root, DatabaseLayout.ArchiveFolder);
        if (StatusChangesNeeded(archive, Read(path)) == 0) return 0;
        int n = 0;
        OnFile(root, path, keepBackups, ed => n = UpdateStatuses(ed, archive));
        return n;
    }

    /// <summary>
    /// Copy of the history sheet showing only <paramref name="include"/> rows (others hidden), with the period name in
    /// the filter cell, the visible count / total and the print area set - ready for PDF export with the sheet's own look.
    /// </summary>
    public static void BuildReport(string path, Func<HistoryRecord, bool> include, string periodLabel, string outputPath)
    {
        using var wb = PackageRepair.OpenWorkbook(path);
        var ws = Sheet(wb);
        var l = Detect(ws);
        var recs = Read(ws, l);
        var shown = recs.Where(include).ToList();
        var shownRows = shown.Select(r => r.Row).ToHashSet();
        foreach (var r in recs)
        {
            if (shownRows.Contains(r.Row)) ws.Row(r.Row).Unhide();
            else ws.Row(r.Row).Hide();
        }
        if (l.IsV28)
        {
            if (ws.Cell("C3").Value.IsNumber || ws.Cell("C3").IsEmpty()) ws.Cell("C3").Value = shown.Count;
            ws.Cell("E5").Value = periodLabel;
            var total = ws.Cell("D7");
            if (!total.HasFormula && (total.Value.IsNumber || total.IsEmpty()))
                total.Value = (double)shown.Sum(x => x.Amount ?? 0);
        }
        int last = recs.Count > 0 ? recs.Max(r => r.Row) : l.FirstDataRow;
        var pa = ws.PageSetup.PrintAreas.FirstOrDefault()?.RangeAddress;
        int r1 = pa?.FirstAddress.RowNumber ?? 1, c1 = pa?.FirstAddress.ColumnNumber ?? 1;
        int c2 = pa?.LastAddress.ColumnNumber ?? Math.Max(l.ColStatus, l.ColInvoice);
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add(r1, c1, last, c2);
        wb.SaveAs(outputPath);
        PackageRepair.StripControls(outputPath);
    }

    /// <summary>
    /// Resolves a stored invoice path against the archive root (the BATCH STUDIO folder). Old rows hold absolute
    /// paths of an earlier archive location (e.g. D:\...\BATCH STUDIO\CLIENTS\x\Mad-1.xlsx); when that file is gone,
    /// the part from CLIENTS\ / SUPPLIERS\ on is looked up under the current archive (as the V28 VBA does).
    /// </summary>
    public static string ResolvePath(string archiveRoot, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        var norm = stored.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(norm))
        {
            var prefix = DatabaseLayout.ArchiveFolder + Path.DirectorySeparatorChar;
            if (norm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) norm = norm[prefix.Length..];
            return Path.Combine(archiveRoot, norm);
        }
        if (File.Exists(norm)) return norm;
        var sep = Path.DirectorySeparatorChar;
        var upper = norm.ToUpperInvariant();
        int pos = Math.Max(upper.LastIndexOf($"{sep}{DatabaseLayout.ArchiveClients}{sep}", StringComparison.Ordinal),
                           upper.LastIndexOf($"{sep}{DatabaseLayout.ArchiveSuppliers}{sep}", StringComparison.Ordinal));
        if (pos >= 0)
        {
            var local = Path.Combine(archiveRoot, norm[(pos + 1)..]);
            if (File.Exists(local)) return local;
        }
        return norm;
    }
}
