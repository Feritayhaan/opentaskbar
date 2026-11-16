using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services.Widgets;

/// <summary>
/// Smart automations: widgets share data so the bar can act before you need to check something.
/// Each automation has a single on/off switch in Settings → Widgets.
/// </summary>
public sealed class AutomationService
{
    private readonly Func<DockSettings> _settings;
    private readonly CalendarService _calendar;
    private readonly AiUsageService _aiUsage;
    private readonly MediaSessionService _media;
    private readonly SystemStatusService _status;
    private readonly AlertCenter _alerts;
    private readonly Action _openWidgets;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _handledMeetings = [];
    private readonly HashSet<string> _alertedAiProviders = [];
    private bool _lowBatteryAlerted;

    public AutomationService(
        Dispatcher dispatcher,
        Func<DockSettings> settings,
        CalendarService calendar,
        AiUsageService aiUsage,
        MediaSessionService media,
        SystemStatusService status,
        AlertCenter alerts,
        Action openWidgets)
    {
        _settings = settings;
        _calendar = calendar;
        _aiUsage = aiUsage;
        _media = media;
        _status = status;
        _alerts = alerts;
        _openWidgets = openWidgets;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public async Task TickAsync()
    {
        var settings = _settings();
        if (settings.AutoMeetingMedia) await CheckMeetingsAsync();
        if (settings.AutoAiLimitAlert) CheckAiLimits();
        if (settings.AutoLowBatteryAlert) CheckBattery();
    }

    private async Task CheckMeetingsAsync()
    {
        var now = DateTime.Now;
        foreach (var meeting in _calendar.Events)
        {
            if (meeting.AllDay) continue;
            var until = meeting.Start - now;
            if (until < TimeSpan.Zero || until > TimeSpan.FromMinutes(2)) continue;
            var key = $"{meeting.Title}|{meeting.Start:O}";
            if (!_handledMeetings.Add(key)) continue;

            var paused = await _media.PauseIfPlayingAsync();
            var minutes = Math.Max(1, (int)Math.Ceiling(until.TotalMinutes));
            _alerts.Notify(
                UiText.Format("MeetingStartsSoon", meeting.Title, minutes),
                paused ? UiText.Get("MeetingMusicPaused") : (meeting.Location ?? meeting.Start.ToString("HH:mm", UiText.Culture)),
                "IconTimer", _openWidgets, sound: true);
        }
        if (_handledMeetings.Count > 200) _handledMeetings.Clear();
    }

    private void CheckAiLimits()
    {
        foreach (var provider in _aiUsage.Providers)
        {
            if (!provider.IsConnected) continue;
            if (provider.FiveHourPercent >= 90)
            {
                if (_alertedAiProviders.Add(provider.Name))
                {
                    _alerts.Notify(
                        UiText.Format("AiLimitTitle", provider.Name, provider.FiveHourPercent),
                        $"{provider.FiveHourDetail} · {provider.ResetText}",
                        "IconSparkles", _openWidgets, sound: true);
                }
            }
            else if (provider.FiveHourPercent < 70)
            {
                // The window reset: allow the next warning.
                _alertedAiProviders.Remove(provider.Name);
            }
        }
    }

    private void CheckBattery()
    {
        var battery = _status.ReadBattery();
        if (!battery.HasBattery) return;
        if (battery.Percent <= 15 && !battery.IsCharging && !battery.IsPluggedIn)
        {
            if (_lowBatteryAlerted) return;
            _lowBatteryAlerted = true;
            _alerts.Notify(UiText.Format("LowBatteryTitle", battery.Percent), UiText.Get("LowBatteryMessage"),
                "IconBattery", _openWidgets, sound: true);
        }
        else if (battery.Percent > 25 || battery.IsCharging || battery.IsPluggedIn)
        {
            _lowBatteryAlerted = false;
        }
    }
}
