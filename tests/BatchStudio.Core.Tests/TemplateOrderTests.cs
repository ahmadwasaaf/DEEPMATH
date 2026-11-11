using BatchStudio.Core.Templates;

namespace BatchStudio.Core.Tests;

public class TemplateOrderTests
{
    [Fact]
    public void Own_templates_then_INVOICE_then_INV_by_number()
    {
        var names = new[] { "INV10", "INV2", "INV1", "INVOICE", "INV15", "Madar", "INV3", "INV11" };
        Assert.Equal(["Madar", "INVOICE", "INV1", "INV2", "INV3", "INV10", "INV11", "INV15"], TemplateOrder.Sort(names));
    }
}
