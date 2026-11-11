using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BatchStudio.Core.Entities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.Services;

/// <summary>One Google account added to the program (Home account menu, Settings).</summary>
public sealed partial class GoogleAccountEntry : ObservableObject
{
    public required string Email { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ImageSource? _avatar;
    /// <summary>Signed in (a saved sign-in); false = "Signed out", kept in the list until the user removes it.</summary>
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isActive;

    public string Initial => (Name.Trim().Length > 0 ? Name.Trim() : Email)[..1].ToUpperInvariant();
    public string Title => Name.Trim().Length > 0 ? Name : Email;
}

/// <summary>
/// The user's Google accounts: "Sign in with Google" through the system browser, like every desktop app (OAuth 2 for
/// installed apps: loopback redirect + PKCE). The program never sees the password. Like Google's own account menu, every
/// account that signed in stays (switch with one click); Sign out keeps it in the list as "Signed out" and only Remove
/// forgets it. The refresh tokens are kept encrypted for this Windows user (DPAPI) in %AppData%\BATCH STUDIO\google.dat,
/// the profile photos in the avatars folder. Gmail access is read-only and limited to message headers (gmail.metadata:
/// sender and subject, never the body).
/// </summary>
public sealed partial class GoogleAccount : ObservableObject
{
    // created on first use: every static value below is ready by then (a field initialised before them read the
    // encryption key while it was still empty - the saved sign-in could not be read back at the next start)
    private static GoogleAccount? _instance;
    public static GoogleAccount Instance => _instance ??= new();

    public const string Scopes = "openid email profile https://www.googleapis.com/auth/gmail.metadata";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static byte[] Entropy => Encoding.UTF8.GetBytes("DEEPMATH.google.v1");

    private sealed class StoredAccount
    {
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string Picture { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        /// <summary>The user allowed the Gmail box on Google's consent page (sender / subject of emails).</summary>
        public bool Gmail { get; set; } = true;
    }

    private sealed class Stored
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public List<StoredAccount> Accounts { get; set; } = [];
        /// <summary>E-mail of the account in use.</summary>
        public string Active { get; set; } = "";
        // 0.11.x kept one account here (moved into Accounts when read)
        public string RefreshToken { get; set; } = "";
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string Picture { get; set; } = "";
        public bool Gmail { get; set; } = true;
    }

    private Stored _data = new();
    private string? _accessToken;
    private DateTime _accessUntil;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static string DataPath => Path.Combine(SettingsStore.Folder, "google.dat");
    private static string AvatarFolder => Path.Combine(SettingsStore.Folder, "avatars");

    private static string AvatarPath(string email) =>
        Path.Combine(AvatarFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..16] + ".png");

    /// <summary>Every account added (the one in use first).</summary>
    public System.Collections.ObjectModel.ObservableCollection<GoogleAccountEntry> Accounts { get; } = [];
    /// <summary>The other accounts (Home account menu).</summary>
    public System.Collections.ObjectModel.ObservableCollection<GoogleAccountEntry> Others { get; } = [];
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasOthers => Others.Count > 0;

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ImageSource? _avatar;
    /// <summary>First letter of the name, shown while there is no photo.</summary>
    [ObservableProperty] private string _initial = "";
    /// <summary>Last problem (sign-in cancelled, session expired ...), shown in Settings.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>The program's own client (built in); a client entered in Settings › OAuth client is used instead.</summary>
    public string ClientId => _data.ClientId.Trim().Length > 0 ? _data.ClientId : GoogleClient.Id;
    public string ClientSecret => _data.ClientId.Trim().Length > 0 ? _data.ClientSecret : GoogleClient.Secret;
    public bool IsConfigured => ClientId.Length > 0;
    /// <summary>Gmail (sender / subject) was allowed at sign-in for the account in use.</summary>
    public bool HasGmail => Current?.Gmail ?? false;

