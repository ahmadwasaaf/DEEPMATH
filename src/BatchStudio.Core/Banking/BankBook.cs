using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchStudio.Core.Banking;

/// <summary>Money in (a deposit), money out (an expense / withdrawal) or a transfer (out to another party, or between two of
/// the entity's own accounts: then a linked pair, out of one and into the other).</summary>
public enum BankTxKind { Deposit, Withdrawal, Transfer }

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

    /// <summary>Transfer only: true on the receiving side of a transfer between two of the entity's accounts.</summary>
    public bool Incoming { get; set; }
    /// <summary>Transfer between own accounts: the same id on both sides (edited / deleted together). Empty otherwise.</summary>
    public string LinkId { get; set; } = "";
    /// <summary>Transfer between own accounts: the id of the other account. Empty for an external transfer.</summary>
    public string Counterparty { get; set; } = "";

    [JsonIgnore] public decimal Signed => Kind == BankTxKind.Deposit || Kind == BankTxKind.Transfer && Incoming ? Amount : -Amount;
    [JsonIgnore] public bool IsIn => Signed > 0;
    [JsonIgnore] public bool IsInternal => LinkId.Length > 0;
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
    /// <summary>Account info: free text, several lines (IBAN, SWIFT, branch, address ...). Kept as "Note" in the file.</summary>
    public string Note { get; set; } = "";
    /// <summary>The account's password, hashed (Security.PasswordHash); empty until the user creates one.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>The Google account that may reset the password (signed in when it was created).</summary>
    public string RecoveryEmail { get; set; } = "";
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
    decimal TotalIn, decimal TotalOut, decimal Closing, BankTxKind? Only = null)
{
    public int Count => Lines.Count;
}

/// <summary>
/// The entity's bank accounts, kept in "Bank Accounts.json" inside its data folder (so Auto Save Files backs it up to
/// Google Drive with everything else). Independent of the Excel workbooks. Saved atomically, the previous copy kept as
/// "Bank Accounts.json.bak" (both in the "Bank Accounts" folder of the data folder).
/// </summary>
public sealed class BankBook
{
    public const string FileName = "Bank Accounts.json";
    public const string FolderName = "Bank Accounts";
    public int Version { get; set; } = 2;
    public List<BankAccount> Accounts { get; set; } = [];
    public long NextSeq { get; set; } = 1;
    /// <summary>Descriptions the user saved for quick use ("Bank charges" ...), in the order added.</summary>
    public List<string> Descriptions { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, FolderName, FileName);

