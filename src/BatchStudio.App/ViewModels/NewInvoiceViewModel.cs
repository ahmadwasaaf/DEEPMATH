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
    [ObservableProperty] private string _newPartyTitle = "";
    [ObservableProperty] private string _newPartyError = "";
    public ObservableCollection<NewFieldViewModel> NewPartyFields { get; } = [];

    // Smart Invoice Repair
    private RepairSource? _repair;
    private string _repairNo = "";
    private bool _suspendClear;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormal), nameof(GenerateText))]
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
    }

    partial void OnIsClientsChanged(bool value) => FillParties();

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
        if (value == null) { if (!_suspendClear) PartyDetails = ""; return; }
        var parts = new List<string> { value.Name };
        if (value.NativeName != null) parts.Add(value.NativeName);
        if (value.CategoryText != null) parts.Add(value.CategoryText);
        if (value.Address != null) parts.Add(value.Address);
        if (value.Bank != null) parts.Add(value.Bank);
        PartyDetails = string.Join("\n", parts);
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

    private void ClearLines()
    {
        if (_suspendClear) return;
        Lines.Clear();
        HasLines = false;
        _draft = null;
        SavedPath = null;
        NetText = VatText = TotalText = DifferenceText = Words = "";
    }

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
            Bank = src?.Bank ?? party?.Bank,
            CategoryText = src?.CategoryText ?? SelectedCategory?.Text,
            Target = target,
            Lines = lines,
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
        var page = PartyPage;
        if (page == null) return;
        var headers = page.Session.Current.Headers;
        NewPartyFields.Clear();
        NewPartyError = "";
        NewPartyTitle = Loc.T(IsClients ? "Inv.NewClient" : "Inv.NewSupplier");
        for (int i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            var options = main.OptionsFor(page.Session.Definition.Key, h, []);
            string value = "";
            if (i == 0) value = page.NextId();
            else if (options != null && h.Contains("Category", StringComparison.OrdinalIgnoreCase) && SelectedCategory != null) value = SelectedCategory.Text;
            else if (options != null && h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase) && SelectedTemplate != null) value = SelectedTemplate.Name;
            if (options != null && value.Length > 0 && !options.Contains(value)) options = [value, .. options];
            NewPartyFields.Add(new NewFieldViewModel
            {
                Caption = h, Options = options, Value = value,
                IsLong = h.Contains("Address", StringComparison.OrdinalIgnoreCase) || h.Contains("Bank", StringComparison.OrdinalIgnoreCase) ||
                         h.Contains("Activit", StringComparison.OrdinalIgnoreCase),
            });
        }
        IsAddingParty = true;
    }

    [RelayCommand]
    private void CancelParty()
    {
        IsAddingParty = false;
        NewPartyFields.Clear();
    }

    [RelayCommand]
    private async Task SavePartyAsync()
    {
        var page = PartyPage;
        if (page == null) return;
        NewPartyError = "";
        var values = NewPartyFields.Select(f => f.Value.Trim()).ToArray();
        var headers = page.Session.Current.Headers;
        int nameCol = headers.FindIndex(h => h.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                                             !h.Contains("Chinese", StringComparison.OrdinalIgnoreCase) && !h.Contains("Native", StringComparison.OrdinalIgnoreCase));
        if (nameCol < 0) nameCol = 1;
        var name = nameCol < values.Length ? values[nameCol] : "";
        if (values[0].Length == 0 || name.Length == 0) { NewPartyError = Loc.F("Inv.PartyRequired", headers[0], headers[nameCol]); return; }
        var existing = IsClients ? _clients : _suppliers;
        if (existing.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        { NewPartyError = Loc.F("Inv.PartyExists", name); return; }
        if (page.Session.Current.Rows.Any(r => string.Equals(r[0], values[0], StringComparison.OrdinalIgnoreCase)))
        { NewPartyError = Loc.F("Inv.PartyIdExists", values[0]); return; }

        IsBusy = true;
        Status = Loc.T("Status.SavingWorkbook");
        try
        {
            page.AddRecord(values);
            await page.SaveAsync();
        }
        finally { IsBusy = false; }

        IsAddingParty = false;
        NewPartyFields.Clear();
        Refresh();
        SelectedParty = Parties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Status = page.IsDirty ? page.Status : Loc.F("Inv.PartyAdded", name);
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
                Bank = party.Bank,
                CategoryText = SelectedCategory.Text,
                Target = amount,
                Lines = lines,
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
        var draft = _draft;
        int keep = main.Settings.KeepBackups;
        try
        {
            IsBusy = true;
            Status = Loc.T("Status.SavingWorkbook");
            SavedPath = await Task.Run(() => InvoiceService.Archive(ws, draft, keep));
            Status = Loc.T("Inv.Saved");
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
            var pdf = await Task.Run(() => ExcelPdf.Export(source, dest));
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
