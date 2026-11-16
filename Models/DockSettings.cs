namespace OpenTaskbar.Models;

public enum DockPosition
{
    Left,
    Center,
    Right
}

public enum AppTheme { Dark, Light }
public enum AppLanguage { Turkish, English }
public enum AutoHideMode { Disabled, Smart, Always }
public enum DockEdge { Bottom, Top }
public enum DockLayout { Pill, FullWidth }
public enum BarStyle { Glass, White, IosGlass, Midnight, Black }

public sealed record DockSettings(
    DockPosition Position = DockPosition.Center,
    IReadOnlyList<string>? PinOrder = null,
    AppTheme Theme = AppTheme.Dark,
    AppLanguage Language = AppLanguage.Turkish,
    bool AnimationsEnabled = true,
    bool PreviewsEnabled = true,
    bool NotificationBadgesEnabled = true,
    AutoHideMode AutoHide = AutoHideMode.Disabled,
    double SurfaceOpacity = 0.9,
    string? WeatherCity = null,
    IReadOnlyList<string>? MarketSymbols = null,
    bool SearchHotkeyEnabled = true,
    IReadOnlyList<string>? BarWidgets = null,
    DockEdge Edge = DockEdge.Bottom,
    DockLayout Layout = DockLayout.Pill,
    BarStyle Style = BarStyle.Glass,
    bool QuietHoursEnabled = false,
    string QuietHoursStart = "22:00",
    string QuietHoursEnd = "07:00",
    bool AutoMeetingMedia = true,
    bool AutoAiLimitAlert = true,
    bool AutoLowBatteryAlert = true,
    IReadOnlyList<string>? CalendarUrls = null,
    IReadOnlyList<string>? WorldClocks = null)
{
    public static readonly IReadOnlyList<string> DefaultMarketSymbols =
        ["XU100.IS", "USDTRY=X", "EURTRY=X", "GC=F", "BTC-USD"];

    public static readonly IReadOnlyList<string> AllBarWidgetIds =
        ["search", "weather", "claude", "hardware", "pomodoro", "media", "markets", "status", "calendar", "worldclock"];

    public static readonly IReadOnlyList<string> DefaultBarWidgets =
        ["search", "weather", "status"];

    public static readonly IReadOnlyList<string> DefaultWorldClocks =
        ["UTC", "America/New_York", "Asia/Tokyo"];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveMarketSymbols =>
        MarketSymbols is { Count: > 0 } ? MarketSymbols : DefaultMarketSymbols;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveBarWidgets =>
        BarWidgets ?? DefaultBarWidgets;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveWorldClocks =>
        WorldClocks is { Count: > 0 } ? WorldClocks : DefaultWorldClocks;

    /// <summary>The palette (text and surface colors) that the selected bar style requires.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AppTheme EffectiveTheme => Style switch
    {
        BarStyle.White => AppTheme.Light,
        BarStyle.Glass => Theme,
        _ => AppTheme.Dark
    };

    public bool HasBarWidget(string id) =>
        EffectiveBarWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);

    public bool Equals(DockSettings? other) =>
        other is not null
        && Position == other.Position
        && SequenceEquals(PinOrder, other.PinOrder)
        && Theme == other.Theme
        && Language == other.Language
        && AnimationsEnabled == other.AnimationsEnabled
        && PreviewsEnabled == other.PreviewsEnabled
        && NotificationBadgesEnabled == other.NotificationBadgesEnabled
        && AutoHide == other.AutoHide
        && SurfaceOpacity.Equals(other.SurfaceOpacity)
        && string.Equals(WeatherCity ?? "", other.WeatherCity ?? "", StringComparison.Ordinal)
        && SequenceEquals(MarketSymbols, other.MarketSymbols)
        && SearchHotkeyEnabled == other.SearchHotkeyEnabled
        && SequenceEquals(EffectiveBarWidgets, other.EffectiveBarWidgets)
        && Edge == other.Edge
        && Layout == other.Layout
        && Style == other.Style
        && QuietHoursEnabled == other.QuietHoursEnabled
        && string.Equals(QuietHoursStart, other.QuietHoursStart, StringComparison.Ordinal)
        && string.Equals(QuietHoursEnd, other.QuietHoursEnd, StringComparison.Ordinal)
        && AutoMeetingMedia == other.AutoMeetingMedia
        && AutoAiLimitAlert == other.AutoAiLimitAlert
        && AutoLowBatteryAlert == other.AutoLowBatteryAlert
        && SequenceEquals(CalendarUrls, other.CalendarUrls)
        && SequenceEquals(WorldClocks, other.WorldClocks);

    public override int GetHashCode() => HashCode.Combine(Position, Theme, Language, AutoHide, SurfaceOpacity, WeatherCity, Edge, Style);

    private static bool SequenceEquals(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return (a?.Count ?? 0) == 0 && (b?.Count ?? 0) == 0;
        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }
}
