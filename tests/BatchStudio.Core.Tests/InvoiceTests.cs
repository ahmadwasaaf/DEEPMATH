using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

public class InvoiceTests
{
    [Fact]
    public void Words_match_V28_templates()
    {
        Assert.Equal("(ثلاثون ألف دولار أمريكي فقط) Total Invoice Amount\n(Thirty Thousand US Dollars Only)",
            AmountWords.Format(30000m, InvoiceCurrency.USD));
        // Arabic "و" is joined to the next word, as in the V28 Madar template text
        Assert.Equal("(مائتان وخمسة وخمسون ألف ريال سعودي فقط) Total Amount Including VAT\n(Two Hundred Fifty-Five Thousand Saudi Riyals Only)",
            AmountWords.Format(255000m, InvoiceCurrency.SAR));
        Assert.Contains("and Fifty Cents Only", AmountWords.Format(1234.50m, InvoiceCurrency.USD));
        Assert.StartsWith("(خمسة عشر دولارًا أمريكيًا", AmountWords.Format(15m, InvoiceCurrency.USD));
        Assert.StartsWith("(خمسة دولارات أمريكية", AmountWords.Format(5m, InvoiceCurrency.USD));
        Assert.Contains("One Thousand Two Hundred Thirty-Four US Dollars", AmountWords.Format(1234.50m, InvoiceCurrency.USD));
    }

    private static List<CatalogItem> Pool(int n = 30) =>
        Enumerable.Range(1, n).Select(i => new CatalogItem($"PMP-{i:000}", $"Pump {i}", 50m + i * 37.5m, "PMP")).ToList();

