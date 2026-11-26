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
            RenumberIds();
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
        TableRebuilt?.Invoke();
        SelectedRow = (keepSel == null ? null
            : View.Cast<DataRowView>().FirstOrDefault(v => string.Equals(v[ColName(0)] as string, keepSel, StringComparison.OrdinalIgnoreCase)))
            ?? View.Cast<DataRowView>().FirstOrDefault();
    }

    private static string ColName(int i) => "c" + i;

    /// <summary>Raised when the sheet is (re)loaded into a new DataTable (the Categories page re-reads the items).</summary>
    public event Action? TableRebuilt;

    // ---------------------------------------------------------------- Categories page: the items of the selected category
    public bool IsCategories => Session.Definition.Key == "categories";
    private DataSetViewModel? ItemsPage => _main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "items");
    private bool _itemsHooked;
    private DataTable? _itemsTable;
    private Dictionary<string, int> _itemCounts = new(StringComparer.OrdinalIgnoreCase);
    public const string Uncategorized = "\u0000none";

    /// <summary>The items of the selected category (rows of the Items sheet: editing them edits Items).</summary>
    [ObservableProperty] private DataView? _categoryItems;
    [ObservableProperty] private int _categoryItemCount;
    [ObservableProperty] private string _itemSearch = "";
    [ObservableProperty] private string _categoryTitle = "";
    partial void OnItemSearchChanged(string value) => FilterCategoryItems();
    /// <summary>"Uncategorized" (items with no category, or one that no longer exists) is shown at the end of the list.</summary>
    [ObservableProperty] private bool _showUncategorized;
    public int UncategorizedCount => _itemCounts.TryGetValue(Uncategorized, out var n) ? n : 0;
    partial void OnShowUncategorizedChanged(bool value) { if (value) SelectedRow = null; FilterCategoryItems(); }

    /// <summary>Category choices for an item (ID shown with the name).</summary>
    public ObservableCollection<CategoryChoice> CategoryChoices { get; } = [];

    private int ItemCol(string caption) =>
        _itemsTable?.Columns.Cast<DataColumn>().FirstOrDefault(c => string.Equals(c.Caption.Trim(), caption, StringComparison.OrdinalIgnoreCase)) is { } c
            ? _itemsTable.Columns.IndexOf(c) : -1;
    private int ItemCategoryCol => ItemCol("Category ID");

    private void HookItems()
    {
        if (!IsCategories) return;
        var items = ItemsPage;
        if (items == null) return;
        if (!_itemsHooked) { items.TableRebuilt += OnItemsRebuilt; _itemsHooked = true; }
        if (!ReferenceEquals(_itemsTable, items.Table)) OnItemsRebuilt();
    }

    private void OnItemsRebuilt()
    {
        var items = ItemsPage;
        _itemsTable = items?.Table;
        if (_itemsTable == null) { CategoryItems = null; return; }
        _itemsTable.RowChanged += (_, e) => { if (e.Action is DataRowAction.Change or DataRowAction.Add) RecountItems(); };
        _itemsTable.RowDeleted += (_, _) => RecountItems();
        CategoryItems = new DataView(_itemsTable);
        RecountItems();
        FilterCategoryItems();
    }

    /// <summary>Items per category ID (and how many have none / an unknown one).</summary>
    private void RecountItems()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cc = ItemCategoryCol;
        var known = Table?.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted)
                         .Select(r => (r[0] as string ?? "").Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (_itemsTable != null && cc >= 0)
            foreach (DataRow r in _itemsTable.Rows)
            {
                if (r.RowState == DataRowState.Deleted) continue;
                var id = (r[cc] as string ?? "").Trim();
                if (id.Length == 0 || !known.Contains(id)) id = Uncategorized;
                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
            }
        _itemCounts = counts;
        OnPropertyChanged(nameof(UncategorizedCount));
        BadgeVersion++;
        CategoryChoices.Clear();
        if (Table != null)
            foreach (DataRow r in Table.Rows)
            {
                if (r.RowState == DataRowState.Deleted) continue;
                var id = (r[0] as string ?? "").Trim();
                if (id.Length > 0) CategoryChoices.Add(new CategoryChoice(id, $"{id} · {(Table.Columns.Count > 1 ? r[1] as string : "")}"));
            }
    }

    public int ItemCount(DataRow row) =>
        IsCategories && _itemCounts.TryGetValue((row[0] as string ?? "").Trim(), out var n) ? n : 0;

    private string SelectedCategoryId => (SelectedRow?[ColName(0)] as string ?? "").Trim();

    /// <summary>The item list: the selected category (or Uncategorized), narrowed by the item search.</summary>
    private void FilterCategoryItems()
    {
        if (CategoryItems == null || _itemsTable == null) return;
        int cc = ItemCategoryCol;
        string Esc(string v) => v.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");
        var parts = new List<string>();
        if (cc >= 0)
        {
            var col = _itemsTable.Columns[cc].ColumnName;
            if (ShowUncategorized)
            {
                var ids = Table?.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted)
                                .Select(r => (r[0] as string ?? "").Trim()).Where(x => x.Length > 0).Select(x => $"'{x.Replace("'", "''")}'").ToList() ?? [];
                parts.Add(ids.Count == 0 ? $"(ISNULL([{col}], '') = '')" : $"(ISNULL([{col}], '') = '' OR [{col}] NOT IN ({string.Join(",", ids)}))");
                CategoryTitle = Loc.T("Cat.Uncategorized");
            }
            else if (SelectedCategoryId.Length > 0 && ItemSearch.Trim().Length == 0)
            {
                parts.Add($"[{col}] = '{SelectedCategoryId.Replace("'", "''")}'");
                CategoryTitle = SelectedRow?[ColName(1)] as string ?? SelectedCategoryId;
            }
            else CategoryTitle = ItemSearch.Trim().Length > 0 ? Loc.T("Cat.AllItems") : SelectedRow?[ColName(1)] as string ?? "";
        }
        var q = ItemSearch.Trim();
        if (q.Length > 0)
        {
            // the search looks in every item (any category)
            var esc = Esc(q);
            var cols = _itemsTable.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName}] LIKE '%{esc}%'");
            parts.Add("(" + string.Join(" OR ", cols) + ")");
        }
        try { CategoryItems.RowFilter = string.Join(" AND ", parts); } catch { CategoryItems.RowFilter = ""; }
        CategoryItemCount = CategoryItems.Count;
    }

    /// <summary>"+ Item": a new item in the selected category (the next code), selected for typing its name.</summary>
    [RelayCommand]
    private void AddItem()
    {
        var items = ItemsPage;
        if (items?.Table == null) return;
        HookItems();
        ItemSearch = "";
        var row = items.Table.NewRow();
        row[0] = items.ToTableData().NextId(items.Session.Definition.IdPrefix);
        int cc = ItemCategoryCol;
        if (cc >= 0 && !ShowUncategorized) row[cc] = SelectedCategoryId;
        int st = ItemCol("Status");
        if (st >= 0) row[st] = "Active";
        items.Table.Rows.Add(row);
        FilterCategoryItems();
        NewItemAdded?.Invoke(row);
    }

    /// <summary>The view selects and edits the new item row.</summary>
    public event Action<DataRow>? NewItemAdded;

    /// <summary>Moves an item to another category (the item list's Category column).</summary>
    public void MoveItem(DataRowView item, string categoryId)
    {
        int cc = ItemCategoryCol;
        if (cc < 0) return;
        item.Row[cc] = categoryId;
        FilterCategoryItems();
    }

    [RelayCommand]
    private void DeleteItem(DataRowView? item)
    {
        if (item == null || ItemsPage?.Table == null) return;
        var name = _itemsTable!.Columns.Count > 1 ? item.Row[1] as string : "";
        if (!Dialogs.Confirm(Loc.F("Cat.DeleteItemAsk", item.Row[0] as string ?? "", name ?? ""))) return;
        item.Row.Delete();
        FilterCategoryItems();
    }

    [RelayCommand]
    private void ShowUncategorizedItems() => ShowUncategorized = true;

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

    /// <summary>Saves on the next save even though no cell changed (further workbook edits are queued on the session).</summary>
    internal void ForceSave() => IsDirty = true;

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
        if (IsCategories)
        {
            if (value != null && ShowUncategorized) { _showUncategorized = false; OnPropertyChanged(nameof(ShowUncategorized)); }
            HookItems();
            FilterCategoryItems();
        }
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
        var group = new BankGroupViewModel(headers, Get, Set, expanded: false);
        if (Session.Definition.Key == "suppliers")
            group.Exporter = acc => ExportBankAsync(Get, headers, acc);
        return group;
    }

    public bool IsSuppliersSheet => Session.Definition.Key == "suppliers";
    [ObservableProperty] private bool _isExportingAll;
    [ObservableProperty] private string _exportAllText = "";

    /// <summary>
    /// Every bank account of every supplier as a PDF, in a Desktop folder "All Registered Suppliers Bank Accounts"
    /// ("... (2)" when that folder already exists, so an earlier export is never mixed with this one). One hidden Excel.
    /// </summary>
    [RelayCommand]
    private async Task ExportAllBanksAsync()
    {
        if (IsExportingAll || Table == null || !IsSuppliersSheet) return;
        var book = Session.Workspace?.WorkbookPath;
        if (book == null || !File.Exists(book)) { Dialogs.Error(Loc.T("BankPdf.NoWorkbook")); return; }
        var headers = Table.Columns.Cast<DataColumn>().Select(c => c.Caption).ToList();
        int nativeCol = headers.FindIndex(h => h.Contains("Chinese", StringComparison.OrdinalIgnoreCase));
        int templateCol = headers.FindIndex(h => h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Path.Combine(desktop, "All Registered Suppliers Bank Accounts");
        for (int i = 2; Directory.Exists(folder) || File.Exists(folder); i++)
            folder = Path.Combine(desktop, $"All Registered Suppliers Bank Accounts ({i})");

        var jobs = new List<BankAccountPdf.Job>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted || Table.Columns.Count < 2) continue;
            var row = Table.Columns.Cast<DataColumn>().Select(c => r[c] as string ?? "").ToList();
            var name = row[1].Trim();
            if (name.Length == 0) continue;
            string native = nativeCol >= 0 ? row[nativeCol] : "";
            string template = templateCol >= 0 ? row[templateCol].Trim() : "";
            int? theme = _main.ThemeFor(name, template.Length > 0 ? template : "INVOICE");
            foreach (var account in SupplierBanks.Accounts(headers, row).Where(a => a.Text.Trim().Length > 0))
            {
                // two suppliers with the same file name (or the same account twice): "(2)"
                var file = BankAccountPdf.FileName(name, account.Number);
                var stem = Path.GetFileNameWithoutExtension(file);
                for (int k = 2; !taken.Add(file); k++) file = $"{stem} ({k}).pdf";
                jobs.Add(new BankAccountPdf.Job(name, native, account.Text, theme, Path.Combine(folder, file)));
            }
        }
        if (jobs.Count == 0) { Status = Loc.T("BankPdf.NoAccounts"); return; }

        IsExportingAll = true;
        ExportAllText = $"0 / {jobs.Count}";
        Status = Loc.F("BankPdf.ExportingAll", jobs.Count);
        var ui = System.Windows.Application.Current?.Dispatcher;
        try
        {
            List<(BankAccountPdf.Job Job, string Error)> failed = [];
            await Task.Run(() =>
            {
                Exception? error = null;
                var t = new Thread(() =>
                {
                    try
                    {
                        failed = BankAccountPdf.ExportMany(book, jobs,
                            (done, total) => ui?.InvokeAsync(() => ExportAllText = $"{done} / {total}"));
                    }
                    catch (Exception ex) { error = ex; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (error != null) throw error;
            });
            int ok = jobs.Count - failed.Count;
            Status = Loc.F("BankPdf.AllDone", ok, Path.GetFileName(folder));
            if (failed.Count > 0)
                Dialogs.Error(Loc.F("BankPdf.SomeFailed", failed.Count) + "\n\n" +
                              string.Join("\n", failed.Take(15).Select(f => "•  " + Path.GetFileName(f.Job.PdfPath) + " — " + f.Error)));
        }
        catch (Exception ex)
        {
            Status = "";
            Dialogs.Error(Loc.F("BankPdf.Failed", ex.Message));
        }
        finally
        {
            IsExportingAll = false;
            ExportAllText = "";
        }
    }

    /// <summary>
    /// A supplier's bank account as a PDF on the Desktop ("Bank Account" sheet): its name, Chinese name and theme colour,
    /// "NAME Bank Account.pdf" (account 1) / "NAME Bank Account 3.pdf" (Banking Details 3), "(2)" when the name is taken.
    /// </summary>
    private async Task<string?> ExportBankAsync(Func<string, string> get, IReadOnlyList<string> headers, BankAccountViewModel account)
    {
        var book = Session.Workspace?.WorkbookPath;
        if (book == null || !File.Exists(book)) { Dialogs.Error(Loc.T("BankPdf.NoWorkbook")); return null; }
        string name = headers.Count > 1 ? get(headers[1]).Trim() : "";
        if (name.Length == 0) { Dialogs.Error(Loc.T("BankPdf.NoName")); return null; }
        string native = headers.FirstOrDefault(h => h.Contains("Chinese", StringComparison.OrdinalIgnoreCase)) is { } nh ? get(nh) : "";
        string template = headers.FirstOrDefault(h => h.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase)) is { } th ? get(th).Trim() : "";
        int? theme = _main.ThemeFor(name, template.Length > 0 ? template : "INVOICE");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var pdf = BankAccountPdf.FreePath(desktop, BankAccountPdf.FileName(name, account.Number));
        string bank = account.Value;
        Status = Loc.T("BankPdf.Exporting");
        try
        {
            await Task.Run(() =>
            {
                Exception? error = null;
                var t = new Thread(() =>
                {
                    try { BankAccountPdf.Export(book, name, native, bank, theme, pdf); }
                    catch (Exception ex) { error = ex; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                if (error != null) throw error;
            });
            Status = Loc.F("BankPdf.Done", Path.GetFileName(pdf));
            return Status;
        }
        catch (Exception ex)
        {
            Status = "";
            Dialogs.Error(Loc.F("BankPdf.Failed", ex.Message));
            return null;
        }
    }

    /// <summary>The same Desktop PDF from a supplier form (Mass Invoice / New Invoice / review): returns the done message.</summary>
    public Task<string?> ExportBankPdfAsync(Func<string, string> get, BankAccountViewModel account) => ExportBankAsync(get, Headers, account);

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

    /// <summary>A save is running (the next SaveAsync returns at once and the edits wait for the auto-save).</summary>
    public bool IsSaving => _saving;

    /// <summary>
    /// Suppliers / Clients: the IDs stay a serial without gaps (SUP-001 ...). After a delete (here or in Excel) the
    /// records after it move up one number; the change is saved like any edit. Invoices do not keep these IDs.
    /// </summary>
    private void RenumberIds()
    {
        if (!IsPartySheet || Table == null) return;
        var rows = Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted).ToList();
        var fresh = BatchStudio.Core.Data.IdSequence.Renumber(rows.Select(r => r[0] as string).ToList(), Session.Definition.IdPrefix);
        if (fresh == null) return;
        var keepSel = SelectedRow != null && SelectedRow.Row.RowState != DataRowState.Deleted ? SelectedRow.Row : null;
        int changed = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals((rows[i][0] as string ?? "").Trim(), fresh[i], StringComparison.Ordinal)) continue;
            rows[i][0] = fresh[i];
            changed++;
        }
        if (changed == 0) return;
        Status = Loc.F("Status.Renumbered", changed);
        if (keepSel != null) SelectedRow = View?.Cast<DataRowView>().FirstOrDefault(v => v.Row == keepSel);
        _main.RefreshLists();
    }

    // ---------------------------------------------------------------- full check (Suppliers / Clients)
    public System.Collections.ObjectModel.ObservableCollection<HealthItemViewModel> HealthItems { get; } = [];
    [ObservableProperty] private bool _isHealthOpen;
    [ObservableProperty] private string _healthSummary = "";

    /// <summary>Home › "Needs your attention": how many findings the full check has now (no window opened).</summary>
    public int HealthIssueCount()
    {
        if (Table == null || !IsPartySheet) return 0;
        try
        {
            var rows = Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted)
                .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, Table.Columns.Count).Select(i => r[i] as string ?? "").ToList())
                .ToList();
            return (Session.Definition.Key == "suppliers"
                ? BatchStudio.Core.Analysis.HealthCheck.Suppliers(Headers, rows)
                : BatchStudio.Core.Analysis.HealthCheck.Clients(Headers, rows)).Count;
        }
        catch { return 0; }
    }

    /// <summary>Checks the whole list at once (shared / repeated accounts, paragraphs without account or SWIFT, same names ...).</summary>
    [RelayCommand]
    private void HealthCheck()
    {
        if (Table == null || !IsPartySheet) return;
        var rows = Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, Table.Columns.Count).Select(i => r[i] as string ?? "").ToList())
            .ToList();
        var issues = Session.Definition.Key == "suppliers"
            ? BatchStudio.Core.Analysis.HealthCheck.Suppliers(Headers, rows)
            : BatchStudio.Core.Analysis.HealthCheck.Clients(Headers, rows);
        HealthItems.Clear();
        foreach (var i in issues) HealthItems.Add(new HealthItemViewModel(this, i));
        HealthSummary = issues.Count == 0 ? Loc.T("Health.AllGood") : Loc.F("Health.Found", issues.Count, rows.Count);
        IsHealthOpen = true;
    }

    [RelayCommand]
    private void CloseHealth() => IsHealthOpen = false;

    /// <summary>A record named in the check: shown in the list and its details (the panel stays open to go on).</summary>
    public void GoToRecord(string id)
    {
        if (View == null) return;
        if (SearchText.Length > 0) SearchText = "";
        SelectedRow = View.Cast<DataRowView>().FirstOrDefault(v => string.Equals((v[ColName(0)] as string ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase));
        IsHealthOpen = false;
    }

    /// <summary>Suppliers and Clients: a name may be saved only once.</summary>
    public bool IsPartySheet => Session.Definition.Key is "suppliers" or "clients";

    private static string NameKey(string? name) => BatchStudio.Core.Analysis.AnalysisMatcher.NormalizeName(name);

    /// <summary>
    /// ID of another record (saved or not) with the same name: case, spacing, punctuation and the usual
    /// abbreviations do not count ("CO., LTD." = "Company Limited"). <paramref name="exceptId"/>: the record being edited.
    /// </summary>
    public string? SameNameId(string name, string? exceptId = null)
    {
        if (Table == null || Table.Columns.Count < 2) return null;
        var key = NameKey(name);
        if (key.Length == 0) return null;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            var id = (r[0] as string ?? "").Trim();
            if (exceptId != null && string.Equals(id, exceptId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (NameKey(r[1] as string) == key) return id.Length > 0 ? id : "?";
        }
        return null;
    }

    /// <summary>Names saved more than once (first one found), with their IDs.</summary>
    public (string Name, List<string> Ids)? FirstDuplicateName()
    {
        if (Table == null || Table.Columns.Count < 2) return null;
        var seen = new Dictionary<string, (string Name, List<string> Ids)>();
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            var name = (r[1] as string ?? "").Trim();
            var key = NameKey(name);
            if (key.Length == 0) continue;
            var id = (r[0] as string ?? "").Trim();
            if (seen.TryGetValue(key, out var e)) e.Ids.Add(id);
            else seen[key] = (name, [id]);
        }
        foreach (var e in seen.Values)
            if (e.Ids.Count > 1) return e;
        return null;
    }

    /// <summary>ID of the record whose name column (column 1, or <paramref name="nameColumn"/>) is this name, saved or not.</summary>
    public string? FindIdByName(string name, int nameColumn = 1)
    {
        if (Table == null || nameColumn < 0 || nameColumn >= Table.Columns.Count) return null;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            if (string.Equals((r[nameColumn] as string ?? "").Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return (r[0] as string ?? "").Trim();
        }
        return null;
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

    // ---------------------------------------------------------------- the same supplier saved twice: update / merge
    /// <summary>
    /// The differences between a saved record (<paramref name="keep"/>) and incoming details (<paramref name="newer"/>),
    /// header order: every field the incoming one has and that is not the same (ID and name never change), and every bank
    /// paragraph not saved yet (taking it adds an account). Nothing is chosen: the saved values stay unless the user picks.
    /// </summary>
    public List<Views.MergeRow> MergeRows(string[] keep, string[] newer)
    {
        var headers = Headers;
        bool banks = Session.Definition.Key == "suppliers";
        var rows = new List<Views.MergeRow>();
        static string Norm(string? v) => SupplierBanks.Normalize(v);
        for (int i = 2; i < headers.Count; i++)
        {
            if (banks && SupplierBanks.IsBankHeader(headers, i)) continue;
            var inc = i < newer.Length ? (newer[i] ?? "").Trim() : "";
            var old = i < keep.Length ? (keep[i] ?? "").Trim() : "";
            if (inc.Length == 0 || Norm(inc) == Norm(old)) continue;
            rows.Add(new Views.MergeRow { Header = headers[i], Column = i, Saved = old, Incoming = inc });
        }
        if (banks)
        {
            var cols = SupplierBanks.Columns(headers);
            var saved = cols.Select(c => c.Column < keep.Length ? Norm(keep[c.Column]) : "").Where(t => t.Length > 0).ToHashSet();
            foreach (var (_, c) in cols)
            {
                var t = c < newer.Length ? (newer[c] ?? "").Trim() : "";
                if (t.Length == 0 || saved.Contains(Norm(t))) continue;
                saved.Add(Norm(t));
                rows.Add(new Views.MergeRow { Header = Loc.T("MergeReview.BankHeader"), Column = c, Saved = "", Incoming = t, IsNewBank = true });
            }
        }
        return rows;
    }

    /// <summary>The saved record with the rows the user took (new bank paragraphs go to the next free account).</summary>
    public string[] ApplyMerge(string[] keep, IEnumerable<Views.MergeRow> rows)
    {
        var result = (string[])keep.Clone();
        void Fit()
        {
            if (result.Length >= Headers.Count) return;
            int old = result.Length;
            Array.Resize(ref result, Headers.Count);
            for (int k = old; k < result.Length; k++) result[k] = "";
        }
        Fit();
        foreach (var r in rows.Where(r => r.TakeIncoming))
        {
            if (!r.IsNewBank) { if (r.Column < result.Length) result[r.Column] = r.Incoming; continue; }
            // the next empty account (the printed account, DEFAULT BANK, does not change)
            var cols = SupplierBanks.Columns(Headers);
            var free = cols.FirstOrDefault(c => c.Column < result.Length && (result[c.Column] ?? "").Trim().Length == 0);
            if (free.Number == 0)
            {
                int n = cols.Count == 0 ? 1 : cols.Max(c => c.Number) + 1;
                if (n > SupplierBanks.MaxAccounts) continue;
                EnsureColumn(SupplierBanks.HeaderFor(n));
                Fit();
                free = SupplierBanks.Columns(Headers).First(c => c.Number == n);
            }
            result[free.Column] = r.Incoming;
        }
        return result;
    }

    /// <summary>Removes the record with this ID (IDs after it are renumbered, as Delete does).</summary>
    public bool DeleteRecord(string id)
    {
        if (Table == null) return false;
        foreach (DataRow r in Table.Rows)
        {
            if (r.RowState == DataRowState.Deleted) continue;
            if (!string.Equals((r[0] as string ?? "").Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            r.Delete();
            RenumberIds();
            return true;
        }
        return false;
    }

    /// <summary>The same name saved twice: "Merge SUP-058 into SUP-004" next to the status (the older ID stays).</summary>
    [ObservableProperty] private string _mergeOffer = "";
    public bool HasMergeOffer => MergeOffer.Length > 0;
    partial void OnMergeOfferChanged(string value) => OnPropertyChanged(nameof(HasMergeOffer));
    private (string Keep, string Drop)? _merge;

    [RelayCommand]
    private async Task MergeDuplicatesAsync()
    {
        if (_merge is not { } m) return;
        var keep = RecordValues(m.Keep);
        var drop = RecordValues(m.Drop);
        if (keep == null || drop == null) { MergeOffer = ""; _merge = null; return; }
        var name = keep.Length > 1 ? keep[1] : m.Keep;
        var rows = MergeRows(keep, drop);
        if (rows.Count == 0)
        {
            if (!Dialogs.Confirm(Loc.F("Merge.NoDiff", m.Drop, m.Keep))) return;
        }
        else if (!Views.MergeReviewDialog.Review(Loc.F("Merge.Title", m.Drop, m.Keep), Loc.F("Merge.Text", m.Keep, name, m.Drop),
                                                 Loc.F("Merge.Offer", m.Drop, m.Keep), rows))
            return;
        UpdateRecord(m.Keep, ApplyMerge(keep, rows));
        DeleteRecord(m.Drop);
        MergeOffer = "";
        _merge = null;
        SelectedRow = View?.Cast<DataRowView>().FirstOrDefault(r => string.Equals(r[ColName(0)] as string, m.Keep, StringComparison.OrdinalIgnoreCase));
        await SaveAsync();
        if (!IsDirty) Status = Loc.F("Merge.Done", m.Drop, m.Keep);
    }

    private static int IdNumber(string id) => int.TryParse(new string(id.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue;

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
        RenumberIds();
    }

    /// <summary>Saves into the workbook in the background (Excel writes it); edits made meanwhile stay pending.</summary>
    [RelayCommand]
    public void Save() => _ = SaveAsync();

    public async Task SaveAsync()
    {
        // Categories: the items edited here belong to the Items sheet (no page of its own any more)
        if (IsCategories && ItemsPage is { IsDirty: true } itemsPage) await itemsPage.SaveAsync();
        _autoSave.Stop();
        if (_saving) { _autoSave.Start(); return; }
        var data = ToTableData();
        if (data.Rows.Any(r => string.IsNullOrWhiteSpace(r[0])))
        {
            Status = Loc.T("Validate.MissingId");
            return;
        }
        // the same supplier / client name twice is never saved: the user deletes or renames one first
        if (IsPartySheet && FirstDuplicateName() is { } dup)
        {
            Status = Loc.F("Validate.DuplicateName", dup.Name, string.Join(", ", dup.Ids));
            // offer to merge the newer record into the older one (lowest number stays)
            var ids = dup.Ids.OrderBy(IdNumber).ToList();
            if (ids.Count >= 2) { _merge = (ids[0], ids[^1]); MergeOffer = Loc.F("Merge.Offer", ids[^1], ids[0]); }
            return;
        }
        MergeOffer = "";
        _merge = null;
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
                    RenumberIds();                   // a row deleted in Excel: the serial is closed up and written back
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

    /// <summary>The colour picked in the form (not written yet), null when none.</summary>
    public int? PickedColor()
    {
        if (BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(Hex, out var typed)) return typed;
        return _hasPending ? _pending : null;
    }

    /// <summary>The picked colour was written with the supplier (same workbook save): the field shows it as saved.</summary>
    public void MarkSaved(string supplier, string template)
    {
        Supplier = supplier.Trim();
        Template = template.Trim().Length > 0 ? template.Trim() : "INVOICE";
        _saved = PickedColor();
        _hasPending = false;
        Status = Loc.T("Theme.Saved");
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


/// <summary>One finding of the full check, with links to the records concerned.</summary>
public sealed class HealthItemViewModel
{
    public HealthItemViewModel(DataSetViewModel owner, BatchStudio.Core.Analysis.HealthIssue issue)
    {
        Issue = issue;
        Title = Loc.T("Health." + issue.Kind);
        var detail = issue.Detail;
        if (issue.Bank > 0) detail = (detail.Length > 0 ? detail + " · " : "") + Loc.F("Health.BankN", issue.Bank);
        Detail = detail;
        Links = issue.Ids.Select((id, k) => new HealthLink(id, k < issue.Names.Count ? issue.Names[k] : "", new RelayCommand(() => owner.GoToRecord(id)))).ToList();
        Severe = issue.Kind is BatchStudio.Core.Analysis.HealthKind.SharedAccount or BatchStudio.Core.Analysis.HealthKind.SameName
                 or BatchStudio.Core.Analysis.HealthKind.NoAccount;
    }

    public BatchStudio.Core.Analysis.HealthIssue Issue { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool Severe { get; }
    public List<HealthLink> Links { get; }
}

public sealed record HealthLink(string Id, string Name, IRelayCommand Open)
{
    public string ShortId => System.Text.RegularExpressions.Regex.Replace(Id, @"-0+(\d)", "-$1");
}

/// <summary>A category in the item list's Category column: its ID, shown with the name.</summary>
public sealed record CategoryChoice(string Id, string Label);
