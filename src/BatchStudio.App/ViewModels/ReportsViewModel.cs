using System.Collections.ObjectModel;
using BatchStudio.App.Services;
using BatchStudio.Core.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

public sealed record ReportParty(string Id, string Name);

/// <summary>Reports page: suppliers / clients list; View Report shows the statement beside it, Export Report saves the PDF.</summary>
public sealed partial class ReportsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<ReportParty> Rows { get; } = [];
    public IReadOnlyList<string> Types => [Loc.T("Nav.Suppliers"), Loc.T("Nav.Clients")];

    [ObservableProperty] private int _typeIndex;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ReportParty? _selected;
    [ObservableProperty] private CompanyReportViewModel? _report;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasReport => Report != null;
    public bool NoReport => Report == null;
    partial void OnReportChanged(CompanyReportViewModel? value)
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(NoReport));
    }

    private bool IsClients => TypeIndex == 1;
    private string PartyType => IsClients ? "CLIENTS" : "SUPPLIERS";

    /// <summary>The ID clicked: the details of that supplier / client.</summary>
    [RelayCommand]
    private void OpenParty(ReportParty? party)
    {
        if (party != null) main.ShowParty(PartyType, party.Id, party.Name);
    }

    partial void OnTypeIndexChanged(int value)
    {
        Report = null;
        Refresh();
    }

    partial void OnSearchChanged(string value) => Refresh();

    // while a report is open, picking another company shows its report
    partial void OnSelectedChanged(ReportParty? value)
    {
        if (value != null && Report != null) ViewReport();
    }

    public void Refresh()
    {
        var keep = Selected?.Name;
        Rows.Clear();
        var page = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == (IsClients ? "clients" : "suppliers"));
        if (page != null)
        {
            var q = Search.Trim();
            foreach (var r in page.Session.Current.Rows)
            {
                var id = r.Length > 0 ? r[0] ?? "" : "";
                var name = r.Length > 1 ? (r[1] ?? "").Trim() : "";
                if (name.Length == 0) continue;
                if (q.Length > 0 && !name.Contains(q, StringComparison.CurrentCultureIgnoreCase) && !id.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
                Rows.Add(new ReportParty(id, name));
            }
        }
        CountText = Loc.F("Reports.Count", Rows.Count);
        var again = keep == null ? null : Rows.FirstOrDefault(r => r.Name == keep);
        Selected = again ?? Rows.FirstOrDefault();
    }

    [RelayCommand]
    private void ViewReport()
    {
        if (Selected is not { } p) return;
        var vm = main.BuildCompanyReport(p.Name, PartyType, new DashboardFilter());
        if (vm == null) return;
        vm.Embedded = true;
        Report = vm;
        Status = "";
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (Selected is not { } p) return;
        var vm = main.BuildCompanyReport(p.Name, PartyType, new DashboardFilter());
        if (vm == null) return;
        IsBusy = true;
        Status = Loc.T("Rep.Exporting");
        try { Status = await vm.ExportPdfCoreAsync(); }
        finally { IsBusy = false; }
    }
}
