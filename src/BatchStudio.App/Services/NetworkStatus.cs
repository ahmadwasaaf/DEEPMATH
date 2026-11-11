using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Uwp.Notifications;

namespace BatchStudio.App.Services;

/// <summary>
/// The Home connection indicator: Wi-Fi or cable, and whether the internet is really reachable (a light request to
/// Google every 30 seconds and whenever Windows reports a network change).
/// </summary>
public sealed partial class NetworkStatus : ObservableObject
{
    public static NetworkStatus Instance { get; } = new();

    public enum Level { Online, NoInternet, Offline }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(Glyph), nameof(Tip), nameof(IsOffline), nameof(IsLimited))]
    private Level _state = Level.Online;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(Tip))]
    private bool _wired;

    public bool IsOnline => State == Level.Online;
    public bool IsOffline => State == Level.Offline;
    public bool IsLimited => State == Level.NoInternet;

    /// <summary>Segoe Fluent Icons: Wi-Fi or Ethernet.</summary>
    public string Glyph => Wired ? "" : "";

    public string Tip => State switch
    {
        Level.Online => Loc.T(Wired ? "Net.OnlineCable" : "Net.OnlineWifi"),
        Level.NoInternet => Loc.T("Net.NoInternet"),
        _ => Loc.T("Net.Offline"),
    };

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Tick += (_, _) => _ = CheckAsync();
        _timer.Start();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Later();
        NetworkChange.NetworkAddressChanged += (_, _) => Later();
        _ = CheckAsync();
    }

    private void Later() => Application.Current?.Dispatcher.BeginInvoke(async () =>
    {
        await Task.Delay(1500);   // the adapter needs a moment after a change
        await CheckAsync();
    });

    public async Task CheckAsync()
    {
        var was = State;
        Wired = ActiveIsWired();
        if (!NetworkInterface.GetIsNetworkAvailable()) State = Level.Offline;
        else
        {
            bool ok;
            try
            {
                using var res = await Probe.GetAsync("http://www.gstatic.com/generate_204");
                ok = (int)res.StatusCode is 204 or 200;
            }
            catch { ok = false; }
            State = ok ? Level.Online : Level.NoInternet;
        }
        // back online: the inbox is checked at once
        if (was != Level.Online && State == Level.Online) GmailWatcher.Instance.CheckSoon();
    }

    private static bool ActiveIsWired()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                            && n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();
            if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) return false;
            return up.Any(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet);
        }
        catch { return false; }
    }
}

/// <summary>Windows notifications for new emails; a click opens the message in Gmail.</summary>
public static class MailToasts
{
    private static bool _hooked;

    public static void Register()
    {
        if (_hooked) return;
        _hooked = true;
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue("url", out var url) && url.StartsWith("https://mail.google.com/", StringComparison.Ordinal))
                    Shell.OpenUrl(url);
            };
        }
        catch { }
    }

    public static void Show(IReadOnlyList<MailItem> items)
    {
        try
        {
            // several at once: one notification with the newest and the count
            var first = items.OrderByDescending(x => x.Received).First();
            var b = new ToastContentBuilder()
                .AddArgument("url", first.Url)
                .AddText(items.Count > 1 ? $"{first.From}  (+{items.Count - 1})" : first.From)
                .AddText(first.Subject)
                .AddAttributionText(first.Account);
            b.Show(t => { t.Group = "mail"; t.Tag = first.Id.Length > 16 ? first.Id[..16] : first.Id; });
        }
        catch { /* notifications switched off in Windows */ }
    }
}