    /// <summary>Moves "Bank Accounts.json" (and its .bak) from the data folder itself into its "Bank Accounts" folder
    /// (0.19 kept them at the top). Nothing is overwritten.</summary>
    public static void Migrate(string dataFolder)
    {
        var target = PathIn(dataFolder);
        foreach (var ext in new[] { "", ".bak" })
        {
            var old = Path.Combine(dataFolder, FileName + ext);
            if (!File.Exists(old) || File.Exists(target + ext)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(old, target + ext);
        }
    }

    public static BankBook Load(string path)
    {
        if (!File.Exists(path)) return new BankBook();
        var book = JsonSerializer.Deserialize<BankBook>(File.ReadAllText(path), Json) ?? new BankBook();
        book.Accounts ??= [];
        book.Descriptions ??= [];
        foreach (var a in book.Accounts)
        {
            a.Transactions ??= [];
            a.PasswordHash ??= ""; a.RecoveryEmail ??= ""; a.Note ??= "";
            foreach (var t in a.Transactions) { t.LinkId ??= ""; t.Counterparty ??= ""; t.Description ??= ""; }
        }
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

    /// <summary>
    /// A transfer of <paramref name="amount"/> out of <paramref name="from"/>: to <paramref name="to"/> (another account of the
    /// entity, same currency: it receives the amount, the two sides linked) or, when null, to an outside party.
    /// Returns the outgoing side.
    /// </summary>
    public BankTransaction Transfer(BankAccount from, BankAccount? to, DateTime date, decimal amount, string description)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be above zero.");
        if (to == from) throw new ArgumentException("A transfer needs two different accounts.", nameof(to));
        if (to != null && !string.Equals(to.Currency, from.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfers between accounts need the same currency.", nameof(to));
        var link = to == null ? "" : Guid.NewGuid().ToString("N");
        var amt = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var desc = (description ?? "").Trim();
        var outTx = new BankTransaction { Kind = BankTxKind.Transfer, Date = date.Date, Amount = amt, Description = desc, Seq = NextSeq++, LinkId = link, Counterparty = to?.Id ?? "" };
        from.Transactions.Add(outTx);
        if (to != null)
            to.Transactions.Add(new BankTransaction { Kind = BankTxKind.Transfer, Incoming = true, Date = date.Date, Amount = amt, Description = desc, Seq = NextSeq++, LinkId = link, Counterparty = from.Id });
        return outTx;
    }

    /// <summary>The other side of a transfer between own accounts (account and movement), or null.</summary>
    public (BankAccount Account, BankTransaction Tx)? Partner(BankTransaction tx)
    {
        if (!tx.IsInternal) return null;
        foreach (var a in Accounts)
            foreach (var t in a.Transactions)
                if (t != tx && t.LinkId == tx.LinkId) return (a, t);
        return null;
    }

    public BankAccount? Find(string id) => Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>Removes a movement (both sides of a transfer between own accounts). Returns what was removed, for undo.</summary>
    public List<(BankAccount Account, BankTransaction Tx, int Index)> Remove(BankAccount account, BankTransaction tx)
    {
        var removed = new List<(BankAccount, BankTransaction, int)>();
        if (Partner(tx) is { } p) { removed.Add((p.Account, p.Tx, p.Account.Transactions.IndexOf(p.Tx))); p.Account.Transactions.Remove(p.Tx); }
        removed.Add((account, tx, account.Transactions.IndexOf(tx)));
        account.Transactions.Remove(tx);
        return removed;
    }

    /// <summary>Puts back what <see cref="Remove"/> took out (a save that failed).</summary>
    public static void Restore(List<(BankAccount Account, BankTransaction Tx, int Index)> removed)
    {
        foreach (var (a, t, i) in Enumerable.Reverse(removed)) a.Transactions.Insert(Math.Clamp(i, 0, a.Transactions.Count), t);
    }

    /// <summary>Saves a description for quick use; false when empty or already there (any case).</summary>
    public bool AddDescription(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || Descriptions.Any(d => string.Equals(d, t, StringComparison.CurrentCultureIgnoreCase))) return false;
        Descriptions.Add(t);
        return true;
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
    /// <paramref name="only"/> shows one kind of movement: the balance after each line stays the real balance of the
    /// account, and money in / out add up the lines shown.
    /// </summary>
    public static BankStatement Statement(BankAccount account, DateTime? from, DateTime? to, BankTxKind? only = null)
    {
        decimal opening = account.OpeningBalance;
        decimal running = opening, tin = 0, tout = 0;
        var lines = new List<StatementLine>();
        foreach (var t in account.Ordered())
        {
            if (from is { } f && t.Date.Date < f.Date) { opening += t.Signed; running = opening; continue; }
            if (to is { } e && t.Date.Date > e.Date) break;
            running += t.Signed;
            if (only is { } k && t.Kind != k) continue;
            if (t.IsIn) tin += t.Amount; else tout += t.Amount;
            lines.Add(new StatementLine(t, running));
        }
        return new BankStatement(account, from, to, opening, lines, tin, tout, running, only);
    }

    /// <summary>"1,250.00" (two decimals, invariant grouping) for amounts everywhere in the bank pages.</summary>
    public static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>Common currencies offered in the account form (any other code can be typed).</summary>
    public static readonly string[] Currencies = ["USD", "EUR", "GBP", "EGP", "AED", "SAR", "CHF", "CNY", "TRY", "JPY"];
}
