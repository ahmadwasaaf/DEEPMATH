using System.Collections.ObjectModel;
using BatchStudio.App.Services;
using BatchStudio.Core.Invoices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>One bank account of a supplier in the details panel.</summary>
public sealed partial class BankAccountViewModel : ObservableObject
{
    private readonly BankGroupViewModel _group;
    public int Number { get; }
    public string Header { get; }
    /// <summary>"Banking Details (full paragraph) 1" for the first account (display only; the sheet header has no number).</summary>
    public string Caption => Number == 1 && SupplierBanks.ExtraNumber(Header) == 0 ? Header + " 1" : Header;
    public bool IsFirst => Number == _group.FirstNumber;

    public BankAccountViewModel(BankGroupViewModel group, int number, string header)
    {
        _group = group;
        Number = number;
        Header = header;
    }

    public string Value
    {
        get => _group.Get(Header);
        set
        {
            if (Value == value) return;
            _group.Set(Header, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Preview));
            _group.RaiseChanged();
        }
    }

    /// <summary>The account printed on invoices (radio button).</summary>
    public bool IsDefault
    {
        get => _group.DefaultNumber == Number;
        set { if (value) _group.SetDefault(Number); }
    }

    public bool CanRemove => !IsFirst;

    /// <summary>Each account opens / closes on its own (arrow), so one account can be read among several.</summary>
    [ObservableProperty] private bool _isCollapsed;

    /// <summary>One line shown while the account is closed (its first lines, joined).</summary>
    public string Preview
    {
        get
        {
            var lines = Value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.StartsWith("PAYMENT", StringComparison.OrdinalIgnoreCase)).Take(3);
            var text = string.Join("  ·  ", lines);
            return text.Length > 0 ? text : "—";
        }
    }

    [RelayCommand]
    private void Toggle() => IsCollapsed = !IsCollapsed;

    /// <summary>Copy: the paragraph to the clipboard in one click (the icon turns into a tick for a moment).</summary>
    [ObservableProperty] private bool _isCopied;

    [RelayCommand]
    private async Task Copy()
    {
        if (Value.Trim().Length == 0) return;
        try { System.Windows.Clipboard.SetText(Value); }
        catch { return; }   // the clipboard is busy (another program holds it)
        IsCopied = true;
        await Task.Delay(1500);
        IsCopied = false;
    }

    public bool CanExport => _group.Exporter != null;
    [ObservableProperty] private bool _isExporting;

    /// <summary>Export: this account as a PDF on the Desktop (the "Bank Account" sheet).</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (_group.Exporter == null || IsExporting || Value.Trim().Length == 0) return;
        IsExporting = true;
        try { await _group.Exporter(this); }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private void Remove() => _group.Remove(this);

    /// <summary>"+" on any account: a new account at the bottom.</summary>
    [RelayCommand]
    private void Add() => _group.AddCommand.Execute(null);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsFirst));
        OnPropertyChanged(nameof(CanRemove));
    }
}

/// <summary>
/// The bank accounts of a supplier (Banking Details 1, 2, 3 ...): each account's arrow opens / closes that account, "+" adds one
/// at the bottom,
/// the radio button picks the account printed on invoices (DEFAULT BANK). Reads and writes through the record's columns,
/// so the same group works on the Suppliers page (a table row) and in New Invoice (a copy of the record).
/// </summary>
public sealed partial class BankGroupViewModel : ObservableObject
{
    private readonly Func<string, string> _get;
    private readonly Action<string, string> _set;

    public ObservableCollection<BankAccountViewModel> Accounts { get; } = [];
    public int DefaultNumber { get; private set; } = 1;
    public int FirstNumber => Accounts.Count > 0 ? Accounts[0].Number : 1;
    public bool HasMany => Accounts.Count > 1;
    public string CountText => Accounts.Count > 1 ? Loc.F("Bank.Count", Accounts.Count) : "";
    public string DefaultText => Loc.F("Bank.DefaultIs", DefaultNumber);

    /// <summary>Exports one account as a PDF (suppliers page); null hides the Export icon.</summary>
    public Func<BankAccountViewModel, Task>? Exporter { get; set; }

    /// <summary>Raised when an account's text or the default account changes.</summary>
    public event Action? Changed;

    /// <param name="headers">Columns of the record.</param>
    /// <param name="get">Value of a column (by header; "" when the column does not exist yet).</param>
    /// <param name="set">Writes a column (by header), creating it when it is new.</param>
    public BankGroupViewModel(IReadOnlyList<string> headers, Func<string, string> get, Action<string, string> set, bool expanded)
    {
        _get = get;
        _set = set;
        var row = headers.Select(h => get(h)).ToList();
        var columns = SupplierBanks.Columns(headers);
        foreach (var account in SupplierBanks.Accounts(headers, row))
        {
            var match = columns.Where(c => c.Number == account.Number).Select(c => headers[c.Column]).FirstOrDefault();
            var header = match ?? SupplierBanks.HeaderFor(account.Number);
            Accounts.Add(new BankAccountViewModel(this, account.Number, header));
        }
        DefaultNumber = SupplierBanks.DefaultNumber(headers, row, SupplierBanks.Accounts(headers, row));
        // several accounts: the one printed on invoices is open, the others show one line (unless all are asked open)
        if (!expanded)
            foreach (var a in Accounts) a.IsCollapsed = Accounts.Count > 1 && a.Number != DefaultNumber;
    }

    internal string Get(string header) => _get(header);
    internal void Set(string header, string value) => _set(header, value);
    internal void RaiseChanged() => Changed?.Invoke();

    /// <summary>"+": a new empty account after the last one (always at the bottom), open and ready to type in.</summary>
    [RelayCommand]
    private void Add()
    {
        int n = Accounts.Count == 0 ? 2 : Math.Max(2, Accounts.Max(a => a.Number) + 1);
        if (n > SupplierBanks.MaxAccounts) return;
        // the DEFAULT BANK column comes before the extra account columns in the sheet
        _set(SupplierBanks.DefaultHeader, _get(SupplierBanks.DefaultHeader));
        var header = SupplierBanks.HeaderFor(n);
        _set(header, _get(header));
        Accounts.Add(new BankAccountViewModel(this, n, header));
        Notify();
    }

    internal void Remove(BankAccountViewModel account)
    {
        if (account.IsFirst) return;
        if (account.Value.Trim().Length > 0 && !Dialogs.Confirm(Loc.F("Bank.RemoveConfirm", account.Number))) return;
        _set(account.Header, "");
        Accounts.Remove(account);
        if (DefaultNumber == account.Number) SetDefault(FirstNumber);
        Notify();
        Changed?.Invoke();
    }

    internal void SetDefault(int number)
    {
        if (DefaultNumber == number) return;
        DefaultNumber = number;
        _set(SupplierBanks.DefaultHeader, number <= 1 ? "" : number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Notify();
        Changed?.Invoke();
    }

    private void Notify()
    {
        foreach (var a in Accounts) a.Refresh();
        OnPropertyChanged(nameof(HasMany));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(DefaultText));
        OnPropertyChanged(nameof(DefaultNumber));
        OnPropertyChanged(nameof(FirstNumber));
    }
}
