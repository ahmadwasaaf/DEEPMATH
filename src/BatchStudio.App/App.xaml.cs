using System.Windows;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;
using BatchStudio.Core.Entities;

namespace BatchStudio.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var settings = SettingsStore.Load();
        ThemeService.Apply(settings.Theme);
        LanguageService.Apply(settings.Language);

        var window = new MainWindow();
        MainWindow = window;
        window.FlowDirection = LanguageService.CurrentFlow;
        var vm = new MainViewModel(settings);
        window.DataContext = vm;
        window.Show();

        // --page <suppliers|clients|items|categories|templates|settings>  (used by the screenshot smoke test)
        var i = Array.IndexOf(e.Args, "--page");
        if (i >= 0 && i + 1 < e.Args.Length && vm.HasEntity)
        {
            var key = "Nav." + char.ToUpperInvariant(e.Args[i + 1][0]) + e.Args[i + 1][1..].ToLowerInvariant();
            var nav = vm.Nav.FirstOrDefault(n => n.Key == key);
            if (nav != null) vm.SelectedNav = nav;
        }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var log = Path.Combine(SettingsStore.Folder, "error.log");
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "BATCH STUDIO", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
