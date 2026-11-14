using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BatchStudio.App.Services;
using BatchStudio.Core.Banking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>One total at the top of the page: all the balances of one currency.</summary>
public sealed record CurrencyTotal(string Currency, string Amount, string Accounts, bool Negative);

/// <summary>A bank account card (left column).</summary>
public sealed partial class BankCardViewModel : ObservableObject
{
    public BankAccount Account { get; }
    public BankCardViewModel(BankAccount a) { Account = a; Update(); }

    [ObservableProperty] private string _bank = "";
    [ObservableProperty] private string _shortNumber = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _currency = "";
    [ObservableProperty] private string _balance = "";
    [ObservableProperty] private bool _negative;
    [ObservableProperty] private string _monthIn = "";
    [ObservableProperty] private string _monthOut = "";
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private Brush _gradient = Brushes.SteelBlue;
    [ObservableProperty] private PointCollection _spark = [];
    [ObservableProperty] private bool _isSelected;

    /// <summary>Re-reads the account (after an edit or a new movement).</summary>
    public void Update()
    {
        var a = Account;
        Bank = a.Bank;
        ShortNumber = a.ShortNumber;
        Number = a.Number;
        Note = a.Note;
        Currency = a.Currency;
        Balance = BankBook.Money(a.Balance);
        Negative = a.Balance < 0;
        var m0 = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        MonthIn = "+" + BankBook.Money(a.Transactions.Where(t => t.Date >= m0 && t.Kind == BankTxKind.Deposit).Sum(t => t.Amount));
        MonthOut = "−" + BankBook.Money(a.Transactions.Where(t => t.Date >= m0 && t.Kind == BankTxKind.Withdrawal).Sum(t => t.Amount));
        Initials = string.Concat(a.Bank.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        if (Initials.Length == 0) Initials = "?";
        Gradient = CardGradient(a.Bank + a.Number);
        Spark = Sparkline(a);
    }

    /// <summary>A calm two-colour gradient picked from the bank name (the same bank always gets the same card colour).</summary>
    private static Brush CardGradient(string key)
    {
        (string, string)[] palette =
        [
            ("#0F766E", "#14B8A6"), ("#1E3A8A", "#3B82F6"), ("#4C1D95", "#8B5CF6"), ("#9D174D", "#EC4899"),
            ("#7C2D12", "#F97316"), ("#134E4A", "#22C55E"), ("#1F2937", "#64748B"), ("#0C4A6E", "#06B6D4"),
        ];
        uint h = 2166136261;
        foreach (var ch in key.ToUpperInvariant()) h = (h ^ ch) * 16777619;
        var (a, b) = palette[h % (uint)palette.Length];
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    /// <summary>The balance after each of the last 30 movements, scaled into a 120 × 32 box.</summary>
    private static PointCollection Sparkline(BankAccount a)
    {
        var values = new List<decimal> { a.OpeningBalance };
        decimal run = a.OpeningBalance;
        foreach (var t in a.Ordered()) { run += t.Signed; values.Add(run); }
        if (values.Count > 31) values = values.Skip(values.Count - 31).ToList();
        var pts = new PointCollection();
        if (values.Count < 2) { pts.Add(new Point(0, 16)); pts.Add(new Point(120, 16)); pts.Freeze(); return pts; }
        decimal min = values.Min(), max = values.Max(), span = max - min;
        for (int i = 0; i < values.Count; i++)
        {
            double y = span == 0 ? 16 : 30 - (double)((values[i] - min) / span) * 28;
            pts.Add(new Point(i * 120.0 / (values.Count - 1), y));
        }
        pts.Freeze();
        return pts;
    }
}

/// <summary>A row of the statement list.</summary>
public sealed record StatementRow(BankTransaction? Tx, string Date, string Description, string In, string Out, string Balance, bool NegativeBalance, bool IsForward)
{
    public bool IsMovement => Tx != null;
}

/// <summary>The add / edit account window.</summary>
public sealed partial class BankAccountFormViewModel : ObservableObject
{
    public BankAccount? Editing { get; }
    public BankAccountFormViewModel(BankAccount? a)
    {
        Editing = a;
        if (a == null) return;
        _bank = a.Bank; _number = a.Number; _currency = a.Currency; _note = a.Note;
        _opening = a.OpeningBalance.ToString("0.##", CultureInfo.InvariantCulture);
        _openingDate = a.OpeningDate;
    }

    public string Title => Loc.T(Editing == null ? "Bank.AddAccount" : "Bank.EditAccount");
    public IReadOnlyList<string> Currencies => BankBook.Currencies;

    [ObservableProperty] private string _bank = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _currency = "USD";
    [ObservableProperty] private string _opening = "0";
    [ObservableProperty] private DateTime? _openingDate = DateTime.Today;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _error = "";
}

/// <summary>
/// Bank Accounts page: the entity's bank accounts (kept apart from the Excel workbooks, in "Bank Accounts.json" in its data
/// folder): totals per currency on top, a card per account, and the selected account's statement with a running balance,
/// a period filter, quick entry of deposits / withdrawals and a PDF export.
/// </summary>
public sealed partial class BankAccountsViewModel(MainViewModel main) : ObservableObject
{
    private BankBook _book = new();
    private string _path = "";

    public ObservableCollection<CurrencyTotal> Totals { get; } = [];
    public ObservableCollection<BankCardViewModel> Cards { get; } = [];
    public ObservableCollection<StatementRow> Rows { get; } = [];

    [ObservableProperty] private BankCardViewModel? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private BankAccountFormViewModel? _form;

    public bool HasAccounts => Cards.Count > 0;
    public bool NoAccounts => Cards.Count == 0;
    public bool HasSelection => Selected != null;
    public bool HasForm => Form != null;
    partial void OnFormChanged(BankAccountFormViewModel? value) => OnPropertyChanged(nameof(HasForm));

    // ---------------------------------------------------------------- period filter
    public IReadOnlyList<string> Periods =>
        [Loc.T("Bank.Period.All"), Loc.T("Bank.Period.ThisMonth"), Loc.T("Bank.Period.LastMonth"), Loc.T("Bank.Period.ThisYear"), Loc.T("Bank.Period.Custom")];
    [ObservableProperty] private int _periodIndex;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    public bool IsCustom => PeriodIndex == 4;
    private bool _settingPeriod;
    partial void OnPeriodIndexChanged(int value)
    {
        var today = DateTime.Today;
        var m0 = new DateTime(today.Year, today.Month, 1);
        DateTime? f = null, t = null;
        switch (value)
        {
            case 1: f = m0; t = m0.AddMonths(1).AddDays(-1); break;
            case 2: f = m0.AddMonths(-1); t = m0.AddDays(-1); break;
            case 3: f = new DateTime(today.Year, 1, 1); t = new DateTime(today.Year, 12, 31); break;
            case 4: f = From ?? m0; t = To ?? today; break;
        }
        _settingPeriod = true;
        try { From = f; To = t; }
        finally { _settingPeriod = false; }
        OnPropertyChanged(nameof(IsCustom));
        BuildStatement();
    }
    partial void OnFromChanged(DateTime? value) { if (IsCustom && !_settingPeriod) BuildStatement(); }
    partial void OnToChanged(DateTime? value) { if (IsCustom && !_settingPeriod) BuildStatement(); }

    // ---------------------------------------------------------------- statement figures
    [ObservableProperty] private string _openingText = "";
    [ObservableProperty] private string _inText = "";
    [ObservableProperty] private string _outText = "";
    [ObservableProperty] private string _closingText = "";
    [ObservableProperty] private bool _closingNegative;
    [ObservableProperty] private string _movementsText = "";
    [ObservableProperty] private string _periodText = "";

    // ---------------------------------------------------------------- quick entry (add or edit a movement)
    [ObservableProperty] private bool _entryIsDeposit = true;
    [ObservableProperty] private DateTime? _entryDate = DateTime.Today;
    [ObservableProperty] private string _entryAmount = "";
    [ObservableProperty] private string _entryDescription = "";
    [ObservableProperty] private string _entryError = "";
    [ObservableProperty] private BankTransaction? _editingTx;
    public bool IsEditingTx => EditingTx != null;
    public string EntryButton => Loc.T(EditingTx == null ? "Bank.AddMovement" : "Bank.SaveMovement");
    partial void OnEditingTxChanged(BankTransaction? value) { OnPropertyChanged(nameof(IsEditingTx)); OnPropertyChanged(nameof(EntryButton)); }

    public void Refresh()
    {
        var entity = main.ActiveEntity;
        if (entity == null) return;
        var path = BankBook.PathIn(DriveSync.DataFolder(entity));
        var keep = Selected?.Account.Id;
        try { _book = BankBook.Load(path); _path = path; Status = ""; }
        catch (Exception ex)
        {
            // never overwrite a file that could not be read
            _book = new BankBook(); _path = "";
            Status = Loc.F("Bank.LoadFailed", ex.Message);
        }
        Cards.Clear();
        foreach (var a in _book.Accounts) Cards.Add(new BankCardViewModel(a));
        Selected = Cards.FirstOrDefault(c => c.Account.Id == keep) ?? Cards.FirstOrDefault();
        UpdateTotals();
        BuildStatement();
    }

    partial void OnSelectedChanged(BankCardViewModel? oldValue, BankCardViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(HasSelection));
        CancelEntry();
        BuildStatement();
    }

    [RelayCommand]
    private void Select(BankCardViewModel? card) { if (card != null) Selected = card; }

    private void UpdateTotals()
    {
        Totals.Clear();
        foreach (var (cur, total, n) in _book.TotalsByCurrency())
            Totals.Add(new CurrencyTotal(cur, BankBook.Money(total), Loc.F(n == 1 ? "Bank.OneAccount" : "Bank.NAccounts", n), total < 0));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(NoAccounts));
    }

