using System.Runtime.InteropServices;

namespace BatchStudio.App.Services;

/// <summary>Exports an invoice workbook to PDF through the installed Microsoft Excel (same output as Excel's own PDF export).</summary>
public static class ExcelPdf
{
    public static string Export(string xlsxPath)
    {
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("Microsoft Excel is needed to export PDF. Open the invoice and use File > Export instead.");
        var pdf = Path.ChangeExtension(xlsxPath, ".pdf");
        dynamic? app = null;
        dynamic? wb = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            app.DisplayAlerts = false;
            app.Visible = false;
            wb = app.Workbooks.Open(xlsxPath, 0, true);
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
