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
/// Calm animated background (Home page): Network, Constellation, Floating dots, Data stream, Waves, a glowing 3D sphere,
/// Math (floating numbers and equations), Live chart, Aurora, Geometry (rotating wireframe shapes) or Ledger (graph paper).
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

    // Math / Ledger / Geometry / Live chart / Aurora state
    private sealed class Glyph
    {
        public double X, Y, Vx, Vy, Rot, VRot, Depth, Age, Life;
        public FormattedText? Text;
    }
    private readonly List<Glyph> _glyphs = [];
    private sealed class Cell { public int Col, Row; public double Age, Life, Target; public bool Money; }
    private readonly List<Cell> _cells = [];
    private sealed class Shape3 { public double X, Y, Size, Ax, Ay, Az, Vx, Vy, VAx, VAy, Depth; public int Kind; }
    private readonly List<Shape3> _shapes = [];
    private double[] _bars = [], _barTarget = [];
    private readonly List<double> _line = [];
    private double _lineShift;
    private static readonly string[] MathTokens =
    [
        "∑", "∫", "π", "√", "Δ", "±", "÷", "×", "%", "$", "€", "∞", "≈", "≠", "θ", "λ", "σ", "μ",
        "a²+b²", "f(x)", "∑xᵢ", "√2", "πr²", "x̄", "e^x", "dy/dx", "n!", "log₁₀", "∫f(x)dx", "Δy/Δx",
        "1,250.00", "3.14159", "0.618", "15%", "VAT", "x=−b/2a", "7×8=56", "1+1=2", "2ⁿ", "√(x²+y²)",
    ];

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
        _glyphs.Clear();   // Math text carries its brush; respawn it in the new colour
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
            case "Math":
            {
                _glyphs.Clear();
                int n = (int)(Math.Clamp(w * h / 16000, 18, 70) * d);
                for (int i = 0; i < n; i++) _glyphs.Add(NewGlyph(w, h, randomAge: true));
                break;
            }
            case "Ledger":
                _cells.Clear();
                break;   // cells appear over time
            case "Geometry":
            {
                _shapes.Clear();
                int n = (int)(Math.Clamp(w * h / 90000, 4, 12) * d);
                for (int i = 0; i < n; i++)
                {
                    double depth = 0.3 + _rng.NextDouble() * 0.7;
                    _shapes.Add(new Shape3
                    {
                        X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h, Size = (28 + _rng.NextDouble() * 50) * (0.6 + depth),
                        Ax = _rng.NextDouble() * 6.28, Ay = _rng.NextDouble() * 6.28, Az = _rng.NextDouble() * 6.28,
                        Vx = (_rng.NextDouble() - 0.5) * 10, Vy = (_rng.NextDouble() - 0.5) * 10,
                        VAx = (_rng.NextDouble() - 0.5) * 0.6, VAy = 0.2 + _rng.NextDouble() * 0.5, Depth = depth, Kind = _rng.Next(4),
                    });
                }
                break;
            }
            case "LiveChart":
            {
                int n = (int)(Math.Clamp(w / 34, 12, 60) * Math.Clamp(d, 0.6, 1.6));
                _bars = new double[n];
                _barTarget = new double[n];
                for (int i = 0; i < n; i++) _bars[i] = _barTarget[i] = 0.15 + _rng.NextDouble() * 0.55;
                _line.Clear();
                double v = 0.5;
                for (int i = 0; i < 80; i++) { v = Math.Clamp(v + (_rng.NextDouble() - 0.48) * 0.12, 0.1, 0.9); _line.Add(v); }
                break;
            }
            case "Aurora":
                break;   // drawn from time only
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
        if (_style != S.Style || _ps.Count == 0 && !StatelessParticles(S.Style) || S.Style == "Math" && _glyphs.Count == 0
            || S.Style == "LiveChart" && _bars.Length == 0 || S.Style == "Geometry" && _shapes.Count == 0) Populate(force: true);
        switch (_style)
        {
            case "Sphere":
            case "Waves":
            case "Aurora":
                break;
            case "Math": StepMath(dt, w, h); break;
            case "Ledger": StepLedger(dt, w, h); break;
            case "Geometry": StepGeometry(dt, w, h); break;
            case "LiveChart": StepChart(dt); break;
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
            case "Math": DrawMath(dc); break;
            case "Ledger": DrawLedger(dc, w, h); break;
            case "Geometry": DrawGeometry(dc); break;
            case "LiveChart": DrawChart(dc, w, h); break;
            case "Aurora": DrawAurora(dc, w, h); break;
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

    private static bool StatelessParticles(string style) => style is "Waves" or "Math" or "Ledger" or "Geometry" or "LiveChart" or "Aurora";

    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    private static readonly Typeface MathFace = new(new FontFamily("Cambria Math, Segoe UI Symbol, Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // ---------------------------------------------------------------- Math: numbers and equations drifting in space
    private Glyph NewGlyph(double w, double h, bool randomAge)
    {
        double depth = Math.Pow(_rng.NextDouble(), 1.6);              // most far (small, faint), a few near
        var token = MathTokens[_rng.Next(MathTokens.Length)];
        var ft = new FormattedText(token, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MathFace,
                                   11 + depth * 26, _dots.Length > 0 ? _dots[15] : Brushes.White, Dip);
        double life = 9 + _rng.NextDouble() * 12;
        return new Glyph
        {
            X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h,
            Vx = (_rng.NextDouble() - 0.5) * (6 + depth * 14), Vy = -(3 + _rng.NextDouble() * 8) * (0.5 + depth),
            Rot = (_rng.NextDouble() - 0.5) * 30, VRot = (_rng.NextDouble() - 0.5) * 8,
            Depth = depth, Life = life, Age = randomAge ? _rng.NextDouble() * life : 0, Text = ft,
        };
    }

    private void StepMath(double dt, double w, double h)
    {
        for (int i = 0; i < _glyphs.Count; i++)
        {
            var g = _glyphs[i];
            g.Age += dt;
            g.X += (g.Vx + Math.Sin(_t * 0.5 + i) * 3) * dt;
            g.Y += g.Vy * dt;
            g.Rot += g.VRot * dt;
            if (_pointer is { } m)
            {
                double dx = g.X - m.X, dy = g.Y - m.Y, d2 = dx * dx + dy * dy;
                if (d2 < 120 * 120 && d2 > 1) { double k = (30 + 50 * g.Depth) * dt / Math.Sqrt(d2); g.X += dx * k; g.Y += dy * k; }
            }
            if (g.Age > g.Life || g.Y < -60 || g.X < -120 || g.X > w + 120) _glyphs[i] = NewGlyph(w, h, randomAge: false);
        }
    }

    private void DrawMath(DrawingContext dc)
    {
        foreach (var g in _glyphs)
        {
            if (g.Text == null) continue;
            double life = g.Age / g.Life, fade = Math.Min(1, Math.Min(life * 4, (1 - life) * 4));   // fade in / out
            double a = Math.Clamp(fade * (0.18 + 0.82 * g.Depth), 0, 1);
            if (a < 0.02) continue;
            dc.PushTransform(new RotateTransform(g.Rot, g.X, g.Y));
            dc.PushOpacity(a);
            dc.DrawText(g.Text, new Point(g.X - g.Text.Width / 2, g.Y - g.Text.Height / 2));
            dc.Pop();
            dc.Pop();
        }
    }

    // ---------------------------------------------------------------- Ledger: graph paper, numbers counting up in random cells
    private const double LedgerCell = 34;

    private void StepLedger(double dt, double w, double h)
    {
        int cols = Math.Max(1, (int)(w / (LedgerCell * 2.4))), rows = Math.Max(1, (int)(h / LedgerCell));
        int want = (int)(Math.Clamp(cols * rows / 9.0, 6, 40) * Math.Clamp(S.Density, 0.3, 2.5));
        if (_cells.Count < want && _rng.NextDouble() < 0.25)
            _cells.Add(new Cell { Col = _rng.Next(cols), Row = _rng.Next(rows), Life = 4 + _rng.NextDouble() * 5,
                                  Target = Math.Round(_rng.NextDouble() * (_rng.NextDouble() < 0.5 ? 9999 : 250000), 2), Money = _rng.NextDouble() < 0.6 });
        for (int i = _cells.Count - 1; i >= 0; i--)
        {
            _cells[i].Age += dt;
            if (_cells[i].Age > _cells[i].Life) _cells.RemoveAt(i);
        }
    }

    private void DrawLedger(DrawingContext dc, double w, double h)
    {
        // the paper: faint lines, a stronger one every 5
        int k = 0;
        for (double x = 0; x < w; x += LedgerCell, k++) dc.DrawLine(Line(k % 5 == 0 ? 0.35 : 0.14), new Point(x, 0), new Point(x, h));
        k = 0;
        for (double y = 0; y < h; y += LedgerCell, k++) dc.DrawLine(Line(k % 5 == 0 ? 0.35 : 0.14), new Point(0, y), new Point(w, y));
        if (_pointer is { } m)
        {
            double cx = Math.Floor(m.X / LedgerCell) * LedgerCell, cy = Math.Floor(m.Y / LedgerCell) * LedgerCell;
            dc.DrawRectangle(Dot(0.12), null, new Rect(cx, cy, LedgerCell, LedgerCell));
        }
        foreach (var c in _cells)
        {
            double t = c.Age / c.Life, count = Math.Min(1, t * 2.2);                // counts up, holds, fades
            double eased = 1 - Math.Pow(1 - count, 3);
            double v = c.Target * eased;
            string text = c.Money ? v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture) : ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            double a = Math.Clamp(Math.Min(t * 6, (1 - t) * 3), 0, 1) * 0.9;
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 12, Dot(a), Dip);
            double x = c.Col * LedgerCell * 2.4 + 6, y = c.Row * LedgerCell + (LedgerCell - ft.Height) / 2;
            dc.DrawText(ft, new Point(x, y));
        }
    }

    // ---------------------------------------------------------------- Geometry: transparent wireframe shapes turning slowly
    private static readonly (double X, double Y, double Z)[][] Verts =
    [
        [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)],   // cube
        [(0, 1.3, 0), (-1, -0.7, -1), (1, -0.7, -1), (1, -0.7, 1), (-1, -0.7, 1)],                              // pyramid
        [(0, 1.4, 0), (0, -1.4, 0), (1.4, 0, 0), (-1.4, 0, 0), (0, 0, 1.4), (0, 0, -1.4)],                      // octahedron
        [(1, 1, 1), (-1, -1, 1), (-1, 1, -1), (1, -1, -1)],                                                      // tetrahedron
    ];
    private static readonly (int A, int B)[][] Edges =
    [
        [(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7)],
        [(0, 1), (0, 2), (0, 3), (0, 4), (1, 2), (2, 3), (3, 4), (4, 1)],
        [(0, 2), (0, 3), (0, 4), (0, 5), (1, 2), (1, 3), (1, 4), (1, 5), (2, 4), (4, 3), (3, 5), (5, 2)],
        [(0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3)],
    ];

    private void StepGeometry(double dt, double w, double h)
    {
        foreach (var g in _shapes)
        {
            g.X += g.Vx * dt;
            g.Y += g.Vy * dt;
            g.Ax += g.VAx * dt;
            g.Ay += g.VAy * dt;
            if (_pointer is { } m)
            {
                double dx = m.X - g.X, dy = m.Y - g.Y, d2 = dx * dx + dy * dy;
                if (d2 < 220 * 220) { g.Ay += dx * 0.0006 * dt * 30; g.Ax += dy * 0.0006 * dt * 30; }   // turns toward the mouse
            }
            if (g.X < -g.Size) g.X = w + g.Size; else if (g.X > w + g.Size) g.X = -g.Size;
            if (g.Y < -g.Size) g.Y = h + g.Size; else if (g.Y > h + g.Size) g.Y = -g.Size;
        }
    }

    private void DrawGeometry(DrawingContext dc)
    {
        foreach (var g in _shapes.OrderBy(s => s.Depth))
        {
            var vs = Verts[g.Kind];
            var pts = new Point[vs.Length];
            var zs = new double[vs.Length];
            double ca = Math.Cos(g.Ax), sa = Math.Sin(g.Ax), cb = Math.Cos(g.Ay), sb = Math.Sin(g.Ay);
            for (int i = 0; i < vs.Length; i++)
            {
                var (x, y, z) = vs[i];
                double x1 = x * cb + z * sb, z1 = -x * sb + z * cb;
                double y2 = y * ca - z1 * sa, z2 = y * sa + z1 * ca;
                double persp = 4 / (4 - z2);
                pts[i] = new Point(g.X + x1 * g.Size * 0.5 * persp, g.Y + y2 * g.Size * 0.5 * persp);
                zs[i] = z2;
            }
            foreach (var (a, b) in Edges[g.Kind])
            {
                double near = ((zs[a] + zs[b]) / 2 + 1.4) / 2.8;                 // back edges fainter
                dc.DrawLine(Line(Math.Clamp((0.35 + 0.65 * near) * (0.4 + 0.6 * g.Depth), 0.08, 1)), pts[a], pts[b]);
            }
            foreach (var pt in pts) dc.DrawEllipse(Dot(0.25 + 0.5 * g.Depth), null, pt, 1.6, 1.6);
        }
    }

    // ---------------------------------------------------------------- Live chart: bars rising and falling, a line gliding over them
    private double _barClock;

    private void StepChart(double dt)
    {
        _barClock += dt;
        if (_barClock > 0.9)
        {
            _barClock = 0;
            for (int i = 0; i < _barTarget.Length; i++)
                if (_rng.NextDouble() < 0.35) _barTarget[i] = Math.Clamp(_barTarget[i] + (_rng.NextDouble() - 0.5) * 0.3, 0.08, 0.85);
        }
        for (int i = 0; i < _bars.Length; i++) _bars[i] += (_barTarget[i] - _bars[i]) * Math.Min(1, dt * 2.5);
        _lineShift += dt * 0.6;
        while (_lineShift >= 1)
        {
            _lineShift -= 1;
            double last = _line.Count > 0 ? _line[^1] : 0.5;
            _line.Add(Math.Clamp(last + (_rng.NextDouble() - 0.48) * 0.12, 0.1, 0.9));
            if (_line.Count > 120) _line.RemoveAt(0);
        }
    }

    private void DrawChart(DrawingContext dc, double w, double h)
    {
        int n = _bars.Length;
        if (n == 0) return;
        double area = h * 0.55, baseY = h - 6, slot = w / n, bw = slot * 0.56;
        int hot = _pointer is { } pm ? (int)(pm.X / slot) : -1;
        for (int i = 0; i < n; i++)
        {
            double bh = _bars[i] * area, x = i * slot + (slot - bw) / 2;
            dc.DrawRectangle(Dot(i == hot ? 0.38 : 0.14 + 0.1 * _bars[i]), null, new Rect(x, baseY - bh, bw, bh));
            dc.DrawRectangle(Dot(i == hot ? 0.9 : 0.5), null, new Rect(x, baseY - bh, bw, 2));   // bright cap
        }
        // the line: last 60 points across the width, scrolling left
        int pts = Math.Min(60, _line.Count);
        if (pts < 2) return;
        double step = w / (pts - 2);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < pts; i++)
            {
                double v = _line[_line.Count - pts + i];
                var p = new Point((i - _lineShift) * step, h * 0.12 + (1 - v) * h * 0.5);
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        var pen = new Pen(Dot(0.75), 1.6);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
        double lv = _line[^1];
        dc.DrawEllipse(Dot(1), null, new Point((pts - 1 - _lineShift) * step, h * 0.12 + (1 - lv) * h * 0.5), 3.2, 3.2);
    }

    // ---------------------------------------------------------------- Aurora: soft colour waves drifting slowly
    private void DrawAurora(DrawingContext dc, double w, double h)
    {
        var c = BaseColor();
        double o = Math.Clamp(S.Opacity, 0.05, 1);
        int bands = (int)Math.Round(3 * Math.Clamp(S.Density, 0.5, 2));
        for (int b = 0; b < bands; b++)
        {
            // each band: a wide curtain of light whose top edge waves; colours shift around the theme colour
            double hueShift = (b - bands / 2.0) * 0.18;
            var col = Shift(c, hueShift);
            var fill = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(140 * o), col.R, col.G, col.B), 0.35));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1));
            fill.Freeze();
            var geo = new StreamGeometry();
            double baseY = h * (0.18 + 0.22 * b / Math.Max(1, bands - 1)), thick = h * 0.42;
            using (var g = geo.Open())
            {
                bool first = true;
                for (double x = -20; x <= w + 20; x += 18)
                {
                    double y = baseY + Math.Sin(x * 0.006 + _t * 0.35 + b * 1.7) * h * 0.07 + Math.Sin(x * 0.017 - _t * 0.22 + b) * h * 0.03;
                    if (_pointer is { } m) y += 30 * Math.Exp(-Math.Pow((x - m.X) / 140, 2)) * Math.Sign(m.Y - y);
                    if (first) { g.BeginFigure(new Point(x, y), true, true); first = false; } else g.LineTo(new Point(x, y), true, true);
                }
                for (double x = w + 20; x >= -20; x -= 36)
                    g.LineTo(new Point(x, baseY + thick + Math.Sin(x * 0.004 + _t * 0.2 + b) * h * 0.05), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }
    }

    /// <summary>The theme colour turned a little around the colour wheel (Aurora's bands).</summary>
    private static Color Shift(Color c, double turn)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min, hue = 0, sat = 0;
        if (d > 1e-6)
        {
            sat = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            hue = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            hue /= 6;
        }
        hue = (hue + turn + 1) % 1;
        sat = Math.Max(sat, 0.45);
        double q = l < 0.5 ? l * (1 + sat) : l + sat - l * sat, p = 2 * l - q;
        double H(double t) { t = (t + 1) % 1; return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; }
        return Color.FromRgb((byte)(255 * H(hue + 1 / 3.0)), (byte)(255 * H(hue)), (byte)(255 * H(hue - 1 / 3.0)));
    }
}
