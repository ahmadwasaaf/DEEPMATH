using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class PartyFormView : UserControl
{
    public PartyFormView() => InitializeComponent();

    private void OnThemeLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }

    private void OnThemeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }
}
