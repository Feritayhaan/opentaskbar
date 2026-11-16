using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenTaskbar.Services.Search;

/// <summary>
/// Orchestrates Spotlight-style search across installed apps, math expressions,
/// system commands, personal files, URLs, and web search, with async Shell icon loading.
/// </summary>
public sealed class SpotlightEngine : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AppIndex _appIndex = new();
    private readonly FileIndex _fileIndex = new();
    private readonly UsageStore _usage = new();
    private readonly SystemCommandProvider.Actions _actions;
    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string CacheKey, string Source, bool IconOnly, SearchResult Target)> _iconQueue = new();
    private readonly AutoResetEvent _iconSignal = new(false);
    private readonly Thread _iconThread;
    private bool _disposed;

    public SpotlightEngine(Dispatcher dispatcher, SystemCommandProvider.Actions actions)
    {
        _dispatcher = dispatcher;
        _actions = actions;
        _iconThread = new Thread(IconWorkerLoop)
        {
            IsBackground = true,
            Name = "OpenTaskbar Spotlight icons"
        };
        _iconThread.SetApartmentState(ApartmentState.STA);
        _iconThread.Start();

        _appIndex.Updated += () => IndexUpdated?.Invoke();
        _appIndex.Refresh(dispatcher);
        _fileIndex.Refresh();
    }

    public bool IsAppIndexReady => _appIndex.IsReady;
    public event Action? IndexUpdated;

    public void WarmIndexes()
    {
        if (!_appIndex.IsReady) _appIndex.Refresh(_dispatcher);
        _fileIndex.RefreshIfStale(TimeSpan.FromMinutes(10));
    }

    public IReadOnlyList<SearchResult> Search(string rawQuery)
    {
        var query = rawQuery.Trim();
        if (query.Length == 0)
        {
            return BuildEmptyStateResults();
        }

        var normalized = TextMatcher.Normalize(query);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<SearchResult>();

        // 1. Calculator
        SearchResult? calcResult = null;
        if (Calculator.LooksLikeExpression(query) && Calculator.TryEvaluate(query, out var value))
        {
            var formatted = Calculator.Format(value, UiText.Culture);
            calcResult = new SearchResult
            {
                Title = $"= {formatted}",
                Subtitle = $"{query}  ·  {UiText.Get("CopyResult")}",
                Category = SearchCategory.Calculator,
                GroupName = UiText.Get("Calculator"),
                Key = $"calc:{query}",
                Score = 2000,
                Glyph = SystemCommandProvider.ResolveGeometry("IconCalculator"),
                GlyphBackground = new SolidColorBrush(Color.FromRgb(234, 138, 36)),
                Execute = () =>
                {
                    try { Clipboard.SetText(formatted); } catch { }
                    return true;
                }
            };
            results.Add(calcResult);
        }

        // 2. Installed applications
        var appMatches = new List<SearchResult>();
        foreach (var app in _appIndex.Apps)
        {
            var score = TextMatcher.Score(normalized, tokens, app.Text);
            if (score <= 0) continue;
            var key = $"app:{app.AppId}";
            score += _usage.Boost(key) + 120; // Apps rank slightly above files for equal text scores.
            var captured = app;
            appMatches.Add(new SearchResult
            {
                Title = captured.Name,
                Subtitle = UiText.Get("Application"),
                Category = SearchCategory.Applications,
                GroupName = UiText.Get("Applications"),
                Key = key,
                Score = score,
                IconSource = $"shell:AppsFolder\\{captured.AppId}",
                IconCacheKey = key,
                Glyph = SystemCommandProvider.ResolveGeometry("IconAppFallback"),
                Execute = () =>
                {
                    _usage.Record(key);
                    return AppIndex.Launch(captured);
                }
            });
        }

        // 3. System commands
        var systemMatches = new List<SearchResult>();
        foreach (var cmd in SystemCommandProvider.Build(_actions))
        {
            var score = TextMatcher.Score(normalized, tokens, cmd.Text);
            if (score <= 0) continue;
            score += _usage.Boost(cmd.Key) + 60;
            var captured = cmd;
            systemMatches.Add(new SearchResult
            {
                Title = captured.Title,
                Subtitle = captured.Subtitle,
                Category = SearchCategory.System,
                GroupName = UiText.Get("SystemCommands"),
                Key = captured.Key,
                Score = score,
                Glyph = SystemCommandProvider.ResolveGeometry(captured.GeometryKey),
                GlyphBackground = new SolidColorBrush(Color.FromArgb(40, 120, 165, 220)),
                Execute = () =>
                {
                    _usage.Record(captured.Key);
                    return captured.Execute();
                }
            });
        }

        // 4. Files & Folders (scan only when query >= 2 chars to keep 1-char typing instantaneous)
        var fileMatches = new List<SearchResult>();
        if (normalized.Length >= 2)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _fileIndex.Entries)
            {
                var score = TextMatcher.Score(normalized, tokens, entry.Text);
                if (score <= 0) continue;
                var key = $"file:{entry.Path}";
                var recencyBoost = entry.LastWrite > DateTime.MinValue
                    ? Math.Max(0, 40 - (now - entry.LastWrite).TotalDays * 0.5)
                    : 0;
                score += _usage.Boost(key) + recencyBoost;
                if (entry.IsDirectory) score += 15;
                var captured = entry;
                fileMatches.Add(new SearchResult
                {
                    Title = captured.Name,
                    Subtitle = ShortenPath(captured.Path),
                    Category = SearchCategory.Files,
                    GroupName = UiText.Get("Files"),
                    Key = key,
                    Score = score,
                    IconSource = captured.Path,
                    IconCacheKey = key,
                    Glyph = SystemCommandProvider.ResolveGeometry(captured.IsDirectory ? "IconFolder" : "IconFile"),
                    Execute = () =>
                    {
                        _usage.Record(key);
                        return OpenPath(captured.Path);
                    },
                    Reveal = () => RevealPath(captured.Path)
                });
            }
        }

        // 5. Promote single Top Hit (excluding calculator which is already at top)
        var nonCalc = appMatches.Concat(systemMatches).Concat(fileMatches)
            .OrderByDescending(r => r.Score)
            .ToList();

        if (calcResult is null && nonCalc.Count > 0)
        {
            var top = nonCalc[0];
            top.Category = SearchCategory.TopHit;
            top.GroupName = UiText.Get("TopHit");
            results.Add(top);
            nonCalc.RemoveAt(0);
        }

        var remainingApps = nonCalc.Where(r => r.Category == SearchCategory.Applications).Take(6).ToList();
        var remainingSystem = nonCalc.Where(r => r.Category == SearchCategory.System).Take(4).ToList();
        var remainingFiles = nonCalc.Where(r => r.Category == SearchCategory.Files).Take(6).ToList();

        results.AddRange(remainingApps);
        results.AddRange(remainingSystem);
        results.AddRange(remainingFiles);

        // 6. Direct URL or Web Search fallback
        if (LooksLikeUrl(query, out var url))
        {
            results.Add(new SearchResult
            {
                Title = query,
                Subtitle = UiText.Get("OpenUrl"),
                Category = SearchCategory.Web,
                GroupName = UiText.Get("Web"),
                Key = $"url:{url}",
                Score = 50,
                Glyph = SystemCommandProvider.ResolveGeometry("IconGlobe"),
                GlyphBackground = new SolidColorBrush(Color.FromArgb(45, 74, 158, 255)),
                Execute = () => SystemCommandProvider.LaunchUri(url)
            });
        }
        else if (calcResult is null)
        {
            var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
            results.Add(new SearchResult
            {
                Title = UiText.Format("SearchWeb", query),
                Subtitle = "Google",
                Category = SearchCategory.Web,
                GroupName = UiText.Get("Web"),
                Key = $"web:{query}",
                Score = 10,
                Glyph = SystemCommandProvider.ResolveGeometry("IconSearch"),
                GlyphBackground = new SolidColorBrush(Color.FromArgb(45, 74, 158, 255)),
                Execute = () => SystemCommandProvider.LaunchUri(searchUrl)
            });
        }

        foreach (var item in results)
        {
            ScheduleIconLoad(item);
        }

        return results;
    }

    private IReadOnlyList<SearchResult> BuildEmptyStateResults()
    {
        var results = new List<SearchResult>();

        // Top frequently launched apps first, or alphabetical popular apps
        var topApps = _appIndex.Apps
            .Select(app => (App: app, Boost: _usage.Boost($"app:{app.AppId}")))
            .OrderByDescending(pair => pair.Boost)
            .ThenBy(pair => pair.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(6);

        foreach (var (app, _) in topApps)
        {
            var key = $"app:{app.AppId}";
            var captured = app;
            results.Add(new SearchResult
            {
                Title = captured.Name,
                Subtitle = UiText.Get("Application"),
                Category = SearchCategory.Applications,
                GroupName = UiText.Get("Applications"),
                Key = key,
                IconSource = $"shell:AppsFolder\\{captured.AppId}",
                IconCacheKey = key,
                Glyph = SystemCommandProvider.ResolveGeometry("IconAppFallback"),
                Execute = () =>
                {
                    _usage.Record(key);
                    return AppIndex.Launch(captured);
                }
            });
        }

        // Quick system commands
        var quickKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "cmd:widgets", "cmd:opentaskbar-settings", "cmd:taskmgr", "cmd:terminal"
        };
        foreach (var cmd in SystemCommandProvider.Build(_actions).Where(c => quickKeys.Contains(c.Key)))
        {
            var captured = cmd;
            results.Add(new SearchResult
            {
                Title = captured.Title,
                Subtitle = captured.Subtitle,
                Category = SearchCategory.System,
                GroupName = UiText.Get("SystemCommands"),
                Key = captured.Key,
                Glyph = SystemCommandProvider.ResolveGeometry(captured.GeometryKey),
                GlyphBackground = new SolidColorBrush(Color.FromArgb(40, 120, 165, 220)),
                Execute = () =>
                {
                    _usage.Record(captured.Key);
                    return captured.Execute();
                }
            });
        }

        foreach (var item in results)
        {
            ScheduleIconLoad(item);
        }

        return results;
    }

    private void ScheduleIconLoad(SearchResult item)
    {
        if (item.IconSource is null || item.IconCacheKey is null) return;
        if (_iconCache.TryGetValue(item.IconCacheKey, out var cached))
        {
            item.Icon = cached;
            return;
        }

        _iconQueue.Enqueue((item.IconCacheKey, item.IconSource, true, item));
        _iconSignal.Set();
    }

    private void IconWorkerLoop()
    {
        while (!_disposed)
        {
            if (!_iconQueue.TryDequeue(out var job))
            {
                _iconSignal.WaitOne(250);
                continue;
            }

            if (!_iconCache.TryGetValue(job.CacheKey, out var image))
            {
                image = ShellItems.GetImage(job.Source, 64, job.IconOnly);
                if (image is null && File.Exists(job.Source))
                {
                    image = ApplicationIconProvider.GetForFile(job.Source);
                }

                if (_iconCache.Count > 600) _iconCache.Clear();
                _iconCache[job.CacheKey] = image;
            }

            if (image is not null && !_disposed)
            {
                _dispatcher.BeginInvoke(() => job.Target.Icon = image, DispatcherPriority.Background);
            }
        }
    }

    private static bool LooksLikeUrl(string query, out string url)
    {
        url = "";
        if (query.Contains(' ')) return false;
        if (query.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || query.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = query;
            return Uri.TryCreate(url, UriKind.Absolute, out _);
        }

        var dot = query.IndexOf('.');
        if (dot > 0 && dot < query.Length - 2 && !query.EndsWith('.'))
        {
            var tld = query[(query.LastIndexOf('.') + 1)..];
            if (tld.Length is >= 2 and <= 6 && tld.All(char.IsLetter)
                && !File.Exists(query))
            {
                url = "https://" + query;
                return Uri.TryCreate(url, UriKind.Absolute, out _);
            }
        }

        return false;
    }

    private static string ShortenPath(string path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "~" + path[userProfile.Length..];
        }
        return path;
    }

    private static bool OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RevealPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Reveal path failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _iconSignal.Set();
    }
}
