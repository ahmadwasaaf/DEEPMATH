using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>One line of the MASS INVOICE sheet (like a row of the BATCH STUDIO sheet: party, total, items, date, status).</summary>
public sealed partial class MassRowViewModel : ObservableObject
{
    private readonly MassInvoiceViewModel _owner;
    public MassRowViewModel(MassInvoiceViewModel owner, int no) { _owner = owner; No = no; }

    public int No { get; }
    [ObservableProperty] private string _partyName = "";
    [ObservableProperty] private string _template = "";
    [ObservableProperty] private string _categoryText = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private string _status = "";
    /// <summary>"" | run | ok | warn | fail - colours the status.</summary>
    [ObservableProperty] private string _state = "";

    // each row has its own copy: the type-to-filter of a dropdown filters its list's view
    public List<string> PartyOptions { get; private set; } = [];
    public IReadOnlyList<string> TemplateOptions => _owner.TemplateNames;

    public bool IsEmpty => PartyName.Trim().Length == 0 && AmountText.Trim().Length == 0 && CountText.Trim().Length == 0;

    partial void OnPartyNameChanged(string value)
    {
        _owner.OnPartyPicked(this);
        ResetStatus();
    }
    partial void OnAmountTextChanged(string value) { ResetStatus(); _owner.Recount(); }
    partial void OnCountTextChanged(string value) => ResetStatus();
    partial void OnTemplateChanged(string value) => ResetStatus();

    private void ResetStatus()
    {
        if (_owner.IsBusy) return;
        Status = "";
        State = "";
    }

    public void FormatAmount()
    {
        if (decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && a > 0)
            AmountText = NewInvoiceViewModel.FormatMoney(a);
    }

    public void Clear()
    {
        PartyName = Template = CategoryText = AmountText = CountText = Status = State = "";
        Date = null;
    }

    public void Refresh()
    {
        PartyOptions = [.. _owner.PartyNames];
        OnPropertyChanged(nameof(PartyOptions));
        OnPropertyChanged(nameof(TemplateOptions));
    }
}

/// <summary>
/// MASS INVOICE: up to 50 invoices in one go, like the BATCH STUDIO sheet of the workbook. Every filled row gets its
/// own items (same engine as New Invoice), a new invoice number, the party's default template and category, the
/// supplier theme; all are archived to the party folders and recorded in INVOICE HISTORY in one workbook save.
/// </summary>
public sealed partial class MassInvoiceViewModel(MainViewModel main) : ObservableObject
{
    public const int MaxRows = 50;

    public ObservableCollection<MassRowViewModel> Rows { get; } = [];
    public List<string> PartyNames { get; private set; } = [];
    public List<string> TemplateNames { get; private set; } = [];
    public IReadOnlyList<string> Types => [Loc.T("Nav.Suppliers"), Loc.T("Nav.Clients")];

    [ObservableProperty] private int _typeIndex;
    [ObservableProperty] private DateTime? _defaultDate = DateTime.Today;
    [ObservableProperty] private string _defaultCount = "10";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _countText = "";

    private List<Party> _parties = [];
    private List<CategoryInfo> _categories = [];
    private List<TemplateEntry> _templates = [];
    private int _typeGuard;

    private bool IsClients => TypeIndex == 1;
    public string PartyHeader => Loc.T(IsClients ? "Mass.Client" : "Mass.Supplier");

