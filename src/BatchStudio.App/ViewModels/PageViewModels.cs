using System.Collections.ObjectModel;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Entities;
using BatchStudio.Core.Templates;
using BatchStudio.Core.Workbook;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.ViewModels;

// ====================================================================== Home
public sealed class StatTile
{
    public required string Title { get; init; }
    public required string Value { get; init; }
    public required string Glyph { get; init; }
}

/// <summary>A colour square of the accent palette (Settings → Appearance).</summary>
public sealed partial class AccentSwatch : ObservableObject
{
    public required string Hex { get; init; }
    public System.Windows.Media.Brush Brush => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Hex));
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One indicator of the Home "Business pulse" strip (small caps label over the value).</summary>
public sealed class TickerItem
{
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public required string Value { get; init; }
    /// <summary>Exact figure shown on hover.</summary>
    public string Tip { get; init; } = "";
    public bool HasTip => Tip.Length > 0;
}

public sealed partial class HomeViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<StatTile> Tiles { get; } = [];
    public ObservableCollection<TickerItem> Ticker { get; } = [];
    // Business pulse (all time)
    [ObservableProperty] private string _pulseTopSupplier = "—";
    [ObservableProperty] private string _pulseTopSupplierTip = "";
    [ObservableProperty] private string _pulseTopClient = "—";
    [ObservableProperty] private string _pulseTopClientTip = "";
    [ObservableProperty] private string _pulseInvoices = "0";
    [ObservableProperty] private string _pulseTotal = "$0";
    [ObservableProperty] private string _pulseTotalTip = "";
    /// <summary>Raised after the ticker entries changed (the view restarts its scroll).</summary>
    public event Action? TickerChanged;
    public ObservableCollection<string> Missing { get; } = [];
    [ObservableProperty] private string _entityName = "";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private bool _hasMissing;

    public GoogleAccount Account => GoogleAccount.Instance;
    public GmailWatcher Mail => GmailWatcher.Instance;
    public NetworkStatus Net => NetworkStatus.Instance;

    /// <summary>The picture beside "Welcome": signed out, it leads to Settings (Sign in with Google).</summary>
    [RelayCommand]
    private void OpenAccount()
    {
        if (main.Nav.FirstOrDefault(n => n.Key == "Nav.Settings") is { } nav) main.SelectedNav = nav;
    }

    public void Refresh()
    {
        Tiles.Clear();
        Missing.Clear();
        var root = main.Root;
        if (root == null) return;
        EntityName = main.ActiveEntity?.Name ?? "";
        var ws = main.Workspace!;
        DatabasePath = ws.WorkbookPath;
        // same order and icons as the sidebar
        foreach (var (key, nav) in new[] { ("suppliers", "Nav.Suppliers"), ("clients", "Nav.Clients"), ("items", "Nav.Items"), ("categories", "Nav.Categories") })
        {
            var p = main.DataPages.FirstOrDefault(x => x.Session.Definition.Key == key);
            if (p == null) continue;
            Tiles.Add(new StatTile { Title = p.Title, Value = p.RowCount.ToString("N0"), Glyph = main.Nav.FirstOrDefault(n => n.Key == nav)?.Glyph ?? "" });
        }
        var tpl = TemplateRegistry.Scan(root);
        Tiles.Add(new StatTile
        {
            Title = Loc.T("Nav.Templates"),
            Value = $"{tpl.Count(t => t.Status == TemplateStatus.Linked)} / {tpl.Count}",
            Glyph = main.Nav.FirstOrDefault(n => n.Key == "Nav.Templates")?.Glyph ?? "",
        });
        RefreshTicker();
        foreach (var d in DataSets.All)
            if (!ws.Sheets.Contains(d.SheetName, StringComparer.OrdinalIgnoreCase)) Missing.Add(Loc.F("Home.MissingSheet", d.SheetName));
        if (!Directory.Exists(ws.ArchiveRoot)) Missing.Add(Loc.F("Home.MissingArchive", ws.ArchiveRoot));
        HasMissing = Missing.Count > 0;
    }

    /// <summary>The "Invoices" card (count of INVOICE HISTORY) - kept last, updated with the pulse.</summary>
    private void SetInvoicesTile(int count)
    {
        var title = Loc.T("Home.InvoicesTile");
        var tile = new StatTile
        {
            Title = title,
            Value = count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            Glyph = main.Nav.FirstOrDefault(n => n.Key == "Nav.History")?.Glyph ?? "",
        };
        int i = -1;
        for (int k = 0; k < Tiles.Count; k++) if (Tiles[k].Title == title) { i = k; break; }
        if (i >= 0) Tiles[i] = tile; else Tiles.Add(tile);
    }

    /// <summary>$5.19M / $812.5K / $950 style amount for the pulse strip.</summary>
    public static string Compact(decimal v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var a = Math.Abs(v);
        if (a >= 1_000_000_000m) return "$" + (v / 1_000_000_000m).ToString("0.##", inv) + "B";
        if (a >= 1_000_000m) return "$" + (v / 1_000_000m).ToString("0.##", inv) + "M";
        if (a >= 1_000m) return "$" + (v / 1_000m).ToString("0.#", inv) + "K";
        return "$" + v.ToString("0.##", inv);
    }

    /// <summary>Latest figures of INVOICE HISTORY (all time): top supplier, top client, total value, invoice count.</summary>
    public void RefreshTicker()
    {
        Ticker.Clear();
        var root = main.Root;
        if (root == null) { TickerChanged?.Invoke(); return; }
        try
        {
            var path = DataSets.InvoiceHistory.FullPath(root);
            var history = File.Exists(path) ? BatchStudio.Core.Invoices.InvoiceHistoryBook.Read(path) : [];
            var sup = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers")?.Session.Current;
            var cli = main.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "clients")?.Session.Current;
            var r = BatchStudio.Core.Reports.DashboardEngine.Compute(history, BatchStudio.Core.Reports.DashboardEngine.Categories(sup, cli),
                new BatchStudio.Core.Reports.DashboardFilter());
            var topS = r.Ranking.FirstOrDefault(x => x.Type == "SUPPLIERS");
            var topC = r.Ranking.FirstOrDefault(x => x.Type == "CLIENTS");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            PulseInvoices = r.Invoices.ToString("N0", inv);
            SetInvoicesTile(r.Invoices);
            PulseTotal = Compact(r.Total);
            PulseTotalTip = r.Total.ToString("N2", inv);
            PulseTopSupplier = topS?.Name ?? "—";
            PulseTopSupplierTip = topS == null ? "" : $"{topS.Name} · {topS.Amount.ToString("N2", inv)}";
            PulseTopClient = topC?.Name ?? "—";
            PulseTopClientTip = topC == null ? "" : $"{topC.Name} · {topC.Amount.ToString("N2", inv)}";
            Ticker.Add(new TickerItem { Glyph = "", Label = Loc.T("Home.PulseInvoices"), Value = r.Invoices.ToString("N0", inv), Tip = "" });
            Ticker.Add(new TickerItem { Glyph = "", Label = Loc.T("Home.PulseValue"), Value = Compact(r.Total), Tip = r.Total.ToString("N2", inv) });
            if (topS != null) Ticker.Add(new TickerItem { Glyph = "", Label = Loc.T("Home.PulseTopSupplier"), Value = topS.Name, Tip = $"{topS.Name} · {topS.Amount.ToString("N2", inv)}" });
            if (topC != null) Ticker.Add(new TickerItem { Glyph = "", Label = Loc.T("Home.PulseTopClient"), Value = topC.Name, Tip = $"{topC.Name} · {topC.Amount.ToString("N2", inv)}" });
        }
        catch { }
        TickerChanged?.Invoke();
    }

    [RelayCommand]
    private void Repair()
    {
        var ws = main.Workspace;
        if (ws == null) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(ws.ArchiveRoot, DatabaseLayout.ArchiveSuppliers));
            Directory.CreateDirectory(Path.Combine(ws.ArchiveRoot, DatabaseLayout.ArchiveClients));
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
        if (main.ActiveEntity != null) main.OpenEntity(main.ActiveEntity);
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var e = main.ActiveEntity;
        if (e == null) return;
        var file = Dialogs.PickFile(Loc.T("Setup.WorkbookFilter"), Path.GetDirectoryName(e.DatabasePath));
        if (file != null) main.ChangeEntityPath(e, file);
    }

    [RelayCommand]
    private void OpenDatabase()
    {
        if (main.Workspace != null) Shell.ShowInFolder(main.Workspace.WorkbookPath);
    }

    [RelayCommand]
    private void OpenWorkbook()
    {
        if (main.Workspace != null) Shell.Open(main.Workspace.WorkbookPath);
    }
}

