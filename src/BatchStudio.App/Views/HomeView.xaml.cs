using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            GmailWatcher.Instance.Arrived += OnMailArrived;
        };
        Unloaded += (_, _) => { _clock.Stop(); Hook(null); GmailWatcher.Instance.Arrived -= OnMailArrived; };
        DataContextChanged += (_, e) => { if (IsLoaded) Hook(e.NewValue as HomeViewModel); };
    }

    private void Hook(HomeViewModel? vm)
    {
        if (_vm != null) _vm.TickerChanged -= OnTickerChanged;
        _vm = vm;
        if (_vm != null) _vm.TickerChanged += OnTickerChanged;
    }

    private void OnTickerChanged() { }

    // ---------------------------------------------------------------- lock-screen clock
    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = LanguageService.CurrentFlow == FlowDirection.RightToLeft ? new CultureInfo("ar-EG") : new CultureInfo("en-US");
        // 12-hour clock: 9:11 with AM / PM (ص / م in Arabic)
        ClockTime.Text = now.ToString("h:mm", CultureInfo.InvariantCulture);
        ClockAmPm.Text = now.ToString("tt", culture);
        ClockSeconds.Text = now.ToString("ss", CultureInfo.InvariantCulture);
        ClockDate.Text = now.ToString("dddd, d MMMM yyyy", culture);
    }

    private void OnParticleAreaMouseMove(object sender, System.Windows.Input.MouseEventArgs e) => Particles.SetPointer(e.GetPosition(Particles));

    private void OnParticleAreaMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => Particles.SetPointer(null);

    /// <summary>A new email: the banner rises into place and the envelope sends out two soft rings.</summary>
    private void OnMailArrived(IReadOnlyList<MailItem> items) => AnimateBanner();

    public void AnimateBanner()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BannerShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        MailBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
        var grow = new DoubleAnimation(1, 1.9, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = new RepeatBehavior(2), EasingFunction = ease };
        var fade = new DoubleAnimation(0.45, 0, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = new RepeatBehavior(2) };
        MailGlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        MailGlowScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
        MailGlow.BeginAnimation(OpacityProperty, fade);
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        LiveDot.BeginAnimation(OpacityProperty, pulse);
    }
}
