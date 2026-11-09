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
        // invoices are written by Excel from the workbook's template sheet (shapes, heights, theme as in Excel)
        BatchStudio.Core.Invoices.InvoiceService.FileWriter = Services.ExcelInvoiceEngine.Fill;

        var settings = SettingsStore.Load();
        ThemeService.Accent = string.IsNullOrWhiteSpace(settings.Accent) ? "auto" : settings.Accent;
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
            // screenshot checks of the Home background styles: --page home-sphere, home-waves ... (not saved)
            if (arg.StartsWith("home-", StringComparison.Ordinal))
            {
                var style = BatchStudio.Core.Entities.BackgroundSettings.Styles.FirstOrDefault(x => x.Equals(arg[5..], StringComparison.OrdinalIgnoreCase));
                if (style != null) BatchStudio.App.Controls.BackgroundFx.Settings = new BatchStudio.Core.Entities.BackgroundSettings { Style = style };
                arg = "home";
            }
            var key = arg is "newinvoice" or "newparty" or "newinvoice-details" ? "Nav.NewInvoice" : arg == "reportview" ? "Nav.Reports" : arg == "mass" ? "Nav.Mass" : "Nav." + char.ToUpperInvariant(arg[0]) + arg[1..];
            var nav = vm.Nav.FirstOrDefault(n => n.Key == key);
            if (nav != null) vm.SelectedNav = nav;
            if (arg == "newparty") vm.NewInvoice.AddPartyCommand.Execute(null);
            if (arg == "reportview") vm.Reports.ViewReportCommand.Execute(null);
            if (arg == "mass") vm.Mass.FillSample();
            if (arg == "newinvoice-details")
            {
                vm.NewInvoice.SelectedParty = vm.NewInvoice.Parties.FirstOrDefault();
                vm.NewInvoice.DetailsExpanded = true;
            }
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
        MessageBox.Show(e.Exception.Message, "DEEPMATH", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
