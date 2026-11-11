using System.Collections.ObjectModel;
using System.Globalization;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>Editable invoice line in the preview grid.</summary>
public sealed partial class LineViewModel : ObservableObject
{
    private readonly NewInvoiceViewModel _owner;
    public InvoiceLine Line { get; }

    public LineViewModel(NewInvoiceViewModel owner, InvoiceLine line, bool isLocked = false)
    {
        _owner = owner;
        Line = line;
        IsLocked = isLocked;
    }

    /// <summary>Smart Invoice Repair: rows restored from the archive are protected.</summary>
    public bool IsLocked { get; }

    public string Code { get => Line.Code; set { if (!IsLocked) Line.Code = value; OnPropertyChanged(); } }
    public string Description { get => Line.Description; set { if (!IsLocked) Line.Description = value; OnPropertyChanged(); } }

    public long Quantity
    {
        get => Line.Quantity;
        set { if (!IsLocked) Line.Quantity = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(Amount)); _owner.RecalcTotals(); }
    }

    public decimal UnitPrice
    {
        get => Line.UnitPrice;
        set
        {
            if (!IsLocked) Line.UnitCents = Math.Max(1, (long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Amount));
            _owner.RecalcTotals();
        }
    }

    public decimal Amount => Line.Amount;
}

/// <summary>One field of the "new supplier / client" form.</summary>
public sealed partial class NewFieldViewModel : ObservableObject
{
    public required string Caption { get; init; }
    /// <summary>Column of the field in the sheet (party details editor).</summary>
    public int Index { get; init; }
    public bool IsLong { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public bool IsChoice => Options != null;
    public bool IsText => Options == null;
    [ObservableProperty] private string _value = "";
}

public sealed class TemplateOption
{
    public required TemplateEntry Entry { get; init; }
    public string Name => Entry.Name;
    public override string ToString() => Name;
}

/// <summary>Create one invoice: choose template + party, generate exact lines, review, archive.</summary>
public sealed partial class NewInvoiceViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<TemplateOption> Templates { get; } = [];
    public ObservableCollection<Party> Parties { get; } = [];
    public ObservableCollection<CategoryInfo> Categories { get; } = [];
    public ObservableCollection<LineViewModel> Lines { get; } = [];

    private List<Party> _suppliers = [];
    private List<Party> _clients = [];
    private InvoiceDraft? _draft;

    [ObservableProperty] private TemplateOption? _selectedTemplate;
    [ObservableProperty] private bool _isClients;
    [ObservableProperty] private Party? _selectedParty;
    [ObservableProperty] private CategoryInfo? _selectedCategory;
    [ObservableProperty] private string _invoiceNo = "";
    [ObservableProperty] private DateTime? _date = DateTime.Today;
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _countText = "10";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasLines;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _partyDetails = "";
    /// <summary>All fields of the selected supplier / client, editable in the expanded details panel
    /// (<see cref="NewFieldViewModel"/>s and, for suppliers, the <see cref="BankGroupViewModel"/>).</summary>
    public ObservableCollection<object> PartyFields { get; } = [];
    private BankGroupViewModel? _partyBanks;
    private Dictionary<string, string> _partyExtra = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _partyExtraOriginal = new(StringComparer.OrdinalIgnoreCase);

