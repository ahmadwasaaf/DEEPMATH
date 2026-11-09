using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatchStudio.App.Controls;

/// <summary>
/// Calm "network" background: slow drifting points joined by faint lines when close, and to the mouse pointer.
/// Drawn at ~30 fps, only while visible; it never takes mouse input itself (the host passes the pointer in).
/// </summary>
public sealed class ParticleNetwork : FrameworkElement
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(ParticleNetwork),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ParticleNetwork)d).BuildPens()));

    /// <summary>Particle / line colour (white on the dark theme).</summary>
    public Brush Tint { get => (Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>Opacity of the dots (lines are fainter).</summary>
    public double DotOpacity { get; set; } = 0.45;
    public double LineOpacity { get; set; } = 0.16;
    public double LinkDistance { get; set; } = 130;

    private struct P { public double X, Y, Vx, Vy, R; }
    private readonly List<P> _ps = [];
    private readonly Random _rng = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private Brush _dot = Brushes.White;
    private Pen[] _pens = [];
    private Point? _pointer;
    private DateTime _last = DateTime.Now;

    public ParticleNetwork()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => { BuildPens(); _last = DateTime.Now; _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { _last = DateTime.Now; _timer.Start(); } else _timer.Stop(); };
        SizeChanged += (_, _) => Populate();
    }

    /// <summary>The host forwards the mouse position (relative to this element), or null when it leaves.</summary>
    public void SetPointer(Point? p) => _pointer = p;

    private System.Windows.Media.Color BaseColor() => Tint is SolidColorBrush s ? s.Color : Colors.White;

    private void BuildPens()
    {
        var c = BaseColor();
        _dot = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(255 * DotOpacity), c.R, c.G, c.B));
        _dot.Freeze();
        // 12 alpha steps for the lines (fading with distance)
        _pens = new Pen[12];
        for (int i = 0; i < _pens.Length; i++)
        {
            var a = (byte)(255 * LineOpacity * (i + 1) / _pens.Length);
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B));
            b.Freeze();
            var pen = new Pen(b, 0.8);
            pen.Freeze();
            _pens[i] = pen;
        }
    }

    private void Populate()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        int target = (int)Math.Clamp(w * h / 11000, 25, 110);
        while (_ps.Count < target)
            _ps.Add(new P
            {
                X = _rng.NextDouble() * w,
                Y = _rng.NextDouble() * h,
                Vx = (_rng.NextDouble() - 0.5) * 18,
                Vy = (_rng.NextDouble() - 0.5) * 18,
                R = 1.1 + _rng.NextDouble() * 1.3,
            });
        if (_ps.Count > target) _ps.RemoveRange(target, _ps.Count - target);
        for (int i = 0; i < _ps.Count; i++)
        {
            var p = _ps[i];
            p.X = Math.Clamp(p.X, 0, w);
            p.Y = Math.Clamp(p.Y, 0, h);
            _ps[i] = p;
        }
    }

    private void Step()
    {
        var now = DateTime.Now;
        double dt = Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        if (_ps.Count == 0) Populate();
        for (int i = 0; i < _ps.Count; i++)
        {
            var p = _ps[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            if (p.X < 0) { p.X = 0; p.Vx = Math.Abs(p.Vx); }
            else if (p.X > w) { p.X = w; p.Vx = -Math.Abs(p.Vx); }
            if (p.Y < 0) { p.Y = 0; p.Vy = Math.Abs(p.Vy); }
            else if (p.Y > h) { p.Y = h; p.Vy = -Math.Abs(p.Vy); }
            _ps[i] = p;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pens.Length == 0) BuildPens();
        double max = LinkDistance, max2 = max * max;
        int n = _ps.Count;
        for (int i = 0; i < n; i++)
        {
            var a = _ps[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = _ps[j];
                double dx = a.X - b.X, dy = a.Y - b.Y, d2 = dx * dx + dy * dy;
                if (d2 >= max2) continue;
                int k = (int)((1 - Math.Sqrt(d2) / max) * (_pens.Length - 1));
                dc.DrawLine(_pens[Math.Clamp(k, 0, _pens.Length - 1)], new Point(a.X, a.Y), new Point(b.X, b.Y));
            }
            if (_pointer is { } m)
            {
                double dx = a.X - m.X, dy = a.Y - m.Y, d2 = dx * dx + dy * dy, reach = max * 1.3;
                if (d2 < reach * reach)
                {
                    int k = (int)((1 - Math.Sqrt(d2) / reach) * (_pens.Length - 1));
                    dc.DrawLine(_pens[Math.Clamp(k, 0, _pens.Length - 1)], new Point(a.X, a.Y), m);
                }
            }
        }
        foreach (var p in _ps) dc.DrawEllipse(_dot, null, new Point(p.X, p.Y), p.R, p.R);
    }
}
