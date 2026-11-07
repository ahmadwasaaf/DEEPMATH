using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

/// <summary>
/// The three V28 templates (bank details / numbers replaced by placeholders) filled end to end.
/// With BS_SAMPLE_OUT set, the filled invoices are also copied there (CI publishes them for visual checks).
/// </summary>
public class RealTemplateTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".xlsx");

    private static readonly List<CatalogItem> Pumps = Enumerable.Range(1, 40)
        .Select(i => new CatalogItem($"PMP-{100000 + i * 7919 % 900000}", $"Pump model {i} - {i % 10 + 1} HP", 120m + i * 23.75m, "PMP")).ToList();

    [Theory]
    [InlineData("INVOICE", 14, 30000)]
    [InlineData("INVOICE", 8, 30000)]
    [InlineData("INVOICE", 20, 87500.5)]
    [InlineData("INV1", 8, 30000)]
    [InlineData("INV1", 12, 45000)]
    [InlineData("Madar", 11, 255000)]
    [InlineData("Madar", 5, 115000)]
    [InlineData("Madar", 16, 400000)]
    public void Real_template_fills_exactly(string template, int count, double amount)
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "Test");
            var tpl = TemplateRegistry.Import(root, Fixture(template));
            var map = TemplateDetector.DetectFile(tpl);
            Assert.NotNull(map.Items);
            decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = map.EffectivePartyType,
                PartyName = map.EffectivePartyType == "CLIENTS" ? "أركان التجريبية" : "SAMPLE TRADING CO., LIMITED",
                NativeName = "अरविंद एंटरप्राइजेज",
                Address = "Company Address:\nUnit 5, Sample Tower, Sample Road, Sample City 700027",
                Bank = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: SAMPLE BANK\nBRANCH: MAIN\nACCOUNT NO: 0000000000\nSWIFT CODE: SAMPLEXX\nIFSC: SMPL0000001",
                CategoryText = "Water Pumps & Motors | مضخات ومواتير المياه",
                InvoiceNo = $"Mad-{1000000 + count * 1111}", Date = new DateTime(2026, 11, 7), Target = (decimal)amount,
                Lines = InvoiceAllocator.Allocate((decimal)amount, count, Pumps, vat, new Random(count)),
            };
            Assert.Equal((decimal)amount, draft.Total);
            var path = InvoiceService.Archive(root, draft);

            using (var wb = new XLWorkbook(path))
            {
                var ws = wb.Worksheets.First();
                var total = map.Fields[TemplateFields.Total].Cell!;
                TemplateDetector.TryParseRef(total, out var tr, out var tc);
                int shift = count - map.Items!.Capacity;
                Assert.Equal(amount, ws.Cell(tr + shift, tc).GetDouble(), 2);
                Assert.DoesNotContain(ws.CellsUsed(), c => c.HasFormula);
                // re-detecting the filled invoice finds the same layout (table grew/shrank cleanly)
                var again = TemplateDetector.Detect(SheetModel.FromWorksheet(ws));
                Assert.Equal(count, again.Items!.Capacity);
            }

            var outDir = Environment.GetEnvironmentVariable("BS_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.Copy(path, Path.Combine(outDir, $"{template}_{count}_items.xlsx"), true);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
