using System.Runtime.InteropServices;

namespace BatchStudio.App.Services;

/// <summary>Exports an invoice workbook to PDF through the installed Microsoft Excel (same output as Excel's own PDF export).</summary>
public static class ExcelPdf
{
    public static string Export(string xlsxPath, string? pdfPath = null)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("Microsoft Excel is needed to export PDF. Open the invoice and use File > Export instead.");
        var pdf = pdfPath ?? Path.ChangeExtension(xlsxPath, ".pdf");
        dynamic? app = null;
        dynamic? wb = null;
        try
        {
            app = Activator.CreateInstance(type)!;
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
            wb.ExportAsFixedFormat(0, pdf); // 0 = xlTypePDF
            wb.Close(false);
            return pdf;
        }
        finally
        {
            try { app?.Quit(); } catch { }
            if (wb != null) Marshal.FinalReleaseComObject(wb);
            if (app != null) Marshal.FinalReleaseComObject(app);
        }
    }
}
