using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatchStudio.App.Services;

/// <summary>
/// Alt+Enter (like Excel) and Shift+Enter add a new line in the wrapping text fields (supplier / client details,
/// address, banking ...). The line break is a single LF, the same as Alt+Enter inside an Excel cell.
/// </summary>
public static class TextNewLine
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.IsReadOnly || !tb.IsEnabled || tb.TextWrapping == TextWrapping.NoWrap) return;
        bool altEnter = e.Key == Key.System && e.SystemKey == Key.Enter;
        bool shiftEnter = e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift;
        if (!altEnter && !shiftEnter) return;
        int at = tb.SelectionStart;
        tb.SelectedText = "\n";
        tb.SelectionLength = 0;
        tb.CaretIndex = at + 1;
        e.Handled = true;
    }
}
