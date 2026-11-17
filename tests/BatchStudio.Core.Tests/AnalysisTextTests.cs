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
}
