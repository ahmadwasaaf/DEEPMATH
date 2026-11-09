using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Workbook;

namespace BatchStudio.App.Services;

/// <summary>
/// Produces invoice files with Microsoft Excel from the template sheet of the BATCH STUDIO workbook, the same
/// way the workbook's own archive does (ArchiveEngine): the sheet keeps every shape, line, row height and
/// format; banking rows are fitted (BankFit); the supplier's theme colour is applied (Theme.RestoreClientTheme);
/// the sheet is copied to a new workbook, buttons / ActiveX removed, formulas frozen to values, links broken.
/// The BATCH STUDIO workbook itself is never changed (a private copy is used). Without Excel the ClosedXML
/// filler is used instead.
/// </summary>
public static class ExcelInvoiceEngine
{
    /// <summary>Why the last invoice was written without Excel (null when Excel wrote it).</summary>
    public static string? LastFallbackReason { get; private set; }

    public static void Fill(InvoiceDraft d, string outputPath)
    {
        LastFallbackReason = null;
        if (d.WorkbookPath == null || !File.Exists(d.WorkbookPath) || Type.GetTypeFromProgID("Excel.Application") == null)
        {
            TemplateFiller.Fill(d, outputPath);
            return;
        }
        try { FillWithExcel(d, outputPath); }
        catch (Exception ex)
        {
            LastFallbackReason = ex.Message;
            Log(ex);
            if (File.Exists(outputPath)) try { File.Delete(outputPath); } catch { }
            TemplateFiller.Fill(d, outputPath);
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BATCH STUDIO");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Excel invoice engine: {ex}\n");
        }
        catch { }
    }

    private static void FillWithExcel(InvoiceDraft d, string outputPath)
    {
        var map = d.Map;
        var items = map.Items ?? throw new InvalidOperationException("The template's items table was not detected.");
        if (d.Lines.Count == 0) throw new InvalidOperationException("The invoice has no items.");
        var sheetName = string.IsNullOrWhiteSpace(map.Sheet) ? Path.GetFileNameWithoutExtension(d.TemplatePath) : map.Sheet;

        var tmpBook = Path.Combine(Path.GetTempPath(), $"BS_tpl_{Guid.NewGuid():N}{Path.GetExtension(d.WorkbookPath)}");
        var tmpOut = Path.Combine(Path.GetTempPath(), $"BS_inv_{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(tmpBook, PackageRepair.ReadAllShared(d.WorkbookPath!));

        var type = Type.GetTypeFromProgID("Excel.Application")!;
        dynamic app = Activator.CreateInstance(type)!;
        dynamic? wb = null, outWb = null;   // outWb kept for the cleanup below
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;
            app.AskToUpdateLinks = false;
            app.AutomationSecurity = 3;     // macros off
            app.EnableEvents = false;
            wb = app.Workbooks.Open(tmpBook, 0, false);
            dynamic ws = FindSheet(wb, sheetName) ?? throw new InvalidOperationException($"Sheet \"{sheetName}\" was not found in the workbook.");

            // ---- item rows: add / remove so the table fits (alternating colours kept)
            int first = items.FirstRow, capacity = items.Capacity, n = d.Lines.Count, delta = n - capacity, lastRow = items.LastRow;
            if (delta > 0)
            {
                ws.Range[$"A{lastRow + 1}:A{lastRow + delta}"].EntireRow.Insert();
                for (int i = 0; i < delta; i++)
                {
                    int r = lastRow + 1 + i;
                    int src = first + ((r - first) % 2);
                    ws.Range[$"A{src}"].EntireRow.Copy(ws.Range[$"A{r}"].EntireRow);
                    ws.Range[$"A{r}"].EntireRow.RowHeight = ws.Range[$"A{src}"].EntireRow.RowHeight;
                }
            }
            else if (delta < 0)
            {
                ws.Range[$"A{first + n}:A{lastRow}"].EntireRow.Delete();
            }

            string? Shift(string? a1)
            {
                if (a1 == null || !TemplateDetector.TryParseRef(a1, out var r, out var c)) return a1;
                return r > lastRow ? TemplateDetector.Ref(r + delta, c) : a1;
            }
            string? CellOf(string field) => map.Fields.TryGetValue(field, out var m) ? Shift(m.Cell) : null;

            // ---- header
            Put(ws, CellOf(TemplateFields.PartyName), d.PartyName);
            if (d.NativeName != null) Put(ws, CellOf(TemplateFields.PartyNativeName), d.NativeName);
            Put(ws, CellOf(TemplateFields.InvoiceNo), d.InvoiceNo);
            if (CellOf(TemplateFields.IssueDate) is { } dateCell)
            {
                dynamic c = Top(ws.Range[dateCell]);
                if ((bool)c.HasArray) c.CurrentArray.ClearContents();
                c.Value = d.Date;
                string fmt = Convert.ToString(c.NumberFormat) ?? "";
                if (!fmt.Contains('y', StringComparison.OrdinalIgnoreCase)) c.NumberFormat = "dd/mm/yyyy";
            }
            if (d.CategoryText != null) Put(ws, CellOf(TemplateFields.Category), d.CategoryText);
            if (d.Bank != null && CellOf(TemplateFields.Bank) is { } bank)
            {
                Put(ws, bank, d.Bank);
                FitBankRow(ws, ws.Range[bank]);
            }
            if (d.Address != null && CellOf(TemplateFields.Address) is { } addr)
            {
                Put(ws, addr, d.Address);
                try { Top(ws.Range[addr]).WrapText = true; } catch { }
            }

            // ---- item values
            int Col(string key) => items.Columns.TryGetValue(key, out var letter) ? ColumnNumber(letter) : 0;
            int cCode = Col("code"), cDesc = Col("description"), cQty = Col("qty"), cUnit = Col("unit"), cPrice = Col("price"), cAmt = Col("amount");
            for (int i = 0; i < n; i++)
            {
                int r = first + i;
                var l = d.Lines[i];
                if (cCode > 0) SetText(ws.Cells[r, cCode], l.Code);
                if (cDesc > 0) SetText(ws.Cells[r, cDesc], l.Description);
                if (cQty > 0) ws.Cells[r, cQty].Value2 = (double)l.Quantity;
                if (cUnit > 0) SetText(ws.Cells[r, cUnit], "PCS");
                if (cPrice > 0) ws.Cells[r, cPrice].Value2 = (double)l.UnitPrice;
                if (cAmt > 0) ws.Cells[r, cAmt].Value2 = (double)l.Amount;
            }

            // ---- totals and amount in words (the workbook's own word functions live in an add-in)
            if (CellOf(TemplateFields.VatAmount) is { } vatCell)
            {
                dynamic vc = Top(ws.Range[vatCell]);
                PutValue(vc, (double)d.Vat);
                dynamic sub = ws.Cells[(int)vc.Row - 1, (int)vc.Column];
                if ((bool)sub.HasFormula || sub.Value2 is double) PutValue(Top(sub), (double)d.Net);
            }
            if (CellOf(TemplateFields.Total) is { } totalCell) PutValue(Top(ws.Range[totalCell]), (double)d.Total);
            if (CellOf(TemplateFields.AmountWords) is { } wordsCell) Put(ws, wordsCell, d.Words);

            // ---- supplier theme colour (Theme.RestoreClientTheme)
            if (d.ThemeRgb is { } theme && ThemeMemory.Supports(sheetName)) ApplyTheme(wb, ws, sheetName, theme);

            app.Calculate();

            // ---- the invoice becomes its own workbook. We already work on a private copy of the system file, so instead of
            // Worksheet.Copy (whose new workbook a hidden Excel does not reliably hand back) every other sheet is removed
            // from that copy and it is saved as .xlsx: the invoice sheet keeps all its shapes, lines, row heights and colours.
            FreezeFormulas(ws);                       // values first: the formulas point at sheets that are about to go
            try { for (int i = (int)ws.OLEObjects().Count; i >= 1; i--) ws.OLEObjects(i).Delete(); } catch { }
            try
            {
                for (int i = (int)ws.Shapes.Count; i >= 1; i--)
                {
                    dynamic shp = ws.Shapes.Item(i);
                    string onAction = "";
                    try { onAction = Convert.ToString(shp.OnAction) ?? ""; } catch { }
                    if (onAction.Length > 0) shp.Delete();
                }
            }
            catch { }
            try { wb.Unprotect(); } catch { }
            string keep = (string)ws.Name;
            try { ws.Visible = -1; ws.Activate(); } catch { }
            for (int i = (int)wb.Sheets.Count; i >= 1; i--)
            {
                dynamic sh = wb.Sheets[i];
                if (string.Equals((string)sh.Name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { sh.Visible = -1; } catch { }
                try { sh.Delete(); } catch { }
            }
            if ((int)wb.Sheets.Count != 1)
                throw new InvalidOperationException($"Could not reduce the workbook to the invoice sheet ({(int)wb.Sheets.Count} sheets left).");
            CleanLinks(wb);
            try { ws.Range["A1"].Select(); } catch { }
            wb.SaveAs(tmpOut, 51);     // xlOpenXMLWorkbook (no macros)
            wb.Close(false);
            wb = null;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmpOut, outputPath);
        }
        finally
        {
            try { outWb?.Close(false); } catch { }
            try { wb?.Close(false); } catch { }
            try { app.Quit(); } catch { }
            try { if (wb != null) Marshal.FinalReleaseComObject(wb); } catch { }
            try { Marshal.FinalReleaseComObject(app); } catch { }
            try { File.Delete(tmpBook); } catch { }
            try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
        }
    }

    // ------------------------------------------------------------------ cells
    private static dynamic? FindSheet(dynamic wb, string name)
    {
        foreach (dynamic s in wb.Worksheets)
            if (string.Equals((string)s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    private static dynamic Top(dynamic cell) => (bool)cell.MergeCells ? cell.MergeArea.Cells[1, 1] : cell.Cells[1, 1];

    private static void Put(dynamic ws, string? address, string text)
    {
        if (address == null) return;
        SetText(Top(ws.Range[address]), text);
    }

    private static void SetText(dynamic cell, string text)
    {
        dynamic c = Top(cell);
        if ((bool)c.HasArray) c.CurrentArray.ClearContents();
        c.Value2 = NeedsQuote(text) ? "'" + text : text;
    }

    private static void PutValue(dynamic c, double v)
    {
        if ((bool)c.HasArray) c.CurrentArray.ClearContents();
        c.Value2 = v;
    }

    private static bool NeedsQuote(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return false;
        if (t[0] is '=' or '+' or '-' or '@') return true;
        return double.TryParse(t, NumberStyles.Any, CultureInfo.CurrentCulture, out _) ||
               double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _) ||
               Regex.IsMatch(t, @"^\d{1,4}[-/.]\d{1,2}([-/.]\d{1,4})?$");
    }

    private static int ColumnNumber(string letters) =>
        letters.Trim().ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + (ch - 'A' + 1));

    /// <summary>BankFit.FitBankRow: the banking row grows with its text (merged cells measured with a text box).</summary>
    private static void FitBankRow(dynamic ws, dynamic bankCell)
    {
        const double top = 20, bottom = 24, safety = 10, minH = 48, maxH = 409.5;
        try
        {
            dynamic target = (bool)bankCell.MergeCells ? bankCell.MergeArea : bankCell.Cells[1, 1];
            if ((int)target.Rows.Count != 1) return;
            string text = (Convert.ToString(target.Cells[1, 1].Value2) ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            double fontSize = Convert.ToDouble(target.Cells[1, 1].Font.Size);
            double desired = minH;
            if (text.Trim().Length > 0)
            {
                dynamic probe = ws.Shapes.AddTextbox(1, (double)target.Left, (double)target.Top, (double)target.Width, 20.0);
                try
                {
                    probe.Line.Visible = 0;
                    probe.Fill.Visible = 0;
                    probe.TextFrame.Characters().Text = text;
                    probe.TextFrame.MarginLeft = 0; probe.TextFrame.MarginRight = 0;
                    probe.TextFrame.MarginTop = 0; probe.TextFrame.MarginBottom = 0;
                    probe.TextFrame2.WordWrap = -1;
                    probe.TextFrame2.AutoSize = 1;   // msoAutoSizeShapeToFitText
                    dynamic f = probe.TextFrame.Characters().Font;
                    f.Name = target.Cells[1, 1].Font.Name;
                    f.Size = fontSize;
                    f.Bold = target.Cells[1, 1].Font.Bold;
                    double measured = Math.Max((double)probe.Height, text.Split('\n').Length * (fontSize * 1.45 + 3));
                    desired = measured + top + bottom + safety;
                }
                finally { try { probe.Delete(); } catch { } }
            }
            target.EntireRow.RowHeight = Math.Max(minH, Math.Min(maxH, desired));
        }
        catch { /* cosmetic */ }
    }

    // ------------------------------------------------------------------ theme (Theme.bas)
    private static void ApplyTheme(dynamic wb, dynamic ws, string template, int target)
    {
        try
        {
            dynamic ctl = FindSheet(wb, "CONTROL");
            bool inv1 = string.Equals(template, "INV1", StringComparison.OrdinalIgnoreCase);
            int old;
            object? stored = ctl?.Range[inv1 ? "AB1" : "AA1"].Value2;
            string? ready = Convert.ToString(ctl?.Range[inv1 ? "AB2" : "AA2"].Value2);
            if (inv1 && ready != "THEME_INV1_READY") old = 255;                  // INV1 starts with pure red
            else if (stored is double dv) old = (int)dv;
            else old = Convert.ToInt32(ws.Range["B1"].Interior.Color);
            int? design = null;
            if (!inv1)
            {
                dynamic b11 = ws.Range["B11"].Interior;
                if ((int)b11.Pattern != -4142)
                {
                    int c = Convert.ToInt32(b11.Color);
                    if (c != old && c != target) design = c;
                }
            }
            if (old == target && design == null) return;

            foreach (dynamic cell in ws.UsedRange.Cells)
            {
                try
                {
                    if ((int)cell.Row == 1) continue;
                    dynamic interior = cell.Interior;
                    if ((int)interior.Pattern != -4142)
                    {
                        int c = Convert.ToInt32(interior.Color);
                        if (c == old || (design != null && c == design)) interior.Color = target;
                    }
                    object fc = cell.Font.Color;
                    if (fc is double f && ((int)f == old || (design != null && (int)f == design))) cell.Font.Color = target;
                }
                catch { }
            }
            foreach (dynamic shp in ws.Shapes)
            {
                string name = "";
                try { name = (string)shp.Name; } catch { }
                if (name is "V07_ApplyTheme_Button" or "ClientTheme_Assign_Button") continue;
                RecolorShape(shp, old, target);
                if (design != null) RecolorShape(shp, design.Value, target);
            }
            ws.Range["B1"].Interior.Color = target;
        }
        catch { /* theme is cosmetic: never stop an invoice */ }
    }

    private static void RecolorShape(dynamic shp, int oldColor, int newColor)
    {
        try
        {
            if ((int)shp.Type == 6)   // msoGroup
            {
                int count = (int)shp.GroupItems.Count;
                for (int i = 1; i <= count; i++) RecolorShape(shp.GroupItems.Item(i), oldColor, newColor);
                return;
            }
        }
        catch { }
        try
        {
            if ((int)shp.Fill.Visible == -1 && (int)shp.Fill.Type == 1 && Convert.ToInt32(shp.Fill.ForeColor.RGB) == oldColor)
                shp.Fill.ForeColor.RGB = newColor;
        }
        catch { }
        try
        {
            if ((int)shp.Line.Visible == -1 && Convert.ToInt32(shp.Line.ForeColor.RGB) == oldColor)
                shp.Line.ForeColor.RGB = newColor;
        }
        catch { }
        try
        {
            if ((bool)shp.TextFrame.HasText)
            {
                object c = shp.TextFrame.Characters().Font.Color;
                if (c is double d && (int)d == oldColor) shp.TextFrame.Characters().Font.Color = newColor;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ archive clean-up (ArchiveEngine)
    private static void FreezeFormulas(dynamic ws)
    {
        dynamic? cells = null;
        try { cells = ws.UsedRange.SpecialCells(-4123); } catch { return; }   // xlCellTypeFormulas; none → error
        foreach (dynamic fc in cells!.Cells)
        {
            try
            {
                if ((bool)fc.HasArray)
                {
                    dynamic arr = fc.CurrentArray;
                    object v = arr.Value2;
                    arr.Value2 = v;
                }
                else if ((bool)fc.MergeCells)
                {
                    if ((string)fc.Address == (string)fc.MergeArea.Cells[1, 1].Address) fc.Value2 = fc.Value2;
                }
                else fc.Value2 = fc.Value2;
            }
            catch { }
        }
    }

    private static void CleanLinks(dynamic book)
    {
        try
        {
            object links = book.LinkSources(1);   // xlExcelLinks
            if (links is Array a)
                foreach (var l in a) try { book.BreakLink(Convert.ToString(l), 1); } catch { }
        }
        catch { }
        try
        {
            for (int j = (int)book.Names.Count; j >= 1; j--)
            {
                dynamic nm = book.Names.Item(j);
                string refers = "";
                try { refers = Convert.ToString(nm.RefersTo) ?? ""; } catch { }
                if (refers.Contains('[') || refers.Contains("#REF")) try { nm.Delete(); } catch { }
            }
        }
        catch { }
    }
}
