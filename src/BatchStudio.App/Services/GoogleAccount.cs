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

namespace BatchStudio.App.Services;

/// <summary>
/// The user's Google account: "Sign in with Google" (Settings) through the system browser, like every desktop app
/// (OAuth 2 for installed apps: loopback redirect + PKCE). The program never sees the password; it keeps the refresh
/// token, encrypted for this Windows user (DPAPI), in %AppData%\BATCH STUDIO\google.dat, and the profile photo as
/// avatar.png. Gmail access is read-only and limited to message headers (gmail.metadata: sender and subject, never the
/// body).
/// </summary>
public sealed partial class GoogleAccount : ObservableObject
{
    public static GoogleAccount Instance { get; } = new();

    public const string Scopes = "openid email profile https://www.googleapis.com/auth/gmail.metadata";
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed class Stored
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string Picture { get; set; } = "";
        /// <summary>The user allowed the Gmail box on Google's consent page (sender / subject of emails).</summary>
        public bool Gmail { get; set; } = true;
    }

    private Stored _data = new();
    private string? _accessToken;
    private DateTime _accessUntil;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private static string DataPath => Path.Combine(SettingsStore.Folder, "google.dat");
    private static string AvatarPath => Path.Combine(SettingsStore.Folder, "avatar.png");

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ImageSource? _avatar;
    /// <summary>First letter of the name, shown while there is no photo.</summary>
    [ObservableProperty] private string _initial = "";
    /// <summary>Last problem (sign-in cancelled, session expired ...), shown in Settings.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>The program's own client (built in); a client loaded in an older version is still honoured.</summary>
    public string ClientId => _data.ClientId.Trim().Length > 0 ? _data.ClientId : GoogleClient.Id;
    /// <summary>Gmail (sender / subject) was allowed at sign-in.</summary>
    public bool HasGmail => IsSignedIn && _data.Gmail;
    public string ClientSecret => _data.ClientId.Trim().Length > 0 ? _data.ClientSecret : GoogleClient.Secret;
    public bool IsConfigured => ClientId.Length > 0;

    /// <summary>Raised after sign-in / sign-out (the Gmail watcher starts or stops).</summary>
    public event Action? SignedInChanged;

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

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DEEPMATH.google.v1");

    private void Show()
    {
        IsSignedIn = _data.RefreshToken.Length > 0;
        Email = IsSignedIn ? _data.Email : "";
        Name = IsSignedIn ? _data.Name : "";
        Initial = Name.Trim().Length > 0 ? Name.Trim()[..1].ToUpperInvariant() : Email.Length > 0 ? Email[..1].ToUpperInvariant() : "";
        Avatar = IsSignedIn ? LoadAvatar() : null;
        OnPropertyChanged(nameof(HasGmail));
    }

    private static ImageSource? LoadAvatar()
    {
        try
        {
            if (!File.Exists(AvatarPath)) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(AvatarPath);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>The OAuth client of the user's Google Cloud project (Desktop app).</summary>
    public void SetClient(string id, string secret)
    {
        id = (id ?? "").Trim();
        secret = (secret ?? "").Trim();
        if (id == _data.ClientId && secret == _data.ClientSecret) return;
        _data.ClientId = id;
        _data.ClientSecret = secret;
        Save();
        OnPropertyChanged(nameof(ClientId));
        OnPropertyChanged(nameof(ClientSecret));
        OnPropertyChanged(nameof(IsConfigured));
    }

    /// <summary>Reads the client_secret_….json downloaded from Google Cloud (Credentials → OAuth client → Download JSON).</summary>
    public bool LoadClientFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var node = root.TryGetProperty("installed", out var inst) ? inst : root.TryGetProperty("web", out var web) ? web : root;
            var id = node.TryGetProperty("client_id", out var i) ? i.GetString() ?? "" : "";
            var secret = node.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : "";
            if (id.Length == 0) return false;
            SetClient(id, secret);
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ sign in / out
    /// <summary>Opens Google's sign-in page in the browser and waits (up to 5 minutes) for the user to allow the access.</summary>
    public async Task<bool> SignInAsync()
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
            string url = AuthUrl + "?" + Query(new()
            {
                ["client_id"] = ClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = Scopes,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["access_type"] = "offline",
                ["prompt"] = "consent select_account",
            });
            Shell.OpenUrl(url);

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
            if (token?.RefreshToken is not { Length: > 0 } refresh) { Status = Loc.F("Google.Failed", token?.Error ?? "token"); return false; }
            _data.RefreshToken = refresh;
            // Google's consent page shows Gmail as a separate box: without it only the name and photo are allowed
            _data.Gmail = token.Scope == null || token.Scope.Contains("gmail.metadata", StringComparison.Ordinal);
            _accessToken = token.AccessToken;
            _accessUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            await LoadProfileAsync();
            Save();
            Status = _data.Gmail ? "" : Loc.T("Google.NoGmail");
            Show();
            SignedInChanged?.Invoke();
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

    /// <summary>Signs out: Google forgets the access (revoke) and the token and photo are removed from this computer.</summary>
    public async Task SignOutAsync()
    {
        var refresh = _data.RefreshToken;
        _data.RefreshToken = "";
        _data.Email = _data.Name = _data.Picture = "";
        _accessToken = null;
        Save();
        try { File.Delete(AvatarPath); } catch { }
        Status = "";
        Show();
        SignedInChanged?.Invoke();
        if (refresh.Length > 0)
            try { await Http.PostAsync(RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh })); }
            catch { }
    }

    /// <summary>A valid access token (renewed with the refresh token when needed); null when signed out or the access was withdrawn.</summary>
    public async Task<string?> AccessTokenAsync(bool forceRenew = false)
    {
        if (!IsSignedIn) return null;
        await _tokenLock.WaitAsync();
        try
        {
            if (!forceRenew && _accessToken != null && DateTime.UtcNow < _accessUntil) return _accessToken;
            var token = await PostTokenAsync(new()
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = _data.RefreshToken,
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
                // the access was withdrawn (password change, removed in the Google account, 7-day test token ...)
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await SignOutAsync();
                    Status = Loc.T("Google.Expired");
                });
            }
            return null;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task LoadProfileAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        _data.Email = r.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        _data.Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        _data.Picture = r.TryGetProperty("picture", out var p) ? p.GetString() ?? "" : "";
        if (_data.Picture.Length > 0)
        {
            try
            {
                // a larger photo than the 96 px default
                var big = System.Text.RegularExpressions.Regex.Replace(_data.Picture, @"=s\d+(-c)?$", "=s256-c");
                var bytes = await Http.GetByteArrayAsync(big);
                Directory.CreateDirectory(SettingsStore.Folder);
                await File.WriteAllBytesAsync(AvatarPath, bytes);
            }
            catch { }
        }
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
        if (!_data.Gmail) return;
        _data.Gmail = false;
        Save();
        Status = Loc.T("Google.NoGmail");
        OnPropertyChanged(nameof(HasGmail));
    }

    /// <summary>Screenshot check only: shows a signed-in account without contacting Google (nothing is saved).</summary>
    internal void Demo(string name, string email, ImageSource? avatar)
    {
        IsSignedIn = true;
        Name = name;
        Email = email;
        Initial = name[..1];
        Avatar = avatar;
    }
}
