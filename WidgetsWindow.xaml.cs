using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenTaskbar.Services;
using OpenTaskbar.Services.Widgets;

namespace OpenTaskbar;

public partial class WidgetsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly CalendarService _calendar;
    private DateTime? _selectedDay;
    private string _worldClockKey = "";
    private bool _fromTop;
    private readonly AudioEndpointService _audio;
    private readonly MediaSessionService _media;
    private readonly SystemStatusService _systemStatus;
    private readonly OnlineFeedsService _onlineFeeds;
    private readonly ProductivityService _productivity;
    private readonly AiUsageService _aiUsage;
    private readonly BackgroundAppService _backgroundApps;
    private readonly Action _openSettings;
    private readonly Action _openNotifications;
    private readonly Action _openSystemTray;
    private readonly DispatcherTimer _telemetryTimer;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _lastClosedUtc;
    private bool _updatingVolumeSlider;
    private bool _updatingNoteBox;
    private bool _suppressDeactivate;

    public WidgetsWindow(
        ApplicationPreferences preferences,
        CalendarService calendar,
        AudioEndpointService audio,
        MediaSessionService media,
        SystemStatusService systemStatus,
        OnlineFeedsService onlineFeeds,
        ProductivityService productivity,
        AiUsageService aiUsage,
        BackgroundAppService backgroundApps,
        Action openSettings,
        Action openNotifications,
        Action openSystemTray)
    {
        _preferences = preferences;
        _calendar = calendar;
        _audio = audio;
        _media = media;
        _systemStatus = systemStatus;
        _onlineFeeds = onlineFeeds;
        _productivity = productivity;
        _aiUsage = aiUsage;
        _backgroundApps = backgroundApps;
        _openSettings = openSettings;
        _openNotifications = openNotifications;
        _openSystemTray = openSystemTray;

        InitializeComponent();
        _audio.Changed += OnAudioChanged;
        _media.Changed += OnMediaChanged;
        _productivity.PomodoroChanged += OnPomodoroChanged;
        _productivity.TimerChanged += OnToolTimerChanged;
        _calendar.Changed += OnCalendarChanged;

        SizeChanged += Window_SizeChanged;
        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _telemetryTimer.Tick += (_, _) => UpdateTelemetry();
    }

    public bool WasJustClosed => (DateTime.UtcNow - _lastClosedUtc).TotalMilliseconds < 220;

    public async void ToggleAtAnchor(Rect dockWorkArea, bool fromTop = false)
    {
        _fromTop = fromTop;
        if (IsVisible)
        {
            CloseDrawer();
            return;
        }
        if (WasJustClosed) return;

        HeaderDateText.Text = DateTime.Now.ToString("d MMMM yyyy, dddd", UiText.Culture);
        _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _selectedDay = null;
        RenderCalendar();
        RenderUpcomingEvents();
        RenderTimerPresets();
        UpdateToolTimerUi();
        _worldClockKey = "";
        RenderWorldClocks();
        _ = LoadRecentFilesAsync();
        _ = _calendar.RefreshAsync(_preferences.Current.CalendarUrls);
        _audio.Refresh();
        UpdateAudioUi();
        UpdateMediaUi();
        UpdatePomodoroUi();
        RenderBarWidgetPicker();
        RenderTodosAndNotes();
        UpdateTelemetry();
        PositionPanel(dockWorkArea);
        Show();
        Activate();
        _telemetryTimer.Start();
        RenderAiLimits(_aiUsage.Providers);
        _ = RefreshBackgroundAppsAsync();
        await LoadOnlineFeedsAsync(force: false);
    }

    public void CloseDrawer()
    {
        if (!IsVisible) return;
        _telemetryTimer.Stop();
        _lastClosedUtc = DateTime.UtcNow;
        Hide();
    }

    private Rect _anchorArea;

    // Online feeds and new cards change the content height after the panel is shown; keep it glued to its anchor edge.
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || _anchorArea.IsEmpty || _fromTop) return;
        Top = Math.Max(_anchorArea.Top + 8, _anchorArea.Bottom - ActualHeight - 6);
    }

    private void PositionPanel(Rect dockWorkArea)
    {
        UpdateLayout();
        var marginRight = 14.0;
        var area = dockWorkArea.Width > 0 ? dockWorkArea : SystemParameters.WorkArea;
        _anchorArea = area;
        MaxHeight = Math.Max(360, area.Height - 24);
        Left = Math.Max(area.Left + 8, area.Right - Width - marginRight);
        var panelHeight = Math.Min(ActualHeight > 0 ? ActualHeight : 640, MaxHeight);
        // A top-placed dock opens panels downward; a bottom dock opens them upward.
        Top = _fromTop ? area.Top + 2 : Math.Max(area.Top + 8, area.Bottom - panelHeight - 6);
    }

    private void UpdateTelemetry()
    {
        var net = _systemStatus.ReadNetwork();
        NetworkTitleText.Text = net.Name;
        NetworkSubtitleText.Text = net.IsConnected
            ? (net.IsWifi && net.SignalQuality > 0 ? UiText.Format("Signal", net.SignalQuality) : UiText.Get("Connected"))
            : UiText.Get("NotConnected");
        NetworkGlyph.Data = FindResource(net.IsConnected ? "IconWifi" : "IconWifiOff") as Geometry;

        var bat = _systemStatus.ReadBattery();
        if (bat.HasBattery)
        {
            BatteryTitleText.Text = $"{bat.Percent}%";
            if (bat.IsCharging) BatterySubtitleText.Text = UiText.Get("Charging");
            else if (bat.IsPluggedIn) BatterySubtitleText.Text = UiText.Get("PluggedIn");
            else if (bat.SecondsRemaining > 0)
            {
                var span = TimeSpan.FromSeconds(bat.SecondsRemaining);
                BatterySubtitleText.Text = UiText.Format("RemainingTime", (int)span.TotalHours, span.Minutes);
            }
            else BatterySubtitleText.Text = UiText.Get("OnBattery");
            BatteryGlyph.Data = FindResource(bat.IsCharging || bat.IsPluggedIn ? "IconBatteryCharging" : "IconBattery") as Geometry;
        }
        else
        {
            BatteryTitleText.Text = UiText.Get("Battery");
            BatterySubtitleText.Text = UiText.Get("PluggedIn");
            BatteryGlyph.Data = FindResource("IconBatteryCharging") as Geometry;
        }

        RenderWorldClocks();
        var perf = _systemStatus.ReadPerformance();
        CpuBar.Value = perf.CpuPercent;
        CpuValueText.Text = $"{perf.CpuPercent}%";
        MemoryBar.Value = perf.MemoryPercent;
        MemoryValueText.Text = $"{perf.MemoryUsedGb:F1}/{perf.MemoryTotalGb:F0} GB";
        DiskBar.Value = perf.DiskPercent;
        DiskValueText.Text = $"{perf.DiskFreeGb:F0} GB";
    }

    private void OnAudioChanged()
    {
        if (IsVisible) UpdateAudioUi();
    }

    private void UpdateAudioUi()
    {
        _updatingVolumeSlider = true;
        try
        {
            VolumeSliderControl.IsEnabled = _audio.IsAvailable;
            VolumeSliderControl.Value = _audio.VolumePercent;
            VolumePercentText.Text = _audio.IsMuted ? UiText.Get("Muted") : $"{_audio.VolumePercent}%";
            VolumeGlyph.Data = FindResource(_audio.IsMuted || _audio.VolumePercent == 0 ? "IconVolumeMute" : "IconVolume") as Geometry;
        }
        finally
        {
            _updatingVolumeSlider = false;
        }
    }

    private void OnMediaChanged()
    {
        if (IsVisible) UpdateMediaUi();
    }

    private void UpdateMediaUi()
    {
        var m = _media.Current;
        if (m.HasMedia)
        {
            MediaTitleText.Text = m.Title;
            MediaArtistText.Text = m.Artist;
            MediaArtImage.Source = m.Thumbnail;
            MediaArtImage.Visibility = m.Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
            MediaFallbackGlyph.Visibility = m.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
            MediaPlayPauseGlyph.Data = FindResource(m.IsPlaying ? "IconPause" : "IconPlay") as Geometry;
            MediaPrevButton.IsEnabled = true;
            MediaPlayPauseButton.IsEnabled = true;
            MediaNextButton.IsEnabled = true;
        }
        else
        {
            MediaTitleText.Text = UiText.Get("NothingPlaying");
            MediaArtistText.Text = "";
            MediaArtImage.Source = null;
            MediaArtImage.Visibility = Visibility.Collapsed;
            MediaFallbackGlyph.Visibility = Visibility.Visible;
            MediaPlayPauseGlyph.Data = FindResource("IconPlay") as Geometry;
            MediaPrevButton.IsEnabled = false;
            MediaPlayPauseButton.IsEnabled = false;
            MediaNextButton.IsEnabled = false;
        }
    }

    private async Task LoadOnlineFeedsAsync(bool force)
    {
        var aiTask = _aiUsage.RefreshAsync(force);
        var weatherTask = _onlineFeeds.GetWeatherAsync(_preferences.Current.WeatherCity, force);
        var marketsTask = _onlineFeeds.GetMarketsAsync(_preferences.Current.EffectiveMarketSymbols, force);
        var newsTask = _onlineFeeds.GetNewsAsync(_preferences.Current.Language, force);

        await Task.WhenAll(aiTask, weatherTask, marketsTask, newsTask);
        RenderAiLimits(await aiTask);
        RenderWeather(await weatherTask);
        RenderMarkets(await marketsTask);
        RenderNews(await newsTask);
    }

    private void RenderAiLimits(IReadOnlyList<AiProviderStatus> providers)
    {
        AiLimitsListPanel.Children.Clear();
        foreach (var p in providers)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 2, 0, 2)
            };
            box.SetResourceReference(Border.BackgroundProperty, "SurfaceSelected");

            var stack = new StackPanel();
            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock { Text = p.Name, FontSize = 12, FontWeight = FontWeights.SemiBold });
            var planBadge = new TextBlock
            {
                Text = $"  ·  {p.PlanName} ({p.TopModel})",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            planBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            titleStack.Children.Add(planBadge);
            Grid.SetColumn(titleStack, 0);
            topRow.Children.Add(titleStack);

            var resetTxt = new TextBlock
            {
                Text = p.ResetText,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            resetTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetColumn(resetTxt, 1);
            topRow.Children.Add(resetTxt);
            stack.Children.Add(topRow);

            var barRow = new Grid { Margin = new Thickness(0, 5, 0, 4) };
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = p.FiveHourPercent,
                Height = 5,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            bar.SetResourceReference(ProgressBar.BackgroundProperty, "StrokeSubtle");
            bar.Foreground = p.Name == "Claude"
                ? new SolidColorBrush(Color.FromRgb(229, 142, 90))
                : (Brush)FindResource("AccentBrush");
            Grid.SetColumn(bar, 0);
            barRow.Children.Add(bar);

            var pctLbl = new TextBlock
            {
                Text = $"{p.FiveHourPercent}%",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctLbl, 1);
            barRow.Children.Add(pctLbl);
            stack.Children.Add(barRow);

            var subRow = new Grid();
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fhTxt = new TextBlock { Text = p.FiveHourDetail, FontSize = 10.5 };
            fhTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(fhTxt, 0);
            subRow.Children.Add(fhTxt);

            var wkTxt = new TextBlock { Text = p.WeeklyDetail, FontSize = 10.5 };
            wkTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(wkTxt, 1);
            subRow.Children.Add(wkTxt);
            stack.Children.Add(subRow);

            box.Child = stack;
            AiLimitsListPanel.Children.Add(box);
        }
    }

    private async Task RefreshBackgroundAppsAsync()
    {
        var (groups, bin) = await Task.Run(() =>
            (_backgroundApps.GetRunningAppGroups(7), BackgroundAppService.ReadRecycleBin()));
        RenderBackgroundAppsAndScreenTime(groups, bin);
    }

    private void RenderBackgroundAppsAndScreenTime(IReadOnlyList<AppResourceGroup> groups, RecycleBinInfo bin)
    {
        BackgroundAppsListPanel.Children.Clear();
        foreach (var group in groups)
        {
            var captured = group;
            var row = new Grid { Margin = new Thickness(0, 2.5, 0, 2.5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftStack.Children.Add(new TextBlock
            {
                Text = captured.Name,
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var meta = new TextBlock
            {
                Text = captured.IsBackground
                    ? $"  ({captured.ProcessCount}) · {UiText.Get("BackgroundTag")}"
                    : $"  ({captured.ProcessCount})",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            leftStack.Children.Add(meta);
            Grid.SetColumn(leftStack, 0);
            row.Children.Add(leftStack);

            var memTxt = new TextBlock
            {
                Text = $"{captured.MemoryMb:F0} MB",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0)
            };
            memTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(memTxt, 1);
            row.Children.Add(memTxt);

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (captured.MainWindowHandle != 0)
            {
                var focusBtn = new Button
                {
                    Content = UiText.Get("BringToFront"),
                    Style = (Style)FindResource("WidgetActionPill"),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                focusBtn.Click += (_, _) =>
                {
                    CloseDrawer();
                    NativeWindowTracker.Activate(captured.MainWindowHandle);
                };
                btnStack.Children.Add(focusBtn);
            }

            var killBtn = new Button
            {
                Content = UiText.Get("EndTask"),
                Style = (Style)FindResource("WidgetActionPill")
            };
            killBtn.SetResourceReference(ForegroundProperty, "DangerText");
            killBtn.Click += async (_, _) =>
            {
                if (await Task.Run(() => BackgroundAppService.KillAppGroup(captured)))
                {
                    await RefreshBackgroundAppsAsync();
                }
            };
            btnStack.Children.Add(killBtn);
            Grid.SetColumn(btnStack, 2);
            row.Children.Add(btnStack);

            BackgroundAppsListPanel.Children.Add(row);
        }

        // Screen Time
        var totalSec = _backgroundApps.TotalScreenSecondsToday;
        TotalScreenTimeText.Text = FormatDuration(totalSec);
        ScreenTimeListPanel.Children.Clear();
        var topApps = _backgroundApps.GetTopScreenTimeApps(4);
        var maxSec = Math.Max(1, topApps.FirstOrDefault()?.SecondsToday ?? 1);
        foreach (var entry in topApps)
        {
            var r = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var appLbl = new TextBlock { Text = entry.AppName, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(appLbl, 0);
            r.Children.Add(appLbl);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = maxSec,
                Value = entry.SecondsToday,
                Height = 5,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(6, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            bar.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
            bar.SetResourceReference(ProgressBar.BackgroundProperty, "StrokeSubtle");
            Grid.SetColumn(bar, 1);
            r.Children.Add(bar);

            var durLbl = new TextBlock { Text = FormatDuration(entry.SecondsToday), FontSize = 10.5, FontWeight = FontWeights.SemiBold };
            durLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(durLbl, 2);
            r.Children.Add(durLbl);

            ScreenTimeListPanel.Children.Add(r);
        }

        // Recycle Bin
        RecycleBinStatusText.Text = UiText.IsEnglish
            ? $"{UiText.Get("RecycleBin")} · {bin.ItemCount} items ({bin.SizeMb:F1} MB)"
            : $"{UiText.Get("RecycleBin")} · {bin.ItemCount} öğe ({bin.SizeMb:F1} MB)";
        EmptyRecycleBinBtn.IsEnabled = bin.ItemCount > 0;
    }

    private static string FormatDuration(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1)
            return UiText.IsEnglish ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalHours} sa {span.Minutes} dk";
        if (span.TotalMinutes >= 1)
            return UiText.IsEnglish ? $"{span.Minutes}m" : $"{span.Minutes} dk";
        return UiText.IsEnglish ? $"{span.Seconds}s" : $"{span.Seconds} sn";
    }

    private void RenderWeather(WeatherData? wx)
    {
        if (wx is null)
        {
            WeatherCityText.Text = UiText.Get("Weather");
            WeatherTempText.Text = "--°";
            WeatherConditionText.Text = UiText.Get("WeatherUnavailable");
            WeatherHighLowText.Text = "";
            WeatherDetailsText.Text = "";
            return;
        }

        WeatherCityText.Text = wx.City;
        WeatherTempText.Text = $"{Math.Round(wx.Temperature):0}°";
        WeatherConditionText.Text = wx.ConditionText;
        WeatherHighLowText.Text = UiText.Format("HighLow", $"{Math.Round(wx.High):0}°", $"{Math.Round(wx.Low):0}°");
        WeatherDetailsText.Text = $"{UiText.Format("FeelsLike", $"{Math.Round(wx.FeelsLike):0}°")} · {UiText.Get("Humidity")} %{wx.Humidity} · {Math.Round(wx.WindSpeed):0} km/h";
        WeatherGlyph.Data = FindResource(wx.GlyphKey) as Geometry;
    }

    private void RenderMarkets(IReadOnlyList<MarketQuote> quotes)
    {
        MarketsListPanel.Children.Clear();
        if (quotes.Count == 0)
        {
            MarketsStatusText.Text = UiText.Get("MarketsUnavailable");
            MarketsStatusText.Visibility = Visibility.Visible;
            return;
        }

        MarketsStatusText.Visibility = Visibility.Collapsed;
        foreach (var q in quotes)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            namePanel.Children.Add(new TextBlock { Text = q.DisplayName, FontSize = 12, FontWeight = FontWeights.Medium });
            namePanel.Children.Add(new TextBlock
            {
                Text = "  " + q.Symbol,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            ((TextBlock)namePanel.Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(namePanel, 0);
            row.Children.Add(namePanel);

            var priceText = new TextBlock
            {
                Text = FormatMarketPrice(q.Price),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 8, 0)
            };
            Grid.SetColumn(priceText, 1);
            row.Children.Add(priceText);

            var positive = q.ChangePercent >= 0;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            badge.SetResourceReference(Border.BackgroundProperty, positive ? "PositiveSurface" : "NegativeSurface");
            var pctText = new TextBlock
            {
                Text = $"{(positive ? "+" : "")}{q.ChangePercent:F2}%",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            pctText.SetResourceReference(TextBlock.ForegroundProperty, positive ? "PositiveBrush" : "NegativeBrush");
            badge.Child = pctText;
            Grid.SetColumn(badge, 2);
            row.Children.Add(badge);

            MarketsListPanel.Children.Add(row);
        }
    }

    private void RenderNews(IReadOnlyList<NewsItem> items)
    {
        NewsListPanel.Children.Clear();
        if (items.Count == 0)
        {
            NewsStatusText.Text = UiText.Get("NewsUnavailable");
            NewsStatusText.Visibility = Visibility.Visible;
            return;
        }

        NewsStatusText.Visibility = Visibility.Collapsed;
        foreach (var item in items)
        {
            var captured = item;
            var btn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(-6, 1, -6, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            btn.SetResourceReference(ForegroundProperty, "TextPrimary");

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "B";
            borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(6, 5, 6, 5));
            borderFactory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = borderFactory;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("SurfaceHover"), "B"));
            template.Triggers.Add(hover);
            btn.Template = template;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 36,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var meta = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Source)
                    ? FormatRelativeTime(item.PublishedAt)
                    : $"{item.Source}  ·  {FormatRelativeTime(item.PublishedAt)}",
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(meta);
            btn.Content = stack;
            btn.Click += (_, _) =>
            {
                CloseDrawer();
                OpenUri(captured.Link);
            };
            NewsListPanel.Children.Add(btn);
        }
    }

    private void RenderCalendar()
    {
        var culture = UiText.Culture;
        CalendarMonthText.Text = culture.TextInfo.ToTitleCase(_calendarMonth.ToString("MMMM yyyy", culture));

        CalendarWeekdays.Children.Clear();
        var dayNames = culture.DateTimeFormat.AbbreviatedDayNames;
        // Monday-first order
        int[] order = [1, 2, 3, 4, 5, 6, 0];
        foreach (var d in order)
        {
            var lbl = new TextBlock
            {
                Text = dayNames[d][..Math.Min(2, dayNames[d].Length)],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            lbl.SetResourceReference(ForegroundProperty, "TextSecondary");
            CalendarWeekdays.Children.Add(lbl);
        }

        CalendarDaysGrid.Children.Clear();
        var firstDayOfWeek = ((int)_calendarMonth.DayOfWeek + 6) % 7; // Monday = 0
        var daysInMonth = DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month);
        var today = DateTime.Today;

        for (var i = 0; i < 42; i++)
        {
            var dayNum = i - firstDayOfWeek + 1;
            if (dayNum < 1 || dayNum > daysInMonth)
            {
                CalendarDaysGrid.Children.Add(new Border { Height = 22 });
                continue;
            }

            var isToday = _calendarMonth.Year == today.Year && _calendarMonth.Month == today.Month && dayNum == today.Day;
            var date = new DateTime(_calendarMonth.Year, _calendarMonth.Month, dayNum);
            var isSelected = _selectedDay?.Date == date;
            var cell = new Border
            {
                Width = 24,
                Height = 22,
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            if (isToday) cell.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else if (isSelected) cell.SetResourceReference(Border.BackgroundProperty, "SurfacePressed");
            cell.MouseLeftButtonUp += (_, _) =>
            {
                _selectedDay = _selectedDay?.Date == date ? null : date;
                RenderCalendar();
                RenderUpcomingEvents();
            };

            var txt = new TextBlock
            {
                Text = dayNum.ToString(CultureInfo.InvariantCulture),
                FontSize = 10.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            txt.SetResourceReference(ForegroundProperty, isToday ? "AccentText" : "TextPrimary");
            var cellGrid = new Grid();
            cellGrid.Children.Add(txt);
            if (_calendar.HasEventsOn(date))
            {
                var dot = new Ellipse
                {
                    Width = 4, Height = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 1)
                };
                dot.SetResourceReference(Shape.FillProperty, isToday ? "AccentText" : "AccentBrush");
                cellGrid.Children.Add(dot);
            }
            cell.Child = cellGrid;
            CalendarDaysGrid.Children.Add(cell);
        }
    }

    private static string FormatMarketPrice(double price)
    {
        var culture = UiText.Culture;
        return price >= 1000 ? price.ToString("N0", culture) : price.ToString("N2", culture);
    }

    private static string FormatRelativeTime(DateTimeOffset time)
    {
        if (time == default) return "";
        var diff = DateTimeOffset.UtcNow - time.ToUniversalTime();
        if (diff.TotalMinutes < 2) return UiText.Get("JustNow");
        if (diff.TotalHours < 1) return UiText.Format("MinutesAgo", (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return UiText.Format("HoursAgo", (int)diff.TotalHours);
        return UiText.Format("DaysAgo", (int)diff.TotalDays);
    }

    private void OnPomodoroChanged()
    {
        if (IsVisible) UpdatePomodoroUi();
    }

    private void UpdatePomodoroUi()
    {
        PomodoroModeLabel.Text = _productivity.IsBreak ? UiText.Get("ShortBreak") : UiText.Get("FocusSession");
        PomodoroTimeText.Text = _productivity.FormattedTime;
    }

    private void RenderBarWidgetPicker()
    {
        BarWidgetPickerPanel.Children.Clear();
        (string Id, string LabelKey)[] items =
        [
            ("search", "WidgetSearch"),
            ("weather", "WidgetWeather"),
            ("claude", "WidgetClaude"),
            ("hardware", "WidgetHardware"),
            ("pomodoro", "WidgetPomodoro"),
            ("media", "WidgetMedia"),
            ("markets", "WidgetMarkets"),
            ("status", "WidgetStatus"),
            ("calendar", "WidgetCalendar"),
            ("worldclock", "WidgetWorldClock")
        ];

        foreach (var (id, labelKey) in items)
        {
            var capturedId = id;
            var active = _preferences.Current.HasBarWidget(capturedId);
            var btn = new Button
            {
                Content = $"{(active ? "✓ " : "+ ")}{UiText.Get(labelKey)}",
                Style = (Style)FindResource("WidgetActionPill"),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(9, 4, 9, 4)
            };
            if (active)
            {
                btn.SetResourceReference(BackgroundProperty, "AccentBrush");
                btn.SetResourceReference(ForegroundProperty, "AccentText");
            }
            btn.Click += (_, _) =>
            {
                var list = _preferences.Current.EffectiveBarWidgets.ToList();
                var idx = list.FindIndex(x => string.Equals(x, capturedId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) list.RemoveAt(idx);
                else list.Add(capturedId);
                _ = _preferences.Save(_preferences.Current with { BarWidgets = list });
                RenderBarWidgetPicker();
            };
            BarWidgetPickerPanel.Children.Add(btn);
        }
    }

    private void RenderTodosAndNotes()
    {
        _updatingNoteBox = true;
        try
        {
            QuickNoteBox.Text = _productivity.QuickNote;
        }
        finally
        {
            _updatingNoteBox = false;
        }

        TodosListPanel.Children.Clear();
        foreach (var item in _productivity.Todos.Take(6))
        {
            var captured = item;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                IsChecked = captured.Done,
                Content = new TextBlock
                {
                    Text = captured.Text,
                    FontSize = 11.5,
                    TextDecorations = captured.Done ? TextDecorations.Strikethrough : null,
                    Opacity = captured.Done ? 0.6 : 1.0,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            cb.SetResourceReference(ForegroundProperty, "TextPrimary");
            cb.Click += (_, _) =>
            {
                _productivity.ToggleTodo(captured.Id);
                RenderTodosAndNotes();
            };
            Grid.SetColumn(cb, 0);
            row.Children.Add(cb);

            var delBtn = new Button
            {
                Content = "×",
                Width = 20,
                Height = 20,
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            delBtn.SetResourceReference(ForegroundProperty, "TextSecondary");
            delBtn.Click += (_, _) =>
            {
                _productivity.RemoveTodo(captured.Id);
                RenderTodosAndNotes();
            };
            Grid.SetColumn(delBtn, 1);
            row.Children.Add(delBtn);

            TodosListPanel.Children.Add(row);
        }
    }

    private async void CheckAiLimits_Click(object sender, RoutedEventArgs e)
    {
        RenderAiLimits(await _aiUsage.RefreshAsync(force: true));
    }

    private void SystemTrayIcons_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        _openSystemTray();
    }

    private void OpenRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = false })?.Dispose(); } catch { }
    }

    private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(BackgroundAppService.EmptyRecycleBin);
        await RefreshBackgroundAppsAsync();
    }

    // ---- Upcoming events -------------------------------------------------------------------------------

    private void OnCalendarChanged()
    {
        if (!IsVisible) return;
        RenderCalendar();
        RenderUpcomingEvents();
    }

    private void RenderUpcomingEvents()
    {
        UpcomingEventsPanel.Children.Clear();
        var culture = UiText.Culture;
        IReadOnlyList<CalendarEvent> events;
        if (_selectedDay is { } day)
        {
            UpcomingTitleText.Text = day.ToString("d MMMM, dddd", culture);
            events = _calendar.ForDay(day);
        }
        else
        {
            UpcomingTitleText.Text = UiText.Get("Upcoming");
            events = _calendar.Upcoming(DateTime.Now, 5);
        }

        if (events.Count == 0)
        {
            UpcomingStatusText.Visibility = Visibility.Visible;
            UpcomingStatusText.Text = !_calendar.HasFeeds ? UiText.Get("ConnectCalendar")
                : _calendar.LastError is not null ? UiText.Get("CalendarFetchError")
                : UiText.Get("NoUpcoming");
            return;
        }

        UpcomingStatusText.Visibility = Visibility.Collapsed;
        foreach (var item in events)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var when = item.AllDay ? UiText.Get("AllDay")
                : item.Start.Date == DateTime.Today || _selectedDay is not null ? item.Start.ToString("HH:mm", culture)
                : item.Start.ToString("ddd HH:mm", culture);
            var time = new TextBlock { Text = when, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
            time.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            row.Children.Add(time);

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = item.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            if (item.Location is not null)
            {
                var loc = new TextBlock { Text = item.Location, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
                loc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                stack.Children.Add(loc);
            }
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);
            UpcomingEventsPanel.Children.Add(row);
        }
    }

    // ---- World clocks ----------------------------------------------------------------------------------

    private void RenderWorldClocks()
    {
        var entries = WorldClockService.Read(_preferences.Current.EffectiveWorldClocks);
        var key = string.Join('|', entries.Select(e => $"{e.City}{e.Time}"));
        if (key == _worldClockKey) return;
        _worldClockKey = key;
        WorldClocksPanel.Children.Clear();
        WorldClocksPanel.Columns = Math.Clamp(entries.Count, 1, 3);
        foreach (var entry in entries)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 4) };
            var city = new TextBlock { Text = entry.City, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            city.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(city);
            stack.Children.Add(new TextBlock { Text = entry.Time, FontSize = 19, FontWeight = FontWeights.SemiBold });
            var meta = new TextBlock { Text = $"{entry.DayLabel} · {entry.Offset}", FontSize = 10 };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(meta);
            WorldClocksPanel.Children.Add(stack);
        }
    }

    // ---- Timer & stopwatch -----------------------------------------------------------------------------

    private void OnToolTimerChanged()
    {
        if (IsVisible) UpdateToolTimerUi();
    }

    private void UpdateToolTimerUi()
    {
        TimerTimeText.Text = ProductivityService.FormatSpan(_productivity.TimerRemaining);
        TimerToggleBtn.Content = UiText.Get(_productivity.IsTimerRunning ? "Pause" : "Run");
        StopwatchTimeText.Text = ProductivityService.FormatSpan(_productivity.StopwatchElapsed, tenths: true);
        StopwatchToggleBtn.Content = UiText.Get(_productivity.IsStopwatchRunning ? "Pause" : "Run");
    }

    private void RenderTimerPresets()
    {
        TimerPresetPanel.Children.Clear();
        foreach (var minutes in new[] { 1, 5, 10, 15, 25, 45 })
        {
            var captured = minutes;
            var active = _productivity.TimerPresetMinutes == captured;
            var btn = new Button
            {
                Content = UiText.Format("TimerMinutes", captured),
                Style = (Style)FindResource("WidgetActionPill"),
                Margin = new Thickness(0, 0, 5, 0)
            };
            if (active)
            {
                btn.SetResourceReference(BackgroundProperty, "AccentBrush");
                btn.SetResourceReference(ForegroundProperty, "AccentText");
            }
            btn.Click += (_, _) =>
            {
                _productivity.SetTimerPreset(captured);
                RenderTimerPresets();
            };
            TimerPresetPanel.Children.Add(btn);
        }
    }

    private void TimerToggle_Click(object sender, RoutedEventArgs e) => _productivity.ToggleTimer();
    private void TimerReset_Click(object sender, RoutedEventArgs e) => _productivity.ResetTimer();
    private void StopwatchToggle_Click(object sender, RoutedEventArgs e) => _productivity.ToggleStopwatch();
    private void StopwatchReset_Click(object sender, RoutedEventArgs e) => _productivity.ResetStopwatch();

    // ---- Recent files & quick settings -----------------------------------------------------------------

    private static readonly string DownloadsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private async Task LoadRecentFilesAsync()
    {
        var files = await Task.Run(() =>
        {
            var roots = new[]
            {
                DownloadsPath,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            var found = new List<FileInfo>();
            foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    found.AddRange(new DirectoryInfo(root).EnumerateFiles()
                        .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) == 0
                                    && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                                    && !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
                                    && !f.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return found.OrderByDescending(f => f.LastWriteTimeUtc).Take(5).ToList();
        });

        RecentFilesPanel.Children.Clear();
        RecentFilesStatusText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var file in files)
        {
            var captured = file;
            var row = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(-6, 1, -6, 1),
                Cursor = Cursors.Hand,
                ToolTip = captured.FullName
            };
            row.SetResourceReference(ForegroundProperty, "TextPrimary");
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)) { Name = "B" };
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = border;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("SurfaceHover"), "B"));
            template.Triggers.Add(hover);
            row.Template = template;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = captured.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var age = new TextBlock
            {
                Text = FormatRelativeTime(new DateTimeOffset(captured.LastWriteTimeUtc, TimeSpan.Zero)),
                FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            age.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(age, 1);
            grid.Children.Add(age);
            row.Content = grid;
            row.Click += (_, _) => { CloseDrawer(); OpenUri(captured.FullName); };
            RecentFilesPanel.Children.Add(row);
        }
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        OpenUri(Directory.Exists(DownloadsPath) ? DownloadsPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private void QuickSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;
        CloseDrawer();
        OpenUri(uri);
    }

    private void PomodoroToggle_Click(object sender, RoutedEventArgs e) => _productivity.TogglePomodoro();
    private void PomodoroReset_Click(object sender, RoutedEventArgs e) => _productivity.ResetPomodoro();

    private void AddTodo_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TodoInputBox.Text)) return;
        _productivity.AddTodo(TodoInputBox.Text);
        TodoInputBox.Text = "";
        RenderTodosAndNotes();
    }

    private void TodoInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTodo_Click(sender, e);
    }

    private void QuickNoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingNoteBox) return;
        _productivity.UpdateQuickNote(QuickNoteBox.Text);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider) return;
        _audio.SetVolumePercent((int)Math.Round(e.NewValue));
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => _audio.ToggleMute();
    private void SoundSettings_Click(object sender, RoutedEventArgs e) { CloseDrawer(); OpenUri("ms-settings:sound"); }
    private void NetworkCard_Click(object sender, RoutedEventArgs e) { CloseDrawer(); OpenUri("ms-settings:network-wifi"); }
    private void BatteryCard_Click(object sender, RoutedEventArgs e) { CloseDrawer(); OpenUri("ms-settings:powersleep"); }
    private void TaskManager_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })?.Dispose(); } catch { }
    }

    private async void MediaPrev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void MediaNext_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        RenderCalendar();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        UpdateTelemetry();
        await _media.RefreshAsync();
        await LoadOnlineFeedsAsync(force: true);
    }

    private void OpenNotifications_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        _openNotifications();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        CloseDrawer();
        _openSettings();
        _suppressDeactivate = false;
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseDrawer();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_suppressDeactivate) CloseDrawer();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            CloseDrawer();
            return;
        }

        _telemetryTimer.Stop();
        _audio.Changed -= OnAudioChanged;
        _media.Changed -= OnMediaChanged;
        _productivity.PomodoroChanged -= OnPomodoroChanged;
        _productivity.TimerChanged -= OnToolTimerChanged;
        _calendar.Changed -= OnCalendarChanged;
        base.OnClosing(e);
    }
}
