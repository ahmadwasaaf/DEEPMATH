using System.IO;

namespace BatchStudio.App.Services;

/// <summary>
/// How long each step of a Mass Invoice "Approve all" took, one line per approval in
/// %LOCALAPPDATA%\BATCH STUDIO\Logs\approve-timing.txt (to see which step is slow on a given PC).
/// </summary>
public static class ApproveTiming
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BATCH STUDIO", "Logs", "approve-timing.txt");

    public static void Write(int documents, int newSuppliers, int newAccounts, IEnumerable<string> steps, TimeSpan total)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  documents {documents} · new suppliers {newSuppliers} · new accounts {newAccounts}" +
                       $"  |  {string.Join("  |  ", steps)}  |  total {total.TotalSeconds:0.0}s";
            File.AppendAllText(FilePath, line + Environment.NewLine);
            var info = new FileInfo(FilePath);
            if (info.Length > 512 * 1024)
            {
                var keep = File.ReadAllLines(FilePath).TakeLast(500).ToArray();
                File.WriteAllLines(FilePath, keep);
            }
        }
        catch { }
    }
}
