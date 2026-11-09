using System.Collections.ObjectModel;
using System.Globalization;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

public sealed class ReportLine
{
    public required int Index { get; init; }
    public required HistoryRecord Record { get; init; }
    public required string ResolvedPath { get; init; }
    public string No => $"INV-{Index}";
    public decimal? Amount => Record.Amount;
    public string DateText => Record.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";
    public string InvoiceNo => Record.InvoiceNo;
    public bool FileExists => ResolvedPath.Length > 0 && File.Exists(ResolvedPath);
}

/// <summary>Account statement of one company (V28 "Supplier Rebort").</summary>
public sealed partial class CompanyReportViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly object? _returnPage;
    private bool _loading;

    public string Company { get; }
    public string Type { get; }
    public ObservableCollection<ReportLine> Lines { get; } = [];
    public IReadOnlyList<string> Periods => HistoryPeriods.All;

    [ObservableProperty] private string _period;
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _nativeName = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _invoicesText = "";
    [ObservableProperty] private string _banksText = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _descriptionDirty;
    [ObservableProperty] private ReportLine? _selected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public bool IsCustom => Period == HistoryPeriods.CustomRange;

    /// <summary>Shown inside the Reports page (no Back button).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBack))]
    private bool _embedded;
    public bool ShowBack => !Embedded;
    public string TypeText => Type == "SUPPLIERS" ? Loc.T("Nav.Suppliers") : Loc.T("Nav.Clients");
    private CompanyReport? _report;

    public CompanyReportViewModel(MainViewModel main, string company, string type, DashboardFilter filter, object? returnPage)
    {
        _main = main;
        _returnPage = returnPage;
        Company = company;
        Type = type;
        _period = filter.Period;
        _from = filter.From;
        _to = filter.To;
        Load();
    }

    private string Root => _main.Root!;

    private DashboardFilter Filter() => new()
    {
        Period = Period, From = From, To = To, WeekStart = HistoryPeriods.ParseWeekStart(_main.History.WeekStart),
    };

    private DataSetViewModel? MasterPage => _main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == (Type == "SUPPLIERS" ? "suppliers" : "clients"));

    public void Load()
    {
        _loading = true;
        try
        {
            var path = DataSets.InvoiceHistory.FullPath(Root);
            var history = File.Exists(path) ? InvoiceHistoryBook.Read(path) : [];
            var f = Filter();
            _report = CompanyReports.Build(Company, Type, history, MasterPage?.Session.Current, f, CompanyReports.PeriodLabel(f));
            Name = _report.Name;
            NativeName = _report.NativeName;
            Category = _report.Category;
            TotalText = _report.Total.ToString("N2", CultureInfo.InvariantCulture);
            InvoicesText = Loc.F("Rep.Invoices", _report.Invoices.Count);
            BanksText = _report.BankAccounts >= 0 ? Loc.F("Rep.Banks", _report.BankAccounts) : "";
            if (!DescriptionDirty) Description = _report.Description;
            Lines.Clear();
            int i = 0;
            foreach (var r in _report.Invoices)
                Lines.Add(new ReportLine { Index = ++i, Record = r, ResolvedPath = InvoiceHistoryBook.ResolvePath(_main.Workspace!.ArchiveRoot, r.FilePath) });
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { _loading = false; }
    }

    partial void OnPeriodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (value == HistoryPeriods.CustomRange && From == null && To == null)
        {
            From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            To = DateTime.Today;
        }
        Load();
    }
    partial void OnFromChanged(DateTime? value) { if (IsCustom && _report != null) Load(); }
    partial void OnToChanged(DateTime? value) { if (IsCustom && _report != null) Load(); }
    partial void OnDescriptionChanged(string value) { if (!_loading) DescriptionDirty = true; }

    [RelayCommand]
    private void Back() => _main.CurrentPage = _returnPage ?? _main.Dashboard;

    /// <summary>Saves "Company's Activities" back to Suppliers / Clients (V28 SaveEditedDescription).</summary>
    [RelayCommand]
    private async Task SaveDescriptionAsync()
    {
        var page = MasterPage;
        if (page == null) return;
        int n = page.SetWhere(name => DashboardEngine.SameCompany(name, Company),
            t => CompanyReports.DescriptionColumn(t), Description);
        if (n == 0) { Status = Loc.T("Rep.NotFound"); return; }
        IsBusy = true;
        Status = Loc.T("Status.SavingWorkbook");
        try { await page.SaveAsync(); }
        finally { IsBusy = false; }
        DescriptionDirty = page.IsDirty;
        Status = page.IsDirty ? page.Status : Loc.F("Rep.DescSaved", n);
    }

    [RelayCommand]
    private void OpenInvoice(ReportLine? line)
    {
        line ??= Selected;
        if (line == null) return;
        if (!line.FileExists) { Status = Loc.F("Hist.FileMissing", line.InvoiceNo); return; }
        Shell.Open(line.ResolvedPath);
    }

    [RelayCommand]
    private async Task InvoicePdfAsync(ReportLine? line)
    {
        line ??= Selected;
        if (line == null) return;
        if (!line.FileExists) { Status = Loc.F("Hist.FileMissing", line.InvoiceNo); return; }
        var dest = InvoiceService.DesktopPdfPath(line.Record.Name, line.Amount);
        IsBusy = true;
        try
        {
            await Task.Run(() => ExcelPdf.Export(line.ResolvedPath, dest));
            Status = Loc.F("Hist.PdfSaved", dest);
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        IsBusy = true;
        try
        {
            Status = await ExportPdfCoreAsync();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Fills the Supplier Report template and exports it to the Desktop. Returns a status message.</summary>
    public async Task<string> ExportPdfCoreAsync()
    {
        if (_report == null) return "";
        var template = ReportBooks.ReportTemplatePath(Root);
        if (!File.Exists(template)) return Loc.T("Rep.NoTemplate");
        var name = InvoiceService.SafeName(_report.Name).Replace(' ', '_');
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"Supplier_Report_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        var rep = _report;
        var withDescription = new CompanyReport
        {
            Name = rep.Name, Type = rep.Type, NativeName = rep.NativeName, Category = rep.Category,
            Description = Description, BankAccounts = rep.BankAccounts, PeriodLabel = rep.PeriodLabel, Invoices = rep.Invoices,
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"BS_report_{Guid.NewGuid():N}.xlsx");
        try
        {
            await Task.Run(() =>
            {
                ReportBooks.FillReport(template, withDescription, tmp);
                ExcelPdf.Export(tmp, dest);
            });
            Shell.Open(dest);
            return Loc.F("Hist.PdfSaved", dest);
        }
        catch (Exception ex) { return $"{Loc.T("Status.Error")}: {ex.Message}"; }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
