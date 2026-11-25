using BatchStudio.Core.Analysis;
using BatchStudio.Core.Invoices;
using Xunit;

namespace BatchStudio.Core.Tests;

public class AccountMisreadTests
{
    private static Party Garbin() => new("SUPPLIERS", "SUP-051", "GARBIN CORPORATION", null, null, null, null, null)
    {
        Banks = [new BankAccount(1, "Beneficiary Name: GARBIN CORPORATION\nAccount Number: 083JSD100152\nBeneficiary Bank: KEB HANA BANK\nSWIFT/BIC: KOEXKRSE")],
    };

    private static AnalysisResult Read(string account, string swift) => AnalysisResult.Parse(
        "{\"file\":\"90000.jpeg\",\"name\":\"90000.jpeg\",\"amount\":90000,\"ok\":true,\"company_name\":\"GARBIN CORPORATION\"," +
        "\"bank_details\":\"Beneficiary Name: GARBIN CORPORATION\\nAccount Number: " + account + "\\nSWIFT CODE: " + swift + "\"," +
        "\"identifiers\":{\"account\":[\"" + account + "\"],\"swift\":[\"" + swift + "\"]}}")!;

    [Fact]
    public void Part_of_a_saved_account_is_a_misread_not_a_new_account()
    {
        var m = AnalysisMatcher.Match([Read("JSD100152", "KOEXKRSE")], [Garbin()])[0];
        Assert.Equal(AnalysisStatus.Matched, m.Status);
        Assert.Equal(1, m.BankNumber);
        Assert.Equal("Analysis.NoteAccountMisread", m.Note);
        Assert.Contains("account_misread", m.Result.Notes);
    }

    [Fact]
    public void Another_swift_stays_a_new_account()
    {
        var m = AnalysisMatcher.Match([Read("JSD100152", "HNBNKRSE")], [Garbin()])[0];
        Assert.Equal(AnalysisStatus.NewAccount, m.Status);
    }

    [Fact]
    public void New_account_at_another_bank_is_flagged()
    {
        var m = AnalysisMatcher.Match([Read("555777999111", "HZBKCN2X")], [Garbin()])[0];
        Assert.Equal(AnalysisStatus.NewAccount, m.Status);
        Assert.Equal("Analysis.NoteOtherBank", m.Note);
        Assert.Contains("account_other_bank", m.Result.Notes);
    }

    [Fact]
    public void New_account_at_the_same_bank_is_not_flagged()
    {
        var m = AnalysisMatcher.Match([Read("555777999111", "KOEXKRSE")], [Garbin()])[0];
        Assert.DoesNotContain("account_other_bank", m.Result.Notes);
    }

    [Fact]
    public void A_different_number_stays_a_new_account()
    {
        var m = AnalysisMatcher.Match([Read("555777999111", "KOEXKRSE")], [Garbin()])[0];
        Assert.Equal(AnalysisStatus.NewAccount, m.Status);
    }
}
