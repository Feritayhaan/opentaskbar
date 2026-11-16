using System.Diagnostics;
using System.Globalization;
using System.Net.Http;

namespace OpenTaskbar.Services.Widgets;

public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay, string? Location);

/// <summary>
/// Read-only calendar fed by secret iCal links (Google, Outlook, iCloud). Links stay on this PC and the feed
/// refreshes every 15 minutes. Recurring events (DAILY/WEEKLY/MONTHLY/YEARLY) are expanded for a rolling window.
/// </summary>
public sealed class CalendarService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private IReadOnlyList<CalendarEvent> _events = [];
    private string _lastKey = "";
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private int _fetching;

    public IReadOnlyList<CalendarEvent> Events => _events;
    public bool HasFeeds { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTaskbar/1.0");
        return client;
    }

    public async Task RefreshAsync(IReadOnlyList<string>? urls, bool force = false)
    {
        var feeds = (urls ?? [])
            .Select(NormalizeUrl)
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();
        HasFeeds = feeds.Count > 0;
        var key = string.Join('|', feeds);
        if (!HasFeeds)
        {
            if (_events.Count > 0) { _events = []; _lastKey = key; Changed?.Invoke(); }
            return;
        }
        if (!force && key == _lastKey && DateTime.UtcNow - _lastFetchUtc < RefreshInterval) return;
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;

        try
        {
            var windowStart = DateTime.Today.AddDays(-1);
            var windowEnd = DateTime.Today.AddDays(90);
            var all = new List<CalendarEvent>();
            string? error = null;
            foreach (var feed in feeds)
            {
                try
                {
                    var text = await Http.GetStringAsync(feed);
                    all.AddRange(await Task.Run(() => Parse(text, windowStart, windowEnd)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    error = ex.Message;
                    Trace.TraceWarning("Calendar feed failed: {0}", ex.Message);
                }
            }

            LastError = error;
            if (all.Count > 0 || error is null)
            {
                _events = all.OrderBy(e => e.Start).ToList();
            }
            _lastKey = key;
            _lastFetchUtc = DateTime.UtcNow;
            Changed?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _fetching, 0);
        }
    }

    public IReadOnlyList<CalendarEvent> Upcoming(DateTime from, int count) =>
        _events.Where(e => e.End > from).OrderBy(e => e.Start).Take(count).ToList();

    public IReadOnlyList<CalendarEvent> ForDay(DateTime day) =>
        _events.Where(e => e.Start.Date <= day.Date && (e.AllDay ? e.End.Date > day.Date : e.End > day.Date && e.Start < day.Date.AddDays(1)))
            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ToList();

    public bool HasEventsOn(DateTime day) => ForDay(day).Count > 0;

    public static string? NormalizeUrl(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text[9..];
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.ToString() : null;
    }

    // ---- iCalendar parsing -------------------------------------------------------------------------------

    internal static IReadOnlyList<CalendarEvent> Parse(string ics, DateTime windowStart, DateTime windowEnd)
    {
        var result = new List<CalendarEvent>();
        var lines = Unfold(ics);
        List<(string Name, string Params, string Value)>? current = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = []; continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) Expand(current, windowStart, windowEnd, result);
                current = null;
                continue;
            }
            if (current is null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = (semi >= 0 ? head[..semi] : head).ToUpperInvariant();
            var parameters = semi >= 0 ? head[(semi + 1)..] : "";
            current.Add((name, parameters, line[(colon + 1)..]));
        }
        return result;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        string? pending = null;
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                pending += raw[1..];
                continue;
            }
            if (pending is not null) yield return pending;
            pending = raw;
        }
        if (pending is not null) yield return pending;
    }

    private static void Expand(
        List<(string Name, string Params, string Value)> props,
        DateTime windowStart,
        DateTime windowEnd,
        List<CalendarEvent> output)
    {
        var dtStart = props.FirstOrDefault(p => p.Name == "DTSTART");
        if (dtStart.Name is null) return;
        if (props.Any(p => p.Name == "STATUS" && p.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))) return;
        if (!TryParseDate(dtStart.Value, dtStart.Params, out var start, out var allDay)) return;

        DateTime end;
        var dtEnd = props.FirstOrDefault(p => p.Name == "DTEND");
        if (dtEnd.Name is not null && TryParseDate(dtEnd.Value, dtEnd.Params, out var parsedEnd, out _)) end = parsedEnd;
        else if (props.FirstOrDefault(p => p.Name == "DURATION") is { Name: not null } d && TryParseDuration(d.Value, out var duration))
            end = start + duration;
        else end = allDay ? start.AddDays(1) : start;
        if (end < start) end = start;
        var length = end - start;

        var title = Unescape(props.FirstOrDefault(p => p.Name == "SUMMARY").Value);
        if (string.IsNullOrWhiteSpace(title)) title = "—";
        var location = Unescape(props.FirstOrDefault(p => p.Name == "LOCATION").Value);
        if (string.IsNullOrWhiteSpace(location)) location = null;

        var excluded = new HashSet<DateTime>();
        foreach (var ex in props.Where(p => p.Name == "EXDATE"))
            foreach (var part in ex.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (TryParseDate(part, ex.Params, out var exDate, out _)) excluded.Add(exDate);

        void Add(DateTime occurrenceStart)
        {
            if (excluded.Contains(occurrenceStart)) return;
            var occurrenceEnd = occurrenceStart + length;
            if (occurrenceEnd < windowStart || occurrenceStart > windowEnd) return;
            output.Add(new CalendarEvent(title, occurrenceStart, occurrenceEnd, allDay, location));
        }

        var rrule = props.FirstOrDefault(p => p.Name == "RRULE");
        if (rrule.Name is null)
        {
            Add(start);
            return;
        }

        var rule = ParseRule(rrule.Value);
        var freq = rule.GetValueOrDefault("FREQ", "");
        var interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var i) && i > 0 ? i : 1;
        var count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : int.MaxValue;
        var until = DateTime.MaxValue;
        if (rule.TryGetValue("UNTIL", out var untilText) && TryParseDate(untilText, "", out var parsedUntil, out _))
            until = parsedUntil.Date.AddDays(1);
        var days = ParseByDay(rule.GetValueOrDefault("BYDAY"));

        var produced = 0;
        switch (freq)
        {
            case "DAILY":
                for (var n = 0; produced < count && n < 4000; n++)
                {
                    var occurrence = start.AddDays((double)n * interval);
                    if (occurrence >= until || occurrence > windowEnd) break;
                    produced++;
                    Add(occurrence);
                }
                break;
            case "WEEKLY":
            {
                var weekAnchor = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                var targetDays = days.Count > 0 ? days : [start.DayOfWeek];
                for (var day = start.Date; produced < count && day <= windowEnd && day < until; day = day.AddDays(1))
                {
                    var weeks = (int)Math.Floor((day - weekAnchor).TotalDays / 7);
                    if (weeks % interval != 0 || !targetDays.Contains(day.DayOfWeek)) continue;
                    produced++;
                    Add(day + start.TimeOfDay);
                }
                break;
            }
            case "MONTHLY":
                for (var n = 0; produced < count && n < 600; n++)
                {
                    var occurrence = start.AddMonths(n * interval);
                    if (occurrence >= until || occurrence > windowEnd) break;
                    produced++;
                    Add(occurrence);
                }
                break;
            case "YEARLY":
                for (var n = 0; produced < count && n < 100; n++)
                {
                    var occurrence = start.AddYears(n * interval);
                    if (occurrence >= until || occurrence > windowEnd) break;
                    produced++;
                    Add(occurrence);
                }
                break;
            default:
                Add(start);
                break;
        }
    }

    private static Dictionary<string, string> ParseRule(string value)
    {
        var rule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) rule[part[..eq].ToUpperInvariant()] = part[(eq + 1)..];
        }
        return rule;
    }

    private static List<DayOfWeek> ParseByDay(string? text)
    {
        var list = new List<DayOfWeek>();
        foreach (var token in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var code = token.Length >= 2 ? token[^2..].ToUpperInvariant() : "";
            DayOfWeek? day = code switch
            {
                "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
                "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
                "SU" => DayOfWeek.Sunday, _ => null
            };
            if (day is { } d) list.Add(d);
        }
        return list;
    }

    private static bool TryParseDate(string value, string parameters, out DateTime result, out bool allDay)
    {
        result = default;
        allDay = false;
        value = value.Trim();
        if (parameters.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !parameters.Contains("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase)
            || value.Length == 8)
        {
            if (!DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result)) return false;
            allDay = true;
            return true;
        }

        var utc = value.EndsWith('Z');
        var text = utc ? value[..^1] : value;
        if (!DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        if (utc)
        {
            result = DateTime.SpecifyKind(parsed, DateTimeKind.Utc).ToLocalTime();
            return true;
        }

        var tzid = parameters.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"');
        if (tzid is not null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
                result = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local);
                return true;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        result = parsed;
        return true;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var text = value.Trim();
        if (!text.StartsWith('P')) return false;
        var number = "";
        var inTime = false;
        foreach (var ch in text[1..])
        {
            if (ch == 'T') { inTime = true; continue; }
            if (char.IsDigit(ch)) { number += ch; continue; }
            if (!int.TryParse(number, out var n)) return false;
            number = "";
            duration += ch switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' when inTime => TimeSpan.FromHours(n),
                'M' when inTime => TimeSpan.FromMinutes(n),
                'S' when inTime => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero
            };
        }
        return true;
    }

    private static string Unescape(string text) => text
        .Replace("\\n", " ", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
