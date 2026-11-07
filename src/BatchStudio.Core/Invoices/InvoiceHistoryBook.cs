using System.Globalization;
using BatchStudio.Core.Data;
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

    public static HistoryLayout Detect(IXLWorksheet ws)
    {
        for (int r = 1; r <= 40; r++)
        {
            var row = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 1; c <= 40; c++)
            {
                var t = ExcelTableStore.CellText(ws.Cell(r, c)).Trim();
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

    public static List<HistoryRecord> Read(IXLWorksheet ws, HistoryLayout l)
    {
        var list = new List<HistoryRecord>();
        int last = LastDataRow(ws, l);
        string T(int r, int c) => c > 0 ? ExcelTableStore.CellText(ws.Cell(r, c)).Trim() : "";
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
                var cell = ws.Cell(r, l.ColDate);
                try
                {
                    if (cell.Value.IsDateTime) date = cell.Value.GetDateTime();
                    else if (cell.Value.IsNumber) date = DateTime.FromOADate(cell.Value.GetNumber());
                    else if (DateTime.TryParse(cell.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
                }
                catch { }
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

    private static int LastDataRow(IXLWorksheet ws, HistoryLayout l)
    {
        int a = ws.Column(l.ColInvoice).LastCellUsed()?.Address.RowNumber ?? 0;
        int b = l.ColName > 0 ? ws.Column(l.ColName).LastCellUsed()?.Address.RowNumber ?? 0 : 0;
        return Math.Max(Math.Max(a, b), l.HeaderRow);
    }

    /// <summary>Adds one invoice row below the last record, formatted like the row above (V28 ArchiveCurrentInvoice).</summary>
    public static void Append(string root, string path, HistoryRecord rec, int keepBackups = 30)
    {
        ExcelTableStore.EnsureWritable(path);
        using var wb = PackageRepair.OpenWorkbook(path);
        var ws = Sheet(wb);
        var l = Detect(ws);
        var records = Read(ws, l);
        if (records.Any(x => string.Equals(x.InvoiceNo, rec.InvoiceNo, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Invoice number {rec.InvoiceNo} already exists in INVOICE HISTORY.");

        int r = LastDataRow(ws, l) + 1;
        int firstCol = new[] { l.ColNo, l.ColName, l.ColAmount, l.ColDate, l.ColInvoice, l.ColStatus }.Where(c => c > 0).Min();
        int lastCol = l.LastCol;
        if (r > l.FirstDataRow)
        {
            int prev = r - 1;
            for (int c = firstCol; c <= lastCol; c++) ws.Cell(r, c).Style = ws.Cell(prev, c).Style;
            ws.Row(r).Height = ws.Row(prev).Height;
        }
        ws.Row(r).Unhide();

        int maxNo = records.Select(x => x.No)
            .Select(v => v.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[4..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        void Put(int c, XLCellValue v) { if (c > 0) ws.Cell(r, c).Value = v; }
        Put(l.ColNo, $"INV-{maxNo + 1}");
        Put(l.ColName, rec.Name);
        if (rec.Amount is { } amt) Put(l.ColAmount, (double)amt);
        if (rec.Date is { } dt && l.ColDate > 0)
        {
            ws.Cell(r, l.ColDate).Value = dt;
            ws.Cell(r, l.ColDate).Style.NumberFormat.Format = "dd/mm/yyyy";
        }
        Put(l.ColInvoice, rec.InvoiceNo);
        Put(l.ColStatus, string.IsNullOrEmpty(rec.Status) ? "File available" : rec.Status);
        Put(l.ColType, rec.PartyType);
        Put(l.ColRecordId, rec.RecordId);
        Put(l.ColPath, rec.FilePath);
        Put(l.ColTemplate, rec.Template);
        foreach (var (col, def) in l.Actions)
        {
            var above = r > l.FirstDataRow ? ExcelTableStore.CellText(ws.Cell(r - 1, col)).Trim() : "";
            ws.Cell(r, col).Value = above.Length > 0 ? above : def;
        }

        // the print area follows the last record (as V28 UpdateHistoryPrintArea did)
        var pa = ws.PageSetup.PrintAreas.FirstOrDefault()?.RangeAddress;
        if (pa != null && pa.LastAddress.RowNumber < r)
        {
            int r1 = pa.FirstAddress.RowNumber, c1 = pa.FirstAddress.ColumnNumber, c2 = pa.LastAddress.ColumnNumber;
            ws.PageSetup.PrintAreas.Clear();
            ws.PageSetup.PrintAreas.Add(r1, c1, r, c2);
        }

        // V28: C3 shows the number of invoices ("84 Invoice")
        if (l.IsV28)
        {
            var count = ws.Cell("C3");
            if (count.Value.IsNumber) count.Value = records.Count + 1;
        }

        BackupService.Backup(root, DataSets.InvoiceHistory, keepBackups);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var tmp = Path.Combine(dir, $".~history.{Guid.NewGuid():N}.tmp.xlsx");
        wb.SaveAs(tmp);
        try { File.Replace(tmp, path, null); }
        catch (IOException ex)
        {
            try { File.Delete(tmp); } catch { }
            throw new FileLockedException(path, ex);
        }
    }

    /// <summary>
    /// Resolves a stored invoice path. Old V28 rows hold absolute paths of the previous archive location
    /// (e.g. D:\...\BATCH STUDIO\CLIENTS\x\Mad-1.xlsx); when that file is gone, the same path is looked up
    /// under this Database's BATCH STUDIO folder.
    /// </summary>
    public static string ResolvePath(string root, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        if (!Path.IsPathRooted(stored)) return Path.Combine(root, stored);
        if (File.Exists(stored)) return stored;
        var marker = Path.DirectorySeparatorChar + DatabaseLayout.ArchiveFolder + Path.DirectorySeparatorChar;
        var norm = stored.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        int i = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var local = Path.Combine(root, norm[(i + 1)..]);
            if (File.Exists(local)) return local;
        }
        return stored;
    }
}
