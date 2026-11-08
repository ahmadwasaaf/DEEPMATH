using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Reports;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

public class ReportTests
{
    private static string Fx(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);
    private static List<HistoryRecord> History() => InvoiceHistoryBook.Read(Fx("History_V28.xlsx"));

    [Fact]
    public void BaseName_strips_duplicate_suffix()
    {
        Assert.Equal("ACME", DashboardEngine.BaseName("ACME (2)"));
        Assert.Equal("ACME (1)", DashboardEngine.BaseName("ACME (1)"));
        Assert.True(DashboardEngine.SameCompany("acme (3)", "ACME"));
    }

    [Fact]
    public void Dashboard_totals_and_ranking()
    {
        var h = History();
        var all = DashboardEngine.Compute(h, new Dictionary<string, string>(), new DashboardFilter());
        Assert.Equal(h.Sum(r => r.Amount ?? 0), all.Total);
        Assert.Equal(all.Total, all.SuppliersTotal + all.ClientsTotal);
        Assert.Equal(h.Count, all.Invoices);
        Assert.True(all.Ranking.Zip(all.Ranking.Skip(1)).All(p => p.First.Amount >= p.Second.Amount));
        Assert.Equal(all.Ranking.Count, all.SupplierCount + all.ClientCount);

        var less = DashboardEngine.Compute(h, new Dictionary<string, string>(), new DashboardFilter { Ranking = "LESS", Mode = "SUPPLIERS" });
        Assert.All(less.Ranking, r => Assert.Equal("SUPPLIERS", r.Type));
        Assert.True(less.Ranking.Zip(less.Ranking.Skip(1)).All(p => p.First.Amount <= p.Second.Amount));
        Assert.Equal(0m, less.ClientsTotal);

        var oct = DashboardEngine.Compute(h, new Dictionary<string, string>(),
            new DashboardFilter { Period = "Custom Range", From = new DateTime(2026, 10, 1), To = new DateTime(2026, 10, 31) });
        Assert.Equal(h.Count(r => r.Date is { Month: 10, Year: 2026 }), oct.Invoices);
    }

    [Fact]
    public void Dashboard_sheet_is_written_in_place()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Dashboard.xlsx");
            File.Copy(Fx("Dashboard_V28.xlsx"), path);
            var f = new DashboardFilter { Mode = "ALL", Ranking = "TOP" };
            var d = DashboardEngine.Compute(History(), new Dictionary<string, string> { ["SUPPLIERS|Party 4100"] = "Pumps | مضخات" }, f);
            Assert.True(ReportBooks.WriteDashboard(path, d, f));
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Dashboard");
            Assert.Equal((double)d.Total, ws.Cell("B8").GetDouble(), 2);
            Assert.Equal(d.Invoices, (int)ws.Cell("G8").GetDouble());
            Assert.Equal("Rank-1", ws.Cell("B12").GetString());
            Assert.Equal(d.Ranking[0].Name, ws.Cell("C12").GetString());
            Assert.Equal("All Time", ws.Cell("G3").GetString());
            Assert.Equal("View Full Report", ws.Cell("G12").GetString());
            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); File.Copy(path, Path.Combine(outDir, "Dashboard_written.xlsx"), true); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Company_report_is_built_and_filled()
    {
        var h = History();
        var company = h.GroupBy(r => r.Name).OrderByDescending(g => g.Count()).First().Key;
        var type = CompanyReports.ResolveType(company, null, h)!;
        var sup = new TableData { KeyColumn = 0 };
        sup.Headers.AddRange(["Supplier ID", "Supplier Name (English)", "Chinese Name", "Company Address", "Banking Details (full paragraph)", "Category (English | Arabic)", "DEFAULT TEMPLATE", "Company’s Activities"]);
        sup.Kinds.AddRange(Enumerable.Repeat(ColumnKind.Text, 8));
        sup.Rows.Add(["SUP-001", company, "名字", "", "", "Pumps | مضخات", "INVOICE", "Trading company"]);
        sup.Rows.Add(["SUP-002", company + " (2)", "", "", "", "", "", ""]);
        var rep = CompanyReports.Build(company, type, h, type == "SUPPLIERS" ? sup : null, new DashboardFilter(), "All Time");
        Assert.Equal(h.Count(r => r.Name == company && r.PartyType == type), rep.Invoices.Count);
        Assert.True(rep.Invoices.Zip(rep.Invoices.Skip(1)).All(p => p.First.Date <= p.Second.Date));
        if (type == "SUPPLIERS")
        {
            Assert.Equal(2, rep.BankAccounts);
            Assert.Equal("Trading company", rep.Description);
        }

        var dir = Path.Combine(Path.GetTempPath(), "bs-rep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outFile = Path.Combine(dir, "report.xlsx");
            ReportBooks.FillReport(Fx("SupplierReport_V28.xlsx"), rep, outFile);
            using var wb = new XLWorkbook(outFile);
            var ws = wb.Worksheet(1);
            Assert.Equal(rep.Name, ws.Cell("B6").GetString());
            Assert.Equal($"{rep.Invoices.Count} Invoices", ws.Cell("D4").GetString());
            Assert.Equal("INV-1", ws.Cell("B13").GetString());
            Assert.Equal(rep.Invoices[^1].InvoiceNo, ws.Cell(12 + rep.Invoices.Count, 5).GetString());
            Assert.True(ws.Cell(13 + rep.Invoices.Count, 2).IsEmpty());
            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); File.Copy(outFile, Path.Combine(outDir, "SupplierReport_filled.xlsx"), true); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
