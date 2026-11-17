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
