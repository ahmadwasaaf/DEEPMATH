using System.Globalization;
using BatchStudio.Core.Data;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Workbook;
using ClosedXML.Excel;

namespace BatchStudio.Core.Invoices;

/// <summary>An archived invoice read back for Smart Invoice Repair.</summary>
public sealed class RepairSource
{
    public required string ArchivePath { get; init; }
    public required string InvoiceNo { get; init; }
    public string PartyName { get; init; } = "";
    public string? NativeName { get; init; }
    public string? Address { get; init; }
    public string? Bank { get; init; }
    public string? CategoryText { get; init; }
    public DateTime? Date { get; init; }
    /// <summary>Final total written on the archived invoice (gross incl. VAT where the template has VAT).</summary>
    public decimal Total { get; init; }
    /// <summary>The archived item rows; they stay unchanged during the repair.</summary>
    public List<InvoiceLine> Lines { get; init; } = [];
}

/// <summary>Result of extending a restored invoice.</summary>
public sealed record RepairPlan(List<InvoiceLine> Lines, int RequestedCount, int FinalCount)
{
    public bool Expanded => FinalCount > RequestedCount;
}

/// <summary>
/// Smart Invoice Repair (same rules as SmartInvoiceRepair.bas in the workbook, for INVOICE, INV1 and Madar):
/// the archived invoice is restored, its item rows are protected, and only NEW unique items (same category,
/// not already on the invoice, realistic prices) are added for the difference so the total matches to the cent.
/// Saving replaces the same archived file and updates the same INVOICE HISTORY row.
/// </summary>
public static class InvoiceRepair
{
    public static RepairSource Read(string archivePath, TemplateMap map, string expectedInvoiceNo)
    {
        var items = map.Items ?? throw new InvalidOperationException("The template's items table was not detected. Open Templates and detect again.");
        using var wb = PackageRepair.OpenWorkbook(archivePath);
        var ws = wb.Worksheets.First(w => w.Visibility == XLWorksheetVisibility.Visible);

        int Col(string key) => items.Columns.TryGetValue(key, out var letter) ? XLHelper.GetColumnNumberFromLetter(letter) : 0;
        int cCode = Col("code"), cDesc = Col("description"), cQty = Col("qty"), cPrice = Col("price");
        if (cQty == 0 || cPrice == 0) throw new InvalidOperationException("The template has no QTY / price columns.");

        var lines = new List<InvoiceLine>();
        for (int r = items.FirstRow; r < items.FirstRow + InvoiceAllocator.MaxItems; r++)
        {
            if (!TryNumber(ws.Cell(r, cQty), out var q) || !TryNumber(ws.Cell(r, cPrice), out var p)) break;
            if (q < 1 || q != Math.Floor(q) || p < 0) break;
            var cents = Math.Round(p * 100, 0);
            if (Math.Abs(p * 100 - cents) > 0.00001) break;
            lines.Add(new InvoiceLine
            {
                Code = cCode > 0 ? ExcelTableStore.CellText(ws.Cell(r, cCode)).Trim() : "",
                Description = cDesc > 0 ? ExcelTableStore.CellText(ws.Cell(r, cDesc)).Trim() : "",
                Quantity = (long)q,
                UnitCents = (long)cents,
                ReferencePrice = (decimal)p,
            });
        }
        if (lines.Count == 0) throw new InvalidOperationException("No valid item rows were found in the archived invoice.");

        int delta = lines.Count - items.Capacity;
        string? Cell(string field)
        {
            if (!map.Fields.TryGetValue(field, out var m) || m.Cell == null || !TemplateDetector.TryParseRef(m.Cell, out var r, out var c)) return null;
            return TemplateDetector.Ref(r > items.LastRow ? r + delta : r, c);
        }
        string? Text(string field) => Cell(field) is { } a ? NullIfEmpty(ExcelTableStore.CellText(ws.Cell(a)).Trim()) : null;

        var invoiceNo = Text(TemplateFields.InvoiceNo) ?? "";
        if (!string.Equals(invoiceNo, expectedInvoiceNo.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invoice number does not match the history record ({invoiceNo} ≠ {expectedInvoiceNo}).");

        decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
        decimal computed = Value(lines, vat) / 100m;
        decimal total = computed;
        if (Cell(TemplateFields.Total) is { } t && TryNumber(ws.Cell(t), out var tv) && tv > 0) total = Math.Round((decimal)tv, 2);
        if (Value(lines, vat) > (long)(total * 100m))
            throw new InvalidOperationException("The archived items already exceed the invoice total; manual review required.");

        DateTime? date = null;
        if (Cell(TemplateFields.IssueDate) is { } dcell)
        {
            var v = ws.Cell(dcell).CachedValueOrValue();
            if (v.IsDateTime) date = v.GetDateTime();
            else if (v.IsNumber && v.GetNumber() is > 0 and < 2958466) date = DateTime.FromOADate(v.GetNumber());
            else if (DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd)) date = pd;
        }

        return new RepairSource
        {
            ArchivePath = archivePath,
            InvoiceNo = invoiceNo,
            PartyName = Text(TemplateFields.PartyName) ?? "",
            NativeName = Text(TemplateFields.PartyNativeName),
            Address = Text(TemplateFields.Address),
            Bank = Text(TemplateFields.Bank),
            CategoryText = Text(TemplateFields.Category),
            Date = date,
            Total = total,
            Lines = lines,
        };
    }

    /// <summary>Value of lines in cents: net, or gross with VAT rounded per line.</summary>
    public static long Value(IEnumerable<InvoiceLine> lines, decimal? vatRate) =>
        vatRate is { } r && r > 0 ? lines.Sum(l => l.AmountCents + InvoiceAllocator.VatCents(l.AmountCents, r)) : lines.Sum(l => l.AmountCents);

    /// <summary>
    /// Keeps the restored lines and adds new ones for <paramref name="newTarget"/>. When the requested count cannot
    /// reach the total exactly, a larger count is searched (Smart Auto Expand) - check <see cref="RepairPlan.Expanded"/>.
    /// </summary>
    public static RepairPlan Extend(IReadOnlyList<InvoiceLine> existing, decimal newTarget, int requestedCount,
        IReadOnlyList<CatalogItem> categoryPool, decimal? vatRate, Random? rng = null)
    {
        rng ??= Random.Shared;
        int oldCount = existing.Count;
        if (decimal.Round(newTarget, 2) != newTarget || newTarget <= 0) throw new AllocationException("Amount must be positive with at most 2 decimals.");
        if (requestedCount < oldCount) throw new AllocationException($"The item count cannot be lower than the {oldCount} protected items.");
        if (requestedCount > InvoiceAllocator.MaxItems) throw new AllocationException($"Item count must be between 1 and {InvoiceAllocator.MaxItems}.");
        long remaining = (long)(newTarget * 100m) - Value(existing, vatRate);
        if (remaining == 0 && requestedCount == oldCount) return new RepairPlan(existing.Select(Copy).ToList(), oldCount, oldCount);
        if (remaining <= 0) throw new AllocationException("The new total must be higher than the protected items.");
        if (requestedCount == oldCount) throw new AllocationException("Increase the number of items to add the difference.");

        var used = existing.Select(l => l.Code).Where(c => c.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pool = categoryPool.Where(p => !used.Contains(p.Code) && p.ApproxPrice > 0)
                               .GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        int wanted = requestedCount - oldCount;
        if (pool.Count < wanted)
            throw new AllocationException($"Only {pool.Count} unused products are available in this category; {wanted} were requested.");
        int maxNew = (int)Math.Min(Math.Min(pool.Count, InvoiceAllocator.MaxItems - oldCount), remaining);
        for (int addN = wanted; addN <= maxNew; addN++)
        {
            List<InvoiceLine> added;
            try { added = InvoiceAllocator.Allocate(remaining / 100m, addN, pool, vatRate, rng); }
            catch (AllocationException) { continue; }
            if (added.Select(l => l.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != added.Count) continue;   // unique products only
            if (Value(added, vatRate) != remaining) continue;
            for (int i = 0; i < added.Count; i++)
            {
                var d = added[i].Description;
                int dash = d.IndexOf("- ", StringComparison.Ordinal);
                if (dash > 0 && int.TryParse(d[..dash], out _)) d = d[(dash + 2)..];
                added[i].Description = $"{oldCount + i + 1}- {d}";
            }
            return new RepairPlan([.. existing.Select(Copy), .. added], requestedCount, oldCount + addN);
        }
        throw new AllocationException($"No exact allocation with realistic item prices was found (up to {maxNew} new items). Nothing was changed.");
    }

    private static InvoiceLine Copy(InvoiceLine l) => new()
    {
        Code = l.Code, Description = l.Description, Quantity = l.Quantity, UnitCents = l.UnitCents, ReferencePrice = l.ReferencePrice,
    };

    private static bool TryNumber(IXLCell cell, out double value)
    {
        value = 0;
        var v = cell.CachedValueOrValue();
        if (v.IsNumber) { value = v.GetNumber(); return true; }
        if (v.IsText && double.TryParse(v.GetText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    private static XLCellValue CachedValueOrValue(this IXLCell c) => c.HasFormula ? c.CachedValue : c.Value;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
