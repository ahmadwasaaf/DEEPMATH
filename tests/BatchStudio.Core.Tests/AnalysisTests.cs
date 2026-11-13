using BatchStudio.Core.Analysis;
using BatchStudio.Core.Invoices;
using Xunit;

namespace BatchStudio.Core.Tests;

public class AnalysisTests
{
    private static Party Supplier(string id, string name, params string[] banks) =>
        new("SUPPLIERS", id, name, null, null, banks.FirstOrDefault(), null, "INVOICE")
        {
            Banks = banks.Select((b, i) => new BankAccount(i + 1, b)).ToList(),
            DefaultBank = 1,
        };

    private static AnalysisResult Doc(string file, string name, string account, string swift = "CZCBCN2X", decimal? amount = 5000) => new()
    {
        File = @"C:\docs\" + file,
        FileName = file,
        Amount = amount,
        Ok = true,
        CompanyName = name,
        Bank = $"Beneficiary Name: {name}\nAccount Number: {account}\nSWIFT CODE: {swift}",
        Ids = new(StringComparer.OrdinalIgnoreCase) { ["account"] = [account], ["swift"] = [swift] },
    };

    private static readonly List<Party> Known =
    [
        Supplier("SUP-001", "UNION GROUP TRADING LIMITED",
            "Beneficiary Name: UNION GROUP TRADING LIMITED\nAccount Number: NRA 8725 3553 5857 4555 4659\nSWIFT CODE: CZCBCN2X"),
        Supplier("SUP-002", "JIANGMEN KORVEN TECHNOLOGY CO.,LTD",
            "Account Number: 111122223333", "Beneficiary: JIANGMEN KORVEN\nA/C NO: 675458598093558"),
    ];

    [Fact]
    public void Parses_an_engine_line()
    {
        var r = AnalysisResult.Parse("{\"file\":\"C:\\\\d\\\\5000.pdf\",\"name\":\"5000.pdf\",\"amount\":5000,\"ok\":true,\"company_name\":\"A CO\"," +
                                     "\"bank_details\":\"Account Number: 123456789\",\"identifiers\":{\"account\":[\"123456789\"]},\"notes\":[\"no_swift\"]}");
        Assert.NotNull(r);
        Assert.Equal(5000m, r!.Amount);
        Assert.Equal(new[] { "123456789" }, r.AccountIds.ToArray());
        Assert.Contains("no_swift", r.Notes);
    }

    [Fact]
    public void Parses_the_other_accounts_of_a_document()
    {
        var r = AnalysisResult.Parse("{\"name\":\"1.pdf\",\"ok\":true,\"company_name\":\"A CO\",\"bank_details\":\"Account Number: 111111111\"," +
                                     "\"bank_details_alt\":[\"Account Number: 222222222\\nCurrency: USD\"]}");
        Assert.Single(r!.AltBanks);
        Assert.Contains("222222222", AnalysisMatcher.AccountIdsIn(r.AltBanks[0]));
    }

    [Fact]
    public void A_resolved_conflict_keeps_the_entered_accounts_and_no_conflict()
    {
        var r = AnalysisResult.Parse("{\"name\":\"5000.pdf\",\"file\":\"C:\\\\a\\\\5000.pdf\",\"amount\":5000,\"ok\":true,\"company_name\":\"A CO\"," +
            "\"notes\":[\"test_document\",\"conflicting_instructions\"],\"conflicts\":[{\"code\":\"conflicting_instructions\",\"detail\":\"X / Y\"}]}")!;
        var v = r.Verified(@"D:\\Desktop\\5000.pdf", " B CO ", ["Account Number: 123456789012\nSWIFT: ABCDHKHHXXX", "", "Account Number: 998877665544"]);
        Assert.False(v.HasConflict);
        Assert.Equal("B CO", v.CompanyName);
        Assert.Equal(@"D:\\Desktop\\5000.pdf", v.File);
        Assert.Equal(5000m, v.Amount);
        Assert.Contains("123456789012", v.AccountIds);
        Assert.Single(v.AltBanks);
        Assert.Equal(new[] { "test_document", "verified_manually" }, v.Notes);
    }

