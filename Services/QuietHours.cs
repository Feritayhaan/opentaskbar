using System.Globalization;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>Quiet hours hold alerts (toasts, sounds, dock badges) during a user-defined daily window.</summary>
public static class QuietHours
{
    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    public static bool IsActive(DockSettings settings, DateTime now)
    {
        if (!settings.QuietHoursEnabled) return false;
        if (!TryParseTime(settings.QuietHoursStart, out var start) || !TryParseTime(settings.QuietHoursEnd, out var end)) return false;
        if (start == end) return false;
        var t = now.TimeOfDay;
        // A window that crosses midnight (22:00 → 07:00) is active when either side matches.
        return start < end ? t >= start && t < end : t >= start || t < end;
    }
}
