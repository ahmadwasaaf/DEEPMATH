using BatchStudio.Core.Analysis;
using Xunit;

namespace BatchStudio.Core.Tests;

public class HealthCheckTests
{
    private static readonly string[] Headers =
        ["Supplier ID", "Supplier Name (English)", "Banking Details (full paragraph)", "Category (English | Arabic)", "Banking Details (full paragraph) 2"];

    private static string[] Row(string id, string name, string bank1, string bank2 = "", string cat = "General") => [id, name, bank1, cat, bank2];

    [Fact]
    public void Finds_the_mistakes_analysis_found_one_by_one()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            Row("SUP-012", "JIANGMEN AXO MOTOBIKE CO., LTD.", "Account Number: 691808261764\nSWIFT Code: BKCHCNBJ44E"),
            Row("SUP-029", "LIRAN GROUP TRADING CO LIMITED", "Account / IBAN: 89420531626\nBank Identifier: BEASHKHH", "Account Number: 6918 0826 1764\nSWIFT CODE: BKCHCNBJ44E"),
            Row("SUP-030", "EAMON INTERNATIONAL TRADING COMPANY LIMITED", "Account / IBAN: NRA87283813585755559721\nSWIFT/BIC: CZCBCN2X",
                "Account Number: NRA87283813585755559721\nSWIFT CODE: CZCBCN2XXXX"),
            Row("SUP-031", "Guangzhou Jiayin Electronics Co., Ltd", "Bank Name: JPMorgan Chase Bank N.A.\nSWIFT/BIC Code: CHASHKHH"),
            Row("SUP-032", "LIRAN GROUP TRADING COMPANY LIMITED", "Beneficiary Bank: Iaxghty Hang Seng Bank Limited\nAccount Number: 642329090009"),
            Row("SUP-033", "NO BANK TRADING LTD", "", cat: ""),
        };
        var found = HealthCheck.Suppliers(Headers, rows);
        Assert.Contains(found, i => i.Kind == HealthKind.SharedAccount && i.Ids.Contains("SUP-012") && i.Ids.Contains("SUP-029"));
        Assert.Contains(found, i => i.Kind == HealthKind.AccountTwice && i.Ids[0] == "SUP-030");
        Assert.Contains(found, i => i.Kind == HealthKind.NoAccount && i.Ids[0] == "SUP-031");
        Assert.Contains(found, i => i.Kind == HealthKind.NoSwift && i.Ids[0] == "SUP-032");
        Assert.Contains(found, i => i.Kind == HealthKind.BankNameJunk && i.Ids[0] == "SUP-032");
        Assert.Contains(found, i => i.Kind == HealthKind.SameName && i.Ids.Contains("SUP-029") && i.Ids.Contains("SUP-032"));
        Assert.Contains(found, i => i.Kind == HealthKind.NoBank && i.Ids[0] == "SUP-033");
        Assert.Contains(found, i => i.Kind == HealthKind.NoCategory && i.Ids[0] == "SUP-033");
        Assert.DoesNotContain(found, i => i.Kind == HealthKind.NoSwift && i.Ids[0] == "SUP-029");
    }

    [Fact]
    public void A_clean_list_has_nothing()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            Row("SUP-001", "ALPHA TRADING LIMITED", "Account Number: 1234567890\nSWIFT CODE: ABCDCNSH"),
            Row("SUP-002", "OMEGA INDUSTRIAL CO., LTD", "Account Number: 9876543210\nSWIFT CODE: WXYZHKHH"),
        };
        Assert.Empty(HealthCheck.Suppliers(Headers, rows));
    }

    [Theory]
    [InlineData("Iaxghty", true)]
    [InlineData("Standard", false)]
    [InlineData("Deutsche", false)]
    [InlineData("HSBC", false)]
    [InlineData("Ping", false)]
    public void Junk_word(string word, bool junk) => Assert.Equal(junk, HealthCheck.IsJunkWord(word));

    [Fact]
    public void Clients_close_names_and_missing_category()
    {
        string[] h = ["Client ID", "Client Name", "Client Category", "Company’s Activities"];
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "CLI-001", "AL NOOR TRADING CO.", "Retail", "" },
            new[] { "CLI-002", "Al Noor Trading Company", "", "" },
        };
        var found = HealthCheck.Clients(h, rows);
        Assert.Contains(found, i => i.Kind == HealthKind.SameName);
        Assert.Contains(found, i => i.Kind == HealthKind.NoCategory && i.Ids[0] == "CLI-002");
    }
}
