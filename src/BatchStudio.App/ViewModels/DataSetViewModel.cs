using System.Collections.ObjectModel;
using System.Data;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

/// <summary>One editable field of the selected row, shown in the details pane.</summary>
public sealed partial class FieldEditViewModel : ObservableObject
{
    private readonly DataRowView _row;
    private readonly string _column;
    public string Caption { get; }
    public bool IsLong { get; }

    public FieldEditViewModel(DataRowView row, DataColumn column)
    {
        _row = row;
        _column = column.ColumnName;
        Caption = column.Caption;
        var v = row[_column] as string ?? "";
        IsLong = v.Contains('\n') || v.Length > 60 ||
                 Caption.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
                 Caption.Contains("Bank", StringComparison.OrdinalIgnoreCase) ||
                 Caption.Contains("Activit", StringComparison.OrdinalIgnoreCase);
    }

    public string Value
    {
        get => _row[_column] as string ?? "";
        set
        {
            if (Value == value) return;
            _row.BeginEdit();
            _row[_column] = value;
            _row.EndEdit();
            OnPropertyChanged();
        }
    }
}

/// <summary>Suppliers / Clients / Items / Categories page: grid + details, synced with the Excel file.</summary>
public sealed partial class DataSetViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _autoSave;
    private readonly DispatcherTimer _retry;
    private bool _loading;

    public DataSetSession Session { get; }
    public string TitleKey { get; }
    public string Icon { get; }

    [ObservableProperty] private DataView? _view;
    [ObservableProperty] private DataRowView? _selectedRow;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isWaitingForExcel;
    [ObservableProperty] private int _rowCount;

    public ObservableCollection<FieldEditViewModel> Fields { get; } = [];
    public DataTable? Table { get; private set; }
    public string Title => Loc.T(TitleKey);

    public DataSetViewModel(MainViewModel main, DataSetSession session, string titleKey, string icon)
    {
        _main = main;
        Session = session;
        TitleKey = titleKey;
        Icon = icon;
        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); if (IsDirty) Save(); };
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _retry.Tick += (_, _) => { if (!ExcelTableStore.IsLocked(Session.FilePath)) { _retry.Stop(); Save(); } };
        LanguageService.Changed += () => OnPropertyChanged(nameof(Title));
    }

    public void Load()
    {
        try
        {
            Session.Load();
            BuildTable(Session.Current);
            IsDirty = false;
            Status = Loc.T("Status.Synced");
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- table <-> DataTable
    private void BuildTable(TableData data)
    {
        _loading = true;
        var keepSel = SelectedRow?[ColName(0)] as string;
        var dt = new DataTable(Session.Definition.Key);
        for (int i = 0; i < data.Headers.Count; i++)
            dt.Columns.Add(new DataColumn(ColName(i), typeof(string)) { Caption = data.Headers[i], DefaultValue = "" });
        foreach (var r in data.Rows)
        {
            var row = dt.NewRow();
            for (int i = 0; i < data.Headers.Count; i++) row[i] = i < r.Length ? r[i] ?? "" : "";
            dt.Rows.Add(row);
        }
        dt.AcceptChanges();
        dt.RowChanged += (_, e) => { if (e.Action is not (DataRowAction.Commit or DataRowAction.Nothing)) MarkDirty(); };
        dt.RowDeleted += (_, _) => MarkDirty();
        Table = dt;
        View = dt.DefaultView;
        ApplyFilter();
        RowCount = dt.Rows.Count;
        _loading = false;
        if (keepSel != null)
            SelectedRow = View.Cast<DataRowView>().FirstOrDefault(v => string.Equals(v[ColName(0)] as string, keepSel, StringComparison.OrdinalIgnoreCase));
    }

    private static string ColName(int i) => "c" + i;

    private TableData ToTableData()
    {
        var t = new TableData { KeyColumn = 0 };
        t.Headers.AddRange(Session.Current.Headers);
        t.Kinds.AddRange(Session.Current.Kinds);
        while (t.Kinds.Count < t.Headers.Count) t.Kinds.Add(ColumnKind.Text);
        if (Table == null) return t;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            var arr = new string[t.Headers.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = (r[i] as string ?? "").Trim();
            if (arr.All(string.IsNullOrEmpty)) continue;
            t.Rows.Add(arr);
        }
        return t;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
        RowCount = Table?.Rows.Cast<DataRow>().Count(r => r.RowState != DataRowState.Deleted) ?? 0;
        if (!IsWaitingForExcel) Status = Loc.T("Status.Unsaved");
        if (_main.Settings.AutoSave)
        {
            _autoSave.Stop();
            _autoSave.Start();
        }
    }

    // ---------------------------------------------------------------- search & selection
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (View == null || Table == null) return;
        var q = SearchText.Trim();
        if (q.Length == 0) { View.RowFilter = ""; return; }
        var esc = q.Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
        View.RowFilter = string.Join(" OR ", Table.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName}] LIKE '%{esc}%'"));
    }

    partial void OnSelectedRowChanged(DataRowView? value)
    {
        Fields.Clear();
        if (value == null || Table == null) return;
        foreach (DataColumn c in Table.Columns) Fields.Add(new FieldEditViewModel(value, c));
    }

    // ---------------------------------------------------------------- commands
    [RelayCommand]
    private void AddRow()
    {
        if (Table == null) return;
        var row = Table.NewRow();
        var id = ToTableData().NextId(Session.Definition.IdPrefix);
        row[0] = id;
        Table.Rows.Add(row);
        SearchText = "";
        SelectedRow = View?.Cast<DataRowView>().LastOrDefault();
    }

    [RelayCommand]
    private void DeleteRow()
    {
        if (SelectedRow == null) return;
        var id = SelectedRow[ColName(0)] as string;
        if (!Dialogs.Confirm($"{Loc.T("Common.Delete")} {id}?")) return;
        SelectedRow.Delete();
        SelectedRow = null;
    }

    [RelayCommand]
    public void Save()
    {
        _autoSave.Stop();
        var data = ToTableData();
        if (data.Rows.Any(r => string.IsNullOrWhiteSpace(r[0])))
        {
            Status = Loc.T("Validate.MissingId");
            return;
        }
        try
        {
            Status = Loc.T("Status.Saving");
            Session.SetCurrent(data);
            Session.Save(_main.Settings.KeepBackups);
            IsDirty = false;
            IsWaitingForExcel = false;
            Table?.AcceptChanges();
            Status = Loc.T("Status.Synced") + $"  ·  {DateTime.Now:HH:mm:ss}";
            if (Session.Definition.Key == "categories") _main.RefreshLists();
        }
        catch (FileLockedException)
        {
            IsWaitingForExcel = true;
            Status = Loc.T("Status.Locked");
            _retry.Start();
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Reload()
    {
        if (IsDirty && !Dialogs.Confirm(Loc.T("Status.Unsaved") + " - " + Loc.T("Common.Reload") + "?")) return;
        Load();
    }

    [RelayCommand]
    private void OpenInExcel()
    {
        if (IsDirty) Save();
        Shell.Open(Session.FilePath);
    }

    [RelayCommand]
    private void OpenFolder() => Shell.ShowInFolder(Session.FilePath);

    // ---------------------------------------------------------------- sync from Excel
    /// <summary>Called on the UI thread when the watcher reports a change to this data set's file.</summary>
    public void OnExternalChange()
    {
        try
        {
            if (IsDirty) Session.SetCurrent(ToTableData());
            var outcome = Session.OnFileChanged();
            switch (outcome)
            {
                case ExternalChangeOutcome.Ignored:
                    return;
                case ExternalChangeOutcome.Reloaded:
                    BuildTable(Session.Current);
                    IsDirty = false;
                    Status = Loc.T("Status.Reloaded") + $"  ·  {DateTime.Now:HH:mm:ss}";
                    break;
                case ExternalChangeOutcome.Merged:
                    BuildTable(Session.Current);
                    Status = Loc.T("Status.Merged");
                    MarkDirty();
                    break;
                case ExternalChangeOutcome.Conflicts:
                    var merge = Session.PendingMerge!;
                    var keys = string.Join("\n", merge.Conflicts.Select(c => "• " + c.Key).Take(20));
                    bool keepMine = Dialogs.Confirm(Loc.F("Conflict.Message", keys));
                    foreach (var c in merge.Conflicts) c.Choice = keepMine ? ConflictChoice.KeepMine : ConflictChoice.TakeExcel;
                    Session.ResolveConflicts();
                    BuildTable(Session.Current);
                    Status = Loc.T("Status.Merged");
                    MarkDirty();
                    break;
            }
            _main.Home.Refresh();
            if (Session.Definition.Key == "categories") _main.RefreshLists();
        }
        catch (IOException)
        {
            // file still being written; the watcher fires again
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    /// <summary>Called on close / entity switch.</summary>
    public bool FlushPending()
    {
        if (!IsDirty) return true;
        Save();
        return !IsDirty;
    }

    public void Stop()
    {
        _autoSave.Stop();
        _retry.Stop();
    }
}