    private BankStatement? _statement;

    private void BuildStatement()
    {
        Rows.Clear();
        _statement = null;
        if (Selected == null) return;
        var s = _statement = BankBook.Statement(Selected.Account, From, To);
        string cur = " " + s.Account.Currency;
        OpeningText = BankBook.Money(s.Opening) + cur;
        InText = "+" + BankBook.Money(s.TotalIn) + cur;
        OutText = "−" + BankBook.Money(s.TotalOut) + cur;
        ClosingText = BankBook.Money(s.Closing) + cur;
        ClosingNegative = s.Closing < 0;
        MovementsText = Loc.F("Bank.MovementsCount", s.Count);
        PeriodText = PeriodLabel(s);
        // newest first on screen (the PDF reads oldest first, like a bank statement)
        foreach (var l in s.Lines.Reverse())
            Rows.Add(new StatementRow(l.Tx, l.Tx.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), l.Tx.Description,
                                      l.Tx.Kind == BankTxKind.Deposit ? BankBook.Money(l.Tx.Amount) : "",
                                      l.Tx.Kind == BankTxKind.Withdrawal ? BankBook.Money(l.Tx.Amount) : "",
                                      BankBook.Money(l.Balance), l.Balance < 0, false));
        var fwdDate = s.From ?? s.Account.OpeningDate;
        Rows.Add(new StatementRow(null, fwdDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                                  Loc.T(s.From == null ? "Bank.OpeningBalance" : "Bank.BroughtForward"), "", "", BankBook.Money(s.Opening), s.Opening < 0, true));
    }

    private string PeriodLabel(BankStatement s)
    {
        string D(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        if (s.From == null && s.To == null) return Loc.T("Bank.Period.All");
        return $"{(s.From is { } f ? D(f) : "…")} – {(s.To is { } t ? D(t) : "…")}";
    }

    private bool Save()
    {
        if (_path.Length == 0) { Status = Loc.T("Bank.ReadOnly"); return false; }
        try { _book.Save(_path); return true; }
        catch (Exception ex) { Status = Loc.F("Bank.SaveFailed", ex.Message); return false; }
    }

    private void AfterChange()
    {
        Selected?.Update();
        UpdateTotals();
        BuildStatement();
    }

    // ---------------------------------------------------------------- accounts
    [RelayCommand]
    private void AddAccount() => Form = new BankAccountFormViewModel(null);

    [RelayCommand]
    private void EditAccount() { if (Selected != null) Form = new BankAccountFormViewModel(Selected.Account); }

    [RelayCommand]
    private void CloseForm() => Form = null;

    [RelayCommand]
    private void SaveForm()
    {
        var f = Form;
        if (f == null) return;
        f.Error = "";
        var bank = f.Bank.Trim();
        var cur = (f.Currency ?? "").Trim().ToUpperInvariant();
        if (bank.Length == 0) { f.Error = Loc.T("Bank.NeedBank"); return; }
        if (cur.Length is < 3 or > 4 || !cur.All(char.IsLetter)) { f.Error = Loc.T("Bank.NeedCurrency"); return; }
        if (!TryAmount(f.Opening, allowNegative: true, allowZero: true, out var opening)) { f.Error = Loc.T("Bank.BadAmount"); return; }
        var number = f.Number.Trim();
        if (number.Length > 0 && _book.Accounts.Any(a => a != f.Editing && string.Equals(Compact(a.Number), Compact(number), StringComparison.OrdinalIgnoreCase)))
        { f.Error = Loc.T("Bank.Duplicate"); return; }
        var acc = f.Editing ?? new BankAccount();
        if (f.Editing != null && !string.Equals(acc.Currency, cur, StringComparison.OrdinalIgnoreCase) && acc.Transactions.Count > 0
            && !Dialogs.Confirm(Loc.F("Bank.CurrencyChange", acc.Currency, cur))) return;
        acc.Bank = bank;
        acc.Number = number;
        acc.Currency = cur;
        acc.OpeningBalance = opening;
        acc.OpeningDate = (f.OpeningDate ?? DateTime.Today).Date;
        acc.Note = f.Note.Trim();
        if (f.Editing == null) _book.Accounts.Add(acc);
        if (!Save()) { if (f.Editing == null) _book.Accounts.Remove(acc); return; }
        Form = null;
        if (f.Editing == null)
        {
            var card = new BankCardViewModel(acc);
            Cards.Add(card);
            Selected = card;
            UpdateTotals();
        }
        else AfterChange();
        Status = Loc.T("Bank.Saved");
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        var c = Selected;
        if (c == null) return;
        if (!Dialogs.Confirm(Loc.F("Bank.DeleteAccountAsk", c.Account.Bank, c.Account.Number, c.Account.Transactions.Count))) return;
        int i = _book.Accounts.IndexOf(c.Account);
        _book.Accounts.Remove(c.Account);
        if (!Save()) { _book.Accounts.Insert(Math.Max(0, i), c.Account); return; }
        int ci = Cards.IndexOf(c);
        Cards.Remove(c);
        Selected = Cards.Count == 0 ? null : Cards[Math.Min(ci, Cards.Count - 1)];
        UpdateTotals();
    }

    [RelayCommand]
    private void CopyNumber()
    {
        if (Selected == null || Selected.Account.Number.Length == 0) return;
        try { Clipboard.SetText(Selected.Account.Number); Status = Loc.T("Bank.NumberCopied"); } catch { }
    }

    // ---------------------------------------------------------------- movements
    [RelayCommand]
    private void SetKind(string kind) => EntryIsDeposit = kind == "in";

    [RelayCommand]
    private void SubmitEntry()
    {
        EntryError = "";
        if (Selected == null) return;
        if (!TryAmount(EntryAmount, allowNegative: false, allowZero: false, out var amount)) { EntryError = Loc.T("Bank.BadAmount"); return; }
        var date = (EntryDate ?? DateTime.Today).Date;
        var kind = EntryIsDeposit ? BankTxKind.Deposit : BankTxKind.Withdrawal;
        var acc = Selected.Account;
        if (EditingTx is { } tx)
        {
            var old = (tx.Kind, tx.Date, tx.Amount, tx.Description);
            tx.Kind = kind; tx.Date = date; tx.Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero); tx.Description = EntryDescription.Trim();
            if (!Save()) { (tx.Kind, tx.Date, tx.Amount, tx.Description) = old; return; }
            Status = Loc.T("Bank.MovementSaved");
        }
        else
        {
            var added = _book.Add(acc, kind, date, amount, EntryDescription);
            if (!Save()) { acc.Transactions.Remove(added); return; }
            Status = Loc.F(kind == BankTxKind.Deposit ? "Bank.DepositAdded" : "Bank.WithdrawalAdded", BankBook.Money(added.Amount), acc.Currency);
        }
        // keep date and kind for the next entry (several movements of the same day are typed in a row)
        EntryAmount = "";
        EntryDescription = "";
        EditingTx = null;
        AfterChange();
    }

    [RelayCommand]
    private void CancelEntry()
    {
        EditingTx = null;
        EntryAmount = "";
        EntryDescription = "";
        EntryError = "";
    }

    [RelayCommand]
    private void EditMovement(StatementRow? row)
    {
        if (row?.Tx is not { } tx) return;
        EditingTx = tx;
        EntryIsDeposit = tx.Kind == BankTxKind.Deposit;
        EntryDate = tx.Date;
        EntryAmount = tx.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        EntryDescription = tx.Description;
        EntryError = "";
    }

    [RelayCommand]
    private void DeleteMovement(StatementRow? row)
    {
        if (row?.Tx is not { } tx || Selected == null) return;
        var kind = Loc.T(tx.Kind == BankTxKind.Deposit ? "Bank.Deposit" : "Bank.Withdrawal");
        if (!Dialogs.Confirm(Loc.F("Bank.DeleteMovementAsk", kind, BankBook.Money(tx.Amount), Selected.Account.Currency, tx.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)))) return;
        var acc = Selected.Account;
        int i = acc.Transactions.IndexOf(tx);
        acc.Transactions.Remove(tx);
        if (!Save()) { acc.Transactions.Insert(Math.Max(0, i), tx); return; }
        if (EditingTx == tx) CancelEntry();
        AfterChange();
        Status = Loc.T("Bank.MovementDeleted");
    }

    // ---------------------------------------------------------------- PDF statement
    [RelayCommand]
    private async Task ExportStatement()
    {
        if (_statement == null || Selected == null || IsBusy) return;
        IsBusy = true;
        Status = Loc.T("Bank.Exporting");
        var s = _statement;
        var entity = main.ActiveEntity?.Name ?? "";
        var words = new StatementWords(Loc.T("Bank.StatementTitle"), Loc.T("Bank.Entity"), Loc.T("Bank.Bank"), Loc.T("Bank.AccountNumber"),
            Loc.T("Bank.Currency"), Loc.T("Bank.Period"), Loc.T("Bank.Printed"), Loc.T("Bank.Date"), Loc.T("Bank.Description"),
            Loc.T("Bank.Deposit"), Loc.T("Bank.Withdrawal"), Loc.T("Bank.Balance"), Loc.T("Bank.Opening"), Loc.T("Bank.MoneyIn"),
            Loc.T("Bank.MoneyOut"), Loc.T("Bank.Closing"), Loc.T(s.From == null ? "Bank.OpeningBalance" : "Bank.BroughtForward"), Loc.T("Bank.Movements"));
        var period = PeriodText;
        var accent = (Application.Current.TryFindResource("BrandBrush") as SolidColorBrush)?.Color.ToString() is { } hex && hex.Length == 9 ? "#" + hex[3..] : "#0F766E";
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bank Statements");
        var name = Safe($"{s.Account.Bank} {s.Account.ShortNumber.Replace("•", "").Trim()} Statement {DateTime.Today:yyyy-MM-dd}") + ".pdf";
        var dest = BankAccountPdf.FreePath(folder, name);
        var tmp = Path.Combine(Path.GetTempPath(), "DEEPMATH Statement " + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                BankStatementBook.Build(tmp, s, entity, period, words, accent);
                ExcelPdf.Export(tmp, dest);
            });
            LastExport = dest;
            Status = Loc.F("Bank.Exported", Path.GetFileName(dest));
        }
        catch (Exception ex) { Status = Loc.F("Bank.ExportFailed", ex.Message); }
        finally
        {
            IsBusy = false;
            try { File.Delete(tmp); } catch { }
        }
    }

    [ObservableProperty] private string _lastExport = "";
    public bool HasLastExport => LastExport.Length > 0;
    partial void OnLastExportChanged(string value) => OnPropertyChanged(nameof(HasLastExport));

    [RelayCommand]
    private void ShowExport() { if (LastExport.Length > 0) Shell.ShowInFolder(LastExport); }

    /// <summary>Screenshot checks only (--page bank): three demo accounts with a few months of movements.</summary>
    internal void FillSample()
    {
        if (main.ActiveEntity == null) return;
        var path = BankBook.PathIn(DriveSync.DataFolder(main.ActiveEntity));
        if (File.Exists(path)) return;
        var book = new BankBook();
        var today = DateTime.Today;
        var rng = new Random(7);
        foreach (var (bank, number, cur, open) in new[]
        {
            ("Demo National Bank", "EG38 0019 0005 0000 0000 2631 4821", "USD", 48_250m),
            ("Second Demo Bank", "DE89 3704 0044 0532 0130 00", "EUR", 12_400m),
            ("Cairo Demo Bank", "1002 3344 5566 7788", "EGP", 350_000m),
        })
        {
            var a = new BankAccount { Bank = bank, Number = number, Currency = cur, OpeningBalance = open, OpeningDate = today.AddMonths(-4) };
            book.Accounts.Add(a);
            string[] ins = ["Client payment - INV 1042", "Client payment - INV 1057", "Transfer from savings", "Client payment - INV 1063"];
            string[] outs = ["Office rent", "Supplier payment - Demo Trading", "Bank charges", "Salaries", "Shipping - Demo Logistics"];
            for (int d = 120; d >= 0; d -= rng.Next(3, 9))
            {
                bool dep = rng.NextDouble() < 0.45;
                decimal amt = Math.Round((decimal)(rng.NextDouble() * (dep ? 9000 : 6000) + 150) * (cur == "EGP" ? 30 : 1), 2);
                book.Add(a, dep ? BankTxKind.Deposit : BankTxKind.Withdrawal, today.AddDays(-d), amt, dep ? ins[rng.Next(ins.Length)] : outs[rng.Next(outs.Length)]);
            }
        }
        try { book.Save(path); } catch { }
    }

    // ---------------------------------------------------------------- helpers
    private static string Compact(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"1,250.50" / "1250.5" / "١٢٥٠٫٥" → 1250.50 (two decimals at most).</summary>
    internal static bool TryAmount(string? text, bool allowNegative, bool allowZero, out decimal value)
    {
        value = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in t)
        {
            if (ch >= '٠' && ch <= '٩') sb.Append((char)('0' + ch - '٠'));
            else if (ch >= '۰' && ch <= '۹') sb.Append((char)('0' + ch - '۰'));
            else if (ch is '٫') sb.Append('.');
            else if (ch is ',' or '٬' or ' ' or ' ') continue;
            else if (ch is '−') sb.Append('-');
            else sb.Append(ch);
        }
        if (!decimal.TryParse(sb.ToString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (decimal.Round(value, 2) != value) return false;          // no fractions of a cent
        if (value < 0 && !allowNegative) return false;
        if (value == 0 && !allowZero) return false;
        return Math.Abs(value) < 1_000_000_000_000m;
    }
}
