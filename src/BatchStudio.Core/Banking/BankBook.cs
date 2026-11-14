using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchStudio.Core.Banking;

/// <summary>Money in (a deposit) or money out (an expense / withdrawal).</summary>
public enum BankTxKind { Deposit, Withdrawal }

/// <summary>One movement on a bank account. <see cref="Amount"/> is always positive; <see cref="Kind"/> gives the sign.</summary>
public sealed class BankTransaction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Today;
    public BankTxKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
    /// <summary>Order of entry: movements on the same day keep the order they were added in.</summary>
    public long Seq { get; set; }

    [JsonIgnore] public decimal Signed => Kind == BankTxKind.Deposit ? Amount : -Amount;
}

/// <summary>A bank account of the entity: the bank, the number, the currency and its movements.</summary>
public sealed class BankAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Bank { get; set; } = "";
    public string Number { get; set; } = "";
    public string Currency { get; set; } = "USD";
    /// <summary>The balance on <see cref="OpeningDate"/>, before any movement entered here.</summary>
    public decimal OpeningBalance { get; set; }
    public DateTime OpeningDate { get; set; } = DateTime.Today;
    public string Note { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<BankTransaction> Transactions { get; set; } = [];

    /// <summary>Movements in date order (same day: order of entry).</summary>
    public IEnumerable<BankTransaction> Ordered() => Transactions.OrderBy(t => t.Date.Date).ThenBy(t => t.Seq);

    /// <summary>Balance now: opening balance plus every movement.</summary>
    [JsonIgnore] public decimal Balance => OpeningBalance + Transactions.Sum(t => t.Signed);

    /// <summary>Balance at the end of <paramref name="day"/> (movements dated after it not counted).</summary>
    public decimal BalanceAt(DateTime day) => OpeningBalance + Transactions.Where(t => t.Date.Date <= day.Date).Sum(t => t.Signed);

    /// <summary>Last digits for the cards: "•••• 4821".</summary>
    [JsonIgnore] public string ShortNumber
    {
        get
        {
            var digits = new string(Number.Where(char.IsLetterOrDigit).ToArray());
            return digits.Length <= 4 ? Number.Trim() : "•••• " + digits[^4..];
        }
    }
}

/// <summary>One line of a statement: the movement and the balance after it.</summary>
public sealed record StatementLine(BankTransaction Tx, decimal Balance);

/// <summary>A bank statement for a period: balance brought forward, the movements with a running balance, totals.</summary>
public sealed record BankStatement(
    BankAccount Account, DateTime? From, DateTime? To, decimal Opening, IReadOnlyList<StatementLine> Lines,
    decimal TotalIn, decimal TotalOut, decimal Closing)
{
    public int Count => Lines.Count;
}

/// <summary>
/// The entity's bank accounts, kept in "Bank Accounts.json" inside its data folder (so Auto Save Files backs it up to
/// Google Drive with everything else). Independent of the Excel workbooks. Saved atomically, the previous copy kept as
/// "Bank Accounts.json.bak".
/// </summary>
public sealed class BankBook
{
    public const string FileName = "Bank Accounts.json";
    public int Version { get; set; } = 1;
    public List<BankAccount> Accounts { get; set; } = [];
    public long NextSeq { get; set; } = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, FileName);

    public static BankBook Load(string path)
    {
        if (!File.Exists(path)) return new BankBook();
        var book = JsonSerializer.Deserialize<BankBook>(File.ReadAllText(path), Json) ?? new BankBook();
        book.Accounts ??= [];
        foreach (var a in book.Accounts) a.Transactions ??= [];
        long max = book.Accounts.SelectMany(a => a.Transactions).Select(t => t.Seq).DefaultIfEmpty(0).Max();
        if (book.NextSeq <= max) book.NextSeq = max + 1;
        return book;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    /// <summary>Adds a movement (amount must be above zero); returns it.</summary>
    public BankTransaction Add(BankAccount account, BankTxKind kind, DateTime date, decimal amount, string description)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be above zero.");
        var tx = new BankTransaction
        {
            Kind = kind, Date = date.Date, Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero),
            Description = (description ?? "").Trim(), Seq = NextSeq++,
        };
        account.Transactions.Add(tx);
        return tx;
    }

    /// <summary>Balances of all accounts added up per currency (USD, EUR ...: never mixed), largest first.</summary>
    public IReadOnlyList<(string Currency, decimal Total, int Accounts)> TotalsByCurrency() =>
        Accounts.GroupBy(a => (a.Currency ?? "").Trim().ToUpperInvariant())
                .Select(g => (g.Key, g.Sum(a => a.Balance), g.Count()))
                .OrderByDescending(x => x.Item3).ThenBy(x => x.Key)
                .ToList();

    /// <summary>
    /// Statement of <paramref name="account"/> from <paramref name="from"/> to <paramref name="to"/> (inclusive; null = no
    /// limit): the balance brought forward is the opening balance plus every movement dated before the period.
    /// </summary>
    public static BankStatement Statement(BankAccount account, DateTime? from, DateTime? to)
    {
        decimal opening = account.OpeningBalance;
        decimal running = opening, tin = 0, tout = 0;
        var lines = new List<StatementLine>();
        foreach (var t in account.Ordered())
        {
            if (from is { } f && t.Date.Date < f.Date) { opening += t.Signed; running = opening; continue; }
            if (to is { } e && t.Date.Date > e.Date) break;
            running += t.Signed;
            if (t.Kind == BankTxKind.Deposit) tin += t.Amount; else tout += t.Amount;
            lines.Add(new StatementLine(t, running));
        }
        return new BankStatement(account, from, to, opening, lines, tin, tout, running);
    }

    /// <summary>"1,250.00" (two decimals, invariant grouping) for amounts everywhere in the bank pages.</summary>
    public static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>Common currencies offered in the account form (any other code can be typed).</summary>
    public static readonly string[] Currencies = ["USD", "EUR", "GBP", "EGP", "AED", "SAR", "CHF", "CNY", "TRY", "JPY"];
}
