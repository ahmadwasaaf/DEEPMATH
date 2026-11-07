namespace BatchStudio.Core.Invoices;

/// <summary>A product that can appear on an invoice (from Items.xlsx).</summary>
public sealed record CatalogItem(string Code, string Name, decimal ApproxPrice, string CategoryId);

/// <summary>One invoice line. Amounts are whole cents so totals are exact.</summary>
public sealed class InvoiceLine
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public long Quantity { get; set; }
    public long UnitCents { get; set; }
    public decimal ReferencePrice { get; set; }
    public long AmountCents => Quantity * UnitCents;
    public decimal UnitPrice => UnitCents / 100m;
    public decimal Amount => AmountCents / 100m;
}

public sealed class AllocationException(string message) : Exception(message);

/// <summary>
/// Picks random items and distributes quantities / unit prices so the invoice total equals the target exactly.
/// Non-VAT: port of InvoiceEngine.GenerateInvoice (prices 85-115% of reference, last line 70-130%).
/// VAT: target is the gross amount; net = gross / (1 + rate) and VAT is rounded per line
/// (ROUND(line*rate, 2)) as in MadarEngine, then the last two lines are tuned so the gross matches to the cent.
/// </summary>
public static class InvoiceAllocator
{
    public const int MaxItems = 100;
    private const int MaxAttempts = 3000;

    public static List<InvoiceLine> Allocate(decimal target, int count, IReadOnlyList<CatalogItem> pool,
        decimal? vatRate = null, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (count < 1 || count > MaxItems) throw new AllocationException($"Item count must be between 1 and {MaxItems}.");
        if (target <= 0 || target > 100_000_000m) throw new AllocationException("Amount must be between 0 and 100,000,000.");
        if (decimal.Round(target, 2) != target) throw new AllocationException("Amount must have at most 2 decimals.");
        var usable = pool.Where(p => p.ApproxPrice > 0 && !string.IsNullOrWhiteSpace(p.Code)).ToList();
        if (usable.Count == 0) throw new AllocationException("The selected category has no active items with a price.");

        long grossCents = (long)(target * 100m);
        long netCents = vatRate is { } r && r > 0 ? (long)Math.Round(grossCents / (1m + r), 0, MidpointRounding.AwayFromZero) : grossCents;
        if (netCents < count) throw new AllocationException("Amount too small for the requested number of items.");

        for (int round = 0; round < 40; round++)
        {
            var picked = Pick(usable, count, rng);
            var lines = AllocateNet(netCents, picked, rng);
            if (lines == null) continue;
            if (vatRate is { } rate && rate > 0)
            {
                if (!FitVat(lines, grossCents, rate, rng)) continue;
                if (GrossCents(lines, rate) != grossCents) continue;
            }
            else if (lines.Sum(l => l.AmountCents) != grossCents) continue;
            for (int i = 0; i < lines.Count; i++) lines[i].Description = $"{i + 1}- {lines[i].Description}";
            return lines;
        }
        throw new AllocationException("No exact allocation within price limits. Try another amount or item count.");
    }

    /// <summary>Random distinct items; repeats only when the category has fewer items than requested.</summary>
    private static List<CatalogItem> Pick(List<CatalogItem> pool, int count, Random rng)
    {
        var ids = Enumerable.Range(0, pool.Count).ToArray();
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        return Enumerable.Range(0, count).Select(i => pool[ids[i % ids.Length]]).ToList();
    }

