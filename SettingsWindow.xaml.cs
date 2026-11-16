using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Services.Widgets;

namespace OpenTaskbar;

public partial class SettingsWindow : Window
{
    private readonly ApplicationPreferences _preferences;
    private readonly StartupRegistration _startup = new();
    private bool _startupEnabled;
    private bool _startupAvailable;
    private DockSettings _applied;
    private bool _closingApplication;
    private bool _initializing = true;
    private bool _sourceReady;
    private HwndSource? _source;

    public SettingsWindow(ApplicationPreferences preferences)
    {
        _preferences = preferences;
        _applied = preferences.Current;
        InitializeComponent();
        _preferences.Changed += Preferences_Changed;
        Closed += (_, _) =>
        {
            _preferences.Changed -= Preferences_Changed;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WindowProc);
            ThemeManager.SetSurfaceOpacity(_preferences.Current.SurfaceOpacity);
        };
        DarkTheme.IsChecked = _applied.Theme == AppTheme.Dark;
        LightTheme.IsChecked = _applied.Theme == AppTheme.Light;
        TurkishLanguage.IsChecked = _applied.Language == AppLanguage.Turkish;
        EnglishLanguage.IsChecked = _applied.Language == AppLanguage.English;
        LeftPosition.IsChecked = _applied.Position == DockPosition.Left;
        CenterPosition.IsChecked = _applied.Position == DockPosition.Center;
        RightPosition.IsChecked = _applied.Position == DockPosition.Right;
        EdgeBottomRadio.IsChecked = _applied.Edge == DockEdge.Bottom;
        EdgeTopRadio.IsChecked = _applied.Edge == DockEdge.Top;
        LayoutPillRadio.IsChecked = _applied.Layout == DockLayout.Pill;
        LayoutFullRadio.IsChecked = _applied.Layout == DockLayout.FullWidth;
        StyleGlassRadio.IsChecked = _applied.Style == BarStyle.Glass;
        StyleWhiteRadio.IsChecked = _applied.Style == BarStyle.White;
        StyleIosRadio.IsChecked = _applied.Style == BarStyle.IosGlass;
        StyleMidnightRadio.IsChecked = _applied.Style == BarStyle.Midnight;
        StyleBlackRadio.IsChecked = _applied.Style == BarStyle.Black;
        AutoMeetingToggle.IsChecked = _applied.AutoMeetingMedia;
        AutoAiToggle.IsChecked = _applied.AutoAiLimitAlert;
        AutoBatteryToggle.IsChecked = _applied.AutoLowBatteryAlert;
        QuietHoursToggle.IsChecked = _applied.QuietHoursEnabled;
        QuietStartBox.Text = _applied.QuietHoursStart;
        QuietEndBox.Text = _applied.QuietHoursEnd;
        CalendarUrlsBox.Text = string.Join(", ", _applied.CalendarUrls ?? []);
        WorldClocksBox.Text = string.Join(", ", _applied.WorldClocks ?? []);
        AnimationsToggle.IsChecked = _applied.AnimationsEnabled;
        PreviewsToggle.IsChecked = _applied.PreviewsEnabled;
        BadgesToggle.IsChecked = _applied.NotificationBadgesEnabled;
        AutoHideDisabled.IsChecked = _applied.AutoHide == AutoHideMode.Disabled;
        AutoHideSmart.IsChecked = _applied.AutoHide == AutoHideMode.Smart;
        AutoHideAlways.IsChecked = _applied.AutoHide == AutoHideMode.Always;
        SearchHotkeyToggle.IsChecked = _applied.SearchHotkeyEnabled;
        WeatherCityBox.Text = _applied.WeatherCity ?? "";
        MarketSymbolsBox.Text = string.Join(", ", _applied.EffectiveMarketSymbols);
        OpacitySlider.Value = _applied.SurfaceOpacity * 100;
        UpdateOpacityLabel();
        var startup = _startup.Read();
        _startupEnabled = startup.Enabled;
        _startupAvailable = startup.Error is null;
        StartupToggle.IsEnabled = _startupAvailable;
        if (startup.Error is not null)
        {
            StartupToggle.ToolTip = startup.Error;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartupToggle, true);
        }
        StartupToggle.IsChecked = _startupEnabled;
        var errors = new[] { preferences.LoadError, startup.Error }.OfType<string>();
        var message = string.Join(" ", errors.Select(UiText.LocalizeError));
        if (message.Length > 0) ShowResult(message, error: true);
        var workArea = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(PreferenceEdited));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(PreferenceEdited));
        _initializing = false;
        UpdateApplyState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProc);
        _sourceReady = true;
        UpdateBackdrop();
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == 0) return 0; // WM_GETMINMAXINFO
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            Trace.TraceWarning("Settings monitor work area unavailable: {0}", Marshal.GetLastWin32Error());
            return 0;
        }
        var sizing = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        sizing.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        sizing.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var dpi = GetDpiForWindow(window);
        var scale = dpi > 0 ? dpi / 96d : 1;
        sizing.MinTrackSize = new NativePoint((int)Math.Ceiling(MinWidth * scale), (int)Math.Ceiling(MinHeight * scale));
        Marshal.StructureToPtr(sizing, lParam, false);
        handled = true;
        return 0;
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        ThemeManager.SetSurfaceOpacity(Math.Round(OpacitySlider.Value) / 100);
        UpdateOpacityLabel();
        UpdateBackdrop();
    }

    private void UpdateBackdrop()
    {
        if (!_sourceReady) return;
        var glass = SettingsBackdrop.TryEnable(this, _preferences.Current.Theme);
        SettingsFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, glass ? "SettingsGlassMaterial" : "SettingsBackground");
        if (glass)
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(-1);
            SettingsFrame.ClearValue(System.Windows.Controls.Border.CornerRadiusProperty);
        }
        else
        {
            WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(0);
            SettingsFrame.CornerRadius = new CornerRadius(0);
        }
    }

    private DockSettings ReadSelection()
    {
        var city = WeatherCityBox.Text.Trim();
        var symbols = MarketSymbolsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var calendars = CalendarUrlsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        var clocks = WorldClocksBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(WorldClockService.IsKnownZone)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        var quietStart = QuietHours.TryParseTime(QuietStartBox.Text, out var qs) ? qs.ToString(@"hh\:mm") : _applied.QuietHoursStart;
        var quietEnd = QuietHours.TryParseTime(QuietEndBox.Text, out var qe) ? qe.ToString(@"hh\:mm") : _applied.QuietHoursEnd;
        return _preferences.Current with
        {
            Theme = LightTheme.IsChecked == true ? AppTheme.Light : AppTheme.Dark,
            Language = EnglishLanguage.IsChecked == true ? AppLanguage.English : AppLanguage.Turkish,
            Position = RightPosition.IsChecked == true ? DockPosition.Right : CenterPosition.IsChecked == true ? DockPosition.Center : DockPosition.Left,
            AnimationsEnabled = AnimationsToggle.IsChecked == true,
            PreviewsEnabled = PreviewsToggle.IsChecked == true,
            NotificationBadgesEnabled = BadgesToggle.IsChecked == true,
            AutoHide = AutoHideAlways.IsChecked == true ? AutoHideMode.Always : AutoHideSmart.IsChecked == true ? AutoHideMode.Smart : AutoHideMode.Disabled,
            SurfaceOpacity = Math.Round(OpacitySlider.Value) / 100,
            WeatherCity = string.IsNullOrEmpty(city) ? null : city,
            MarketSymbols = symbols.Count > 0 ? symbols : null,
            SearchHotkeyEnabled = SearchHotkeyToggle.IsChecked == true,
            Edge = EdgeTopRadio.IsChecked == true ? DockEdge.Top : DockEdge.Bottom,
            Layout = LayoutFullRadio.IsChecked == true ? DockLayout.FullWidth : DockLayout.Pill,
            Style = StyleWhiteRadio.IsChecked == true ? BarStyle.White
                : StyleIosRadio.IsChecked == true ? BarStyle.IosGlass
                : StyleMidnightRadio.IsChecked == true ? BarStyle.Midnight
                : StyleBlackRadio.IsChecked == true ? BarStyle.Black
                : BarStyle.Glass,
            AutoMeetingMedia = AutoMeetingToggle.IsChecked == true,
            AutoAiLimitAlert = AutoAiToggle.IsChecked == true,
            AutoLowBatteryAlert = AutoBatteryToggle.IsChecked == true,
            QuietHoursEnabled = QuietHoursToggle.IsChecked == true,
            QuietHoursStart = quietStart,
            QuietHoursEnd = quietEnd,
            CalendarUrls = calendars.Count > 0 ? calendars : null,
            WorldClocks = clocks.Count > 0 ? clocks : null
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!QuietHours.TryParseTime(QuietStartBox.Text, out _) || !QuietHours.TryParseTime(QuietEndBox.Text, out _))
        {
            ShowResult(UiText.Get("QuietHoursInvalid"), error: true);
            return;
        }
        var startupChanged = _startupAvailable && StartupToggle.IsChecked != _startupEnabled;
        if (startupChanged && _startup.SetEnabled(StartupToggle.IsChecked == true) is { } startupError)
        {
            ShowResult(startupError, error: true);
            return;
        }
        var settings = ReadSelection();
        if (_preferences.Save(settings) is { } error)
        {
            error = UiText.LocalizeError(error);
            if (startupChanged && _startup.SetEnabled(_startupEnabled) is not null)
                error += " " + UiText.Get("StartupRestoreError");
            ShowResult(error, error: true);
            return;
        }
        _applied = settings;
        _startupEnabled = StartupToggle.IsChecked == true;
        ShowResult(UiText.Get("SettingsSaved"), error: false);
        UpdateApplyState();
    }

    private void ShowResult(string message, bool error)
    {
        SaveStatus.Text = UiText.LocalizeError(message);
        SaveStatus.Visibility = Visibility.Visible;
        SaveStatus.SetResourceReference(ForegroundProperty, error ? "DangerText" : "TextSecondary");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingApplication || Application.Current.Dispatcher.HasShutdownStarted) return;
        if (!HasEdits()) return;
        e.Cancel = MessageBox.Show(this, UiText.Get("DiscardChanges"), UiText.Get("DiscardTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
    }

    private bool HasEdits()
    {
        var selected = ReadSelection();
        // Pin order may change behind this window and is not part of its edit buffer.
        return selected != (_applied with { PinOrder = selected.PinOrder })
            || (_startupAvailable && StartupToggle.IsChecked != _startupEnabled);
    }

    private void PreferenceEdited(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SaveStatus.Visibility = Visibility.Collapsed;
        UpdateApplyState();
    }

    private void WidgetField_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        PreferenceEdited(sender, e);

    private void UpdateApplyState() => ApplyButton.IsEnabled = HasEdits() || _preferences.LoadError is not null;

    private void UpdateOpacityLabel() => OpacityValue.Text = UiText.Format("OpacityPercent", (int)Math.Round(OpacitySlider.Value));

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateOpacityLabel();
        ThemeManager.SetSurfaceOpacity(Math.Round(e.NewValue) / 100);
        PreferenceEdited(sender, e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void CloseForShutdown()
    {
        _closingApplication = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

}
