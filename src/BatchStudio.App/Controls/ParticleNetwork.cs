using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BatchStudio.Core.Entities;

namespace BatchStudio.App.Controls;

/// <summary>Shared Home-background settings; every background (Home page and the Settings preview) follows them live.</summary>
public static class BackgroundFx
{
    public static BackgroundSettings Settings { get; set; } = new();
    public static event Action? Changed;
    public static void Notify() => Changed?.Invoke();
}

/// <summary>
/// Calm animated background (Home page): Network, Constellation, Floating dots, Data stream, Waves or a glowing 3D sphere.
/// Drawn at ~30 fps only while visible; never takes mouse input itself (the host passes the pointer in).
/// </summary>
public sealed class ParticleNetwork : FrameworkElement
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(ParticleNetwork),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ParticleNetwork)d).Rebuild()));

    /// <summary>Theme colour used when the settings say "auto".</summary>
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    private struct P { public double X, Y, Z, Vx, Vy, R, Phase, Len; }
    private readonly List<P> _ps = [];
    private readonly List<P> _stars = [];
    private readonly Random _rng = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private Brush[] _dots = [];
    private Pen[] _pens = [];
    private Point? _pointer;
    private DateTime _last = DateTime.Now;
    private double _t;
    private string _style = "";
    private double _density = -1;

    private static BackgroundSettings S => BackgroundFx.Settings;

    public ParticleNetwork()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => { BackgroundFx.Changed += OnSettingsChanged; Rebuild(); Restart(); };
        Unloaded += (_, _) => { BackgroundFx.Changed -= OnSettingsChanged; _timer.Stop(); };
        IsVisibleChanged += (_, _) => Restart();
        SizeChanged += (_, _) => Populate(force: true);
    }

    private void OnSettingsChanged()
    {
        Rebuild();
        Populate(force: false);
        Restart();
        InvalidateVisual();
    }

    private void Restart()
    {
        _last = DateTime.Now;
        if (IsVisible && S.Enabled) _timer.Start(); else _timer.Stop();
        InvalidateVisual();
    }

    /// <summary>The host forwards the mouse position (relative to this element), or null when it leaves.</summary>
    public void SetPointer(Point? p) => _pointer = S.Mouse ? p : null;

    // ---------------------------------------------------------------- colours (16 alpha steps, frozen)
    private Color BaseColor()
    {
        if (!string.Equals(S.Color, "auto", StringComparison.OrdinalIgnoreCase))
        {
            try { return (Color)ColorConverter.ConvertFromString(S.Color); } catch { }
        }
        return Tint is SolidColorBrush s ? s.Color : Colors.White;
    }

    private void Rebuild()
    {
        var c = BaseColor();
        double o = Math.Clamp(S.Opacity, 0.05, 1);
        _dots = new Brush[16];
        _pens = new Pen[16];
        for (int i = 0; i < 16; i++)
        {
            double f = (i + 1) / 16.0;
            var b = new SolidColorBrush(Color.FromArgb((byte)(255 * o * f), c.R, c.G, c.B));
            b.Freeze();
            _dots[i] = b;
            var lb = new SolidColorBrush(Color.FromArgb((byte)(255 * o * 0.36 * f), c.R, c.G, c.B));
            lb.Freeze();
            var pen = new Pen(lb, 0.8);
            pen.Freeze();
            _pens[i] = pen;
        }
    }

    private Brush Dot(double f) => _dots[Math.Clamp((int)(f * 16) - 1, 0, 15)];
    private Pen Line(double f) => _pens[Math.Clamp((int)(f * 16) - 1, 0, 15)];

    // ---------------------------------------------------------------- particles
    private void Populate(bool force)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        if (!force && _style == S.Style && Math.Abs(_density - S.Density) < 0.001) return;
        _style = S.Style;
        _density = S.Density;
        _ps.Clear();
        _stars.Clear();
        double d = Math.Clamp(S.Density, 0.3, 2.5);
        switch (_style)
        {
            case "Sphere":
            {
                int n = (int)(320 * d);
                double golden = Math.PI * (3 - Math.Sqrt(5));
                for (int i = 0; i < n; i++)
                {
                    double y = 1 - 2.0 * (i + 0.5) / n, r = Math.Sqrt(1 - y * y), th = golden * i;
                    _ps.Add(new P { X = Math.Cos(th) * r, Y = y, Z = Math.Sin(th) * r, Phase = _rng.NextDouble() * 6.28 });
                }
                for (int i = 0; i < (int)(70 * d); i++)
                    _stars.Add(new P { X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h, R = 0.5 + _rng.NextDouble() * 0.9, Phase = _rng.NextDouble() * 6.28 });
                break;
            }
            case "Waves":
                break; // drawn from a grid, no state
            case "DataStream":
            {
                int n = (int)(Math.Clamp(w / 26, 12, 90) * d);
                for (int i = 0; i < n; i++)
                    _ps.Add(new P { X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h, Vy = 40 + _rng.NextDouble() * 90, Len = 25 + _rng.NextDouble() * 90, R = 1 + _rng.NextDouble() });
                break;
            }
            default:
            {
                int n = (int)(Math.Clamp(w * h / 11000, 25, 110) * d);
                bool floating = _style == "Floating", stars = _style == "Constellation";
                for (int i = 0; i < n; i++)
                    _ps.Add(new P
                    {
                        X = _rng.NextDouble() * w,
                        Y = _rng.NextDouble() * h,
                        Vx = (_rng.NextDouble() - 0.5) * (stars ? 5 : floating ? 6 : 18),
                        Vy = floating ? -(8 + _rng.NextDouble() * 22) : (_rng.NextDouble() - 0.5) * (stars ? 5 : 18),
                        R = floating ? 1 + _rng.NextDouble() * 3 : 1.1 + _rng.NextDouble() * 1.3,
                        Phase = _rng.NextDouble() * 6.28,
                    });
                break;
            }
        }
    }

    private void Step()
    {
        var now = DateTime.Now;
        double dt = Math.Min(0.1, (now - _last).TotalSeconds) * Math.Clamp(S.Speed, 0.1, 4);
        _last = now;
        _t += dt;
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        if (_style != S.Style || _ps.Count == 0 && S.Style != "Waves") Populate(force: true);
        switch (_style)
        {
            case "Sphere":
            case "Waves":
                break;
            case "DataStream":
                for (int i = 0; i < _ps.Count; i++)
                {
                    var p = _ps[i];
                    p.Y += p.Vy * dt;
                    if (p.Y - p.Len > h) { p.Y = -_rng.NextDouble() * 60; p.X = _rng.NextDouble() * w; }
                    _ps[i] = p;
                }
                break;
            case "Floating":
                for (int i = 0; i < _ps.Count; i++)
                {
                    var p = _ps[i];
                    p.X += (p.Vx + Math.Sin(_t * 0.8 + p.Phase) * 6) * dt;
                    p.Y += p.Vy * dt;
                    if (_pointer is { } m)
                    {
                        double dx = p.X - m.X, dy = p.Y - m.Y, d2 = dx * dx + dy * dy;
                        if (d2 < 90 * 90 && d2 > 1) { double k = 40 * dt / Math.Sqrt(d2); p.X += dx * k; p.Y += dy * k; }
                    }
                    if (p.Y < -10) { p.Y = h + 10; p.X = _rng.NextDouble() * w; }
                    if (p.X < -10) p.X = w + 10; else if (p.X > w + 10) p.X = -10;
                    _ps[i] = p;
                }
                break;
            default:
                for (int i = 0; i < _ps.Count; i++)
                {
                    var p = _ps[i];
                    p.X += p.Vx * dt;
                    p.Y += p.Vy * dt;
                    if (p.X < 0) { p.X = 0; p.Vx = Math.Abs(p.Vx); } else if (p.X > w) { p.X = w; p.Vx = -Math.Abs(p.Vx); }
                    if (p.Y < 0) { p.Y = 0; p.Vy = Math.Abs(p.Vy); } else if (p.Y > h) { p.Y = h; p.Vy = -Math.Abs(p.Vy); }
                    _ps[i] = p;
                }
                break;
        }
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- drawing
    protected override void OnRender(DrawingContext dc)
    {
        if (!S.Enabled) return;
        if (_dots.Length == 0) Rebuild();
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        switch (_style)
        {
            case "Sphere": DrawSphere(dc, w, h); break;
            case "Waves": DrawWaves(dc, w, h); break;
            case "DataStream": DrawStream(dc); break;
            case "Floating": foreach (var p in _ps) dc.DrawEllipse(Dot(0.35 + 0.5 * (0.5 + 0.5 * Math.Sin(_t + p.Phase))), null, new Point(p.X, p.Y), p.R, p.R); break;
            case "Constellation": DrawNetwork(dc, 0.75, twinkle: true); break;
            default: DrawNetwork(dc, 1, twinkle: false); break;
        }
    }

    private void DrawNetwork(DrawingContext dc, double reachFactor, bool twinkle)
    {
        double max = 130 * reachFactor, max2 = max * max;
        int n = _ps.Count;
        for (int i = 0; i < n; i++)
        {
            var a = _ps[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = _ps[j];
                double dx = a.X - b.X, dy = a.Y - b.Y, d2 = dx * dx + dy * dy;
                if (d2 >= max2) continue;
                dc.DrawLine(Line(1 - Math.Sqrt(d2) / max), new Point(a.X, a.Y), new Point(b.X, b.Y));
            }
            if (_pointer is { } m)
            {
                double dx = a.X - m.X, dy = a.Y - m.Y, d2 = dx * dx + dy * dy, reach = max * 1.3;
                if (d2 < reach * reach) dc.DrawLine(Line(1 - Math.Sqrt(d2) / reach), new Point(a.X, a.Y), m);
            }
        }
        foreach (var p in _ps)
        {
            double f = twinkle ? 0.25 + 0.75 * Math.Pow(0.5 + 0.5 * Math.Sin(_t * 1.6 + p.Phase), 2) : 1;
            double r = twinkle ? p.R * (0.7 + 0.5 * f) : p.R;
            dc.DrawEllipse(Dot(f), null, new Point(p.X, p.Y), r, r);
        }
    }

    private void DrawStream(DrawingContext dc)
    {
        foreach (var p in _ps)
        {
            // fading tail made of 4 segments, bright head
            for (int k = 0; k < 4; k++)
            {
                double y0 = p.Y - p.Len * (k + 1) / 4, y1 = p.Y - p.Len * k / 4;
                dc.DrawLine(Line(1 - k / 4.0), new Point(p.X, y0), new Point(p.X, y1));
            }
            dc.DrawEllipse(Dot(0.9), null, new Point(p.X, p.Y), p.R, p.R);
        }
    }

    private void DrawWaves(DrawingContext dc, double w, double h)
    {
        double gap = 22 / Math.Sqrt(Math.Clamp(S.Density, 0.3, 2.5));
        int rows = Math.Max(4, (int)(h / (gap * 2.2)));
        for (int r = 0; r < rows; r++)
        {
            double baseY = h * (r + 0.5) / rows, amp = 10 + 14 * Math.Sin(r * 0.9 + 1), phase = r * 0.7;
            double fRow = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(r * 1.3));
            for (double x = gap / 2; x < w; x += gap)
            {
                double y = baseY + amp * Math.Sin(x * 0.012 + _t * 0.9 + phase) + 6 * Math.Sin(x * 0.031 - _t * 0.6);
                if (_pointer is { } m)
                {
                    double dx = x - m.X, dy = y - m.Y, d2 = dx * dx + dy * dy;
                    if (d2 < 120 * 120) y += 14 * (1 - Math.Sqrt(d2) / 120) * Math.Sign(dy == 0 ? 1 : dy);
                }
                double f = fRow * (0.55 + 0.45 * Math.Sin(x * 0.02 + _t + r));
                dc.DrawEllipse(Dot(Math.Clamp(f, 0.1, 1)), null, new Point(x, y), 1.4, 1.4);
            }
        }
    }

    private void DrawSphere(DrawingContext dc, double w, double h)
    {
        foreach (var s in _stars)
        {
            double f = 0.15 + 0.35 * (0.5 + 0.5 * Math.Sin(_t * 1.2 + s.Phase));
            dc.DrawEllipse(Dot(f), null, new Point(s.X, s.Y), s.R, s.R);
        }
        double cx = w / 2, cy = h * 0.56, rad = Math.Min(w, h) * 0.36;
        double tiltX = 0.35, tiltY = 0;
        if (_pointer is { } m) { tiltX += (m.Y - cy) / h * 0.8; tiltY = (m.X - cx) / w * 0.8; }
        double ay = _t * 0.35 + tiltY, cyA = Math.Cos(ay), syA = Math.Sin(ay), cxA = Math.Cos(tiltX), sxA = Math.Sin(tiltX);
        const double cam = 3.2;
        foreach (var p in _ps)
        {
            // breathing surface + rotation (Y then X)
            double k = 1 + 0.04 * Math.Sin(_t * 1.3 + p.Phase);
            double x = p.X * k, y = p.Y * k, z = p.Z * k;
            double x1 = x * cyA + z * syA, z1 = -x * syA + z * cyA;
            double y2 = y * cxA - z1 * sxA, z2 = y * sxA + z1 * cxA;
            double s = cam / (cam - z2);
            var pt = new Point(cx + x1 * rad * s, cy + y2 * rad * s);
            double depth = (z2 + 1) / 2;   // 0 back … 1 front
            double r = 0.6 + 1.7 * depth;
            if (depth > 0.62) dc.DrawEllipse(Dot(0.18 * depth), null, pt, r * 3.2, r * 3.2);   // glow
            dc.DrawEllipse(Dot(0.15 + 0.85 * depth), null, pt, r, r);
        }
    }
}
