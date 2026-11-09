using System.Windows.Controls;
using System.Windows.Input;

namespace BatchStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnPreviewMouseMove(object sender, MouseEventArgs e) => Preview.SetPointer(e.GetPosition(Preview));

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e) => Preview.SetPointer(null);
}