// ====================================================================== Templates
public sealed partial class TemplateItemViewModel : ObservableObject
{
    public required TemplateEntry Entry { get; init; }
    public string Name => Entry.Name;
    public string StatusText => Entry.Status switch
    {
        TemplateStatus.Linked => Loc.T("Tpl.Status.Linked"),
        TemplateStatus.ChangedSinceLinked => Loc.T("Tpl.Status.Changed"),
        _ => Loc.T("Tpl.Status.NotLinked"),
    };
    public bool IsLinked => Entry.Status == TemplateStatus.Linked;
    public bool NeedsAttention => Entry.Status != TemplateStatus.Linked;
}

public sealed partial class FieldRowViewModel : ObservableObject
{
    public required string Field { get; init; }
    public string FieldName => Loc.T("Field." + Field);
    public bool IsCore { get; init; }
    [ObservableProperty] private string _cell = "";
    public string OriginalCell { get; init; } = "";
    public double Confidence { get; init; }
    public string ConfidenceText => Confidence <= 0 ? "-" : $"{Confidence:P0}";
    public string Level => Confidence >= 0.85 ? "High" : Confidence >= 0.6 ? "Medium" : Confidence > 0 ? "Low" : "None";
    public string Method { get; init; } = "";
    public string Sample { get; init; } = "";
    public string? Note { get; init; }
}

