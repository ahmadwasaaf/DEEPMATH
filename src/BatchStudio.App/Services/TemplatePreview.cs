using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchStudio.App.ViewModels;
using BatchStudio.Core.Templates;

namespace BatchStudio.App.Services;

/// <summary>
/// Hovering a template in any list of the program (New Invoice, DEFAULT TEMPLATE on the Suppliers / Clients pages, the
/// new / edit party form ...) shows a picture of that template beside the list, in the theme colour of the supplier
/// being worked on. The picture is drawn by the program from the template file (no Excel needed) and kept in memory
/// until the template file changes.
/// </summary>
public static class TemplatePreview
{
    public const double PreviewWidth = 340;
    private const double Scale = 2;          // drawn at twice the size: sharp on high-DPI screens

    private static readonly Dictionary<string, Task<TemplateSnapshot?>> Snapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapSource> Pictures = new(StringComparer.OrdinalIgnoreCase);
    private static List<TemplateEntry> _entries = [];
    private static DateTime _entriesAt;
    private static string? _entriesRoot;

    private static Popup? _popup;
    private static Image? _image;
    private static TextBlock? _title, _note;
    private static Border? _swatch;
    private static readonly DispatcherTimer ShowTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private static readonly DispatcherTimer HideTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private static ComboBoxItem? _pending;
    private static int _request;
    private static readonly HashSet<ComboBox> Hooked = [];

