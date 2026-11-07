using System.Globalization;
using BatchStudio.Core.Data;

namespace BatchStudio.Core.Invoices;

/// <summary>A supplier or client as needed on an invoice.</summary>
public sealed record Party(string Type, string Id, string Name, string? NativeName, string? Address, string? Bank,
    string? CategoryText, string? DefaultTemplate)
{
    public override string ToString() => Name;
}

public sealed record CategoryInfo(string Id, string Text, bool Active)
{
    public override string ToString() => Text;
}

/// <summary>Reads the master data needed to create invoices and archives finished invoices.</summary>
public static class InvoiceService
{
    // --------------------------------------------------------------- master data
    private static int Col(TableData t, params string[] names)
    {
        foreach (var n in names)
        {
            int i = t.Headers.FindIndex(h => string.Equals(h.Trim(), n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        foreach (var n in names)
        {
            int i = t.Headers.FindIndex(h => h.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string? Get(string[] row, int i) => i >= 0 && i < row.Length && row[i].Length > 0 ? row[i] : null;

    public static List<Party> Suppliers(TableData t)
    {
        int id = 0, name = Col(t, "Supplier Name (English)", "Supplier Name", "Name"), native = Col(t, "Chinese Name", "Native Name"),
            addr = Col(t, "Company Address", "Address"), bank = Col(t, "Banking Details (full paragraph)", "Banking", "Bank"),
            cat = Col(t, "Category (English | Arabic)", "Category"), tpl = Col(t, "DEFAULT TEMPLATE", "Template");
        return t.Rows.Where(r => Get(r, name) != null)
            .Select(r => new Party("SUPPLIERS", Get(r, id) ?? "", Get(r, name)!, Get(r, native), Get(r, addr), Get(r, bank), Get(r, cat), Get(r, tpl)))
            .ToList();
    }

    public static List<Party> Clients(TableData t)
    {
        int id = 0, name = Col(t, "Client Name", "Name"), cat = Col(t, "Client Category", "Category"), tpl = Col(t, "DEFAULT TEMPLATE", "Template");
        return t.Rows.Where(r => Get(r, name) != null)
            .Select(r => new Party("CLIENTS", Get(r, id) ?? "", Get(r, name)!, null, null, null, Get(r, cat), Get(r, tpl)))
            .ToList();
    }

    public static List<CategoryInfo> Categories(TableData t)
    {
        int id = 0, text = Col(t, "Category (English | Arabic)", "Category"), status = Col(t, "Status");
        return t.Rows.Where(r => Get(r, id) != null && Get(r, text) != null)
            .Select(r => new CategoryInfo(Get(r, id)!, Get(r, text)!, !string.Equals(Get(r, status), "Inactive", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Active items of a category (Items: Code, Item Name, Approx. Price, Category ID, Status).</summary>
    public static List<CatalogItem> Items(TableData t, string categoryId)
    {
        int code = 0, name = Col(t, "Item Name", "Name"), price = Col(t, "Approx. Price", "Price"), cat = Col(t, "Category ID", "Category"), status = Col(t, "Status");
        var list = new List<CatalogItem>();
        foreach (var r in t.Rows)
        {
            if (!string.Equals(Get(r, cat), categoryId, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Get(r, status), "Inactive", StringComparison.OrdinalIgnoreCase)) continue;
            if (Get(r, code) is not { } c || Get(r, name) is not { } n) continue;
            if (!decimal.TryParse(Get(r, price), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p <= 0) continue;
            list.Add(new CatalogItem(c, n, p, categoryId));
        }
        return list;
    }

    /// <summary>Category of a party: matches the stored "English | Arabic" text (or an ID) against Categories.</summary>
    public static CategoryInfo? CategoryFor(Party party, IReadOnlyList<CategoryInfo> categories)
    {
        var key = party.CategoryText?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        return categories.FirstOrDefault(c => string.Equals(c.Text.Trim(), key, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault(c => string.Equals(c.Text.Split('|')[0].Trim(), key.Split('|')[0].Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // --------------------------------------------------------------- invoice numbers
    public static string NewInvoiceNumber(string prefix, ISet<string> existing, Random? rng = null)
    {
        rng ??= Random.Shared;
        for (int i = 0; i < 1000; i++)
        {
            var no = $"{prefix}-{rng.Next(1_000_000, 10_000_000)}";
            if (!existing.Contains(no)) return no;
        }
        throw new InvalidOperationException("Could not find a free invoice number.");
    }

    public static HashSet<string> ExistingInvoiceNumbers(TableData history)
    {
        int c = Col(history, "Invoice No", "INVOICE NO");
        return new HashSet<string>(history.Rows.Select(r => Get(r, c)).Where(v => v != null)!, StringComparer.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------------- archive
    public static string SafeName(string s)
    {
        s = s.Trim();
        foreach (var ch in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) s = s.Replace(ch, '_');
        s = s.TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s.Length == 0 ? "Unnamed" : s;
    }

    /// <summary>BATCH STUDIO\&lt;SUPPLIERS|CLIENTS&gt;\&lt;party&gt;\&lt;invoice no&gt;.xlsx, relative to the Database folder.</summary>
    public static string ArchiveRelativePath(InvoiceDraft d) =>
        Path.Combine(DatabaseLayout.ArchiveFolder, d.PartyType == "CLIENTS" ? DatabaseLayout.ArchiveClients : DatabaseLayout.ArchiveSuppliers,
                     SafeName(d.PartyName), SafeName(d.InvoiceNo) + ".xlsx");

    /// <summary>
    /// Writes the invoice file and adds its INVOICE HISTORY row. Nothing is overwritten:
    /// an existing file or invoice number is an error. The history file is checked first so
    /// we never create an invoice file without its history record.
    /// </summary>
    public static string Archive(string root, InvoiceDraft d, int keepBackups = 30)
    {
        if (string.IsNullOrWhiteSpace(d.PartyName)) throw new InvalidOperationException("Choose a supplier or client.");
        if (string.IsNullOrWhiteSpace(d.InvoiceNo)) throw new InvalidOperationException("Invoice number is empty.");
        var historyPath = DataSets.InvoiceHistory.FullPath(root);
        if (!File.Exists(historyPath)) DatabaseLayout.CreateEmptyTable(historyPath, DataSets.InvoiceHistory);
        ExcelTableStore.EnsureWritable(historyPath);

        var history = ExcelTableStore.Load(historyPath, DataSets.InvoiceHistory.SheetName);
        if (ExistingInvoiceNumbers(history).Contains(d.InvoiceNo))
            throw new InvalidOperationException($"Invoice number {d.InvoiceNo} already exists in INVOICE HISTORY.");

        var rel = ArchiveRelativePath(d);
        var full = Path.Combine(root, rel);
        if (File.Exists(full)) throw new InvalidOperationException($"An archived invoice already exists:\n{full}");

        var tmp = Path.Combine(Path.GetTempPath(), $"BS_{Guid.NewGuid():N}.xlsx");
        try
        {
            TemplateFiller.Fill(d, tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        try
        {
            AppendHistory(root, history, d, rel, keepBackups);
        }
        catch
        {
            // keep archive and history consistent: no history row -> no invoice file
            try { File.Delete(full); } catch { }
            throw;
        }
        return full;
    }

    private static void AppendHistory(string root, TableData history, InvoiceDraft d, string relPath, int keepBackups)
    {
        foreach (var h in DataSets.InvoiceHistory.DefaultHeaders)
            if (!history.Headers.Contains(h, StringComparer.OrdinalIgnoreCase))
            {
                history.Headers.Add(h);
                history.Kinds.Add(ColumnKind.Text);
                for (int i = 0; i < history.Rows.Count; i++)
                    history.Rows[i] = [.. history.Rows[i], ""];
            }
        int Idx(string h) => history.Headers.FindIndex(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase));
        int maxNo = history.Rows.Select(r => r[Idx("#")])
            .Select(v => v.StartsWith("INV-", StringComparison.OrdinalIgnoreCase) && int.TryParse(v[4..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        var row = Enumerable.Repeat("", history.Headers.Count).ToArray();
        row[Idx("#")] = $"INV-{maxNo + 1}";
        row[Idx("Name")] = d.PartyName;
        row[Idx("Amount")] = d.Total.ToString(CultureInfo.InvariantCulture);
        row[Idx("Date")] = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        row[Idx("Invoice No")] = d.InvoiceNo;
        row[Idx("Status")] = "File available";
        row[Idx("Party Type")] = d.PartyType;
        row[Idx("Record ID")] = $"{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(0, 1_000_000):000000}";
        row[Idx("File Path")] = relPath;
        row[Idx("Template")] = Path.GetFileNameWithoutExtension(d.TemplatePath);
        history.Rows.Add(row);
        int a = Idx("Amount"), dt = Idx("Date");
        while (history.Kinds.Count < history.Headers.Count) history.Kinds.Add(ColumnKind.Text);
        history.Kinds[a] = ColumnKind.Number;
        history.Kinds[dt] = ColumnKind.Date;

        BackupService.Backup(root, DataSets.InvoiceHistory, keepBackups);
        ExcelTableStore.Save(DataSets.InvoiceHistory.FullPath(root), DataSets.InvoiceHistory.SheetName, history);
    }

    /// <summary>Resolves a history "File Path" (relative to the Database folder, or an old absolute path).</summary>
    public static string ResolveArchivePath(string root, string stored) =>
        Path.IsPathRooted(stored) ? stored : Path.Combine(root, stored);
}
