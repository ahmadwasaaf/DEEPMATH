using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using Xunit;

namespace BatchStudio.Core.Tests;

public class InvoiceTotalGuardTests
{
    private static InvoiceDraft Draft(decimal target, double? vat, params (long Qty, long Cents)[] lines) => new()
    {
        TemplatePath = "x.xlsx", Map = new TemplateMap { VatRate = vat }, Target = target,
        Lines = lines.Select(l => new InvoiceLine { Code = "C", Description = "D", Quantity = l.Qty, UnitCents = l.Cents }).ToList(),
    };

    [Fact]
    public void Items_that_add_up_pass()
    {
        InvoiceTotalGuard.Check(Draft(1000m, null, (10, 5000), (5, 10000)));
    }

    [Fact]
    public void A_cent_off_stops_the_invoice()
    {
        var ex = Assert.Throws<InvoiceTotalMismatchException>(() => InvoiceTotalGuard.Check(Draft(1000m, null, (10, 5000), (5, 9999))));
        Assert.Equal(1000m, ex.Entered);
        Assert.Equal(999.95m, ex.Items);
        Assert.Equal(-0.05m, ex.Difference);
    }

    [Fact]
    public void The_sheet_as_calculated_must_match_too()
    {
        var d = Draft(1000m, null, (10, 5000), (5, 10000));
        InvoiceTotalGuard.CheckSheet(d, [500d, 500d], null, 1000d);
        Assert.Throws<InvoiceTotalMismatchException>(() => InvoiceTotalGuard.CheckSheet(d, [500d, 499.99], null, 1000d));
        Assert.Throws<InvoiceTotalMismatchException>(() => InvoiceTotalGuard.CheckSheet(d, [500d, 500d], null, 1200d));
    }
}
