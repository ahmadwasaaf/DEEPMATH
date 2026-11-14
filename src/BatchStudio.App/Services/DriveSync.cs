using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchStudio.Core.Entities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchStudio.App.Services;

/// <summary>One entity in Settings › Google Account › Auto Save Files: its toggle and its upload progress.</summary>
public sealed partial class DriveEntityViewModel : ObservableObject
{
    private readonly DriveSync _sync;
    public string Name { get; }
    public string Folder { get; set; }

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private double _progress;          // 0..100
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _detail = "";       // "120 MB of 260 MB · 140 MB left · Templates.xlsx"

    public DriveEntityViewModel(DriveSync sync, string name, string folder, bool on)
    {
        _sync = sync;
        Name = name;
        Folder = folder;
        _isOn = on;
        _state = Loc.T(on ? "Drive.On" : "Drive.Off");
    }

    partial void OnIsOnChanged(bool value) => _sync.Toggle(this, value);

    /// <summary>Upload: one upload now, by hand (with the toggle off nothing goes up on its own - a mistake in the files never
    /// replaces the good copy on Drive until the user decides).</summary>
    [RelayCommand]
    private Task Upload() => _sync.UploadNowAsync(this);
}

/// <summary>
/// Auto Save Files: each entity switched on has its whole data folder (the "... Database" folder: workbooks, templates, the
/// archive, the engine folder ...) copied to the user's Google Drive under the same names and folders, then kept up to date:
/// new and changed files are uploaded (a changed file becomes a new version of the same Drive file). Files deleted on the PC
/// stay on Drive (a backup). The engine's .venv and __pycache__ folders are skipped. Only files this program created are
/// visible to it (Google's drive.file access). What was uploaded is remembered in %AppData%\BATCH STUDIO\drive.json, so a
/// restart goes on where it stopped. Uploads run in the background, one entity after the other.
/// </summary>
public sealed partial class DriveSync : ObservableObject
{
    private static DriveSync? _instance;
    public static DriveSync Instance => _instance ??= new();

    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const long Chunk = 8L * 1024 * 1024;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".venv", "venv", "__pycache__", ".git" };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BATCH STUDIO", "drive.json");

    // ------------------------------------------------------------------ what is stored
    private sealed class FileEntry
    {
        public string Id { get; set; } = "";
        public long Size { get; set; }
        public long Ticks { get; set; }
    }
    private sealed class EntityState
    {
        public bool On { get; set; }
        public string Account { get; set; } = "";
        /// <summary>Per data folder (a new folder for the entity starts a new Drive folder): its Drive folder ids and files.</summary>
        public string Folder { get; set; } = "";
        public Dictionary<string, string> Dirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // "" = the root
        public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class Store
    {
        public Dictionary<string, EntityState> Entities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private Store _store = new();
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _timer;

    public ObservableCollection<DriveEntityViewModel> Entities { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    /// <summary>Shown at the bottom of the sidebar while files go up (empty when idle).</summary>
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private double _footerProgress;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _note = "";

    private DriveSync() => Load();

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // ------------------------------------------------------------------ storage
    private void Load()
    {
        try { if (File.Exists(StorePath)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) ?? new(); }
        catch { _store = new(); }
        _store.Entities = new(_store.Entities, StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _saveLock = new();
    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                var tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_store));
                File.Move(tmp, StorePath, true);
            }
            catch { }
        }
    }

    /// <summary>The data folder of an entity: its Database folder (the profile may hold the workbook's path itself).</summary>
    public static string DataFolder(EntityProfile e)
    {
        var p = (e.DatabasePath ?? "").Trim();
        return File.Exists(p) ? Path.GetDirectoryName(p) ?? p : p;
    }

    /// <summary>
    /// The entities of the program, with their toggles (called at start and whenever the settings are saved): new entities
    /// are added, removed ones dropped, a changed data folder is followed (and uploaded from the start).
    /// </summary>
    public void Refresh(IEnumerable<EntityProfile> entities)
    {
        var list = entities.Where(e => e.Name.Trim().Length > 0).ToList();
        foreach (var gone in Entities.Where(v => list.All(e => !string.Equals(e.Name, v.Name, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            Unwatch(gone.Name);
            Entities.Remove(gone);
        }
        var changed = new List<string>();
        foreach (var e in list)
        {
            var folder = DataFolder(e);
            var vm = Entities.FirstOrDefault(v => string.Equals(v.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                _store.Entities.TryGetValue(e.Name, out var st);
                Entities.Add(vm = new DriveEntityViewModel(this, e.Name, folder, st?.On ?? false));
                if (vm.IsOn) { Watch(vm); changed.Add(vm.Name); }
            }
            else if (!string.Equals(vm.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                vm.Folder = folder;
                if (vm.IsOn) { Watch(vm); changed.Add(vm.Name); }
            }
        }
        _timer ??= new System.Threading.Timer(_ => _ = SyncDirtyAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(3));
        if (changed.Count > 0) Kick(changed);
    }

    /// <summary>Google Drive was just allowed: everything switched on starts uploading.</summary>
    public void Resume()
    {
        Note = "";
        Kick(Entities.Where(x => x.IsOn).Select(x => x.Name));
    }

    internal void Toggle(DriveEntityViewModel vm, bool on)
    {
        if (!_store.Entities.TryGetValue(vm.Name, out var st)) _store.Entities[vm.Name] = st = new EntityState();
        st.On = on;
        Save();
        vm.State = Loc.T(on ? "Drive.On" : "Drive.Off");
        if (on)
        {
            Watch(vm);
            Kick([vm.Name]);
        }
        else
        {
            Unwatch(vm.Name);
            vm.IsUploading = false;
            vm.Detail = "";
        }
    }

    // ------------------------------------------------------------------ watching the folder (changes are uploaded a little later)
    private void Watch(DriveEntityViewModel vm)
    {
        Unwatch(vm.Name);
        if (!Directory.Exists(vm.Folder)) return;
        try
        {
            var w = new FileSystemWatcher(vm.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler mark = (_, e) => { if (!Skipped(e.FullPath, vm.Folder)) Mark(vm.Name); };
            w.Changed += mark;
            w.Created += mark;
            w.Renamed += (_, e) => { if (!Skipped(e.FullPath, vm.Folder)) Mark(vm.Name); };
            w.EnableRaisingEvents = true;
            _watchers[vm.Name] = w;
        }
        catch { /* the periodic pass still uploads */ }
    }

    private void Unwatch(string name)
    {
        if (_watchers.Remove(name, out var w)) { try { w.Dispose(); } catch { } }
    }

    private System.Threading.Timer? _debounce;
    private void Mark(string name)
    {
        lock (_dirty) _dirty.Add(name);
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => _ = SyncDirtyAsync(), null, TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
    }

    private void Kick(IEnumerable<string> names)
    {
        lock (_dirty) foreach (var n in names) _dirty.Add(n);
        _ = SyncDirtyAsync();
    }

    private async Task SyncDirtyAsync()
    {
        // the periodic pass also looks at every entity switched on (a missed watcher event, a folder that came back)
        List<string> names;
        lock (_dirty)
        {
            names = _dirty.ToList();
            _dirty.Clear();
        }
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui == null) return;
        var targets = await ui.InvokeAsync(() => Entities.Where(e => e.IsOn).ToList());
        if (names.Count == 0) names = targets.Select(t => t.Name).ToList();
        foreach (var vm in targets.Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
            await SyncAsync(vm);
    }

    private static bool Skipped(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Take(parts.Length - 1).Any(p => SkipDirs.Contains(p))) return true;
        var name = parts[^1];
        return name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || SkipDirs.Contains(name);
    }

    // ------------------------------------------------------------------ one pass over an entity's folder
    /// <summary>The Upload button: one pass now, whether the toggle is on or off.</summary>
    internal Task UploadNowAsync(DriveEntityViewModel vm) => SyncAsync(vm, manual: true);

    private async Task SyncAsync(DriveEntityViewModel vm, bool manual = false)
    {
        await _run.WaitAsync();
        var ui = System.Windows.Application.Current!.Dispatcher;
        void Ui(Action a) => ui.InvokeAsync(a);
        try
        {
            if (!vm.IsOn && !manual) return;
            var root = vm.Folder;
            if (!Directory.Exists(root)) { Ui(() => vm.State = Loc.T("Drive.NoFolder")); return; }
            var account = GoogleAccount.Instance;
            if (!account.HasDrive) { Ui(() => { vm.State = Loc.T("Drive.NeedAccess"); Note = Loc.T("Drive.NeedAccess"); }); return; }
            var token = await account.AccessTokenAsync();
            if (token == null) { Ui(() => vm.State = Loc.T("Drive.SignedOut")); return; }

            if (!_store.Entities.TryGetValue(vm.Name, out var st)) _store.Entities[vm.Name] = st = new EntityState { On = vm.IsOn };
            // a new data folder (or another Google account): a new Drive folder, uploaded from the start
            if (!string.Equals(st.Folder, root, StringComparison.OrdinalIgnoreCase) || !string.Equals(st.Account, account.Email, StringComparison.OrdinalIgnoreCase))
            {
                st.Folder = root;
                st.Account = account.Email;
                st.Dirs.Clear();
                st.Files.Clear();
                Save();
            }

            // what needs to go up: new files, and files whose size or time changed since the last upload
            var todo = new List<(string Rel, FileInfo Info)>();
            foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
            {
                if (Skipped(path, root)) continue;
                var fi = new FileInfo(path);
                var rel = Path.GetRelativePath(root, path);
                if (st.Files.TryGetValue(rel, out var known) && known.Size == fi.Length && known.Ticks == fi.LastWriteTimeUtc.Ticks) continue;
                todo.Add((rel, fi));
            }
            if (todo.Count == 0)
            {
                Ui(() => { vm.State = Loc.T("Drive.UpToDate"); vm.IsUploading = false; vm.Detail = ""; vm.Progress = 100; });
                return;
            }

            long total = todo.Sum(t => t.Info.Length), done = 0;
            Ui(() => { vm.IsUploading = true; vm.State = Loc.T("Drive.Uploading"); IsActive = true; });
            int failed = 0;
            foreach (var (rel, fi) in todo)
            {
                if (!vm.IsOn && !manual) break;
                var name = fi.Name;
                long before = done;
                void Report(long sent)
                {
                    long now = before + sent;
                    double pct = total == 0 ? 100 : now * 100.0 / total;
                    string detail = Loc.F("Drive.Detail", Size(now), Size(total), Size(Math.Max(0, total - now)), name);
                    Ui(() =>
                    {
                        vm.Progress = pct;
                        vm.Detail = detail;
                        Footer = Loc.F("Drive.Footer", vm.Name, (int)pct);
                        FooterProgress = pct;
                    });
                }
                try
                {
                    var parent = await EnsureDirAsync(st, root, Path.GetDirectoryName(rel) ?? "");
                    st.Files.TryGetValue(rel, out var known);
                    var id = await UploadAsync(fi, parent, known?.Id, Report);
                    st.Files[rel] = new FileEntry { Id = id, Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks };
                    Save();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failed++;
                    var msg = ex.Message;
                    Ui(() => Note = Loc.F("Drive.FileFailed", name, msg));
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } && failed >= 3) break;
                }
                done += fi.Length;
                Report(0);
            }
            Ui(() =>
            {
                vm.IsUploading = false;
                vm.Progress = 100;
                vm.Detail = "";
                vm.State = failed == 0 ? Loc.T("Drive.UpToDate") : Loc.F("Drive.SomeFailed", failed);
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            Ui(() => { vm.IsUploading = false; vm.State = Loc.F("Drive.Failed", msg); });
        }
        finally
        {
            Ui(() =>
            {
                if (Entities.All(e => !e.IsUploading)) { IsActive = false; Footer = ""; FooterProgress = 0; }
            });
            _run.Release();
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    // ------------------------------------------------------------------ Drive calls
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await GoogleAccount.Instance.AccessTokenAsync(attempt > 0) ?? throw new InvalidOperationException(Loc.T("Drive.SignedOut"));
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await Http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { res.Dispose(); continue; }
            if (((int)res.StatusCode == 429 || (int)res.StatusCode >= 500) && attempt < 4)
            {
                res.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt));
                continue;
            }
            return res;
        }
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException(DriveError(body, res.StatusCode), null, res.StatusCode);
        return JsonNode.Parse(body) ?? new JsonObject();
    }

    private static string DriveError(string body, HttpStatusCode code)
    {
        try
        {
            var msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
            if (msg != null && msg.Contains("has not been used", StringComparison.OrdinalIgnoreCase)) return Loc.T("Drive.ApiOff");
            if (msg != null) return msg;
        }
        catch { }
        return ((int)code).ToString();
    }

    /// <summary>The Drive folder for a folder of the data folder (created on the way; the root has the data folder's own name).</summary>
    private async Task<string> EnsureDirAsync(EntityState st, string root, string relDir)
    {
        if (st.Dirs.TryGetValue(relDir, out var id) && await ExistsAsync(id)) return id;
        string parentId;
        string name;
        if (relDir.Length == 0)
        {
            parentId = "root";
            name = new DirectoryInfo(root).Name;
        }
        else
        {
            parentId = await EnsureDirAsync(st, root, Path.GetDirectoryName(relDir) ?? "");
            name = Path.GetFileName(relDir);
        }
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = FolderMime, ["parents"] = new JsonArray(parentId) };
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Api + "?fields=id")
        { Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json") });
        id = (await JsonAsync(res))["id"]!.GetValue<string>();
        st.Dirs[relDir] = id;
        Save();
        return id;
    }

    private readonly Dictionary<string, bool> _seen = [];
    private async Task<bool> ExistsAsync(string id)
    {
        if (_seen.TryGetValue(id, out var ok)) return ok;
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/{id}?fields=id,trashed"));
        if (!res.IsSuccessStatusCode) return _seen[id] = false;
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        return _seen[id] = j?["trashed"]?.GetValue<bool>() != true;
    }

    /// <summary>
    /// Resumable upload: a new file in the folder, or a new version of the Drive file already made for it (gone from Drive:
    /// made again). Files open in Excel are read shared. Returns the Drive file id.
    /// </summary>
    private async Task<string> UploadAsync(FileInfo fi, string parentId, string? existingId, Action<long> progress)
    {
        if (existingId != null && !await ExistsAsync(existingId)) existingId = null;
        string session;
        if (existingId == null)
        {
            var meta = new JsonObject { ["name"] = fi.Name, ["parents"] = new JsonArray(parentId) };
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Upload + "?uploadType=resumable&fields=id")
            { Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json") });
            if (!res.IsSuccessStatusCode) await JsonAsync(res);
            session = res.Headers.Location?.ToString() ?? throw new InvalidOperationException("no upload session");
        }
        else
        {
            using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{Upload}/{existingId}?uploadType=resumable&fields=id")
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            if (!res.IsSuccessStatusCode) await JsonAsync(res);
            session = res.Headers.Location?.ToString() ?? throw new InvalidOperationException("no upload session");
        }

        await using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = fs.Length, sent = 0;
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(1, length))];
        while (true)
        {
            int n = length == 0 ? 0 : await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)));
            var content = new ByteArrayContent(buffer, 0, n);
            content.Headers.ContentLength = n;
            if (length > 0) content.Headers.ContentRange = new ContentRangeHeaderValue(sent, sent + n - 1, length);   // an empty file: no range
            var token = await GoogleAccount.Instance.AccessTokenAsync() ?? throw new InvalidOperationException(Loc.T("Drive.SignedOut"));
            using var req = new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req);
            sent += n;
            progress(sent);
            if ((int)res.StatusCode == 308) continue;          // more to send
            var j = await JsonAsync(res);
            return j["id"]?.GetValue<string>() ?? existingId ?? "";
        }
    }
}
