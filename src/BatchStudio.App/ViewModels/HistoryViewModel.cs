using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

public sealed class HistoryRow
{
    public required HistoryRecord Record { get; init; }
    public required string ResolvedPath { get; init; }
    public bool FileExists { get; init; }
    public string No => Record.No;
    public string Name => Record.Name;
    public decimal? Amount => Record.Amount;
    public string DateText => Record.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";
    public DateTime SortDate => Record.Date ?? DateTime.MinValue;
    public string InvoiceNo => Record.InvoiceNo;
    public string PartyType => Record.PartyType;
    public string Template => Record.Template;
    public string StatusText => FileExists ? Loc.T("Hist.Available") : Loc.T("Hist.Missing");
}

/// <summary>INVOICE HISTORY page: the V28 sheet's search, ALL/SUPPLIERS/CLIENTS, period filter, total and actions.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchDelay;
    private List<HistoryRow> _all = [];

    /// <summary>The invoice number (#) clicked: the details of the supplier / client in the Name column.</summary>
    [RelayCommand]
    private void OpenParty(HistoryRow? row)
    {
        if (row != null) _main.ShowParty(row.PartyType, null, row.Name);
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public IReadOnlyList<string> Periods => HistoryPeriods.All;
    public IReadOnlyList<string> WeekDays { get; } = ["Saturday", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _tab = "ALL";
    public IReadOnlyList<string> Tabs { get; } = ["ALL", "SUPPLIERS", "CLIENTS"];
    [ObservableProperty] private string _period = HistoryPeriods.AllTime;
    [ObservableProperty] private string _weekStart = "Saturday";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private HistoryRow? _selected;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _missingCount;

    public bool IsCustom => Period == HistoryPeriods.CustomRange;
    public bool HasSelection => Selected != null;
    public bool SelectedMissing => Selected is { FileExists: false };

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
    }

    private string? Root => _main.Root;
    private string HistoryPath => DataSets.InvoiceHistory.FullPath(Root!);
    private string Archive => _main.Workspace!.ArchiveRoot;

    public void Reload()
    {
        if (Root == null) return;
        try
        {
            if (!File.Exists(HistoryPath)) { _all = []; ApplyFilter(); return; }
            var recs = InvoiceHistoryBook.Read(HistoryPath);
            var ws = InvoiceHistoryBook.ReadWeekStart(HistoryPath);
            if (ws != null && _all.Count == 0) WeekStart = ws;
            _all = recs.Select(r =>
            {
                var p = InvoiceHistoryBook.ResolvePath(Archive, r.FilePath);
                return new HistoryRow { Record = r, ResolvedPath = p, FileExists = p.Length > 0 && File.Exists(p) };
            }).ToList();
            MissingCount = _all.Count(r => !r.FileExists);
            ApplyFilter();
            Status = "";
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    private HistoryQuery Query() => new()
    {
        Text = SearchText, Tab = Tab, Period = Period, WeekStart = HistoryPeriods.ParseWeekStart(WeekStart),
        From = From, To = To, Today = DateTime.Today,
    };

    /// <summary>Asks the view to show the newest invoice (bottom of the list).</summary>
    public event Action? ScrollToEndRequested;
    private string? _lastQuery;
    private int _lastCount = -1;

    private void ApplyFilter()
    {
        var keep = Selected?.InvoiceNo;
        var q = Query();
        var sig = $"{q.Text}|{q.Tab}|{q.Period}|{q.WeekStart}|{q.From}|{q.To}";
        var shown = _all.Where(r => q.Matches(r.Record)).ToList();
        Rows.Clear();
        foreach (var r in shown) Rows.Add(r);
        CountText = Loc.F("Hist.Count", shown.Count);
        TotalText = shown.Sum(r => r.Amount ?? 0).ToString("N2", CultureInfo.InvariantCulture);
        Selected = Rows.FirstOrDefault(r => r.InvoiceNo == keep);
        // a new filter, or new invoices, jump to the newest one; a plain refresh keeps the scroll position
        if (sig != _lastQuery || shown.Count > _lastCount) ScrollToEndRequested?.Invoke();
        _lastQuery = sig;
        _lastCount = shown.Count;
    }

    partial void OnSearchTextChanged(string value) { _searchDelay.Stop(); _searchDelay.Start(); }
    partial void OnTabChanged(string value) => ApplyFilter();
    partial void OnPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (value == HistoryPeriods.CustomRange && From == null && To == null)
        {
            From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            To = DateTime.Today;
        }
        ApplyFilter();
    }
    partial void OnWeekStartChanged(string value) => ApplyFilter();
    partial void OnFromChanged(DateTime? value) { if (IsCustom) ApplyFilter(); }
    partial void OnToChanged(DateTime? value) { if (IsCustom) ApplyFilter(); }
    partial void OnSelectedChanged(HistoryRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedMissing));
    }

    [RelayCommand] private void ShowAll() => Tab = "ALL";
    [RelayCommand] private void ShowSuppliers() => Tab = "SUPPLIERS";
    [RelayCommand] private void ShowClients() => Tab = "CLIENTS";

    // ------------------------------------------------------------------ row actions
    [RelayCommand]
    private void OpenInvoice(HistoryRow? row)
    {
        row ??= Selected;
        if (row == null) return;
        if (!row.FileExists) { Status = Loc.F("Hist.FileMissing", row.InvoiceNo); return; }
        Shell.Open(row.ResolvedPath);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Selected is { FileExists: true } r) Shell.ShowInFolder(r.ResolvedPath);
    }

    [RelayCommand]
    private async Task ExportInvoicePdfAsync()
    {
        if (Selected is not { } r) return;
        if (!r.FileExists) { Status = Loc.F("Hist.FileMissing", r.InvoiceNo); return; }
        var dest = InvoiceService.DesktopPdfPath(r.Name, r.Amount);
        await RunBusy(async () =>
        {
            var sheets = _main.InvoiceSheetNames();
            await Task.Run(() => ExcelPdf.Export(r.ResolvedPath, dest, sheets));
            Status = Loc.F("Hist.PdfSaved", dest);
        });
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } r || _main.Workspace is not { } ws) return;
        bool deleteFile = r.FileExists;
        if (deleteFile ? !Dialogs.Confirm(Loc.F("Hist.ConfirmDelete", r.InvoiceNo, r.ResolvedPath))
                       : !Dialogs.Confirm(Loc.F("Hist.ConfirmRemoveRecord", r.InvoiceNo))) return;
        int keep = _main.Settings.KeepBackups;
        await WriteAsync(() =>
        {
            var file = ws.Change(ed => InvoiceHistoryBook.Remove(ed, ws.ArchiveRoot, r.InvoiceNo, deleteFile), keep);
            if (file != null) InvoiceHistoryBook.DeleteArchivedFile(file, ws.ArchiveRoot);
        }, Loc.F("Hist.Removed", r.InvoiceNo));
    }

    [RelayCommand]
    private async Task RelinkAsync()
    {
        if (Selected is not { } r || _main.Workspace is not { } ws) return;
        var file = Dialogs.PickFile("Excel (*.xlsx)|*.xlsx", Directory.Exists(ws.ArchiveRoot) ? ws.ArchiveRoot : null);
        if (file == null) return;
        // the chosen file must be this invoice (V28 checked the invoice number inside it)
        if (!FileMentions(file, r.InvoiceNo) && !Dialogs.Confirm(Loc.F("Hist.RelinkMismatch", r.InvoiceNo))) return;
        int keep = _main.Settings.KeepBackups;
        await WriteAsync(() => ws.Change(ed => InvoiceHistoryBook.Relink(ed, r.InvoiceNo, file), keep), Loc.T("Hist.Relinked"));
    }

    /// <summary>Runs a change of the workbook (through Excel) without freezing the window.</summary>
    private async Task WriteAsync(Action write, string done) => await WriteAsync(write, () => done);

    private async Task WriteAsync(Action write, Func<string> done)
    {
        IsBusy = true;
        Status = Loc.T("Status.SavingWorkbook");
        string? error = null;
        try { await Task.Run(write); }
        catch (FileLockedException ex) { error = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { error = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
        Reload();
        Status = error ?? done();
        _main.Home.Refresh();
        _main.Dashboard.Refresh();
    }

    private static bool FileMentions(string xlsx, string text)
    {
        try
        {
            using var wb = PackageRepair.OpenWorkbook(xlsx);
            return wb.Worksheets.Any(ws => ws.CellsUsed().Any(c => ExcelTableStore.CellText(c).Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    /// <summary>Runs a command from a row's Actions list (parameter: [row, action]).</summary>
    [RelayCommand]
    private async Task RunActionAsync(object[]? args)
    {
        if (args is not [HistoryRow row, string action]) return;
        Selected = row;
        switch (action)
        {
            case "open": OpenOrRepair(row); break;
            case "remove": await RemoveAsync(); break;
            case "pdf": await ExportInvoicePdfAsync(); break;
            case "report": CompanyReport(); break;
            case "exportreport": await ExportFullReportAsync(); break;
        }
    }

    /// <summary>
    /// Open Invoice like the V28 sheet: for a linked template the user can restore it for Smart Invoice Repair
    /// (YES), open the archived file (NO) or cancel.
    /// </summary>
    private void OpenOrRepair(HistoryRow row)
    {
        if (!row.FileExists) { Status = Loc.F("Hist.FileMissing", row.InvoiceNo); return; }
        bool canRepair = Root != null && row.Template.Length > 0 &&
            BatchStudio.Core.Templates.TemplateRegistry.Scan(Root).Any(t => t.Map != null &&
                string.Equals(t.Name, row.Template.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!canRepair) { OpenInvoice(row); return; }
        switch (Dialogs.Ask(Loc.F("Rep.Ask", row.InvoiceNo, row.Template)))
        {
            case true: _main.StartInvoiceRepair(row.Template, row.InvoiceNo, row.PartyType, row.ResolvedPath); break;
            case false: OpenInvoice(row); break;
        }
    }

    /// <summary>"Export Full Report": the company's statement as PDF on the Desktop.</summary>
    private async Task ExportFullReportAsync()
    {
        if (Selected is not { } r) return;
        var vm = _main.BuildCompanyReport(r.Name, r.PartyType,
            new BatchStudio.Core.Reports.DashboardFilter { WeekStart = HistoryPeriods.ParseWeekStart(WeekStart) });
        if (vm == null) return;
        IsBusy = true;
        try { Status = await vm.ExportPdfCoreAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CompanyReport()
    {
        if (Selected is { } r)
            _main.ShowCompanyReport(r.Name, r.PartyType, new BatchStudio.Core.Reports.DashboardFilter { WeekStart = HistoryPeriods.ParseWeekStart(WeekStart) });
    }

    [RelayCommand]
    private async Task CheckFilesAsync()
    {
        if (_main.Workspace is not { } ws || Root == null) return;
        var recs = InvoiceHistoryBook.Read(HistoryPath);
        if (InvoiceHistoryBook.StatusChangesNeeded(ws.ArchiveRoot, recs) == 0)
        {
            Reload();
            Status = Loc.F("Hist.Checked", MissingCount, 0);
            return;
        }
        int n = 0, keep = _main.Settings.KeepBackups;
        await WriteAsync(() => n = ws.Change(ed => InvoiceHistoryBook.UpdateStatuses(ed, ws.ArchiveRoot), keep),
                         () => Loc.F("Hist.Checked", MissingCount, n));
    }

    [RelayCommand]
    private void OpenSheet()
    {
        if (_main.Workspace != null) Shell.Open(_main.Workspace.WorkbookPath);
    }

    /// <summary>EXPORT PDF of the sheet: name follows the period ("This Month Report.pdf"), saved on the Desktop.</summary>
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (Root == null) return;
        var name = InvoiceService.SafeName(HistoryPeriods.ReportName(Period));
        var dest = Path.Combine(Desktop(), name + ".pdf");
        if (File.Exists(dest) && !Dialogs.Confirm(Loc.F("Hist.ReplacePdf", Path.GetFileName(dest)))) return;
        var q = Query();
        await RunBusy(async () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"BS_report_{Guid.NewGuid():N}.xlsx");
            try
            {
                await Task.Run(() =>
                {
                    InvoiceHistoryBook.BuildReport(HistoryPath, q.Matches, Period, tmp);
                    ExcelPdf.Export(tmp, dest);
                });
                Status = Loc.F("Hist.PdfSaved", dest);
                Shell.Open(dest);
            }
            finally { try { File.Delete(tmp); } catch { } }
        });
    }

    private async Task RunBusy(Func<Task> work)
    {
        IsBusy = true;
        Status = "";
        try { await work(); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private static string Desktop() => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
}
