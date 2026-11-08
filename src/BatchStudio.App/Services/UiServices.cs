using System.Diagnostics;
using System.Windows;
using BatchStudio.Core.Entities;

namespace BatchStudio.App.Services;

/// <summary>Localized strings from the active Strings.xx.xaml dictionary.</summary>
public static class Loc
{
    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;
    public static string F(string key, params object[] args) => string.Format(T(key), args);
}

public static class LanguageService
{
    public static event Action? Changed;

    public static void Apply(AppLanguage lang)
    {
        var app = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Localization/Strings.") == true);
        var file = lang == AppLanguage.Arabic ? "Strings.ar.xaml" : "Strings.en.xaml";
        var fresh = new ResourceDictionary { Source = new Uri($"pack://application:,,,/BatchStudio;component/Localization/{file}") };
        if (old != null) dicts[dicts.IndexOf(old)] = fresh; else dicts.Add(fresh);
        var dir = lang == AppLanguage.Arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (Window w in app.Windows) w.FlowDirection = dir;
        CurrentFlow = dir;
        Changed?.Invoke();
    }

    public static FlowDirection CurrentFlow { get; private set; } = FlowDirection.LeftToRight;
}

public static class ThemeService
{
    public static void Apply(AppTheme theme)
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = theme == AppTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }
}

public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    public static void Info(string msg) =>
        MessageBox.Show(Owner!, msg, Loc.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string msg) =>
        MessageBox.Show(Owner!, msg, Loc.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string msg) =>
        MessageBox.Show(Owner!, msg, Loc.T("App.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static string? PickFolder(string? initial = null)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    public static string? PickFile(string filter, string? initial = null)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) d.InitialDirectory = initial;
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }
}