public sealed partial class TemplatesViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<TemplateItemViewModel> Items { get; } = [];
    public ObservableCollection<FieldRowViewModel> Fields { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private TemplateItemViewModel? _selected;
    [ObservableProperty] private string _itemsInfo = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty;

    private TemplateMap? _map;

    public void Refresh()
    {
        if (main.Root == null) return;
        var keep = Selected?.Name;
        Items.Clear();
        foreach (var e in TemplateRegistry.Scan(main.Root)) Items.Add(new TemplateItemViewModel { Entry = e });
        IsEmpty = Items.Count == 0;
        Selected = Items.FirstOrDefault(i => i.Name == keep) ?? Items.FirstOrDefault();
    }

    partial void OnSelectedChanged(TemplateItemViewModel? value)
    {
        Fields.Clear();
        Warnings.Clear();
        ItemsInfo = "";
        Status = "";
        _map = null;
        if (value?.Entry.Map is { } map && value.Entry.Status == TemplateStatus.Linked) Show(map);
        else if (value != null) _ = DetectAsync();
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (Selected == null) return;
        var path = Selected.Entry.Path;
        IsBusy = true;
        Status = "";
        try
        {
            var map = await Task.Run(() => TemplateDetector.DetectFile(path));
            Show(map);
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Show(TemplateMap map)
    {
        _map = map;
        Fields.Clear();
        Warnings.Clear();
        foreach (var f in TemplateFields.All)
        {
            if (!map.Fields.TryGetValue(f, out var m) && !TemplateFields.Core.Contains(f)) continue;
            m ??= new FieldMapping { Method = "not found" };
            Fields.Add(new FieldRowViewModel
            {
                Field = f,
                IsCore = TemplateFields.Core.Contains(f),
                Cell = m.Cell ?? "",
                OriginalCell = m.Cell ?? "",
                Confidence = m.Confirmed ? 1 : m.Confidence,
                Method = m.Confirmed ? "confirmed" : m.Method,
                Sample = m.Sample ?? "",
                Note = m.Note,
            });
        }
        foreach (var w in map.Warnings) Warnings.Add(w);
        ItemsInfo = map.Items == null ? "-" :
            $"{Loc.T("Tpl.Items")}: rows {map.Items.FirstRow}–{map.Items.LastRow} ({map.Items.Capacity}) · " +
            string.Join("  ", map.Items.Columns.Select(kv => $"{kv.Key}={kv.Value}")) +
            (map.VatRate is { } v ? $" · VAT {v:P0}" : "");
    }

    [RelayCommand]
    private void SaveMap()
    {
        if (Selected == null || _map == null) return;
        foreach (var row in Fields)
        {
            var cell = row.Cell.Trim().ToUpperInvariant().Replace("$", "");
            if (cell.Length > 0 && !TemplateDetector.TryParseRef(cell, out _, out _))
            {
                Status = $"{row.FieldName}: \"{row.Cell}\" ?";
                return;
            }
            if (!_map.Fields.TryGetValue(row.Field, out var m)) _map.Fields[row.Field] = m = new FieldMapping();
            if (cell != row.OriginalCell) m.Method = "set by user";
            m.Cell = cell.Length == 0 ? null : cell;
            m.Confirmed = true;
        }
        try
        {
            _map.FileHash = TemplateMap.HashFile(Selected.Entry.Path);
            _map.Save(Selected.Entry.Path);
            Status = Loc.T("Tpl.Saved");
            Refresh();
            main.RefreshLists();
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    /// <summary>Templates are sheets of the workbook: a new one is added in Excel and shows up here by itself.</summary>
    [RelayCommand]
    private void AddTemplate()
    {
        if (main.Workspace == null) return;
        if (Dialogs.Confirm(Loc.T("Tpl.AddInWorkbook"))) Shell.Open(main.Workspace.WorkbookPath);
    }

    [RelayCommand]
    private void OpenTemplate()
    {
        if (main.Workspace != null) Shell.Open(main.Workspace.WorkbookPath);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (main.Workspace != null) Shell.ShowInFolder(main.Workspace.WorkbookPath);
    }
}

// ====================================================================== Settings
public sealed partial class EntityRowViewModel : ObservableObject
{
    public required EntityProfile Entity { get; init; }
    [ObservableProperty] private string _name = "";
    public string DatabasePath => Entity.DatabasePath;
    public bool Exists => Workspace.FindWorkbook(Entity.DatabasePath) != null;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    /// <summary>Appearance: Language and Theme always shown; the arrow opens / closes the accent colours (remembered).</summary>
    public bool AppearanceOpen
    {
        get => _main.Settings.AppearanceOpen;
        set
        {
            if (_main.Settings.AppearanceOpen == value) return;
            _main.Settings.AppearanceOpen = value;
            _main.SaveSettings();
            OnPropertyChanged();
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleAppearance() => AppearanceOpen = !AppearanceOpen;
    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public void Refresh()
    {
        Entities.Clear();
        foreach (var e in _main.Settings.Entities) Entities.Add(new EntityRowViewModel { Entity = e, Name = e.Name });
    }

    [RelayCommand]
    private void OpenEntityFolder(EntityRowViewModel? row)
    {
        if (row != null && Workspace.FindWorkbook(row.Entity.DatabasePath) is { } book) Shell.ShowInFolder(book);
    }

    [RelayCommand]
    private void OpenEntityWorkbook(EntityRowViewModel? row)
    {
        if (row != null && Workspace.FindWorkbook(row.Entity.DatabasePath) is { } book) Shell.Open(book);
    }

    [RelayCommand]
    private void ChangePath(EntityRowViewModel? row)
    {
        if (row == null) return;
        var start = File.Exists(row.Entity.DatabasePath) ? Path.GetDirectoryName(row.Entity.DatabasePath)
            : Directory.Exists(row.Entity.DatabasePath) ? row.Entity.DatabasePath : null;
        var file = Dialogs.PickFile(Loc.T("Setup.WorkbookFilter"), start);
        if (file != null) _main.ChangeEntityPath(row.Entity, file);
    }

    [RelayCommand]
    private void Rename(EntityRowViewModel? row)
    {
        if (row != null) _main.RenameEntity(row.Entity, row.Name);
    }

    [ObservableProperty] private bool _isArabic;
    [ObservableProperty] private bool _isLight;
    [ObservableProperty] private bool _autoSave;
    [ObservableProperty] private int _keepBackups;

    // ---------------------------------------------------------------- Home background (particle animation)
    public IReadOnlyList<string> BgStyles => BackgroundSettings.Styles.Select(s => Loc.T("Bg.Style." + s)).ToList();
    public IReadOnlyList<string> BgColors => [Loc.T("Bg.Color.Auto"), Loc.T("Bg.Color.White"), Loc.T("Bg.Color.Blue"), Loc.T("Bg.Color.Custom")];
    [ObservableProperty] private bool _bgEnabled;
    [ObservableProperty] private int _bgStyleIndex;
    [ObservableProperty] private double _bgSpeed;
    [ObservableProperty] private double _bgOpacity;
    [ObservableProperty] private double _bgDensity;
    [ObservableProperty] private bool _bgMouse;
    [ObservableProperty] private int _bgColorIndex;
    [ObservableProperty] private string _bgCustomHex = "#4FACFE";
    private bool _bgLoading;
    private const string BlueHex = "#4FACFE";

    private BackgroundSettings Bg => _main.Settings.Background;

    private void LoadBackground()
    {
        _bgLoading = true;
        BgEnabled = Bg.Enabled;
        BgStyleIndex = Math.Max(0, Array.IndexOf(BackgroundSettings.Styles, Bg.Style));
        BgSpeed = Bg.Speed;
        BgOpacity = Bg.Opacity;
        BgDensity = Bg.Density;
        BgMouse = Bg.Mouse;
        var c = (Bg.Color ?? "auto").Trim();
        BgColorIndex = c.Equals("auto", StringComparison.OrdinalIgnoreCase) ? 0 : c.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) ? 1
                     : c.Equals(BlueHex, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        if (BgColorIndex == 3) BgCustomHex = c;
        _bgLoading = false;
    }

    private void ApplyBackground()
    {
        if (_bgLoading) return;
        Bg.Enabled = BgEnabled;
        Bg.Style = BackgroundSettings.Styles[Math.Clamp(BgStyleIndex, 0, BackgroundSettings.Styles.Length - 1)];
        Bg.Speed = Math.Round(BgSpeed, 2);
        Bg.Opacity = Math.Round(BgOpacity, 2);
        Bg.Density = Math.Round(BgDensity, 2);
        Bg.Mouse = BgMouse;
        Bg.Color = BgColorIndex switch { 0 => "auto", 1 => "#FFFFFF", 2 => BlueHex, _ => BgCustomHex };
        _main.SaveSettings();
        BatchStudio.App.Controls.BackgroundFx.Notify();
    }

    partial void OnBgEnabledChanged(bool value) => ApplyBackground();
    partial void OnBgStyleIndexChanged(int value) => ApplyBackground();
    partial void OnBgSpeedChanged(double value) => ApplyBackground();
    partial void OnBgOpacityChanged(double value) => ApplyBackground();
    partial void OnBgDensityChanged(double value) => ApplyBackground();
    partial void OnBgMouseChanged(bool value) => ApplyBackground();
    partial void OnBgColorIndexChanged(int value)
    {
        if (value == 3 && !_bgLoading) { PickBgColor(); return; }
        ApplyBackground();
    }

    [RelayCommand]
    private void PickBgColor()
    {
        int? initial = null;
        if (ThemeMemory.TryParseHex(BgCustomHex, out var rgb)) initial = rgb;
        if (ColorPicker.Pick(initial) is { } picked) BgCustomHex = ThemeMemory.ToHex(picked);
        _bgLoading = true;
        BgColorIndex = 3;
        _bgLoading = false;
        ApplyBackground();
    }

    [RelayCommand]
    private void ResetBackground()
    {
        var d = new BackgroundSettings();
        _main.Settings.Background.Enabled = d.Enabled;
        _main.Settings.Background.Style = d.Style;
        _main.Settings.Background.Speed = d.Speed;
        _main.Settings.Background.Opacity = d.Opacity;
        _main.Settings.Background.Density = d.Density;
        _main.Settings.Background.Mouse = d.Mouse;
        _main.Settings.Background.Color = d.Color;
        LoadBackground();
        ApplyBackground();
    }

    // ---------------------------------------------------------------- accent colour
    public List<AccentSwatch> AccentPalette { get; } = AccentService.Palette.Select(h => new AccentSwatch { Hex = h }).ToList();
    public ObservableCollection<AccentSwatch> RecentAccents { get; } = [];
    [ObservableProperty] private bool _useWindowsAccent;
    [ObservableProperty] private string _accentHex = "";
    public bool HasRecentAccents => RecentAccents.Count > 0;

    private void LoadAccent()
    {
        var a = _main.Settings.Accent ?? "auto";
        _accentLoading = true;
        UseWindowsAccent = a.Equals("auto", StringComparison.OrdinalIgnoreCase);
        AccentHex = UseWindowsAccent ? "" : a.ToUpperInvariant();
        _accentLoading = false;
        RecentAccents.Clear();
        foreach (var h in _main.Settings.RecentAccents.Take(6)) RecentAccents.Add(new AccentSwatch { Hex = h });
        OnPropertyChanged(nameof(HasRecentAccents));
        MarkSelectedAccent();
    }
    private bool _accentLoading;

    private void MarkSelectedAccent()
    {
        foreach (var sw in AccentPalette.Concat(RecentAccents))
            sw.IsSelected = !UseWindowsAccent && string.Equals(sw.Hex, AccentHex, StringComparison.OrdinalIgnoreCase);
    }

    private void SetAccent(string accent)
    {
        _main.Settings.Accent = accent;
        if (!accent.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var list = _main.Settings.RecentAccents;
            list.RemoveAll(x => string.Equals(x, accent, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, accent.ToUpperInvariant());
            if (list.Count > 6) list.RemoveRange(6, list.Count - 6);
        }
        _main.SaveSettings();
        ThemeService.ApplyAccent(accent);
        LoadAccent();
    }

    partial void OnUseWindowsAccentChanged(bool value)
    {
        if (_accentLoading) return;
        SetAccent(value ? "auto" : (_main.Settings.RecentAccents.FirstOrDefault() ?? "#0078D7"));
    }

    [RelayCommand]
    private void PickAccent(AccentSwatch? swatch)
    {
        if (swatch != null) SetAccent(swatch.Hex);
    }

    [RelayCommand]
    private void CustomAccent()
    {
        int? initial = BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(AccentHex, out var rgb) ? rgb : null;
        if (ColorPicker.Pick(initial) is { } picked) SetAccent(BatchStudio.Core.Workbook.ThemeMemory.ToHex(picked));
    }

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        LoadBackground();
        LoadAccent();
        _isArabic = main.Settings.Language == AppLanguage.Arabic;
        _isLight = main.Settings.Theme == AppTheme.Light;
        _autoSave = main.Settings.AutoSave;
        _keepBackups = main.Settings.KeepBackups;
        _googleClientId = GoogleAccount.Instance.ClientId;
        _googleClientSecret = GoogleAccount.Instance.ClientSecret;
    }

    // ---------------------------------------------------------------- Google account (new emails on Home + notifications)
    public GoogleAccount Account => GoogleAccount.Instance;
    [ObservableProperty] private string _googleClientId = "";
    [ObservableProperty] private string _googleClientSecret = "";

    partial void OnGoogleClientIdChanged(string value) => Account.SetClient(value, GoogleClientSecret);
    partial void OnGoogleClientSecretChanged(string value) => Account.SetClient(GoogleClientId, value);

    public bool MailToasts
    {
        get => _main.Settings.MailToasts;
        set
        {
            if (_main.Settings.MailToasts == value) return;
            _main.Settings.MailToasts = value;
            _main.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private async Task GoogleSignIn()
    {
        Account.SetClient(GoogleClientId, GoogleClientSecret);
        await Account.SignInAsync();
    }

    [RelayCommand]
    private Task GoogleSignOut() => Account.SignOutAsync();

    /// <summary>The client_secret_….json downloaded from Google Cloud fills both boxes.</summary>
    [RelayCommand]
    private void LoadGoogleClient()
    {
        var file = Dialogs.PickFile("Google OAuth client (*.json)|*.json");
        if (file == null) return;
        if (!Account.LoadClientFile(file)) { Dialogs.Error(Loc.T("Google.FileBad")); return; }
        GoogleClientId = Account.ClientId;
        GoogleClientSecret = Account.ClientSecret;
    }

    partial void OnIsArabicChanged(bool value)
    {
        _main.Settings.Language = value ? AppLanguage.Arabic : AppLanguage.English;
        LanguageService.Apply(_main.Settings.Language);
        _main.SaveSettings();
        _main.Home.Refresh();
    }

    partial void OnIsLightChanged(bool value)
    {
        _main.Settings.Theme = value ? AppTheme.Light : AppTheme.Dark;
        ThemeService.Apply(_main.Settings.Theme);
        _main.SaveSettings();
    }

    partial void OnAutoSaveChanged(bool value)
    {
        _main.Settings.AutoSave = value;
        _main.SaveSettings();
    }

    partial void OnKeepBackupsChanged(int value)
    {
        _main.Settings.KeepBackups = Math.Clamp(value, 1, 500);
        _main.SaveSettings();
    }

    [RelayCommand]
    private void RemoveEntity(EntityRowViewModel? row)
    {
        var e = row?.Entity;
        if (e != null && Dialogs.Confirm($"{Loc.T("Set.RemoveEntity")}: {e.Name}?\n\n{Loc.T("Set.RemoveNote")}"))
            _main.RemoveEntity(e);
        Refresh();
    }

    [RelayCommand]
    private void AddEntity() => _main.ShowSetup();
}

// ====================================================================== Entity setup
/// <summary>Links an entity to its BATCH STUDIO workbook (.xlsm). The workbook stays the only data file.</summary>
public sealed partial class EntitySetupViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private string _entityName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchivePath))]
    private string _workbookPath = "";
    [ObservableProperty] private string _error = "";

    public string ArchivePath =>
        WorkbookPath.Length == 0 ? "" : Path.Combine(Path.GetDirectoryName(WorkbookPath) ?? "", DatabaseLayout.ArchiveFolder);

    public bool CanCancel => main.HasEntity;

    public void Reset()
    {
        EntityName = "";
        WorkbookPath = "";
        Error = "";
        OnPropertyChanged(nameof(CanCancel));
    }

    [RelayCommand]
    private void Browse()
    {
        var f = Dialogs.PickFile(Loc.T("Setup.WorkbookFilter"));
        if (f == null) return;
        WorkbookPath = f;
        if (string.IsNullOrWhiteSpace(EntityName)) EntityName = SuggestName(f);
    }

    private static string SuggestName(string workbook)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(workbook)?.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        if (name.EndsWith(" Database", StringComparison.OrdinalIgnoreCase)) name = name[..^" Database".Length];
        return name.Trim().Length > 0 ? name.Trim() : Path.GetFileNameWithoutExtension(workbook);
    }

    [RelayCommand]
    private void Link()
    {
        Error = "";
        var book = Workspace.FindWorkbook(WorkbookPath.Trim().Trim('"'));
        if (book == null)
        {
            Error = Loc.F("Entity.NoWorkbook", WorkbookPath);
            return;
        }
        var name = EntityName.Trim();
        if (name.Length == 0) name = SuggestName(book);
        try { main.AddEntity(new EntityProfile { Name = name, DatabasePath = book }); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (main.HasEntity) main.SelectedNav = main.Nav[0];
    }
}
