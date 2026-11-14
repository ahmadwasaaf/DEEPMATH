using BatchStudio.Core.Banking;
using Xunit;

namespace BatchStudio.Core.Tests;

public class BankBrandsTests
{
    [Theory]
    [InlineData("البنك الأهلي المصري", "nbe")]
    [InlineData("الاهلى", "nbe")]
    [InlineData("NBE", "nbe")]
    [InlineData("National Bank of Egypt", "nbe")]
    [InlineData("البنك العربى الافريقى الدولى", "aaib")]
    [InlineData("AAIB", "aaib")]
    [InlineData("Arab African International Bank", "aaib")]
    [InlineData("بنك مصر", "misr")]
    [InlineData("Banque Misr", "misr")]
    [InlineData("بنك القاهرة", "cairo")]
    [InlineData("Banque du Caire", "cairo")]
    [InlineData("بنك الإسكندرية", "alex")]
    [InlineData("alexbank", "alex")]
    [InlineData("مصرف الراجحي", "rajhi")]
    [InlineData("Al Rajhi Bank", "rajhi")]
    [InlineData("بنك البلاد", "albilad")]
    [InlineData("Bank Albilad", "albilad")]
    [InlineData("ساب", "sab")]
    [InlineData("البنك الأول", "sab")]
    [InlineData("SAB", "sab")]
    [InlineData("Saudi Awwal Bank", "sab")]
    [InlineData("بنك الجزيرة", "jazira")]
    [InlineData("Bank AlJazira", "jazira")]
    [InlineData("البنك السعودي الفرنسي", "bsf")]
    [InlineData("Banque Saudi Fransi", "bsf")]
    public void Recognises_known_banks(string name, string key) => Assert.Equal(key, BankBrands.Match(name)?.Key);

    [Theory]
    [InlineData("HSBC")]
    [InlineData("البنك الأهلي السعودي")]
    [InlineData("Sabadell")]
    [InlineData("حساب التوفير")]
    [InlineData("")]
    public void Leaves_other_banks_alone(string name) => Assert.Null(BankBrands.Match(name));
}
