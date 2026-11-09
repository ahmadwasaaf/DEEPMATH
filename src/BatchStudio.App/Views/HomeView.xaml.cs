using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class HomeView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private HomeViewModel? _vm;

    public HomeView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) =>
        {
            UpdateClock();
            _clock.Start();
            Hook(DataContext as HomeViewModel);
            StartPulse();
            Dispatcher.BeginInvoke(RestartTicker, DispatcherPriority.Loaded);
        };
        Unloaded += (_, _) => { _clock.Stop(); Hook(null); };
        DataContextChanged += (_, e) => { if (IsLoaded) { Hook(e.NewValue as HomeViewModel); RestartTicker(); } };
    }

    private void Hook(HomeViewModel? vm)
    {
        if (_vm != null) _vm.TickerChanged -= OnTickerChanged;
        _vm = vm;
        if (_vm != null) _vm.TickerChanged += OnTickerChanged;
    }

    private void OnTickerChanged() => Dispatcher.BeginInvoke(RestartTicker, DispatcherPriority.Loaded);

    // ---------------------------------------------------------------- lock-screen clock
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = LanguageService.CurrentFlow == FlowDirection.RightToLeft ? new CultureInfo("ar-EG") : new CultureInfo("en-US");
        ClockTime.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockSeconds.Text = now.ToString("ss", CultureInfo.InvariantCulture);
        ClockDate.Text = now.ToString("dddd, d MMMM yyyy", culture);
    }

    // ---------------------------------------------------------------- news ticker
    private void OnTickerSizeChanged(object sender, SizeChangedEventArgs e) => RestartTicker();

    /// <summary>Scrolls the entries from the right edge to the left, forever, at a steady reading speed.</summary>
    private void RestartTicker()
    {
        TickerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        TickerStrip.UpdateLayout();
        TickerStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double content = TickerStrip.DesiredSize.Width;
        double view = TickerHost.ActualWidth;
        if (content <= 0 || view <= 0) return;
        const double pixelsPerSecond = 60;
        var anim = new DoubleAnimation(view, -content, TimeSpan.FromSeconds((view + content) / pixelsPerSecond))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        TickerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LiveDot.BeginAnimation(OpacityProperty, pulse);
    }
}
