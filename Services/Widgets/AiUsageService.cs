using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OpenTaskbar.Services.Widgets;

public sealed record AiProviderStatus(
    string Name,
    string PlanName,
    bool IsConnected,
    int FiveHourPercent,
    string FiveHourDetail,
    string ResetText,
    string WeeklyDetail,
    string TopModel);

/// <summary>
/// Reads local AI tool credentials and session logs (Claude Code ~/.claude, Antigravity/Gemini ~/.gemini, Codex ~/.codex)
/// to display live 5-hour session and 7-day weekly usage limits and token totals locally, matching LumaBar's AI Limits widget.
/// </summary>
public sealed class AiUsageService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private IReadOnlyList<AiProviderStatus> _cached = [];
    private DateTime _lastReadUtc;

    public IReadOnlyList<AiProviderStatus> Providers => _cached;
    public AiProviderStatus? Claude => _cached.FirstOrDefault(p => p.Name == "Claude");

    public async Task<IReadOnlyList<AiProviderStatus>> RefreshAsync(bool force = false)
    {
        if (!force && _cached.Count > 0 && DateTime.UtcNow - _lastReadUtc < TimeSpan.FromSeconds(45))
        {
            return _cached;
        }

        var claudeTask = Task.Run(ReadClaudeAsync);
        var geminiTask = Task.Run(ReadGemini);
        var codexTask = Task.Run(ReadCodex);

        await Task.WhenAll(claudeTask, geminiTask, codexTask);
        _cached = [await claudeTask, await geminiTask, await codexTask];
        _lastReadUtc = DateTime.UtcNow;
        return _cached;
    }

    private static async Task<AiProviderStatus> ReadClaudeAsync()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(userProfile, ".claude");
        var credsPath = Path.Combine(claudeDir, ".credentials.json");
        var projectsDir = Path.Combine(claudeDir, "projects");

        var plan = "Claude";
        string? accessToken = null;
        var hasCreds = false;

        if (File.Exists(credsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(credsPath));
                if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                {
                    hasCreds = true;
                    if (oauth.TryGetProperty("subscriptionType", out var sub) && !string.IsNullOrWhiteSpace(sub.GetString()))
                    {
                        plan = "Claude " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(sub.GetString()!);
                    }
                    if (oauth.TryGetProperty("accessToken", out var tok))
                    {
                        accessToken = tok.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Claude credentials read failed: {0}", ex.Message);
            }
        }

        // Scan local ~/.claude/projects/**/*.jsonl for 5-hour and 7-day token & turn statistics
        var nowUtc = DateTimeOffset.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var weekCutoff = nowUtc.AddDays(-7);

        var fiveHourTurns = 0;
        long fiveHourTokens = 0;
        var oldestFiveHourTurn = DateTimeOffset.MaxValue;
        var weekTurns = 0;
        long weekTokens = 0;
        var modelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(projectsDir))
        {
            try
            {
                var files = new DirectoryInfo(projectsDir)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc >= weekCutoff.UtcDateTime)
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs);
                        while (await reader.ReadLineAsync() is { } line)
                        {
                            if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
                            using var lineDoc = JsonDocument.Parse(line);
                            var root = lineDoc.RootElement;
                            if (!root.TryGetProperty("type", out var tProp) || tProp.GetString() != "assistant") continue;
                            if (!root.TryGetProperty("timestamp", out var tsProp)
                                || !DateTimeOffset.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                                continue;

                            if (ts < weekCutoff) continue;

                            long msgTokens = 0;
                            if (root.TryGetProperty("message", out var msg))
                            {
                                if (msg.TryGetProperty("model", out var mProp) && mProp.GetString() is { Length: > 0 } modelName)
                                {
                                    modelCounts[modelName] = modelCounts.TryGetValue(modelName, out var c) ? c + 1 : 1;
                                }
                                if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                                {
                                    msgTokens += GetLong(usage, "input_tokens");
                                    msgTokens += GetLong(usage, "output_tokens");
                                    msgTokens += GetLong(usage, "cache_creation_input_tokens");
                                    msgTokens += GetLong(usage, "cache_read_input_tokens");
                                }
                            }

                            weekTurns++;
                            weekTokens += msgTokens;
                            if (ts >= fiveHourCutoff)
                            {
                                fiveHourTurns++;
                                fiveHourTokens += msgTokens;
                                if (ts < oldestFiveHourTurn) oldestFiveHourTurn = ts;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        // Try live OAuth utilization endpoint if token is present
        int? oauthFiveHourPct = null;
        string? oauthResetText = null;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                req.Headers.Add("Authorization", $"Bearer {accessToken}");
                req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
                using var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var usageDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (usageDoc.RootElement.TryGetProperty("five_hour", out var fh))
                    {
                        if (fh.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var util))
                        {
                            oauthFiveHourPct = (int)Math.Clamp(Math.Round(util <= 1.0 ? util * 100 : util), 0, 100);
                        }
                        if (fh.TryGetProperty("resets_at", out var ra)
                            && DateTimeOffset.TryParse(ra.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetAt))
                        {
                            oauthResetText = FormatCountdown(resetAt - nowUtc);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var topModel = modelCounts.OrderByDescending(kv => kv.Value).Select(kv => FormatModelName(kv.Key)).FirstOrDefault() ?? "Claude";
        var fiveHourPct = oauthFiveHourPct ?? Math.Min(100, (int)Math.Round(fiveHourTurns / 45.0 * 100));
        var resetText = oauthResetText ?? (fiveHourTurns > 0 && oldestFiveHourTurn < DateTimeOffset.MaxValue
            ? FormatCountdown((oldestFiveHourTurn + TimeSpan.FromHours(5)) - nowUtc)
            : (UiText.IsEnglish ? "5h window ready" : "5 sa pencere hazır"));

        var fiveHourDetail = UiText.IsEnglish
            ? $"{fiveHourTurns} turns · {FormatTokens(fiveHourTokens)} (5h)"
            : $"{fiveHourTurns} yanıt · {FormatTokens(fiveHourTokens)} (5 sa)";
        var weeklyDetail = UiText.IsEnglish
            ? $"7d: {weekTurns} turns · {FormatTokens(weekTokens)}"
            : $"7g: {weekTurns} yanıt · {FormatTokens(weekTokens)}";

        return new AiProviderStatus(
            Name: "Claude",
            PlanName: plan,
            IsConnected: hasCreds || weekTurns > 0,
            FiveHourPercent: fiveHourPct,
            FiveHourDetail: fiveHourDetail,
            ResetText: resetText,
            WeeklyDetail: weeklyDetail,
            TopModel: topModel);
    }

    private static AiProviderStatus ReadGemini()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var brainDir = Path.Combine(userProfile, ".gemini", "antigravity", "brain");
        var nowUtc = DateTime.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var weekCutoff = nowUtc.AddDays(-7);

        var fiveHourSteps = 0;
        var weekSteps = 0;
        var oldestFiveHour = DateTime.MaxValue;

        if (Directory.Exists(brainDir))
        {
            try
            {
                var files = new DirectoryInfo(brainDir)
                    .EnumerateFiles("transcript.jsonl", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc >= weekCutoff)
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs);
                        while (reader.ReadLine() is { } line)
                        {
                            if (!line.Contains("PLANNER_RESPONSE", StringComparison.Ordinal)) continue;
                            weekSteps++;
                            if (file.LastWriteTimeUtc >= fiveHourCutoff)
                            {
                                fiveHourSteps++;
                                if (file.CreationTimeUtc < oldestFiveHour && file.CreationTimeUtc >= fiveHourCutoff)
                                    oldestFiveHour = file.CreationTimeUtc;
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        var pct = Math.Min(100, (int)Math.Round(fiveHourSteps / 120.0 * 100));
        var reset = fiveHourSteps > 0 && oldestFiveHour < DateTime.MaxValue
            ? FormatCountdown((oldestFiveHour + TimeSpan.FromHours(5)) - nowUtc)
            : (UiText.IsEnglish ? "Active in Antigravity" : "Antigravity aktif");

        return new AiProviderStatus(
            Name: "Gemini",
            PlanName: "Antigravity",
            IsConnected: Directory.Exists(brainDir),
            FiveHourPercent: pct,
            FiveHourDetail: UiText.IsEnglish ? $"{fiveHourSteps} steps (5h)" : $"{fiveHourSteps} adım (5 sa)",
            ResetText: reset,
            WeeklyDetail: UiText.IsEnglish ? $"7d: {weekSteps} steps" : $"7g: {weekSteps} adım",
            TopModel: "Gemini / Opus");
    }

    private static AiProviderStatus ReadCodex()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexDir = Path.Combine(userProfile, ".codex");
        var nowUtc = DateTime.UtcNow;
        var fiveHourCutoff = nowUtc.AddHours(-5);
        var weekCutoff = nowUtc.AddDays(-7);

        var fiveHourSessions = 0;
        var weekSessions = 0;

        if (Directory.Exists(codexDir))
        {
            try
            {
                var files = new DirectoryInfo(codexDir)
                    .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc >= weekCutoff)
                    .ToList();
                weekSessions = files.Count;
                fiveHourSessions = files.Count(f => f.LastWriteTimeUtc >= fiveHourCutoff);
            }
            catch
            {
            }
        }

        var pct = Math.Min(100, fiveHourSessions * 12);
        return new AiProviderStatus(
            Name: "ChatGPT",
            PlanName: "Codex CLI",
            IsConnected: Directory.Exists(codexDir),
            FiveHourPercent: pct,
            FiveHourDetail: UiText.IsEnglish ? $"{fiveHourSessions} sessions (5h)" : $"{fiveHourSessions} oturum (5 sa)",
            ResetText: UiText.IsEnglish ? "5h window" : "5 sa pencere",
            WeeklyDetail: UiText.IsEnglish ? $"7d: {weekSessions} sessions" : $"7g: {weekSessions} oturum",
            TopModel: "Codex");
    }

    private static long GetLong(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M tok",
        >= 1_000 => $"{tokens / 1_000.0:F0}B tok",
        _ => $"{tokens} tok"
    };

    private static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return UiText.IsEnglish ? "Resets soon" : "Yakında sıfırlanır";
        var h = (int)remaining.TotalHours;
        var m = remaining.Minutes;
        return UiText.IsEnglish
            ? (h > 0 ? $"Resets in {h}h {m}m" : $"Resets in {m}m")
            : (h > 0 ? $"{h} sa {m} dk sonra" : $"{m} dk sonra");
    }

    private static string FormatModelName(string raw)
    {
        if (raw.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Claude Opus";
        if (raw.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "Claude Sonnet";
        if (raw.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return "Claude Haiku";
        return raw;
    }
}
