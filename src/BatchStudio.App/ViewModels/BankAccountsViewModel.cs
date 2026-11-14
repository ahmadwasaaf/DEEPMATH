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
    /// <summary>A well-known bank (Egypt / Saudi Arabia) recognised from the name: its colours and its other-language name.</summary>
    [ObservableProperty] private BankBrand? _brand;
    [ObservableProperty] private string _brandName = "";
    [ObservableProperty] private Brush _accent = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
    [ObservableProperty] private Brush _accentText = Brushes.White;
    public bool HasBrand => Brand != null;
    partial void OnBrandChanged(BankBrand? value) => OnPropertyChanged(nameof(HasBrand));
    /// <summary>The brand colour as "#RRGGBB" (the PDF statement takes it), or null.</summary>
    public string? BrandHex => Brand?.To;

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
        MonthIn = "+" + BankBook.Money(a.Transactions.Where(t => t.Date >= m0 && t.IsIn).Sum(t => t.Amount));
        MonthOut = "−" + BankBook.Money(a.Transactions.Where(t => t.Date >= m0 && !t.IsIn).Sum(t => t.Amount));
        Initials = string.Concat(a.Bank.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        if (Initials.Length == 0) Initials = "?";
        Brand = BankBrands.Match(a.Bank);
        if (Brand is { } b)
        {
            Gradient = Two(b.From, b.To);
            Initials = b.Monogram;
            Accent = Solid(b.Accent);
            AccentText = Solid(b.AccentText);
            // the name in the other language (typed in Arabic → the English name under it, and the other way round)
            bool typedArabic = a.Bank.Any(ch => ch is >= '\u0600' and <= '\u06FF');
            BrandName = typedArabic ? b.NameEn : b.NameAr;
        }
        else
        {
            Gradient = CardGradient(a.Bank + a.Number);
            Accent = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
            AccentText = Brushes.White;
            BrandName = "";
        }
        Spark = Sparkline(a);
    }

    private static Brush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Brush Two(string a, string b)
    {
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
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
public sealed record StatementRow(BankTransaction? Tx, string Date, string Type, string TypeKey, string Description, string In, string Out, string Balance, bool NegativeBalance, bool IsForward)
{
    public bool IsMovement => Tx != null;
}

/// <summary>Where a transfer goes: one of the entity's other accounts (same currency) or outside.</summary>
public sealed record TransferTarget(BankAccount? Account, string Name)
{
    public override string ToString() => Name;
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
        _opening = BankBook.Money(a.OpeningBalance);
        _openingDate = a.OpeningDate;
    }

    public string Title => Loc.T(Editing == null ? "Bank.AddAccount" : "Bank.EditAccount");
    public IReadOnlyList<string> Currencies => BankBook.Currencies;

    [ObservableProperty] private string _bank = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _currency = "USD";
    [ObservableProperty] private string _opening = "0.00";
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

    // ---------------------------------------------------------------- type filter (all / deposits / withdrawals / transfers)
    public IReadOnlyList<string> Types => [Loc.T("Bank.Type.All"), Loc.T("Bank.Deposit"), Loc.T("Bank.Withdrawal"), Loc.T("Bank.Transfer")];
    [ObservableProperty] private int _typeIndex;
    partial void OnTypeIndexChanged(int value) => BuildStatement();
    private BankTxKind? OnlyKind => TypeIndex switch { 1 => BankTxKind.Deposit, 2 => BankTxKind.Withdrawal, 3 => BankTxKind.Transfer, _ => null };

    // ---------------------------------------------------------------- statement figures
    [ObservableProperty] private string _openingText = "";
    [ObservableProperty] private string _inText = "";
    [ObservableProperty] private string _outText = "";
    [ObservableProperty] private string _closingText = "";
    [ObservableProperty] private bool _closingNegative;
    [ObservableProperty] private string _movementsText = "";
    [ObservableProperty] private string _periodText = "";

    // ---------------------------------------------------------------- quick entry (add or edit a movement)
    /// <summary>0 deposit, 1 withdrawal, 2 transfer.</summary>
    [ObservableProperty] private int _entryKind;
    public bool IsDepositKind { get => EntryKind == 0; set { if (value) EntryKind = 0; } }
    public bool IsWithdrawalKind { get => EntryKind == 1; set { if (value) EntryKind = 1; } }
    public bool IsTransferKind { get => EntryKind == 2; set { if (value) EntryKind = 2; } }
    partial void OnEntryKindChanged(int value)
    {
        OnPropertyChanged(nameof(IsDepositKind)); OnPropertyChanged(nameof(IsWithdrawalKind)); OnPropertyChanged(nameof(IsTransferKind));
        if (value == 2 && SelectedTarget == null) SelectedTarget = Targets.FirstOrDefault();
    }
    public ObservableCollection<TransferTarget> Targets { get; } = [];
    [ObservableProperty] private TransferTarget? _selectedTarget;
    [ObservableProperty] private DateTime? _entryDate = DateTime.Today;
    [ObservableProperty] private string _entryAmount = "";
    [ObservableProperty] private string _entryDescription = "";
    [ObservableProperty] private string _entryError = "";
    [ObservableProperty] private BankTransaction? _editingTx;
    public bool IsEditingTx => EditingTx != null;
    /// <summary>Editing the receiving side of a transfer between own accounts: only date, amount and description change.</summary>
    public bool KindLocked => EditingTx is { Incoming: true, IsInternal: true };
    public bool KindFree => !KindLocked;
    public string EntryButton => Loc.T(EditingTx == null ? "Bank.AddMovement" : "Bank.SaveMovement");
    partial void OnEditingTxChanged(BankTransaction? value)
    {
        OnPropertyChanged(nameof(IsEditingTx)); OnPropertyChanged(nameof(EntryButton));
        OnPropertyChanged(nameof(KindLocked)); OnPropertyChanged(nameof(KindFree));
    }

    // ---------------------------------------------------------------- saved descriptions (the arrow and + inside the box)
    public ObservableCollection<string> SavedDescriptions { get; } = [];
    public bool HasSavedDescriptions => SavedDescriptions.Count > 0;
    [ObservableProperty] private bool _isDescriptionListOpen;
    [ObservableProperty] private bool _isNewDescriptionOpen;
    [ObservableProperty] private string _newDescription = "";

    [RelayCommand]
    private void ToggleDescriptionList() { IsNewDescriptionOpen = false; IsDescriptionListOpen = !IsDescriptionListOpen; }

    [RelayCommand]
    private void UseDescription(string? text)
    {
        if (text == null) return;
        EntryDescription = text;
        IsDescriptionListOpen = false;
    }

    [RelayCommand]
    private void OpenNewDescription()
    {
        IsDescriptionListOpen = false;
        NewDescription = EntryDescription.Trim();
        IsNewDescriptionOpen = true;
    }

    [RelayCommand]
    private void SaveDescription()
    {
        var t = NewDescription.Trim();
        if (t.Length == 0) return;
        if (_book.AddDescription(t))
        {
            if (!Save()) { _book.Descriptions.Remove(t); return; }
            SavedDescriptions.Add(t);
            OnPropertyChanged(nameof(HasSavedDescriptions));
            Status = Loc.F("Bank.DescriptionSaved", t);
        }
        EntryDescription = t;
        IsNewDescriptionOpen = false;
    }

    [RelayCommand]
    private void CancelNewDescription() => IsNewDescriptionOpen = false;

    [RelayCommand]
    private void RemoveDescription(string? text)
    {
        if (text == null || !_book.Descriptions.Remove(text)) return;
        if (!Save()) { _book.Descriptions.Add(text); return; }
        SavedDescriptions.Remove(text);
        OnPropertyChanged(nameof(HasSavedDescriptions));
    }

    public void Refresh()
    {
        var entity = main.ActiveEntity;
        if (entity == null) return;
        var folder = DriveSync.DataFolder(entity);
        try { BankBook.Migrate(folder); } catch { }
        var path = BankBook.PathIn(folder);
        var keep = Selected?.Account.Id;
        try { _book = BankBook.Load(path); _path = path; Status = ""; }
        catch (Exception ex)
        {
            // never overwrite a file that could not be read
            _book = new BankBook(); _path = "";
            Status = Loc.F("Bank.LoadFailed", ex.Message);
        }
        SavedDescriptions.Clear();
        foreach (var d in _book.Descriptions) SavedDescriptions.Add(d);
        OnPropertyChanged(nameof(HasSavedDescriptions));
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
        BuildTargets();
        BuildStatement();
    }

    [RelayCommand]
    private void Select(BankCardViewModel? card) { if (card != null) Selected = card; }

    /// <summary>Transfer destinations for the selected account: outside, then the other accounts of the same currency.</summary>
    private void BuildTargets()
    {
        var keep = SelectedTarget?.Account?.Id;
        Targets.Clear();
        Targets.Add(new TransferTarget(null, Loc.T("Bank.ToExternal")));
        if (Selected != null)
            foreach (var a in _book.Accounts.Where(a => a != Selected.Account && string.Equals(a.Currency, Selected.Account.Currency, StringComparison.OrdinalIgnoreCase)))
                Targets.Add(new TransferTarget(a, $"{a.Bank} {a.ShortNumber}".Trim()));
        SelectedTarget = Targets.FirstOrDefault(t => t.Account?.Id == keep && keep != null) ?? Targets[0];
    }

    private void UpdateTotals()
    {
        Totals.Clear();
        foreach (var (cur, total, n) in _book.TotalsByCurrency())
            Totals.Add(new CurrencyTotal(cur, BankBook.Money(total), Loc.F(n == 1 ? "Bank.OneAccount" : "Bank.NAccounts", n), total < 0));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(NoAccounts));
    }

    private BankStatement? _statement;

    private static string D(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string TypeKeyOf(BankTransaction t) => t.Kind switch { BankTxKind.Deposit => "in", BankTxKind.Withdrawal => "out", _ => "transfer" };
    private static string TypeOf(BankTransaction t) => Loc.T(t.Kind switch { BankTxKind.Deposit => "Bank.Deposit", BankTxKind.Withdrawal => "Bank.Withdrawal", _ => "Bank.Transfer" });

    /// <summary>The description, with the other account of a transfer between own accounts ("→ CIB •••• 1234").</summary>
    private string DescriptionOf(BankTransaction t)
    {
        if (t.Kind != BankTxKind.Transfer) return t.Description;
        string where = t.IsInternal && _book.Find(t.Counterparty) is { } other
            ? Loc.F(t.Incoming ? "Bank.FromAccount" : "Bank.ToAccount", $"{other.Bank} {other.ShortNumber}".Trim())
            : Loc.T("Bank.ToExternal");
        return t.Description.Length == 0 ? where : $"{t.Description} · {where}";
    }

    private void BuildStatement()
    {
        Rows.Clear();
        _statement = null;
        if (Selected == null) return;
        var s = _statement = BankBook.Statement(Selected.Account, From, To, OnlyKind);
        string cur = " " + s.Account.Currency;
        OpeningText = BankBook.Money(s.Opening) + cur;
        InText = "+" + BankBook.Money(s.TotalIn) + cur;
        OutText = "−" + BankBook.Money(s.TotalOut) + cur;
        ClosingText = BankBook.Money(s.Closing) + cur;
        ClosingNegative = s.Closing < 0;
        MovementsText = Loc.F("Bank.MovementsCount", s.Count);
        PeriodText = PeriodLabel(s);
        // like a bank statement: the opening balance first, then the movements from the oldest down to the newest
        Rows.Add(new StatementRow(null, D(s.From ?? s.Account.OpeningDate), "", "", Loc.T(s.From == null ? "Bank.OpeningBalance" : "Bank.BroughtForward"),
                                  "", "", BankBook.Money(s.Opening), s.Opening < 0, true));
        foreach (var l in s.Lines)
            Rows.Add(new StatementRow(l.Tx, D(l.Tx.Date), TypeOf(l.Tx), TypeKeyOf(l.Tx), DescriptionOf(l.Tx),
                                      l.Tx.IsIn ? BankBook.Money(l.Tx.Amount) : "", l.Tx.IsIn ? "" : BankBook.Money(l.Tx.Amount),
                                      BankBook.Money(l.Balance), l.Balance < 0, false));
    }

    private string PeriodLabel(BankStatement s)
    {
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
        foreach (var c in Cards) c.Update();      // a transfer changes two cards
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
        if (f.Editing != null && !string.Equals(acc.Currency, cur, StringComparison.OrdinalIgnoreCase))
        {
            if (acc.Transactions.Any(t => t.IsInternal)) { f.Error = Loc.T("Bank.CurrencyLinked"); return; }
            if (acc.Transactions.Count > 0 && !Dialogs.Confirm(Loc.F("Bank.CurrencyChange", acc.Currency, cur))) return;
        }
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
        else { AfterChange(); BuildTargets(); }
        Status = Loc.T("Bank.Saved");
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        var c = Selected;
        if (c == null) return;
        int linked = c.Account.Transactions.Count(t => t.IsInternal);
        var ask = Loc.F("Bank.DeleteAccountAsk", c.Account.Bank, c.Account.Number, c.Account.Transactions.Count);
        if (linked > 0) ask += "\n\n" + Loc.F("Bank.DeleteAccountLinked", linked);
        if (!Dialogs.Confirm(ask)) return;
        // the other side of its transfers goes too (the money never arrived from / left to a deleted account)
        var removed = new List<(BankAccount, BankTransaction, int)>();
        foreach (var t in c.Account.Transactions.Where(t => t.IsInternal).ToList())
            if (_book.Partner(t) is { } p) { removed.Add((p.Account, p.Tx, p.Account.Transactions.IndexOf(p.Tx))); p.Account.Transactions.Remove(p.Tx); }
        int i = _book.Accounts.IndexOf(c.Account);
        _book.Accounts.Remove(c.Account);
        if (!Save()) { _book.Accounts.Insert(Math.Max(0, i), c.Account); BankBook.Restore(removed); return; }
        int ci = Cards.IndexOf(c);
        Cards.Remove(c);
        Selected = Cards.Count == 0 ? null : Cards[Math.Min(ci, Cards.Count - 1)];
        AfterChange();
    }

    [RelayCommand]
    private void CopyNumber()
    {
        if (Selected == null || Selected.Account.Number.Length == 0) return;
        try { Clipboard.SetText(Selected.Account.Number); Status = Loc.T("Bank.NumberCopied"); } catch { }
    }

    // ---------------------------------------------------------------- movements
    [RelayCommand]
    private void SubmitEntry()
    {
        EntryError = "";
        if (Selected == null) return;
        if (!TryAmount(EntryAmount, allowNegative: false, allowZero: false, out var amount)) { EntryError = Loc.T("Bank.BadAmount"); return; }
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var date = (EntryDate ?? DateTime.Today).Date;
        var desc = EntryDescription.Trim();
        var acc = Selected.Account;
        var kind = EntryKind switch { 0 => BankTxKind.Deposit, 1 => BankTxKind.Withdrawal, _ => BankTxKind.Transfer };
        var target = kind == BankTxKind.Transfer ? SelectedTarget?.Account : null;

        if (EditingTx is { } tx && KindLocked)
        {
            // receiving side of an own transfer: both sides take the new date / amount / description
            var partner = _book.Partner(tx);
            var old = (tx.Date, tx.Amount, tx.Description);
            void Apply(BankTransaction t, (DateTime, decimal, string) v) => (t.Date, t.Amount, t.Description) = v;
            Apply(tx, (date, amount, desc));
            if (partner is { } p0) Apply(p0.Tx, (date, amount, desc));
            if (!Save()) { Apply(tx, old); if (partner is { } p1) Apply(p1.Tx, old); return; }
            Status = Loc.T("Bank.MovementSaved");
        }
        else if (EditingTx is { } etx)
        {
            // replace the movement (and the other side of a transfer) by the new one, keeping its place in the day
            var removed = _book.Remove(acc, etx);
            var added = Create(acc, kind, target, date, amount, desc);
            added.Seq = etx.Seq;
            if (!Save())
            {
                _book.Remove(acc, added);
                BankBook.Restore(removed);
                return;
            }
            Status = Loc.T("Bank.MovementSaved");
        }
        else
        {
            var added = Create(acc, kind, target, date, amount, desc);
            if (!Save()) { _book.Remove(acc, added); return; }
            Status = kind switch
            {
                BankTxKind.Deposit => Loc.F("Bank.DepositAdded", BankBook.Money(amount), acc.Currency),
                BankTxKind.Withdrawal => Loc.F("Bank.WithdrawalAdded", BankBook.Money(amount), acc.Currency),
                _ => Loc.F("Bank.TransferAdded", BankBook.Money(amount), acc.Currency, SelectedTarget?.Name ?? ""),
            };
        }
        // keep date and kind for the next entry (several movements of the same day are typed in a row)
        EntryAmount = "";
        EntryDescription = "";
        EditingTx = null;
        AfterChange();
    }

    private BankTransaction Create(BankAccount acc, BankTxKind kind, BankAccount? target, DateTime date, decimal amount, string desc) =>
        kind == BankTxKind.Transfer ? _book.Transfer(acc, target, date, amount, desc) : _book.Add(acc, kind, date, amount, desc);

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
        EntryKind = tx.Kind switch { BankTxKind.Deposit => 0, BankTxKind.Withdrawal => 1, _ => 2 };
        if (tx.Kind == BankTxKind.Transfer && !tx.Incoming)
            SelectedTarget = Targets.FirstOrDefault(t => tx.IsInternal ? t.Account?.Id == tx.Counterparty : t.Account == null) ?? Targets.FirstOrDefault();
        EntryDate = tx.Date;
        EntryAmount = BankBook.Money(tx.Amount);
        EntryDescription = tx.Description;
        EntryError = "";
    }

    [RelayCommand]
    private void DeleteMovement(StatementRow? row)
    {
        if (row?.Tx is not { } tx || Selected == null) return;
        var ask = Loc.F("Bank.DeleteMovementAsk", TypeOf(tx), BankBook.Money(tx.Amount), Selected.Account.Currency, D(tx.Date));
        if (tx.IsInternal) ask += "\n\n" + Loc.T("Bank.DeleteTransferBoth");
        if (!Dialogs.Confirm(ask)) return;
        var removed = _book.Remove(Selected.Account, tx);
        if (!Save()) { BankBook.Restore(removed); return; }
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
        var accent = Selected.BrandHex
                     ?? ((Application.Current.TryFindResource("BrandBrush") as SolidColorBrush)?.Color.ToString() is { } hex && hex.Length == 9 ? "#" + hex[3..] : "#2F80ED");
        var pdf = new StatementPdfData(
            LanguageService.CurrentFlow == FlowDirection.RightToLeft, entity, Loc.T("Bank.StatementTitle"),
            $"{Loc.T("Bank.Printed")} {DateTime.Now.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture)}",
            s.Account.Bank, s.Account.Number, s.Account.Currency,
            Loc.T("Bank.Period"), PeriodText, Loc.T("Bank.TypeFilter"), Types[Math.Clamp(TypeIndex, 0, 3)], MovementsText,
            Loc.T("Bank.Opening"), OpeningText, Loc.T("Bank.MoneyIn"), InText, Loc.T("Bank.MoneyOut"), OutText, Loc.T("Bank.Closing"), ClosingText, ClosingNegative,
            Loc.T("Bank.Date"), Loc.T("Bank.Type"), Loc.T("Bank.Description"), Loc.T("Bank.MoneyIn"), Loc.T("Bank.MoneyOut"), Loc.T("Bank.Balance"),
            Rows[0].Date, Rows[0].Description, Rows[0].Balance,
            Rows.Where(r => r.IsMovement).Select(r => new StatementPdfLine(r.Date, r.Type, r.TypeKey switch { "in" => "dep", "out" => "wd", _ => "tr" },
                                                                         r.Description, r.In, r.Out, r.Balance, r.NegativeBalance)).ToList(),
            accent, $"DEEPMATH · {entity}");
        var words = new StatementWords(Loc.T("Bank.StatementTitle"), Loc.T("Bank.Entity"), Loc.T("Bank.Bank"), Loc.T("Bank.AccountNumber"),
            Loc.T("Bank.Currency"), Loc.T("Bank.Period"), Loc.T("Bank.Printed"), Loc.T("Bank.Date"), Loc.T("Bank.Description"),
            Loc.T("Bank.MoneyIn"), Loc.T("Bank.MoneyOut"), Loc.T("Bank.Balance"), Loc.T("Bank.Opening"), Loc.T("Bank.MoneyIn"),
            Loc.T("Bank.MoneyOut"), Loc.T("Bank.Closing"), Loc.T(s.From == null ? "Bank.OpeningBalance" : "Bank.BroughtForward"), Loc.T("Bank.Movements"));
        var period = PeriodText;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Bank Statements");
        var name = Safe($"{s.Account.Bank} {s.Account.ShortNumber.Replace("•", "").Trim()} Statement {DateTime.Today:yyyy-MM-dd}") + ".pdf";
        var dest = BankAccountPdf.FreePath(folder, name);
        var tmp = Path.Combine(Path.GetTempPath(), "DEEPMATH Statement " + Guid.NewGuid().ToString("N")[..8] + ".xlsx");
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                if (BankStatementPdf.TryExport(pdf, dest)) return;
                // no Edge on this PC: the Excel layout
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
            ("البنك الأهلي المصري", "1002 3344 5566 7788", "EGP", 350_000m),
            ("Al Rajhi Bank", "SA03 8000 0000 6080 1016 7519", "SAR", 95_000m),
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
        var usd2 = new BankAccount { Bank = "Demo Savings Bank", Number = "US64 SVBK US6S 3300 9581 22", Currency = "USD", OpeningBalance = 5_000m, OpeningDate = today.AddMonths(-4) };
        book.Accounts.Add(usd2);
        book.Transfer(book.Accounts[0], usd2, today.AddDays(-10), 2_500m, "Monthly saving");
        book.Transfer(book.Accounts[0], null, today.AddDays(-4), 1_200m, "Demo Logistics");
        book.AddDescription("Bank charges");
        book.AddDescription("Office rent");
        try { book.Save(path); } catch { }
    }

    // ---------------------------------------------------------------- helpers
    private static string Compact(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>An amount box when it loses focus: "20800" → "20,800.00" (left as typed when it is not a number).</summary>
    internal static string FormatAmount(string? text, bool allowNegative) =>
        TryAmount(text, allowNegative, allowZero: true, out var v) ? BankBook.Money(v) : text ?? "";

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
