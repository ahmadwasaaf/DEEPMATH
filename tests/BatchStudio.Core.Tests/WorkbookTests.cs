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
    public void Sheets_of_a_closedxml_workbook_are_found()
    {
        var (dir, book) = SmallBook();
        try
        {
            var bytes = File.ReadAllBytes(book);
            var pkg = SheetExtractor.Load(bytes);
            string Wb()
            {
                using var z = new ZipArchive(new MemoryStream(bytes));
                using var r = new StreamReader(z.GetEntry("xl/workbook.xml")!.Open());
                var x = r.ReadToEnd();
                using var r2 = new StreamReader(z.GetEntry("xl/_rels/workbook.xml.rels")!.Open());
                return x[..Math.Min(1500, x.Length)] + "\n" + r2.ReadToEnd();
            }
            Assert.True(pkg.Find("Suppliers") != null && pkg.Find("INVOICE HISTORY") != null, Wb());
            Assert.True(File.Exists(Path.Combine(dir, "x")) || pkg.Sheets.All(s => s.PartName.Length > 0), Wb());
        }
        finally { Clean(dir); }
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
    public void A_second_bank_account_adds_columns_and_becomes_the_invoice_bank()
    {
        var (dir, book) = SmallBook();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            var session = new DataSetSession(ws, DataSets.Suppliers);
            session.Load();
            var t = session.Current.Clone();
            Assert.Equal(-1, SupplierBanks.BaseColumn(t.Headers));
            t.Headers.Add(SupplierBanks.BaseHeader);
            t.Headers.Add(SupplierBanks.DefaultHeader);
            t.Headers.Add(SupplierBanks.HeaderFor(2));
            foreach (var r in t.Rows.ToList())
            {
                int i = t.Rows.IndexOf(r);
                var arr = r.Concat(new string[t.Headers.Count - r.Length]).Select(x => x ?? "").ToArray();
                t.Rows[i] = arr;
            }
            t.Rows[0][t.Headers.Count - 3] = "FIRST BANK";
            t.Rows[0][t.Headers.Count - 2] = "2";
            t.Rows[0][t.Headers.Count - 1] = "SECOND BANK\nACCOUNT 2";
            session.SetCurrent(t);
            session.Save();

            using (var wb = new XLWorkbook(book))
            {
                var s = wb.Worksheet("Suppliers");
                int last = s.Row(1).LastCellUsed()!.Address.ColumnNumber;
                Assert.Equal(SupplierBanks.HeaderFor(2), s.Cell(1, last).GetString());
                Assert.Equal(SupplierBanks.DefaultHeader, s.Cell(1, last - 1).GetString());
                Assert.Equal("SECOND BANK\nACCOUNT 2", s.Cell(2, last).GetString());
            }
            session.Load();
            var party = InvoiceService.Suppliers(session.Current)[0];
            Assert.Equal(2, party.DefaultBank);
            Assert.True(party.HasManyBanks);
            Assert.Equal("SECOND BANK\nACCOUNT 2", party.Bank);
            Assert.Equal("FIRST BANK", party.BankText(1));
            Assert.Equal("SECOND BANK\nACCOUNT 2", party.BankText(2));
            // the second supplier has no extra account: account 1 is printed
            var other = InvoiceService.Suppliers(session.Current)[1];
            Assert.Equal(1, other.DefaultBank);
            Assert.False(other.HasManyBanks);
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Bank_headers_are_recognised()
    {
        string[] h = ["Supplier ID", "Supplier Name (English)", "Banking Details (full paragraph)", "Company’s Activities", "DEFAULT BANK",
            "Banking Details (full paragraph) 2", "Banking Details (full paragraph) 3"];
        Assert.Equal(2, SupplierBanks.BaseColumn(h));
        Assert.Equal(new List<(int, int)> { (1, 2), (2, 5), (3, 6) }, SupplierBanks.Columns(h).Select(x => (x.Number, x.Column)).ToList());
        Assert.True(SupplierBanks.IsBankHeader(h, 4));
        Assert.False(SupplierBanks.IsBankHeader(h, 3));
        string[] row = ["SUP-1", "A", "BANK ONE", "", "3", "", "BANK THREE"];
        var acc = SupplierBanks.Accounts(h, row);
        Assert.Equal(new[] { 1, 3 }, acc.Select(a => a.Number).ToArray());
        Assert.Equal(3, SupplierBanks.DefaultNumber(h, row, acc));
        row[6] = "";   // the default account is empty: account 1
        Assert.Equal(1, SupplierBanks.DefaultNumber(h, row, SupplierBanks.Accounts(h, row)));
    }

    [Fact]
    public void Mass_invoice_archives_many_and_records_them_in_one_change()
    {
        var (dir, book) = SmallBook();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            int before = InvoiceHistoryBook.Read(ws.HistoryPath).Count;
            var tpl = Path.Combine(dir, "INVOICE.xlsx");
            File.Copy(Fx("INVOICE.xlsx"), tpl);
            var map = TemplateDetector.DetectFile(tpl);
            InvoiceDraft Draft(string party, string no, decimal amount, int seed) => new()
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS",
                PartyName = party, Address = "Company Address:\nX", Bank = "PAYMENT & BANKING INSTRUCTIONS\nBANK: Y",
                CategoryText = "Lighting | الإضاءة", InvoiceNo = no, Date = new DateTime(2026, 11, 9), Target = amount,
                Lines = InvoiceAllocator.Allocate(amount, 5, Pool, null, new Random(seed)),
            };
            var drafts = new List<InvoiceDraft>
            {
                Draft("ALPHA CO", "Mad-1000001", 10000m, 1),
                Draft("BETA CO", "Mad-1000002", 20500m, 2),
                Draft("ALPHA CO", "Mad-1000001", 9000m, 3),   // same number again: refused, the others go on
                Draft("GAMMA CO", "Mad-1000003", 7500m, 4),
            };
            var seen = new List<int>();
            var res = InvoiceService.ArchiveBatch(ws, drafts, progress: (i, f, e) => seen.Add(i));
            Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
            Assert.True(res[0].Recorded && res[1].Recorded && res[3].Recorded);
            Assert.False(res[2].Recorded);
            Assert.NotNull(res[2].Error);
            Assert.True(File.Exists(res[1].File));

            var after = InvoiceHistoryBook.Read(ws.HistoryPath);
            Assert.Equal(before + 3, after.Count);
            Assert.Equal(new[] { "Mad-1000001", "Mad-1000002", "Mad-1000003" }, after.Skip(before).Select(r => r.InvoiceNo).ToArray());
            Assert.Equal($"INV-{before + 3}", after[^1].No);
            Assert.Equal(7500m, after[^1].Amount);
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

    // ------------------------------------------------------------------ Smart Invoice Repair
    private static List<CatalogItem> BigPool(string cat) =>
        Enumerable.Range(1, 40).Select(i => new CatalogItem($"{cat}-{i:000}", $"Product {i}", 20m + i * 7.5m, cat)).ToList();

    [Fact]
    public void Repair_restores_an_archived_invoice_and_adds_only_new_items()
    {
        var (dir, book) = DemoFolder();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            foreach (var t in TemplateRegistry.Scan(ws.CacheRoot))
            {
                var map = t.Map!;
                decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
                var pool = BigPool("TST");
                var lines = InvoiceAllocator.Allocate(40000m, 6, pool, vat, new Random(3));
                var draft = new InvoiceDraft
                {
                    TemplatePath = t.Path, Map = map, PartyType = map.EffectivePartyType,
                    PartyName = "Supplier 001 Trading Co., Limited", NativeName = "示例公司 001",
                    Address = "Company Address:\nUnit 1", Bank = "PAYMENT & BANKING INSTRUCTIONS\nBANK: DEMO",
                    CategoryText = "Lighting | الإضاءة", InvoiceNo = "Mad-5550001", Date = new DateTime(2026, 10, 1),
                    Target = 40000m, Lines = lines,
                };
                var archived = Path.Combine(dir, t.Name + "_archived.xlsx");
                TemplateFiller.Fill(draft, archived);

                var src = InvoiceRepair.Read(archived, map, "Mad-5550001");
                Assert.Equal(6, src.Lines.Count);
                Assert.Equal(40000m, src.Total);
                Assert.Equal(new DateTime(2026, 10, 1), src.Date);
                Assert.Equal(lines.Select(l => l.Code), src.Lines.Select(l => l.Code));
                Assert.Throws<InvalidOperationException>(() => InvoiceRepair.Read(archived, map, "Mad-0000000"));

                var plan = InvoiceRepair.Extend(src.Lines, 55000m, 9, pool, vat, new Random(5));
                Assert.True(plan.FinalCount >= 9, t.Name);
                Assert.Equal(5500000, InvoiceRepair.Value(plan.Lines, vat));
                for (int i = 0; i < 6; i++)
                {
                    Assert.Equal(src.Lines[i].Code, plan.Lines[i].Code);
                    Assert.Equal(src.Lines[i].Quantity, plan.Lines[i].Quantity);
                    Assert.Equal(src.Lines[i].UnitCents, plan.Lines[i].UnitCents);
                }
                Assert.Equal(plan.Lines.Count, plan.Lines.Select(l => l.Code).Distinct().Count());
                Assert.StartsWith("7- ", plan.Lines[6].Description);

                Assert.Throws<AllocationException>(() => InvoiceRepair.Extend(src.Lines, 30000m, 8, pool, vat));   // below protected items
                Assert.Throws<AllocationException>(() => InvoiceRepair.Extend(src.Lines, 50000m, 5, pool, vat));   // fewer rows
            }
        }
        finally { Clean(dir); }
    }

    [Fact]
    public void Repair_replaces_the_archived_file_and_updates_the_same_history_row()
    {
        var (dir, book) = SmallBook();
        try
        {
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            var tpl = Path.Combine(dir, "INVOICE.xlsx");
            File.Copy(Fx("INVOICE.xlsx"), tpl);
            var map = TemplateDetector.DetectFile(tpl);
            var pool = BigPool("TST");
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS", PartyName = "GAMMA CO",
                CategoryText = "Lighting | الإضاءة", InvoiceNo = "Mad-8880001", Date = new DateTime(2026, 9, 1), Target = 20000m,
                Lines = InvoiceAllocator.Allocate(20000m, 5, pool, null, new Random(8)),
            };
            var file = InvoiceService.Archive(ws, draft);
            int count = InvoiceHistoryBook.Read(ws.HistoryPath).Count;

            var src = InvoiceRepair.Read(file, map, "Mad-8880001");
            var plan = InvoiceRepair.Extend(src.Lines, 26000m, 7, pool, null, new Random(9));
            var repaired = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS", PartyName = src.PartyName,
                CategoryText = src.CategoryText, InvoiceNo = "Mad-8880001", Date = new DateTime(2026, 11, 8), Target = 26000m,
                Lines = plan.Lines,
            };
            Assert.Throws<InvalidOperationException>(() => InvoiceService.Replace(ws, repaired, "Mad-0000001", file));
            Assert.Equal(file, InvoiceService.Replace(ws, repaired, "Mad-8880001", file));

            var recs = InvoiceHistoryBook.Read(ws.HistoryPath);
            Assert.Equal(count, recs.Count);                                  // updated, not duplicated
            var rec = recs.Single(r => r.InvoiceNo == "Mad-8880001");
            Assert.Equal(26000m, rec.Amount);
            Assert.Equal(new DateTime(2026, 11, 8), rec.Date);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.bak"));
            var again = InvoiceRepair.Read(file, map, "Mad-8880001");
            Assert.Equal(plan.Lines.Count, again.Lines.Count);
            Assert.Equal(26000m, again.Total);
        }
        finally { Clean(dir); }
    }
}
