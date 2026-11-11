using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Workbook;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

/// <summary>The Templates workbook (INV2, INV3 ...): found beside the system workbook, detected, filled like INVOICE / INV1.</summary>
public class TemplatesBookTests
{
    private static string Book => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Templates.xlsx");

    private static string ExtractSheet(string sheet, string dir)
    {
        var pkg = SheetExtractor.Load(File.ReadAllBytes(Book));
        var path = Path.Combine(dir, sheet + ".xlsx");
        File.WriteAllBytes(path, SheetExtractor.Extract(pkg, sheet));
        return path;
    }

    [Theory]
    [InlineData("INV2")]
    [InlineData("INV3")]
    [InlineData("INV4")]
    [InlineData("INV5")]
    [InlineData("INV6")]
    public void Every_template_of_the_book_is_detected_through_its_names(string sheet)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-tpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var map = TemplateDetector.DetectFile(ExtractSheet(sheet, dir));
            foreach (var f in new[] { TemplateFields.PartyName, TemplateFields.PartyNativeName, TemplateFields.InvoiceNo, TemplateFields.IssueDate,
                                      TemplateFields.Total, TemplateFields.Bank, TemplateFields.Address })
            {
                Assert.NotNull(map.Fields[f].Cell);
                Assert.Equal(0.99, map.Fields[f].Confidence, 2);
            }
            Assert.NotNull(map.Items);
            Assert.Equal(7, map.Items!.Capacity);
            Assert.Equal(["amount", "code", "description", "price", "qty"], map.Items.Columns.Keys.Order().ToArray());
            Assert.NotNull(map.Fields[TemplateFields.Category].Cell);
            Assert.NotNull(map.Fields[TemplateFields.AmountWords].Cell);
            Assert.Equal("SUPPLIERS", map.EffectivePartyType);
            Assert.True(ThemeMemory.Supports(sheet));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("INV2", 7, 120000)]
    [InlineData("INV2", 13, 45000.5)]
    [InlineData("INV3", 3, 30000)]
    [InlineData("INV3", 10, 87500)]
    [InlineData("INV4", 12, 60000)]
    [InlineData("INV5", 4, 15000)]
    [InlineData("INV6", 9, 99999.99)]
    public void Template_of_the_book_fills_exactly(string sheet, int count, double amount)
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-tplfill-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "Test");
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            var tpl = TemplateRegistry.Import(root, ExtractSheet(sheet, src));
            var map = TemplateDetector.DetectFile(tpl);
            var items = Enumerable.Range(1, 30).Select(i => new CatalogItem($"OFF-{100000 + i * 7919 % 900000}", $"Office item {i}", 20m + i * 9.5m, "OFF")).ToList();
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS",
                PartyName = "SAMPLE TRADING CO., LIMITED", NativeName = "示例贸易有限公司",
                Address = "Company Address:\nUnit 5, Sample Tower,\nSample Road, Sample City 700027",
                Bank = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: SAMPLE BANK\nACCOUNT NO: 0000000000\nSWIFT CODE: SAMPLEXX",
                CategoryText = "Office & Stationery Supplies | المستلزمات المكتبية والقرطاسية",
                InvoiceNo = $"Mad-{2000000 + count}", Date = new DateTime(2026, 11, 11), Target = (decimal)amount,
                Lines = InvoiceAllocator.Allocate((decimal)amount, count, items, null, new Random(count)),
            };
            var path = InvoiceService.Archive(root, draft);
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();
            TemplateDetector.TryParseRef(map.Fields[TemplateFields.Total].Cell!, out var tr, out var tc);
            int shift = count - map.Items!.Capacity;
            Assert.Equal(amount, ws.Cell(tr + shift, tc).GetDouble(), 2);
            TemplateDetector.TryParseRef(map.Fields[TemplateFields.PartyName].Cell!, out var pr, out var pc);
            Assert.Equal("SAMPLE TRADING CO., LIMITED", ws.Cell(pr, pc).GetString());
            Assert.Contains("BUYER / CONSIGNEE", string.Join("\n", ws.CellsUsed().Select(c => c.GetString())));
            Assert.Equal(count, TemplateDetector.Detect(SheetModel.FromWorksheet(ws)).Items!.Capacity);

            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.Copy(path, Path.Combine(outDir, $"{sheet}_{count}_items.xlsx"), true);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Templates_workbook_beside_the_system_workbook_adds_its_sheets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs-tplbook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var book = Path.Combine(dir, "Demo.xlsm");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Demo_Master.xlsm"), book);
            File.Copy(Book, Path.Combine(dir, "Templates.xlsx"));
            var ws = Workspace.Open(book, Path.Combine(dir, "state"));
            Assert.Contains("INV2", ws.TemplateSheets);
            Assert.Contains("INV3", ws.TemplateSheets);
            Assert.Contains("INV6", ws.TemplateSheets);
            Assert.Equal(Path.Combine(dir, "Templates.xlsx"), ws.BookFor("INV2"));
            Assert.Equal(ws.WorkbookPath, ws.BookFor("INVOICE"));
            var cached = Path.Combine(ws.TemplatesDir, "INV2.xlsx");
            Assert.True(File.Exists(cached));
            // linked automatically: every main field was found
            Assert.NotNull(TemplateMap.LoadFor(cached));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
