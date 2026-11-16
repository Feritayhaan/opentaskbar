using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace OpenTaskbar.Services.Search;

public sealed record AppEntry(string Name, string AppId, SearchText Text);

/// <summary>Installed applications from shell:AppsFolder, indexed on a background STA thread.</summary>
public sealed class AppIndex
{
    private static readonly string[] HiddenWords =
    [
        "uninstall", "kaldir", "remove", "readme", "release notes", "license", "documentation", "help", "website",
        "manual", "yardim", "lisans"
    ];

    private volatile IReadOnlyList<AppEntry> _apps = [];
    private int _loading;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public bool IsReady { get; private set; }
    public event Action? Updated;

    public void Refresh(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var apps = new List<AppEntry>();
                foreach (var app in ShellItems.EnumerateInstalledApps())
                {
                    var text = new SearchText(app.Name);
                    if (HiddenWords.Any(word => text.Normalized.Contains(word, StringComparison.Ordinal))) continue;
                    if (app.AppId.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (app.AppId.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                        || app.AppId.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)
                        || app.AppId.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                        || app.AppId.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                        || app.AppId.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(app.Name)) continue;
                    apps.Add(new AppEntry(app.Name, app.AppId, text));
                }

                _apps = apps;
                IsReady = true;
                dispatcher.BeginInvoke(() => Updated?.Invoke());
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("App index failed: {0}", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _loading, 0);
            }
        })
        {
            IsBackground = true,
            Name = "OpenTaskbar app index"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static bool Launch(AppEntry app)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + app.AppId) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.AppId) { UseShellExecute = false })?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}

public sealed record FileEntry(string Name, string Path, bool IsDirectory, DateTime LastWrite, SearchText Text);

/// <summary>Lightweight in-memory index of the user's personal folders.</summary>
public sealed class FileIndex
{
    private const int MaximumEntries = 60000;
    private const int MaximumDepth = 6;
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", "__pycache__", "AppData", "$RECYCLE.BIN", "venv", ".venv",
        "site-packages", "dist", "build", "target", ".git", ".vs", ".idea", "Library"
    };

    private volatile IReadOnlyList<FileEntry> _entries = [];
    private int _loading;

    public IReadOnlyList<FileEntry> Entries => _entries;
    public DateTime LastIndexed { get; private set; }

    public void RefreshIfStale(TimeSpan maximumAge)
    {
        if (DateTime.UtcNow - LastIndexed < maximumAge) return;
        Refresh();
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        LastIndexed = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                var entries = new List<FileEntry>(8192);
                var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in GetRoots())
                {
                    if (entries.Count >= MaximumEntries) break;
                    if (!Directory.Exists(root) || seenRoots.Any(seen => root.StartsWith(seen + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    seenRoots.Add(root);
                    Walk(new DirectoryInfo(root), 0, entries);
                }

                _entries = entries;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("File index failed: {0}", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _loading, 0);
            }
        });
    }

    private static IEnumerable<string> GetRoots()
    {
        // Most specific folders first so they are indexed before the cap is reached.
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return KnownFolders.Downloads;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive)) yield return oneDrive;
    }

    private static void Walk(DirectoryInfo directory, int depth, List<FileEntry> entries)
    {
        if (entries.Count >= MaximumEntries) return;
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary
            }).ToList();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var subdirectories = new List<DirectoryInfo>();
        foreach (var child in children)
        {
            if (entries.Count >= MaximumEntries) return;
            var isDirectory = child is DirectoryInfo;
            if (isDirectory && (child.Name.StartsWith('.') || SkippedDirectories.Contains(child.Name))) continue;
            if (!isDirectory && (child.Name.StartsWith("~$", StringComparison.Ordinal) || child.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
                continue;
            DateTime lastWrite;
            try
            {
                lastWrite = child.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                lastWrite = DateTime.MinValue;
            }

            var displayName = isDirectory ? child.Name : System.IO.Path.GetFileNameWithoutExtension(child.Name);
            entries.Add(new FileEntry(child.Name, child.FullName, isDirectory, lastWrite, new SearchText(displayName)));
            if (isDirectory && (child.Attributes & FileAttributes.ReparsePoint) == 0) subdirectories.Add((DirectoryInfo)child);
        }

        if (depth >= MaximumDepth) return;
        foreach (var subdirectory in subdirectories) Walk(subdirectory, depth + 1, entries);
    }
}

/// <summary>Remembers which results the user opens so they rank higher next time.</summary>
public sealed class UsageStore
{
    private readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar", "search-usage.json");
    private Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public UsageStore()
    {
        try
        {
            if (File.Exists(_path))
                _counts = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path)) is { } counts
                    ? new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase)
                    : _counts;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Search usage could not be read: {0}", ex.Message);
        }
    }

    public double Boost(string key) => _counts.TryGetValue(key, out var count) ? Math.Min(220, 60 * Math.Log2(1 + count)) : 0;

    public void Record(string key)
    {
        _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
        if (_counts.Count > 500)
        {
            _counts = _counts.OrderByDescending(pair => pair.Value).Take(400)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_counts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Search usage could not be saved: {0}", ex.Message);
        }
    }
}

internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, 0, out var pointer) == 0 && pointer != 0)
            {
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer) ?? "";
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer);
                }
            }

            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
        uint flags, nint token, out nint path);
}
