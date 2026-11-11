using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.Services;

/// <summary>One email in the Home banner: sender and subject only (the program cannot read the message itself).</summary>
public sealed class MailItem
{
    public required string Id { get; init; }
    public required string ThreadId { get; init; }
    public required string From { get; init; }
    public required string FromEmail { get; init; }
    public required string Subject { get; init; }
    public required DateTime Received { get; init; }
    public required string Account { get; init; }

    public string Url => $"https://mail.google.com/mail/u/{Uri.EscapeDataString(Account)}/#inbox/{ThreadId}";

    public string Time
    {
        get
        {
            var culture = LanguageService.CurrentFlow == FlowDirection.RightToLeft ? new CultureInfo("ar-EG") : new CultureInfo("en-US");
            return Received.Date == DateTime.Today ? Received.ToString("h:mm tt", culture) : Received.ToString("d MMM, h:mm tt", culture);
        }
    }

    public string Initial => From.Trim().Length > 0 ? From.Trim()[..1].ToUpperInvariant() : "?";
}

/// <summary>
/// Watches the Gmail inbox of the signed-in account (every 30 seconds while online): new messages appear in the Home
/// banner and as a Windows notification; a click opens the message in Gmail in the browser.
/// </summary>
public sealed partial class GmailWatcher : ObservableObject
{
    public static GmailWatcher Instance { get; } = new();

    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private const int Keep = 15;

    /// <summary>Newest first: the latest unread messages, then everything that arrives while the program runs.</summary>
    public ObservableCollection<MailItem> Items { get; } = [];

    [ObservableProperty] private int _index;
    [ObservableProperty] private int _unread;
    [ObservableProperty] private bool _isActive;
    /// <summary>The banner was closed with X (it comes back with the next email).</summary>
    [ObservableProperty] private bool _dismissed;

    public MailItem? Current => Index >= 0 && Index < Items.Count ? Items[Index] : null;
    public bool HasItems => Items.Count > 0;
    public bool HasMany => Items.Count > 1;
    public string Position => Items.Count > 1 ? $"{Index + 1} / {Items.Count}" : "";
    public bool ShowBanner => IsActive && !Dismissed;
    public string UnreadText => Unread > 0 ? Loc.F("Mail.Unread", Unread) : Loc.T("Mail.AllRead");

    /// <summary>New messages (not the ones already unread at start): the Home banner animates, a notification is shown.</summary>
    public event Action<IReadOnlyList<MailItem>>? Arrived;

    private ulong _historyId;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _seen = [];

    private GmailWatcher()
    {
        Items.CollectionChanged += (_, _) => Notify();
        GoogleAccount.Instance.SignedInChanged += () => { if (GoogleAccount.Instance.IsSignedIn) Start(); else Stop(); };
    }

