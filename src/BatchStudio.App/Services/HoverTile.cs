using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace BatchStudio.App.Services;

/// <summary>
/// Home cards that feel alive: on hover the card lifts (up and a touch larger, a soft shadow), its accent ring lights
/// and a soft glow follows the mouse inside it; a press pushes it in. Needs, inside the card, an element named "Ring"
/// (the border that lights) and an element named "Glow" (on a Canvas, follows the mouse). <c>Lift="False"</c>: ring and
/// glow only (big cards).
/// </summary>
public static class HoverTile
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(HoverTile), new PropertyMetadata(false, OnEnabled));
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.RegisterAttached("Lift", typeof(bool), typeof(HoverTile), new PropertyMetadata(true));
    public static void SetLift(DependencyObject d, bool v) => d.SetValue(LiftProperty, v);
    public static bool GetLift(DependencyObject d) => (bool)d.GetValue(LiftProperty);

    private static readonly Duration Fast = new(TimeSpan.FromMilliseconds(180));
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        el.MouseEnter += (_, a) => Hover(el, true, a);
        el.MouseLeave += (_, a) => Hover(el, false, a);
        el.MouseMove += (_, a) => Follow(el, a);
        el.PreviewMouseLeftButtonDown += (_, _) => Press(el, true);
        el.PreviewMouseLeftButtonUp += (_, _) => Press(el, false);
    }

    private static (ScaleTransform S, TranslateTransform T) Transforms(FrameworkElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform s0 && g.Children[1] is TranslateTransform t0)
            return (s0, t0);
        var s = new ScaleTransform(1, 1);
        var t = new TranslateTransform();
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }

    private static void To(IAnimatable target, DependencyProperty p, double to) =>
        target.BeginAnimation(p, new DoubleAnimation(to, Fast) { EasingFunction = Ease });

    private static void Hover(FrameworkElement el, bool on, MouseEventArgs a)
    {
        if (GetLift(el))
        {
            var (s, t) = Transforms(el);
            To(s, ScaleTransform.ScaleXProperty, on ? 1.02 : 1);
            To(s, ScaleTransform.ScaleYProperty, on ? 1.02 : 1);
            To(t, TranslateTransform.YProperty, on ? -4 : 0);
            if (el.Effect is not DropShadowEffect fx)
                el.Effect = fx = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0, Color = Colors.Black };
            To(fx, DropShadowEffect.OpacityProperty, on ? 0.35 : 0);
        }
        if (Find(el, "Ring") is { } ring) To(ring, UIElement.OpacityProperty, on ? 1 : 0);
        if (Find(el, "Glow") is { } glow)
        {
            if (on && glow is System.Windows.Shapes.Shape sh)
            {
                var c = (el.TryFindResource("BrandBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x2F, 0x80, 0xED);
                sh.Fill = new RadialGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
                Follow(el, a);
            }
            To(glow, UIElement.OpacityProperty, on ? 1 : 0);
        }
    }

    private static void Follow(FrameworkElement el, MouseEventArgs a)
    {
        if (Find(el, "Glow") is not FrameworkElement glow) return;
        var p = a.GetPosition(el);
        System.Windows.Controls.Canvas.SetLeft(glow, p.X - glow.Width / 2);
        System.Windows.Controls.Canvas.SetTop(glow, p.Y - glow.Height / 2);
    }

    private static void Press(FrameworkElement el, bool down)
    {
        if (!GetLift(el)) return;
        var (s, _) = Transforms(el);
        double to = down ? 0.98 : el.IsMouseOver ? 1.02 : 1;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(90))));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(90))));
    }

    private static FrameworkElement? Find(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is FrameworkElement f && f.Name == name) return f;
            if (Find(c, name) is { } r) return r;
        }
        return null;
    }
}
