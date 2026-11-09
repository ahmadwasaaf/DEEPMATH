using System.Globalization;
using System.Text.RegularExpressions;
using BatchStudio.Core.Data;
using BatchStudio.Core.Templates;
using ClosedXML.Excel;

namespace BatchStudio.Core.Invoices;

/// <summary>Everything needed to produce one invoice file.</summary>
public sealed class InvoiceDraft
{
    public required string TemplatePath { get; init; }
    public required TemplateMap Map { get; init; }
    public string PartyType { get; set; } = "SUPPLIERS";          // SUPPLIERS / CLIENTS
    public string PartyName { get; set; } = "";
    public string? NativeName { get; set; }
    public string? Address { get; set; }
    public string? Bank { get; set; }
    public string? CategoryText { get; set; }
    public string InvoiceNo { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Target { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];
    /// <summary>The BATCH STUDIO workbook the template sheet comes from (lets Excel write the invoice as-is).</summary>
    public string? WorkbookPath { get; set; }
    /// <summary>Supplier theme colour (VBA colour number) from CLIENT THEME MEMORY, or null.</summary>
    public int? ThemeRgb { get; set; }

    public decimal? VatRate => Map.VatRate is > 0 ? (decimal)Map.VatRate.Value : null;
    public InvoiceCurrency Currency => Map.EffectiveCurrency == "SAR" ? InvoiceCurrency.SAR : InvoiceCurrency.USD;
    public long NetCents => Lines.Sum(l => l.AmountCents);
    public long VatCents => VatRate is { } r ? Lines.Sum(l => InvoiceAllocator.VatCents(l.AmountCents, r)) : 0;
    public decimal Net => NetCents / 100m;
    public decimal Vat => VatCents / 100m;
    public decimal Total => (NetCents + VatCents) / 100m;
    public string Words => AmountWords.Format(Total, Currency);
}

/// <summary>
/// Writes an <see cref="InvoiceDraft"/> into a copy of its template: header fields, item rows
/// (rows are added / removed so the table fits the item count), totals and amount in words.
/// Like the V28 archive, the result contains values only (no formulas, no external links).
/// </summary>
public static class TemplateFiller
{
    public static void Fill(InvoiceDraft d, string outputPath)
    {
        var map = d.Map;
        if (map.Items == null) throw new InvalidOperationException("The template's items table was not detected. Open Templates and detect again.");
        if (d.Lines.Count == 0) throw new InvalidOperationException("The invoice has no items.");

        using var wb = PackageRepair.OpenWorkbook(d.TemplatePath);
        var ws = wb.Worksheets.TryGetWorksheet(map.Sheet, out var named) ? named
               : wb.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible);

        // fields that the saved link may not have yet (older links): detect them now
        var fields = new Dictionary<string, FieldMapping>(map.Fields);
        if (!fields.ContainsKey(TemplateFields.Category))
        {
            var fresh = TemplateDetector.Detect(SheetModel.FromWorksheet(ws));
            if (fresh.Fields.TryGetValue(TemplateFields.Category, out var cat)) fields[TemplateFields.Category] = cat;
        }

        var items = map.Items;
        int first = items.FirstRow, capacity = items.Capacity, n = d.Lines.Count;
        int delta = n - capacity;
        var printArea = ws.PageSetup.PrintAreas.FirstOrDefault()?.RangeAddress;
        int paR1 = printArea?.FirstAddress.RowNumber ?? 0, paC1 = printArea?.FirstAddress.ColumnNumber ?? 0;
        int paR2 = printArea?.LastAddress.RowNumber ?? 0, paC2 = printArea?.LastAddress.ColumnNumber ?? 0;

        // pictures below the table move with it
        var pictures = ws.Pictures.Select(p => (Pic: p, Row: p.TopLeftCell.Address.RowNumber, Col: p.TopLeftCell.Address.ColumnNumber)).ToList();
        // single-row merges inside an item row (e.g. G:H in Madar) are re-created on new rows
        var rowMerges = ws.MergedRanges.Where(m => m.RangeAddress.FirstAddress.RowNumber == first && m.RangeAddress.LastAddress.RowNumber == first)
                          .Select(m => (C1: m.RangeAddress.FirstAddress.ColumnNumber, C2: m.RangeAddress.LastAddress.ColumnNumber)).ToList();

        if (delta > 0)
        {
            ws.Row(items.LastRow).InsertRowsBelow(delta);
            for (int i = 0; i < delta; i++)
            {
                int r = items.LastRow + 1 + i;
                int srcRow = first + ((r - first) % 2);   // keep the alternating row colours
                ws.Row(r).Height = ws.Row(srcRow).Height;
                foreach (var cell in ws.Row(srcRow).CellsUsed(XLCellsUsedOptions.All))
                    ws.Cell(r, cell.Address.ColumnNumber).Style = cell.Style;
                foreach (var (c1, c2) in rowMerges)
                    if (!ws.Cell(r, c1).IsMerged()) ws.Range(r, c1, r, c2).Merge();
            }
        }
        else if (delta < 0)
        {
            ws.Rows(first + n, items.LastRow).Delete();
        }
        int lastRow = first + n - 1;

