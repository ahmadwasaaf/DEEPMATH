using System.Runtime.InteropServices;

namespace BatchStudio.App.Services;

/// <summary>Exports an invoice workbook to PDF through the installed Microsoft Excel (same output as Excel's own PDF export).</summary>
public static class ExcelPdf
{
    /// <param name="invoiceSheets">For an invoice file: only the invoice sheet is exported (a file holding more sheets
    /// exports the sheet named like a template, otherwise the active sheet).</param>
    public static string Export(string xlsxPath, string? pdfPath = null, IReadOnlyCollection<string>? invoiceSheets = null)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("Microsoft Excel is needed to export PDF. Open the invoice and use File > Export instead.");
        var pdf = pdfPath ?? Path.ChangeExtension(xlsxPath, ".pdf");
        dynamic? app = null;
        dynamic? wb = null;
        int? pid = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            pid = ExcelProcess.Id(app);
            app.DisplayAlerts = false;
            app.Visible = false;
            try { app.AutomationSecurity = 3; } catch { }   // no macros / controls run
            try { wb = app.Workbooks.Open(xlsxPath, 0, true); }
            catch (COMException)
            {
                // a copy Excel finds odd (e.g. leftover control parts): let Excel repair it in memory (CorruptLoad = xlRepairFile)
                var m = Type.Missing;
                wb = app.Workbooks.Open(xlsxPath, 0, true, m, m, m, m, m, m, m, m, m, m, m, 1);
            }
            try { app.CalculateFull(); } catch { }   // SUBTOTAL / totals reflect hidden rows
            if (invoiceSheets != null && (int)wb.Sheets.Count > 1)
            {
                dynamic? target = null;
                foreach (dynamic sh in wb.Worksheets)
                {
                    if ((int)sh.Visible != -1) continue;
                    if (invoiceSheets.Any(n => string.Equals(n?.Trim(), ((string)sh.Name).Trim(), StringComparison.OrdinalIgnoreCase))) { target = sh; break; }
                }
                target ??= wb.ActiveSheet;
                target.ExportAsFixedFormat(0, pdf);
            }
            else wb.ExportAsFixedFormat(0, pdf); // 0 = xlTypePDF
            wb.Close(false);
            return pdf;
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { if (wb != null) Marshal.FinalReleaseComObject(wb); } catch { }
            ExcelProcess.Shutdown(app, pid);
        }
    }
}
