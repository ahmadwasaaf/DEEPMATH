using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatchStudio.App.Services;

/// <summary>
/// Ctrl+V (and Shift+Insert) paste in EVERY text field of the program - supplier / client details, banking paragraphs,
/// the editable lists, the Mass rows, settings ... - whatever the keyboard layout (Arabic / English) or the theme does
/// with the key. The clipboard text goes where the caret is (replacing the selection); a one-line field gets one line.
/// Undo (Ctrl+Z) works as usual.
/// </summary>
public static class TextPaste
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.IsReadOnly || !tb.IsEnabled) return;
        // the real key: an Arabic layout / IME may hand it over as ImeProcessed, Alt combinations as System
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key,
        };
        var mods = Keyboard.Modifiers;
        bool paste = (key == Key.V && mods == ModifierKeys.Control) || (key == Key.Insert && mods == ModifierKeys.Shift);
        if (!paste) return;
        var text = ClipboardText();
        if (text == null) return;                       // nothing to paste: leave the key to WPF
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!tb.AcceptsReturn && tb.TextWrapping == TextWrapping.NoWrap)
            text = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (tb.MaxLength > 0)
        {
            int room = tb.MaxLength - (tb.Text.Length - tb.SelectionLength);
            if (room <= 0) { e.Handled = true; return; }
            if (text.Length > room) text = text[..room];
        }
        int at = tb.SelectionStart;
        tb.SelectedText = text;                          // replaces the selection; Ctrl+Z undoes it
        tb.SelectionLength = 0;
        tb.CaretIndex = at + text.Length;
        e.Handled = true;
    }

    /// <summary>The clipboard's text (unicode first), retried a few times while another program holds the clipboard.</summary>
    private static string? ClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) return Clipboard.GetText(TextDataFormat.UnicodeText);
                if (Clipboard.ContainsText()) return Clipboard.GetText();
                return null;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
        }
        return null;
    }
}
