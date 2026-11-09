using System.Globalization;
using BatchStudio.Core.Data;
using BatchStudio.Core.Workbook;

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

    // --------------------------------------------------------------- archive
    public static string SafeName(string s)
    {
        s = s.Trim();
        foreach (var ch in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) s = s.Replace(ch, '_');
        s = s.TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s.Length == 0 ? "Unnamed" : s;
    }

    /// <summary>&lt;SUPPLIERS|CLIENTS&gt;\&lt;party&gt;\&lt;invoice no&gt;.xlsx, relative to the archive (BATCH STUDIO) folder.</summary>
    public static string ArchiveRelativePath(InvoiceDraft d) =>
        Path.Combine(d.PartyType == "CLIENTS" ? DatabaseLayout.ArchiveClients : DatabaseLayout.ArchiveSuppliers,
                     SafeName(d.PartyName), SafeName(d.InvoiceNo) + ".xlsx");

    /// <summary>"HALIMA INTERNATIONAL TRADING LIMITED 30,000" (cents only when there are any): the PDF name of an invoice.</summary>
    public static string PdfBaseName(string party, decimal? amount)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(party) ? "Invoice" : party.Trim());
        if (amount is not { } a) return name;
        var text = a == decimal.Truncate(a) ? a.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture)
                                            : a.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
        return $"{name} {text}";
    }

    /// <summary>A free path on the Desktop for the invoice PDF ("name 30,000.pdf", then "name 30,000 (2).pdf" ...).</summary>
    public static string DesktopPdfPath(string party, decimal? amount)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        var baseName = PdfBaseName(party, amount);
        var path = Path.Combine(desktop, baseName + ".pdf");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(desktop, $"{baseName} ({i}).pdf");
        return path;
    }

    /// <summary>Most used invoice-number prefix in the history ("Mad" for Mad-1234567); "INV" when there is none.</summary>
    public static string DetectPrefix(IEnumerable<string> invoiceNumbers) =>
        invoiceNumbers.Select(n => n.Trim()).Where(n => n.Contains('-'))
            .Select(n => n[..n.LastIndexOf('-')]).Where(p => p.Length is > 0 and <= 12)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefault() ?? "INV";

    /// <summary>
    /// Writes the invoice file into the archive and adds its INVOICE HISTORY row in the workbook. Nothing is
    /// overwritten: an existing file or invoice number is an error. When the workbook cannot be changed the
    /// invoice file is removed again, so archive and history stay consistent.
    /// </summary>
    public static string Archive(Workspace ws, InvoiceDraft d, int keepBackups = 30)
    {
        if (string.IsNullOrWhiteSpace(d.PartyName)) throw new InvalidOperationException("Choose a supplier or client.");
        if (string.IsNullOrWhiteSpace(d.InvoiceNo)) throw new InvalidOperationException("Invoice number is empty.");
        ws.Refresh();
        if (File.Exists(ws.HistoryPath) && InvoiceHistoryBook.InvoiceNumbers(ws.HistoryPath).Contains(d.InvoiceNo))
            throw new InvalidOperationException($"Invoice number {d.InvoiceNo} already exists in INVOICE HISTORY.");

        var full = Path.Combine(ws.ArchiveRoot, ArchiveRelativePath(d));
        if (File.Exists(full)) throw new InvalidOperationException($"An archived invoice already exists:\n{full}");
        WriteInvoiceFile(d, full);
        try
        {
            ws.Change(ed =>
            {
                ws.SyncArchiveConfig(ed);
                InvoiceHistoryBook.Append(ed, NewRecord(d, full));
            }, keepBackups);
        }
        catch
        {
            try { File.Delete(full); } catch { }
            throw;
        }
        return full;
    }

    /// <summary>
    /// MASS INVOICE: writes many invoices, then records all of them in INVOICE HISTORY in one workbook change
    /// (one backup, one save). A failing invoice is reported and the others go on; when the history cannot be
    /// written, the files of this batch are removed again and every row is reported as failed.
    /// </summary>
    /// <param name="progress">Called after each invoice file: index, archived file (null on failure), error.</param>
    public static List<BatchOutcome> ArchiveBatch(Workspace ws, IReadOnlyList<InvoiceDraft> drafts, int keepBackups = 30,
        Action<int, string?, string?>? progress = null)
    {
        ws.Refresh();
        var used = File.Exists(ws.HistoryPath) ? InvoiceHistoryBook.InvoiceNumbers(ws.HistoryPath) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<BatchOutcome>();
        for (int i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var res = new BatchOutcome { Index = i, Draft = d };
            results.Add(res);
            try
            {
                if (string.IsNullOrWhiteSpace(d.PartyName)) throw new InvalidOperationException("Choose a supplier or client.");
                if (string.IsNullOrWhiteSpace(d.InvoiceNo)) throw new InvalidOperationException("Invoice number is empty.");
                if (used.Contains(d.InvoiceNo)) throw new InvalidOperationException($"Invoice number {d.InvoiceNo} already exists in INVOICE HISTORY.");
                var full = Path.Combine(ws.ArchiveRoot, ArchiveRelativePath(d));
                if (File.Exists(full)) throw new InvalidOperationException($"An archived invoice already exists:\n{full}");
                WriteInvoiceFile(d, full);
                used.Add(d.InvoiceNo);
                res.File = full;
            }
            catch (Exception ex) { res.Error = ex.Message; }
            progress?.Invoke(i, res.File, res.Error);
        }

        var written = results.Where(r => r.File != null).ToList();
        if (written.Count == 0) return results;
        try
        {
            ws.Change(ed =>
            {
                ws.SyncArchiveConfig(ed);
                foreach (var r in written) InvoiceHistoryBook.Append(ed, NewRecord(r.Draft, r.File!));
            }, keepBackups);
            foreach (var r in written) r.Recorded = true;
        }
        catch (Exception ex)
        {
            foreach (var r in written)
            {
                try { File.Delete(r.File!); } catch { }
                r.File = null;
                r.Error = ex is FileLockedException fl ? $"INVOICE HISTORY is locked ({Path.GetFileName(fl.FilePath)})." : ex.Message;
            }
        }
        return results;
    }

    /// <summary>
    /// Smart Invoice Repair: writes the repaired invoice over its archived file (the original is kept as a rollback
    /// copy until the history row is updated) and updates the same INVOICE HISTORY row. The invoice number cannot change.
    /// </summary>
    public static string Replace(Workspace ws, InvoiceDraft d, string invoiceNo, string archivedFile, int keepBackups = 30)
    {
        if (!string.Equals(d.InvoiceNo.Trim(), invoiceNo.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The invoice number changed; the archived invoice was not replaced.");
        var full = Path.GetFullPath(archivedFile);
        var archive = Path.GetFullPath(ws.ArchiveRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(archive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The archived invoice is outside the BATCH STUDIO archive; it was not replaced.");
        if (!File.Exists(full)) throw new FileNotFoundException("The archived invoice is missing; nothing was replaced.", full);

        var tmp = Path.Combine(Path.GetTempPath(), $"BS_{Guid.NewGuid():N}.xlsx");
        var rollback = full + $".BS_REPLACE_{DateTime.Now:yyyyMMddHHmmss}.bak";
        FileWriter(d, tmp);
        try
        {
            File.Move(full, rollback);
            File.Move(tmp, full);
            try
            {
                ws.Change(ed => InvoiceHistoryBook.UpdateRecord(ed, invoiceNo, d.Total, d.Date), keepBackups);
            }
            catch
            {
                File.Delete(full);
                File.Move(rollback, full);
                throw;
            }
            File.Delete(rollback);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (!File.Exists(full) && File.Exists(rollback)) File.Move(rollback, full);
        }
        return full;
    }

    /// <summary>Same as <see cref="Archive(Workspace, InvoiceDraft, int)"/> for a stand-alone Database folder (tests / old layout).</summary>
    public static string Archive(string root, InvoiceDraft d, int keepBackups = 30)
    {
        if (string.IsNullOrWhiteSpace(d.PartyName)) throw new InvalidOperationException("Choose a supplier or client.");
        if (string.IsNullOrWhiteSpace(d.InvoiceNo)) throw new InvalidOperationException("Invoice number is empty.");
        var historyPath = DataSets.InvoiceHistory.FullPath(root);
        if (!File.Exists(historyPath)) DatabaseLayout.CreateEmptyTable(historyPath, DataSets.InvoiceHistory);
        ExcelTableStore.EnsureWritable(historyPath);
        if (InvoiceHistoryBook.InvoiceNumbers(historyPath).Contains(d.InvoiceNo))
            throw new InvalidOperationException($"Invoice number {d.InvoiceNo} already exists in INVOICE HISTORY.");

        var full = Path.Combine(root, DatabaseLayout.ArchiveFolder, ArchiveRelativePath(d));
        if (File.Exists(full)) throw new InvalidOperationException($"An archived invoice already exists:\n{full}");
        WriteInvoiceFile(d, full);
        try
        {
            InvoiceHistoryBook.Append(root, historyPath, NewRecord(d, full), keepBackups);
        }
        catch
        {
            try { File.Delete(full); } catch { }
            throw;
        }
        return full;
    }

    /// <summary>Writes the invoice file. The app replaces this with Microsoft Excel (exact template formatting).</summary>
    public static Action<InvoiceDraft, string> FileWriter { get; set; } = TemplateFiller.Fill;

    private static void WriteInvoiceFile(InvoiceDraft d, string full)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"BS_{Guid.NewGuid():N}.xlsx");
        try
        {
            FileWriter(d, tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static HistoryRecord NewRecord(InvoiceDraft d, string full) => new()
    {
        Name = d.PartyName,
        Amount = d.Total,
        Date = d.Date,
        InvoiceNo = d.InvoiceNo,
        Status = "File available",
        PartyType = d.PartyType,
        RecordId = $"{DateTime.Now:yyyyMMddHHmmss}-{Random.Shared.Next(0, 1_000_000):000000}",
        FilePath = full,                       // absolute, like the V28 archive
        Template = Path.GetFileNameWithoutExtension(d.TemplatePath),
    };

    /// <summary>Resolves a history "File Path" (relative to the archive, or an old absolute path).</summary>
    public static string ResolveArchivePath(string archiveRoot, string stored) => InvoiceHistoryBook.ResolvePath(archiveRoot, stored);
}

/// <summary>Result of one invoice of a MASS INVOICE run.</summary>
public sealed class BatchOutcome
{
    public int Index { get; init; }
    public required InvoiceDraft Draft { get; init; }
    public string? File { get; set; }
    public string? Error { get; set; }
    /// <summary>True when the invoice is archived and recorded in INVOICE HISTORY.</summary>
    public bool Recorded { get; set; }
}
