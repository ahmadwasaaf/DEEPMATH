using System.Globalization;

namespace BatchStudio.Core.Invoices;

/// <summary>
/// The items do not add up to the amount the user entered (TOTAL VALUE): the invoice is stopped, never written.
/// <see cref="Describe"/> lets the app word it in the user's language.
/// </summary>
public sealed class InvoiceTotalMismatchException(decimal entered, decimal items, string where) : Exception
{
    public decimal Entered { get; } = entered;
    public decimal Items { get; } = items;
    public decimal Difference => Items - Entered;
    /// <summary>"draft" (the generated items) or "sheet" (the invoice as Excel calculated it).</summary>
    public string Where { get; } = where;

    /// <summary>(entered, items, difference, where) → the message shown to the user.</summary>
    public static Func<decimal, decimal, decimal, string, string>? Describe { get; set; }

    public override string Message => Describe?.Invoke(Entered, Items, Difference, Where)
        ?? string.Format(CultureInfo.InvariantCulture,
            "The items total {0:N2} does not equal the invoice total {1:N2} (difference {2:N2}). The invoice was stopped.",
            Items, Entered, Difference);
}

/// <summary>
/// Safety: an invoice is written only when its items add up exactly to the amount the user entered.
/// Non-VAT: the sum of the item amounts = TOTAL VALUE. VAT: sum of items = net, net + VAT (per line, as in the template) =
/// the total entered. Each item amount is quantity × unit price in whole cents.
/// </summary>
public static class InvoiceTotalGuard
{
    /// <summary>Before writing: the generated / edited items against the amount entered.</summary>
    public static void Check(InvoiceDraft d)
    {
        if (d.Lines.Count == 0) return;   // "no items" has its own message
        if (d.Lines.Any(l => l.Quantity <= 0 || l.UnitCents < 0))
            throw new InvoiceTotalMismatchException(d.Target, d.Total, "draft");
        if (d.Total != d.Target)
            throw new InvoiceTotalMismatchException(d.Target, d.Total, "draft");
    }

    /// <summary>
    /// After Excel calculated the filled sheet: the cells' own values (item amounts, VAT, total) against the draft.
    /// Values are rounded to the cent like the template shows them.
    /// </summary>
    public static void CheckSheet(InvoiceDraft d, IReadOnlyList<double> itemAmounts, double? vat, double? total)
    {
        decimal R(double v) => Math.Round((decimal)v, 2, MidpointRounding.AwayFromZero);
        decimal items = itemAmounts.Sum(a => R(a));
        decimal shown = total is double t ? R(t) : items + (vat is double v0 ? R(v0) : 0m);
        if (items != d.Net)
            throw new InvoiceTotalMismatchException(d.Target, items + (d.VatRate != null ? d.Vat : 0m), "sheet");
        if (d.VatRate != null && vat is double v && R(v) != d.Vat)
            throw new InvoiceTotalMismatchException(d.Target, items + R(v), "sheet");
        if (shown != d.Target)
            throw new InvoiceTotalMismatchException(d.Target, d.VatRate != null ? shown : items, "sheet");
    }
}