    // ---- bank account of this invoice ("< Bank 2 >"): the supplier's default, switchable per invoice
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BankLabel), nameof(CanPrevBank), nameof(CanNextBank), nameof(BankTip))]
    private int _bankNumber = 1;
    public string BankLabel => Loc.F("Bank.Label", BankNumber);
    public bool HasManyBanks => !IsClients && SelectedParty is { HasManyBanks: true } && !IsRepair;
    public bool ShowBankPager => !IsClients;
    private List<int> BankNumbers => SelectedParty?.Banks.Where(b => b.Text.Trim().Length > 0).Select(b => b.Number).ToList() ?? [];
    public bool CanPrevBank => HasManyBanks && BankNumbers.IndexOf(BankNumber) > 0;
    public bool CanNextBank => HasManyBanks && BankNumbers.IndexOf(BankNumber) is var i && i >= 0 && i < BankNumbers.Count - 1;
    public string BankTip
    {
        get
        {
            if (SelectedParty?.BankText(BankNumber) is not { Length: > 0 } text) return Loc.T("Bank.None");
            var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var head = string.Join("\n", lines.Take(4));
            return (BankNumber == SelectedParty.DefaultBank ? Loc.T("Bank.IsDefault") + "\n" : "") + head + (lines.Length > 4 ? "\n…" : "");
        }
    }

    [RelayCommand]
    private void PrevBank() => StepBank(-1);

    [RelayCommand]
    private void NextBank() => StepBank(1);

    private void StepBank(int step)
    {
        var list = BankNumbers;
        int i = list.IndexOf(BankNumber) + step;
        if (i < 0 || i >= list.Count) return;
        BankNumber = list[i];
        // the generated items stay; the invoice just prints the other account
        if (_draft != null && _repair == null) _draft.Bank = SelectedParty?.BankText(BankNumber);
    }

    private void ResetBank()
    {
        BankNumber = SelectedParty?.DefaultBank ?? 1;
        OnPropertyChanged(nameof(HasManyBanks));
        OnPropertyChanged(nameof(ShowBankPager));
        OnPropertyChanged(nameof(CanPrevBank));
        OnPropertyChanged(nameof(CanNextBank));
        OnPropertyChanged(nameof(BankTip));
    }
    /// <summary>Supplier theme colour (CLIENT THEME MEMORY) for the selected template; null for clients.</summary>
    [ObservableProperty] private ThemeFieldViewModel? _partyTheme;
    [ObservableProperty] private bool _detailsExpanded;
    [ObservableProperty] private bool _partyDirty;
    public bool CanEditParty => PartyFields.Count > 0 && !IsRepair;
    private string[] _partyOriginal = [];
    private string? _partyId;
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private string _vatText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _differenceText = "";
    [ObservableProperty] private bool _isBalanced = true;
    [ObservableProperty] private bool _hasVat;
    [ObservableProperty] private string _words = "";
    [ObservableProperty] private string _templateInfo = "";
    [ObservableProperty] private string? _savedPath;
    [ObservableProperty] private bool _isAddingParty;
    private PartyFormViewModel? _partyForm;
    /// <summary>The new supplier / client form (fields, bank accounts, theme), shared with Mass Invoice.</summary>
    public PartyFormViewModel PartyForm
    {
        get
        {
            if (_partyForm != null) return _partyForm;
            _partyForm = new PartyFormViewModel(main);
            _partyForm.Closed += OnPartyFormClosed;
            return _partyForm;
        }
    }

    // Smart Invoice Repair
    private RepairSource? _repair;
    private string _repairNo = "";
    private bool _suspendClear;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormal), nameof(GenerateText), nameof(CanEditParty), nameof(HasManyBanks), nameof(CanPrevBank), nameof(CanNextBank))]
    private bool _isRepair;
    [ObservableProperty] private string _repairInfo = "";
    public bool IsNormal => !IsRepair;
    public string GenerateText => Loc.T(IsRepair ? "Rep.AddItems" : "Inv.Generate");

    public bool HasSaved => SavedPath != null;
    partial void OnSavedPathChanged(string? value) => OnPropertyChanged(nameof(HasSaved));

    private string Root => main.Root ?? throw new InvalidOperationException("No entity is open.");

    // ------------------------------------------------------------------ loading
    public void Refresh()
    {
        if (main.Root == null) return;
        bool suspend = IsRepair && !_suspendClear;
        if (suspend) _suspendClear = true;
        try { RefreshCore(); }
        finally { if (suspend) _suspendClear = false; }
        if (IsRepair) Status = "";
    }

    private void RefreshCore()
    {
        var keepTpl = SelectedTemplate?.Name;
        Templates.Clear();
        foreach (var e in TemplateRegistry.Scan(Root).Where(t => t.Map != null))
            Templates.Add(new TemplateOption { Entry = e });

        var sup = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers")?.Session.Current;
        var cli = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "clients")?.Session.Current;
        var cat = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "categories")?.Session.Current;
        _suppliers = sup != null ? InvoiceService.Suppliers(sup) : [];
        _clients = cli != null ? InvoiceService.Clients(cli) : [];
        Categories.Clear();
        if (cat != null) foreach (var c in InvoiceService.Categories(cat).Where(c => c.Active)) Categories.Add(c);

        SelectedTemplate = Templates.FirstOrDefault(t => t.Name == keepTpl) ?? Templates.FirstOrDefault();
        FillParties();
        if (string.IsNullOrEmpty(InvoiceNo)) NewNumber();
        Status = Templates.Count == 0 ? Loc.T("Inv.NoTemplates") : "";
    }

    partial void OnSelectedTemplateChanged(TemplateOption? value)
    {
        var map = value?.Entry.Map;
        if (map == null) { TemplateInfo = ""; return; }
        if (!_suspendClear) IsClients = map.EffectivePartyType == "CLIENTS";
        HasVat = map.VatRate is > 0;
        TemplateInfo = $"{(IsClients ? Loc.T("Nav.Clients") : Loc.T("Nav.Suppliers"))} · {map.EffectiveCurrency}" +
                       (HasVat ? $" · VAT {map.VatRate:P0}" : "") +
                       (map.Items != null ? $" · {Loc.T("Tpl.Items")}: {map.Items.Capacity}" : "");
        if (value!.Entry.Status == TemplateStatus.ChangedSinceLinked) Status = Loc.T("Tpl.Status.Changed");
        ClearLines();
        if (SelectedParty is { } p && !IsClients && _partyId != null)
            PartyTheme = new ThemeFieldViewModel(main, p.Name, ThemeTemplateName(p));
    }

    partial void OnIsClientsChanged(bool value)
    {
        FillParties();
        ResetBank();
    }

    private void FillParties()
    {
        var keep = SelectedParty?.Name;
        Parties.Clear();
        foreach (var p in (IsClients ? _clients : _suppliers).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) Parties.Add(p);
        SelectedParty = Parties.FirstOrDefault(p => p.Name == keep);
    }

    partial void OnSelectedPartyChanged(Party? value)
    {
        ClearLines();
        if (!_suspendClear || value != null) ResetBank();
        if (value == null) { if (!_suspendClear) { PartyDetails = ""; LoadPartyFields(null); } return; }
        // collapsed view: name, native name, category; everything else in the expandable panel
        var parts = new List<string> { value.Name };
        if (value.NativeName != null) parts.Add(value.NativeName);
        if (value.CategoryText != null) parts.Add(value.CategoryText);
        PartyDetails = string.Join("\n", parts);
        LoadPartyFields(value);
        if (_suspendClear) return;
        SelectedCategory = InvoiceService.CategoryFor(value, Categories) ?? SelectedCategory;
        // supplier's default template (column DEFAULT TEMPLATE)
        if (!string.IsNullOrWhiteSpace(value.DefaultTemplate))
        {
            var t = Templates.FirstOrDefault(x => string.Equals(x.Name, value.DefaultTemplate.Trim(), StringComparison.OrdinalIgnoreCase));
            if (t != null && t != SelectedTemplate)
            {
                var party = value;
                SelectedTemplate = t;
                if (SelectedParty != party) SelectedParty = Parties.FirstOrDefault(p => p.Name == party.Name);
            }
        }
        NewNumber();
    }

    partial void OnSelectedCategoryChanged(CategoryInfo? value) => ClearLines();

    // ------------------------------------------------------------------ party details (expand / edit / save)
    private void LoadPartyFields(Party? party)
    {
        foreach (var f in PartyFields.OfType<NewFieldViewModel>()) f.PropertyChanged -= OnPartyFieldChanged;
        if (_partyBanks != null) _partyBanks.Changed -= OnPartyBanksChanged;
        PartyFields.Clear();
        _partyBanks = null;
        _partyExtra = new(StringComparer.OrdinalIgnoreCase);
        _partyExtraOriginal = new(StringComparer.OrdinalIgnoreCase);
        PartyDirty = false;
        _partyId = null;
        _partyOriginal = [];
        var page = PartyPage;
        if (party != null && page != null && party.Id.Length > 0 && page.RecordValues(party.Id) is { } values)
        {
            var headers = page.Headers;
            bool suppliers = !IsClients;
            _partyId = party.Id;
            _partyOriginal = values;
            for (int i = 1; i < headers.Count && i < values.Length; i++)
            {
                var h = headers[i];
                if (suppliers && SupplierBanks.IsBankHeader(headers, i))
                {
                    // all bank accounts as one group (expanded: this is where the account printed on invoices is chosen)
                    if (_partyBanks == null)
                    {
                        for (int j = 0; j < headers.Count && j < values.Length; j++)
                            if (SupplierBanks.IsBankHeader(headers, j)) _partyExtra[headers[j]] = values[j];
                        _partyExtraOriginal = new(_partyExtra, StringComparer.OrdinalIgnoreCase);
                        _partyBanks = new BankGroupViewModel(headers, k => _partyExtra.TryGetValue(k, out var v) ? v : "",
                            (k, v) => _partyExtra[k] = v, expanded: true);
                        _partyBanks.Changed += OnPartyBanksChanged;
                        PartyFields.Add(_partyBanks);
                    }
                    continue;
                }
                var options = main.OptionsFor(page.Session.Definition.Key, h, []);
                if (options != null && values[i].Length > 0 && !options.Contains(values[i])) options = [values[i], .. options];
                var f = new NewFieldViewModel
                {
                    Caption = h, Options = options, Value = values[i], Index = i,
                    IsLong = IsLongField(h),
                };
                f.PropertyChanged += OnPartyFieldChanged;
                PartyFields.Add(f);
            }
        }
        OnPropertyChanged(nameof(CanEditParty));
        PartyTheme = party != null && !IsClients && _partyId != null
            ? new ThemeFieldViewModel(main, party.Name, ThemeTemplateName(party))
            : null;
    }

    private static bool IsLongField(string h) => PartyFormViewModel.IsLongField(h);
    private static string Clean(string? v) => PartyFormViewModel.Clean(v);

    private void OnPartyBanksChanged() => UpdatePartyDirty();

    /// <summary>The template the theme is shown for: the selected one when it has themes, else the supplier's default.</summary>
    private string ThemeTemplateName(Party party)
    {
        var sel = SelectedTemplate?.Name;
        if (BatchStudio.Core.Workbook.ThemeMemory.Supports(sel)) return sel!;
        return string.IsNullOrWhiteSpace(party.DefaultTemplate) ? "INVOICE" : party.DefaultTemplate.Trim();
    }

    private void OnPartyFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewFieldViewModel.Value)) UpdatePartyDirty();
    }

    private void UpdatePartyDirty() =>
        PartyDirty = PartyFields.OfType<NewFieldViewModel>().Any(f => f.Index < _partyOriginal.Length && !string.Equals(f.Value, _partyOriginal[f.Index], StringComparison.Ordinal))
                     || _partyExtra.Any(kv => !string.Equals(kv.Value, _partyExtraOriginal.TryGetValue(kv.Key, out var o) ? o : "", StringComparison.Ordinal));

    [RelayCommand]
    private void ToggleDetails() => DetailsExpanded = !DetailsExpanded;

    /// <summary>Saves the edited supplier / client into its sheet; the invoice keeps its lines and uses the new details.</summary>
    [RelayCommand]
    private async Task SavePartyDetailsAsync()
    {
        var page = PartyPage;
        if (page == null || _partyId == null || !PartyDirty) return;
        var values = (string[])_partyOriginal.Clone();
        foreach (var f in PartyFields.OfType<NewFieldViewModel>()) if (f.Index < values.Length) values[f.Index] = Clean(f.Value);
        // bank accounts: columns that are new (a second account ...) are added to the sheet
        foreach (var (header, value) in _partyExtra)
        {
            var col = page.Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
            if (col < 0 && value.Trim().Length == 0 && !SupplierBanks.IsDefaultHeader(header)) continue;
            page.EnsureColumn(header);
            col = page.Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
            if (col >= values.Length) Array.Resize(ref values, page.Headers.Count);
            for (int k = 0; k < values.Length; k++) values[k] ??= "";
            values[col] = Clean(value);
        }
        var headers = page.Headers;
        int nameCol = headers.FindIndex(h => h.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                                             !h.Contains("Chinese", StringComparison.OrdinalIgnoreCase) && !h.Contains("Native", StringComparison.OrdinalIgnoreCase));
        if (nameCol < 0) nameCol = 1;
        var name = nameCol < values.Length ? values[nameCol] : "";
        if (name.Length == 0) { Status = Loc.F("Inv.PartyRequired", headers[0], headers[nameCol]); return; }
        var others = (IsClients ? _clients : _suppliers).Where(p => !string.Equals(p.Id, _partyId, StringComparison.OrdinalIgnoreCase));
        if (others.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))) { Status = Loc.F("Inv.PartyExists", name); return; }

        IsBusy = true;
        Status = Loc.T("Status.SavingWorkbook");
        try
        {
            page.UpdateRecord(_partyId, values);
            await page.SaveAsync();
        }
        finally { IsBusy = false; }

        // reload the parties without clearing the generated lines
        var id = _partyId;
        _suspendClear = true;
        try
        {
            RefreshCore();
            SelectedParty = Parties.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        finally { _suspendClear = false; }
        if (SelectedParty is { } party)
        {
            var parts = new List<string> { party.Name };
            if (party.NativeName != null) parts.Add(party.NativeName);
            if (party.CategoryText != null) parts.Add(party.CategoryText);
            PartyDetails = string.Join("\n", parts);
            if (_draft != null && _repair == null)
            {
                _draft.PartyName = party.Name;
                _draft.NativeName = party.NativeName;
                _draft.Address = party.Address;
                _draft.Bank = party.BankText(BankNumber);
            }
        }
        Status = page.IsDirty ? page.Status : Loc.F("Inv.PartySaved", name);
    }

    private void ClearLines()
    {
        if (_suspendClear) return;
        Lines.Clear();
        HasLines = false;
        _draft = null;
        SavedPath = null;
        NetText = VatText = TotalText = DifferenceText = Words = "";
    }

    /// <summary>Supplier theme colour for the selected template (suppliers only, like Theme.RestoreClientTheme).</summary>
    private int? ThemeFor(string? supplier) =>
        IsClients || string.IsNullOrWhiteSpace(supplier) || SelectedTemplate == null ? null : main.ThemeFor(supplier, SelectedTemplate.Name);

    // ------------------------------------------------------------------ Smart Invoice Repair
    /// <summary>
    /// Restores an archived invoice for repair (History → Open Invoice → Edit): its rows are protected; a new total and
    /// a higher item count add only new unique items; saving replaces the same archived file and history row.
    /// </summary>
    public bool StartRepair(string templateName, string invoiceNo, string partyType, string archivedFile)
    {
        CancelRepair();
        Refresh();
        var tpl = Templates.FirstOrDefault(t => string.Equals(t.Name, templateName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (tpl?.Entry.Map == null) { Dialogs.Error(Loc.F("Rep.NoTemplateMap", templateName)); return false; }
        RepairSource src;
        try { src = InvoiceRepair.Read(archivedFile, tpl.Entry.Map, invoiceNo); }
        catch (Exception ex) { Dialogs.Error(Loc.T("Rep.RestoreFailed") + "\n" + ex.Message); return false; }

        _suspendClear = true;
        try
        {
            IsAddingParty = false;
            SelectedTemplate = tpl;
            IsClients = partyType.Equals("CLIENTS", StringComparison.OrdinalIgnoreCase) ||
                        (partyType.Length == 0 && tpl.Entry.Map.EffectivePartyType == "CLIENTS");
            FillParties();
            SelectedParty = Parties.FirstOrDefault(p => BatchStudio.Core.Reports.DashboardEngine.SameCompany(p.Name, src.PartyName));
            SelectedCategory = Categories.FirstOrDefault(c => string.Equals(c.Text.Trim(), src.CategoryText?.Trim(), StringComparison.OrdinalIgnoreCase))
                               ?? (SelectedParty != null ? InvoiceService.CategoryFor(SelectedParty, Categories) : null)
                               ?? SelectedCategory;
            InvoiceNo = src.InvoiceNo;
            Date = src.Date ?? DateTime.Today;
            AmountText = FormatMoney(src.Total);
            CountText = src.Lines.Count.ToString(CultureInfo.InvariantCulture);
            PartyDetails = string.Join("\n", new[] { src.PartyName, src.NativeName, src.CategoryText }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        finally { _suspendClear = false; }

        _repair = src;
        _repairNo = invoiceNo;
        IsRepair = true;
        SavedPath = null;
        ShowLines(src.Lines.Select(CopyLine).ToList(), src.Total);
        RepairInfo = Loc.F("Rep.Info", invoiceNo, src.Lines.Count);
        Status = Loc.T("Rep.Hint");
        return true;
    }

    private static InvoiceLine CopyLine(InvoiceLine l) => new()
    {
        Code = l.Code, Description = l.Description, Quantity = l.Quantity, UnitCents = l.UnitCents, ReferencePrice = l.ReferencePrice,
    };

    private void ShowLines(List<InvoiceLine> lines, decimal target)
    {
        var map = SelectedTemplate!.Entry.Map!;
        var src = _repair;
        var party = SelectedParty;
        _draft = new InvoiceDraft
        {
            TemplatePath = SelectedTemplate.Entry.Path,
            Map = map,
            PartyType = IsClients ? "CLIENTS" : "SUPPLIERS",
            PartyName = src?.PartyName is { Length: > 0 } pn ? pn : party?.Name ?? "",
            NativeName = src?.NativeName ?? party?.NativeName,
            Address = src?.Address ?? party?.Address,
            Bank = src?.Bank ?? party?.BankText(BankNumber),
            CategoryText = src?.CategoryText ?? SelectedCategory?.Text,
            Target = target,
            Lines = lines,
            WorkbookPath = main.Workspace?.BookFor(SelectedTemplate?.Entry.Name),   // INV2, INV3 ... live in the Templates workbook
            ThemeRgb = ThemeFor(src?.PartyName is { Length: > 0 } tn ? tn : party?.Name),
        };
        int locked = _repair?.Lines.Count ?? 0;
        Lines.Clear();
        for (int i = 0; i < lines.Count; i++) Lines.Add(new LineViewModel(this, lines[i], i < locked));
        HasLines = true;
        RecalcTotals();
    }

    private async Task ExtendRepairAsync()
    {
        if (_repair == null) return;
        Status = "";
        var map = SelectedTemplate?.Entry.Map;
        if (map == null) return;
        if (SelectedCategory == null) { Status = Loc.T("Inv.ChooseCategory"); return; }
        if (!TryAmount(out var amount)) { Status = Loc.T("Inv.BadAmount"); return; }
        if (!int.TryParse(CountText.Trim(), out var count) || count < 1 || count > InvoiceAllocator.MaxItems) { Status = Loc.T("Inv.BadCount"); return; }
        var itemsTable = main.DataPages.First(p => p.Session.Definition.Key == "items").Session.Current;
        var pool = InvoiceService.Items(itemsTable, SelectedCategory.Id);
        decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
        var existing = _repair.Lines;
        IsBusy = true;
        try
        {
            var plan = await Task.Run(() => InvoiceRepair.Extend(existing, amount, count, pool, vat));
            if (plan.Expanded && !Dialogs.Confirm(Loc.F("Rep.Expand", plan.RequestedCount, plan.FinalCount, plan.FinalCount - plan.RequestedCount)))
            {
                Status = Loc.T("Rep.ExpandCancelled");
                return;
            }
            CountText = plan.FinalCount.ToString(CultureInfo.InvariantCulture);
            ShowLines(plan.Lines, amount);
            Status = plan.FinalCount > existing.Count ? Loc.F("Rep.Added", plan.FinalCount - existing.Count) : "";
        }
        catch (AllocationException ex) { Status = ex.Message; }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task SaveRepairAsync(BatchStudio.Core.Workbook.Workspace ws)
    {
        if (_draft == null || _repair == null) return;
        if (!string.Equals(_draft.InvoiceNo, _repairNo, StringComparison.OrdinalIgnoreCase)) { Status = Loc.T("Rep.NumberLocked"); return; }
        if (!Dialogs.Confirm(Loc.F("Rep.ConfirmReplace", _repairNo, _repair.ArchivePath))) return;
        var draft = _draft;
        var no = _repairNo;
        var file = _repair.ArchivePath;
        int keep = main.Settings.KeepBackups;
        try
        {
            IsBusy = true;
            Status = Loc.T("Status.SavingWorkbook");
            SavedPath = await Task.Run(() => InvoiceService.Replace(ws, draft, no, file, keep));
            _repair = null;
            IsRepair = false;
            RepairInfo = "";
            Status = Loc.F("Rep.Replaced", no);
            main.OnHistoryChanged();
        }
        catch (FileLockedException ex) { Status = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CancelRepair()
    {
        if (!IsRepair) return;
        _repair = null;
        IsRepair = false;
        RepairInfo = "";
        ClearLines();
        AmountText = "";
        NewNumber();
        Status = "";
    }

    // ------------------------------------------------------------------ new supplier / client
    private DataSetViewModel? PartyPage => main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == (IsClients ? "clients" : "suppliers"));

    /// <summary>Opens the form for a supplier / client that is not in the data yet (same columns as the sheet).</summary>
    [RelayCommand]
    private void AddParty()
    {
        if (PartyForm.Open(IsClients, SelectedCategory?.Text, SelectedTemplate?.Name)) IsAddingParty = true;
    }

    private void OnPartyFormClosed(string? name)
    {
        IsAddingParty = false;
        if (name == null) return;
        Refresh();
        SelectedParty = Parties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Status = PartyForm.LastStatus;
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void NewNumber()
    {
        if (main.Root == null) return;
        try
        {
            var existing = InvoiceHistoryBook.InvoiceNumbers(DataSets.InvoiceHistory.FullPath(Root));
            InvoiceNo = InvoiceService.NewInvoiceNumber(InvoiceService.DetectPrefix(existing), existing);
        }
        catch
        {
            InvoiceNo = InvoiceService.NewInvoiceNumber("Mad", new HashSet<string>());
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsRepair) { await ExtendRepairAsync(); return; }
        Status = "";
        SavedPath = null;
        var map = SelectedTemplate?.Entry.Map;
        if (map == null) { Status = Loc.T("Inv.NoTemplates"); return; }
        if (SelectedParty == null) { Status = Loc.T("Inv.ChooseParty"); return; }
        if (SelectedCategory == null) { Status = Loc.T("Inv.ChooseCategory"); return; }
        if (!TryAmount(out var amount)) { Status = Loc.T("Inv.BadAmount"); return; }
        if (!int.TryParse(CountText.Trim(), out var count) || count < 1 || count > InvoiceAllocator.MaxItems)
        {
            Status = Loc.T("Inv.BadCount");
            return;
        }
        var itemsTable = main.DataPages.First(p => p.Session.Definition.Key == "items").Session.Current;
        var pool = InvoiceService.Items(itemsTable, SelectedCategory.Id);
        if (pool.Count == 0) { Status = Loc.F("Inv.NoItems", SelectedCategory.Id); return; }

        IsBusy = true;
        try
        {
            decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
            var lines = await Task.Run(() => InvoiceAllocator.Allocate(amount, count, pool, vat));
            var party = SelectedParty;
            _draft = new InvoiceDraft
            {
                TemplatePath = SelectedTemplate!.Entry.Path,
                Map = map,
                PartyType = IsClients ? "CLIENTS" : "SUPPLIERS",
                PartyName = party.Name,
                NativeName = party.NativeName,
                Address = party.Address,
                Bank = party.BankText(BankNumber),
                CategoryText = SelectedCategory.Text,
                Target = amount,
                Lines = lines,
                WorkbookPath = main.Workspace?.BookFor(SelectedTemplate?.Entry.Name),   // INV2, INV3 ... live in the Templates workbook
                ThemeRgb = ThemeFor(SelectedParty?.Name),
            };
            Lines.Clear();
            foreach (var l in lines) Lines.Add(new LineViewModel(this, l));
            HasLines = true;
            RecalcTotals();
            if (pool.Count < count) Status = Loc.F("Inv.Repeated", pool.Count);
        }
        catch (AllocationException ex) { Status = ex.Message; }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Shows the amount as 30,000 or 30,000.50 (called when the box loses focus).</summary>
    public void FormatAmount()
    {
        if (decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a))
            AmountText = FormatMoney(a);
    }

    public static string FormatMoney(decimal a) =>
        a == decimal.Truncate(a) ? a.ToString("#,##0", CultureInfo.InvariantCulture) : a.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private bool TryAmount(out decimal amount) =>
        decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) && amount > 0;

    public void RecalcTotals()
    {
        if (_draft == null) return;
        string F(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
        NetText = F(_draft.Net);
        VatText = F(_draft.Vat);
        TotalText = F(_draft.Total);
        var diff = _draft.Total - _draft.Target;
        IsBalanced = diff == 0;
        DifferenceText = IsBalanced ? "✓" : (diff > 0 ? "+" : "") + F(diff);
        Words = _draft.Words;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_draft == null) { Status = Loc.T("Inv.GenerateFirst"); return; }
        if (!IsBalanced && !Dialogs.Confirm(Loc.F("Inv.Unbalanced", TotalText, _draft.Target.ToString("N2", CultureInfo.InvariantCulture))))
            return;
        _draft.InvoiceNo = InvoiceNo.Trim();
        _draft.Date = Date ?? DateTime.Today;
        var ws = main.Workspace ?? throw new InvalidOperationException("No entity is open.");
        if (IsRepair && _repair != null)
        {
            await SaveRepairAsync(ws);
            return;
        }
        // a theme changed in the details panel since the items were generated is used too
        if (!IsClients) _draft.ThemeRgb = ThemeFor(_draft.PartyName);
        var draft = _draft;
        int keep = main.Settings.KeepBackups;
        try
        {
            IsBusy = true;
            Status = Loc.T("Status.SavingWorkbook");
            SavedPath = await Task.Run(() => InvoiceService.Archive(ws, draft, keep));
            Status = Loc.T("Inv.Saved") + (ExcelInvoiceEngine.LastFallbackReason is { } why ? "\n" + Loc.F("Inv.NoExcelFormat", why) : "");
            main.OnHistoryChanged();
        }
        catch (FileLockedException ex) { Status = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenSaved()
    {
        if (SavedPath != null) Shell.Open(SavedPath);
    }

    [RelayCommand]
    private void ShowSaved()
    {
        if (SavedPath != null) Shell.ShowInFolder(SavedPath);
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (SavedPath == null) return;
        IsBusy = true;
        try
        {
            var source = SavedPath;
            var dest = InvoiceService.DesktopPdfPath(_draft?.PartyName ?? SelectedParty?.Name ?? "", _draft?.Total);
            var sheets = main.InvoiceSheetNames();
            var pdf = await Task.Run(() => ExcelPdf.Export(source, dest, sheets));
            Status = Loc.F("Hist.PdfSaved", pdf);
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void NewInvoice()
    {
        if (IsRepair) { CancelRepair(); return; }
        ClearLines();
        AmountText = "";
        NewNumber();
        Status = "";
    }
}
