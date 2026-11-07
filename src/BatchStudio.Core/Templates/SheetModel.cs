using System.Text;
using ClosedXML.Excel;
using BatchStudio.Core.Data;

namespace BatchStudio.Core.Templates;

public sealed record CellInfo(int Row, int Col, string Text, string? Formula, double FontSize, bool IsNumber);

public sealed record MergedArea(int Row1, int Col1, int Row2, int Col2);

/// <summary>Plain snapshot of a worksheet used by the detector (keeps the detection logic testable and ClosedXML-free).</summary>
public sealed class SheetModel
{
    public string Name { get; init; } = "";
    public int Row1 { get; set; } = 1;
    public int Col1 { get; set; } = 1;
    public int Row2 { get; set; }
    public int Col2 { get; set; }
    public Dictionary<(int Row, int Col), CellInfo> Cells { get; } = new();
    public List<MergedArea> Merged { get; } = [];
    /// <summary>Workbook defined names that point into this sheet: name -> (row, col).</summary>
    public Dictionary<string, (int Row, int Col)> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Defined names that point to another sheet: name -> sheet.</summary>
    public Dictionary<string, string> ForeignNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(int row, int col, string text, string? formula = null, double fontSize = 11, bool isNumber = false)
        => Cells[(row, col)] = new CellInfo(row, col, Normalize(text), formula, fontSize, isNumber);

    public static string Normalize(string? s) =>
        (s ?? "").Normalize(NormalizationForm.FormKC).Replace("ـ", "").Trim();

    public static SheetModel FromWorksheet(IXLWorksheet ws)
    {
        var m = new SheetModel { Name = ws.Name };
        var pa = ws.PageSetup.PrintAreas.FirstOrDefault();
        if (pa != null)
        {
            m.Row1 = pa.RangeAddress.FirstAddress.RowNumber;
            m.Col1 = pa.RangeAddress.FirstAddress.ColumnNumber;
            m.Row2 = pa.RangeAddress.LastAddress.RowNumber;
            m.Col2 = pa.RangeAddress.LastAddress.ColumnNumber;
        }
        else
        {
            m.Row2 = ws.LastRowUsed()?.RowNumber() ?? 1;
            m.Col2 = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        }

        foreach (var c in ws.CellsUsed(XLCellsUsedOptions.Contents))
        {
            int r = c.Address.RowNumber, col = c.Address.ColumnNumber;
            if (r < m.Row1 || r > m.Row2 || col < m.Col1 || col > m.Col2) continue;
            string? formula = null;
            try { if (c.HasFormula) formula = "=" + c.FormulaA1; } catch { }
            var text = formula != null ? formula : ExcelTableStore.CellText(c);
            if (string.IsNullOrWhiteSpace(text)) continue;
            bool isNum = false;
            try { isNum = !c.HasFormula && c.Value.Type == XLDataType.Number; } catch { }
            double size = 11;
            try { size = c.Style.Font.FontSize; } catch { }
            m.Add(r, col, text, formula, size, isNum);
        }

        foreach (var mr in ws.MergedRanges)
            m.Merged.Add(new MergedArea(mr.RangeAddress.FirstAddress.RowNumber, mr.RangeAddress.FirstAddress.ColumnNumber,
                                        mr.RangeAddress.LastAddress.RowNumber, mr.RangeAddress.LastAddress.ColumnNumber));

        void AddNames(IEnumerable<IXLDefinedName> names)
        {
            foreach (var dn in names)
            {
                try
                {
                    var first = dn.Ranges.FirstOrDefault();
                    if (first == null) continue;
                    var sheet = first.Worksheet?.Name ?? "";
                    var a = first.RangeAddress.FirstAddress;
                    if (string.Equals(sheet, ws.Name, StringComparison.OrdinalIgnoreCase))
                        m.Names[dn.Name] = (a.RowNumber, a.ColumnNumber);
                    else if (sheet.Length > 0)
                        m.ForeignNames[dn.Name] = sheet;
                }
                catch { /* names with formulas (INDEX/OFFSET) are not single cells */ }
            }
        }
        try { AddNames(ws.Workbook.DefinedNames); } catch { }
        try { AddNames(ws.DefinedNames); } catch { }
        return m;
    }

    /// <summary>Loads a template file. Uses the sheet named like the file, else the first visible sheet.</summary>
    public static SheetModel Load(string path, string? sheetName = null)
    {
        using var wb = PackageRepair.OpenWorkbook(path);
        IXLWorksheet? ws = null;
        if (sheetName != null) wb.Worksheets.TryGetWorksheet(sheetName, out ws);
        if (ws == null) wb.Worksheets.TryGetWorksheet(Path.GetFileNameWithoutExtension(path), out ws);
        ws ??= wb.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? wb.Worksheet(1);
        return FromWorksheet(ws);
    }
}
