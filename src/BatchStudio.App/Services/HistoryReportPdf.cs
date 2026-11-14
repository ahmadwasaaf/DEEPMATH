using System.Runtime.InteropServices;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;

namespace BatchStudio.App.Services;

/// <summary>
/// The INVOICE HISTORY period report as a PDF, made by Excel from a private copy of the REAL workbook: the sheet keeps its
/// buttons (All / SUPPLIERS / CLIENTS) and its search box exactly like a PDF exported from Excel itself. Only the rows of
/// the period are shown; the count, period and total cells and the print area follow the plan. The workbook is never changed.
/// </summary>
public static class HistoryReportPdf
{
    public static void Export(string workbookPath, InvoiceHistoryBook.ReportPlan plan, string pdfPath)
    {
        var type = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException(Loc.T("Excel.Missing"));
        var tmpBook = Path.Combine(Path.GetTempPath(), $"BS_hist_{Guid.NewGuid():N}{Path.GetExtension(workbookPath)}");
        File.WriteAllBytes(tmpBook, PackageRepair.ReadAllShared(workbookPath));
        dynamic app = Activator.CreateInstance(type)!;
        int? pid = ExcelProcess.Id(app);
        dynamic? wb = null;
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;
            app.AskToUpdateLinks = false;
            app.AutomationSecurity = 3;     // macros off: nothing of the workbook runs
            app.EnableEvents = false;
            wb = app.Workbooks.Open(tmpBook, 0, false);
            dynamic ws = ExcelInvoiceEngine.FindSheet(wb, plan.Sheet) ?? throw new InvalidOperationException(plan.Sheet);
            foreach (int r in plan.Show) ws.Rows[r].Hidden = false;
            foreach (int r in plan.Hide) ws.Rows[r].Hidden = true;
            if (plan.SetCount) ws.Range["C3"].Value2 = plan.Count;
            if (plan.PeriodCell != null) ws.Range[plan.PeriodCell].Value2 = plan.PeriodLabel;
            if (plan.SetTotal) ws.Range["D7"].Value2 = plan.Total;
            ws.PageSetup.PrintArea = plan.PrintArea;
            app.Calculate();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pdfPath))!);
            ws.ExportAsFixedFormat(0, pdfPath, 0, true, false);
            wb.Close(false);
            wb = null;
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { if (wb != null) Marshal.FinalReleaseComObject(wb); } catch { }
            ExcelProcess.Shutdown(app, pid);
            try { File.Delete(tmpBook); } catch { }
        }
    }
}