        string? Shift(string? a1)
        {
            if (a1 == null || !TemplateDetector.TryParseRef(a1, out var r, out var c)) return a1;
            return r > items.LastRow ? TemplateDetector.Ref(r + delta, c) : a1;
        }
        IXLCell? CellOf(string field) =>
            fields.TryGetValue(field, out var m) && Shift(m.Cell) is { } a ? ws.Cell(a) : null;

        // header fields
        Set(CellOf(TemplateFields.PartyName), d.PartyName);
        if (d.NativeName != null) Set(CellOf(TemplateFields.PartyNativeName), d.NativeName);
        Set(CellOf(TemplateFields.InvoiceNo), d.InvoiceNo);
        if (CellOf(TemplateFields.IssueDate) is { } dc)
        {
            dc.Value = d.Date;
            if (!dc.Style.NumberFormat.Format.Contains('y', StringComparison.OrdinalIgnoreCase) || dc.Style.NumberFormat.NumberFormatId == 0)
                dc.Style.NumberFormat.Format = "dd/mm/yyyy";
        }
        if (d.Bank != null && CellOf(TemplateFields.Bank) is { } bc) { Set(bc, d.Bank); FitRowHeight(bc, d.Bank); }
        if (d.Address != null && CellOf(TemplateFields.Address) is { } ac) { Set(ac, d.Address); FitRowHeight(ac, d.Address); }
        if (d.CategoryText != null) Set(CellOf(TemplateFields.Category), d.CategoryText);

        // item rows
        int Col(string key) => items.Columns.TryGetValue(key, out var letter) ? XLHelper.GetColumnNumberFromLetter(letter) : 0;
        int cCode = Col("code"), cDesc = Col("description"), cQty = Col("qty"), cUnit = Col("unit"), cPrice = Col("price"), cAmt = Col("amount");
        for (int i = 0; i < n; i++)
        {
            int r = first + i;
            var l = d.Lines[i];
            if (cCode > 0) ws.Cell(r, cCode).Value = l.Code;
            if (cDesc > 0) ws.Cell(r, cDesc).Value = l.Description;
            if (cQty > 0) ws.Cell(r, cQty).Value = l.Quantity;
            if (cUnit > 0) ws.Cell(r, cUnit).Value = "PCS";
            if (cPrice > 0) ws.Cell(r, cPrice).Value = (double)l.UnitPrice;
            if (cAmt > 0) ws.Cell(r, cAmt).Value = (double)l.Amount;
        }

        // totals
        if (CellOf(TemplateFields.VatAmount) is { } vc)
        {
            vc.Value = (double)d.Vat;
            // subtotal sits directly above the VAT cell (Madar: G28 / G29 / G30)
            var sub = ws.Cell(vc.Address.RowNumber - 1, vc.Address.ColumnNumber);
            if (sub.HasFormula || sub.Value.IsNumber) sub.Value = (double)d.Net;
        }
        if (CellOf(TemplateFields.Total) is { } tc) tc.Value = (double)d.Total;
        if (CellOf(TemplateFields.AmountWords) is { } wc) Set(wc, d.Words);

        // values only: freeze every remaining formula (external links / TODAY() etc. would break or drift)
        foreach (var cell in ws.CellsUsed(XLCellsUsedOptions.Contents).Where(c => c.HasFormula).ToList())
        {
            XLCellValue v;
            try { v = cell.CachedValue; } catch { v = Blank.Value; }
            cell.Value = v;
        }

        // print area follows the table
        if (printArea != null && delta != 0)
        {
            ws.PageSetup.PrintAreas.Clear();
            ws.PageSetup.PrintAreas.Add(paR1, paC1, Math.Max(paR1, paR2 + delta), paC2);
        }
        foreach (var (pic, row, col) in pictures)
        {
            if (row <= items.LastRow || delta == 0) continue;
            int now = pic.TopLeftCell.Address.RowNumber;
            if (now == row) pic.MoveTo(ws.Cell(Math.Max(1, row + delta), col)); // ClosedXML did not move it with the rows
        }

        // archive must not carry links to the old workbook / other files
        foreach (var name in wb.DefinedNames.ToList())
        {
            try { if (name.RefersTo.Contains('[')) wb.DefinedNames.Delete(name.Name); } catch { }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        wb.SaveAs(outputPath);
        PackageRepair.StripControls(outputPath);
    }

    private static void Set(IXLCell? cell, string value)
    {
        if (cell == null) return;
        cell.Value = value;
    }

    /// <summary>Grows a row so a multi-line block (bank details, address) is fully visible.</summary>
    private static void FitRowHeight(IXLCell cell, string text)
    {
        int lines = text.Split('\n').Length;
        var row = cell.WorksheetRow();
        double needed = lines * 15.0 + 6;
        if (cell.IsMerged())
        {
            var m = cell.MergedRange();
            int rows = m.RangeAddress.LastAddress.RowNumber - m.RangeAddress.FirstAddress.RowNumber + 1;
            if (rows > 1) return; // multi-row block: leave the designed height
        }
        if (row.Height < needed) row.Height = needed;
        cell.Style.Alignment.WrapText = true;
    }
}