    /// <summary>Raised after sign-in, switch and sign-out (the Gmail watcher follows the account in use).</summary>
    public event Action? SignedInChanged;

    private StoredAccount? Current =>
        _data.Accounts.FirstOrDefault(a => a.RefreshToken.Length > 0 && string.Equals(a.Email, _data.Active, StringComparison.OrdinalIgnoreCase));

    private StoredAccount? Find(string email) =>
        _data.Accounts.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

    private GoogleAccount() => Load();

    // ------------------------------------------------------------------ storage (DPAPI, this Windows user only)
    private void Load()
    {
        try
        {
            if (File.Exists(DataPath))
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(DataPath), Entropy, DataProtectionScope.CurrentUser);
                _data = JsonSerializer.Deserialize<Stored>(bytes) ?? new Stored();
            }
        }
        catch { _data = new Stored(); }
        // the single account of 0.11.x becomes the first of the list (its photo moves with it)
        if (_data.RefreshToken.Length > 0 && _data.Email.Length > 0 && Find(_data.Email) == null)
        {
            _data.Accounts.Add(new StoredAccount
            { Email = _data.Email, Name = _data.Name, Picture = _data.Picture, RefreshToken = _data.RefreshToken, Gmail = _data.Gmail });
            _data.Active = _data.Email;
            try
            {
                var old = Path.Combine(SettingsStore.Folder, "avatar.png");
                if (File.Exists(old))
                {
                    Directory.CreateDirectory(AvatarFolder);
                    File.Move(old, AvatarPath(_data.Email), true);
                }
            }
            catch { }
            _data.RefreshToken = _data.Email = _data.Name = _data.Picture = "";
            Save();
        }
        Show();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_data), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(DataPath, bytes);
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private void Show()
    {
        var cur = Current;
        IsSignedIn = cur != null;
        Email = cur?.Email ?? "";
        Name = cur?.Name ?? "";
        Initial = Name.Trim().Length > 0 ? Name.Trim()[..1].ToUpperInvariant() : Email.Length > 0 ? Email[..1].ToUpperInvariant() : "";
        Avatar = cur != null ? LoadAvatar(cur.Email) : null;
        OnPropertyChanged(nameof(HasGmail));

        Accounts.Clear();
        Others.Clear();
        foreach (var a in _data.Accounts.OrderByDescending(a => ReferenceEquals(a, cur)))
        {
            var entry = new GoogleAccountEntry
            {
                Email = a.Email,
                Name = a.Name,
                Avatar = ReferenceEquals(a, cur) ? Avatar : LoadAvatar(a.Email),
                IsSignedIn = a.RefreshToken.Length > 0,
                IsActive = ReferenceEquals(a, cur),
            };
            Accounts.Add(entry);
            if (!entry.IsActive) Others.Add(entry);
        }
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasOthers));
    }

    private static ImageSource? LoadAvatar(string email)
    {
        try
        {
            var path = AvatarPath(email);
            if (!File.Exists(path)) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>Another OAuth client (Settings › OAuth client); empty = the client built into the program. True when it changed.</summary>
    public bool SetClient(string id, string secret)
    {
        id = (id ?? "").Trim();
        secret = (secret ?? "").Trim();
        // the built-in values typed back in are the built-in client
        if (id == GoogleClient.Id && (secret == GoogleClient.Secret || secret.Length == 0)) { id = ""; secret = ""; }
        if (id == _data.ClientId.Trim() && secret == _data.ClientSecret.Trim()) return false;
        _data.ClientId = id;
        _data.ClientSecret = secret;
        _accessToken = null;
        Save();
        OnPropertyChanged(nameof(ClientId));
        OnPropertyChanged(nameof(ClientSecret));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsCustomClient));
        return true;
    }

    public bool IsCustomClient => _data.ClientId.Trim().Length > 0;

    /// <summary>Another client: Google ties every sign-in to the client it was made with, so all accounts show "Signed out".</summary>
    public void ClientChanged()
    {
        bool had = Current != null;
        foreach (var a in _data.Accounts) a.RefreshToken = "";
        _data.Active = "";
        _accessToken = null;
        Save();
        Show();
        if (had) SignedInChanged?.Invoke();
    }

    /// <summary>Reads the client_secret_….json downloaded from Google Cloud (Clients → the client → Download JSON).</summary>
    public static (string Id, string Secret)? ReadClientFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var node = root.TryGetProperty("installed", out var inst) ? inst : root.TryGetProperty("web", out var web) ? web : root;
            var id = node.TryGetProperty("client_id", out var i) ? i.GetString() ?? "" : "";
            var secret = node.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : "";
            return id.Length == 0 ? null : (id, secret);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ accounts: add, switch, sign out, remove
    /// <summary>"Add another account" / first sign-in: Google's account chooser in the browser.</summary>
    [RelayCommand]
    private Task AddAccount() => SignInAsync();

    /// <summary>Uses another added account: at once when it is signed in, else Google's sign-in for that account.</summary>
    [RelayCommand]
    private async Task SwitchTo(GoogleAccountEntry? entry)
    {
        if (entry == null || entry.IsActive) return;
        var a = Find(entry.Email);
        if (a == null) return;
        if (a.RefreshToken.Length == 0) { await SignInAsync(a.Email); return; }
        _data.Active = a.Email;
        _accessToken = null;
        Save();
        Status = "";
        Show();
        SignedInChanged?.Invoke();
    }

    /// <summary>"Sign in" on a signed-out account of the list.</summary>
    [RelayCommand]
    private Task SignInAgain(GoogleAccountEntry? entry) => SignInAsync(entry?.Email);

    /// <summary>Forgets an account: Google forgets the access too, its photo is deleted.</summary>
    [RelayCommand]
    private async Task Remove(GoogleAccountEntry? entry)
    {
        if (entry == null || Find(entry.Email) is not { } a) return;
        if (!Dialogs.Confirm(Loc.F("Google.RemoveConfirm", a.Email))) return;
        var refresh = a.RefreshToken;
        bool wasActive = ReferenceEquals(a, Current);
        _data.Accounts.Remove(a);
        try { File.Delete(AvatarPath(a.Email)); } catch { }
        if (wasActive) UseNextSignedIn();
        Save();
        Show();
        if (wasActive) SignedInChanged?.Invoke();
        await RevokeAsync(refresh);
    }

    /// <summary>Signs the account in use out: it stays in the list ("Signed out"); the next signed-in account takes over.</summary>
    [RelayCommand]
    public async Task SignOutAsync()
    {
        if (Current is not { } a) return;
        var refresh = a.RefreshToken;
        a.RefreshToken = "";
        UseNextSignedIn();
        Save();
        Status = "";
        Show();
        SignedInChanged?.Invoke();
        await RevokeAsync(refresh);
    }

    private void UseNextSignedIn()
    {
        _accessToken = null;
        _data.Active = _data.Accounts.FirstOrDefault(x => x.RefreshToken.Length > 0)?.Email ?? "";
    }

    private static async Task RevokeAsync(string refresh)
    {
        if (refresh.Length == 0) return;
        try { await Http.PostAsync(RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh })); }
        catch { }
    }

    /// <summary>
    /// Opens Google's sign-in page in the browser and waits (up to 5 minutes) for the user to allow the access.
    /// <paramref name="hint"/>: the account to sign in again (Google skips the chooser).
    /// </summary>
    public async Task<bool> SignInAsync(string? hint = null)
    {
        if (!IsConfigured) { Status = Loc.T("Google.NeedClient"); return false; }
        if (IsBusy) return false;
        IsBusy = true;
        Status = Loc.T("Google.Waiting");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirect = $"http://127.0.0.1:{port}/";
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var query = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = Scopes,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["access_type"] = "offline",
                ["prompt"] = hint == null ? "consent select_account" : "consent",
            };
            if (hint != null) query["login_hint"] = hint;
            Shell.OpenUrl(AuthUrl + "?" + Query(query));

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string? code = null, error = null;
            while (code == null && error == null)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var (path, ok) = await AnswerAsync(client, timeout.Token);
                if (!ok) continue;
                var q = ParseQuery(path);
                if (!q.TryGetValue("state", out var st) || st != state) continue;     // favicon or another tab
                q.TryGetValue("code", out code);
                q.TryGetValue("error", out error);
                if (code == null && error == null) error = "no code";
            }
            if (code == null) { Status = Loc.F("Google.Failed", error ?? ""); return false; }

            var token = await PostTokenAsync(new()
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            });
            if (token?.RefreshToken is not { Length: > 0 } refresh || token.AccessToken is not { Length: > 0 } access)
            { Status = Loc.F("Google.Failed", token?.Error ?? "token"); return false; }
            var profile = await LoadProfileAsync(access);
            if (profile.Email.Length == 0) { Status = Loc.F("Google.Failed", "profile"); return false; }

            // the same account signed in again replaces its old sign-in; a new one is added to the list
            var a = Find(profile.Email);
            if (a == null) _data.Accounts.Add(a = new StoredAccount { Email = profile.Email });
            var oldRefresh = a.RefreshToken;
            a.Name = profile.Name;
            a.Picture = profile.Picture;
            a.RefreshToken = refresh;
            // Google's consent page shows Gmail as a separate box: without it only the name and photo are allowed
            a.Gmail = token.Scope == null || token.Scope.Contains("gmail.metadata", StringComparison.Ordinal);
            _data.Active = a.Email;
            _accessToken = access;
            _accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            Save();
            Status = a.Gmail ? "" : Loc.T("Google.NoGmail");
            Show();
            SignedInChanged?.Invoke();
            if (oldRefresh.Length > 0 && oldRefresh != refresh) await RevokeAsync(oldRefresh);
            return true;
        }
        catch (OperationCanceledException) { Status = Loc.T("Google.TimedOut"); return false; }
        catch (Exception ex) { Status = Loc.F("Google.Failed", ex.Message); return false; }
        finally
        {
            try { listener.Stop(); } catch { }
            IsBusy = false;
        }
    }

    /// <summary>A valid access token of the account in use (renewed when needed); null when signed out or the access was withdrawn.</summary>
    public async Task<string?> AccessTokenAsync(bool forceRenew = false)
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (Current is not { } a) return null;
            if (!forceRenew && _accessToken != null && DateTime.UtcNow < _accessUntil) return _accessToken;
            var token = await PostTokenAsync(new()
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = a.RefreshToken,
                ["grant_type"] = "refresh_token",
            });
            if (token?.AccessToken is { Length: > 0 } access)
            {
                _accessToken = access;
                _accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
                return access;
            }
            if (token?.Error == "invalid_grant")
            {
                // the access was withdrawn (password change, removed in the Google account ...): the account shows "Signed out"
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    a.RefreshToken = "";
                    UseNextSignedIn();
                    Save();
                    Show();
                    Status = Loc.F("Google.ExpiredFor", a.Email);
                    SignedInChanged?.Invoke();
                });
            }
            return null;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>Name, e-mail and photo of the account that just signed in (the photo is saved for it).</summary>
    private static async Task<(string Email, string Name, string Picture)> LoadProfileAsync(string access)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return ("", "", "");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        var email = r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var picture = r.TryGetProperty("picture", out var p) ? p.GetString() ?? "" : "";
        if (picture.Length > 0 && email.Length > 0)
        {
            try
            {
                // a larger photo than the 96 px default
                var big = System.Text.RegularExpressions.Regex.Replace(picture, @"=s\d+(-c)?$", "=s256-c");
                var bytes = await Http.GetByteArrayAsync(big);
                Directory.CreateDirectory(AvatarFolder);
                await File.WriteAllBytesAsync(AvatarPath(email), bytes);
            }
            catch { }
        }
        return (email, name, picture);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    private static async Task<TokenResponse?> PostTokenAsync(Dictionary<string, string> form)
    {
        using var res = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        try { return JsonSerializer.Deserialize<TokenResponse>(body); }
        catch { return new TokenResponse { Error = $"HTTP {(int)res.StatusCode}" }; }
    }

    // ------------------------------------------------------------------ the loopback page
    /// <summary>Reads the browser's request and answers with a small page ("you can close this tab").</summary>
    private static async Task<(string Path, bool Ok)> AnswerAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        int n = await stream.ReadAsync(buffer, ct);
        var request = Encoding.ASCII.GetString(buffer, 0, n);
        var first = request.Split("\r\n")[0].Split(' ');
        if (first.Length < 2) return ("", false);
        string path = first[1];
        bool ok = path.Contains("state=", StringComparison.Ordinal);
        bool error = path.Contains("error=", StringComparison.Ordinal);
        string html = ok ? Page(error) : "";
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            (ok ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 404 Not Found\r\n") +
            $"Content-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        return (path, ok);
    }

    private static string Page(bool error)
    {
        string title = error ? "Sign-in was cancelled" : "You're signed in";
        string text = error ? "You can close this tab and try again from DEEPMATH Settings."
                            : "DEEPMATH is now connected to your Google account. You can close this tab.";
        string color = error ? "#D13438" : "#2EA043";
        string mark = error ? "&#10005;" : "&#10003;";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>DEEPMATH</title>
            <style>
            body{margin:0;height:100vh;display:grid;place-items:center;background:#1c1c1c;color:#f3f3f3;font-family:'Segoe UI',system-ui,sans-serif}
            .card{background:#2b2b2b;border:1px solid #3a3a3a;border-radius:16px;padding:40px 48px;text-align:center;max-width:420px}
            .mark{width:64px;height:64px;border-radius:50%;margin:0 auto 18px;display:grid;place-items:center;font-size:30px;color:#fff;background:{{color}}}
            h1{font-size:22px;margin:0 0 10px;font-weight:600} p{margin:0;color:#b5b5b5;line-height:1.5}
            .brand{margin-top:26px;font-size:12px;letter-spacing:.12em;color:#8a8a8a}
            </style></head><body><div class="card"><div class="mark">{{mark}}</div><h1>{{title}}</h1><p>{{text}}</p>
            <div class="brand">DEEPMATH</div></div></body></html>
            """;
    }

    // ------------------------------------------------------------------ helpers
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Query(Dictionary<string, string> q) =>
        string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = path.IndexOf('?');
        if (i < 0) return d;
        foreach (var part in path[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    /// <summary>Gmail answered "not allowed": the box was not ticked (or the access was removed in the Google account).</summary>
    internal void GmailRefused()
    {
        if (Current is not { Gmail: true } a) return;
        a.Gmail = false;
        Save();
        Status = Loc.T("Google.NoGmail");
        OnPropertyChanged(nameof(HasGmail));
    }

    /// <summary>Screenshot check only: shows signed-in accounts without contacting Google (nothing is saved).</summary>
    internal void Demo(string name, string email, ImageSource? avatar)
    {
        IsSignedIn = true;
        Name = name;
        Email = email;
        Initial = name[..1];
        Avatar = avatar;
        Accounts.Clear();
        Others.Clear();
        Accounts.Add(new GoogleAccountEntry { Email = email, Name = name, IsSignedIn = true, IsActive = true });
        var second = new GoogleAccountEntry { Email = "sales.demo@gmail.com", Name = "Madar Sales", IsSignedIn = true };
        var third = new GoogleAccountEntry { Email = "old.demo@gmail.com", Name = "Old Mailbox", IsSignedIn = false };
        foreach (var x in new[] { second, third }) { Accounts.Add(x); Others.Add(x); }
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasOthers));
    }
}
