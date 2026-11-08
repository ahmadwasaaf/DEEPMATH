using System.Globalization;
using BatchStudio.Core.Data;
using ClosedXML.Excel;

namespace BatchStudio.Core.Workbook;

/// <summary>
/// Changes cells of the BATCH STUDIO workbook. In the program this is Microsoft Excel itself (the open workbook
/// when the user has it open, otherwise a hidden Excel), so everything Excel keeps in the file - VBA, buttons,
/// shapes, validations - stays untouched. Tests use the ClosedXML implementation on plain .xlsx files.
/// </summary>
public interface IWorkbookEditor : IDisposable
{
    bool HasSheet(string name);
    ISheetEditor Sheet(string name);
    /// <summary>Saves the workbook (Excel: the user's open copy or the hidden one).</summary>
    void Save();
}

public interface ISheetEditor
{
    string Name { get; }
    /// <summary>Last row / column of the used range (1-based).</summary>
    int LastUsedRow { get; }
    int LastUsedColumn { get; }
    /// <summary>Values of r1..r2 × c1..c2 as a 0-based array: null, string, double, decimal, bool or DateTime.</summary>
    object?[,] Read(int r1, int c1, int r2, int c2);
    /// <summary>Writes a 0-based block starting at (r1, c1). Strings are stored as text, never parsed as numbers.</summary>
    void Write(int r1, int c1, object?[,] values);
    void Set(int r, int c, object? value);
    void SetNumberFormat(int r, int c, string format);
    void SetFontColor(int r, int c, int red, int green, int blue);
    /// <summary>Copies values and formatting of columns c1..c2 of a row to another row (and its height).</summary>
    void CopyRow(int fromRow, int toRow, int c1, int c2);
    void DeleteRow(int r);
    void ShowRow(int r);
    /// <summary>First print area as (r1, c1, r2, c2), or null.</summary>
    (int R1, int C1, int R2, int C2)? GetPrintArea();
    void SetPrintArea(int r1, int c1, int r2, int c2);
}

/// <summary>Reads text out of a sheet the same way, whether it comes from ClosedXML or from Excel.</summary>
public interface ICellSource
{
    object? Raw(int r, int c);
    string Text(int r, int c);
    /// <summary>Last row with a value in the column (0 when empty).</summary>
    int LastRow(int c);
}

