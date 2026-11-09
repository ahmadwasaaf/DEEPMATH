using System.Globalization;
using BatchStudio.Core.Data;

namespace BatchStudio.Core.Workbook;

/// <summary>
/// Writes a table (row 1 = headers) into a sheet of the workbook, changing only the cells whose value differs -
/// formatting, other columns and everything else on the sheet stay as they are.
/// </summary>
public static class TableWriter
{
    /// <summary>Returns the number of cells written.</summary>
    public static int Save(ISheetEditor sheet, TableData table)
    {
        var src = BlockSource.ReadAll(sheet, 80);
        int lastCol = 0;
        for (int c = 1; c <= src.Columns; c++) if (src.Text(1, c).Length > 0) lastCol = c;

        int written = 0;
        var colOf = new int[table.Headers.Count];
        for (int i = 0; i < table.Headers.Count; i++)
        {
            int found = 0;
            for (int c = 1; c <= lastCol; c++)
                if (string.Equals(src.Text(1, c).Trim(), table.Headers[i].Trim(), StringComparison.OrdinalIgnoreCase)) { found = c; break; }
            if (found == 0)
            {
                found = ++lastCol;
                sheet.Set(1, found, table.Headers[i]);
                written++;
            }
            colOf[i] = found;
        }

        int oldLast = 1;
        foreach (var c in colOf) oldLast = Math.Max(oldLast, src.LastRow(c));
        int newLast = table.Rows.Count + 1;
        int last = Math.Max(oldLast, newLast);

        // new rows look like the rows above them (borders, fonts, wrapping, height; banding kept by copying the row two above)
        if (newLast > oldLast && oldLast >= 2)
            for (int r = oldLast + 1; r <= newLast; r++)
                sheet.CopyRow(r - 2 >= 2 ? r - 2 : oldLast, r, 1, Math.Max(1, lastCol));

        for (int i = 0; i < colOf.Length; i++)
        {
            var kind = i < table.Kinds.Count ? table.Kinds[i] : ColumnKind.Text;
            // contiguous runs of changed cells are written in one call
            int runStart = -1;
            var run = new List<object?>();
            void Flush()
            {
                if (runStart < 0) return;
                var block = new object?[run.Count, 1];
                for (int k = 0; k < run.Count; k++) block[k, 0] = run[k];
                sheet.Write(runStart, colOf[i], block);
                written += run.Count;
                runStart = -1;
                run.Clear();
            }
            for (int r = 2; r <= last; r++)
            {
                var row = r - 2 < table.Rows.Count ? table.Rows[r - 2] : null;
                var s = row != null && i < row.Length ? row[i] ?? "" : "";
                var old = src.Text(r, colOf[i]);
                // rows added below the old data were copied from a row above: every cell is written
                if (!(r > oldLast && oldLast >= 2) && Same(old, s, kind)) { Flush(); continue; }
                if (runStart < 0) runStart = r;
                run.Add(ToValue(s, kind));
            }
            Flush();
        }
        return written;
    }

    private static bool Same(string old, string now, ColumnKind kind)
    {
        if (old == now) return true;
        if (old.Length == 0 || now.Length == 0) return false;
        if (double.TryParse(old, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Math.Abs(a - b) < 1e-9;
        return false;
    }

    public static object? ToValue(string s, ColumnKind kind)
    {
        if (s.Length == 0) return null;
        if (kind == ColumnKind.Number && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (kind == ColumnKind.Date && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        return s;
    }
}
