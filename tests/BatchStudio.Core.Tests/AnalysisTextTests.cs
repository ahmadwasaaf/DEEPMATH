using BatchStudio.Core.Analysis;
using Xunit;

namespace BatchStudio.Core.Tests;

public class AnalysisTextTests
{
    [Fact]
    public void Text_has_the_engine_layout_and_v16_block()
    {
        var line = """{"file":"C:\\docs\\12000.png","name":"12000.png","ok":true,"amount":12000,"company_name":"ACME CO., LTD","company_address":"Yiwu","bank_details":"Beneficiary Name: ACME\nSWIFT: ABCDCNBJ","identifiers":{"account":["123"]},"notes":["swift_check","v16_filled"],"v16":{"changes":["swift ABCDCNBJ from extra OCR"],"checks":{"swift":"ok"}},"seconds":3.2}""";
        var r = AnalysisResult.Parse(line)!;
        var t = AnalysisText.TextOf(r, "DEEPMATH-ANALYZE 2.1", new DateTime(2026, 11, 17, 15, 0, 0));
        Assert.Contains("DEEPMATH analysis - 12000.png", t);
        Assert.Contains("Amount (from file name): 12000", t);
        Assert.Contains("--- Banking details ---\r\nBeneficiary Name: ACME", t);
        Assert.Contains("account: 123", t);
        Assert.Contains("- Check against the document before approving", t);
        Assert.Contains("--- What V16 did ---\r\n- swift ABCDCNBJ from extra OCR\r\n- check swift: ok", t);
        Assert.EndsWith("Time: 3.2s\r\n", t);
    }

    [Fact]
    public void Document_total_is_read_and_written()
    {
        var line = """{"file":"x\\9000.pdf","name":"9000.pdf","ok":true,"amount":9000,"company_name":"ACME","bank_details":"Currency: USD","document_amount":10000.5,"document_currency":"EUR","notes":["currency_differs"],"identifiers":{"intermediary_swift":["GCLBUS33XXX"]}}""";
        var r = AnalysisResult.Parse(line)!;
        Assert.Equal(10000.5m, r.DocumentAmount);
        Assert.Equal("EUR", r.DocumentCurrency);
        var t = AnalysisText.TextOf(r, "v", DateTime.Now);
        Assert.Contains("Total in the document: EUR 10,000.5", t);
        Assert.Contains("- The document's currency is not the account's currency", t);
        Assert.Contains("intermediary_swift: GCLBUS33XXX", t);
        Assert.Empty(r.AccountIds);
    }
}

public class AnalysisCategoryTests
{
    private static readonly List<BatchStudio.Core.Invoices.CategoryInfo> Cats =
    [
        new("C1", "General Trading - Import & Export | تجارة عامة - استيراد وتصدير السلع", true),
        new("C2", "Industrial Fasteners & Hardware | مسامير ومعدات صناعية", true),
        new("C3", "Auto Spare Parts | قطع غيار سيارات", true),
    ];

    [Fact]
    public void Closest_category_else_general_trading()
    {
        Assert.Equal(Cats[1].Text, AnalysisRecords.CategoryFor("Industrial fasteners and maintenance components", Cats));
        Assert.Equal(Cats[0].Text, AnalysisRecords.CategoryFor("Precision motion control assemblies", Cats));
        Assert.Equal(Cats[0].Text, AnalysisRecords.CategoryFor("", Cats));
    }

    [Fact]
    public void Contact_goes_under_the_address()
    {
        var r = new AnalysisResult { CompanyName = "X", Address = "1 Road", Contact = "Tel: 123456789 · Email: a@b.com" };
        var v = AnalysisRecords.NewSupplier(["ID", "Supplier Name (English)", "Company Address"], "S1", r, null, null, null);
        Assert.Equal("1 Road\nTel: 123456789\nEmail: a@b.com", v[2]);
    }
}

public class SwiftCompareTests
{
    [Fact]
    public void O_and_zero_are_one_in_the_location_part()
    {
        Assert.True(AnalysisMatcher.SameSwift("BHMBHKHOXXX", "BHMBHKH0XXX"));
        Assert.True(AnalysisMatcher.SameSwift("BEASHKHH", "BEASHKHHXXX"));
        Assert.False(AnalysisMatcher.SameSwift("BOFAUS3N", "B0FAUS3N"));        // the bank part never
        Assert.Equal("ICICINBBCTS", AnalysisMatcher.SwiftIn("Beneficiary Name: X\nIntermediary SWIFT: CITIUS33\nSWIFT CODE: ICICINBBCTS"));
    }
}
