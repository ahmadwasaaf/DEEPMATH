using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BatchStudio.App.Services;

/// <summary>
/// Type-to-filter for dropdown lists: typing in the box shows only the entries that contain the text.
/// The bound value changes only when an entry of the list is picked; anything else typed is undone.
/// Use with an editable ComboBox whose Text is bound with UpdateSourceTrigger=Explicit.
/// </summary>
public static class ComboSearch
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty BusyProperty =
        DependencyProperty.RegisterAttached("Busy", typeof(bool), typeof(ComboSearch), new PropertyMetadata(false));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box || e.NewValue is not true) return;
        box.IsEditable = true;
        box.IsTextSearchEnabled = false;
        box.StaysOpenOnEdit = true;
        box.Loaded += (_, _) => Hook(box);
        box.SelectionChanged += (_, _) => Commit(box);
        box.DropDownClosed += (_, _) => ClearFilter(box);
        box.LostKeyboardFocus += (_, ev) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            Finish(box);
        };
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Escape) { Revert(box); ev.Handled = false; }
            if (ev.Key == System.Windows.Input.Key.Enter) { PickFirst(box); Finish(box); }
        };
    }

    private static void Hook(ComboBox box)
    {
        if (box.Template?.FindName("PART_EditableTextBox", box) is not TextBox tb || tb.Tag as string == "hooked") return;
        tb.Tag = "hooked";
        tb.TextChanged += (_, _) =>
        {
            if ((bool)box.GetValue(BusyProperty) || !tb.IsKeyboardFocusWithin) return;
            var text = tb.Text ?? "";
            var view = CollectionViewSource.GetDefaultView(box.ItemsSource);
            if (view == null) return;
            if (Matches(box, text) != null) view.Filter = null;
            else view.Filter = o => (o?.ToString() ?? "").Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase);
            if (!box.IsDropDownOpen) box.IsDropDownOpen = true;
            tb.SelectionStart = tb.Text?.Length ?? 0;
        };
    }

    private static object? Matches(ComboBox box, string text)
    {
        if (box.ItemsSource == null) return null;
        foreach (var o in box.ItemsSource)
            if (string.Equals(o?.ToString(), text.Trim(), StringComparison.CurrentCultureIgnoreCase)) return o;
        return null;
    }

    private static void PickFirst(ComboBox box)
    {
        var view = CollectionViewSource.GetDefaultView(box.ItemsSource);
        if (view == null || Matches(box, box.Text ?? "") != null) return;
        var first = view.Cast<object>().FirstOrDefault();
        if (first != null) SetText(box, first.ToString() ?? "");
    }

    private static void Commit(ComboBox box)
    {
        if ((bool)box.GetValue(BusyProperty) || box.SelectedItem == null) return;
        SetText(box, box.SelectedItem.ToString() ?? "");
        box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    private static void Finish(ComboBox box)
    {
        var match = Matches(box, box.Text ?? "");
        if (match != null)
        {
            SetText(box, match.ToString() ?? "");
            box.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        }
        else Revert(box);
        ClearFilter(box);
    }

    private static void Revert(ComboBox box)
    {
        box.SetValue(BusyProperty, true);
        try { box.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget(); }
        finally { box.SetValue(BusyProperty, false); }
    }

    private static void SetText(ComboBox box, string text)
    {
        box.SetValue(BusyProperty, true);
        try { box.Text = text; }
        finally { box.SetValue(BusyProperty, false); }
    }

    private static void ClearFilter(ComboBox box)
    {
        if (CollectionViewSource.GetDefaultView(box.ItemsSource) is ICollectionView view && view.Filter != null) view.Filter = null;
    }
}
