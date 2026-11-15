using BatchStudio.Core.Analysis;
using Xunit;

namespace BatchStudio.Core.Tests;

public class NameMatchTests
{
    [Theory]
    [InlineData("590A) GUANGzHOU XENLONG TRADING CO.LIMITED")]
    [InlineData("59A) GUANGZHOU XENLONG TRADING CO.,LIMITED")]
    [InlineData("GUANGZHOU XENLONG TRADING CO., LIMITED")]
    [InlineData("guangzhou xenlong trading co ltd")]
    public void Same_supplier_whatever_the_spelling(string typed) =>
        Assert.Equal(AnalysisMatcher.NormalizeName("GUANGZHOU XENLONG TRADING CO.,LIMITED"), AnalysisMatcher.NormalizeName(typed));

    [Fact]
    public void Numbers_in_a_real_name_stay() =>
        Assert.NotEqual(AnalysisMatcher.NormalizeName("57 STAR TRADING LIMITED"), AnalysisMatcher.NormalizeName("STAR TRADING LIMITED"));
}