    /// <summary>Called once at start-up: every ComboBoxItem of the program gets the hover preview (template names only).</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), UIElement.MouseEnterEvent, new MouseEventHandler(OnEnter));
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), UIElement.MouseLeaveEvent, new MouseEventHandler(OnLeave));
        ShowTimer.Tick += (_, _) => { ShowTimer.Stop(); if (_pending != null) _ = ShowAsync(_pending); };
        HideTimer.Tick += (_, _) => { HideTimer.Stop(); Hide(); };
    }

    // ------------------------------------------------------------------ hover
    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is not ComboBoxItem item || TemplateName(item) is not { } name || Find(name) == null) return;
        HideTimer.Stop();
        _pending = item;
        ShowTimer.Stop();
        // moving from one template to the next: the picture follows at once
        if (_popup?.IsOpen == true) _ = ShowAsync(item);
        else ShowTimer.Start();
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(sender, _pending)) return;
        ShowTimer.Stop();
        _pending = null;
        HideTimer.Start();
    }

    public static void Hide()
    {
        _request++;
        if (_popup != null) _popup.IsOpen = false;
    }

    private static string? TemplateName(ComboBoxItem item)
    {
        var dc = item.DataContext ?? item.Content;
        if (dc is string s) return s.Trim();
        return dc?.GetType().GetProperty("Name")?.GetValue(dc) as string;
    }

    /// <summary>Shows the picture of a template beside a list item (also used by the screenshot check).</summary>
    public static async Task ShowAsync(ComboBoxItem item)
    {
        var name = TemplateName(item);
        if (name == null || Find(name) is not { } entry) return;
        int request = ++_request;
        var combo = ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
        var (supplier, color) = ThemeFor(combo, entry.Name);
        EnsurePopup();
        _title!.Text = entry.Name;
        _note!.Text = color != null && supplier.Length > 0 ? Loc.F("Preview.ThemeOf", supplier) : color != null ? Loc.T("Preview.Theme") : "";
        _swatch!.Visibility = color != null ? Visibility.Visible : Visibility.Collapsed;
        if (color is { } c) _swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
        var bmp = await PictureAsync(entry, color);
        if (request != _request) return;
        if (bmp == null) { Hide(); return; }
        _image!.Source = bmp;
        _popup!.PlacementTarget = item;
        _popup.FlowDirection = item.FlowDirection;
        if (combo != null && Hooked.Add(combo)) combo.DropDownClosed += (_, _) => Hide();
        _popup.IsOpen = false;   // re-place next to the new item
        _popup.IsOpen = true;
    }

    private static void EnsurePopup()
    {
        if (_popup != null) return;
        _image = new Image { Width = PreviewWidth, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var paper = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.White, Child = _image };
        _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _note = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var footer = new DockPanel { Margin = new Thickness(4, 10, 4, 0), LastChildFill = true };
        footer.Children.Add(_title);
        footer.Children.Add(_swatch);
        footer.Children.Add(_note);
        var stack = new StackPanel();
        stack.Children.Add(paper);
        stack.Children.Add(footer);
        var card = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        _popup = new Popup
        {
            Child = card,
            AllowsTransparency = true,
            Placement = PlacementMode.Right,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            IsHitTestVisible = false,
            HorizontalOffset = 4,
            VerticalOffset = -40,
        };
    }

    // ------------------------------------------------------------------ which template, which colour
    private static TemplateEntry? Find(string name)
    {
        var main = MainViewModel.Current;
        if (main?.Root is not { } root) return null;
        if (!string.Equals(root, _entriesRoot, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - _entriesAt > TimeSpan.FromSeconds(20))
        {
            try { _entries = TemplateRegistry.Scan(root).ToList(); }
            catch { _entries = []; }
            _entriesRoot = root;
            _entriesAt = DateTime.UtcNow;
        }
        return _entries.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The supplier being worked on near the list and the colour its theme gives this template (RGB 0xRRGGBB):
    /// the colour just picked in the Theme field (not saved yet) for the template in that field, else CLIENT THEME MEMORY.
    /// </summary>
    private static (string Supplier, uint? Color) ThemeFor(ComboBox? combo, string template)
    {
        var main = MainViewModel.Current;
        if (combo == null || main == null) return ("", null);
        string supplier = "";
        ThemeFieldViewModel? field = null;
        if (combo.DataContext is NewInvoiceViewModel nv)
        {
            if (nv.IsClients) return ("", null);
            supplier = nv.SelectedParty?.Name ?? "";
            field = nv.PartyTheme;
        }
        else
        {
            // details lists (Suppliers page, new / edit party form): the Theme field among the fields
            for (DependencyObject? d = VisualTreeHelper.GetParent(combo); d != null; d = VisualTreeHelper.GetParent(d))
                if (d is ItemsControl ic && ic.ItemsSource is IEnumerable items && items.OfType<ThemeFieldViewModel>().FirstOrDefault() is { } tf)
                {
                    field = tf;
                    supplier = tf.Supplier;
                    break;
                }
            if (field == null) return ("", null);
        }
        if (field != null && string.Equals(field.Template, template, StringComparison.OrdinalIgnoreCase)
            && BatchStudio.Core.Workbook.ThemeMemory.TryParseHex(field.Hex, out var picked))
            return (supplier, Rgb(picked));
        if (supplier.Length > 0 && main.ThemeFor(supplier, template) is { } saved) return (supplier, Rgb(saved));
        return (supplier, null);
    }

    /// <summary>Excel colour (0xBBGGRR) to 0xRRGGBB.</summary>
    private static uint Rgb(int excel) => (uint)(((excel & 0xFF) << 16) | (excel & 0xFF00) | ((excel >> 16) & 0xFF));

    // ------------------------------------------------------------------ the picture
    private static async Task<BitmapSource?> PictureAsync(TemplateEntry entry, uint? color)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(entry.Path); }
        catch { return null; }
        string fileKey = $"{entry.Path}|{stamp.Ticks}";
        string key = $"{fileKey}|{color?.ToString("X6", CultureInfo.InvariantCulture) ?? "-"}";
        if (Pictures.TryGetValue(key, out var done)) return done;
        if (!Snapshots.TryGetValue(fileKey, out var task))
        {
            string? sheet = entry.Map?.Sheet;
            string name = entry.Name;
            string path = entry.Path;
            Snapshots[fileKey] = task = Task.Run(() => TemplateSnapshot.Load(path, sheet, name));
        }
        var snap = await task;
        if (snap == null || snap.Width <= 0 || snap.Height <= 0) return null;
        var bmp = Render(snap, color);
        Pictures[key] = bmp;
        return bmp;
    }

    /// <summary>Draws a template: fills, text, borders and pictures; the template's theme colour becomes <paramref name="color"/>.</summary>
    public static BitmapSource Render(TemplateSnapshot s, uint? color, double width = PreviewWidth)
    {
        double k = width / s.Width;
        double[] xs = TemplateSnapshot.Starts(s.Columns), ys = TemplateSnapshot.Starts(s.Rows);
        uint? from = s.ThemeBase;
        uint Map(uint argb, int row) =>
            color is { } to && from is { } f && argb == f && !(s.KeepRow1 && row == 0) ? 0xFF000000u | to : argb;

        // cells with text: used to let a short text run over its empty neighbours, as in Excel
        var occupied = new HashSet<(int, int)>();
        foreach (var c in s.Cells)
            if (c.Text.Length > 0 || c.ColSpan > 1)
                for (int dc = 0; dc < c.ColSpan; dc++) occupied.Add((c.Row, c.Col + dc));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, s.Width * k, s.Height * k));
            dc.PushTransform(new ScaleTransform(k, k));
            foreach (var c in s.Cells)
                if (c.Fill is { } fill)
                    dc.DrawRectangle(Brush(Map(fill, c.Row)), null, Box(c));
            foreach (var c in s.Cells)
                if (c.Text.Length > 0) DrawText(dc, c);
            foreach (var c in s.Cells) DrawBorders(dc, c);
            foreach (var p in s.Pictures)
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = new MemoryStream(p.Data);
                    img.EndInit();
                    img.Freeze();
                    dc.DrawImage(img, new Rect(p.X, p.Y, Math.Max(1, p.Width), Math.Max(1, p.Height)));
                }
                catch { }
            }
            dc.Pop();
        }
        int pw = (int)Math.Ceiling(s.Width * k * Scale), ph = (int)Math.Ceiling(s.Height * k * Scale);
        var bmp = new RenderTargetBitmap(Math.Max(1, pw), Math.Max(1, ph), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;

        Rect Box(SnapCell c) => new(xs[c.Col], ys[c.Row], xs[c.Col + c.ColSpan] - xs[c.Col], ys[c.Row + c.RowSpan] - ys[c.Row]);

        void DrawText(DrawingContext dc, SnapCell c)
        {
            var box = Box(c);
            if (box.Width <= 0 || box.Height <= 0) return;
            double pad = 3 + c.Indent * 9;
            var tf = new Typeface(new FontFamily(c.FontName + ", Segoe UI"), c.Italic ? FontStyles.Italic : FontStyles.Normal,
                c.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText(c.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, c.FontSize * 96 / 72,
                Brush(Map(c.FontColor, c.Row)), Scale * k)
            {
                TextAlignment = c.Horizontal switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left },
                Trimming = TextTrimming.None,
            };
            // wrapped text stays in its cell; a single line keeps its natural width and may run over empty neighbours (Excel)
            bool wrap = c.Wrap || c.Text.Contains('\n');
            var clip = box;
            double x;
            if (wrap)
            {
                ft.MaxTextWidth = Math.Max(1, box.Width - 2 * pad);
                x = box.X + pad;
            }
            else
            {
                ft.TextAlignment = TextAlignment.Left;
                int first = c.Col, last = c.Col + c.ColSpan - 1;
                if (c.Horizontal != "Right") while (last + 1 < s.Columns.Length && !occupied.Contains((c.Row, last + 1))) last++;
                if (c.Horizontal != "Left") while (first - 1 >= 0 && !occupied.Contains((c.Row, first - 1))) first--;
                clip = new Rect(xs[first], box.Y, xs[last + 1] - xs[first], box.Height);
                x = c.Horizontal switch
                {
                    "Center" => box.X + (box.Width - ft.Width) / 2,
                    "Right" => box.Right - pad - ft.Width,
                    _ => box.X + pad,
                };
            }
            double y = c.Vertical switch
            {
                "Top" => box.Y + 1,
                "Center" => box.Y + (box.Height - ft.Height) / 2,
                _ => box.Bottom - ft.Height - 1,
            };
            dc.PushClip(new RectangleGeometry(clip));
            dc.DrawText(ft, new Point(x, y));
            dc.Pop();
        }

        void DrawBorders(DrawingContext dc, SnapCell c)
        {
            var b = Box(c);
            Line(c.Top, b.TopLeft, b.TopRight);
            Line(c.Bottom, b.BottomLeft, b.BottomRight);
            Line(c.Left, b.TopLeft, b.BottomLeft);
            Line(c.Right, b.TopRight, b.BottomRight);

            void Line(SnapEdge? e, Point a, Point z)
            {
                if (e == null) return;
                var pen = new Pen(Brush(e.Color), Math.Max(e.Width, 0.8 / k));
                pen.Freeze();
                dc.DrawLine(pen, a, z);
            }
        }
    }

    private static SolidColorBrush Brush(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}
