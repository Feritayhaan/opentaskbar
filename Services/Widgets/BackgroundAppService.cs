using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenTaskbar.Services.Widgets;

public sealed record AppResourceGroup(
    string Name,
    double MemoryMb,
    int ProcessCount,
    nint MainWindowHandle,
    bool IsBackground,
    IReadOnlyList<int> ProcessIds);

public sealed record ScreenTimeEntry(string AppName, int SecondsToday);

public sealed record RecycleBinInfo(long ItemCount, double SizeMb);

/// <summary>
/// Tracks running foreground/background processes (Top RAM / Background Apps with focus and force-kill),
/// daily per-app Screen Time, and Recycle Bin status.
/// </summary>
public sealed class BackgroundAppService
{
    private static readonly HashSet<string> SystemIdleProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit", "services", "lsass",
        "svchost", "fontdrvhost", "dwm", "conhost", "sihost", "taskhostw", "RuntimeBroker", "SearchIndexer",
        "ctfmon", "dllhost", "WmiPrvSE", "audiodg", "spoolsv", "OpenTaskbar"
    };

    private readonly string _screenTimePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "screentime.json");

    private string _currentDay = DateTime.Today.ToString("yyyy-MM-dd");
    private Dictionary<string, int> _secondsByApp = new(StringComparer.OrdinalIgnoreCase);
    private int _ticksSinceSave;

    public BackgroundAppService()
    {
        LoadScreenTime();
    }

    public int TotalScreenSecondsToday => _secondsByApp.Values.Sum();

    public void RecordForegroundTick(int seconds = 1)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!string.Equals(today, _currentDay, StringComparison.Ordinal))
        {
            _currentDay = today;
            _secondsByApp.Clear();
        }

        var fg = GetForegroundWindow();
        if (fg == 0) return;
        _ = GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = FriendlyAppName(proc.ProcessName);
            if (SystemIdleProcesses.Contains(proc.ProcessName)
                || string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _secondsByApp[name] = _secondsByApp.TryGetValue(name, out var s) ? s + seconds : seconds;
            if (++_ticksSinceSave >= 15)
            {
                _ticksSinceSave = 0;
                SaveScreenTime();
            }
        }
        catch
        {
        }
    }

    public IReadOnlyList<ScreenTimeEntry> GetTopScreenTimeApps(int maxCount = 5) =>
        _secondsByApp
            .OrderByDescending(kv => kv.Value)
            .Take(maxCount)
            .Select(kv => new ScreenTimeEntry(kv.Key, kv.Value))
            .ToList();

    public IReadOnlyList<AppResourceGroup> GetRunningAppGroups(int maxCount = 8)
    {
        var groups = new Dictionary<string, (double MemBytes, List<int> Pids, nint Hwnd)>(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (SystemIdleProcesses.Contains(proc.ProcessName)) continue;
                var mem = (double)proc.WorkingSet64;
                if (mem < 15 * 1024 * 1024) continue; // Ignore tiny helper daemons < 15 MB

                var name = FriendlyAppName(proc.ProcessName);
                var hwnd = proc.MainWindowHandle;
                if (groups.TryGetValue(name, out var existing))
                {
                    existing.Pids.Add(proc.Id);
                    groups[name] = (existing.MemBytes + mem, existing.Pids, existing.Hwnd != 0 ? existing.Hwnd : hwnd);
                }
                else
                {
                    groups[name] = (mem, [proc.Id], hwnd);
                }
            }
            catch
            {
            }
            finally
            {
                proc.Dispose();
            }
        }

        return groups
            .OrderByDescending(kv => kv.Value.MemBytes)
            .Take(maxCount)
            .Select(kv => new AppResourceGroup(
                Name: kv.Key,
                MemoryMb: kv.Value.MemBytes / (1024.0 * 1024.0),
                ProcessCount: kv.Value.Pids.Count,
                MainWindowHandle: kv.Value.Hwnd,
                IsBackground: kv.Value.Hwnd == 0,
                ProcessIds: kv.Value.Pids))
            .ToList();
    }

    public static bool KillAppGroup(AppResourceGroup group)
    {
        var anyKilled = false;
        foreach (var pid in group.ProcessIds)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                anyKilled = true;
            }
            catch
            {
            }
        }
        return anyKilled;
    }

    public static RecycleBinInfo ReadRecycleBin()
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        if (SHQueryRecycleBin(null, ref info) != 0)
            return new RecycleBinInfo(0, 0);
        return new RecycleBinInfo(info.i64NumItems, info.i64Size / (1024.0 * 1024.0));
    }

    public static void EmptyRecycleBin()
    {
        // SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2
        _ = SHEmptyRecycleBin(0, null, 0);
    }

    private static string FriendlyAppName(string processName) => processName.ToLowerInvariant() switch
    {
        "code" => "VS Code",
        "antigravity" => "Antigravity",
        "claude" => "Claude",
        "chrome" => "Google Chrome",
        "brave" => "Brave",
        "msedge" => "Microsoft Edge",
        "firefox" => "Firefox",
        "spotify" => "Spotify",
        "discord" => "Discord",
        "windowsterminal" => "Terminal",
        "explorer" => "File Explorer",
        _ => CultureInfoTitle(processName)
    };

    private static string CultureInfoTitle(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private sealed record ScreenTimeFile(string Date, Dictionary<string, int> Apps);

    private void LoadScreenTime()
    {
        try
        {
            if (!File.Exists(_screenTimePath)) return;
            var data = JsonSerializer.Deserialize<ScreenTimeFile>(File.ReadAllText(_screenTimePath));
            if (data is not null && string.Equals(data.Date, _currentDay, StringComparison.Ordinal) && data.Apps is not null)
            {
                _secondsByApp = new Dictionary<string, int>(data.Apps, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }
    }

    private void SaveScreenTime()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_screenTimePath)!);
            var json = JsonSerializer.Serialize(new ScreenTimeFile(_currentDay, _secondsByApp));
            File.WriteAllText(_screenTimePath, json);
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(nint hwnd, string? rootPath, uint flags);
}
