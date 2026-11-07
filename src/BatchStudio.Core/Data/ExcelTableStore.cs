using System.Globalization;
using ClosedXML.Excel;

namespace BatchStudio.Core.Data;

/// <summary>Raised when the Excel file is open in Excel (or another program) and cannot be written.</summary>
public sealed class FileLockedException(string path, Exception inner)
    : IOException($"\"{Path.GetFileName(path)}\" is open in another program.", inner)
{
    public string FilePath { get; } = path;
}

/// <summary>Reads and writes a simple table (row 1 = headers) from/to an Excel worksheet.</summary>
public static class ExcelTableStore
{
    public static TableData Load(string path, string sheetName, int keyColumn = 0)
    {
        // FileShare.ReadWrite lets us read while the file is open in Excel.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var wb = new XLWorkbook(fs);
        var ws = wb.Worksheets.TryGetWorksheet(sheetName, out var named) ? named : wb.Worksheet(1);
        return Read(ws, keyColumn);
    }

    public static TableData Read(IXLWorksheet ws, int keyColumn = 0)
    {
        var t = new TableData { KeyColumn = keyColumn };
        int lastCol = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        // Only columns that have a header are part of the table.
        var cols = new List<int>();
        for (int c = 1; c <= lastCol; c++)
        {
            var h = CellText(ws.Cell(1, c)).Trim();
            if (h.Length == 0) continue;
            cols.Add(c);
            t.Headers.Add(h);
        }
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        var numeric = new int[cols.Count];
        var dates = new int[cols.Count];
        var filled = new int[cols.Count];
        for (int r = 2; r <= lastRow; r++)
        {
            var row = new string[cols.Count];
            bool any = false;
            for (int i = 0; i < cols.Count; i++)
            {
                var cell = ws.Cell(r, cols[i]);
                var v = cell.HasFormula ? cell.CachedValue : cell.Value;
                string s;
                switch (v.Type)
                {
                    case XLDataType.Number:
                        s = v.GetNumber().ToString("R", CultureInfo.InvariantCulture);
                        numeric[i]++;
                        break;
                    case XLDataType.DateTime:
                        s = v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        dates[i]++;
                        break;
                    case XLDataType.Boolean:
                        s = v.GetBoolean() ? "TRUE" : "FALSE";
                        break;
                    case XLDataType.Blank:
                        s = "";
                        break;
                    default:
                        s = v.ToString();
                        break;
                }
                // Cells holding only spaces are treated as empty (the old workbook has many " " cells).
                if (string.IsNullOrWhiteSpace(s)) s = "";
                if (s.Length > 0) { any = true; filled[i]++; }
                row[i] = s;
            }
            if (any) t.Rows.Add(row);
        }
        for (int i = 0; i < cols.Count; i++)
        {
            var kind = ColumnKind.Text;
            if (filled[i] > 0 && dates[i] * 2 > filled[i]) kind = ColumnKind.Date;
            else if (filled[i] > 0 && numeric[i] * 2 > filled[i]) kind = ColumnKind.Number;
            t.Kinds.Add(kind);
        }
        return t;
    }

    /// <summary>
    /// Writes the table into the existing workbook (formatting, other sheets and column widths are kept).
    /// Saves to a temp file first and then replaces the original, so a failed save never corrupts it.
    /// </summary>
    public static void Save(string path, string sheetName, TableData table)
    {
        EnsureWritable(path);
        // ClosedXML keeps reading the source stream until SaveAs, so load the file into memory
        // (the stream must stay open while the workbook is used).
        using var source = new MemoryStream();
        XLWorkbook wb;
        if (File.Exists(path))
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                fs.CopyTo(source);
            source.Position = 0;
            wb = new XLWorkbook(source);
        }
        else wb = new XLWorkbook();

        using (wb)
        {
            if (!wb.Worksheets.TryGetWorksheet(sheetName, out var ws))
                ws = wb.Worksheets.Count > 0 ? wb.Worksheet(1) : wb.Worksheets.Add(sheetName);

            // Map headers to existing columns; append new headers at the end.
            int lastCol = Math.Max(ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0, 0);
            var colOf = new int[table.Headers.Count];
            for (int i = 0; i < table.Headers.Count; i++)
            {
                int found = 0;
                for (int c = 1; c <= lastCol; c++)
                    if (string.Equals(CellText(ws.Cell(1, c)).Trim(), table.Headers[i], StringComparison.OrdinalIgnoreCase)) { found = c; break; }
                if (found == 0)
                {
                    found = ++lastCol;
                    ws.Cell(1, found).Value = table.Headers[i];
                    ws.Cell(1, found).Style.Font.Bold = true;
                }
                colOf[i] = found;
            }

            int oldLast = ws.LastRowUsed()?.RowNumber() ?? 1;
            if (oldLast >= 2)
                foreach (var c in colOf)
                    ws.Range(2, c, oldLast, c).Clear(XLClearOptions.Contents);

            for (int r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                for (int i = 0; i < table.Headers.Count && i < row.Length; i++)
                {
                    var cell = ws.Cell(r + 2, colOf[i]);
                    var s = row[i] ?? "";
                    var kind = i < table.Kinds.Count ? table.Kinds[i] : ColumnKind.Text;
                    if (s.Length == 0) { cell.Clear(XLClearOptions.Contents); continue; }
                    if (kind == ColumnKind.Number && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        cell.Value = d;
                    else if (kind == ColumnKind.Date && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        cell.Value = dt;
                    else
                        cell.Value = s;
                }
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, $".~{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp.xlsx");
            wb.SaveAs(tmp);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException ex)
            {
                TryDelete(tmp);
                throw new FileLockedException(path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(tmp);
                throw new FileLockedException(path, ex);
            }
        }
    }

    /// <summary>Throws <see cref="FileLockedException"/> when another program holds the file open.</summary>
    public static void EnsureWritable(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new FileLockedException(path, ex); }
        catch (UnauthorizedAccessException ex) { throw new FileLockedException(path, ex); }
    }

    public static bool IsLocked(string path)
    {
        try { EnsureWritable(path); return false; }
        catch (FileLockedException) { return true; }
    }

    public static string CellText(IXLCell cell)
    {
        try
        {
            var v = cell.HasFormula ? cell.CachedValue : cell.Value;
            return v.Type switch
            {
                XLDataType.Blank => "",
                XLDataType.Number => v.GetNumber().ToString("R", CultureInfo.InvariantCulture),
                XLDataType.DateTime => v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
