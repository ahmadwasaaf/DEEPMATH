using BatchStudio.Core.Banking;
using Xunit;

namespace BatchStudio.Core.Tests;

public class BankBookTests
{
    private static (BankBook Book, BankAccount Acc) Sample()
    {
        var book = new BankBook();
        var acc = new BankAccount { Bank = "HSBC", Number = "EG12 0000 1111 2222 4821", Currency = "usd", OpeningBalance = 1000m, OpeningDate = new DateTime(2026, 1, 1) };
        book.Accounts.Add(acc);
        book.Add(acc, BankTxKind.Deposit, new DateTime(2026, 1, 10), 500m, "Client A");
        book.Add(acc, BankTxKind.Withdrawal, new DateTime(2026, 2, 5), 200.555m, "Rent");      // rounds to 200.56
        book.Add(acc, BankTxKind.Deposit, new DateTime(2026, 1, 10), 0.1m, "Same day, later");
        book.Add(acc, BankTxKind.Withdrawal, new DateTime(2026, 3, 1), 99.99m, "Fees");
        return (book, acc);
    }

    [Fact]
    public void Balance_and_running_balance_add_up()
    {
        var (_, acc) = Sample();
        Assert.Equal(1000m + 500m - 200.56m + 0.1m - 99.99m, acc.Balance);
        var s = BankBook.Statement(acc, null, null);
        Assert.Equal(1000m, s.Opening);
        Assert.Equal(new[] { 1500m, 1500.1m, 1299.54m, 1199.55m }, s.Lines.Select(l => l.Balance).ToArray());
        Assert.Equal("Same day, later", s.Lines[1].Tx.Description);   // same day keeps entry order
        Assert.Equal(acc.Balance, s.Closing);
        Assert.Equal(s.Opening + s.TotalIn - s.TotalOut, s.Closing);
    }

    [Fact]
    public void Period_brings_earlier_movements_forward()
    {
        var (_, acc) = Sample();
        var s = BankBook.Statement(acc, new DateTime(2026, 2, 1), new DateTime(2026, 2, 28));
        Assert.Equal(1500.1m, s.Opening);
        Assert.Single(s.Lines);
        Assert.Equal(0m, s.TotalIn);
        Assert.Equal(200.56m, s.TotalOut);
        Assert.Equal(1299.54m, s.Closing);
        Assert.Equal(acc.BalanceAt(new DateTime(2026, 2, 28)), s.Closing);
    }

    [Fact]
    public void Totals_never_mix_currencies()
    {
        var (book, _) = Sample();
        var eur = new BankAccount { Bank = "BNP", Currency = "EUR", OpeningBalance = 50m };
        book.Accounts.Add(eur);
        book.Accounts.Add(new BankAccount { Bank = "Citi", Currency = "USD", OpeningBalance = 10m });
        var t = book.TotalsByCurrency();
        Assert.Equal(2, t.Count);
        Assert.Equal(("USD", 1199.55m + 10m, 2), t[0]);
        Assert.Equal(("EUR", 50m, 1), t[1]);
    }

    [Fact]
    public void Save_and_load_round_trip_with_backup()
    {
        var (book, acc) = Sample();
        var dir = Path.Combine(Path.GetTempPath(), "bankbook-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = BankBook.PathIn(dir);
            book.Save(path);
            book.Add(acc, BankTxKind.Deposit, DateTime.Today, 1m, "x");
            book.Save(path);
            Assert.True(File.Exists(path + ".bak"));
            var back = BankBook.Load(path);
            Assert.Equal(acc.Balance, back.Accounts[0].Balance);
            Assert.Equal(5, back.Accounts[0].Transactions.Count);
            Assert.True(back.NextSeq > back.Accounts[0].Transactions.Max(t => t.Seq));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Amount_must_be_positive()
    {
        var (book, acc) = Sample();
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Add(acc, BankTxKind.Deposit, DateTime.Today, 0m, ""));
    }

    [Fact]
    public void Statement_workbook_builds()
    {
        var (_, acc) = Sample();
        var path = Path.Combine(Path.GetTempPath(), "stmt-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var w = new StatementWords("Bank statement", "Entity", "Bank", "Account", "Currency", "Period", "Printed", "Date", "Description",
                                       "Deposit", "Withdrawal", "Balance", "Opening", "In", "Out", "Closing", "Brought forward", "Movements");
            BankStatementBook.Build(path, BankBook.Statement(acc, null, null), "ACME", "All", w);
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
