using System.Collections.ObjectModel;
using System.Windows;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Entities;
using BatchStudio.Core.Invoices;
using BatchStudio.Core.Workbook;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public required string Key { get; init; }       // localization key
    public required string Glyph { get; init; }     // Segoe Fluent Icons glyph
    public required Func<object?> Page { get; init; }
    public string Title => Loc.T(Key);
    public void Refresh() => OnPropertyChanged(nameof(Title));

    /// <summary>Small number shown at the right of the item (records / invoices / templates); empty = none.</summary>
    [ObservableProperty] private string _count = "";
}

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }

    /// <summary>Sidebar footer: "v0.9.0 · Powered by Precision" (version read from the program itself).</summary>
    public string VersionText
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return $"v{v?.Major}.{v?.Minor}.{v?.Build} · Powered by Precision";
        }
    }
    public ObservableCollection<EntityProfile> Entities { get; } = [];
    public ObservableCollection<NavItem> Nav { get; } = [];
    public List<DataSetViewModel> DataPages { get; } = [];

    public HomeViewModel Home { get; }
    public TemplatesViewModel Templates { get; }
    public SettingsViewModel SettingsPage { get; }
    public EntitySetupViewModel Setup { get; }
    public NewInvoiceViewModel NewInvoice { get; }
    public HistoryViewModel History { get; }
    public DashboardViewModel Dashboard { get; }
    public ReportsViewModel Reports { get; }
    // ------------------------------------------------------------------ supplier / client details over any page
    private PartyFormViewModel? _partyDetails;

    /// <summary>
    /// The supplier / client details panel (view, change, Save) opened from the first cell of a company list: Invoice
    /// History (#), Dashboard (Rank), Reports (ID) - the same form as the row number of Mass Invoice.
    /// </summary>
    public PartyFormViewModel PartyDetails
    {
        get
        {
            if (_partyDetails != null) return _partyDetails;
            _partyDetails = new PartyFormViewModel(this);
            _partyDetails.Closed += _ => IsPartyDetailsOpen = false;
            return _partyDetails;
        }
    }

    [ObservableProperty] private bool _isPartyDetailsOpen;

    /// <summary>
    /// Opens a company's details: <paramref name="type"/> "SUPPLIERS" / "CLIENTS" (empty = looked up in both lists);
    /// by <paramref name="id"/> when known, else by its name.
    /// </summary>
    public bool ShowParty(string? type, string? id, string? name)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        bool[] lists = t == "CLIENTS" ? [true] : t == "SUPPLIERS" ? [false] : [false, true];
        foreach (var clients in lists)
        {
            var page = DataPages.FirstOrDefault(p => p.Session.Definition.Key == (clients ? "clients" : "suppliers"));
            if (page == null) continue;
            var pid = id;
            if (string.IsNullOrWhiteSpace(pid) && !string.IsNullOrWhiteSpace(name))
            {
                var list = clients ? InvoiceService.Clients(page.Session.Current) : InvoiceService.Suppliers(page.Session.Current);
                pid = list.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                      ?? list.FirstOrDefault(p => BatchStudio.Core.Reports.DashboardEngine.SameCompany(p.Name, name))?.Id;
            }
            if (string.IsNullOrWhiteSpace(pid)) continue;
            if (PartyDetails.OpenExisting(clients, pid)) { IsPartyDetailsOpen = true; return true; }
        }
        Dialogs.Info(Loc.F("Party.NotFound", name ?? id ?? ""));
        return false;
    }

    public MassInvoiceViewModel Mass { get; }
    public BankAccountsViewModel Banks { get; }

    [ObservableProperty] private EntityProfile? _activeEntity;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private bool _hasEntity;

    private WorkbookWatcher? _watcher;
    private System.Windows.Threading.DispatcherTimer? _poll;
    private System.Windows.Threading.DispatcherTimer? _countTimer;
    private DateTime _historyStamp, _templatesChecked;
    private int _historyCount = -1, _templateCount = -1;
    private bool _switching;

    /// <summary>The open entity's BATCH STUDIO workbook (null when none is open).</summary>
    public Workspace? Workspace { get; private set; }

    /// <summary>Folder with the sheet copies the pages read (laid out like the old Database folder).</summary>
    public string? Root => Workspace?.CacheRoot;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Current = this;
        Settings.Background ??= new();
        Settings.ListWidths ??= [];
        Settings.RecentColors ??= [];
        BatchStudio.App.Controls.BackgroundFx.Settings = Settings.Background;
        Home = new HomeViewModel(this);
        Templates = new TemplatesViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        Setup = new EntitySetupViewModel(this);
        NewInvoice = new NewInvoiceViewModel(this);
        History = new HistoryViewModel(this);
        Dashboard = new DashboardViewModel(this);
        Reports = new ReportsViewModel(this);
        Mass = new MassInvoiceViewModel(this);
        Banks = new BankAccountsViewModel(this);
        foreach (var e in settings.Entities) Entities.Add(e);
        SettingsPage.Refresh();
        DriveSync.Instance.Refresh(settings.Entities);   // Auto Save Files: the entities switched on resume uploading

        Nav.Add(new NavItem { Key = "Nav.Home", Glyph = "", Page = () => Home });
        Nav.Add(new NavItem { Key = "Nav.Dashboard", Glyph = "\uE9D2", Page = () => Dashboard });
        Nav.Add(new NavItem { Key = "Nav.NewInvoice", Glyph = "\uE710", Page = () => NewInvoice });
        Nav.Add(new NavItem { Key = "Nav.Mass", Glyph = "\uE8FD", Page = () => Mass });
        Nav.Add(new NavItem { Key = "Nav.History", Glyph = "\uE81C", Page = () => History });
        Nav.Add(new NavItem { Key = "Nav.Reports", Glyph = "\uE9F9", Page = () => Reports });
        Nav.Add(new NavItem { Key = "Nav.Bank", Glyph = "\uE825", Page = () => Banks });
        Nav.Add(new NavItem { Key = "Nav.Suppliers", Glyph = "", Page = () => Page("suppliers") });
        Nav.Add(new NavItem { Key = "Nav.Clients", Glyph = "", Page = () => Page("clients") });
        Nav.Add(new NavItem { Key = "Nav.Categories", Glyph = "", Page = () => Page("categories") });
        Nav.Add(new NavItem { Key = "Nav.Templates", Glyph = "\uE8F1", Page = () => Templates });
        // Secure mode: not a page - selecting it locks the program (the page in use stays behind the lock screen)
        Nav.Add(new NavItem { Key = "Secure.Mode", Glyph = "\uE72E", Page = () => null });
        Nav.Add(new NavItem { Key = "Nav.Settings", Glyph = "", Page = () => SettingsPage });
        LanguageService.Changed += () => { foreach (var n in Nav) n.Refresh(); };

        var active = settings.Active;
        if (active != null && Workspace.FindWorkbook(active.DatabasePath) != null) ActiveEntity = active;
        else ShowSetup();

        // Secure mode survives closing the program (and can be asked at every start)
        if (HasSecurePassword && (settings.LockedAtExit || settings.LockOnStartup)) IsLocked = true;
    }

    private DataSetViewModel? Page(string key) => DataPages.FirstOrDefault(p => p.Session.Definition.Key == key);

    // ---------------------------------------------------------------- back / forward (mouse side buttons)
    private readonly List<object> _back = [];
    private readonly List<object> _forward = [];
    private bool _historyMove;

    partial void OnCurrentPageChanged(object? oldValue, object? newValue)
    {
        if (_historyMove || oldValue == null || ReferenceEquals(oldValue, newValue) || ReferenceEquals(oldValue, Setup)) return;
        _back.Add(oldValue);
        if (_back.Count > 40) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <summary>Mouse back button: the page shown before (Home → Suppliers → back = Home).</summary>
    public void GoBack() => Move(_back, _forward);

    /// <summary>Mouse forward button: the page left with Back.</summary>
    public void GoForward() => Move(_forward, _back);

    private void Move(List<object> from, List<object> to)
    {
        if (from.Count == 0 || CurrentPage == null || !HasEntity) return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(CurrentPage);
        _historyMove = true;
        try
        {
            // a sidebar page: select it there (it refreshes like a click); a report / details page: shown as it was
            var nav = Nav.FirstOrDefault(n => ReferenceEquals(n.Page(), target));
            if (nav != null && nav != SelectedNav) SelectedNav = nav;
            else if (nav == null) CurrentPage = target;
            else CurrentPage = target;
        }
        finally { _historyMove = false; }
    }

    /// <summary>Opens a sidebar page by its key (Home cards, pulse).</summary>
    public void OpenNav(string key)
    {
        if (Nav.FirstOrDefault(n => n.Key == key) is { } nav) SelectedNav = nav;
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? value)
    {
        if (value == null) return;
        if (value.Key == "Secure.Mode")
        {
            // back to the item of the page in use, then lock
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                SetProperty(ref _selectedNav, oldValue, nameof(SelectedNav));
                EnterSecureMode();
            });
            return;
        }
        if (!HasEntity && value.Key != "Nav.Settings") { ShowSetup(); return; }
        CurrentPage = value.Page();
        if (CurrentPage is TemplatesViewModel t) t.Refresh();
        if (CurrentPage is HomeViewModel h) h.Refresh();
        if (CurrentPage is SettingsViewModel sp) sp.Refresh();
        if (CurrentPage is NewInvoiceViewModel ni) ni.Refresh();
        if (CurrentPage is HistoryViewModel hv) hv.Reload();
        if (CurrentPage is DashboardViewModel dv) dv.Refresh();
        if (CurrentPage is ReportsViewModel rv) rv.Refresh();
        if (CurrentPage is MassInvoiceViewModel mv) mv.Refresh();
        if (CurrentPage is BankAccountsViewModel bv) bv.Refresh();
    }

    partial void OnActiveEntityChanged(EntityProfile? oldValue, EntityProfile? newValue)
    {
        if (_switching || newValue == null) return;
        OpenEntity(newValue);
    }

    public void OpenEntity(EntityProfile entity)
    {
        foreach (var p in DataPages) { p.FlushPending(); p.Stop(); }
        StopWatching();
        DataPages.Clear();

        Workspace ws;
        try
        {
            var book = Workspace.FindWorkbook(entity.DatabasePath)
                       ?? throw new FileNotFoundException(Loc.F("Entity.NoWorkbook", entity.DatabasePath));
            ws = Workspace.Open(book);
            if (!string.Equals(entity.DatabasePath, book, StringComparison.OrdinalIgnoreCase)) entity.DatabasePath = book;
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
            ShowSetup();
            return;
        }
        Workspace = ws;

        foreach (var (def, key) in new[] { (DataSets.Suppliers, "Nav.Suppliers"), (DataSets.Clients, "Nav.Clients"), (DataSets.Items, "Nav.Items"), (DataSets.Categories, "Nav.Categories") })
            if (File.Exists(ws.DataPath(def)))
                DataPages.Add(new DataSetViewModel(this, new DataSetSession(ws, def), key, ""));
        // categories first: the suppliers / clients dropdowns list them
        foreach (var p in DataPages.OrderBy(p => p.Session.Definition.Key == "categories" ? 0 : 1)) p.Load();

        _watcher = new WorkbookWatcher(ws);
        _watcher.SheetsChanged += paths => Application.Current.Dispatcher.InvokeAsync(() => { foreach (var p in paths) OnFileChanged(p); });
        // safety net for folders where change notifications are unreliable (OneDrive, network drives)
        _poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _poll.Tick += (_, _) => _watcher?.Poke();
        _poll.Start();

        entity.LastOpenedUtc = DateTime.UtcNow;
        Settings.ActiveEntity = entity.Name;
        SaveSettings();
        HasEntity = true;
        _switching = true;
        ActiveEntity = entity;
        _switching = false;
        Home.Refresh();
        SelectedNav = Nav[0];
        CurrentPage = Home;
        Dashboard.Reset();
        Dashboard.Refresh();

        _historyStamp = default; _templatesChecked = default; _historyCount = _templateCount = -1;
        _countTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _countTimer.Tick += (_, _) => UpdateCounts();
        _countTimer.Start();
        UpdateCounts();
    }

    /// <summary>Sidebar numbers: records of each list, invoices in INVOICE HISTORY, linked templates.</summary>
    public void UpdateCounts(bool force = false)
    {
        if (Root == null) { foreach (var n in Nav) n.Count = ""; return; }
        static string N(int c) => c < 0 ? "" : c.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, nav) in new[] { ("suppliers", "Nav.Suppliers"), ("clients", "Nav.Clients"), ("items", "Nav.Items"), ("categories", "Nav.Categories") })
        {
            var item = Nav.FirstOrDefault(n => n.Key == nav);
            if (item == null) continue;
            var page = DataPages.FirstOrDefault(p => p.Session.Definition.Key == key);
            item.Count = page == null ? "" : N(page.Session.Current.Rows.Count(r => r.Any(c => !string.IsNullOrWhiteSpace(c))));
        }
        try
        {
            var path = DataSets.InvoiceHistory.FullPath(Root);
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (force || stamp != _historyStamp)
            {
                bool first = _historyStamp == default;
                _historyStamp = stamp;
                _historyCount = File.Exists(path) ? BatchStudio.Core.Invoices.InvoiceHistoryBook.Read(path).Count : 0;
                if (!first && !force) Home.RefreshTicker();
            }
        }
        catch { }
        try
        {
            if (force || DateTime.UtcNow - _templatesChecked > TimeSpan.FromSeconds(15))
            {
                _templatesChecked = DateTime.UtcNow;
                _templateCount = BatchStudio.Core.Templates.TemplateRegistry.Scan(Root).Count(t => t.Map != null);
            }
        }
        catch { }
        if (Nav.FirstOrDefault(n => n.Key == "Nav.History") is { } h) h.Count = N(_historyCount);
        if (Nav.FirstOrDefault(n => n.Key == "Nav.Templates") is { } t) t.Count = N(_templateCount);
    }

    private void StopWatching()
    {
        _countTimer?.Stop();
        _countTimer = null;
        _poll?.Stop();
        _poll = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// Runs a change against the workbook on a background thread (Excel writes it), then refreshes what depends on it.
    /// </summary>
    public Task RunWorkbookChange(Action<Workspace> change) =>
        Task.Run(() => change(Workspace ?? throw new InvalidOperationException("No entity is open.")));

    private void OnFileChanged(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var p in DataPages)
            if (string.Equals(Path.GetFullPath(p.Session.FilePath), full, StringComparison.OrdinalIgnoreCase))
            {
                p.OnExternalChange();
                return;
            }
        if (Root != null && string.Equals(full, Path.GetFullPath(DataSets.InvoiceHistory.FullPath(Root)), StringComparison.OrdinalIgnoreCase))
        {
            if (CurrentPage == History) History.Reload();
            Dashboard.Refresh();
            if (CurrentPage is CompanyReportViewModel cr) cr.Load();
            return;
        }
        if (Root != null && full.StartsWith(Path.GetFullPath(DatabaseLayout.Templates(Root)), StringComparison.OrdinalIgnoreCase))
        {
            Templates.Refresh();
            Home.Refresh();
            return;
        }
        if (Workspace != null && (string.Equals(full, Workspace.DashboardPath, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(full, Workspace.ReportTemplatePath, StringComparison.OrdinalIgnoreCase)))
            Dashboard.Refresh();
    }

    /// <summary>The workbook keeps its own dropdown lists (named ranges); nothing to copy any more.</summary>
    public void RefreshLists() { }

    /// <summary>
    /// Dropdown choices for a field, like the workbook's validations: categories (V06CategoryList /
    /// V14_ClientCategories), templates (V11_TemplateChoices) and Status. Null = free text.
    /// </summary>
    public bool HasOptions(string dataSet, string caption)
    {
        var c = caption.Trim();
        return c.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase) ||
               (c.Contains("Category", StringComparison.OrdinalIgnoreCase) && dataSet != "categories") ||
               string.Equals(c, "Status", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sheet names an invoice file can carry (template names + their sheets), for invoice-only PDF export.</summary>
    public IReadOnlyCollection<string> InvoiceSheetNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "INVOICE", "INV1" };
        try
        {
            if (Root != null)
                foreach (var t in BatchStudio.Core.Templates.TemplateRegistry.Scan(Root))
                {
                    set.Add(t.Name);
                    if (!string.IsNullOrWhiteSpace(t.Map?.Sheet)) set.Add(t.Map!.Sheet);
                }
        }
        catch { }
        return set;
    }

    public IReadOnlyList<string>? OptionsFor(string dataSet, string caption, IEnumerable<string> existing)
    {
        var c = caption.Trim();
        if (c.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))
            return Root == null ? [] : BatchStudio.Core.Templates.TemplateRegistry.Scan(Root).Select(t => t.Name).ToList();
        if (c.Contains("Category", StringComparison.OrdinalIgnoreCase) && dataSet != "categories")
        {
            var cat = DataPages.FirstOrDefault(p => p.Session.Definition.Key == "categories")?.Session.Current;
            if (cat == null) return null;
            var cats = BatchStudio.Core.Invoices.InvoiceService.Categories(cat);
            return c.Contains("ID", StringComparison.OrdinalIgnoreCase)
                ? cats.Select(x => x.Id).ToList()
                : cats.Where(x => x.Active).Select(x => x.Text).ToList();
        }
        if (string.Equals(c, "Status", StringComparison.OrdinalIgnoreCase))
            return new[] { "Active", "Inactive" }.Concat(existing).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return null;
    }

    /// <summary>Theme colour assigned to a supplier for a template (CLIENT THEME MEMORY), or null.</summary>
    public int? ThemeFor(string supplier, string template) =>
        Workspace == null ? null
        : BatchStudio.Core.Workbook.ThemeMemory.Find(BatchStudio.Core.Workbook.ThemeMemory.Read(Workspace.ThemeMemoryPath), supplier, template);

    /// <summary>Saves (null = removes) a supplier's theme colour in the workbook, like ASSIGN THEME.</summary>
    public Task AssignTheme(string supplier, string template, int? rgb)
    {
        var ws = Workspace ?? throw new InvalidOperationException("No entity is open.");
        int keep = Settings.KeepBackups;
        return Task.Run(() => ws.Change(ed => BatchStudio.Core.Workbook.ThemeMemory.Assign(ed, supplier, template, rgb), keep));
    }

    /// <summary>History → Open Invoice → Edit: restores the archived invoice on the New Invoice page (Smart Invoice Repair).</summary>
    public void StartInvoiceRepair(string templateName, string invoiceNo, string partyType, string archivedFile)
    {
        var nav = Nav.FirstOrDefault(n => n.Key == "Nav.NewInvoice");
        if (nav != null) SelectedNav = nav;
        NewInvoice.StartRepair(templateName, invoiceNo, partyType, archivedFile);
    }

    /// <summary>Called after an invoice was archived (history file changed by the program).</summary>
    public void OnHistoryChanged()
    {
        Home.Refresh();
        History.Reload();
        Dashboard.Refresh();
        UpdateCounts(force: true);
    }

    public CompanyReportViewModel? BuildCompanyReport(string company, string? type, BatchStudio.Core.Reports.DashboardFilter filter, object? returnPage = null)
    {
        if (Root == null) return null;
        var path = DataSets.InvoiceHistory.FullPath(Root);
        var history = File.Exists(path) ? BatchStudio.Core.Invoices.InvoiceHistoryBook.Read(path) : [];
        var t = BatchStudio.Core.Reports.CompanyReports.ResolveType(company, type, history);
        if (t == null) { Dialogs.Error(company); return null; }
        return new CompanyReportViewModel(this, company, t, filter, returnPage);
    }

    public void ShowCompanyReport(string company, string? type, BatchStudio.Core.Reports.DashboardFilter filter)
    {
        var back = CurrentPage;
        var vm = BuildCompanyReport(company, type, filter, back);
        if (vm != null) CurrentPage = vm;
    }

    public void AddEntity(EntityProfile entity)
    {
        var existing = Settings.Entities.FirstOrDefault(e => string.Equals(Workspace.FindWorkbook(e.DatabasePath) ?? e.DatabasePath, entity.DatabasePath, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            // keep names unique in the switcher
            var name = entity.Name;
            for (int i = 2; Settings.Entities.Any(e => string.Equals(e.Name, entity.Name, StringComparison.OrdinalIgnoreCase)); i++)
                entity.Name = $"{name} ({i})";
            Settings.Entities.Add(entity);
            Entities.Add(entity);
            existing = entity;
            SettingsPage.Refresh();
        }
        OpenEntity(existing);
    }

    /// <summary>Points an entity at another BATCH STUDIO workbook (e.g. after moving it or to a OneDrive copy).</summary>
    public void ChangeEntityPath(EntityProfile entity, string newPath)
    {
        var book = Workspace.FindWorkbook(newPath);
        if (book == null)
        {
            Dialogs.Error(Loc.F("Entity.NoWorkbook", newPath));
            return;
        }
        if (string.Equals(Path.GetFullPath(entity.DatabasePath), book, StringComparison.OrdinalIgnoreCase)) return;
        var other = Settings.Entities.FirstOrDefault(e => e != entity &&
            string.Equals(Workspace.FindWorkbook(e.DatabasePath) ?? e.DatabasePath, book, StringComparison.OrdinalIgnoreCase));
        if (other != null)
        {
            Dialogs.Error(Loc.F("Entity.PathInUse", other.Name));
            return;
        }
        bool wasActive = ActiveEntity == entity;
        if (wasActive) foreach (var p in DataPages) p.FlushPending();
        entity.DatabasePath = book;
        SaveSettings();
        RefreshEntityList();
        if (wasActive) OpenEntity(entity);
    }

    public void RenameEntity(EntityProfile entity, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == entity.Name) return;
        if (Settings.Entities.Any(e => e != entity && string.Equals(e.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Error(Loc.F("Entity.NameInUse", newName));
            return;
        }
        bool wasActive = ActiveEntity == entity;
        entity.Name = newName;
        if (wasActive) Settings.ActiveEntity = newName;
        SaveSettings();
        RefreshEntityList();
        if (wasActive) Home.Refresh();
    }

    /// <summary>Re-binds the entity switcher so renamed / moved entities show up.</summary>
    private void RefreshEntityList()
    {
        var active = ActiveEntity;
        _switching = true;
        Entities.Clear();
        foreach (var e in Settings.Entities) Entities.Add(e);
        ActiveEntity = active;
        _switching = false;
        SettingsPage.Refresh();
    }

    public void RemoveEntity(EntityProfile entity)
    {
        Settings.Entities.Remove(entity);
        Entities.Remove(entity);
        if (ActiveEntity == entity)
        {
            StopWatching();
            Workspace = null;
            DataPages.Clear();
            HasEntity = false;
            _switching = true;
            ActiveEntity = null;
            _switching = false;
            var next = Settings.Entities.FirstOrDefault(e => Workspace.FindWorkbook(e.DatabasePath) != null);
            if (next != null) OpenEntity(next); else ShowSetup();
        }
        SaveSettings();
    }

    [RelayCommand]
    public void ShowSetup()
    {
        SelectedNav = null;
        Setup.Reset();
        CurrentPage = Setup;
    }

    // ---------------------------------------------------------------- Secure mode: the whole program locked behind a password
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _lockError = "";
    public bool HasSecurePassword => Settings.SecurePasswordHash.Length > 0;

    /// <summary>The locked state is written at once: closing the program (✕, Alt+F4, Task Manager, a restart) never unlocks it.</summary>
    partial void OnIsLockedChanged(bool value)
    {
        if (Settings.LockedAtExit == value) return;
        Settings.LockedAtExit = value;
        SaveSettings();
    }

    /// <summary>Settings › Secure mode: ask the password every time the program starts.</summary>
    public bool LockOnStartup
    {
        get => Settings.LockOnStartup;
        set { if (Settings.LockOnStartup == value) return; Settings.LockOnStartup = value; SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Logo menu › Secure mode: locks now (creates the password first when there is none yet).</summary>
    [RelayCommand]
    private void EnterSecureMode()
    {
        if (!HasSecurePassword && !CreateSecurePassword()) return;
        LockError = "";
        IsLocked = true;
    }

    /// <summary>Settings: create the Secure mode password, or change it (the current one first).</summary>
    [RelayCommand]
    private void ChangeSecurePassword()
    {
        if (HasSecurePassword)
        {
            if (!Views.PasswordDialog.Ask(Loc.T("Pwd.SecureCurrentTitle"), Loc.T("Pwd.SecureCurrentText"),
                                          p => Core.Security.PasswordHash.Verify(p, Settings.SecurePasswordHash), PasswordRecovery.For(Settings.SecureRecoveryEmail), out var reset))
                return;
            if (reset != null) { SetSecurePassword(reset); return; }   // "Forgot password?" already asked for the new one
        }
        CreateSecurePassword();
    }

    private bool CreateSecurePassword()
    {
        var pw = Views.PasswordDialog.Create(Loc.T("Pwd.SecureCreateTitle"), Loc.T("Pwd.SecureCreateText"));
        if (pw == null) return false;
        SetSecurePassword(pw);
        return true;
    }

    private void SetSecurePassword(string pw)
    {
        Settings.SecurePasswordHash = Core.Security.PasswordHash.Create(pw);
        Settings.SecureRecoveryEmail = PasswordRecovery.CurrentEmail;
        SaveSettings();
        OnPropertyChanged(nameof(HasSecurePassword));
        Settings_SecureChanged?.Invoke();
    }

    /// <summary>Raised when the Secure mode password is created / changed (Settings refreshes its button).</summary>
    public event Action? Settings_SecureChanged;

    /// <summary>Screenshot checks only (--page locked): the lock screen without a password.</summary>
    internal void LockForScreenshot() { IsLocked = true; Settings.LockedAtExit = false; SaveSettings(); }

    /// <summary>Checks the Secure mode password without any change (safe off the screen thread).</summary>
    public bool CheckScreenPassword(string password) => Core.Security.PasswordHash.Verify(password, Settings.SecurePasswordHash);

    /// <summary>Lock screen: the password typed.</summary>
    public bool TryUnlockScreen(string password)
    {
        if (Core.Security.PasswordHash.Verify(password, Settings.SecurePasswordHash)) { IsLocked = false; LockError = ""; return true; }
        LockError = Loc.T("Pwd.Wrong");
        return false;
    }

    /// <summary>Lock screen › Forgot password: Google sign-in with the linked account, then a new password, then open.</summary>
    public async Task ForgotScreenPasswordAsync()
    {
        LockError = "";
        if (!await PasswordRecovery.For(Settings.SecureRecoveryEmail)()) { LockError = Loc.T("Pwd.ForgotFailed"); return; }
        var pw = Views.PasswordDialog.Create(Loc.T("Pwd.ResetTitle"), Loc.T("Pwd.ResetText"));
        if (pw == null) return;
        SetSecurePassword(pw);
        IsLocked = false;
    }

    /// <summary>The running program's view model (views use it for small UI preferences such as list widths).</summary>
    public static MainViewModel? Current { get; private set; }

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
        DriveSync.Instance.Refresh(Settings.Entities);   // a new entity / data folder is followed by Auto Save Files
    }

    /// <summary>Returns false if the user cancels closing.</summary>
    public bool OnClosing()
    {
        bool allSaved = DataPages.All(p => p.FlushPending());
        if (!allSaved && !Dialogs.Confirm(Loc.T("Close.Pending"))) return false;
        foreach (var p in DataPages) p.Stop();
        StopWatching();
        SaveSettings();
        return true;
    }
}
