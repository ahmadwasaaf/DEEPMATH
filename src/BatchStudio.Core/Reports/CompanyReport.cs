using System.Globalization;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using ClosedXML.Excel;

namespace BatchStudio.Core.Reports;

/// <summary>Account statement of one supplier / client (V28 "Supplier Rebort" sheet).</summary>
public sealed class CompanyReport
{
    public required string Name { get; init; }
    public required string Type { get; init; }          // SUPPLIERS / CLIENTS
    public string NativeName { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Rows of this company in Suppliers ("1 Bank Accounts"); -1 for clients ("?").</summary>
    public int BankAccounts { get; init; }
    public string PeriodLabel { get; init; } = HistoryPeriods.AllTime;
    public required List<HistoryRecord> Invoices { get; init; }
    public decimal Total => Invoices.Sum(i => i.Amount ?? 0);
}

public static class CompanyReports
{
    /// <summary>SUPPLIERS / CLIENTS for a company: the preferred type, else the first history row of that company.</summary>
    public static string? ResolveType(string company, string? preferred, IEnumerable<HistoryRecord> history)
    {
        var p = (preferred ?? "").Trim().ToUpperInvariant();
        if (p is "SUPPLIERS" or "CLIENTS") return p;
        foreach (var r in history)
            if (DashboardEngine.SameCompany(r.Name, company))
            {
                var t = r.PartyType.Trim().ToUpperInvariant();
                if (t is "SUPPLIERS" or "CLIENTS") return t;
            }
        return null;
    }

