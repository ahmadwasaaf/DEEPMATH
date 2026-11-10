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
    public string Caption => Header;
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
            _group.RaiseChanged();
        }
    }

    /// <summary>The account printed on invoices (radio button).</summary>
    public bool IsDefault
    {
        get => _group.DefaultNumber == Number;
        set { if (value) _group.SetDefault(Number); }
    }

    public bool IsVisible => _group.IsExpanded || IsFirst;
    public bool CanRemove => !IsFirst;

    [RelayCommand]
    private void Remove() => _group.Remove(this);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsFirst));
        OnPropertyChanged(nameof(CanRemove));
    }
}

/// <summary>
/// The bank accounts of a supplier (Banking Details 1, 2, 3 ...): the arrow shows / hides the extra accounts, "+" adds one,
/// the radio button picks the account printed on invoices (DEFAULT BANK). Reads and writes through the record's columns,
/// so the same group works on the Suppliers page (a table row) and in New Invoice (a copy of the record).
/// </summary>
public sealed partial class BankGroupViewModel : ObservableObject
{
    private readonly Func<string, string> _get;
    private readonly Action<string, string> _set;

    public ObservableCollection<BankAccountViewModel> Accounts { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    public int DefaultNumber { get; private set; } = 1;
    public int FirstNumber => Accounts.Count > 0 ? Accounts[0].Number : 1;
    public bool HasMany => Accounts.Count > 1;
    public string CountText => Accounts.Count > 1 ? Loc.F("Bank.Count", Accounts.Count) : "";
    public string DefaultText => Loc.F("Bank.DefaultIs", DefaultNumber);

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
        _isExpanded = expanded && Accounts.Count > 1;
    }

    internal string Get(string header) => _get(header);
    internal void Set(string header, string value) => _set(header, value);
    internal void RaiseChanged() => Changed?.Invoke();

    partial void OnIsExpandedChanged(bool value)
    {
        foreach (var a in Accounts) a.Refresh();
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>"+": a new empty account with the next free number, shown and ready to type in.</summary>
    [RelayCommand]
    private void Add()
    {
        int n = 2;
        while (Accounts.Any(a => a.Number == n)) n++;
        if (n > SupplierBanks.MaxAccounts) return;
        // the DEFAULT BANK column comes before the extra account columns in the sheet
        _set(SupplierBanks.DefaultHeader, _get(SupplierBanks.DefaultHeader));
        var header = SupplierBanks.HeaderFor(n);
        _set(header, _get(header));
        var account = new BankAccountViewModel(this, n, header);
        int at = 0;
        while (at < Accounts.Count && Accounts[at].Number < n) at++;
        Accounts.Insert(at, account);
        IsExpanded = true;
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
