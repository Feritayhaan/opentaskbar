namespace OpenTaskbar.Services.Widgets;

public sealed record WorldClockEntry(string Id, string City, string Time, string DayLabel, string Offset, bool IsDaytime);

/// <summary>Resolves time zone identifiers (IANA such as "Asia/Tokyo" or Windows ids) into display rows.</summary>
public static class WorldClockService
{
    public static IReadOnlyList<WorldClockEntry> Read(IReadOnlyList<string> zoneIds, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
        var culture = UiText.Culture;
        var entries = new List<WorldClockEntry>();
        foreach (var id in zoneIds)
        {
            if (!TryFind(id, out var zone)) continue;
            var time = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
            var diffDays = (time.Date - local.Date).Days;
            var dayLabel = diffDays switch
            {
                0 => UiText.Get("Today"),
                1 => UiText.Get("Tomorrow"),
                -1 => UiText.Get("Yesterday"),
                _ => time.ToString("ddd", culture)
            };
            var offset = zone.GetUtcOffset(now);
            var sign = offset < TimeSpan.Zero ? "−" : "+";
            var offsetText = $"UTC{sign}{Math.Abs(offset.Hours)}" + (offset.Minutes != 0 ? $":{Math.Abs(offset.Minutes):00}" : "");
            entries.Add(new WorldClockEntry(id, CityName(id, zone), time.ToString("HH:mm", culture), dayLabel, offsetText,
                time.Hour is >= 7 and < 19));
        }
        return entries;
    }

    public static bool IsKnownZone(string id) => TryFind(id, out _);

    public static bool TryFind(string id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static string CityName(string id, TimeZoneInfo zone)
    {
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return "UTC";
        var slash = id.LastIndexOf('/');
        return slash >= 0 ? id[(slash + 1)..].Replace('_', ' ') : zone.StandardName;
    }
}
