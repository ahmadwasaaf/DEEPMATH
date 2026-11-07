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
        window.DataContext = new MainViewModel(settings);
        window.Show();
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