public static class CellValues
{
    public static string Text(object? v) => v switch
    {
        null => "",
        string s => string.IsNullOrWhiteSpace(s) ? "" : s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => ((double)m).ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static object? FromXL(XLCellValue v) => v.Type switch
    {
        XLDataType.Blank => null,
        XLDataType.Number => v.GetNumber(),
        XLDataType.DateTime => v.GetDateTime(),
        XLDataType.TimeSpan => v.GetTimeSpan().TotalDays,
        XLDataType.Boolean => v.GetBoolean(),
        XLDataType.Error => null,
        _ => v.ToString(),
    };

    public static XLCellValue ToXL(object? v) => v switch
    {
        null => Blank.Value,
        string s => s,
        double d => d,
        decimal m => (double)m,
        int i => (double)i,
        long l => (double)l,
        bool b => b,
        DateTime dt => dt,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}

/// <summary>A block read from an editor (rows r1.., columns c1..) used as a cell source.</summary>
public sealed class BlockSource : ICellSource
{
    private readonly object?[,] _v;
    private readonly int _r1, _c1;

    public BlockSource(object?[,] values, int r1 = 1, int c1 = 1) { _v = values; _r1 = r1; _c1 = c1; }

    public static BlockSource ReadAll(ISheetEditor sheet, int maxColumns = 60)
    {
        int lr = Math.Max(1, sheet.LastUsedRow), lc = Math.Clamp(sheet.LastUsedColumn, 1, maxColumns);
        return new BlockSource(sheet.Read(1, 1, lr, lc));
    }

    public int Rows => _v.GetLength(0);
    public int Columns => _v.GetLength(1);

    public object? Raw(int r, int c)
    {
        int i = r - _r1, j = c - _c1;
        return i < 0 || j < 0 || i >= _v.GetLength(0) || j >= _v.GetLength(1) ? null : _v[i, j];
    }

    public string Text(int r, int c) => CellValues.Text(Raw(r, c));

    public int LastRow(int c)
    {
        int j = c - _c1;
        if (j < 0 || j >= _v.GetLength(1)) return 0;
        for (int i = _v.GetLength(0) - 1; i >= 0; i--)
            if (CellValues.Text(_v[i, j]).Length > 0) return i + _r1;
        return 0;
    }
}

public sealed class XLSource(IXLWorksheet ws) : ICellSource
{
    public object? Raw(int r, int c)
    {
        try
        {
            var cell = ws.Cell(r, c);
            return CellValues.FromXL(cell.HasFormula ? cell.CachedValue : cell.Value);
        }
        catch { return null; }
    }

    public string Text(int r, int c) => ExcelTableStore.CellText(ws.Cell(r, c)).Trim();

    public int LastRow(int c) => ws.Column(c).LastCellUsed()?.Address.RowNumber ?? 0;
}

// ======================================================================== ClosedXML implementation (tests, .xlsx)
public sealed class ClosedXmlWorkbookEditor : IWorkbookEditor
{
    private readonly string _path;
    private readonly XLWorkbook _wb;

    private ClosedXmlWorkbookEditor(string path)
    {
        _path = path;
        _wb = PackageRepair.OpenWorkbook(path);
    }

    public static IWorkbookEditor Open(string path)
    {
        ExcelTableStore.EnsureWritable(path);
        return new ClosedXmlWorkbookEditor(path);
    }

    public bool HasSheet(string name) => _wb.Worksheets.TryGetWorksheet(name, out _);

    public ISheetEditor Sheet(string name) =>
        _wb.Worksheets.TryGetWorksheet(name, out var ws) ? new XLSheetEditor(ws)
        : throw new InvalidOperationException($"Sheet \"{name}\" was not found in {Path.GetFileName(_path)}.");

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        var tmp = Path.Combine(dir, $".~{Path.GetFileNameWithoutExtension(_path)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(_path)}");
        _wb.SaveAs(tmp);
        try { File.Replace(tmp, _path, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { }
            throw new FileLockedException(_path, ex);
        }
    }

    public void Dispose() => _wb.Dispose();

    private sealed class XLSheetEditor(IXLWorksheet ws) : ISheetEditor
    {
        public string Name => ws.Name;
        public int LastUsedRow => ws.LastRowUsed(XLCellsUsedOptions.All)?.RowNumber() ?? 1;
        public int LastUsedColumn => ws.LastColumnUsed(XLCellsUsedOptions.All)?.ColumnNumber() ?? 1;

        public object?[,] Read(int r1, int c1, int r2, int c2)
        {
            var a = new object?[r2 - r1 + 1, c2 - c1 + 1];
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                {
                    var cell = ws.Cell(r, c);
                    a[r - r1, c - c1] = CellValues.FromXL(cell.HasFormula ? cell.CachedValue : cell.Value);
                }
            return a;
        }

        public void Write(int r1, int c1, object?[,] values)
        {
            for (int i = 0; i < values.GetLength(0); i++)
                for (int j = 0; j < values.GetLength(1); j++)
                    Set(r1 + i, c1 + j, values[i, j]);
        }

        public void Set(int r, int c, object? value)
        {
            var cell = ws.Cell(r, c);
            if (value == null) { cell.Clear(XLClearOptions.Contents); return; }
            cell.Value = CellValues.ToXL(value);
            if (value is DateTime && !cell.Style.NumberFormat.Format.Contains('y')) cell.Style.NumberFormat.Format = "dd/mm/yyyy";
        }

        public void SetNumberFormat(int r, int c, string format) => ws.Cell(r, c).Style.NumberFormat.Format = format;

        public void SetFontColor(int r, int c, int red, int green, int blue) =>
            ws.Cell(r, c).Style.Font.FontColor = XLColor.FromArgb(red, green, blue);

        public void CopyRow(int fromRow, int toRow, int c1, int c2)
        {
            for (int c = c1; c <= c2; c++)
            {
                ws.Cell(toRow, c).Style = ws.Cell(fromRow, c).Style;
                var src = ws.Cell(fromRow, c);
                ws.Cell(toRow, c).Value = src.HasFormula ? src.CachedValue : src.Value;
            }
            ws.Row(toRow).Height = ws.Row(fromRow).Height;
        }

        public void DeleteRow(int r) => ws.Row(r).Delete();
        public void ShowRow(int r) => ws.Row(r).Unhide();

        public (int R1, int C1, int R2, int C2)? GetPrintArea()
        {
            var pa = ws.PageSetup.PrintAreas.FirstOrDefault()?.RangeAddress;
            return pa == null ? null
                : (pa.FirstAddress.RowNumber, pa.FirstAddress.ColumnNumber, pa.LastAddress.RowNumber, pa.LastAddress.ColumnNumber);
        }

        public void SetPrintArea(int r1, int c1, int r2, int c2)
        {
            ws.PageSetup.PrintAreas.Clear();
            ws.PageSetup.PrintAreas.Add(r1, c1, r2, c2);
        }
    }
}