    [Fact]
    public void Parses_the_conflicts_of_a_document()
    {
        var r = AnalysisResult.Parse("{\"name\":\"1.pdf\",\"ok\":true,\"company_name\":\"A CO\",\"bank_details\":\"\"," +
            "\"notes\":[\"conflicting_instructions\"],\"conflicts\":[{\"code\":\"conflicting_instructions\",\"detail\":\"HKEUR1 / HKUSD2\"}]}")!;
        Assert.True(r.HasConflict);
        Assert.Equal(new AnalysisConflict("conflicting_instructions", "HKEUR1 / HKUSD2"), Assert.Single(r.Conflicts));
        Assert.False(AnalysisResult.Parse("{\"name\":\"2.pdf\",\"ok\":true}")!.HasConflict);
    }

    [Fact]
    public void Known_account_matches_whatever_the_name()
    {
        var m = AnalysisMatcher.Match([Doc("1.pdf", "UNION GRP TRADING LTD", "NRA87253553585745554659")], Known)[0];
        Assert.Equal(AnalysisStatus.Matched, m.Status);
        Assert.Equal("SUP-001", m.Supplier!.Id);
        Assert.Equal(1, m.BankNumber);
        Assert.Equal("account", m.MatchedBy);
    }

    [Fact]
    public void Account_on_two_suppliers_is_decided_by_the_name()
    {
        List<Party> parties =
        [
            Supplier("SUP-12", "JIANGMEN AXO MOTOBIKE CO., LTD.", "Swift Code: BKCHCNBJ44E\nAccount Number: 691808261764"),
            Supplier("SUP-29", "LIRAN GROUP TRADING CO LIMITED", "Account Number: 642-329090-009", "Account Number: 6918 0826 1764"),
        ];
        var m = AnalysisMatcher.Match([Doc("70000.jpeg", "JIANGMEN AXO MOTOBIKE CO LTD", "691808261764", "BKCHCNBJ44E", 70000m)], parties)[0];
        Assert.Equal(AnalysisStatus.Matched, m.Status);
        Assert.Equal("SUP-12", m.Supplier!.Id);
        Assert.Equal(1, m.BankNumber);
    }

    [Theory]
    [InlineData("Account / IBAN: NRA87283813585755559721")]
    [InlineData("IBAN / Account No.: NRA87283813585755559721")]
    [InlineData("NRA87283813585755559721")]
    public void Account_iban_label_and_letter_prefix(string line) =>
        Assert.Contains("NRA87283813585755559721", AnalysisMatcher.AccountIdsIn("Beneficiary Name: EAMON\n" + line));

    [Theory]
    [InlineData("Account Number (USD): NRA358555851124912")]
    [InlineData("Account Number: NRA358555851124912 (USD)")]
    [InlineData("A/C No. (USD Account): NRA 3585 5585 1124 912")]
    public void Currency_note_beside_the_account_is_ignored(string line) =>
        Assert.Contains("NRA358555851124912", AnalysisMatcher.AccountIdsIn("Beneficiary Name: X\n" + line));

    [Fact]
    public void Second_account_of_a_supplier_is_used()
    {
        var m = AnalysisMatcher.Match([Doc("2.pdf", "JIANGMEN KORVEN TECHNOLOGY CO., LTD", "675458598093558", "CMBCCNBS200")], Known)[0];
        Assert.Equal(AnalysisStatus.Matched, m.Status);
        Assert.Equal(2, m.BankNumber);
    }

    [Fact]
    public void Known_name_with_another_account_is_a_new_account()
    {
        var m = AnalysisMatcher.Match([Doc("3.pdf", "Union Group Trading Co., Limited", "99990000111122")], Known)[0];
        Assert.Equal(AnalysisStatus.NewAccount, m.Status);
        Assert.Equal("SUP-001", m.Supplier!.Id);
    }

    [Fact]
    public void Unknown_supplier_is_new_and_a_second_document_of_it_follows_the_first()
    {
        var list = AnalysisMatcher.Match(
        [
            Doc("10.png", "HAIRO TRADING LIMITED", "16189795488", "SCBLHKHHXXX"),
            Doc("32.png", "HAIRO TRADING LIMITED", "16189795488", "SCBLHKHHXXX"),
            Doc("33.png", "HAIRO TRADING LTD", "55556666777788", "SCBLHKHHXXX"),
        ], Known);
        Assert.All(list, m => Assert.Equal(AnalysisStatus.NewSupplier, m.Status));
        Assert.Null(list[0].Owner);
        Assert.Same(list[0], list[1].Owner);
        Assert.False(list[1].OtherAccount);
        Assert.Same(list[0], list[2].Owner);
        Assert.True(list[2].OtherAccount);
    }

