using System.ComponentModel;
using System.Windows;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.OnClosing()) e.Cancel = true;
        base.OnClosing(e);
    }
}
