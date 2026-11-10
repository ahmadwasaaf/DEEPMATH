using System.Collections.ObjectModel;
using BatchStudio.App.Services;
using BatchStudio.Core.Invoices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>
/// The "new supplier / client" form (New Invoice "+", Mass Invoice "+"): the sheet's columns, the supplier's bank accounts
/// and theme. Saving adds the record to the Suppliers / Clients sheet (and the theme to CLIENT THEME MEMORY).
/// </summary>
public sealed partial class PartyFormViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<object> Fields { get; } = [];
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isOpen;
    public bool IsClients { get; private set; }

    private readonly Dictionary<string, string> _bankValues = new(StringComparer.OrdinalIgnoreCase);
    private ThemeFieldViewModel? _theme;
    private string _status = "";

    /// <summary>Raised after Save (name of the new record) or Cancel (null).</summary>
    public event Action<string?>? Closed;

    /// <summary>Status line to show after a save ("added" or the sheet's saving status).</summary>
    public string LastStatus => _status;

    private DataSetViewModel? Page => main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == (IsClients ? "clients" : "suppliers"));

    public static bool IsLongField(string h) =>
        h.Contains("Address", StringComparison.OrdinalIgnoreCase) || h.Contains("Bank", StringComparison.OrdinalIgnoreCase) ||
        h.Contains("Activit", StringComparison.OrdinalIgnoreCase);

    public static string Clean(string? v) => (v ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Opens an empty form (same columns as the sheet); category and template are pre-filled when given.</summary>
    public bool Open(bool isClients, string? category, string? template)
    {
        IsClients = isClients;
        var page = Page;
        if (page == null) return false;
        var headers = page.Headers;
        Fields.Clear();
        _bankValues.Clear();
        _theme = null;
        Error = "";
        Title = Loc.T(isClients ? "Inv.NewClient" : "Inv.NewSupplier");
        bool suppliers = !isClients;
        BankGroupViewModel? banks = null;
        NewFieldViewModel? nameField = null, templateField = null;
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (suppliers && SupplierBanks.IsBankHeader(headers, i))
            {
                // bank accounts (1, + more) as on the Suppliers page
                if (banks == null)
                {
                    banks = new BankGroupViewModel(headers, k => _bankValues.TryGetValue(k, out var v) ? v : "",
                        (k, v) => _bankValues[k] = v, expanded: true);
                    Fields.Add(banks);
                }
                continue;
            }
            var options = main.OptionsFor(page.Session.Definition.Key, h, []);
            string value = "";
            if (i == 0) value = page.NextId();
            else if (options != null && h.Contains("Category", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(category)) value = category;
            else if (options != null && h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(template)) value = template;
            if (options != null && value.Length > 0 && !options.Contains(value)) options = [value, .. options];
            var field = new NewFieldViewModel { Caption = h, Options = options, Value = value, Index = i, IsLong = IsLongField(h) };
            Fields.Add(field);
            if (i == 1) nameField = field;
            // suppliers: the theme comes right after DEFAULT TEMPLATE (saved with the new supplier)
            if (suppliers && h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))
            {
                templateField = field;
                Fields.Add(_theme = new ThemeFieldViewModel(main, "", value, deferred: true));
            }
        }
        if (suppliers && _theme == null) Fields.Add(_theme = new ThemeFieldViewModel(main, "", template ?? "INVOICE", deferred: true));
        if (_theme is { } theme && templateField != null)
            templateField.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NewFieldViewModel.Value)) _ = theme.UpdateAsync(nameField?.Value ?? "", templateField.Value);
            };
        IsOpen = true;
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        Reset();
        Closed?.Invoke(null);
    }

    private void Reset()
    {
        IsOpen = false;
        Fields.Clear();
        _bankValues.Clear();
        _theme = null;
        Error = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var page = Page;
        if (page == null || IsBusy) return;
        Error = "";
        var fields = Fields.OfType<NewFieldViewModel>().ToList();
        var headers = page.Headers;
        int nameCol = headers.FindIndex(h => h.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                                             !h.Contains("Chinese", StringComparison.OrdinalIgnoreCase) && !h.Contains("Native", StringComparison.OrdinalIgnoreCase));
        if (nameCol < 0) nameCol = 1;
        string ValueAt(int index) => fields.FirstOrDefault(f => f.Index == index) is { } f ? Clean(f.Value) : "";
        var id = ValueAt(0);
        var name = ValueAt(nameCol);
        if (id.Length == 0 || name.Length == 0) { Error = Loc.F("Inv.PartyRequired", headers[0], headers[nameCol]); return; }
        var existing = IsClients ? InvoiceService.Clients(page.Session.Current) : InvoiceService.Suppliers(page.Session.Current);
        if (existing.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        { Error = Loc.F("Inv.PartyExists", name); return; }
        if (page.Session.Current.Rows.Any(r => string.Equals(r[0], id, StringComparison.OrdinalIgnoreCase)))
        { Error = Loc.F("Inv.PartyIdExists", id); return; }

        // extra bank accounts may need new columns
        foreach (var (header, value) in _bankValues)
            if (value.Trim().Length > 0 || SupplierBanks.IsDefaultHeader(header)) page.EnsureColumn(header);
        headers = page.Headers;
        var values = new string[headers.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ValueAt(i);
            if (_bankValues.TryGetValue(headers[i], out var bank)) values[i] = Clean(bank);
        }
        string template = fields.FirstOrDefault(f => f.Caption.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))?.Value ?? "INVOICE";

        IsBusy = true;
        try
        {
            page.AddRecord(values);
            await page.SaveAsync();
            // the theme picked in the form goes to CLIENT THEME MEMORY with the new supplier
            if (!IsClients && _theme != null) await _theme.FlushAsync(name, template);
            _status = page.IsDirty ? page.Status : Loc.F("Inv.PartyAdded", name);
        }
        catch (Exception ex) { _status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }

        Reset();
        Closed?.Invoke(name);
    }
}