    // ------------------------------------------------------------------ loading
    public void Refresh()
    {
        if (main.Root == null) return;
        if (Rows.Count == 0)
            for (int i = 1; i <= MaxRows; i++) Rows.Add(new MassRowViewModel(this, i));

        var sup = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers")?.Session.Current;
        var cli = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "clients")?.Session.Current;
        var cat = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "categories")?.Session.Current;
        var parties = IsClients ? (cli != null ? InvoiceService.Clients(cli) : []) : (sup != null ? InvoiceService.Suppliers(sup) : []);
        _parties = parties.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _categories = cat != null ? InvoiceService.Categories(cat).Where(c => c.Active).ToList() : [];
        _templates = TemplateRegistry.Scan(main.Root).Where(t => t.Map != null).ToList();

        var type = IsClients ? "CLIENTS" : "SUPPLIERS";
        PartyNames = _parties.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        TemplateNames = _templates.Where(t => t.Map!.EffectivePartyType == type).Select(t => t.Name).ToList();
        if (TemplateNames.Count == 0) TemplateNames = _templates.Select(t => t.Name).ToList();
        foreach (var r in Rows) r.Refresh();
        OnPropertyChanged(nameof(PartyHeader));
        Recount();
        if (_templates.Count == 0) Status = Loc.T("Inv.NoTemplates");
    }

    partial void OnTypeIndexChanged(int oldValue, int newValue)
    {
        if (_typeGuard > 0) return;
        if (Rows.Any(r => !r.IsEmpty))
        {
            if (!Dialogs.Confirm(Loc.T("Mass.SwitchClears")))
            {
                _typeGuard++;
                Application.Current?.Dispatcher.BeginInvoke(() => { TypeIndex = oldValue; _typeGuard--; });
                return;
            }
            foreach (var r in Rows) r.Clear();
        }
        Summary = "";
        Refresh();
    }

    public void Recount()
    {
        int filled = Rows.Count(r => !r.IsEmpty);
        decimal total = 0;
        foreach (var r in Rows)
            if (decimal.TryParse(r.AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && a > 0) total += a;
        CountText = Loc.F("Mass.Count", filled, MaxRows, total.ToString("N2", CultureInfo.InvariantCulture));
    }

    private Party? FindParty(string name) =>
        _parties.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A party was picked on a row: its default template and category are shown, items / date get the defaults.</summary>
    public void OnPartyPicked(MassRowViewModel row)
    {
        var p = FindParty(row.PartyName);
        if (p == null) { row.CategoryText = ""; Recount(); return; }
        var tpl = !string.IsNullOrWhiteSpace(p.DefaultTemplate)
            ? TemplateNames.FirstOrDefault(t => string.Equals(t, p.DefaultTemplate.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? _templates.FirstOrDefault(t => string.Equals(t.Name, p.DefaultTemplate.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
            : null;
        row.Template = tpl ?? (row.Template.Length > 0 ? row.Template : TemplateNames.FirstOrDefault() ?? "");
        row.CategoryText = InvoiceService.CategoryFor(p, _categories)?.Text ?? "";
        if (row.CountText.Trim().Length == 0) row.CountText = DefaultCount;
        row.Date ??= DefaultDate;
        Recount();
    }

    // ------------------------------------------------------------------ commands
    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) return;
        if (Rows.Any(r => !r.IsEmpty) && !Dialogs.Confirm(Loc.T("Mass.ClearAsk"))) return;
        foreach (var r in Rows) r.Clear();
        Summary = Status = "";
        Progress = 0;
        Recount();
    }

    [RelayCommand]
    private void ApplyDefaults()
    {
        foreach (var r in Rows.Where(r => !r.IsEmpty))
        {
            r.CountText = DefaultCount;
            r.Date = DefaultDate;
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;
        Status = Summary = "";
        var ws = main.Workspace;
        if (ws == null || main.Root == null) { Status = "No entity is open."; return; }
        Refresh();

        // ---- check every filled row first (like the batch audit): nothing is written while a row is wrong
        var itemsTable = main.DataPages.First(p => p.Session.Definition.Key == "items").Session.Current;
        var plans = new List<(MassRowViewModel Row, Party Party, TemplateEntry Tpl, CategoryInfo Cat, decimal Amount, int Count, DateTime Date, List<CatalogItem> Pool)>();
        int bad = 0;
        foreach (var r in Rows)
        {
            if (r.IsEmpty) { r.Status = r.State = ""; continue; }
            string? err = null;
            var p = FindParty(r.PartyName);
            var tpl = _templates.FirstOrDefault(t => string.Equals(t.Name, r.Template.Trim(), StringComparison.OrdinalIgnoreCase));
            CategoryInfo? cat = p != null ? InvoiceService.CategoryFor(p, _categories) : null;
            decimal amount = 0;
            int count = 0;
            List<CatalogItem> pool = [];
            if (p == null) err = Loc.F("Mass.ErrParty", r.PartyName.Trim().Length == 0 ? "-" : r.PartyName.Trim());
            else if (tpl?.Map == null) err = Loc.T("Mass.ErrTemplate");
            else if (cat == null) err = Loc.T("Mass.ErrCategory");
            else if (!decimal.TryParse(r.AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount <= 0)
                err = Loc.T("Inv.BadAmount");
            else if (!int.TryParse((r.CountText.Trim().Length == 0 ? DefaultCount : r.CountText).Trim(), out count) || count < 1 || count > InvoiceAllocator.MaxItems)
                err = Loc.T("Inv.BadCount");
            else if ((pool = InvoiceService.Items(itemsTable, cat.Id)).Count == 0)
                err = Loc.F("Inv.NoItems", cat.Id);
            if (err != null) { r.Status = err; r.State = "fail"; bad++; continue; }
            r.Status = ""; r.State = "";
            plans.Add((r, p!, tpl!, cat!, amount, count, r.Date ?? DefaultDate ?? DateTime.Today, pool));
        }
        if (plans.Count == 0 && bad == 0) { Status = Loc.T("Mass.Empty"); return; }
        if (bad > 0) { Status = Loc.F("Mass.FixRows", bad); return; }
        if (!Dialogs.Confirm(Loc.F("Mass.Confirm", plans.Count))) return;

        IsBusy = true;
        Progress = 0;
        try
        {
            // ---- items + numbers
            HashSet<string> used;
            try { used = InvoiceHistoryBook.InvoiceNumbers(DataSets.InvoiceHistory.FullPath(main.Root)); }
            catch { used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
            var prefix = InvoiceService.DetectPrefix(used);
            var drafts = new List<InvoiceDraft>();
            var rows = new List<MassRowViewModel>();
            foreach (var pl in plans)
            {
                var map = pl.Tpl.Map!;
                decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
                List<InvoiceLine> lines;
                try { lines = await Task.Run(() => InvoiceAllocator.Allocate(pl.Amount, pl.Count, pl.Pool, vat)); }
                catch (Exception ex) { pl.Row.Status = ex.Message; pl.Row.State = "fail"; continue; }
                var no = InvoiceService.NewInvoiceNumber(prefix, used);
                used.Add(no);
                drafts.Add(new InvoiceDraft
                {
                    TemplatePath = pl.Tpl.Path,
                    Map = map,
                    PartyType = IsClients ? "CLIENTS" : "SUPPLIERS",
                    PartyName = pl.Party.Name,
                    NativeName = pl.Party.NativeName,
                    Address = pl.Party.Address,
                    Bank = pl.Party.Bank,
                    CategoryText = pl.Cat.Text,
                    Target = pl.Amount,
                    Lines = lines,
                    InvoiceNo = no,
                    Date = pl.Date,
                    WorkbookPath = main.Workspace?.WorkbookPath,
                    ThemeRgb = IsClients ? null : main.ThemeFor(pl.Party.Name, pl.Tpl.Name),
                });
                rows.Add(pl.Row);
                pl.Row.Status = Loc.T("Mass.Waiting");
                pl.Row.State = "run";
            }

            // ---- files (one hidden Excel for the whole batch), then one INVOICE HISTORY save
            var ui = Application.Current?.Dispatcher;
            int done = 0;
            var notes = new string?[drafts.Count];
            if (drafts.Count > 0) rows[0].Status = Loc.T("Mass.Writing");
            Status = Loc.F("Mass.Running", drafts.Count);
            int keep = main.Settings.KeepBackups;
            var results = await Task.Run(() =>
            {
                using var session = ExcelInvoiceEngine.BeginSession();
                return InvoiceService.ArchiveBatch(ws, drafts, keep, (i, file, error) =>
                {
                    notes[i] = ExcelInvoiceEngine.LastFallbackReason;
                    ui?.Invoke(() =>
                    {
                        done++;
                        Progress = 100.0 * done / drafts.Count;
                        rows[i].Status = error ?? Loc.T("Mass.Recording");
                        rows[i].State = error != null ? "fail" : "run";
                        if (i + 1 < rows.Count) rows[i + 1].Status = Loc.T("Mass.Writing");
                        Status = Loc.F("Mass.Progress", done, drafts.Count);
                    });
                });
            });

            int ok = 0, failed = 0, plain = 0;
            decimal total = 0;
            foreach (var res in results)
            {
                var row = rows[res.Index];
                if (res.Recorded)
                {
                    ok++;
                    total += res.Draft.Target;
                    row.Status = $"{Loc.T("Mass.Success")} · {res.Draft.InvoiceNo}";
                    row.State = "ok";
                    if (notes[res.Index] is { } why)
                    {
                        plain++;
                        row.Status += " · " + Loc.T("Mass.Plain");
                        row.State = "warn";
                    }
                }
                else
                {
                    failed++;
                    row.Status = res.Error ?? Loc.T("Status.Error");
                    row.State = "fail";
                }
            }
            failed += plans.Count - drafts.Count;
            int empty = Rows.Count(r => r.IsEmpty);
            Summary = Loc.F("Mass.Summary", ok, failed, empty, total.ToString("N2", CultureInfo.InvariantCulture));
            Status = plain > 0 ? Loc.F("Mass.PlainNote", plain) : "";
            Progress = 100;
            if (ok > 0) main.OnHistoryChanged();
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Screenshot smoke test: a few sample rows.</summary>
    public void FillSample()
    {
        Refresh();
        var names = PartyNames.Take(3).ToList();
        decimal[] amounts = [30000m, 12500.5m, 48000m];
        for (int i = 0; i < names.Count; i++)
        {
            Rows[i].PartyName = names[i];
            Rows[i].AmountText = NewInvoiceViewModel.FormatMoney(amounts[i]);
        }
        Rows[1].Status = $"{Loc.T("Mass.Success")} · Mad-1234567";
        Rows[1].State = "ok";
        Rows[2].Status = Loc.T("Mass.ErrCategory");
        Rows[2].State = "fail";
    }
}
