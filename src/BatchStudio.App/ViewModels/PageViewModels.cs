using System.Collections.ObjectModel;
using BatchStudio.App.Services;
using BatchStudio.Core.Data;
using BatchStudio.Core.Entities;
using BatchStudio.Core.Templates;
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

public sealed partial class HomeViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<StatTile> Tiles { get; } = [];
    public ObservableCollection<string> Missing { get; } = [];
    [ObservableProperty] private string _entityName = "";
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private bool _hasMissing;

    public void Refresh()
    {
        Tiles.Clear();
        Missing.Clear();
        var root = main.Root;
        if (root == null) return;
        EntityName = main.ActiveEntity?.Name ?? "";
        DatabasePath = root;
        foreach (var p in main.DataPages)
            Tiles.Add(new StatTile { Title = p.Title, Value = p.RowCount.ToString("N0"), Glyph = p.Icon });
        var tpl = TemplateRegistry.Scan(root);
        Tiles.Add(new StatTile
        {
            Title = Loc.T("Nav.Templates"),
            Value = $"{tpl.Count(t => t.Status == TemplateStatus.Linked)} / {tpl.Count}",
            Glyph = "",
        });
        foreach (var m in DatabaseLayout.Validate(root)) Missing.Add(m);
        HasMissing = Missing.Count > 0;
    }

    [RelayCommand]
    private void Repair()
    {
        if (main.ActiveEntity != null) main.OpenEntity(main.ActiveEntity);
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var e = main.ActiveEntity;
        if (e == null) return;
        var folder = Dialogs.PickFolder(Path.GetDirectoryName(e.DatabasePath));
        if (folder != null) main.ChangeEntityPath(e, folder);
    }

    [RelayCommand]
    private void OpenDatabase()
    {
        if (main.Root != null) Shell.ShowInFolder(main.Root);
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
        }
        catch (Exception ex)
        {
            Status = $"{Loc.T("Status.Error")}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddTemplate()
    {
        if (main.Root == null) return;
        var file = Dialogs.PickFile("Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm");
        if (file == null) return;
        try
        {
            var dst = TemplateRegistry.Import(main.Root, file);
            Refresh();
            Selected = Items.FirstOrDefault(i => string.Equals(i.Entry.Path, dst, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void OpenTemplate()
    {
        if (Selected != null) Shell.Open(Selected.Entry.Path);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (main.Root != null) Shell.ShowInFolder(DatabaseLayout.Templates(main.Root));
    }
}

// ====================================================================== Settings
public sealed partial class EntityRowViewModel : ObservableObject
{
    public required EntityProfile Entity { get; init; }
    [ObservableProperty] private string _name = "";
    public string DatabasePath => Entity.DatabasePath;
    public bool Exists => Directory.Exists(Entity.DatabasePath);
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<EntityRowViewModel> Entities { get; } = [];

    public void Refresh()
    {
        Entities.Clear();
        foreach (var e in _main.Settings.Entities) Entities.Add(new EntityRowViewModel { Entity = e, Name = e.Name });
    }

    [RelayCommand]
    private void ChangePath(EntityRowViewModel? row)
    {
        if (row == null) return;
        var start = Directory.Exists(row.Entity.DatabasePath) ? Path.GetDirectoryName(row.Entity.DatabasePath) : null;
        var folder = Dialogs.PickFolder(start);
        if (folder != null) _main.ChangeEntityPath(row.Entity, folder);
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

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _isArabic = main.Settings.Language == AppLanguage.Arabic;
        _isLight = main.Settings.Theme == AppTheme.Light;
        _autoSave = main.Settings.AutoSave;
        _keepBackups = main.Settings.KeepBackups;
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
public sealed partial class EntitySetupViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string _entityName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    private string _location = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    [ObservableProperty] private string _error = "";

    public string TargetPath =>
        string.IsNullOrWhiteSpace(EntityName) ? "" : Path.Combine(Location, $"{EntityName.Trim()} Database");

    public bool CanCancel => main.HasEntity;

    public void Reset()
    {
        EntityName = "";
        Error = "";
        OnPropertyChanged(nameof(CanCancel));
    }

    [RelayCommand]
    private void Browse()
    {
        var f = Dialogs.PickFolder(Location);
        if (f != null) Location = f;
    }

    [RelayCommand]
    private void Create()
    {
        Error = "";
        var name = EntityName.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Error = Loc.T("Setup.Name") + " ?";
            return;
        }
        try
        {
            var root = TargetPath;
            DatabaseLayout.EnsureStructure(root, name);
            main.AddEntity(new EntityProfile { Name = name, DatabasePath = root });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void OpenExisting()
    {
        Error = "";
        var root = Dialogs.PickFolder(Location);
        if (root == null) return;
        var info = DatabaseLayout.ReadEntityInfo(root);
        var name = info?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
            if (name.EndsWith(" Database", StringComparison.OrdinalIgnoreCase)) name = name[..^" Database".Length];
        }
        try { main.AddEntity(new EntityProfile { Name = name!, DatabasePath = root }); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (main.HasEntity) main.SelectedNav = main.Nav[0];
    }
}