    partial void OnIndexChanged(int value) => Notify();
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ShowBanner));
    partial void OnDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowBanner));
    partial void OnUnreadChanged(int value) => OnPropertyChanged(nameof(UnreadText));

    private void Notify()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasMany));
        OnPropertyChanged(nameof(Position));
    }

    // ------------------------------------------------------------------ start / stop
    public void Start()
    {
        Stop();
        if (!GoogleAccount.Instance.IsSignedIn) return;
        IsActive = true;
        Dismissed = false;
        _historyId = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        IsActive = false;
        Items.Clear();
        _seen.Clear();
        Unread = 0;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (NetworkStatus.Instance.IsOnline) await CheckAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch { /* offline, Google busy ...: next round */ }
            try { await Task.Delay(Every, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Checks now (after the connection comes back).</summary>
    public void CheckSoon()
    {
        if (_cts is { } cts && !cts.IsCancellationRequested) _ = Task.Run(async () => { try { await CheckAsync(cts.Token); } catch { } });
    }

    private readonly SemaphoreSlim _checking = new(1, 1);

    private async Task CheckAsync(CancellationToken ct)
    {
        if (!await _checking.WaitAsync(0, ct)) return;
        try
        {
            var account = GoogleAccount.Instance.Email;
            if (_historyId == 0)
            {
                // first round: where the inbox is now, and the latest unread messages (shown, but not announced)
                using var profile = await GetAsync("profile", ct);
                if (profile == null) return;
                _historyId = ulong.Parse(profile.RootElement.GetProperty("historyId").GetString() ?? "0", CultureInfo.InvariantCulture);
                var first = new List<MailItem>();
                using var list = await GetAsync("messages?labelIds=INBOX&labelIds=UNREAD&maxResults=5", ct);
                if (list != null && list.RootElement.TryGetProperty("messages", out var msgs))
                    foreach (var m in msgs.EnumerateArray())
                        if (await MessageAsync(m.GetProperty("id").GetString()!, account, ct) is { } item) first.Add(item);
                await UpdateUnreadAsync(ct);
                await OnUi(() =>
                {
                    foreach (var i in first.OrderByDescending(x => x.Received)) { if (_seen.Add(i.Id)) Items.Add(i); }
                    Index = 0;
                });
                return;
            }

            var added = new List<string>();
            string? page = null;
            ulong latest = _historyId;
            do
            {
                var url = $"history?startHistoryId={_historyId}&historyTypes=messageAdded&labelId=INBOX" + (page != null ? $"&pageToken={page}" : "");
                using var doc = await GetAsync(url, ct, allowNotFound: true);
                if (doc == null) { _historyId = 0; return; }      // history too old: start again
                var root = doc.RootElement;
                if (root.TryGetProperty("historyId", out var hid)) latest = ulong.Parse(hid.GetString() ?? "0", CultureInfo.InvariantCulture);
                if (root.TryGetProperty("history", out var history))
                    foreach (var h in history.EnumerateArray())
                        if (h.TryGetProperty("messagesAdded", out var ma))
                            foreach (var a in ma.EnumerateArray())
                            {
                                var msg = a.GetProperty("message");
                                var labels = msg.TryGetProperty("labelIds", out var l) ? l.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                                if (labels.Contains("INBOX") && labels.Contains("UNREAD") && !labels.Contains("SENT") && !labels.Contains("DRAFT"))
                                    added.Add(msg.GetProperty("id").GetString()!);
                            }
                page = root.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            } while (page != null);
            _historyId = latest;

            var fresh = new List<MailItem>();
            foreach (var id in added.Distinct())
                if (!_seen.Contains(id) && await MessageAsync(id, account, ct) is { } item) fresh.Add(item);
            await UpdateUnreadAsync(ct);
            if (fresh.Count == 0) return;
            await OnUi(() =>
            {
                foreach (var i in fresh.OrderBy(x => x.Received))
                    if (_seen.Add(i.Id)) Items.Insert(0, i);
                while (Items.Count > Keep) Items.RemoveAt(Items.Count - 1);
                Index = 0;
                Dismissed = false;
                Arrived?.Invoke(fresh);
            });
        }
        finally { _checking.Release(); }
    }

    private async Task UpdateUnreadAsync(CancellationToken ct)
    {
        using var label = await GetAsync("labels/INBOX", ct);
        if (label != null && label.RootElement.TryGetProperty("messagesUnread", out var u))
        {
            int n = u.GetInt32();
            await OnUi(() => Unread = n);
        }
    }

    private async Task<MailItem?> MessageAsync(string id, string account, CancellationToken ct)
    {
        using var doc = await GetAsync($"messages/{id}?format=metadata&metadataHeaders=Subject&metadataHeaders=From", ct, allowNotFound: true);
        if (doc == null) return null;
        var r = doc.RootElement;
        string subject = "", from = "";
        if (r.TryGetProperty("payload", out var p) && p.TryGetProperty("headers", out var hs))
            foreach (var h in hs.EnumerateArray())
            {
                var n = h.GetProperty("name").GetString();
                var v = h.GetProperty("value").GetString() ?? "";
                if (string.Equals(n, "Subject", StringComparison.OrdinalIgnoreCase)) subject = v;
                else if (string.Equals(n, "From", StringComparison.OrdinalIgnoreCase)) from = v;
            }
        var (name, email) = SplitFrom(from);
        long ms = r.TryGetProperty("internalDate", out var d) && long.TryParse(d.GetString(), out var x) ? x : 0;
        return new MailItem
        {
            Id = id,
            ThreadId = r.TryGetProperty("threadId", out var t) ? t.GetString() ?? id : id,
            From = name,
            FromEmail = email,
            Subject = subject.Trim().Length > 0 ? subject.Trim() : Loc.T("Mail.NoSubject"),
            Received = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now,
            Account = account,
        };
    }

    /// <summary>"Name &lt;mail@x.com&gt;" → (Name, mail@x.com); a bare address gives the address twice.</summary>
    public static (string Name, string Email) SplitFrom(string from)
    {
        from = from.Trim();
        int lt = from.LastIndexOf('<');
        if (lt >= 0 && from.EndsWith('>'))
        {
            var email = from[(lt + 1)..^1].Trim();
            var name = from[..lt].Trim().Trim('"').Trim();
            return (name.Length > 0 ? name : email, email);
        }
        return (from, from);
    }

    private static async Task<JsonDocument?> GetAsync(string path, CancellationToken ct, bool allowNotFound = false)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var token = await GoogleAccount.Instance.AccessTokenAsync(forceRenew: attempt > 0);
            if (token == null) return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await GoogleAccount.Http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) continue;
            if (res.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Gmail: HTTP {(int)res.StatusCode}");
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        return null;
    }

    private static Task OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) { a(); return Task.CompletedTask; }
        return d.InvokeAsync(a, DispatcherPriority.Normal).Task;
    }

    // ------------------------------------------------------------------ banner commands
    [RelayCommand]
    private void Next() { if (Items.Count > 0) Index = (Index + 1) % Items.Count; }

    [RelayCommand]
    private void Previous() { if (Items.Count > 0) Index = (Index - 1 + Items.Count) % Items.Count; }

    [RelayCommand]
    private void Dismiss() => Dismissed = true;

    /// <summary>Opens the message shown (or the inbox) in Gmail in the browser.</summary>
    [RelayCommand]
    private void Open(MailItem? item)
    {
        item ??= Current;
        var account = GoogleAccount.Instance.Email;
        Shell.OpenUrl(item?.Url ?? $"https://mail.google.com/mail/u/{Uri.EscapeDataString(account)}/#inbox");
    }

    /// <summary>Screenshot check only: sample messages (nothing is contacted).</summary>
    internal void Demo(string account)
    {
        IsActive = true;
        Dismissed = false;
        Items.Clear();
        var now = DateTime.Now;
        Items.Add(new MailItem { Id = "1", ThreadId = "1", From = "Shenzhen Logistics", FromEmail = "ops@example.com", Subject = "Shipment MAD-2026-114 has left the port — documents attached", Received = now.AddMinutes(-2), Account = account });
        Items.Add(new MailItem { Id = "2", ThreadId = "2", From = "شركة البسامي الدولية", FromEmail = "info@example.com", Subject = "تأكيد استلام الفاتورة رقم Mad-2000114", Received = now.AddMinutes(-26), Account = account });
        Items.Add(new MailItem { Id = "3", ThreadId = "3", From = "SAB", FromEmail = "noreply@example.com", Subject = "Your monthly statement is ready", Received = now.AddHours(-3), Account = account });
        Index = 0;
        Unread = 12;
    }
}