    [Theory]
    [InlineData(30000, 14)]
    [InlineData(115000, 11)]
    [InlineData(999.99, 3)]
    [InlineData(250000, 1)]
    [InlineData(48750.25, 25)]
    public void Allocation_is_exact_without_vat(double target, int count)
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var lines = InvoiceAllocator.Allocate((decimal)target, count, Pool(), null, new Random(seed));
            Assert.Equal(count, lines.Count);
            Assert.Equal((long)Math.Round(target * 100), lines.Sum(l => l.AmountCents));
            Assert.All(lines, l => Assert.True(l.Quantity >= 1 && l.UnitCents >= 1));
            Assert.StartsWith("1- ", lines[0].Description);
        }
    }

    [Theory]
    [InlineData(255000, 11)]
    [InlineData(115000, 7)]
    [InlineData(1000, 2)]
    [InlineData(575, 1)]
    [InlineData(333333.33, 20)]
    public void Allocation_is_exact_with_line_vat(double target, int count)
    {
        for (int seed = 0; seed < 25; seed++)
        {
            var lines = InvoiceAllocator.Allocate((decimal)target, count, Pool(), 0.15m, new Random(seed));
            Assert.Equal((long)Math.Round(target * 100), InvoiceAllocator.GrossCents(lines, 0.15m));
        }
    }

    [Fact]
    public void Category_with_fewer_items_than_requested_repeats_items()
    {
        var lines = InvoiceAllocator.Allocate(5000m, 6, Pool(2), null, new Random(1));
        Assert.Equal(6, lines.Count);
    }

    // ------------------------------------------------------------------ filling + archive
    private static string MakeInvoiceTemplate(string dir)
    {
        var path = Path.Combine(dir, "INVOICE.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("INVOICE");
        ws.Cell("B3").Value = "OLD SUPPLIER"; ws.Cell("B3").Style.Font.FontSize = 20;
        ws.Cell("B4").Value = "पुराना नाम"; ws.Cell("B4").Style.Font.FontSize = 14;
        ws.Cell("E3").Value = "INVOICE"; ws.Cell("E3").Style.Font.FontSize = 40;
        ws.Cell("E5").Value = "INVOICE NO: "; ws.Cell("F5").Value = "Mad-1111111";
        ws.Cell("E7").Value = "ISSUE DATE: "; ws.Cell("F7").Value = new DateTime(2026, 1, 1);
        ws.Cell("B10").Value = "Old Category | تصنيف قديم";
        string[] h = ["Code", "DESCRIPTION", "QTY", "Price", "TOTAL AMOUNT"];
        for (int i = 0; i < h.Length; i++) ws.Cell(11, 2 + i).Value = h[i];
        for (int r = 13; r <= 26; r++)
        {
            ws.Cell(r, 6).FormulaA1 = $"D{r}*E{r}";
            ws.Range(r, 2, r, 6).Style.Fill.BackgroundColor = r % 2 == 1 ? XLColor.LightGray : XLColor.White;
        }
        ws.Cell("E28").Value = "TOTAL VALUE"; ws.Cell("F28").Value = 30000;
        ws.Cell("B28").FormulaA1 = "[1]!SAPAmountWords_USD(F28)";
        ws.Cell("B31").Value = "PAYMENT & BANKING INSTRUCTIONS";
        ws.Cell("B33").Value = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: OLD";
        ws.Cell("B35").Value = "Company Address:\nOld street";
        ws.Range("B33:F33").Merge(); ws.Range("B35:F35").Merge();
        ws.PageSetup.PrintAreas.Add("B1:F35");
        wb.SaveAs(path);
        return path;
    }

    private static string MakeMadarTemplate(string dir)
    {
        var path = Path.Combine(dir, "Madar.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Madar");
        ws.Cell("C8").Value = "Invoice NO: رقم الفاتورة"; ws.Cell("D8").Value = "Mad-4218627";
        ws.Cell("C10").Value = "ISSUE DATE: "; ws.Cell("E10").Value = new DateTime(2026, 11, 1); ws.Range("E10:F10").Merge();
        ws.Cell("C11").Value = "عميل قديم | Old client"; ws.Range("C11:G11").Merge(); ws.Cell("H11").Value = "السيد/السادة";
        ws.Cell("B13").Value = "Auto Parts | قطع غيار السيارات";
        string[] h = ["Item Code", "Descriptions", "QTY", "UNIT", "Unit Price ", "TOTAL AMOUNT"];
        for (int i = 0; i < h.Length; i++) ws.Cell(14, 2 + i).Value = h[i];
        for (int r = 16; r <= 26; r++) { ws.Cell(r, 7).FormulaA1 = $"D{r}*F{r}"; ws.Range(r, 7, r, 8).Merge(); }
        ws.Cell("E28").Value = "Subtotal (Excluding VAT)";
        ws.Cell("G28").FormulaA1 = "ROUND(SUM(G16:G26),2)";
        ws.Cell("G29").FormulaA1 = "SUMPRODUCT(ROUND(G16:G26*15%,2))";
        ws.Cell("B30").FormulaA1 = "[2]!SAPAmountWords(G30)";
        ws.Cell("G30").FormulaA1 = "SUM(G28,G29)";
        ws.Cell("B42").Value = "Headquarters Jeddah";
        ws.PageSetup.PrintAreas.Add("B1:H43");
        wb.SaveAs(path);
        return path;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(14)]
    [InlineData(20)]
    public void Invoice_template_is_filled_and_archived(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-inv-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "Test");
            var tpl = MakeInvoiceTemplate(DatabaseLayout.Templates(root));
            var map = TemplateDetector.DetectFile(tpl);
            Assert.Equal("B10", map.Fields[TemplateFields.Category].Cell);
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = "SUPPLIERS",
                PartyName = "ARVIND ENTERPRISES", NativeName = "अरविंद एंटरप्राइजेज",
                Address = "Company Address:\nJain House", Bank = "PAYMENT & BANKING INSTRUCTIONS\nBANK: YES\nSWIFT: X\nACC: 1",
                CategoryText = "Water Pumps & Motors | مضخات ومواتير المياه",
                InvoiceNo = "Mad-7654321", Date = new DateTime(2026, 11, 7), Target = 30000m,
                Lines = InvoiceAllocator.Allocate(30000m, count, Pool(), null, new Random(3)),
            };
            var path = InvoiceService.Archive(root, draft);
            Assert.Equal(Path.Combine(root, "BATCH STUDIO", "SUPPLIERS", "ARVIND ENTERPRISES", "Mad-7654321.xlsx"), path);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("INVOICE");
            int shift = count - 14;
            Assert.Equal("ARVIND ENTERPRISES", ws.Cell("B3").GetString());
            Assert.Equal("अरविंद एंटरप्राइजेज", ws.Cell("B4").GetString());
            Assert.Equal("Mad-7654321", ws.Cell("F5").GetString());
            Assert.Equal(new DateTime(2026, 11, 7), ws.Cell("F7").GetDateTime());
            Assert.Equal("Water Pumps & Motors | مضخات ومواتير المياه", ws.Cell("B10").GetString());
            Assert.Equal(30000d, ws.Cell(28 + shift, 6).GetDouble(), 2);
            Assert.StartsWith("(ثلاثون ألف دولار أمريكي فقط)", ws.Cell(28 + shift, 2).GetString());
            Assert.Contains("BANK: YES", ws.Cell(33 + shift, 2).GetString());
            Assert.Contains("Jain House", ws.Cell(35 + shift, 2).GetString());
            double sum = 0;
            for (int r = 13; r < 13 + count; r++) sum += ws.Cell(r, 6).GetDouble();
            Assert.Equal(30000d, sum, 2);
            Assert.StartsWith($"{count}- ", ws.Cell(12 + count, 3).GetString());
            Assert.DoesNotContain(ws.CellsUsed(), c => c.HasFormula);
            Assert.Equal(35 + shift, ws.PageSetup.PrintAreas.First().RangeAddress.LastAddress.RowNumber);

            var rec = Assert.Single(InvoiceHistoryBook.Read(DataSets.InvoiceHistory.FullPath(root)));
            Assert.Equal("INV-1", rec.No);
            Assert.Equal(path, rec.FilePath);
            Assert.Equal("SUPPLIERS", rec.PartyType);
            Assert.Equal(30000m, rec.Amount);

            // same number again is refused
            Assert.Throws<InvalidOperationException>(() => InvoiceService.Archive(root, draft));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Madar_template_gets_vat_totals_and_sar_words()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-mad-" + Guid.NewGuid().ToString("N"));
        try
        {
            DatabaseLayout.EnsureStructure(root, "Test");
            var tpl = MakeMadarTemplate(DatabaseLayout.Templates(root));
            var map = TemplateDetector.DetectFile(tpl);
            Assert.Equal("CLIENTS", map.EffectivePartyType);
            Assert.Equal("SAR", map.EffectiveCurrency);
            var draft = new InvoiceDraft
            {
                TemplatePath = tpl, Map = map, PartyType = map.EffectivePartyType,
                PartyName = "أركان التجريبية", CategoryText = "Auto Parts | قطع غيار السيارات",
                InvoiceNo = "Mad-1000001", Date = new DateTime(2026, 11, 7), Target = 115000m,
                Lines = InvoiceAllocator.Allocate(115000m, 7, Pool(), 0.15m, new Random(5)),
            };
            Assert.Equal(115000m, draft.Total);
            var path = InvoiceService.Archive(root, draft);
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Madar");
            int shift = 7 - 11;
            Assert.Equal("أركان التجريبية", ws.Cell("C11").GetString());
            Assert.Equal("PCS", ws.Cell("E16").GetString());
            Assert.Equal((double)draft.Net, ws.Cell(28 + shift, 7).GetDouble(), 2);
            Assert.Equal((double)draft.Vat, ws.Cell(29 + shift, 7).GetDouble(), 2);
            Assert.Equal(115000d, ws.Cell(30 + shift, 7).GetDouble(), 2);
            Assert.Contains("Total Amount Including VAT", ws.Cell(30 + shift, 2).GetString());
            Assert.Equal("Headquarters Jeddah", ws.Cell(42 + shift, 2).GetString());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
