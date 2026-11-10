using System.Collections.ObjectModel;
using System.Data;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;
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

    private readonly Func<IReadOnlyList<string>?>? _options;
    private IReadOnlyList<string>? _optionList;
    private bool _optionsBuilt;

    /// <summary>Choices for a dropdown field (Category, DEFAULT TEMPLATE, Status); null = free text. Built when first shown.</summary>
    public IReadOnlyList<string>? Options
    {
        get
        {
            if (_optionsBuilt) return _optionList;
            _optionsBuilt = true;
            var options = _options?.Invoke();
            if (options == null) return null;
            var current = _row[_column] as string ?? "";
            var list = options.Where(o => o.Length > 0).Distinct().ToList();
            if (current.Length > 0 && !list.Contains(current)) list.Insert(0, current);
            return _optionList = list;
        }
    }

    public bool IsChoice { get; }
    public bool IsText => !IsChoice;

    public FieldEditViewModel(DataRowView row, DataColumn column, bool isChoice = false, Func<IReadOnlyList<string>?>? options = null)
    {
        _row = row;
        _column = column.ColumnName;
        Caption = column.Caption;
        IsChoice = isChoice && options != null;
        _options = options;
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
    private bool _saving;
    private int _editVersion;

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

    /// <summary>Details pane: <see cref="FieldEditViewModel"/>s plus, for suppliers, the <see cref="ThemeFieldViewModel"/>.</summary>
    public ObservableCollection<object> Fields { get; } = [];
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
        _retry.Tick += (_, _) => { _retry.Stop(); Save(); };
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
        BadgeVersion++;
        UpdateBankCounts();
        _loading = false;
        SelectedRow = (keepSel == null ? null
            : View.Cast<DataRowView>().FirstOrDefault(v => string.Equals(v[ColName(0)] as string, keepSel, StringComparison.OrdinalIgnoreCase)))
            ?? View.Cast<DataRowView>().FirstOrDefault();
    }

    private static string ColName(int i) => "c" + i;

    /// <summary>Shows <see cref="DataSetSession.Current"/> again (after it was replaced in memory).</summary>
    public void ShowCurrent() => BuildTable(Session.Current);

    private TableData ToTableData()
    {
        var t = new TableData { KeyColumn = 0 };
        // the table's own columns: new ones (a supplier's second bank account ...) are added to the sheet on save
        if (Table != null) foreach (DataColumn c in Table.Columns) t.Headers.Add(c.Caption);
        else t.Headers.AddRange(Session.Current.Headers);
        t.Kinds.AddRange(Session.Current.Kinds.Take(t.Headers.Count));
        while (t.Kinds.Count < t.Headers.Count) t.Kinds.Add(ColumnKind.Text);
        if (Table == null) return t;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            var arr = new string[t.Headers.Count];
            // line breaks are stored like Alt+Enter in Excel (LF)
            for (int i = 0; i < arr.Length; i++) arr[i] = (r[i] as string ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (arr.All(string.IsNullOrEmpty)) continue;
            t.Rows.Add(arr);
        }
        return t;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        BadgeVersion++;
        _editVersion++;
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
        // suppliers: a number alone (2, 3 ...) = the suppliers with that many bank accounts
        if (Session.Definition.Key == "suppliers" && int.TryParse(q, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= SupplierBanks.MaxAccounts)
        {
            var ids = Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted && BankCount(r) == n)
                .Select(r => "'" + (r[0] as string ?? "").Replace("'", "''") + "'").ToList();
            View.RowFilter = ids.Count == 0 ? "1 = 0" : $"[{ColName(0)}] IN ({string.Join(",", ids)})";
            return;
        }
        var esc = q.Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
        // suppliers / clients are searched by name and details, not by their codes (SUP-001 ...)
        var columns = Table.Columns.Cast<DataColumn>().Where(c => !(CompactGrid && Table.Columns.IndexOf(c) == 0));
        View.RowFilter = string.Join(" OR ", columns.Select(c => $"[{c.ColumnName}] LIKE '%{esc}%'"));
    }

    /// <summary>Number of bank accounts (with text) of a supplier row.</summary>
    public int BankCount(DataRow row)
    {
        if (Table == null || Session.Definition.Key != "suppliers") return 0;
        var headers = Table.Columns.Cast<DataColumn>().Select(c => c.Caption).ToList();
        int n = 0;
        foreach (var (_, col) in SupplierBanks.Columns(headers))
            if ((row[col] as string ?? "").Trim().Length > 0) n++;
        return n;
    }

    /// <summary>Suppliers: the name → number of bank accounts map used by the badges of the other lists (Reports, dropdowns).</summary>
    private void UpdateBankCounts()
    {
        if (Session.Definition.Key != "suppliers" || Table == null) return;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted || Table.Columns.Count < 2) continue;
            var name = (r[1] as string ?? "").Trim();
            if (name.Length > 0) counts[name] = BankCount(r);
        }
        BankBadgeConverter.CountsByName = counts;
        BankBadgeConverter.Version++;
    }

    /// <summary>Changes whenever data changes, so the bank-count badges of the list are recomputed.</summary>
    [ObservableProperty] private int _badgeVersion;

    partial void OnSelectedRowChanged(DataRowView? value)
    {
        Fields.Clear();
        if (value == null || Table == null) return;
        bool suppliers = Session.Definition.Key == "suppliers";
        var headers = Table.Columns.Cast<DataColumn>().Select(c => c.Caption).ToList();
        ThemeFieldViewModel? theme = null;
        FieldEditViewModel? nameField = null, templateField = null;
        bool banksAdded = false;
        for (int i = 0; i < Table.Columns.Count; i++)
        {
            var col = Table.Columns[i];
            // suppliers: all bank accounts (1, 2, 3 ... and DEFAULT BANK) are one group
            if (suppliers && SupplierBanks.IsBankHeader(headers, i))
            {
                if (!banksAdded) Fields.Add(BankGroupFor(value, headers));
                banksAdded = true;
                continue;
            }
            var field = new FieldEditViewModel(value, col, _main.HasOptions(Session.Definition.Key, col.Caption),
                () => _main.OptionsFor(Session.Definition.Key, col.Caption, ColumnValues(col)));
            Fields.Add(field);
            if (i == 1) nameField = field;
            // suppliers: the client theme colour sits right after DEFAULT TEMPLATE
            if (suppliers && col.Caption.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))
            {
                templateField = field;
                Fields.Add(theme = ThemeFieldFor(value));
            }
        }
        if (suppliers && theme == null) Fields.Add(theme = ThemeFieldFor(value));
        if (theme != null)
        {
            // a new record: the theme follows the name as it is typed (a colour picked before is saved with it);
            // any record: another DEFAULT TEMPLATE shows that template's colour
            bool isNew = theme.Supplier.Length == 0;
            void Follow(object? _, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(FieldEditViewModel.Value)) return;
                var name = isNew ? nameField?.Value ?? "" : theme.Supplier;
                _ = theme.UpdateAsync(name, templateField?.Value ?? theme.Template);
            }
            if (nameField != null && isNew) nameField.PropertyChanged += Follow;
            if (templateField != null) templateField.PropertyChanged += Follow;
        }
    }

    private BankGroupViewModel BankGroupFor(DataRowView row, IReadOnlyList<string> headers)
    {
        string Get(string header) => Table!.Columns.Cast<DataColumn>().FirstOrDefault(c => string.Equals(c.Caption, header, StringComparison.OrdinalIgnoreCase)) is { } c
            ? row[c.ColumnName] as string ?? "" : "";
        void Set(string header, string value)
        {
            var name = EnsureColumn(header);
            if ((row[name] as string ?? "") == value) return;
            row.BeginEdit();
            row[name] = value;
            row.EndEdit();
        }
        return new BankGroupViewModel(headers, Get, Set, expanded: false);
    }

    /// <summary>Column for this header, added to the table when new (written to the sheet on save). Returns its name.</summary>
    public string EnsureColumn(string header)
    {
        var existing = Table!.Columns.Cast<DataColumn>().FirstOrDefault(c => string.Equals(c.Caption, header, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.ColumnName;
        var col = new DataColumn(ColName(Table.Columns.Count), typeof(string)) { Caption = header, DefaultValue = "" };
        Table.Columns.Add(col);
        MarkDirty();
        return col.ColumnName;
    }

    private ThemeFieldViewModel ThemeFieldFor(DataRowView row)
    {
        string Get(Func<string, bool> pick)
        {
            foreach (DataColumn c in Table!.Columns) if (pick(c.Caption)) return (row[c.ColumnName] as string ?? "").Trim();
            return "";
        }
        var name = Table!.Columns.Count > 1 ? (row[ColName(1)] as string ?? "").Trim() : "";
        var template = Get(c => c.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase));
        return new ThemeFieldViewModel(_main, name, template.Length > 0 ? template : "INVOICE");
    }

    private IEnumerable<string> ColumnValues(DataColumn c) =>
        Table == null ? [] : Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted)
            .Select(r => (r[c] as string ?? "").Trim()).Where(v => v.Length > 0).Distinct();

    /// <summary>Suppliers / Clients show only ID and name in the list; the details pane gets the room.</summary>
    public bool CompactGrid => Session.Definition.Key is "suppliers" or "clients";

    // ---------------------------------------------------------------- full report of the selected company
    private string? SelectedCompany => SelectedRow != null && Table != null && Table.Columns.Count > 1
        ? (SelectedRow[ColName(1)] as string)?.Trim() : null;

    private string PartyType => Session.Definition.Key == "clients" ? "CLIENTS" : "SUPPLIERS";

    [RelayCommand]
    private void ViewReport()
    {
        if (SelectedCompany is { Length: > 0 } name)
            _main.ShowCompanyReport(name, PartyType, new BatchStudio.Core.Reports.DashboardFilter());
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (SelectedCompany is not { Length: > 0 } name) return;
        var vm = _main.BuildCompanyReport(name, PartyType, new BatchStudio.Core.Reports.DashboardFilter());
        if (vm == null) return;
        Status = Loc.T("Rep.Exporting");
        Status = await vm.ExportPdfCoreAsync();
    }

    public string NextId() => ToTableData().NextId(Session.Definition.IdPrefix);

    /// <summary>Column headers of the record (the table's columns, including ones added since the last save).</summary>
    public List<string> Headers => Table != null ? Table.Columns.Cast<DataColumn>().Select(c => c.Caption).ToList() : [.. Session.Current.Headers];

    /// <summary>Adds a record (values in header order) to the table; saved by the normal sync.</summary>
    public void AddRecord(string[] values)
    {
        if (Table == null) return;
        var row = Table.NewRow();
        for (int i = 0; i < Table.Columns.Count; i++) row[i] = i < values.Length ? values[i] ?? "" : "";
        Table.Rows.Add(row);
    }

    /// <summary>The values (header order) of the record with this ID, or null.</summary>
    public string[]? RecordValues(string id)
    {
        if (Table == null) return null;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            if (!string.Equals((r[0] as string ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var arr = new string[Table.Columns.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = r[i] as string ?? "";
            return arr;
        }
        return null;
    }

    /// <summary>Replaces the values of the record with this ID (saved by the normal sync / SaveAsync).</summary>
    public bool UpdateRecord(string id, string[] values)
    {
        if (Table == null) return false;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            if (!string.Equals((r[0] as string ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            for (int i = 1; i < Table.Columns.Count && i < values.Length; i++)
                if (!string.Equals(r[i] as string ?? "", values[i] ?? "", StringComparison.Ordinal)) r[i] = values[i] ?? "";
            return true;
        }
        return false;
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

    /// <summary>Saves into the workbook in the background (Excel writes it); edits made meanwhile stay pending.</summary>
    [RelayCommand]
    public void Save() => _ = SaveAsync();

    public async Task SaveAsync()
    {
        _autoSave.Stop();
        if (_saving) { _autoSave.Start(); return; }
        var data = ToTableData();
        if (data.Rows.Any(r => string.IsNullOrWhiteSpace(r[0])))
        {
            Status = Loc.T("Validate.MissingId");
            return;
        }
        int version = _editVersion;
        int keep = _main.Settings.KeepBackups;
        _saving = true;
        Status = Loc.T("Status.SavingWorkbook");
        try
        {
            Session.SetCurrent(data);
            await Task.Run(() => Session.Save(keep));
            IsWaitingForExcel = false;
            if (version == _editVersion)
            {
                IsDirty = false;
                Table?.AcceptChanges();
                Status = Loc.T("Status.Synced") + $"  ·  {DateTime.Now:HH:mm:ss}";
                UpdateBankCounts();
            }
            else _autoSave.Start();
            _main.Home.Refresh();
        }
        catch (Exception ex) when (ex is FileLockedException or ExcelBusyException)
        {
            IsWaitingForExcel = true;
            Status = ex is ExcelBusyException ? ex.Message : Loc.T("Status.Locked");
            _retry.Start();
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
        finally { _saving = false; }
    }

    /// <summary>Saves now and waits (used when closing / switching entity).</summary>
    private void SaveNow()
    {
        var data = ToTableData();
        if (data.Rows.Any(r => string.IsNullOrWhiteSpace(r[0]))) { Status = Loc.T("Validate.MissingId"); return; }
        try
        {
            Session.SetCurrent(data);
            Session.Save(_main.Settings.KeepBackups);
            IsDirty = false;
            IsWaitingForExcel = false;
            Table?.AcceptChanges();
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
    }

    /// <summary>
    /// Sets one column for every row whose name matches (used by the company report to save "Company's Activities").
    /// Returns the number of rows changed; the change is saved by the normal sync.
    /// </summary>
    public int SetWhere(Func<string, bool> nameMatches, Func<TableData, int> targetColumn, string value)
    {
        if (Table == null) return 0;
        var headers = Session.Current;
        int target = targetColumn(headers);
        int nameCol = headers.Headers.FindIndex(h => h.Contains("Name", StringComparison.OrdinalIgnoreCase));
        if (target < 0 || nameCol < 0) return 0;
        int n = 0;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            if (!nameMatches(r[nameCol] as string ?? "")) continue;
            if ((r[target] as string ?? "") != value) r[target] = value;
            n++;
        }
        return n;
    }

    [RelayCommand]
    private void Reload()
    {
        if (IsDirty && !Dialogs.Confirm(Loc.T("Status.Unsaved") + " - " + Loc.T("Common.Reload") + "?")) return;
        Load();
    }

    [RelayCommand]
    private async Task OpenInExcelAsync()
    {
        if (IsDirty) await SaveAsync();
        Shell.Open(Session.Workspace?.WorkbookPath ?? Session.FilePath);
    }

    [RelayCommand]
    private void OpenFolder() => Shell.ShowInFolder(Session.Workspace?.WorkbookPath ?? Session.FilePath);

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
        _autoSave.Stop();
        _retry.Stop();
        SaveNow();
        return !IsDirty;
    }

    public void Stop()
    {
        _autoSave.Stop();
        _retry.Stop();
    }
}

/// <summary>
/// Supplier theme colour (Client Theme Memory): hex code + swatch; empty = "Not Assigned Yet".
/// Saved into CLIENT THEME MEMORY for the supplier's template (INVOICE / INV1), like ASSIGN THEME in Excel.
/// For a supplier that has no name yet (a new record) the colour is kept and saved together with the supplier.
/// </summary>
public sealed partial class ThemeFieldViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int? _saved;
    private int? _pending;
    private bool _hasPending;

    /// <summary>New supplier form (New Invoice): the colour is written by <see cref="FlushAsync"/> after the supplier is saved.</summary>
    public bool Deferred { get; }

    [ObservableProperty]
    private string _supplier;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Supported), nameof(Note))]
    private string _template;

    public bool Supported => BatchStudio.Core.Workbook.ThemeMemory.Supports(Template);
    public string Caption => "Theme";
    public string Note => Supported ? Loc.F("Theme.For", Template.ToUpperInvariant()) : Loc.T("Theme.NotSupported");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Swatch), nameof(HasColor))]
    private string _hex = "";
    [ObservableProperty] private string _status = "";

    public bool HasColor => BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out _);
    public System.Windows.Media.Brush Swatch
    {
        get
        {
            if (!BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out var rgb)) return System.Windows.Media.Brushes.Transparent;
            var (r, g, b) = BatchStudio.Core.Workbook.ThemeMemory.Split(rgb);
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        }
    }

    public ThemeFieldViewModel(MainViewModel main, string supplier, string template, bool deferred = false)
    {
        _main = main;
        _supplier = supplier.Trim();
        _template = template.Trim().Length > 0 ? template.Trim() : "INVOICE";
        Deferred = deferred;
        _saved = Supported && _supplier.Length > 0 ? main.ThemeFor(_supplier, _template) : null;
        _hex = _saved is { } v ? BatchStudio.Core.Workbook.ThemeMemory.ToHex(v) : "";
    }

    [RelayCommand]
    private async Task PickAsync()
    {
        if (!Supported) return;
        int? initial = BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out var cur) ? cur : _saved;
        var picked = ColorPicker.Pick(initial);
        if (picked == null) return;
        Hex = BatchStudio.Core.Workbook.ThemeMemory.ToHex(picked.Value);
        await CommitAsync();
    }

    /// <summary>Called when the hex box is left or Enter is pressed.</summary>
    [RelayCommand]
    public async Task CommitAsync()
    {
        if (!Supported) return;
        int? rgb = null;
        if (Hex.Trim().Length > 0)
        {
            if (!BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out var v)) { Status = Loc.T("Theme.BadHex"); return; }
            rgb = v;
            Hex = BatchStudio.Core.Workbook.ThemeMemory.ToHex(v);
        }
        if (Deferred || Supplier.Length == 0)
        {
            // no supplier yet: kept and saved with the supplier
            _pending = rgb;
            _hasPending = rgb != _saved;
            Status = _hasPending && rgb != null ? Loc.T("Theme.Pending") : "";
            return;
        }
        if (rgb == _saved) { Status = ""; return; }
        Status = Loc.T("Status.SavingWorkbook");
        try
        {
            await _main.AssignTheme(Supplier, Template, rgb);
            _saved = rgb;
            _hasPending = false;
            Status = rgb == null ? Loc.T("Theme.Cleared") : Loc.T("Theme.Saved");
        }
        catch (Exception ex) { Status = $"{Loc.T("Status.Error")}: {ex.Message}"; }
    }

    /// <summary>The supplier's name or template changed in the details (a new record gets its name, another template is chosen).</summary>
    public async Task UpdateAsync(string supplier, string template)
    {
        supplier = supplier.Trim();
        template = template.Trim().Length > 0 ? template.Trim() : "INVOICE";
        if (supplier == Supplier && string.Equals(template, Template, StringComparison.OrdinalIgnoreCase)) return;
        Supplier = supplier;
        Template = template;
        if (Deferred) return;
        if (_hasPending)
        {
            // a colour picked before the supplier had a name: saved now
            Hex = _pending is { } p ? BatchStudio.Core.Workbook.ThemeMemory.ToHex(p) : "";
            _saved = null;
            await CommitAsync();
            return;
        }
        _saved = Supported && Supplier.Length > 0 ? _main.ThemeFor(Supplier, Template) : null;
        Hex = _saved is { } v ? BatchStudio.Core.Workbook.ThemeMemory.ToHex(v) : "";
        Status = "";
    }

    /// <summary>New supplier saved: writes the colour picked in the form (if any) for this supplier and template.</summary>
    public async Task FlushAsync(string supplier, string template)
    {
        Supplier = supplier.Trim();
        Template = template.Trim().Length > 0 ? template.Trim() : "INVOICE";
        if (BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out var typed)) { _pending = typed; _hasPending = true; }
        if (!_hasPending || _pending == null || !Supported || Supplier.Length == 0) return;
        await _main.AssignTheme(Supplier, Template, _pending);
        _saved = _pending;
        _hasPending = false;
        Status = Loc.T("Theme.Saved");
    }
}