    private static List<InvoiceLine>? AllocateNet(long targetCents, List<CatalogItem> items, Random rng)
    {
        int n = items.Count;
        var refCents = items.Select(i => Math.Max(1L, (long)Math.Round(i.ApproxPrice * 100m, 0, MidpointRounding.AwayFromZero))).ToArray();
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            long remaining = targetCents;
            double budget = (double)targetCents / n;
            var qty = new long[n];
            var price = new long[n];
            bool ok = true;
            for (int i = 0; i < n - 1; i++)
            {
                long p = Math.Max(1, (long)Math.Round(refCents[i] * (0.85 + rng.NextDouble() * 0.3)));
                long q = Math.Max(1, (long)(budget / p * (0.45 + rng.NextDouble() * 0.85)));
                if (q * p >= remaining) q = 1;
                if (q * p >= remaining) { ok = false; break; }
                qty[i] = q;
                price[i] = p;
                remaining -= q * p;
            }
            if (!ok || remaining <= 0) continue;

            // last line: quantity near remaining / reference with an exact unit price 70-130% of reference
            long last = refCents[n - 1];
            long lastQty = Math.Max(1, remaining / last);
            bool found = false;
            for (long k = Math.Max(1, lastQty - 8); k <= lastQty + 8; k++)
            {
                if (remaining % k != 0) continue;
                long lp = remaining / k;
                double ratio = (double)lp / last;
                if (ratio is >= 0.7 and <= 1.3)
                {
                    qty[n - 1] = k;
                    price[n - 1] = lp;
                    found = true;
                    break;
                }
            }
            if (!found) continue;
            return Enumerable.Range(0, n).Select(i => new InvoiceLine
            {
                Code = items[i].Code,
                Description = items[i].Name,
                Quantity = qty[i],
                UnitCents = price[i],
                ReferencePrice = items[i].ApproxPrice,
            }).ToList();
        }
        return null;
    }

    /// <summary>VAT of one line in cents: ROUND(amount * rate, 2), half away from zero (Excel ROUND).</summary>
    public static long VatCents(long amountCents, decimal rate) =>
        (long)Math.Round(amountCents * rate, 0, MidpointRounding.AwayFromZero);

    /// <summary>True when amount*rate lands exactly on half a cent (Excel's binary math could round it either way).</summary>
    private static bool IsMidpoint(long amountCents, decimal rate) => (amountCents * rate) % 1m == 0.5m;

    public static long GrossCents(IEnumerable<InvoiceLine> lines, decimal rate) =>
        lines.Sum(l => l.AmountCents + VatCents(l.AmountCents, rate));

    /// <summary>Adjusts the last two lines so Σ(line + ROUND(line*rate)) equals the gross target.</summary>
    private static bool FitVat(List<InvoiceLine> lines, long grossCents, decimal rate, Random rng)
    {
        if (lines.Count == 1)
            return FitLast(lines[0], grossCents, rate);
        var pen = lines[^2];
        var last = lines[^1];
        long fixedGross = GrossCents(lines.Take(lines.Count - 2), rate);
        long penPrice = pen.UnitCents;
        for (int attempt = 0; attempt <= 4000; attempt++)
        {
            long delta = attempt == 0 ? 0 : (attempt % 2 == 1 ? (attempt + 1) / 2 : -(attempt / 2));
            long p = penPrice + delta;
            if (p < 1) continue;
            pen.UnitCents = p;
            long penAmount = pen.AmountCents;
            if (IsMidpoint(penAmount, rate)) continue;
            long required = grossCents - fixedGross - penAmount - VatCents(penAmount, rate);
            if (required <= 0) continue;
            if (FitLast(last, required, rate)) return true;
        }
        pen.UnitCents = penPrice;
        return false;
    }

    /// <summary>Finds qty × unit price for a line whose gross (amount + VAT) is exactly <paramref name="requiredGross"/>.</summary>
    private static bool FitLast(InvoiceLine line, long requiredGross, decimal rate)
    {
        long approx = (long)Math.Round(requiredGross / (1m + rate), 0, MidpointRounding.AwayFromZero);
        long refCents = Math.Max(1, (long)Math.Round(line.ReferencePrice * 100m, 0, MidpointRounding.AwayFromZero));
        for (long amount = approx - 3; amount <= approx + 3; amount++)
        {
            if (amount <= 0 || IsMidpoint(amount, rate)) continue;
            if (amount + VatCents(amount, rate) != requiredGross) continue;
            // choose the quantity whose unit price is closest to the reference price
            long bestQ = 1;
            double bestScore = double.MaxValue;
            long maxQ = Math.Min(5000, amount);
            for (long q = 1; q <= maxQ; q++)
            {
                if (amount % q != 0) continue;
                double score = Math.Abs(Math.Log((double)(amount / q) / refCents));
                if (q == 1) score += 0.15;
                if (score < bestScore) { bestScore = score; bestQ = q; }
            }
            line.Quantity = bestQ;
            line.UnitCents = amount / bestQ;
            return true;
        }
        return false;
    }
}
