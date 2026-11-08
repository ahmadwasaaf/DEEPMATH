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
        // every change to the BATCH STUDIO workbook is written by Microsoft Excel itself
        BatchStudio.Core.Workbook.Workspace.EditorFactory = Services.ExcelWorkbookEditor.Open;

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
            var arg = e.Args[i + 1].ToLowerInvariant();
            var key = arg is "newinvoice" or "newparty" ? "Nav.NewInvoice" : "Nav." + char.ToUpperInvariant(arg[0]) + arg[1..];
            var nav = vm.Nav.FirstOrDefault(n => n.Key == key);
            if (nav != null) vm.SelectedNav = nav;
            if (arg == "newparty") vm.NewInvoice.AddPartyCommand.Execute(null);
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
