using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

public class HistoryLayoutTests
{
    /// <summary>Same layout as the V28 INVOICE HISTORY sheet (headers B8:G8, data from row 9, K..N unlabeled).</summary>
    private static string MakeV28History(string root, int rows)
    {
        var path = DataSets.InvoiceHistory.FullPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("INVOICE HISTORY");
        ws.Cell("C2").Value = "INVOICE HISTORY SEARCH | REVIEW | OPEN | EXPORT";
        ws.Cell("C3").Value = rows;
        ws.Cell("C3").Style.NumberFormat.Format = "#,##0\" Invoice\"";
        ws.Cell("E5").Value = "All Time";
        ws.Cell("D7").FormulaA1 = "SUBTOTAL(109,D9:D50000)";
        string[] h = ["#", "Name", "Amount", "Date", "INVOICE NO", "STATUS", "Actions "];
        for (int i = 0; i < h.Length; i++) ws.Cell(8, 2 + i).Value = h[i];
        for (int i = 0; i < rows; i++)
        {
            int r = 9 + i;
            ws.Cell(r, 2).Value = $"INV-{i + 1}";
            ws.Cell(r, 3).Value = $"Client {i + 1}";
            ws.Cell(r, 4).Value = 1000 * (i + 1);
            ws.Cell(r, 5).Value = new DateTime(2026, 9, 12).AddDays(i);
            ws.Cell(r, 6).Value = $"Mad-{6942817 + i}";
            ws.Cell(r, 7).Value = "File available";
            ws.Cell(r, 8).Value = "Open Invoice"; ws.Cell(r, 9).Value = "Remove"; ws.Cell(r, 10).Value = "Export PDF";
            ws.Cell(r, 11).Value = "CLIENTS"; ws.Cell(r, 12).Value = $"20261026-{i}";
            ws.Cell(r, 13).Value = $@"D:\Old\Accounting\BATCH STUDIO\CLIENTS\Client {i + 1}\Mad-{6942817 + i}.xlsx";
            ws.Cell(r, 14).Value = "Madar"; ws.Cell(r, 15).Value = "View Full Report"; ws.Cell(r, 16).Value = "Export Full Report";
            ws.Range(r, 2, r, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#F4F6F8");
            ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
        }
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reads_and_appends_in_V28_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = MakeV28History(root, 3);
            using (var wb = new XLWorkbook(path))
            {
                var l = InvoiceHistoryBook.Detect(InvoiceHistoryBook.Sheet(wb));
                Assert.True(l.IsV28);
                Assert.Equal(8, l.HeaderRow);
                Assert.Equal(6, l.ColInvoice);
                Assert.Equal(13, l.ColPath);
            }
            var recs = InvoiceHistoryBook.Read(path);
            Assert.Equal(3, recs.Count);
            Assert.Equal("CLIENTS", recs[0].PartyType);
            Assert.Equal(new DateTime(2026, 9, 12), recs[0].Date);

            InvoiceHistoryBook.Append(root, path, new HistoryRecord
            {
                Name = "أركان التجريبية", Amount = 115000m, Date = new DateTime(2026, 11, 7), InvoiceNo = "Mad-1234567",
                PartyType = "CLIENTS", RecordId = "X", FilePath = @"C:\DB\BATCH STUDIO\CLIENTS\a\Mad-1234567.xlsx", Template = "Madar",
            });
            using var after = new XLWorkbook(path);
            var ws = InvoiceHistoryBook.Sheet(after);
            Assert.Equal("INV-4", ws.Cell("B12").GetString());
            Assert.Equal("أركان التجريبية", ws.Cell("C12").GetString());
            Assert.Equal(115000d, ws.Cell("D12").GetDouble());
            Assert.Equal("Mad-1234567", ws.Cell("F12").GetString());
            Assert.Equal("Open Invoice", ws.Cell("H12").GetString());
            Assert.Equal("CLIENTS", ws.Cell("K12").GetString());
            Assert.Equal("Madar", ws.Cell("N12").GetString());
            Assert.Equal("Export Full Report", ws.Cell("P12").GetString());
            Assert.Equal(ws.Cell("B11").Style.Fill.BackgroundColor, ws.Cell("B12").Style.Fill.BackgroundColor);
            Assert.Equal("#,##0.00", ws.Cell("D12").Style.NumberFormat.Format);
            Assert.Equal(4d, ws.Cell("C3").GetDouble());
            Assert.True(ws.Cell("D7").HasFormula);
            Assert.Equal("All Time", ws.Cell("E5").GetString());

            Assert.Throws<InvalidOperationException>(() => InvoiceHistoryBook.Append(root, path, new HistoryRecord { InvoiceNo = "Mad-1234567", Name = "x" }));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Old_absolute_paths_resolve_inside_the_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-res-" + Guid.NewGuid().ToString("N"));
        try
        {
            var local = Path.Combine(root, "BATCH STUDIO", "CLIENTS", "Client 1", "Mad-6942817.xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, "x");
            Assert.Equal(local, InvoiceHistoryBook.ResolvePath(root, @"D:\Old\Accounting\BATCH STUDIO\CLIENTS\Client 1\Mad-6942817.xlsx"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Lists_sheet_is_refreshed()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-lists-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "T");
            var path = DataSets.Suppliers.FullPath(root);
            using (var wb = new XLWorkbook(path))
            {
                var l = wb.Worksheets.Add(ListsSync.SheetName);
                l.Cell("A1").Value = "V06CategoryList"; l.Cell("A2").Value = "Old";
                l.Cell("B1").Value = "V11_TemplateChoices"; l.Cell("B2").Value = "INVOICE";
                wb.DefinedNames.Add("V06CategoryList", l.Range("A2:A2"));
                wb.SaveAs(path);
            }
            var changed = ListsSync.Update(root, ["Auto Parts | قطع غيار السيارات", "Lighting | الإضاءة"], ["INVOICE", "INV1", "Madar"]);
            Assert.Contains(path, changed);
            using (var wb = new XLWorkbook(path))
            {
                var l = wb.Worksheet(ListsSync.SheetName);
                Assert.Equal("Lighting | الإضاءة", l.Cell("A3").GetString());
                Assert.Equal("Madar", l.Cell("B4").GetString());
            }
            Assert.Empty(ListsSync.Update(root, ["Auto Parts | قطع غيار السيارات", "Lighting | الإضاءة"], ["INVOICE", "INV1", "Madar"]));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class RealHistoryTests
{
    [Fact]
    public void Real_V28_history_sheet_reads_and_appends()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-rh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = DataSets.InvoiceHistory.FullPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "History_V28.xlsx"), path);
            var before = InvoiceHistoryBook.Read(path);
            Assert.Equal(84, before.Count);
            Assert.All(before, r => Assert.StartsWith("Mad-", r.InvoiceNo));
            Assert.Contains(before, r => r.PartyType == "SUPPLIERS");

            InvoiceHistoryBook.Append(root, path, new HistoryRecord
            {
                Name = "أركان التجريبية", Amount = 115000m, Date = new DateTime(2026, 11, 7), InvoiceNo = "Mad-1000001",
                PartyType = "CLIENTS", RecordId = "R", FilePath = @"C:\Madar Database\BATCH STUDIO\CLIENTS\x\Mad-1000001.xlsx", Template = "Madar",
            });
            var after = InvoiceHistoryBook.Read(path);
            Assert.Equal(85, after.Count);
            var last = after[^1];
            Assert.Equal("INV-85", last.No);
            Assert.Equal("Mad-1000001", last.InvoiceNo);
            Assert.Equal(before[^1].Row + 1, last.Row);
            using (var wb = new XLWorkbook(path))
                Assert.Equal(last.Row, InvoiceHistoryBook.Sheet(wb).PageSetup.PrintAreas.First().RangeAddress.LastAddress.RowNumber);

            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.Copy(path, Path.Combine(outDir, "History_after_append.xlsx"), true);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class HistoryActionsTests
{
    [Theory]
    [InlineData("This Week", "2026-11-08", "2026-11-07", "2026-11-13")]   // Sunday -> week from Saturday
    [InlineData("This Week", "2026-11-07", "2026-11-07", "2026-11-13")]   // Saturday itself
    [InlineData("This Month", "2026-11-08", "2026-11-01", "2026-11-30")]
    [InlineData("Last Month", "2026-01-15", "2025-12-01", "2025-12-31")]
    [InlineData("This Year", "2026-11-08", "2026-01-01", "2026-12-31")]
    public void Period_windows(string period, string today, string from, string to)
    {
        var w = HistoryPeriods.Window(period, DateTime.Parse(today), DayOfWeek.Saturday)!.Value;
        Assert.Equal(DateTime.Parse(from), w.From);
        Assert.Equal(DateTime.Parse(to), w.To);
    }

    [Fact]
    public void Report_names() 
    {
        Assert.Equal("Today's Report", HistoryPeriods.ReportName("Today"));
        Assert.Equal("Custom Range Report", HistoryPeriods.ReportName("Custom Range"));
    }

    [Fact]
    public void Query_matches_tab_period_and_arabic_text()
    {
        var r = new HistoryRecord { Name = "مؤسسة أفاق النموذج", InvoiceNo = "Mad-1", PartyType = "CLIENTS", Amount = 1500m, Date = new DateTime(2026, 11, 3) };
        Assert.True(new HistoryQuery { Text = "افاق", Today = new DateTime(2026, 11, 8) }.Matches(r));
        Assert.True(new HistoryQuery { Text = "1,500.00" }.Matches(r));
        Assert.False(new HistoryQuery { Tab = "SUPPLIERS" }.Matches(r));
        Assert.True(new HistoryQuery { Period = "This Month", Today = new DateTime(2026, 11, 8) }.Matches(r));
        Assert.False(new HistoryQuery { Period = "This Week", Today = new DateTime(2026, 11, 8) }.Matches(r));
        Assert.True(new HistoryQuery { Period = "Custom Range", From = new DateTime(2026, 11, 1), To = new DateTime(2026, 11, 3) }.Matches(r));
    }

    private static (string Root, string Path) RealCopy()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bs-ha-" + Guid.NewGuid().ToString("N"));
        var path = DataSets.InvoiceHistory.FullPath(root);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "History_V28.xlsx"), path);
        return (root, path);
    }

    [Fact]
    public void Remove_deletes_row_and_file_and_renumbers()
    {
        var (root, path) = RealCopy();
        try
        {
            var recs = InvoiceHistoryBook.Read(path);
            var victim = recs[1];
            var file = System.IO.Path.Combine(root, "BATCH STUDIO", victim.PartyType, "Party", victim.InvoiceNo + ".xlsx");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "x");

            InvoiceHistoryBook.Remove(root, path, victim.InvoiceNo, deleteFile: true);
            Assert.False(File.Exists(file));
            var after = InvoiceHistoryBook.Read(path);
            Assert.Equal(recs.Count - 1, after.Count);
            Assert.DoesNotContain(after, r => r.InvoiceNo == victim.InvoiceNo);
            Assert.Equal("INV-2", after[1].No);
            Assert.Equal(recs[2].InvoiceNo, after[1].InvoiceNo);
            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var ws = InvoiceHistoryBook.Sheet(wb);
            Assert.Equal((double)after.Count, ws.Cell("C3").GetDouble());
            Assert.Equal(after[^1].Row, ws.PageSetup.PrintAreas.First().RangeAddress.LastAddress.RowNumber);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Statuses_relink_and_report()
    {
        var (root, path) = RealCopy();
        try
        {
            Assert.True(InvoiceHistoryBook.UpdateStatuses(root, path) > 0);       // no files exist -> all missing
            var recs = InvoiceHistoryBook.Read(path);
            Assert.All(recs, r => Assert.Equal("File missing", r.Status));

            var moved = System.IO.Path.Combine(root, "moved.xlsx");
            File.WriteAllText(moved, "x");
            InvoiceHistoryBook.Relink(root, path, recs[0].InvoiceNo, moved);
            var r0 = InvoiceHistoryBook.Read(path)[0];
            Assert.Equal(moved, r0.FilePath);
            Assert.Equal("File available", r0.Status);

            var report = System.IO.Path.Combine(root, "report.xlsx");
            var q = new HistoryQuery { Tab = "SUPPLIERS" };
            InvoiceHistoryBook.BuildReport(path, q.Matches, "All Time", report);
            using var wb = new ClosedXML.Excel.XLWorkbook(report);
            var ws = InvoiceHistoryBook.Sheet(wb);
            int shown = recs.Count(q.Matches);
            Assert.Equal((double)shown, ws.Cell("C3").GetDouble());
            Assert.Equal(shown, recs.Count(r => !ws.Row(r.Row).IsHidden));

            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.Copy(report, System.IO.Path.Combine(outDir, "Report_suppliers.xlsx"), true);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
