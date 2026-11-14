using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // On small screens (laptops, 1366x768…) open maximized instead of overflowing the screen.
        var area = SystemParameters.WorkArea;
        if (area.Width < Width + 40 || area.Height < Height + 40)
        {
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            WindowState = WindowState.Maximized;
        }
        // Esc closes the supplier / client details
        // mouse side buttons: back / forward between pages (like a browser)
        PreviewMouseUp += (_, e) =>
        {
            if (DataContext is not MainViewModel vm || vm.IsLocked) return;
            if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1) { vm.GoBack(); e.Handled = true; }
            else if (e.ChangedButton == System.Windows.Input.MouseButton.XButton2) { vm.GoForward(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (DataContext is MainViewModel { IsLocked: true }) return;
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MainViewModel { IsPartyDetailsOpen: true } vm)
            {
                vm.PartyDetails.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    // ---------------------------------------------------------------- logo menu and Secure mode lock screen
    private void OnLockMouseMove(object sender, MouseEventArgs e) => LockParticles.SetPointer(e.GetPosition(LockParticles));
    private void OnLockMouseLeave(object sender, MouseEventArgs e) => LockParticles.SetPointer(null);

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private void OnLockVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (LockScreen.IsVisible)
        {
            LockPassword.Clear();
            Tick(null, EventArgs.Empty);
            _clock.Tick -= Tick;
            _clock.Tick += Tick;
            _clock.Start();
            Dispatcher.BeginInvoke(() => { LockPassword.Focus(); Keyboard.Focus(LockPassword); }, DispatcherPriority.Input);
        }
        else _clock.Stop();
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var culture = LanguageService.CurrentFlow == FlowDirection.RightToLeft ? new CultureInfo("ar-EG") : new CultureInfo("en-US");
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        SecondsText.Text = now.ToString("ss", CultureInfo.InvariantCulture);
        DateText.Text = now.ToString("dddd, d MMMM yyyy", culture);
    }

    private void OnLockUnlock(object sender, RoutedEventArgs e) => TryUnlockScreen();

    private void TryUnlockScreen()
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.TryUnlockScreen(LockPassword.Password))
        {
            LockPassword.Clear();
            LockPassword.Focus();
            // a little shake on a wrong password
            var t = new TranslateTransform();
            LockPassword.RenderTransform = t;
            var a = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
            foreach (var (ms, x) in new[] { (0, 0.0), (60, -10.0), (120, 9.0), (180, -6.0), (240, 4.0), (360, 0.0) })
                a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(x, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            t.BeginAnimation(TranslateTransform.XProperty, a);
        }
        else LockPassword.Clear();
    }

    private void OnLockKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; TryUnlockScreen(); }
    }

    private void OnLockPasswordChanged(object sender, RoutedEventArgs e) =>
        LockHint.Visibility = LockPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnLockForgot(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm) await vm.ForgotScreenPasswordAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.OnClosing()) e.Cancel = true;
        base.OnClosing(e);
    }
}
