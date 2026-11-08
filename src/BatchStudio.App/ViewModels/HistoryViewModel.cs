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

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public IReadOnlyList<string> Periods => HistoryPeriods.All;
    public IReadOnlyList<string> WeekDays { get; } = ["Saturday", "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _tab = "ALL";
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
                var p = InvoiceHistoryBook.ResolvePath(Root, r.FilePath);
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

    private void ApplyFilter()
    {
        var keep = Selected?.InvoiceNo;
        var q = Query();
        var shown = _all.Where(r => q.Matches(r.Record)).ToList();
        Rows.Clear();
        foreach (var r in shown) Rows.Add(r);
        CountText = Loc.F("Hist.Count", shown.Count);
        TotalText = shown.Sum(r => r.Amount ?? 0).ToString("N2", CultureInfo.InvariantCulture);
        Selected = Rows.FirstOrDefault(r => r.InvoiceNo == keep);
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
        var dest = Path.Combine(Desktop(), InvoiceService.SafeName(r.InvoiceNo) + ".pdf");
        if (File.Exists(dest) && !Dialogs.Confirm(Loc.F("Hist.ReplacePdf", Path.GetFileName(dest)))) return;
        await RunBusy(async () =>
        {
            await Task.Run(() => ExcelPdf.Export(r.ResolvedPath, dest));
            Status = Loc.F("Hist.PdfSaved", dest);
        });
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } r || Root == null) return;
        try
        {
            if (r.FileExists)
            {
                if (!Dialogs.Confirm(Loc.F("Hist.ConfirmDelete", r.InvoiceNo, r.ResolvedPath))) return;
                InvoiceHistoryBook.Remove(Root, HistoryPath, r.InvoiceNo, deleteFile: true, _main.Settings.KeepBackups);
            }
            else
            {
                if (!Dialogs.Confirm(Loc.F("Hist.ConfirmRemoveRecord", r.InvoiceNo))) return;
                InvoiceHistoryBook.Remove(Root, HistoryPath, r.InvoiceNo, deleteFile: false, _main.Settings.KeepBackups);
            }
            Status = Loc.F("Hist.Removed", r.InvoiceNo);
            Reload();
            _main.Home.Refresh();
        }
        catch (FileLockedException ex) { Status = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
    }

    [RelayCommand]
    private void Relink()
    {
        if (Selected is not { } r || Root == null) return;
        var file = Dialogs.PickFile("Excel (*.xlsx)|*.xlsx");
        if (file == null) return;
        try
        {
            // the chosen file must be this invoice (V28 checked the invoice number inside it)
            if (!FileMentions(file, r.InvoiceNo) && !Dialogs.Confirm(Loc.F("Hist.RelinkMismatch", r.InvoiceNo)))
                return;
            InvoiceHistoryBook.Relink(Root, HistoryPath, r.InvoiceNo, file, _main.Settings.KeepBackups);
            Status = Loc.T("Hist.Relinked");
            Reload();
        }
        catch (FileLockedException ex) { Status = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
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

    [RelayCommand]
    private void CompanyReport()
    {
        if (Selected is { } r)
            _main.ShowCompanyReport(r.Name, r.PartyType, new BatchStudio.Core.Reports.DashboardFilter { WeekStart = HistoryPeriods.ParseWeekStart(WeekStart) });
    }

    [RelayCommand]
    private void CheckFiles()
    {
        if (Root == null) return;
        try
        {
            int n = InvoiceHistoryBook.UpdateStatuses(Root, HistoryPath, _main.Settings.KeepBackups);
            Reload();
            Status = Loc.F("Hist.Checked", MissingCount, n);
        }
        catch (FileLockedException ex) { Status = Loc.F("Inv.HistoryLocked", Path.GetFileName(ex.FilePath)); }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
    }

    [RelayCommand]
    private void OpenSheet()
    {
        if (Root != null) Shell.Open(HistoryPath);
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