    [Fact]
    public void A_label_read_with_the_name_does_not_make_another_supplier()
    {
        var list = AnalysisMatcher.Match(
        [
            Doc("12,345.67.docx", "NINGBO SEABRIGHT HOUSEWARE CO., LTD.", "68585833555991726", "DTBKCNSHNBO"),
            Doc("USD 30000.pdf", "Customer: NINGBO SEABRIGHT HOUSEWARE CO., LTD.", "NRA330099887766554433", "DTBKCNSHNBO"),
        ], Known);
        Assert.Same(list[0], list[1].Owner);
        Assert.True(list[1].OtherAccount);
    }

    [Fact]
    public void Missing_amount_keeps_the_supplier_with_a_note()
    {
        var m = AnalysisMatcher.Match([Doc("TEST 36.pdf", "UNION GROUP TRADING LIMITED", "NRA87253553585745554659", amount: null)], Known)[0];
        Assert.Equal(AnalysisStatus.Matched, m.Status);
        Assert.Equal("Analysis.NoteNoAmount", m.Note);
    }

    [Fact]
    public void Nothing_read_needs_attention()
    {
        var m = AnalysisMatcher.Match([new AnalysisResult { FileName = "x.pdf", Ok = false, Error = "boom" }], Known)[0];
        Assert.Equal(AnalysisStatus.Attention, m.Status);
    }

    [Theory]
    [InlineData("5000.pdf", 5000)]
    [InlineData("70,000.jpeg", 70000)]
    [InlineData("12500.50.xlsx", 12500.50)]
    [InlineData("5000 (2).pdf", 5000)]
    [InlineData("USD 3,250.pdf", 3250)]
    public void Amount_from_file_name(string file, decimal expected) => Assert.Equal(expected, AnalysisMatcher.AmountFromName(file));

    [Theory]
    [InlineData("TEST 36.pdf")]
    [InlineData("invoice.pdf")]
    public void Not_an_amount(string file) => Assert.Null(AnalysisMatcher.AmountFromName(file));

    [Fact]
    public void New_supplier_record_and_free_bank_number()
    {
        string[] headers = ["Supplier ID", "Supplier Name (English)", "Chinese Name", "Company Address",
            "Banking Details (full paragraph)", "Category (English | Arabic)", "DEFAULT TEMPLATE", "Company’s Activities",
            "Banking Details (full paragraph) 2", "DEFAULT BANK"];
        var doc = new AnalysisResult
        {
            CompanyName = "YI WU SHI FENG IMPORT AND EXPORT CO., LIMITED", LocalName = "义乌", Address = "502, UNIT 1",
            Bank = "Account Number: NRA99505535358555891509", Activity = "Toys",
        };
        var v = AnalysisRecords.NewSupplier(headers, "SUP-099", doc, null, "General Trading - Import & Export | تجارة عامة", "INVOICE");
        Assert.Equal("SUP-099", v[0]);
        Assert.Equal(doc.CompanyName, v[1]);
        Assert.Equal("义乌", v[2]);
        Assert.Equal("502, UNIT 1", v[3]);
        Assert.Equal(doc.Bank, v[4]);
        Assert.StartsWith("General Trading", v[5]);
        Assert.Equal("INVOICE", v[6]);
        Assert.Equal("Toys", v[7]);
        Assert.Equal("", v[8]);

        Assert.Equal(2, AnalysisRecords.FreeBankNumber(headers, v));
        v[8] = "Account Number: 1234567890";
        Assert.Equal(3, AnalysisRecords.FreeBankNumber(headers, v));
    }

    [Fact]
    public void Category_for_new_suppliers()
    {
        List<CategoryInfo> cats = [new("C1", "Electronics | إلكترونيات", true), new("C7", "General Trading - Import & Export | تجارة عامة", true)];
        Assert.Equal("General Trading - Import & Export | تجارة عامة", AnalysisRecords.CategoryText(cats));
    }
}
