using System.Diagnostics;
using System.Text;
using BatchStudio.Core.Analysis;

namespace BatchStudio.App.Services;

/// <summary>
/// Runs the extraction engine (deepmath_analyze.py with the engine's own Python) on a folder of documents.
/// Every finished document comes back as one JSON line; the progress line "PROGRESS i/n name" on stderr.
/// </summary>
public static class AnalysisRunner
{
    public const string Script = "deepmath_analyze.py";

    /// <summary>The engine's Python: .venv\Scripts\python.exe in the engine folder (else venv\, else python on the PATH).</summary>
    public static string? PythonFor(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        foreach (var rel in new[] { @".venv\Scripts\python.exe", @"venv\Scripts\python.exe", @"python\python.exe" })
        {
            var p = Path.Combine(folder, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool HasScript(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, Script));

    /// <summary>Common places of the engine, for a first guess when the setting is empty.</summary>
    public static string? Detect(string? root)
    {
        var bases = new List<string>();
        if (!string.IsNullOrEmpty(root))
        {
            bases.Add(root);
            if (Path.GetDirectoryName(root) is { } up) bases.Add(up);
        }
        foreach (var b in bases.Where(Directory.Exists))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(b))
                    if (HasScript(d) && PythonFor(d) != null) return d;
            }
            catch { }
        }
        return null;
    }

    private static ProcessStartInfo Start(string folder, string python, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(Path.Combine(folder, Script));
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    /// <summary>Checks the engine folder: returns its version line, or throws with the reason.</summary>
    public static async Task<string> TestAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new InvalidOperationException(Loc.T("Analysis.ErrFolder"));
        if (!HasScript(folder)) throw new InvalidOperationException(Loc.F("Analysis.ErrScript", Script));
        var python = PythonFor(folder) ?? throw new InvalidOperationException(Loc.T("Analysis.ErrPython"));
        using var p = Process.Start(Start(folder, python, ["--version"]))!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new InvalidOperationException(Loc.T("Analysis.ErrTimeout")); }
        var output = (await outTask).Trim();
        if (p.ExitCode != 0 || output.Length == 0)
        {
            var err = (await errTask).Trim().Split('\n').LastOrDefault()?.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"exit code {p.ExitCode}" : err);
        }
        return output.Split('\n')[0].Trim();
    }

    /// <summary>
    /// Analyses every document of <paramref name="documents"/>. <paramref name="onResult"/> gets each document as it
    /// finishes, <paramref name="onProgress"/> (done, total, file name). Cancelling stops the engine.
    /// </summary>
    public static async Task<List<AnalysisResult>> RunAsync(string folder, string documents,
        Action<AnalysisResult>? onResult, Action<int, int, string>? onProgress, CancellationToken token)
    {
        var python = PythonFor(folder) ?? throw new InvalidOperationException(Loc.T("Analysis.ErrPython"));
        if (!HasScript(folder)) throw new InvalidOperationException(Loc.F("Analysis.ErrScript", Script));
        var results = new List<AnalysisResult>();
        var lastErr = new StringBuilder();
        using var p = new Process { StartInfo = Start(folder, python, ["--folder", documents]) };
        p.Start();
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("PROGRESS ", StringComparison.Ordinal))
                {
                    var rest = line[9..];
                    int sp = rest.IndexOf(' ');
                    var frac = sp > 0 ? rest[..sp] : rest;
                    var parts = frac.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var d) && int.TryParse(parts[1], out var n))
                        onProgress?.Invoke(d, n, sp > 0 ? rest[(sp + 1)..] : "");
                }
                else if (line.Trim().Length > 0)
                {
                    lock (lastErr) { lastErr.Clear().Append(line.Trim()); }
                }
            }
        });
        using var reg = token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        while (await p.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            if (AnalysisResult.Parse(line) is { } r)
            {
                results.Add(r);
                onResult?.Invoke(r);
            }
        }
        await p.WaitForExitAsync();
        await errTask;
        token.ThrowIfCancellationRequested();
        if (p.ExitCode != 0 && results.Count == 0)
        {
            string msg;
            lock (lastErr) msg = lastErr.ToString();
            throw new InvalidOperationException(msg.Length > 0 ? msg : $"exit code {p.ExitCode}");
        }
        return results;
    }
}
