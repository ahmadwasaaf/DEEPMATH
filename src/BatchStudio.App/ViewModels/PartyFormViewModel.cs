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

    /// <summary>The name typed is already saved (this record's ID): Save offers to update that record instead.</summary>
    [ObservableProperty] private string _existingId = "";
    public bool HasExisting => ExistingId.Length > 0;
    public string UpdateExistingText => Loc.F("Party.UpdateExisting", ExistingId);
    partial void OnExistingIdChanged(string value) { OnPropertyChanged(nameof(HasExisting)); OnPropertyChanged(nameof(UpdateExistingText)); }

    /// <summary>Error for a name already saved: the message and the "Update SUP-004 with these details" button.</summary>
    private void NameTaken(DataSetViewModel page, string name, string? exceptId)
    {
        var id = page.SameNameId(name, exceptId)
                 ?? (IsClients ? InvoiceService.Clients(page.Session.Current) : InvoiceService.Suppliers(page.Session.Current))
                    .Where(p => !string.Equals(p.Id, exceptId, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        ExistingId = id == "?" ? "" : id;
        Error = ExistingId.Length > 0 ? Loc.F("Party.ExistsUpdate", name, ExistingId) : Loc.F("Inv.PartyExists", name);
    }

    /// <summary>The form's values in the sheet's column order (new bank columns added when needed).</summary>
    private string[] FormValues(DataSetViewModel page)
    {
        foreach (var (header, value) in _bankValues)
            if (value.Trim().Length > 0 || SupplierBanks.IsDefaultHeader(header)) page.EnsureColumn(header);
        var headers = page.Headers;
        var values = new string[headers.Count];
        for (int i = 0; i < values.Length; i++) values[i] = i < _original.Length ? _original[i] ?? "" : "";
        foreach (var f in Fields.OfType<NewFieldViewModel>()) if (f.Index < values.Length) values[f.Index] = Clean(f.Value);
        for (int i = 0; i < values.Length; i++)
            if (_bankValues.TryGetValue(headers[i], out var bank)) values[i] = Clean(bank);
        return values;
    }

    /// <summary>
    /// "Update SUP-004 with these details": a side-by-side review of what differs (the saved values chosen by default);
    /// only what the user picks changes, a new bank paragraph is added as another account. Theme, ID and name never change.
    /// Editing a second copy: the two become one (the lower ID stays).
    /// </summary>
    [RelayCommand]
    private async Task UpdateExistingAsync()
    {
        var page = Page;
        if (page == null || IsBusy || ExistingId.Length == 0) return;
        var other = ExistingId;
        string keepId = other, dropId = "";
        if (_editId != null && !string.Equals(_editId, other, StringComparison.OrdinalIgnoreCase))
        {
            static int Num(string id) => int.TryParse(new string(id.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue;
            (keepId, dropId) = Num(_editId) < Num(other) ? (_editId, other) : (other, _editId);
        }
        var newer = FormValues(page);
        var keep = page.RecordValues(keepId);
        var existingName = page.RecordValues(other) is { } ov && ov.Length > 1 ? ov[1] : "";
        if (keep == null) { Error = Loc.F("Inv.PartyExists", existingName); return; }
        // the differences only, side by side; the saved values stay unless the user picks the form's
        var rows = page.MergeRows(keep, newer);
        int nameCol = NameColumn(page.Headers);
        if (rows.Count == 0 && dropId.Length == 0)
        {
            _status = Loc.F("Party.NoChanges", keepId);
            Reset();
            Closed?.Invoke(existingName);                     // nothing to change: just use the saved record
            return;
        }
        if (rows.Count == 0 && !Dialogs.Confirm(Loc.F("Merge.NoDiff", dropId, keepId))) return;
        if (rows.Count > 0 && !Views.MergeReviewDialog.Review(Loc.F("Party.ReviewTitle", keepId, existingName), Loc.F("Party.ReviewText", keepId),
                                                             Loc.F("Party.UpdateExisting", keepId), rows))
            return;
        var merged = page.ApplyMerge(keep, rows);
        if (existingName.Length > 0 && nameCol < merged.Length) merged[nameCol] = existingName;   // the name already used by invoices
        var name = nameCol < merged.Length ? merged[nameCol] : existingName;
        IsBusy = true;
        try
        {
            page.UpdateRecord(keepId, merged);
            if (dropId.Length > 0) page.DeleteRecord(dropId);
            await page.SaveAsync();
            _status = page.IsDirty ? page.Status : Loc.F("Party.Updated", keepId, name);
        }
        catch (Exception ex) { _status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
        Reset();
        Closed?.Invoke(name);
    }

    private readonly Dictionary<string, string> _bankValues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Editing an existing record (its ID and values as loaded); null = a new record.</summary>
    private string? _editId;
    private string[] _original = [];
    public bool IsEdit => _editId != null;
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
        _editId = null;
        _original = [];
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

    /// <summary>Opens an existing supplier / client to check or change its details (Save writes them to the sheet).</summary>
    public bool OpenExisting(bool isClients, string id)
    {
        IsClients = isClients;
        var page = Page;
        if (page == null || page.RecordValues(id) is not { } values) return false;
        var headers = page.Headers;
        Fields.Clear();
        _bankValues.Clear();
        _theme = null;
        Error = "";
        _editId = id;
        _original = values;
        bool suppliers = !isClients;
        int nameCol = NameColumn(headers);
        Title = nameCol < values.Length ? values[nameCol] : id;
        for (int i = 1; i < headers.Count && i < values.Length; i++)
        {
            var h = headers[i];
            if (suppliers && SupplierBanks.IsBankHeader(headers, i))
            {
                if (!Fields.OfType<BankGroupViewModel>().Any())
                {
                    for (int j = 0; j < headers.Count && j < values.Length; j++)
                        if (SupplierBanks.IsBankHeader(headers, j)) _bankValues[headers[j]] = values[j];
                    Fields.Add(new BankGroupViewModel(headers, k => _bankValues.TryGetValue(k, out var v) ? v : "",
                        (k, v) => _bankValues[k] = v, expanded: false));
                }
                continue;
            }
            var options = main.OptionsFor(page.Session.Definition.Key, h, []);
            if (options != null && values[i].Length > 0 && !options.Contains(values[i])) options = [values[i], .. options];
            Fields.Add(new NewFieldViewModel { Caption = h, Options = options, Value = values[i], Index = i, IsLong = IsLongField(h) });
            // suppliers: the theme right after DEFAULT TEMPLATE, saved at once (like the Suppliers page)
            if (suppliers && h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))
                Fields.Add(_theme = new ThemeFieldViewModel(main, Title, values[i]));
        }
        if (suppliers && _theme == null) Fields.Add(_theme = new ThemeFieldViewModel(main, Title, "INVOICE"));
        IsOpen = true;
        return true;
    }

    private static int NameColumn(IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
            if (headers[i].Contains("Name", StringComparison.OrdinalIgnoreCase) && !headers[i].Contains("Chinese", StringComparison.OrdinalIgnoreCase)
                && !headers[i].Contains("Native", StringComparison.OrdinalIgnoreCase)) return i;
        return 1;
    }

    private async Task SaveExistingAsync(DataSetViewModel page)
    {
        var values = (string[])_original.Clone();
        foreach (var f in Fields.OfType<NewFieldViewModel>()) if (f.Index < values.Length) values[f.Index] = Clean(f.Value);
        // bank accounts: new columns (a second account ...) are added to the sheet
        foreach (var (header, value) in _bankValues)
        {
            if (page.Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase)) < 0 &&
                value.Trim().Length == 0 && !SupplierBanks.IsDefaultHeader(header)) continue;
            page.EnsureColumn(header);
            int col = page.Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
            if (col >= values.Length)
            {
                int old = values.Length;
                Array.Resize(ref values, page.Headers.Count);
                for (int k = old; k < values.Length; k++) values[k] = "";
            }
            values[col] = Clean(value);
        }
        var headers = page.Headers;
        int nameCol = NameColumn(headers);
        var name = nameCol < values.Length ? values[nameCol] : "";
        if (name.Length == 0) { Error = Loc.F("Inv.PartyRequired", headers[0], headers[nameCol]); return; }
        var others = (IsClients ? InvoiceService.Clients(page.Session.Current) : InvoiceService.Suppliers(page.Session.Current))
            .Where(p => !string.Equals(p.Id, _editId, StringComparison.OrdinalIgnoreCase));
        if (others.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) || page.SameNameId(name, _editId) != null)
        { NameTaken(page, name, _editId); return; }

        IsBusy = true;
        try
        {
            page.UpdateRecord(_editId!, values);
            await page.SaveAsync();
            _status = page.IsDirty ? page.Status : Loc.F("Inv.PartySaved", name);
        }
        catch (Exception ex) { _status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
        Reset();
        Closed?.Invoke(name);
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
        _editId = null;
        _original = [];
        Fields.Clear();
        _bankValues.Clear();
        _theme = null;
        Error = "";
        ExistingId = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var page = Page;
        if (page == null || IsBusy) return;
        Error = "";
        ExistingId = "";
        if (_editId != null) { await SaveExistingAsync(page); return; }
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
        if (existing.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) || page.SameNameId(name) != null)
        { NameTaken(page, name, null); return; }
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
