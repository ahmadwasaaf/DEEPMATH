using BatchStudio.Core.Invoices;
using BatchStudio.Core.Workbook;

namespace BatchStudio.Core.Tests;

/// <summary>
/// Writes a demo entity folder (used by the CI screenshot smoke test) when BS_DEMO_DB is set:
/// the sanitized V28.1 workbook plus a BATCH STUDIO archive with a few invoice files.
/// </summary>
public class DemoDatabase
{
    [Fact]
    public void Create_demo_database_when_requested()
    {
        var root = Environment.GetEnvironmentVariable("BS_DEMO_DB");
        if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root);
        var book = Path.Combine(root, "BATCH_STUDIO_V28.1.xlsm");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Demo_Master.xlsm"), book, true);
        var archive = Path.Combine(root, "BATCH STUDIO");
        Directory.CreateDirectory(Path.Combine(archive, "SUPPLIERS"));
        Directory.CreateDirectory(Path.Combine(archive, "CLIENTS"));

        // the same cache folder the app will use, so templates are already linked
        var ws = Workspace.Open(book);
        Assert.Equal(archive, ws.ArchiveRoot);

        // a few archived invoices so some history rows show "File available"
        foreach (var r in InvoiceHistoryBook.Read(ws.HistoryPath).Take(12))
        {
            var norm = r.FilePath.Replace('/', '\\');
            int pos = Math.Max(norm.LastIndexOf(@"\CLIENTS\", StringComparison.OrdinalIgnoreCase), norm.LastIndexOf(@"\SUPPLIERS\", StringComparison.OrdinalIgnoreCase));
            if (pos < 0) continue;
            var local = Path.Combine(archive, norm[(pos + 1)..]);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "INVOICE.xlsx"), local, true);
        }
    }
}
