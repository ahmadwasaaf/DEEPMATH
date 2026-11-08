using System.Collections.ObjectModel;
using System.Windows;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Entities;
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
}

public sealed partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
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

    [ObservableProperty] private EntityProfile? _activeEntity;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private bool _hasEntity;

    private DatabaseWatcher? _watcher;
    private bool _switching;

    public string? Root => ActiveEntity?.DatabasePath;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Home = new HomeViewModel(this);
        Templates = new TemplatesViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        Setup = new EntitySetupViewModel(this);
        NewInvoice = new NewInvoiceViewModel(this);
        History = new HistoryViewModel(this);
        Dashboard = new DashboardViewModel(this);
        foreach (var e in settings.Entities) Entities.Add(e);
        SettingsPage.Refresh();

        Nav.Add(new NavItem { Key = "Nav.Home", Glyph = "", Page = () => Home });
        Nav.Add(new NavItem { Key = "Nav.Dashboard", Glyph = "\uE9D2", Page = () => Dashboard });
        Nav.Add(new NavItem { Key = "Nav.NewInvoice", Glyph = "\uE710", Page = () => NewInvoice });
        Nav.Add(new NavItem { Key = "Nav.History", Glyph = "\uE81C", Page = () => History });
        Nav.Add(new NavItem { Key = "Nav.Suppliers", Glyph = "", Page = () => Page("suppliers") });
        Nav.Add(new NavItem { Key = "Nav.Clients", Glyph = "", Page = () => Page("clients") });
        Nav.Add(new NavItem { Key = "Nav.Items", Glyph = "", Page = () => Page("items") });
        Nav.Add(new NavItem { Key = "Nav.Categories", Glyph = "", Page = () => Page("categories") });
        Nav.Add(new NavItem { Key = "Nav.Templates", Glyph = "\uE8F1", Page = () => Templates });
        Nav.Add(new NavItem { Key = "Nav.Settings", Glyph = "", Page = () => SettingsPage });
        LanguageService.Changed += () => { foreach (var n in Nav) n.Refresh(); };

        var active = settings.Active;
        if (active != null && Directory.Exists(active.DatabasePath)) ActiveEntity = active;
        else ShowSetup();
    }

    private DataSetViewModel? Page(string key) => DataPages.FirstOrDefault(p => p.Session.Definition.Key == key);

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        if (!HasEntity && value.Key != "Nav.Settings") { ShowSetup(); return; }
        CurrentPage = value.Page();
        if (CurrentPage is TemplatesViewModel t) t.Refresh();
        if (CurrentPage is HomeViewModel h) h.Refresh();
        if (CurrentPage is SettingsViewModel sp) sp.Refresh();
        if (CurrentPage is NewInvoiceViewModel ni) ni.Refresh();
        if (CurrentPage is HistoryViewModel hv) hv.Reload();
        if (CurrentPage is DashboardViewModel dv) dv.Refresh();
    }

    partial void OnActiveEntityChanged(EntityProfile? oldValue, EntityProfile? newValue)
    {
        if (_switching || newValue == null) return;
        OpenEntity(newValue);
    }

    public void OpenEntity(EntityProfile entity)
    {
        foreach (var p in DataPages) { p.FlushPending(); p.Stop(); }
        _watcher?.Dispose();
        _watcher = null;
        DataPages.Clear();

        try
        {
            DatabaseLayout.EnsureStructure(entity.DatabasePath, entity.Name);
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
            ShowSetup();
            return;
        }

        var root = entity.DatabasePath;
        DataPages.Add(new DataSetViewModel(this, new DataSetSession(root, DataSets.Suppliers), "Nav.Suppliers", ""));
        DataPages.Add(new DataSetViewModel(this, new DataSetSession(root, DataSets.Clients), "Nav.Clients", ""));
        DataPages.Add(new DataSetViewModel(this, new DataSetSession(root, DataSets.Items), "Nav.Items", ""));
        DataPages.Add(new DataSetViewModel(this, new DataSetSession(root, DataSets.Categories), "Nav.Categories", ""));
        foreach (var p in DataPages) p.Load();

        _watcher = new DatabaseWatcher(root);
        _watcher.FileChanged += path => Application.Current.Dispatcher.InvokeAsync(() => OnFileChanged(path));

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
        RefreshLists();
        Dashboard.Reset();
        Dashboard.Refresh();
    }

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
            RefreshLists();
        }
    }

    /// <summary>Refreshes the Category / DEFAULT TEMPLATE dropdown lists inside Suppliers.xlsx and Clients.xlsx.</summary>
    public void RefreshLists()
    {
        if (Root == null) return;
        if (DataPages.Any(p => p.Session.Definition.Key is "suppliers" or "clients" && p.IsDirty)) return;
        var cat = Page("categories")?.Session.Current;
        if (cat == null) return;
        try
        {
            var cats = BatchStudio.Core.Invoices.InvoiceService.Categories(cat).Select(c => c.Text).ToList();
            var tpls = BatchStudio.Core.Templates.TemplateRegistry.Scan(Root).Select(t => t.Name).ToList();
            ListsSync.Update(Root, cats, tpls);
        }
        catch { /* lists are a convenience; never block the app */ }
    }

    /// <summary>Called after an invoice was archived (history file changed by the program).</summary>
    public void OnHistoryChanged()
    {
        Home.Refresh();
        History.Reload();
        Dashboard.Refresh();   // also keeps Dashboard\Dashboard.xlsx current
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
        var existing = Settings.Entities.FirstOrDefault(e => string.Equals(e.DatabasePath, entity.DatabasePath, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Points an entity at another Database folder (e.g. after moving it or to a OneDrive copy).</summary>
    public void ChangeEntityPath(EntityProfile entity, string newPath)
    {
        if (string.Equals(Path.GetFullPath(entity.DatabasePath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase)) return;
        var other = Settings.Entities.FirstOrDefault(e => e != entity &&
            string.Equals(Path.GetFullPath(e.DatabasePath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase));
        if (other != null)
        {
            Dialogs.Error(Loc.F("Entity.PathInUse", other.Name));
            return;
        }
        var missing = DatabaseLayout.Validate(newPath);
        if (missing.Count > 0 && !Dialogs.Confirm(Loc.F("Entity.PathMissing", string.Join("\n", missing.Take(10).Select(m => "• " + m)))))
            return;
        bool wasActive = ActiveEntity == entity;
        if (wasActive) foreach (var p in DataPages) p.FlushPending();
        entity.DatabasePath = newPath;
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
        try
        {
            var info = DatabaseLayout.ReadEntityInfo(entity.DatabasePath) ?? new EntityInfo { CreatedUtc = DateTime.UtcNow };
            info.Name = newName;
            DatabaseLayout.WriteEntityInfo(entity.DatabasePath, info);
        }
        catch { /* folder may be read-only; the name is still kept in settings */ }
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
            _watcher?.Dispose();
            _watcher = null;
            DataPages.Clear();
            HasEntity = false;
            _switching = true;
            ActiveEntity = null;
            _switching = false;
            var next = Settings.Entities.FirstOrDefault(e => Directory.Exists(e.DatabasePath));
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

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    /// <summary>Returns false if the user cancels closing.</summary>
    public bool OnClosing()
    {
        bool allSaved = DataPages.All(p => p.FlushPending());
        if (!allSaved && !Dialogs.Confirm(Loc.T("Close.Pending"))) return false;
        foreach (var p in DataPages) p.Stop();
        _watcher?.Dispose();
        SaveSettings();
        return true;
    }
}
