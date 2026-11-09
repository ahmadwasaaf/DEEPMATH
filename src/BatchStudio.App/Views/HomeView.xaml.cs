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

    /// <summary>
    /// The indicators sit side by side; when they do not fit, the strip glides slowly to the end and back
    /// (a calm pan instead of fast scrolling text).
    /// </summary>
    private void RestartTicker()
    {
        TickerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        TickerShift.X = 0;
        Canvas.SetLeft(TickerStrip, 0);
        TickerStrip.Width = double.NaN;
        TickerStrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double content = TickerStrip.DesiredSize.Width;
        double view = TickerHost.ActualWidth;
        if (content <= 0 || view <= 0) return;
        // fits: the indicators share the whole width of the card
        if (content <= view)
        {
            TickerStrip.Width = view;
            return;
        }
        double travel = content - view;
        const double pixelsPerSecond = 25;
        var glide = TimeSpan.FromSeconds(travel / pixelsPerSecond);
        var pause = TimeSpan.FromSeconds(2.5);
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(pause + glide), new SineEase { EasingMode = EasingMode.EaseInOut }));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(pause + glide + pause)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(pause + glide + pause + glide), new SineEase { EasingMode = EasingMode.EaseInOut }));
        TickerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LiveDot.BeginAnimation(OpacityProperty, pulse);
    }
}
