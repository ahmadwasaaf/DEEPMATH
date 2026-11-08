using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

public sealed class RankItem
{
    public required RankRow Row { get; init; }
    public double Bar { get; init; }               // 0..1 of the largest amount (visual bar)
    public string RankText => $"Rank-{Row.Rank}";
    public string Name => Row.Name;
    public string Type => Row.Type;
    public decimal Amount => Row.Amount;
    public int Count => Row.Count;
    public string Category => Row.Category;
}

/// <summary>Dashboard page (port of the V28 Dashboard sheet). Also keeps Dashboard\Dashboard.xlsx up to date.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _writeDelay;
    private List<HistoryRecord> _history = [];
    private DashboardResult? _result;
    private bool _loadedFilters;
    private bool _suspend;

    public ObservableCollection<RankItem> Rows { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public IReadOnlyList<string> Periods => HistoryPeriods.All;
    public IReadOnlyList<string> Modes { get; } = ["ALL", "SUPPLIERS", "CLIENTS"];
    public IReadOnlyList<string> Rankings { get; } = ["TOP", "LESS"];

    [ObservableProperty] private string _period = HistoryPeriods.AllTime;
    [ObservableProperty] private string _mode = "ALL";
    [ObservableProperty] private string _ranking = "TOP";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _totalText = "0.00";
    [ObservableProperty] private string _suppliersText = "";
    [ObservableProperty] private string _suppliersTotalText = "0.00";
    [ObservableProperty] private string _clientsText = "";
    [ObservableProperty] private string _clientsTotalText = "0.00";
    [ObservableProperty] private string _invoicesText = "";
    [ObservableProperty] private string _rankingTitle = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _selectedSuggestion;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsCustom => Period == HistoryPeriods.CustomRange;
    public bool HasSuggestions => Suggestions.Count > 0;

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        _writeDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _writeDelay.Tick += (_, _) => { _writeDelay.Stop(); WriteExcel(); };
    }

    private string? Root => _main.Root;

    /// <summary>Called when another entity is opened.</summary>
    public void Reset()
    {
        _loadedFilters = false;
        _history = [];
        _result = null;
        Rows.Clear();
    }

    public DashboardFilter Filter() => new()
    {
        Period = Period, Mode = Mode, Ranking = Ranking, From = From, To = To,
        WeekStart = HistoryPeriods.ParseWeekStart(_main.History.WeekStart),
    };

    /// <summary>Reloads history + master data and recomputes. <paramref name="writeExcel"/> also refreshes Dashboard.xlsx.</summary>
    public void Refresh(bool writeExcel = true)
    {
        if (Root == null) return;
        try
        {
            var path = DataSets.InvoiceHistory.FullPath(Root);
            _history = File.Exists(path) ? InvoiceHistoryBook.Read(path) : [];
            if (!_loadedFilters) LoadFiltersFromExcel();
            Compute();
            if (writeExcel) { _writeDelay.Stop(); _writeDelay.Start(); }
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
    }

    /// <summary>Starts from the filters saved in Dashboard.xlsx (G3 / G4 / G5, custom dates in P2 / P4).</summary>
    private void LoadFiltersFromExcel()
    {
        _loadedFilters = true;
        var p = ReportBooks.DashboardPath(Root!);
        if (!File.Exists(p)) return;
        try
        {
            using var wb = PackageRepair.OpenWorkbook(p);
            var ws = wb.Worksheet(1);
            _suspend = true;
            var g3 = ExcelTableStore.CellText(ws.Cell("G3")).Trim();
            if (HistoryPeriods.All.Contains(g3)) Period = g3;
            var g4 = ExcelTableStore.CellText(ws.Cell("G4")).Trim().ToUpperInvariant();
            if (Modes.Contains(g4)) Mode = g4;
            var g5 = ExcelTableStore.CellText(ws.Cell("G5")).Trim().ToUpperInvariant();
            if (Rankings.Contains(g5)) Ranking = g5;
            if (ws.Cell("P2").Value.IsDateTime) From = ws.Cell("P2").Value.GetDateTime();
            else if (ws.Cell("P2").Value.IsNumber) From = DateTime.FromOADate(ws.Cell("P2").Value.GetNumber());
            if (ws.Cell("P4").Value.IsDateTime) To = ws.Cell("P4").Value.GetDateTime();
            else if (ws.Cell("P4").Value.IsNumber) To = DateTime.FromOADate(ws.Cell("P4").Value.GetNumber());
        }
        catch { }
        finally { _suspend = false; }
    }

    private void Compute()
    {
        var sup = _main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers")?.Session.Current;
        var cli = _main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "clients")?.Session.Current;
        var r = DashboardEngine.Compute(_history, DashboardEngine.Categories(sup, cli), Filter());
        _result = r;
        string M(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
        TotalText = M(r.Total);
        SuppliersText = Loc.F("Dash.SupplierCount", r.SupplierCount);
        SuppliersTotalText = M(r.SuppliersTotal);
        ClientsText = Loc.F("Dash.ClientCount", r.ClientCount);
        ClientsTotalText = M(r.ClientsTotal);
        InvoicesText = Loc.F("Hist.Count", r.Invoices);
        RankingTitle = Loc.T(Ranking == "LESS" ? "Dash.Lowest" : "Dash.Top");
        var max = r.Top20.Select(x => x.Amount).DefaultIfEmpty(0).Max();
        Rows.Clear();
        foreach (var x in r.Top20)
            Rows.Add(new RankItem { Row = x, Bar = max > 0 ? (double)(x.Amount / max) : 0 });
    }

    /// <summary>The workbook's own Dashboard sheet keeps working in Excel; the program only shows the numbers.</summary>
    private void WriteExcel()
    {
        if (_result != null) Status = Loc.T("Dash.Updated") + $"  ·  {DateTime.Now:HH:mm:ss}";
    }

    partial void OnPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (_suspend) return;
        if (value == HistoryPeriods.CustomRange && From == null && To == null)
        {
            _suspend = true;
            From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            To = DateTime.Today;
            _suspend = false;
        }
        Recompute();
    }
    partial void OnModeChanged(string value) { if (!_suspend) Recompute(); }
    partial void OnRankingChanged(string value) { if (!_suspend) Recompute(); }
    partial void OnFromChanged(DateTime? value) { if (!_suspend && IsCustom) Recompute(); }
    partial void OnToChanged(DateTime? value) { if (!_suspend && IsCustom) Recompute(); }

    private void Recompute()
    {
        if (Root == null) return;
        Compute();
        _writeDelay.Stop();
        _writeDelay.Start();
    }

    // ------------------------------------------------------------------ company search (Dashboard "Search")
    partial void OnSearchChanged(string value)
    {
        Suggestions.Clear();
        var q = HistoryQuery.Normalize(value);
        if (q.Length >= 2)
        {
            var names = _history.Select(h => DashboardEngine.BaseName(h.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => HistoryQuery.Normalize(n).Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Take(12);
            foreach (var n in names) Suggestions.Add(n);
        }
        OnPropertyChanged(nameof(HasSuggestions));
    }

    partial void OnSelectedSuggestionChanged(string? value)
    {
        if (value == null) return;
        var name = value;
        Search = "";
        _main.ShowCompanyReport(name, null, Filter());
    }

    [RelayCommand]
    private void ViewReport(RankItem? item)
    {
        if (item != null) _main.ShowCompanyReport(item.Name, item.Type, Filter());
    }

    [RelayCommand]
    private async Task ExportReportAsync(RankItem? item)
    {
        if (item == null) return;
        var vm = _main.BuildCompanyReport(item.Name, item.Type, Filter());
        if (vm == null) return;
        IsBusy = true;
        try { Status = await vm.ExportPdfCoreAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>Export PDF (like the Dashboard sheet's button): the Dashboard sheet with the current filters, straight to the Desktop.</summary>
    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (_main.Workspace is not { } ws || _result == null) return;
        if (!ws.Sheets.Contains(BatchStudio.Core.Workbook.Workspace.DashboardSheet, StringComparer.OrdinalIgnoreCase)) { Status = Loc.T("Dash.NoExcel"); return; }
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                $"BATCH_STUDIO_DASHBOARD_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        var result = _result;
        var filter = Filter();
        IsBusy = true;
        Status = Loc.T("Rep.Exporting");
        try
        {
            var book = ws.WorkbookPath;
            await Task.Run(() =>
            {
                // a private copy of the workbook: its real Dashboard sheet gets the current numbers and is exported
                var tmp = Path.Combine(Path.GetTempPath(), $"BS_dash_{Guid.NewGuid():N}{Path.GetExtension(book)}");
                try
                {
                    File.WriteAllBytes(tmp, BatchStudio.Core.Data.PackageRepair.ReadAllShared(book));
                    using var ed = (ExcelWorkbookEditor)ExcelWorkbookEditor.Open(tmp);
                    ReportBooks.WriteDashboard(ed.Sheet(BatchStudio.Core.Workbook.Workspace.DashboardSheet), result, filter);
                    ed.ExportSheetPdf(BatchStudio.Core.Workbook.Workspace.DashboardSheet, dest);
                }
                finally { try { File.Delete(tmp); } catch { } }
            });
            Status = Loc.F("Dash.PdfSaved", dest);
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenExcel()
    {
        if (_main.Workspace != null) Shell.Open(_main.Workspace.WorkbookPath);
    }
}
