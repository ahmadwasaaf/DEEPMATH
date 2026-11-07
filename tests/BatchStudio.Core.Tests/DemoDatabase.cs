using BatchStudio.Core.Data;
using ClosedXML.Excel;

namespace BatchStudio.Core.Tests;

/// <summary>Writes a demo Database (used by the CI screenshot smoke test) when BS_DEMO_DB is set.</summary>
public class DemoDatabase
{
    [Fact]
    public void Create_demo_database_when_requested()
    {
        var root = Environment.GetEnvironmentVariable("BS_DEMO_DB");
        if (string.IsNullOrEmpty(root)) return;
        DatabaseLayout.EnsureStructure(root, "Demo");

        var sup = ExcelTableStore.Load(DataSets.Suppliers.FullPath(root), "Suppliers");
        sup.Rows.Add(["SUP-001", "ARVIND ENTERPRISES", "अरविंद एंटरप्राइजेज", "Company Address:\nJain House, Kolkata", "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: TEST BANK", "Water Pumps & Motors | مضخات ومواتير المياه", "INVOICE", ""]);
        sup.Rows.Add(["SUP-002", "CHARMS IMPORT AND EXPORT CO., LIMITED", "查司進出口有限公司", "Company Address:\nHong Kong", "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: TEST", "Clothing & Apparel | الملابس", "INVOICE", ""]);
        sup.Rows.Add(["SUP-003", "COLOR LEAF TRADING CO., LIMITED", "彩色礼品贸易有限公司", "", "PAYMENT & BANKING INSTRUCTIONS", "Office & Stationery Supplies | القرطاسية", "INV1", ""]);
        ExcelTableStore.Save(DataSets.Suppliers.FullPath(root), "Suppliers", sup);

        var cli = ExcelTableStore.Load(DataSets.Clients.FullPath(root), "Clients");
        cli.Rows.Add(["CLI-001", "أركان التجريبية", "Auto Parts | قطع غيار السيارات", ""]);
        cli.Rows.Add(["CLI-002", "الجيلاني للخدمات اللوجستية", "Auto Parts | قطع غيار السيارات", ""]);
        ExcelTableStore.Save(DataSets.Clients.FullPath(root), "Clients", cli);

        // INVOICE-like template
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("INVOICE");
        ws.Cell("B3").Value = "ARVIND ENTERPRISES"; ws.Cell("B3").Style.Font.FontSize = 20;
        ws.Cell("B4").Value = "अरविंद एंटरप्राइजेज"; ws.Cell("B4").Style.Font.FontSize = 14;
        ws.Cell("E3").Value = "INVOICE"; ws.Cell("E3").Style.Font.FontSize = 40;
        ws.Cell("E5").Value = "INVOICE NO: "; ws.Cell("F5").Value = "Mad-5212411";
        ws.Cell("E7").Value = "ISSUE DATE: "; ws.Cell("F7").Value = new DateTime(2026, 11, 7);
        string[] h = ["Code", "DESCRIPTION", "QTY", "Price", "TOTAL AMOUNT"];
        for (int i = 0; i < h.Length; i++) ws.Cell(11, 2 + i).Value = h[i];
        for (int r = 13; r <= 26; r++) ws.Cell(r, 6).FormulaA1 = $"D{r}*E{r}";
        ws.Cell("E28").Value = "TOTAL VALUE"; ws.Cell("F28").FormulaA1 = "SUM(F13:F26)";
        ws.Cell("B28").Value = "(Thirty Thousand US Dollars Only)";
        ws.Cell("B31").Value = "PAYMENT & BANKING INSTRUCTIONS";
        ws.Cell("B33").Value = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: TEST BANK";
        ws.Cell("B35").Value = "Company Address:\nTest street 1";
        ws.Range("B33:F33").Merge(); ws.Range("B35:F35").Merge();
        ws.PageSetup.PrintAreas.Add("B1:F35");
        wb.SaveAs(Path.Combine(DatabaseLayout.Templates(root), "INVOICE.xlsx"));
    }
}
