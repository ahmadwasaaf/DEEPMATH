using System.ComponentModel;
using System.Windows;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // On small screens (laptops, 1366x768…) open maximized instead of overflowing the screen.
        var area = SystemParameters.WorkArea;
        if (area.Width < Width + 40 || area.Height < Height + 40)
        {
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            WindowState = WindowState.Maximized;
        }
        // Esc closes the supplier / client details
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MainViewModel { IsPartyDetailsOpen: true } vm)
            {
                vm.PartyDetails.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.OnClosing()) e.Cancel = true;
        base.OnClosing(e);
    }
}
