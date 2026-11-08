using System.IO.Compression;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Reports;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Workbook;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

/// <summary>The program working directly on the single BATCH STUDIO workbook (sanitized copy of V28.1).</summary>
public class WorkbookTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (string Dir, string Book) DemoFolder(string fixture = "Demo_Master.xlsm", string name = "BATCH_STUDIO_V28.1.xlsm")
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-wb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "BATCH STUDIO", "SUPPLIERS"));
        Directory.CreateDirectory(Path.Combine(dir, "BATCH STUDIO", "CLIENTS"));
        var book = Path.Combine(dir, name);
        File.Copy(Fx(fixture), book);
        return (dir, book);
    }

    private static void Clean(string dir) { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Finds_the_workbook_in_its_folder()
    {
        var (dir, book) = DemoFolder();
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.xlsx"), "not a workbook");
            Assert.Equal(book, Workspace.FindWorkbook(dir));
            Assert.Equal(book, Workspace.FindWorkbook(book));
            Assert.Null(Workspace.FindWorkbook(Path.Combine(dir, "BATCH STUDIO")));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Every_sheet_is_read_from_the_real_workbook()
    {
        var (dir, book) = DemoFolder();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            Assert.Equal(Path.Combine(dir, "BATCH STUDIO"), ws.ArchiveRoot);
            Assert.Equal(@"D:\Archive\BATCH STUDIO", ws.ConfiguredArchiveRoot);

            var sup = ExcelTableStore.Load(ws.DataPath(DataSets.Suppliers), "Suppliers");
            Assert.Equal("Supplier Name (English)", sup.Headers[1]);
            Assert.True(sup.Rows.Count >= 50, $"suppliers: {sup.Rows.Count}");
            Assert.True(ExcelTableStore.Load(ws.DataPath(DataSets.Clients), "Clients").Rows.Count >= 200);
            Assert.True(ExcelTableStore.Load(ws.DataPath(DataSets.Items), "Items").Rows.Count >= 4000);
            Assert.True(ExcelTableStore.Load(ws.DataPath(DataSets.Categories), "Categories").Rows.Count >= 40);

            var history = InvoiceHistoryBook.Read(ws.HistoryPath);
            Assert.Equal(89, history.Count);
            Assert.All(history, h => Assert.StartsWith("Mad-", h.InvoiceNo));
            Assert.Equal("Mad", InvoiceService.DetectPrefix(history.Select(h => h.InvoiceNo)));

            Assert.True(File.Exists(ws.DashboardPath));
            Assert.True(File.Exists(ws.ReportTemplatePath));
            using (var dash = PackageRepair.OpenWorkbook(ws.DashboardPath))
                Assert.Equal("All Time", dash.Worksheet(1).Cell("G3").GetString());

            Assert.Equal(["INVOICE", "INV1", "Madar"], ws.TemplateSheets);
            var templates = TemplateRegistry.Scan(ws.CacheRoot);
            Assert.Equal(3, templates.Count);
            Assert.All(templates, t => Assert.Equal(TemplateStatus.Linked, t.Status));   // linked automatically

            // sheet copies are plain .xlsx: no VBA, one sheet
            foreach (var f in Directory.GetFiles(ws.CacheRoot, "*.xlsx", SearchOption.AllDirectories))
            {
                using var z = ZipFile.OpenRead(f);
                Assert.DoesNotContain(z.Entries, e => e.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase));
                using var wb = PackageRepair.OpenWorkbook(f);
                Assert.Single(wb.Worksheets);
            }

            // the workbook itself is untouched and nothing is re-copied while it does not change
            Assert.Equal(File.ReadAllBytes(Fx("Demo_Master.xlsm")), File.ReadAllBytes(book));
            Assert.Empty(ws.Refresh(force: true));
            var again = Workspace.Open(book, Path.Combine(dir, "state"));
            Assert.Empty(again.Refresh(force: true));
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Real_templates_from_the_workbook_fill_invoices()
    {
        var (dir, book) = DemoFolder();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            foreach (var t in TemplateRegistry.Scan(ws.CacheRoot))
            {
                var map = t.Map!;
                decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
                var draft = new InvoiceDraft
                {
                    TemplatePath = t.Path, Map = map, PartyType = map.EffectivePartyType,
                    PartyName = "Supplier 001 Trading Co., Limited", NativeName = "示例公司 001",
                    Address = "Company Address:\nUnit 1", Bank = "PAYMENT & BANKING INSTRUCTIONS\nBANK: DEMO",
                    CategoryText = "Lighting | الإضاءة",
                    InvoiceNo = "Mad-1234567", Date = new DateTime(2026, 11, 8), Target = 50000m,
                    Lines = InvoiceAllocator.Allocate(50000m, 9, Pool, vat, new Random(9)),
                };
                var outFile = Path.Combine(dir, t.Name + "_filled.xlsx");
                TemplateFiller.Fill(draft, outFile);
                using var wb = new XLWorkbook(outFile);
                Assert.Contains(wb.Worksheet(1).CellsUsed(), c => c.GetString() == "Mad-1234567");
            }
        }
        finally { Clean(dir); }
    }

    private static readonly List<CatalogItem> Pool =
    [
        new("P1", "LED panel light 60x60", 120m, "C1"), new("P2", "LED bulb 12W", 8.5m, "C1"),
        new("P3", "Flood light 100W", 95m, "C1"), new("P4", "Street light 150W", 210m, "C1"),
        new("P5", "Track light 30W", 45m, "C1"), new("P6", "Ceiling lamp", 60m, "C1"),
        new("P7", "Wall lamp", 35m, "C1"), new("P8", "Solar light", 75m, "C1"),
        new("P9", "Strip light 5m", 22m, "C1"), new("P10", "Emergency light", 30m, "C1"),
    ];

    // ------------------------------------------------------------------ writing (ClosedXML editor on a plain .xlsx)
    /// <summary>A small .xlsx "workbook" with the V28 history sheet and a formatted Suppliers sheet.</summary>
    private static (string Dir, string Book) SmallBook()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-wbw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "BATCH STUDIO", "SUPPLIERS"));
        var book = Path.Combine(dir, "BATCH_STUDIO_TEST.xlsx");
        File.Copy(Fx("History_V28.xlsx"), book);
        using (var wb = new XLWorkbook(book))
        {
            var s = wb.Worksheets.Add("Suppliers");
            string[] h = ["Supplier ID", "Supplier Name (English)", "Chinese Name", "Category (English | Arabic)", "Notes"];
            for (int i = 0; i < h.Length; i++) s.Cell(1, i + 1).Value = h[i];
            s.Cell(2, 1).Value = "SUP-001"; s.Cell(2, 2).Value = "ALPHA CO"; s.Cell(2, 4).Value = "Lighting | الإضاءة";
            s.Cell(3, 1).Value = "SUP-002"; s.Cell(3, 2).Value = "BETA CO"; s.Cell(3, 3).Value = "贝塔";
            s.Cell(2, 5).Value = "keep me";
            s.Range("A2:E3").Style.Fill.BackgroundColor = XLColor.FromArgb(255, 240, 200);
            wb.Save();
        }
        return (dir, book);
    }

    [Fact]
    public void Saving_a_data_set_changes_only_its_cells_in_the_workbook()
    {
        var (dir, book) = SmallBook();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            var session = new DataSetSession(ws, DataSets.Suppliers);
            session.Load();
            var t = session.Current.Clone();
            t.Rows[1][1] = "BETA TRADING CO";                      // edit
            t.Rows.Add(["SUP-003", "GAMMA CO", "", "Lighting | الإضاءة", ""]);   // add
            session.SetCurrent(t);
            session.Save();

            Assert.Single(Directory.GetFiles(ws.BackupDir));
            using (var wb = new XLWorkbook(book))
            {
                var s = wb.Worksheet("Suppliers");
                Assert.Equal("BETA TRADING CO", s.Cell(3, 2).GetString());
                Assert.Equal("GAMMA CO", s.Cell(4, 2).GetString());
                Assert.Equal("keep me", s.Cell(2, 5).GetString());
                Assert.Equal(XLColor.FromArgb(255, 240, 200), s.Cell(3, 2).Style.Fill.BackgroundColor);
                Assert.True(wb.Worksheets.Contains("INVOICE HISTORY"));
            }
            // the sheet copy was refreshed, so the session is in sync
            Assert.Equal(3, ExcelTableStore.Load(ws.DataPath(DataSets.Suppliers), "Suppliers").Rows.Count);
            Assert.Equal(ExternalChangeOutcome.Ignored, session.OnFileChanged());

            // delete a row: the rows below move up, the last one is cleared
            var d = session.Current.Clone();
            d.Rows.RemoveAt(0);
            session.SetCurrent(d);
            session.Save();
            using (var wb = new XLWorkbook(book))
            {
                var s = wb.Worksheet("Suppliers");
                Assert.Equal("SUP-002", s.Cell(2, 1).GetString());
                Assert.Equal("SUP-003", s.Cell(3, 1).GetString());
                Assert.True(s.Cell(4, 1).IsEmpty());
            }
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Archiving_writes_the_history_row_into_the_workbook()
    {
        var (dir, book) = SmallBook();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            int before = InvoiceHistoryBook.Read(ws.HistoryPath).Count;
            var tpl = Path.Combine(dir, "INVOICE.xlsx");
            File.Copy(Fx("INVOICE.xlsx"), tpl);
            var map = TemplateDetector.DetectFile(tpl);
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS",
                PartyName = "GAMMA CO", NativeName = "伽马", Address = "Company Address:\nX", Bank = "PAYMENT & BANKING INSTRUCTIONS\nBANK: Y",
                CategoryText = "Lighting | الإضاءة", InvoiceNo = "Mad-7777777", Date = new DateTime(2026, 11, 8), Target = 12345m,
                Lines = InvoiceAllocator.Allocate(12345m, 6, Pool, null, new Random(6)),
            };
            var file = InvoiceService.Archive(ws, draft);
            Assert.Equal(Path.Combine(dir, "BATCH STUDIO", "SUPPLIERS", "GAMMA CO", "Mad-7777777.xlsx"), file);
            Assert.True(File.Exists(file));

            // the sheet copy follows the workbook
            var after = InvoiceHistoryBook.Read(ws.HistoryPath);
            Assert.Equal(before + 1, after.Count);
            var rec = after[^1];
            Assert.Equal("Mad-7777777", rec.InvoiceNo);
            Assert.Equal($"INV-{before + 1}", rec.No);
            Assert.Equal(12345m, rec.Amount);
            Assert.Equal(file, rec.FilePath);
            Assert.Equal("File available", rec.Status);

            // the same number again is refused and leaves nothing behind
            Assert.Throws<InvalidOperationException>(() => InvoiceService.Archive(ws, draft));

            // remove (with file) through the workbook
            var removed = ws.Change(ed => InvoiceHistoryBook.Remove(ed, ws.ArchiveRoot, "Mad-7777777", deleteFile: true));
            Assert.Equal(file, removed);
            InvoiceHistoryBook.DeleteArchivedFile(removed!, ws.ArchiveRoot);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
            Assert.True(Directory.Exists(Path.Combine(dir, "BATCH STUDIO", "SUPPLIERS")));
            Assert.Equal(before, InvoiceHistoryBook.Read(ws.HistoryPath).Count);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Paths_follow_the_current_archive_like_the_vba()
    {
        var archive = Path.Combine(Path.GetTempPath(), "bs-arc-" + Guid.NewGuid().ToString("N"), "BATCH STUDIO");
        try
        {
            var local = Path.Combine(archive, "CLIENTS", "Client 001", "Mad-6942817.xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, "x");
            Assert.Equal(local, InvoiceHistoryBook.ResolvePath(archive, @"D:\EGY\Accounting\Clints\BATCH STUDIO\CLIENTS\Client 001\Mad-6942817.xlsx"));
            Assert.Equal(local, InvoiceHistoryBook.ResolvePath(archive, @"E:\Old archive\CLIENTS\Client 001\Mad-6942817.xlsx"));
            Assert.Equal(local, InvoiceHistoryBook.ResolvePath(archive, @"CLIENTS\Client 001\Mad-6942817.xlsx"));
            Assert.Equal(local, InvoiceHistoryBook.ResolvePath(archive, @"BATCH STUDIO\CLIENTS\Client 001\Mad-6942817.xlsx"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(archive)!, true); } catch { } }
    }

    [Fact]
    public void Dashboard_numbers_come_from_the_workbook_history()
    {
        var (dir, book) = DemoFolder();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            var history = InvoiceHistoryBook.Read(ws.HistoryPath);
            var d = DashboardEngine.Compute(history, new Dictionary<string, string>(), new DashboardFilter());
            Assert.Equal(89, d.Invoices);
            Assert.Equal(history.Sum(h => h.Amount ?? 0), d.Total);
        }
        finally { Clean(dir); }
    }
}
