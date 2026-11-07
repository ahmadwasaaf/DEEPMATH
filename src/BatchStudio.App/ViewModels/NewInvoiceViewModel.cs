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

    public LineViewModel(NewInvoiceViewModel owner, InvoiceLine line)
    {
        _owner = owner;
        Line = line;
    }

    public string Code { get => Line.Code; set { Line.Code = value; OnPropertyChanged(); } }
    public string Description { get => Line.Description; set { Line.Description = value; OnPropertyChanged(); } }

    public long Quantity
    {
        get => Line.Quantity;
        set { Line.Quantity = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(Amount)); _owner.RecalcTotals(); }
    }

    public decimal UnitPrice
    {
        get => Line.UnitPrice;
        set
        {
            Line.UnitCents = Math.Max(1, (long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero));
            OnPropertyChanged();
            OnPropertyChanged(nameof(Amount));
            _owner.RecalcTotals();
        }
    }

    public decimal Amount => Line.Amount;
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

    public bool HasSaved => SavedPath != null;
    partial void OnSavedPathChanged(string? value) => OnPropertyChanged(nameof(HasSaved));

    private string Root => main.Root ?? throw new InvalidOperationException("No entity is open.");

    // ------------------------------------------------------------------ loading
    public void Refresh()
    {
        if (main.Root == null) return;
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
        IsClients = map.EffectivePartyType == "CLIENTS";
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
        if (value == null) { PartyDetails = ""; return; }
        var parts = new List<string> { value.Name };
        if (value.NativeName != null) parts.Add(value.NativeName);
        if (value.CategoryText != null) parts.Add(value.CategoryText);
        if (value.Address != null) parts.Add(value.Address);
        if (value.Bank != null) parts.Add(value.Bank);
        PartyDetails = string.Join("\n", parts);
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
        Lines.Clear();
        HasLines = false;
        _draft = null;
        SavedPath = null;
        NetText = VatText = TotalText = DifferenceText = Words = "";
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void NewNumber()
    {
        if (main.Root == null) return;
        try
        {
            var hist = ExcelTableStore.Load(DataSets.InvoiceHistory.FullPath(Root), DataSets.InvoiceHistory.SheetName);
            var prefix = DatabaseLayout.ReadEntityInfo(Root)?.InvoicePrefix ?? "Mad";
            InvoiceNo = InvoiceService.NewInvoiceNumber(prefix, InvoiceService.ExistingInvoiceNumbers(hist));
        }
        catch
        {
            InvoiceNo = InvoiceService.NewInvoiceNumber("Mad", new HashSet<string>());
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
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
    private void Save()
    {
        if (_draft == null) { Status = Loc.T("Inv.GenerateFirst"); return; }
        if (!IsBalanced && !Dialogs.Confirm(Loc.F("Inv.Unbalanced", TotalText, _draft.Target.ToString("N2", CultureInfo.InvariantCulture))))
            return;
        _draft.InvoiceNo = InvoiceNo.Trim();
        _draft.Date = Date ?? DateTime.Today;
        try
        {
            IsBusy = true;
            SavedPath = InvoiceService.Archive(Root, _draft, main.Settings.KeepBackups);
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
            var pdf = await Task.Run(() => ExcelPdf.Export(SavedPath));
            Status = Loc.T("Inv.PdfSaved");
            Shell.Open(pdf);
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void NewInvoice()
    {
        ClearLines();
        AmountText = "";
        NewNumber();
        Status = "";
    }
}
