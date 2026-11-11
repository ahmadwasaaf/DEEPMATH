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

        // Alt+Enter / Shift+Enter: a new line inside the text fields, like Excel
        TextNewLine.Register();
        // hovering a template in any list shows its picture
        TemplatePreview.Register();
        // a click on an email notification opens the message in Gmail
        MailToasts.Register();

        var settings = SettingsStore.Load();
        ThemeService.Accent = string.IsNullOrWhiteSpace(settings.Accent) ? "auto" : settings.Accent;
        ThemeService.Apply(settings.Theme);
        LanguageService.Apply(settings.Language);

        var window = new MainWindow();
        MainWindow = window;
        AccentService.Apply(ThemeService.Accent, ThemeService.Current == AppTheme.Light);
        window.FlowDirection = LanguageService.CurrentFlow;
        var vm = new MainViewModel(settings);
        window.DataContext = vm;
        window.Show();

        // connection indicator and the Gmail inbox of the signed-in Google account
        NetworkStatus.Instance.Start();
        GmailWatcher.Instance.Arrived += items => { if (vm.Settings.MailToasts) MailToasts.Show(items); };
        if (GoogleAccount.Instance.IsSignedIn) GmailWatcher.Instance.Start();

        // --page <suppliers|clients|items|categories|templates|settings>  (used by the screenshot smoke test)
        var i = Array.IndexOf(e.Args, "--page");
        if (i >= 0 && i + 1 < e.Args.Length && vm.HasEntity)
        {
            var arg = e.Args[i + 1].ToLowerInvariant();
            // screenshot check of a custom accent colour (not saved)
            // like a user picking a colour on the open Settings page (controls already created)
            if (arg == "settings-accent")
            {
                arg = "settings";
                vm.Settings.AppearanceOpen = true;
                Dispatcher.BeginInvoke(() => ThemeService.ApplyAccent("#E3008C"), DispatcherPriority.ContextIdle);
            }
            // screenshot checks of the Google account, the email banner and the template preview (nothing is contacted or saved)
            bool accounts = arg == "home-accounts";
            if (accounts)
            {
                arg = "home-mail";
                Dispatcher.BeginInvoke(async () => { await Task.Delay(2500); vm.Home.AccountMenuOpen = true; }, DispatcherPriority.ContextIdle);
            }
            if (arg is "home-mail" or "settings-google")
            {
                GoogleAccount.Instance.Demo("Madar Trading", "madar.demo@gmail.com", null);
                GmailWatcher.Instance.Demo("madar.demo@gmail.com");
                bool google = arg == "settings-google";
                arg = arg == "home-mail" ? "home" : "settings";
                if (google)
                    Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(2000);
                        FindAll<FrameworkElement>(window).FirstOrDefault(x => x.Name == "GoogleCard")?.BringIntoView();
                    }, DispatcherPriority.ContextIdle);
            }
            // screenshot checks of the Home background styles: --page home-sphere, home-waves ... (not saved)
            if (arg.StartsWith("home-", StringComparison.Ordinal))
            {
                var style = BatchStudio.Core.Entities.BackgroundSettings.Styles.FirstOrDefault(x => x.Equals(arg[5..], StringComparison.OrdinalIgnoreCase));
                if (style != null) BatchStudio.App.Controls.BackgroundFx.Settings = new BatchStudio.Core.Entities.BackgroundSettings { Style = style };
                arg = "home";
            }
            // screenshot checks of this version: a wider name list, bank accounts, the colour picker (not saved)
            if (arg == "suppliers-wide") { vm.Settings.ListWidths["list.suppliers"] = 560; arg = "suppliers"; }
            bool banks = arg is "suppliers-banks" or "suppliers-search2";
            bool search2 = arg == "suppliers-search2";
            if (banks) arg = "suppliers";
            if (arg == "colorpicker")
            {
                arg = "settings";
                Dispatcher.BeginInvoke(() =>
                {
                    var w = new Views.ColorPickerWindow(System.Windows.Media.Color.FromRgb(0x00, 0x20, 0x60)) { Owner = window };
                    w.Show();
                }, DispatcherPriority.ContextIdle);
            }
            if (arg == "newinvoice-banks") ScreenshotBanks(vm);
            bool preview = arg == "newinvoice-preview";
            if (preview)
            {
                arg = "newinvoice";
                Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(2500);
                    var combo = FindAll<System.Windows.Controls.ComboBox>(window).FirstOrDefault(c => ReferenceEquals(c.ItemsSource, vm.NewInvoice.Templates));
                    if (combo == null) return;
                    combo.IsDropDownOpen = true;
                    await Task.Delay(800);
                    int index = Math.Min(1, combo.Items.Count - 1);
                    if (combo.ItemContainerGenerator.ContainerFromIndex(index) is System.Windows.Controls.ComboBoxItem item)
                        await TemplatePreview.ShowAsync(item);
                }, DispatcherPriority.ContextIdle);
            }
            bool massParty = arg == "mass-newparty";
            bool massDetails = arg == "mass-details";
            if (massParty || massDetails) arg = "mass";
            var key = arg is "newinvoice" or "newparty" or "newinvoice-details" or "newinvoice-banks" ? "Nav.NewInvoice" : arg == "reportview" ? "Nav.Reports" : arg == "mass" ? "Nav.Mass" : "Nav." + char.ToUpperInvariant(arg[0]) + arg[1..];
            var nav = vm.Nav.FirstOrDefault(n => n.Key == key);
            if (nav != null) vm.SelectedNav = nav;
            if (arg == "newparty") vm.NewInvoice.AddPartyCommand.Execute(null);
            if (arg == "reportview") vm.Reports.ViewReportCommand.Execute(null);
            if (arg == "mass") vm.Mass.FillSample();
            if (massParty) vm.Mass.AddPartyFor(vm.Mass.Rows[3]);
            if (massDetails) vm.Mass.OpenDetailsFor(vm.Mass.Rows[0]);
            if (arg == "mass" && !massParty && !massDetails) vm.Mass.SelectedRowIndex = 1;
            if (arg == "newinvoice-details")
            {
                vm.NewInvoice.SelectedParty = vm.NewInvoice.Parties.FirstOrDefault();
                vm.NewInvoice.DetailsExpanded = true;
            }
            if (arg == "newinvoice-banks")
            {
                vm.NewInvoice.Refresh();
                vm.NewInvoice.SelectedParty = vm.NewInvoice.Parties.FirstOrDefault(p => p.HasManyBanks) ?? vm.NewInvoice.Parties.FirstOrDefault();
                vm.NewInvoice.DetailsExpanded = true;
            }
            if (banks && vm.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers") is { } sp)
            {
                sp.SelectedRow = sp.View?.Cast<System.Data.DataRowView>().FirstOrDefault();
                if (sp.Fields.OfType<BankGroupViewModel>().FirstOrDefault() is { } g)
                {
                    g.AddCommand.Execute(null);
                    var added = g.Accounts.Last();
                    added.Value = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: SECOND DEMO BANK\nSWIFT BIC: DEMOCNSH\nBENEFICIARY ACCOUNT NO.: 6222 0000 1111";
                    added.IsDefault = true;
                }
                if (search2) sp.SearchText = "2";
            }
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var x in FindAll<T>(child)) yield return x;
        }
    }

    /// <summary>Screenshot check: the first supplier gets a second bank account (in memory only) printed by default.</summary>
    private static void ScreenshotBanks(MainViewModel vm)
    {
        var page = vm.DataPages.FirstOrDefault(p => p.Session.Definition.Key == "suppliers");
        if (page == null) return;
        var t = page.Session.Current.Clone();
        if (t.Rows.Count == 0) return;
        t.Headers.Add(BatchStudio.Core.Invoices.SupplierBanks.DefaultHeader);
        t.Headers.Add(BatchStudio.Core.Invoices.SupplierBanks.HeaderFor(2));
        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = t.Rows[r];
            Array.Resize(ref row, t.Headers.Count);
            for (int c = 0; c < row.Length; c++) row[c] ??= "";
            t.Rows[r] = row;
        }
        var first = t.Rows.OrderBy(x => x.Length > 1 ? x[1] : "", StringComparer.CurrentCultureIgnoreCase).First();
        first[^2] = "2";
        first[^1] = "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: SECOND DEMO BANK\nSWIFT BIC: DEMOCNSH\nBENEFICIARY ACCOUNT NO.: 6222 0000 1111";
        page.Session.SetCurrent(t);
        page.ShowCurrent();
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