    private static int Col(TableData t, params string[] names)
    {
        foreach (var n in names)
        {
            int i = t.Headers.FindIndex(h => h.Replace('’', '\'').Contains(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>Column holding "Company's Activities" (Suppliers H / Clients D).</summary>
    public static int DescriptionColumn(TableData t) => Col(t, "Company's Activities", "Activities", "Description");

    /// <summary>Port of SupplierReportEngine.LoadCompany.</summary>
    public static CompanyReport Build(string company, string type, IEnumerable<HistoryRecord> history,
        TableData? masterData, DashboardFilter period, string periodLabel)
    {
        string native = "", category = "", description = "";
        int rows = 0;
        bool found = false;
        if (masterData != null)
        {
            int name = Col(masterData, type == "SUPPLIERS" ? "Supplier Name" : "Client Name", "Name");
            if (name < 0) name = 1;
            int cat = Col(masterData, type == "SUPPLIERS" ? "Category (English" : "Client Category", "Category");
            int desc = DescriptionColumn(masterData);
            int nat = type == "SUPPLIERS" ? Col(masterData, "Chinese Name", "Native") : -1;
            foreach (var r in masterData.Rows)
            {
                var n = name < r.Length ? r[name] : "";
                if (!DashboardEngine.SameCompany(n, company)) continue;
                rows++;
                if (!found || string.Equals(n.Trim(), DashboardEngine.BaseName(company), StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    category = cat >= 0 && cat < r.Length ? r[cat] : "";
                    description = desc >= 0 && desc < r.Length ? r[desc] : "";
                    native = nat >= 0 && nat < r.Length ? r[nat] : "";
                }
            }
        }
        var invoices = history
            .Where(r => string.Equals(r.PartyType.Trim(), type, StringComparison.OrdinalIgnoreCase)
                        && DashboardEngine.SameCompany(r.Name, company) && period.InPeriod(r.Date))
            .OrderBy(r => r.Date ?? DateTime.MinValue)
            .ToList();
        return new CompanyReport
        {
            Name = DashboardEngine.BaseName(company), Type = type, NativeName = native, Category = category,
            Description = description, BankAccounts = type == "SUPPLIERS" ? rows : -1, PeriodLabel = periodLabel,
            Invoices = invoices,
        };
    }

    /// <summary>"Custom Range (01/11/2026 - 08/11/2026)" for custom periods, else the period name.</summary>
    public static string PeriodLabel(DashboardFilter f)
    {
        if (f.Period != HistoryPeriods.CustomRange) return f.Period;
        string D(DateTime? d) => d?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "...";
        return $"{f.Period} ({D(f.From)} - {D(f.To)})";
    }
}

/// <summary>Writes into the extracted V28 sheets: Dashboard.xlsx (live numbers) and the Supplier Report template (PDF).</summary>
public static class ReportBooks
{
    public static string DashboardPath(string root) => Path.Combine(root, DatabaseLayout.DashboardFolder, "Dashboard.xlsx");
    public static string ReportTemplatePath(string root) => Path.Combine(root, DatabaseLayout.DashboardFolder, "Supplier Report.xlsx");

    /// <summary>Same cells as DashboardV8.RefreshDashboard, written into the workbook's own Dashboard sheet (through Excel).</summary>
    public static void WriteDashboard(BatchStudio.Core.Workbook.ISheetEditor ws, DashboardResult d, DashboardFilter f)
    {
        ws.Set(3, 7, f.Period);
        ws.Set(4, 7, f.Mode);
        ws.Set(5, 7, f.Ranking);
        if (f.Period == HistoryPeriods.CustomRange)
        {
            if (f.From is { } a) { ws.Set(2, 16, a); ws.SetNumberFormat(2, 16, "dd/mm/yyyy"); }
            if (f.To is { } b) { ws.Set(4, 16, b); ws.SetNumberFormat(4, 16, "dd/mm/yyyy"); }
        }
        void N(int r, int c, double v, string fmt) { ws.Set(r, c, v); ws.SetNumberFormat(r, c, fmt); }
        N(8, 2, (double)d.Total, "#,##0.00 \"USD\"");
        N(6, 4, d.SupplierCount, "#,##0 \"Supplier\"");
        N(8, 4, (double)d.SuppliersTotal, "#,##0.00 \"USD\"");
        N(6, 6, d.ClientCount, "#,##0 \"Client\"");
        N(8, 6, (double)d.ClientsTotal, "#,##0.00 \"USD\"");
        N(8, 7, d.Invoices, "#,##0 \"Invoice\"");
        var block = new object?[20, 7];
        int i = 0;
        foreach (var row in d.Top20.Take(20))
        {
            block[i, 0] = $"Rank-{row.Rank}";
            block[i, 1] = row.Name;
            block[i, 2] = (double)row.Amount;
            block[i, 3] = (double)row.Count;
            block[i, 4] = row.Category;
            block[i, 5] = "View Full Report";
            block[i, 6] = "Export Full Report";
            i++;
        }
        ws.Write(12, 2, block);
    }

    /// <summary>Same cells as DashboardV8.RefreshDashboard. Returns false when the file is missing.</summary>
    public static bool WriteDashboard(string path, DashboardResult d, DashboardFilter f)
    {
        if (!File.Exists(path)) return false;
        ExcelTableStore.EnsureWritable(path);
        using var wb = PackageRepair.OpenWorkbook(path);
        var ws = wb.Worksheets.TryGetWorksheet("Dashboard", out var named) ? named : wb.Worksheet(1);
        ws.Cell("G3").Value = f.Period;
        ws.Cell("G4").Value = f.Mode;
        ws.Cell("G5").Value = f.Ranking;
        if (f.Period == HistoryPeriods.CustomRange)
        {
            if (f.From is { } a) { ws.Cell("P2").Value = a; ws.Cell("P2").Style.NumberFormat.Format = "dd/mm/yyyy"; }
            if (f.To is { } b) { ws.Cell("P4").Value = b; ws.Cell("P4").Style.NumberFormat.Format = "dd/mm/yyyy"; }
        }
        Num(ws.Cell("B8"), d.Total, "#,##0.00 \"USD\"");
        Num(ws.Cell("D6"), d.SupplierCount, "#,##0 \"Supplier\"");
        Num(ws.Cell("D8"), d.SuppliersTotal, "#,##0.00 \"USD\"");
        Num(ws.Cell("F6"), d.ClientCount, "#,##0 \"Client\"");
        Num(ws.Cell("F8"), d.ClientsTotal, "#,##0.00 \"USD\"");
        Num(ws.Cell("G8"), d.Invoices, "#,##0 \"Invoice\"");
        ws.Range("B12:H31").Clear(XLClearOptions.Contents);
        int i = 0;
        foreach (var row in d.Top20)
        {
            int r = 12 + i++;
            ws.Cell(r, 2).Value = $"Rank-{row.Rank}";
            ws.Cell(r, 3).Value = row.Name;
            Num(ws.Cell(r, 4), row.Amount, "#,##0.00");
            Num(ws.Cell(r, 5), row.Count, "#,##0");
            ws.Cell(r, 6).Value = row.Category;
            ws.Cell(r, 7).Value = "View Full Report";
            ws.Cell(r, 8).Value = "Export Full Report";
        }
        for (int r = 12; r <= 31; r++)
        {
            ws.Row(r).Height = 54;
            ws.Range(r, 2, r, 8).Style.Fill.BackgroundColor = r % 2 == 1 ? XLColor.FromArgb(245, 248, 251) : XLColor.White;
        }
        Save(wb, path);
        return true;
    }

    /// <summary>Fills a copy of the Supplier Report template (same cells as SupplierReportEngine.LoadCompany).</summary>
    public static void FillReport(string templatePath, CompanyReport rep, string outputPath)
    {
        using var wb = PackageRepair.OpenWorkbook(templatePath);
        var ws = wb.Worksheet(1);
        const int first = 13;
        ws.Cell("B6").Value = rep.Name;
        ws.Cell("B7").Value = rep.NativeName;
        Num(ws.Cell("B4"), rep.Total, "#,##0.00");
        ws.Cell("D4").Value = $"{rep.Invoices.Count} Invoices";
        ws.Cell("E4").Value = rep.BankAccounts >= 0 ? $"{rep.BankAccounts} Bank Accounts" : "?";
        ws.Cell("F4").Value = rep.PeriodLabel;
        ws.Cell("F6").Value = rep.Category;
        ws.Cell("B9").Value = rep.Description;
        ws.Cell("B9").Style.Alignment.WrapText = true;
        int n = rep.Invoices.Count;
        int clearTo = Math.Max(first + n + 5, 150);
        ws.Range(first, 2, clearTo, 7).Clear(XLClearOptions.Contents);
        ws.Range(first, 11, clearTo, 11).Clear(XLClearOptions.Contents);
        for (int i = 0; i < n; i++)
        {
            int r = first + i;
            var inv = rep.Invoices[i];
            ws.Range(r, 2, r, 7).Style.Fill.BackgroundColor = i % 2 == 0 ? XLColor.White : XLColor.FromArgb(245, 248, 251);
            ws.Row(r).Height = 50;
            ws.Cell(r, 2).Value = $"INV-{i + 1}";
            if (inv.Amount is { } a) Num(ws.Cell(r, 3), a, "#,##0.00");
            if (inv.Date is { } dt) { ws.Cell(r, 4).Value = dt; ws.Cell(r, 4).Style.NumberFormat.Format = "dd/mm/yyyy"; }
            ws.Cell(r, 5).Value = inv.InvoiceNo;
            ws.Cell(r, 6).Value = "Open Invoice";
            ws.Cell(r, 7).Value = "Export PDF";
            ws.Cell(r, 11).Value = inv.RecordId;
        }
        ws.Column(11).Hide();
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add(2, 2, Math.Max(13, first + n - 1), 7);
        ws.PageSetup.FitToPages(1, 0);
        wb.SaveAs(outputPath);
        PackageRepair.StripControls(outputPath);
    }

    private static void Num(IXLCell c, decimal v, string fmt) { c.Value = (double)v; c.Style.NumberFormat.Format = fmt; }
    private static void Num(IXLCell c, int v, string fmt) { c.Value = v; c.Style.NumberFormat.Format = fmt; }

    private static void Save(XLWorkbook wb, string path)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".~{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp.xlsx");
        wb.SaveAs(tmp);
        try { File.Replace(tmp, path, null); }
        catch (IOException ex)
        {
            try { File.Delete(tmp); } catch { }
            throw new FileLockedException(path, ex);
        }
    }
}
