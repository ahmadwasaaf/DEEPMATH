using System.Runtime.InteropServices;
using BatchStudio.Core.Data;

namespace BatchStudio.App.Services;

/// <summary>
/// A supplier's bank account as a PDF, from the "Bank Account" sheet of the BATCH STUDIO workbook (the user's design):
/// B2 the supplier's name, B3 its Chinese name, B7 the banking paragraph (its row fitted to the text), the supplier's
/// theme colour on the title, the bar and the lines. Works on a private copy: the workbook itself is never changed.
/// </summary>
public static class BankAccountPdf
{
    public const string Sheet = "Bank Account";

    /// <summary>"COLOR LEAF TRADING CO., LIMITED Bank Account.pdf" (account 1), "... Bank Account 3.pdf" (Banking Details 3).</summary>
    public static string FileName(string supplier, int account)
    {
        var name = supplier.Trim() + " Bank Account" + (account > 1 ? " " + account : "");
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, ' ');
        return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)) + ".pdf";
    }

    /// <summary>A free path in the folder: "x.pdf", else "x (2).pdf", "x (3).pdf" ... (never overwrites).</summary>
    public static string FreePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>One PDF to write: a supplier's account.</summary>
    public sealed record Job(string Supplier, string? NativeName, string Bank, int? ThemeRgb, string PdfPath);

    /// <param name="themeRgb">The supplier's theme colour (Excel colour number), or null for the sheet's own colour.</param>
    public static void Export(string workbookPath, string supplier, string? nativeName, string bank, int? themeRgb, string pdfPath) =>
        ExportMany(workbookPath, [new Job(supplier, nativeName, bank, themeRgb, pdfPath)], null);

    /// <summary>Many accounts with one hidden Excel (Export all). Returns the jobs that failed, with why.</summary>
    public static List<(Job Job, string Error)> ExportMany(string workbookPath, IReadOnlyList<Job> jobs, Action<int, int>? progress)
    {
        var failed = new List<(Job, string)>();
        var type = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException(Loc.T("Excel.Missing"));
        var tmpBook = Path.Combine(Path.GetTempPath(), $"BS_bank_{Guid.NewGuid():N}{Path.GetExtension(workbookPath)}");
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
            app.AutomationSecurity = 3;     // macros off
            app.EnableEvents = false;
            wb = app.Workbooks.Open(tmpBook, 0, false);
            dynamic ws = ExcelInvoiceEngine.FindSheet(wb, Sheet)
                         ?? throw new InvalidOperationException(Loc.F("BankPdf.NoSheet", Sheet));
            int design = Convert.ToInt32(ws.Range["B2"].Font.Color);
            int current = design;           // the colour the sheet has now (each supplier recolours it)
            for (int n = 0; n < jobs.Count; n++)
            {
                var job = jobs[n];
                progress?.Invoke(n, jobs.Count);
                try
                {
                    ExcelInvoiceEngine.SetText(ws.Range["B2"], job.Supplier.Trim());
                    ExcelInvoiceEngine.SetText(ws.Range["B3"], (job.NativeName ?? "").Trim());
                    ExcelInvoiceEngine.SetText(ws.Range["B7"], job.Bank.Replace("\r\n", "\n").Trim());
                    try { ws.Range["B7"].WrapText = true; } catch { }
                    ExcelInvoiceEngine.FitBankRow(ws, ws.Range["B7"]);
                    int target = job.ThemeRgb ?? design;
                    if (target != current)
                    {
                        Recolor(ws, current, target);
                        current = target;
                    }
                    app.Calculate();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.PdfPath))!);
                    ws.ExportAsFixedFormat(0, job.PdfPath);    // the sheet's own print setup (B1:F9, landscape, fit to page)
                }
                catch (Exception ex) when (jobs.Count > 1) { failed.Add((job, ex.Message)); }
            }
            progress?.Invoke(jobs.Count, jobs.Count);
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
        return failed;
    }

    /// <summary>The design colour → the supplier's theme: cell fills, fonts, borders and shapes (the bar, the lines).</summary>
    private static void Recolor(dynamic ws, int from, int to)
    {
        foreach (dynamic cell in ws.UsedRange.Cells)
        {
            try
            {
                dynamic interior = cell.Interior;
                if ((int)interior.Pattern != -4142 && Convert.ToInt32(interior.Color) == from) interior.Color = to;
                if (cell.Font.Color is double f && (int)f == from) cell.Font.Color = to;
                foreach (int edge in new[] { 7, 8, 9, 10 })   // left, top, bottom, right borders
                {
                    dynamic b = cell.Borders[edge];
                    if ((int)b.LineStyle != -4142 && b.Color is double bc && (int)bc == from) b.Color = to;
                }
            }
            catch { }
        }
        foreach (dynamic shp in ws.Shapes) ExcelInvoiceEngine.RecolorShape(shp, from, to);
    }
}
