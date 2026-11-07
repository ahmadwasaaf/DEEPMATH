using BatchStudio.Core.Data;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Invoices;
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

        // the real V28 history sheet (sanitized) and the three V28 templates, linked
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "History_V28.xlsx"), DataSets.InvoiceHistory.FullPath(root), true);
        foreach (var t in new[] { "INVOICE", "INV1", "Madar" })
        {
            var dst = TemplateRegistry.Import(root, Path.Combine(AppContext.BaseDirectory, "Fixtures", t + ".xlsx"), overwrite: true);
            var map = TemplateDetector.DetectFile(dst);
            foreach (var f in map.Fields.Values) f.Confirmed = f.Cell != null;
            map.Save(dst);
        }
        // a few archived invoices so some history rows show "File available"
        foreach (var r in InvoiceHistoryBook.Read(DataSets.InvoiceHistory.FullPath(root)).Take(5))
        {
            var f = InvoiceHistoryBook.ResolvePath(root, r.FilePath);
            var local = Path.Combine(root, "BATCH STUDIO", r.PartyType, "Party", r.InvoiceNo + ".xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "INVOICE.xlsx"), local, true);
        }
    }
}
