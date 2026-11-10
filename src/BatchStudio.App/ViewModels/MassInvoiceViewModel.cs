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
    /// <summary>INVOICE NO: filled when the party is picked, editable (duplicates refused).</summary>
    [ObservableProperty] private string _invoiceNo = "";
    /// <summary>Archived file of this row after Generate (the row is then skipped by Generate, Export PDF uses the file).</summary>
    public string? ArchivedFile { get; set; }
    public bool IsArchived => ArchivedFile != null;
    [ObservableProperty] private string _status = "";
    /// <summary>"" | run | ok | warn | fail - colours the status.</summary>
    [ObservableProperty] private string _state = "";

    // each row has its own copy: the type-to-filter of a dropdown filters its list's view
    public List<string> PartyOptions { get; private set; } = [];
    public IReadOnlyList<string> TemplateOptions => _owner.TemplateNames;

    public bool IsEmpty => PartyName.Trim().Length == 0 && AmountText.Trim().Length == 0 && CountText.Trim().Length == 0;

    partial void OnPartyNameChanged(string value)
    {
        if (IsArchived) { ArchivedFile = null; InvoiceNo = ""; }
        _owner.OnPartyPicked(this);
        ResetStatus();
    }
    partial void OnAmountTextChanged(string value) { ResetStatus(); _owner.Recount(); }
    partial void OnCountTextChanged(string value) => ResetStatus();
    partial void OnTemplateChanged(string value) => ResetStatus();

    private void ResetStatus()
    {
        if (_owner.IsBusy || IsArchived) return;
        Status = "";
        State = "";
    }

    public void CommitInvoiceNo() => _owner.OnInvoiceNoCommitted(this);

    /// <summary>"+" next to the dropdown: a new supplier / client, put on this row once saved.</summary>
    [RelayCommand]
    private void AddParty() => _owner.AddPartyFor(this);

    public void FormatAmount()
    {
        if (decimal.TryParse(AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && a > 0)
            AmountText = NewInvoiceViewModel.FormatMoney(a);
    }

    public void Clear()
    {
        ArchivedFile = null;
        PartyName = Template = CategoryText = AmountText = CountText = InvoiceNo = Status = State = "";
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
    public string AddPartyTip => Loc.T(IsClients ? "Inv.NewClient" : "Inv.NewSupplier");

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
        // bank-account count badges of the dropdowns
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _parties) counts[p.Name.Trim()] = p.BankCount;
        BankBadgeConverter.CountsByName = counts;
        TemplateNames = _templates.Where(t => t.Map!.EffectivePartyType == type).Select(t => t.Name).ToList();
        if (TemplateNames.Count == 0) TemplateNames = _templates.Select(t => t.Name).ToList();
        LoadHistoryNumbers();
        foreach (var r in Rows) r.Refresh();
        OnPropertyChanged(nameof(PartyHeader));
        OnPropertyChanged(nameof(AddPartyTip));
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

    /// <summary>The company's DEFAULT TEMPLATE when linked, otherwise the first template for suppliers / clients (like TemplateRoute).</summary>
    private string TemplateFor(Party p)
    {
        if (!string.IsNullOrWhiteSpace(p.DefaultTemplate))
        {
            var t = _templates.FirstOrDefault(x => string.Equals(x.Name, p.DefaultTemplate.Trim(), StringComparison.OrdinalIgnoreCase));
            if (t != null) return t.Name;
        }
        return TemplateNames.FirstOrDefault() ?? "";
    }

    // ------------------------------------------------------------------ invoice numbers
    private HashSet<string> _historyNumbers = new(StringComparer.OrdinalIgnoreCase);
    private string _prefix = "INV";

    private void LoadHistoryNumbers()
    {
        try
        {
            var path = main.Root == null ? null : DataSets.InvoiceHistory.FullPath(main.Root);
            _historyNumbers = path != null && File.Exists(path) ? InvoiceHistoryBook.InvoiceNumbers(path) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { _historyNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        _prefix = InvoiceService.DetectPrefix(_historyNumbers);
    }

    private HashSet<string> NumbersInUse(MassRowViewModel? except)
    {
        var set = new HashSet<string>(_historyNumbers, StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows)
            if (r != except && r.InvoiceNo.Trim().Length > 0) set.Add(r.InvoiceNo.Trim());
        return set;
    }

    private string NewNumber(MassRowViewModel row) => InvoiceService.NewInvoiceNumber(_prefix, NumbersInUse(row));

    /// <summary>A typed invoice number is kept unless INVOICE HISTORY or another row already uses it.</summary>
    public void OnInvoiceNoCommitted(MassRowViewModel row)
    {
        if (row.IsArchived) return;
        var v = row.InvoiceNo.Trim();
        if (v.Length == 0)
        {
            if (row.PartyName.Trim().Length > 0) row.InvoiceNo = NewNumber(row);
            return;
        }
        row.InvoiceNo = v;
        if (_historyNumbers.Contains(v))
        {
            row.InvoiceNo = NewNumber(row);
            Status = Loc.F("Mass.NoInHistory", v, row.InvoiceNo);
        }
        else if (Rows.FirstOrDefault(r => r != row && string.Equals(r.InvoiceNo.Trim(), v, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            row.InvoiceNo = NewNumber(row);
            Status = Loc.F("Mass.NoOnRow", v, other.No, row.InvoiceNo);
        }
    }

    /// <summary>A party was picked on a row: invoice number, template, category, items and date get filled.</summary>
    public void OnPartyPicked(MassRowViewModel row)
    {
        if (row.PartyName.Trim().Length == 0)
        {
            row.InvoiceNo = "";
            row.CategoryText = "";
            Recount();
            return;
        }
        if (row.InvoiceNo.Trim().Length == 0) row.InvoiceNo = NewNumber(row);
        var p = FindParty(row.PartyName);
        if (p == null) { row.CategoryText = ""; Recount(); return; }
        row.Template = TemplateFor(p);
        row.CategoryText = InvoiceService.CategoryFor(p, _categories)?.Text ?? "";
        if (row.CountText.Trim().Length == 0) row.CountText = DefaultCount;
        row.Date ??= DefaultDate;
        Recount();
    }

    // ------------------------------------------------------------------ new supplier / client from a row
    [ObservableProperty] private bool _isAddingParty;
    private PartyFormViewModel? _partyForm;
    private MassRowViewModel? _formRow;

    /// <summary>The same form as New Invoice "+" (fields, bank accounts, theme).</summary>
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

    public void AddPartyFor(MassRowViewModel row)
    {
        if (IsBusy) return;
        var category = row.CategoryText.Trim().Length > 0 ? row.CategoryText : null;
        var template = row.Template.Trim().Length > 0 ? row.Template : TemplateNames.FirstOrDefault();
        _formRow = row;
        if (PartyForm.Open(IsClients, category, template)) IsAddingParty = true;
    }

    private void OnPartyFormClosed(string? name)
    {
        IsAddingParty = false;
        var row = _formRow;
        _formRow = null;
        if (name == null) return;
        Refresh();
        if (row != null)
        {
            row.PartyName = name;
            Status = PartyForm.LastStatus;
        }
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
        foreach (var r in Rows.Where(r => !r.IsEmpty && !r.IsArchived))
        {
            r.CountText = DefaultCount;
            r.Date = DefaultDate;
        }
    }

    private sealed record Plan(MassRowViewModel Row, Party Party, TemplateEntry Tpl, CategoryInfo Cat, decimal Amount, int Count, DateTime Date, List<CatalogItem> Pool);

    /// <summary>Checks the rows (like the batch audit). Rows with a problem get a red STATUS and are counted in bad.</summary>
    private List<Plan> CheckRows(IEnumerable<MassRowViewModel> rows, out int bad)
    {
        var itemsTable = main.DataPages.First(p => p.Session.Definition.Key == "items").Session.Current;
        var plans = new List<Plan>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bad = 0;
        foreach (var r in rows)
        {
            string? err = null;
            var p = FindParty(r.PartyName);
            if (p != null) r.Template = TemplateFor(p);
            var tpl = _templates.FirstOrDefault(t => string.Equals(t.Name, r.Template.Trim(), StringComparison.OrdinalIgnoreCase));
            CategoryInfo? cat = p != null ? InvoiceService.CategoryFor(p, _categories) : null;
            decimal amount = 0;
            int count = 0;
            List<CatalogItem> pool = [];
            if (p != null && r.InvoiceNo.Trim().Length == 0) r.InvoiceNo = NewNumber(r);
            if (p == null) err = Loc.F("Mass.ErrParty", r.PartyName.Trim().Length == 0 ? "-" : r.PartyName.Trim());
            else if (tpl?.Map == null) err = Loc.T("Mass.ErrTemplate");
            else if (cat == null) err = Loc.T("Mass.ErrCategory");
            else if (!decimal.TryParse(r.AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount) || amount <= 0)
                err = Loc.T("Inv.BadAmount");
            else if (!int.TryParse((r.CountText.Trim().Length == 0 ? DefaultCount : r.CountText).Trim(), out count) || count < 1 || count > InvoiceAllocator.MaxItems)
                err = Loc.T("Inv.BadCount");
            else if ((pool = InvoiceService.Items(itemsTable, cat.Id)).Count == 0)
                err = Loc.F("Inv.NoItems", cat.Id);
            else if (!seen.Add(r.InvoiceNo.Trim()))
                err = Loc.F("Mass.ErrDupNo", r.InvoiceNo.Trim());
            if (err != null) { r.Status = err; r.State = "fail"; bad++; continue; }
            plans.Add(new Plan(r, p!, tpl!, cat!, amount, count, r.Date ?? DefaultDate ?? DateTime.Today, pool));
        }
        return plans;
    }

    private async Task<InvoiceDraft> DraftAsync(Plan pl)
    {
        var map = pl.Tpl.Map!;
        decimal? vat = map.VatRate is > 0 ? (decimal)map.VatRate.Value : null;
        var lines = await Task.Run(() => InvoiceAllocator.Allocate(pl.Amount, pl.Count, pl.Pool, vat));
        return new InvoiceDraft
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
            InvoiceNo = pl.Row.InvoiceNo.Trim(),
            Date = pl.Date,
            WorkbookPath = main.Workspace?.WorkbookPath,
            ThemeRgb = IsClients ? null : main.ThemeFor(pl.Party.Name, pl.Tpl.Name),
        };
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;
        Status = Summary = "";
        var ws = main.Workspace;
        if (ws == null || main.Root == null) { Status = "No entity is open."; return; }
        Refresh();

        // rows already archived by an earlier Generate are skipped (like ARCHIVED rows in the workbook)
        var todo = Rows.Where(r => !r.IsEmpty && !r.IsArchived).ToList();
        foreach (var r in Rows.Where(r => r.IsEmpty)) { r.Status = r.State = ""; }
        foreach (var r in todo) { r.Status = r.State = ""; }
        var plans = CheckRows(todo, out int bad);
        foreach (var pl in plans)
            if (_historyNumbers.Contains(pl.Row.InvoiceNo.Trim()))
            { pl.Row.Status = Loc.F("Mass.ErrNoUsed", pl.Row.InvoiceNo.Trim()); pl.Row.State = "fail"; bad++; }
        plans = plans.Where(p => p.Row.State != "fail").ToList();
        if (plans.Count == 0 && bad == 0) { Status = Rows.Any(r => r.IsArchived) ? Loc.T("Mass.AllArchived") : Loc.T("Mass.Empty"); return; }
        if (bad > 0) { Status = Loc.F("Mass.FixRows", bad); return; }
        if (!Dialogs.Confirm(Loc.F("Mass.Confirm", plans.Count))) return;

        IsBusy = true;
        Progress = 0;
        try
        {
            var drafts = new List<InvoiceDraft>();
            var rows = new List<MassRowViewModel>();
            int notDrafted = 0;
            foreach (var pl in plans)
            {
                try { drafts.Add(await DraftAsync(pl)); }
                catch (Exception ex) { pl.Row.Status = ex.Message; pl.Row.State = "fail"; notDrafted++; continue; }
                rows.Add(pl.Row);
                pl.Row.Status = Loc.T("Mass.Waiting");
                pl.Row.State = "run";
            }

            // files (one hidden Excel for the whole batch), then one INVOICE HISTORY save
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

            int ok = 0, failed = notDrafted, plain = 0;
            decimal total = 0;
            foreach (var res in results)
            {
                var row = rows[res.Index];
                if (res.Recorded)
                {
                    ok++;
                    total += res.Draft.Target;
                    row.ArchivedFile = res.File;
                    row.Status = $"{Loc.T("Mass.Success")} · {res.Draft.InvoiceNo}";
                    row.State = "ok";
                    if (notes[res.Index] != null)
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
            int empty = Rows.Count(r => r.IsEmpty);
            Summary = Loc.F("Mass.Summary", ok, failed, empty, total.ToString("N2", CultureInfo.InvariantCulture));
            Status = plain > 0 ? Loc.F("Mass.PlainNote", plain) : (ok > 0 ? Loc.T("Mass.ExportHint") : "");
            Progress = 100;
            if (ok > 0) { LoadHistoryNumbers(); main.OnHistoryChanged(); }
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Export PDF (like the Export PDF button of the BATCH STUDIO sheet): a PDF of every filled row to
    /// Desktop\Mass Invoice, named "party amount.pdf". Nothing is archived or recorded. A row archived by
    /// Generate is exported from its archived file, so the PDF matches the archive.
    /// </summary>
    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (IsBusy) return;
        Status = Summary = "";
        if (main.Root == null) { Status = "No entity is open."; return; }
        Refresh();
        var filled = Rows.Where(r => !r.IsEmpty).ToList();
        if (filled.Count == 0) { Status = Loc.T("Mass.Empty"); return; }
        foreach (var r in filled.Where(r => !r.IsArchived)) { r.Status = r.State = ""; }
        var plans = CheckRows(filled.Where(r => !r.IsArchived), out int bad);
        if (bad > 0) { Status = Loc.F("Mass.FixRows", bad); return; }

        IsBusy = true;
        Progress = 0;
        var folder = Path.Combine(InvoiceService.DesktopFolder(), "Mass Invoice");
        var sheets = main.InvoiceSheetNames();
        int ok = 0, failed = 0, done = 0, total = filled.Count;
        try
        {
            Status = Loc.F("Mass.Exporting", total);
            // archived rows: straight from the archive file
            foreach (var r in filled.Where(r => r.IsArchived))
            {
                var keepState = r.State;
                try
                {
                    var amount = decimal.TryParse(r.AmountText.Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : (decimal?)null;
                    var dest = InvoiceService.FreePdfPath(folder, r.PartyName, amount);
                    var file = r.ArchivedFile!;
                    await Task.Run(() => ExcelPdf.Export(file, dest, sheets));
                    ok++;
                }
                catch (Exception ex) { failed++; r.Status = $"PDF: {ex.Message}"; r.State = "fail"; }
                r.State = r.State == "fail" ? "fail" : keepState;
                Progress = 100.0 * ++done / total;
            }
            // other rows: a fresh invoice for the PDF only
            var drafts = new List<(Plan Plan, InvoiceDraft Draft)>();
            foreach (var pl in plans)
            {
                try { drafts.Add((pl, await DraftAsync(pl))); }
                catch (Exception ex) { failed++; pl.Row.Status = ex.Message; pl.Row.State = "fail"; Progress = 100.0 * ++done / total; }
            }
            var ui = Application.Current?.Dispatcher;
            var outcome = await Task.Run(() =>
            {
                var list = new List<(MassRowViewModel Row, string? Error)>();
                using var session = ExcelInvoiceEngine.BeginSession();
                foreach (var (pl, d) in drafts)
                {
                    string? error = null;
                    var tmp = Path.Combine(Path.GetTempPath(), $"BS_PDF_{Guid.NewGuid():N}.xlsx");
                    try
                    {
                        ui?.Invoke(() => { pl.Row.Status = Loc.T("Mass.Writing"); pl.Row.State = "run"; });
                        InvoiceService.FileWriter(d, tmp);
                        var dest = InvoiceService.FreePdfPath(folder, d.PartyName, d.Target);
                        ExcelPdf.Export(tmp, dest, sheets);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    list.Add((pl.Row, error));
                    ui?.Invoke(() =>
                    {
                        Progress = 100.0 * ++done / total;
                        pl.Row.Status = error == null ? Loc.T("Mass.PdfDone") : $"PDF: {error}";
                        pl.Row.State = error == null ? "ok" : "fail";
                    });
                }
                return list;
            });
            ok += outcome.Count(o => o.Error == null);
            failed += outcome.Count(o => o.Error != null);
            Summary = Loc.F("Mass.PdfSummary", ok, failed);
            Status = ok > 0 ? Loc.F("Mass.PdfSaved", folder) : "";
            Progress = 100;
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
