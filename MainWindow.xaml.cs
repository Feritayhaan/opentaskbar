using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Services.Search;
using OpenTaskbar.Services.Widgets;

namespace OpenTaskbar;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WmHotkey = 0x0312;
    private const int SpotlightHotkeyId = 0x5350;
    private const uint ModAlt = 0x0001;
    private const uint ModNorepeat = 0x4000;
    private const uint VkSpace = 0x20;

    private readonly ObservableCollection<DockApp> _pinnedApps = [];
    private readonly ObservableCollection<DockApp> _runningApps = [];
    private readonly HashSet<string> _exitingRunningPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApplicationPreferences _preferences = ((App)Application.Current).Preferences;
    private SettingsWindow? _settingsWindow;
    private SpotlightWindow? _spotlightWindow;
    private WidgetsWindow? _widgetsWindow;
    private readonly SpotlightEngine _spotlightEngine;
    private readonly AudioEndpointService _audioService;
    private readonly MediaSessionService _mediaService;
    private readonly SystemStatusService _systemStatus = new();
    private readonly OnlineFeedsService _onlineFeeds = new();
    private readonly ProductivityService _productivity;
    private readonly AiUsageService _aiUsage = new();
    private readonly BackgroundAppService _backgroundApps = new();
    private readonly CalendarService _calendar = new();
    private AlertCenter _alerts = null!;
    private AutomationService _automation = null!;
    private double _iconOriginY = 0.95;
    private double _bounceDir = -1;
    private bool _edgeDragging;
    private bool _edgeDragMoved;
    private Point _edgeDragStartScreen;
    private bool _lastQuiet;
    private bool _hotkeyRegistered;
    private readonly ApplicationJumpListService _jumpLists = new();
    private CancellationTokenSource? _jumpListLoad;
    private long? _jumpListSession;
    private readonly DispatcherTimer _previewCloseTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _dockStatusTimer;
    private readonly DispatcherTimer _dockTelemetryTimer;
    private int _tickerIndex;
    private int _tickerSubTick;
    private readonly WindowThumbnailPreview _thumbnailPreview;
    private readonly Dictionary<string, nint> _lastSelectedWindows = new(StringComparer.OrdinalIgnoreCase);
    private DockPosition _dockPosition = DockPosition.Center;
    private NativeWindowTracker? _windowTracker;
    private TaskbarReplacementSession? _replacementSession;
    private HwndSource? _source;
    private nint _foregroundWindow;
    private readonly List<FrameworkElement> _magnifiedIcons = [];
    private ContextMenu? _openContextMenu;
    private bool _contextTargetWasForeground;
    private Size _dockArea;
    private nint? _foregroundAtAppPress;
    private uint _taskbarCreatedMessage;
    private Point _dragStartPoint;
    private DockApp? _draggedApp;
    private DockAutoHideController? _autoHideController;

    public MainWindow()
    {
        InitializeComponent();
        PinnedApps.ItemsSource = _pinnedApps;
        RunningApps.ItemsSource = _runningApps;
        _thumbnailPreview = new WindowThumbnailPreview((Style)FindResource("PreviewCloseButton"));
        _previewCloseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _previewCloseTimer.Tick += PreviewCloseTimer_Tick;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _audioService = new AudioEndpointService(Dispatcher);
        _audioService.Changed += UpdateDockStatusIcons;
        _mediaService = new MediaSessionService(Dispatcher);
        _mediaService.Changed += UpdateDockMediaCapsule;
        _productivity = new ProductivityService(Dispatcher);
        _productivity.PomodoroChanged += UpdateDockPomodoroCapsule;
        _alerts = new AlertCenter(Dispatcher, () => _preferences.Current,
            () => (GetWidgetsAnchorArea(), _preferences.Current.Edge == DockEdge.Top));
        _productivity.Alert = (title, message) => _alerts.Notify(title, message, "IconTimer", ToggleWidgetsDrawer, sound: true);
        _calendar.Changed += UpdateDockCalendarCapsule;
        _automation = new AutomationService(Dispatcher, () => _preferences.Current, _calendar, _aiUsage,
            _mediaService, _systemStatus, _alerts, ToggleWidgetsDrawer);
        _automation.Start();
        DockSurface.MouseLeftButtonDown += DockSurface_EdgeDragStart;
        DockSurface.MouseMove += DockSurface_EdgeDragMove;
        DockSurface.MouseLeftButtonUp += DockSurface_EdgeDragEnd;
        DockSurface.LostMouseCapture += (_, _) => EndEdgeDrag();
        _spotlightEngine = new SpotlightEngine(Dispatcher, new SystemCommandProvider.Actions(
            OpenSettings: () => OpenSettings_Click(this, new RoutedEventArgs()),
            OpenWidgets: ToggleWidgetsDrawer,
            ShowDesktop: () => SendWinKeyCombo(0x44)));

        _dockTelemetryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _dockTelemetryTimer.Tick += (_, _) =>
        {
            _backgroundApps.RecordForegroundTick(3);
            UpdateDockTelemetryCapsule();
            if (++_tickerSubTick >= 2)
            {
                _tickerSubTick = 0;
                AdvanceMarketTicker(1);
            }
        };
        _dockTelemetryTimer.Start();

        _dockStatusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _dockStatusTimer.Tick += async (_, _) => await RefreshDockStatusAsync();
        _dockStatusTimer.Start();
        UpdateDockStatusIcons();
        UpdateDockTelemetryCapsule();
        UpdateDockMediaCapsule();
        ApplyBarWidgetsVisibility();
        ApplyEdgeAndLayout();
        _ = InitializeWidgetsBackgroundAsync();
        _thumbnailPreview.PointerEntered += ThumbnailPreview_PointerEntered;
        _thumbnailPreview.PointerLeft += ThumbnailPreview_PointerLeft;
        _thumbnailPreview.PreviewClicked += ThumbnailPreview_PreviewClicked;
        _thumbnailPreview.CloseRequested += ThumbnailPreview_CloseRequested;
        _dockPosition = _preferences.Current.Position;
        _thumbnailPreview.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        _preferences.Changed += Preferences_Changed;
        LoadPinnedApps(_preferences.Current.PinOrder);
        if (_preferences.LoadError is not null)
        {
            ShowStatus(_preferences.LoadError);
        }

        PositionDock();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        if (source is null)
        {
            ShowStatus("Dock penceresi Windows'a bağlanamadı.");
            return;
        }

        _source = source;
        _source.AddHook(WindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (!ExcludeFromTaskSwitcher(source.Handle))
        {
            ShowStatus("OpenTaskbar Alt+Tab listesinden çıkarılamadı.");
        }

        _windowTracker = new NativeWindowTracker(Dispatcher);
        _windowTracker.WindowsChanged += WindowTracker_WindowsChanged;
        _windowTracker.ForegroundWindowChanged += WindowTracker_ForegroundWindowChanged;
        try
        {
            _windowTracker.Start(source.Handle);
            if (!_windowTracker.NotificationsAvailable)
            {
                ShowStatus("Windows uygulama bildirimlerini dinleyemedi; bildirim rozeti kullanılamıyor.");
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowStatus(ex.Message);
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        _autoHideController = new DockAutoHideController(
            this,
            source.Handle,
            DockSurfaceTranslate,
            DockSurface,
            () => (_openContextMenu?.IsOpen == true) || _thumbnailPreview.IsVisible || _draggedApp is not null
                || (_settingsWindow?.IsVisible == true) || (_spotlightWindow?.IsVisible == true) || (_widgetsWindow?.IsVisible == true));
        _autoHideController.SetMode(_preferences.Current.AutoHide);
        UpdateSpotlightHotkey();

        StartReplacementMode();
    }

    private void PositionDock()
    {
        if (_replacementSession?.IsActive == true)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        ResizeDockToContent();
        ApplyDockAlignment();
        Left = _dockPosition switch
        {
            DockPosition.Left => workArea.Left,
            DockPosition.Center => workArea.Left + (workArea.Width - Width) / 2,
            DockPosition.Right => workArea.Right - Width,
            _ => workArea.Left
        };
        Top = _preferences.Current.Edge == DockEdge.Top ? workArea.Top : Math.Max(workArea.Top, workArea.Bottom - Height);
    }

    private void StartReplacementMode()
    {
        var source = _source;
        if (source is null)
        {
            ShowStatus("Dock penceresi hazır olmadan görev çubuğu modu açılamaz.");
            return;
        }

        ResizeDockToContent();
        ApplyDockAlignment();
        var session = new TaskbarReplacementSession(this, source.Handle);
        session.OperationFailed += ShowStatus;
        session.LayoutChanged += ApplyTaskbarLayout;
        session.FullScreenChanged += OnFullScreenChanged;
        _replacementSession = session;
        try
        {
            session.Start();
            session.UpdateAutoHide(_preferences.Current.AutoHide != AutoHideMode.Disabled);
        }
        catch (InvalidOperationException ex)
        {
            session.Dispose();
            if (!session.IsActive) _replacementSession = null;
            PositionDock();
            ShowStatus(ex.Message);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && unchecked((uint)message) == _taskbarCreatedMessage)
        {
            _replacementSession?.HandleTaskbarCreated();
            handled = true;
            return IntPtr.Zero;
        }

        if (message == WmHotkey && wParam.ToInt32() == SpotlightHotkeyId)
        {
            ToggleSpotlight();
            handled = true;
            return IntPtr.Zero;
        }

        // Keep the application's foreground state intact until the dock command runs.
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (_replacementSession?.HandleMessage(unchecked((uint)message), wParam, lParam) == true)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void LoadPinnedApps(IReadOnlyList<string>? preferredOrder = null)
    {
        var result = WindowsTaskbarPinImporter.Import();
        var apps = result.Apps.ToList();
        foreach (var app in apps)
        {
            app.Icon = ApplicationIconProvider.GetForFile(app.ExecutablePath);
        }

        if (preferredOrder is { Count: > 0 })
        {
            var orderMap = preferredOrder
                .Select((path, index) => (path, index))
                .ToDictionary(pair => pair.path, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            apps.Sort((a, b) =>
            {
                var hasA = orderMap.TryGetValue(a.ExecutablePath, out var indexA);
                var hasB = orderMap.TryGetValue(b.ExecutablePath, out var indexB);
                if (hasA && hasB) return indexA.CompareTo(indexB);
                if (hasA) return -1;
                if (hasB) return 1;
                return 0;
            });
        }

        foreach (var app in apps)
        {
            _pinnedApps.Add(app);
        }

        if (result.Error is not null)
        {
            ShowStatus(result.Error);
        }
    }

    private void WindowTracker_WindowsChanged(object? sender, IReadOnlyList<TrackedWindow> windows)
    {
        foreach (var path in _lastSelectedWindows.Keys.Where(path => !windows.Any(window =>
                     string.Equals(window.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _lastSelectedWindows.Remove(path);
        }
        UpdateNotificationBadges(_pinnedApps, windows);
        UpdateWindowIndicators(windows);
        if (UpdateRunningApps(windows))
        {
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }

        if (_thumbnailPreview.IsVisible)
        {
            var activeHandles = _thumbnailPreview.ActiveHandles;
            if (activeHandles.Count > 0 && !activeHandles.Any(h => windows.Any(w => w.Handle == h)))
            {
                _thumbnailPreview.Hide();
            }
        }
    }

    private bool BadgesAllowed => _preferences.Current.NotificationBadgesEnabled
        && !QuietHours.IsActive(_preferences.Current, DateTime.Now);

    private void UpdateNotificationBadges(
        ObservableCollection<DockApp> apps,
        IReadOnlyList<TrackedWindow> windows)
    {
        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var hasNotification = BadgesAllowed && windows.Any(window => window.HasNotification
                && string.Equals(window.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (app.HasNotification != hasNotification)
            {
                app.HasNotification = hasNotification;
            }
        }
    }

    private bool UpdateRunningApps(IReadOnlyList<TrackedWindow> windows)
    {
        var pinnedPaths = _pinnedApps.Select(app => app.ExecutablePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredPaths = windows
            .Where(window => window.ExecutablePath is not null
                && File.Exists(window.ExecutablePath)
                && !pinnedPaths.Contains(window.ExecutablePath))
            .Select(window => window.ExecutablePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var executablePath in desiredPaths)
        {
            CancelRunningAppExit(executablePath);
        }

        var currentApps = _runningApps.ToDictionary(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        var desiredApps = windows
            .Where(window => window.ExecutablePath is not null
                && File.Exists(window.ExecutablePath)
                && !pinnedPaths.Contains(window.ExecutablePath))
            .GroupBy(window => window.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var executablePath = group.Key;
                var hasNotification = BadgesAllowed && group.Any(window => window.HasNotification);
                if (currentApps.TryGetValue(executablePath, out var existingApp))
                {
                    existingApp.HasNotification = hasNotification;
                    existingApp.IsForeground = group.Any(window => window.Handle == _foregroundWindow);
                    existingApp.WindowCount = group.Count();
                    return existingApp;
                }

                return new DockApp(GetApplicationName(executablePath), executablePath)
                {
                    Icon = ApplicationIconProvider.GetForFile(executablePath),
                    HasNotification = hasNotification,
                    IsForeground = group.Any(window => window.Handle == _foregroundWindow),
                    WindowCount = group.Count()
                };
            })
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var layoutChanged = !_runningApps.Select(app => app.ExecutablePath)
            .SequenceEqual(desiredApps.Select(app => app.ExecutablePath), StringComparer.OrdinalIgnoreCase);

        for (var desiredIndex = 0; desiredIndex < desiredApps.Count; desiredIndex++)
        {
            var desiredApp = desiredApps[desiredIndex];
            if (desiredIndex < _runningApps.Count
                && string.Equals(_runningApps[desiredIndex].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }

                continue;
            }

            var existingIndex = -1;
            for (var index = desiredIndex + 1; index < _runningApps.Count; index++)
            {
                if (string.Equals(_runningApps[index].ExecutablePath, desiredApp.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _runningApps.Move(existingIndex, desiredIndex);
                if (_runningApps[desiredIndex] != desiredApp)
                {
                    _runningApps[desiredIndex] = desiredApp;
                }
            }
            else
            {
                _runningApps.Insert(desiredIndex, desiredApp);
            }
        }

        for (var index = _runningApps.Count - 1; index >= desiredApps.Count; index--)
        {
            AnimateRunningAppRemoval(_runningApps[index]);
        }

        RunningAppsDivider.Visibility = desiredApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return layoutChanged;
    }

    private void CancelRunningAppExit(string executablePath)
    {
        if (!_exitingRunningPaths.Remove(executablePath))
        {
            return;
        }

        var app = _runningApps.FirstOrDefault(candidate =>
            string.Equals(candidate.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
        var button = app is null ? null : FindVisualChild<Button>(RunningApps.ItemContainerGenerator.ContainerFromItem(app));
        if (button is null) return;

        button.BeginAnimation(UIElement.OpacityProperty, null);
        button.Opacity = 1;
        button.IsHitTestVisible = true;
        if (button.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    private void AnimateRunningAppRemoval(DockApp app)
    {
        if (!_exitingRunningPaths.Add(app.ExecutablePath))
        {
            return;
        }

        AnimateDockAppRemoval(RunningApps, _runningApps, app, () =>
        {
            _exitingRunningPaths.Remove(app.ExecutablePath);
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        });
    }

    private void AnimateDockAppRemoval(
        ItemsControl owner,
        ObservableCollection<DockApp> apps,
        DockApp app,
        Action afterRemoval)
    {
        var button = FindVisualChild<Button>(owner.ItemContainerGenerator.ContainerFromItem(app));
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation || button?.RenderTransform is not ScaleTransform scale)
        {
            RemoveDockApp(apps, app);
            afterRemoval();
            return;
        }

        button.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(135))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(owner, RunningApps) && !_exitingRunningPaths.Contains(app.ExecutablePath))
            {
                return;
            }
            RemoveDockApp(apps, app);
            afterRemoval();
        };
        button.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(135))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(135))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void RemoveDockApp(ObservableCollection<DockApp> apps, DockApp app)
    {
        var index = apps.IndexOf(app);
        if (index >= 0)
        {
            apps.RemoveAt(index);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void DockAppButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation || sender is not Button button)
        {
            return;
        }

        var scale = new ScaleTransform();
        button.RenderTransform = scale;
        // Keep the new application visible on its first rendered frame.
        button.Opacity = 1;
        scale.ScaleX = 0.95;
        scale.ScaleY = 0.95;
        var duration = TimeSpan.FromMilliseconds(165);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = easing });
    }

    private void DockApp_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Snapshot before Button focus handling and preview dismissal can change foreground activation.
        var foreground = NativeWindowTracker.GetForegroundApplicationWindow();
        _foregroundAtAppPress = foreground != 0 ? foreground : _foregroundWindow;

        if (sender is Button { DataContext: DockApp app } && _pinnedApps.Contains(app))
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedApp = app;
        }
        else
        {
            _draggedApp = null;
        }
    }

    private void DockApp_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggedApp = null;
    }

    private void DockApp_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedApp is null || sender is not Button button)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var app = _draggedApp;
            _draggedApp = null;
            _foregroundAtAppPress = null;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification();
            DragDrop.DoDragDrop(button, app, DragDropEffects.Move);
        }
    }

    private void DockApp_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(DockApp))
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && _pinnedApps.Contains(targetApp))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void DockApp_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DockApp)) is DockApp sourceApp
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && _pinnedApps.Contains(sourceApp)
            && _pinnedApps.Contains(targetApp)
            && !ReferenceEquals(sourceApp, targetApp))
        {
            var oldIndex = _pinnedApps.IndexOf(sourceApp);
            var newIndex = _pinnedApps.IndexOf(targetApp);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                _pinnedApps.Move(oldIndex, newIndex);
                SavePinOrder();
                _replacementSession?.RefreshPosition();
            }
        }

        e.Handled = true;
    }

    private void DockApp_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _foregroundAtAppPress = null;
    }

    private void DockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DockApp app })
        {
            return;
        }

        var foregroundAtPress = _foregroundAtAppPress;
        _foregroundAtAppPress = null;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        AnimateActivationFeedback((Button)sender);
        OpenDockApp(app, toggleIfForeground: true, foregroundAtPress);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        const uint wmSysCommand = 0x0112;
        const int scTaskList = 0xF130;
        if (_source is null || !PostMessage(_source.Handle, wmSysCommand, scTaskList, 0))
        {
            ShowStatus("Windows Başlat menüsüne açılma isteği gönderilemedi.");
        }
    }

    private void ShowDesktop_Click(object sender, RoutedEventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        SendWinKeyCombo(0x44); // Win + D
    }

    private void SearchPillButton_Click(object sender, RoutedEventArgs e) => ToggleSpotlight();

    private void WidgetsButton_Click(object sender, RoutedEventArgs e) => ToggleWidgetsDrawer();

    private void WidgetsButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleWidgetsDrawer();
    }

    private void MarketTickerButton_Click(object sender, RoutedEventArgs e)
    {
        _tickerSubTick = 0;
        if (_onlineFeeds.Markets.Count > 1)
        {
            AdvanceMarketTicker(1);
        }
        else
        {
            ToggleWidgetsDrawer();
        }
    }

    private void DockMediaTitle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleWidgetsDrawer();
    private async void DockMediaPrev_Click(object sender, RoutedEventArgs e) => await _mediaService.PreviousAsync();
    private async void DockMediaPlayPause_Click(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void DockMediaNext_Click(object sender, RoutedEventArgs e) => await _mediaService.NextAsync();

    private void PomodoroMiniButton_Click(object sender, RoutedEventArgs e) => _productivity.TogglePomodoro();

    private void PomodoroMiniButton_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        _productivity.ResetPomodoro();
    }

    private void BarWidgetsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent) return;
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (item.Tag is string id)
            {
                item.IsChecked = _preferences.Current.HasBarWidget(id);
            }
        }
    }

    private void ToggleBarWidgetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string id })
        {
            ToggleBarWidget(id);
        }
    }

    public void ToggleBarWidget(string widgetId)
    {
        var list = _preferences.Current.EffectiveBarWidgets.ToList();
        var existing = list.FindIndex(x => string.Equals(x, widgetId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) list.RemoveAt(existing);
        else list.Add(widgetId);
        _ = _preferences.Save(_preferences.Current with { BarWidgets = list });
    }

    private void ApplyBarWidgetsVisibility()
    {
        var cur = _preferences.Current;
        SearchPillButton.Visibility = cur.HasBarWidget("search") ? Visibility.Visible : Visibility.Collapsed;
        WeatherMiniButton.Visibility = cur.HasBarWidget("weather") ? Visibility.Visible : Visibility.Collapsed;
        AiLimitsMiniButton.Visibility = cur.HasBarWidget("claude") ? Visibility.Visible : Visibility.Collapsed;
        HardwareMiniButton.Visibility = cur.HasBarWidget("hardware") ? Visibility.Visible : Visibility.Collapsed;
        PomodoroMiniButton.Visibility = cur.HasBarWidget("pomodoro") ? Visibility.Visible : Visibility.Collapsed;
        DockMediaCapsule.Visibility = cur.HasBarWidget("media") ? Visibility.Visible : Visibility.Collapsed;
        MarketTickerButton.Visibility = cur.HasBarWidget("markets") ? Visibility.Visible : Visibility.Collapsed;
        StatusMiniButton.Visibility = cur.HasBarWidget("status") ? Visibility.Visible : Visibility.Collapsed;
        CalendarMiniButton.Visibility = cur.HasBarWidget("calendar") ? Visibility.Visible : Visibility.Collapsed;
        WorldClockMiniButton.Visibility = cur.HasBarWidget("worldclock") ? Visibility.Visible : Visibility.Collapsed;
        UpdateDockCalendarCapsule();
        UpdateDockWorldClockCapsule();
        ResizeDockToContent();
    }

    private void UpdateDockPomodoroCapsule()
    {
        DockPomodoroText.Text = _productivity.FormattedTime;
        DockPomodoroGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            _productivity.IsRunning ? "PositiveBrush" : "AccentBrush");
    }

    private void StatusMiniButton_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var delta = e.Delta > 0 ? 2 : -2;
        _audioService.SetVolumePercent(_audioService.VolumePercent + delta);
        UpdateDockStatusIcons();
    }

    private void ToggleSpotlight()
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        _widgetsWindow?.CloseDrawer();
        _spotlightWindow ??= new SpotlightWindow(_spotlightEngine);
        _spotlightWindow.Toggle();
    }

    private void ToggleWidgetsDrawer()
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        _widgetsWindow ??= new WidgetsWindow(
            _preferences,
            _calendar,
            _audioService,
            _mediaService,
            _systemStatus,
            _onlineFeeds,
            _productivity,
            _aiUsage,
            _backgroundApps,
            openSettings: () => OpenSettings_Click(this, new RoutedEventArgs()),
            openNotifications: () => SendWinKeyCombo(0x4E),
            openSystemTray: () => SendWinKeyCombo(0x42)); // Win + B focuses system tray overflow
        _widgetsWindow.ToggleAtAnchor(GetWidgetsAnchorArea(), _preferences.Current.Edge == DockEdge.Top);
    }

    private Rect GetWidgetsAnchorArea()
    {
        var work = SystemParameters.WorkArea;
        if (_preferences.Current.Edge == DockEdge.Top)
        {
            // Panels open downward, below the dock (headroom is part of the dock window).
            var dockBottom = Math.Max(work.Top, Top + DockSurface.Margin.Top + (double.IsNaN(DockSurface.Height) ? 46 : DockSurface.Height) + 4);
            return new Rect(work.Left, dockBottom, work.Width, Math.Max(200, work.Bottom - dockBottom));
        }
        if (_dockArea.Width > 0 && _dockArea.Height > 0)
        {
            var top = Math.Max(work.Top, SystemParameters.PrimaryScreenHeight - _dockArea.Height);
            return new Rect(work.Left, work.Top, work.Width, Math.Max(200, top - work.Top));
        }
        return work;
    }

    private async Task InitializeWidgetsBackgroundAsync()
    {
        await _mediaService.InitializeAsync();
        await RefreshDockStatusAsync();
    }

    private async Task RefreshDockStatusAsync()
    {
        UpdateDockStatusIcons();
        var aiTask = _aiUsage.RefreshAsync();
        var wxTask = _onlineFeeds.GetWeatherAsync(_preferences.Current.WeatherCity);
        var mkTask = _onlineFeeds.GetMarketsAsync(_preferences.Current.EffectiveMarketSymbols);
        var calTask = _calendar.RefreshAsync(_preferences.Current.CalendarUrls);
        await Task.WhenAll(aiTask, wxTask, mkTask, calTask);

        if (_aiUsage.Claude is { } claude)
        {
            DockClaudeBar.Value = claude.FiveHourPercent;
            DockClaudeText.Text = $"{claude.FiveHourPercent}%";
            AiLimitsMiniButton.ToolTip = $"{claude.PlanName} ({claude.TopModel})\n{claude.FiveHourDetail} · {claude.ResetText}\n{claude.WeeklyDetail}";
        }

        var wx = await wxTask;
        if (wx is not null)
        {
            DockWeatherTempText.Text = $"{Math.Round(wx.Temperature):0}°";
            DockWeatherCityText.Text = wx.City;
            DockWeatherGlyph.Data = FindResource(wx.GlyphKey) as Geometry;
            WeatherMiniButton.ToolTip = $"{wx.City}: {Math.Round(wx.Temperature):0}° · {wx.ConditionText}";
        }

        AdvanceMarketTicker(0);
        ResizeDockToContent();
    }

    private void UpdateDockTelemetryCapsule()
    {
        var perf = _systemStatus.ReadPerformance();
        DockCpuBar.Value = perf.CpuPercent;
        DockCpuText.Text = $"{perf.CpuPercent}%";
        DockRamBar.Value = perf.MemoryPercent;
        DockRamText.Text = $"{perf.MemoryPercent}%";
        HardwareMiniButton.ToolTip = $"CPU: {perf.CpuPercent}% · RAM: {perf.MemoryUsedGb:F1}/{perf.MemoryTotalGb:F0} GB ({perf.MemoryPercent}%) · Disk C: {perf.DiskFreeGb:F0} GB";
    }

    private void UpdateDockMediaCapsule()
    {
        var m = _mediaService.Current;
        if (m.HasMedia)
        {
            DockMediaTitleText.Text = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} · {m.Artist}";
            DockMediaTitleText.ToolTip = $"{m.Title}\n{m.Artist}";
            DockMediaArtImage.Source = m.Thumbnail;
            DockMediaArtImage.Visibility = m.Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
            DockMediaFallbackGlyph.Visibility = m.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
            DockMediaPlayPauseGlyph.Data = FindResource(m.IsPlaying ? "IconPause" : "IconPlay") as Geometry;
        }
        else
        {
            DockMediaTitleText.Text = UiText.Get("NowPlaying");
            DockMediaTitleText.ToolTip = UiText.Get("NothingPlaying");
            DockMediaArtImage.Source = null;
            DockMediaArtImage.Visibility = Visibility.Collapsed;
            DockMediaFallbackGlyph.Visibility = Visibility.Visible;
            DockMediaPlayPauseGlyph.Data = FindResource("IconPlay") as Geometry;
        }
    }

    private void AdvanceMarketTicker(int step)
    {
        var quotes = _onlineFeeds.Markets;
        if (quotes.Count == 0) return;
        _tickerIndex = (_tickerIndex + step) % quotes.Count;
        if (_tickerIndex < 0) _tickerIndex += quotes.Count;
        var q = quotes[_tickerIndex];
        DockTickerNameText.Text = q.DisplayName;
        var culture = UiText.Culture;
        DockTickerPriceText.Text = q.Price >= 1000 ? q.Price.ToString("N0", culture) : q.Price.ToString("N2", culture);
        var positive = q.ChangePercent >= 0;
        DockTickerChangeText.Text = $"{(positive ? "+" : "")}{q.ChangePercent:F2}%";
        DockTickerBadge.SetResourceReference(Border.BackgroundProperty, positive ? "PositiveSurface" : "NegativeSurface");
        DockTickerChangeText.SetResourceReference(TextBlock.ForegroundProperty, positive ? "PositiveBrush" : "NegativeBrush");
        MarketTickerButton.ToolTip = $"{q.DisplayName} ({q.Symbol}): {DockTickerPriceText.Text} ({DockTickerChangeText.Text})";
    }

    private void UpdateDockStatusIcons()
    {
        DockVolumeGlyph.Data = FindResource(_audioService.IsMuted || _audioService.VolumePercent == 0 ? "IconVolumeMute" : "IconVolume") as Geometry;
        DockVolumeText.Text = _audioService.IsMuted ? "0%" : $"{_audioService.VolumePercent}%";
        var net = _systemStatus.ReadNetwork();
        DockNetworkGlyph.Data = FindResource(net.IsConnected ? "IconWifi" : "IconWifiOff") as Geometry;
        var bat = _systemStatus.ReadBattery();
        if (bat.HasBattery)
        {
            DockBatteryGlyph.Data = FindResource(bat.IsCharging || bat.IsPluggedIn ? "IconBatteryCharging" : "IconBattery") as Geometry;
            DockBatteryText.Text = $"{bat.Percent}%";
            if (DockBatteryPanel.Visibility != Visibility.Visible)
            {
                DockBatteryPanel.Visibility = Visibility.Visible;
                ResizeDockToContent();
            }
        }
        else if (DockBatteryPanel.Visibility != Visibility.Collapsed)
        {
            DockBatteryPanel.Visibility = Visibility.Collapsed;
            ResizeDockToContent();
        }
    }

    private void UpdateSpotlightHotkey()
    {
        if (_source is null || _source.IsDisposed) return;
        if (_preferences.Current.SearchHotkeyEnabled)
        {
            if (!_hotkeyRegistered)
            {
                _hotkeyRegistered = RegisterHotKey(_source.Handle, SpotlightHotkeyId, ModAlt | ModNorepeat, VkSpace);
            }
            SearchPillButton.ToolTip = _hotkeyRegistered
                ? UiText.Format("SearchTooltip", "Alt+Space")
                : UiText.Get("HotkeyUnavailable");
        }
        else if (_hotkeyRegistered)
        {
            UnregisterHotKey(_source.Handle, SpotlightHotkeyId);
            _hotkeyRegistered = false;
            SearchPillButton.ToolTip = UiText.Get("SearchPlaceholder");
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = _preferences.Current.Language == AppLanguage.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("tr-TR");
        ClockTimeText.Text = now.ToString("HH:mm", culture);
        ClockDateText.Text = now.ToString("d MMM", culture);
        ClockButton.ToolTip = $"{now.ToString("D", culture)}\n{UiText.Get("OpenWidgets")}";
        UpdateDockWorldClockCapsule();
        UpdateDockCalendarCapsule();
        var quiet = QuietHours.IsActive(_preferences.Current, now);
        if (quiet != _lastQuiet)
        {
            _lastQuiet = quiet;
            if (_windowTracker is not null)
            {
                UpdateNotificationBadges(_pinnedApps, _windowTracker.Snapshot);
                UpdateNotificationBadges(_runningApps, _windowTracker.Snapshot);
            }
        }
    }

    private void UpdateDockWorldClockCapsule()
    {
        if (WorldClockMiniButton.Visibility != Visibility.Visible) return;
        var entries = WorldClockService.Read(_preferences.Current.EffectiveWorldClocks);
        if (entries.Count == 0)
        {
            DockWorldClockText.Text = "--";
            return;
        }
        var first = entries[0];
        var text = $"{first.City} {first.Time}";
        if (DockWorldClockText.Text != text)
        {
            DockWorldClockText.Text = text;
            ResizeDockToContent();
        }
        WorldClockMiniButton.ToolTip = string.Join("\n", entries.Select(e => $"{e.City}: {e.Time} ({e.DayLabel}, {e.Offset})"));
    }

    private void UpdateDockCalendarCapsule()
    {
        if (CalendarMiniButton.Visibility != Visibility.Visible) return;
        string text;
        string tooltip;
        var next = _calendar.Upcoming(DateTime.Now, 1).FirstOrDefault();
        if (!_calendar.HasFeeds)
        {
            text = UiText.Get("WidgetCalendar");
            tooltip = UiText.Get("ConnectCalendar");
        }
        else if (next is null)
        {
            text = UiText.Get("NoUpcoming");
            tooltip = _calendar.LastError is null ? text : UiText.Get("CalendarFetchError");
        }
        else
        {
            var when = next.AllDay ? UiText.Get("AllDay")
                : next.Start.Date == DateTime.Today ? next.Start.ToString("HH:mm", UiText.Culture)
                : next.Start.ToString("ddd HH:mm", UiText.Culture);
            text = $"{when} · {next.Title}";
            tooltip = $"{next.Title}\n{next.Start.ToString("f", UiText.Culture)}" + (next.Location is null ? "" : $"\n{next.Location}");
        }
        if (DockCalendarText.Text != text)
        {
            DockCalendarText.Text = text;
            ResizeDockToContent();
        }
        CalendarMiniButton.ToolTip = tooltip;
    }

    // ---- Placement (top / bottom), layout (pill / full width) and drag-to-edge ------------------------------

    private void ApplyEdgeAndLayout()
    {
        var cur = _preferences.Current;
        var top = cur.Edge == DockEdge.Top;
        var full = cur.Layout == DockLayout.FullWidth;
        var headroom = new GridLength((double)FindResource("DockHeadroom"));
        var star = new GridLength(1, GridUnitType.Star);
        DockRowA.Height = top ? star : headroom;
        DockRowB.Height = top ? headroom : star;
        var row = top ? 0 : 1;
        Grid.SetRow(DockHitArea, row);
        Grid.SetRow(DockSurface, row);
        Grid.SetRow(DockEdgeTrigger, row);
        DockSurface.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        DockEdgeTrigger.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        if (DockSurface.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            shadow.Direction = top ? 90 : 270;

        if (full)
        {
            DockSurface.Margin = new Thickness(0);
            DockSurface.CornerRadius = new CornerRadius(0);
            DockSurface.BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
            DockSurface.Height = 50;
            DockSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
            DockCenterColumn.Width = new GridLength(1, GridUnitType.Star);
            DockSurface.Padding = new Thickness(14, 2, 14, 2);
        }
        else
        {
            DockSurface.Margin = top ? new Thickness(10, 2, 10, 0) : new Thickness(10, 0, 10, 2);
            DockSurface.CornerRadius = new CornerRadius(15);
            DockSurface.BorderThickness = new Thickness(1);
            DockSurface.Height = 46;
            DockCenterColumn.Width = GridLength.Auto;
            DockSurface.Padding = new Thickness(10, 2, 10, 2);
        }

        _iconOriginY = top ? 0.05 : 0.95;
        _bounceDir = top ? 1 : -1;
        ApplyDockAlignment();
        _autoHideController?.SetEdge(cur.Edge);
        _replacementSession?.UpdateEdge(cur.Edge);
        ResizeDockToContent();
    }

    private void MoveEdgeMenu_Click(object sender, RoutedEventArgs e)
    {
        var next = _preferences.Current.Edge == DockEdge.Top ? DockEdge.Bottom : DockEdge.Top;
        _ = _preferences.Save(_preferences.Current with { Edge = next });
    }

    private void DockSurface_EdgeDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Buttons mark the event handled, so this only fires on empty bar surface.
        if (e.Handled || e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        _edgeDragging = true;
        _edgeDragMoved = false;
        _edgeDragStartScreen = DockSurface.PointToScreen(e.GetPosition(DockSurface));
        DockSurface.CaptureMouse();
    }

    private void DockSurface_EdgeDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_edgeDragging) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { EndEdgeDrag(); return; }
        var current = DockSurface.PointToScreen(e.GetPosition(DockSurface));
        if (!_edgeDragMoved && Math.Abs(current.Y - _edgeDragStartScreen.Y) > 24)
        {
            _edgeDragMoved = true;
            DockSurface.Cursor = System.Windows.Input.Cursors.SizeNS;
        }
    }

    private void DockSurface_EdgeDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_edgeDragging) return;
        var moved = _edgeDragMoved;
        var end = DockSurface.PointToScreen(e.GetPosition(DockSurface));
        EndEdgeDrag();
        if (!moved) return;
        var screenHeightPx = SystemParameters.PrimaryScreenHeight * VisualTreeHelper.GetDpi(this).DpiScaleY;
        var target = end.Y < screenHeightPx / 2d ? DockEdge.Top : DockEdge.Bottom;
        if (target != _preferences.Current.Edge)
        {
            _ = _preferences.Save(_preferences.Current with { Edge = target });
        }
    }

    private void EndEdgeDrag()
    {
        if (!_edgeDragging) return;
        _edgeDragging = false;
        DockSurface.ClearValue(CursorProperty);
        if (DockSurface.IsMouseCaptured) DockSurface.ReleaseMouseCapture();
    }

    private static void SendWinKeyCombo(ushort virtualKey)
    {
        const uint inputKeyboard = 1;
        const uint keyeventfKeyUp = 0x0002;
        const ushort vkLWin = 0x5B;

        var inputs = new Input[]
        {
            new() { Type = inputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vkLWin } } },
            new() { Type = inputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey } } },
            new() { Type = inputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = keyeventfKeyUp } } },
            new() { Type = inputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vkLWin, Flags = keyeventfKeyUp } } }
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static (ScaleTransform Scale, TranslateTransform Translate) GetIconTransforms(FrameworkElement icon)
    {
        if (icon.RenderTransform is TransformGroup { IsFrozen: false } group)
        {
            var s = group.Children.OfType<ScaleTransform>().FirstOrDefault(x => !x.IsFrozen);
            var t = group.Children.OfType<TranslateTransform>().FirstOrDefault(x => !x.IsFrozen);
            if (s is not null && t is not null)
            {
                return (s, t);
            }
        }

        var newScale = new ScaleTransform();
        var newTranslate = new TranslateTransform();
        var newGroup = new TransformGroup();
        newGroup.Children.Add(newScale);
        newGroup.Children.Add(newTranslate);
        icon.RenderTransform = newGroup;
        icon.RenderTransformOrigin = new Point(0.5, 0.95);
        return (newScale, newTranslate);
    }

    private FrameworkElement? GetButtonIcon(Button button) =>
        button.Content as FrameworkElement ?? FindVisualChild<Grid>(button);

    private IEnumerable<Button> GetAllDockButtons()
    {
        for (var i = 0; i < _pinnedApps.Count; i++)
        {
            if (PinnedApps.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }

        for (var i = 0; i < _runningApps.Count; i++)
        {
            if (RunningApps.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container
                && FindVisualChild<Button>(container) is { } button)
            {
                yield return button;
            }
        }
    }

    private void AnimateActivationFeedback(Button button)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation) return;
        var icon = GetButtonIcon(button);
        if (icon is null) return;

        var (_, translate) = GetIconTransforms(icon);

        var bounce = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(420),
            KeyFrames =
            {
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(12 * _bounceDir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
                    new CubicEase { EasingMode = EasingMode.EaseIn }),
                new EasingDoubleKeyFrame(5 * _bounceDir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
                    new CubicEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
                    new CubicEase { EasingMode = EasingMode.EaseIn })
            }
        };
        translate.BeginAnimation(TranslateTransform.YProperty, bounce);
    }

    private void DockSurface_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var mousePos = e.GetPosition(DockContents);
        var mouseX = mousePos.X;
        const double influenceRadius = 90.0;
        var maxScale = (double)FindResource("DockHoverScale");

        var currentMagnified = new HashSet<FrameworkElement>();

        foreach (var button in GetAllDockButtons())
        {
            var icon = GetButtonIcon(button);
            if (icon is null) continue;

            var (scale, _) = GetIconTransforms(icon);
            icon.RenderTransformOrigin = new Point(0.5, _iconOriginY);
            var buttonPos = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), DockContents);
            var distance = Math.Abs(mouseX - buttonPos.X);

            double targetScale = 1.0;
            if (distance < influenceRadius)
            {
                var factor = Math.Cos(distance / influenceRadius * (Math.PI / 2));
                targetScale = 1.0 + (maxScale - 1.0) * factor * factor;
            }

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = targetScale;
            scale.ScaleY = targetScale;

            if (targetScale > 1.005)
            {
                currentMagnified.Add(icon);
            }
        }

        foreach (var icon in _magnifiedIcons)
        {
            if (!currentMagnified.Contains(icon))
            {
                var (scale, _) = GetIconTransforms(icon);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
            }
        }

        _magnifiedIcons.Clear();
        foreach (var icon in currentMagnified)
        {
            _magnifiedIcons.Add(icon);
        }
    }

    private void DockSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseEnterDock();

    private void DockSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ResetMagnification();
        _autoHideController?.OnMouseLeaveDock();
    }

    private void DockEdgeTrigger_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseEnterEdge();
    private void DockEdgeTrigger_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _autoHideController?.OnMouseLeaveEdge();

    private void ResetMagnification()
    {
        if (_magnifiedIcons.Count == 0) return;
        var duration = _preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation
            ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var icon in _magnifiedIcons)
        {
            var (scale, _) = GetIconTransforms(icon);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, duration) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, duration) { EasingFunction = easing });
        }

        _magnifiedIcons.Clear();
    }

    private void OpenDockApp(DockApp app, bool toggleIfForeground = false, nint? foregroundAtPress = null)
    {
        var executablePath = app.ExecutablePath;
        var existingWindow = foregroundAtPress is { } pressedWindow
            ? _windowTracker?.Snapshot.FirstOrDefault(window => window.Handle == pressedWindow
                && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            : null;
        existingWindow ??= FindNextWindowForExecutable(executablePath);
        if (existingWindow is not null)
        {
            var activated = toggleIfForeground
                ? NativeWindowTracker.ToggleActivation(existingWindow.Handle, executablePath, foregroundAtPress)
                : NativeWindowTracker.Activate(existingWindow.Handle);
            if (!activated)
            {
                ShowStatus("Pencereyi öne getirme veya küçültme isteği tamamlanamadı. Windows odağı engellemiş olabilir.");
                return;
            }

            _lastSelectedWindows[executablePath] = existingWindow.Handle;
            ClearStatus();
            return;
        }

        if (!File.Exists(executablePath))
        {
            ShowStatus("Uygulama dosyası bulunamadı. Windows görev çubuğundaki kısayolu güncelleyin.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath)
            {
                Arguments = app.LaunchArguments ?? string.Empty,
                UseShellExecute = true
            });
            ClearStatus();
        }
        catch (Win32Exception)
        {
            ShowStatus("Uygulama Windows tarafından başlatılamadı.");
        }
        catch (InvalidOperationException)
        {
            ShowStatus("Uygulama başlatma isteği tamamlanamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("Uygulamayı başlatmak için erişim izni yok.");
        }
        catch (IOException)
        {
            ShowStatus("Uygulama dosyasına erişilemedi.");
        }
    }

    private void DockApp_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
        if (!_preferences.Current.PreviewsEnabled)
        {
            if (sender is Button button) ToolTipService.SetIsEnabled(button, true);
            _thumbnailPreview.Hide();
            return;
        }
        if (sender is not Button { Tag: string executablePath })
        {
            return;
        }

        var matchingWindows = _windowTracker?.Snapshot
            .Where(window => string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        ToolTipService.SetIsEnabled((Button)sender, matchingWindows.Count == 0);
        if (matchingWindows.Count == 0)
        {
            _thumbnailPreview.Hide();
            return;
        }

        var appIcon = ((Button)sender).DataContext is DockApp app ? app.Icon : null;
        _thumbnailPreview.ShowForWindows(matchingWindows, (FrameworkElement)sender, appIcon);
    }

    private void DockApp_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void PreviewCloseTimer_Tick(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        _autoHideController?.EvaluateState();
    }

    private void ThumbnailPreview_PointerEntered(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
    }

    private void ThumbnailPreview_PointerLeft(object? sender, EventArgs e)
    {
        _previewCloseTimer.Stop();
        _previewCloseTimer.Start();
    }

    private void ThumbnailPreview_PreviewClicked(object? sender, EventArgs e)
    {
        var sourceHandle = _thumbnailPreview.SourceHandle;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (sourceHandle == 0 || !NativeWindowTracker.Activate(sourceHandle))
        {
            ShowStatus("Önizleme penceresi etkinleştirilemedi. Windows odağı engellemiş olabilir.");
            return;
        }

        ClearStatus();
    }

    private void ThumbnailPreview_CloseRequested(object? sender, EventArgs e)
    {
        var sourceHandle = _thumbnailPreview.SourceHandle;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (sourceHandle == 0 || !NativeWindowTracker.RequestClose(sourceHandle))
        {
            ShowStatus("Önizlenen pencereye kapatma isteği gönderilemedi.");
            return;
        }

        ClearStatus();
    }

    private TrackedWindow? FindWindowForExecutable(string executablePath)
    {
        return _windowTracker?.Snapshot.FirstOrDefault(window => window.Handle == _foregroundWindow
            && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            ?? _windowTracker?.Snapshot.FirstOrDefault(window =>
                string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private TrackedWindow? FindNextWindowForExecutable(string executablePath)
    {
        var matchingWindows = _windowTracker?.Snapshot.Where(window =>
            string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matchingWindows is not { Count: > 0 })
        {
            return null;
        }

        var foreground = matchingWindows.FirstOrDefault(window => window.Handle == _foregroundWindow);
        if (foreground is not null)
        {
            return foreground;
        }

        matchingWindows.Sort((left, right) => left.Handle.ToInt64().CompareTo(right.Handle.ToInt64()));
        var previousIndex = _lastSelectedWindows.TryGetValue(executablePath, out var previousHandle)
            ? matchingWindows.FindIndex(window => window.Handle == previousHandle)
            : -1;
        return matchingWindows[(previousIndex + 1) % matchingWindows.Count];
    }

    private void UpdateWindowIndicators(IReadOnlyList<TrackedWindow> windows)
    {
        for (var index = 0; index < _pinnedApps.Count; index++)
        {
            var app = _pinnedApps[index];
            var matching = windows.Where(window => string.Equals(window.ExecutablePath, app.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)).ToList();
            var isForeground = matching.Any(window => window.Handle == _foregroundWindow);
            var count = matching.Count;
            if (app.IsRunning != (count > 0) || app.IsForeground != isForeground || app.WindowCount != count)
            {
                app.IsForeground = isForeground;
                app.WindowCount = count;
            }
        }
    }

    private static string GetApplicationName(string executablePath)
    {
        var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
        return string.IsNullOrWhiteSpace(description)
            ? Path.GetFileNameWithoutExtension(executablePath)
            : description.Trim();
    }

    private void WindowTracker_ForegroundWindowChanged(nint windowHandle)
    {
        _replacementSession?.UpdateForeground(windowHandle);
        _autoHideController?.CheckForegroundWindow(windowHandle);
        GetWindowThreadProcessId(windowHandle, out var processId);
        // Dock buttons, thumbnail hosts, and menus must not replace the last external foreground window.
        if (processId == Environment.ProcessId) return;
        if (_openContextMenu is not null)
        {
            _openContextMenu.IsOpen = false;
        }
        _foregroundWindow = windowHandle;
        UpdateWindowIndicators(_windowTracker?.Snapshot ?? []);
        foreach (var app in _runningApps)
        {
            app.IsForeground = _windowTracker?.Snapshot.Any(window => window.Handle == windowHandle
                && string.Equals(window.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase)) == true;
        }
        if (_replacementSession?.IsActive == true)
        {
            _replacementSession.RefreshPosition();
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = UiText.LocalizeError(message);
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void ClearStatus()
    {
        StatusText.Text = string.Empty;
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private async void DockContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        ResetMagnification();
        if (_openContextMenu is not null && !ReferenceEquals(_openContextMenu, contextMenu))
            _openContextMenu.IsOpen = false;
        _openContextMenu = contextMenu;

        var pinnedApp = (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as DockApp;
        _contextTargetWasForeground = pinnedApp?.IsForeground == true;
        var hasAppTarget = pinnedApp is not null;
        var isPinnedTarget = pinnedApp?.ShortcutPath is not null;
        var isRunning = pinnedApp?.IsRunning == true;
        SetMenuItemVisibility(contextMenu, "ActivateDockApp", hasAppTarget);
        SetMenuItemVisibility(contextMenu, "CloseDockApp", isRunning);
        SetMenuItemVisibility(contextMenu, "TerminateDockApp", isRunning);
        SetMenuItemVisibility(contextMenu, "PinDockApp", hasAppTarget && !isPinnedTarget);
        SetMenuItemVisibility(contextMenu, "UnpinDockApp", isPinnedTarget);
        SetSeparatorVisibility(contextMenu, "RunningCommandsDivider", isRunning);

        if (pinnedApp is null)
        {
            FocusContextMenu(contextMenu);
            return;
        }

        SetMenuCommandParameter(contextMenu, pinnedApp.ExecutablePath);
        var targetWindow = FindWindowForExecutable(pinnedApp.ExecutablePath);
        if (FindMenuItem(contextMenu, "ActivateDockApp") is { } activateMenuItem)
        {
            activateMenuItem.CommandParameter = pinnedApp;
            activateMenuItem.Header = isRunning
                ? (pinnedApp.IsForeground ? UiText.Get("MinimizeWindow") : UiText.Get("ActivateWindow"))
                : UiText.Get("OpenApplication");
        }
        if (FindMenuItem(contextMenu, "PinDockApp") is { } pinMenuItem)
        {
            pinMenuItem.CommandParameter = pinnedApp;
        }
        if (FindMenuItem(contextMenu, "UnpinDockApp") is { } unpinMenuItem)
        {
            unpinMenuItem.CommandParameter = pinnedApp;
        }
        FocusContextMenu(contextMenu);
        await LoadJumpListAsync(contextMenu, targetWindow?.Handle ?? 0, pinnedApp.ShortcutPath);
    }

    private async Task LoadJumpListAsync(ContextMenu menu, nint window, string? shortcut)
    {
        var boundary = menu.Items.OfType<Separator>().FirstOrDefault(item => item.Tag as string == "JumpListCommands");
        if (boundary is null) return;
        _jumpListLoad?.Cancel();
        _jumpListLoad?.Dispose();
        var load = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _jumpListLoad = load;
        ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksLoading"), IsEnabled = false }]);
        try
        {
            var result = await _jumpLists.ReadAsync(window, shortcut, load.Token);
            if (!menu.IsOpen || !ReferenceEquals(_jumpListLoad, load))
            {
                await _jumpLists.ReleaseAsync(result.SessionId);
                return;
            }
            _jumpListSession = result.SessionId;
            var items = new List<Control>();
            foreach (var group in result.Groups)
            {
                if (items.Count > 0) items.Add(new Separator());
                var heading = new MenuItem { Header = group.Title.Replace("_", "__") };
                heading.SetResourceReference(StyleProperty, "MenuSectionHeading");
                items.Add(heading);
                foreach (var entry in group.Entries)
                {
                    if (entry.IsSeparator) { items.Add(new Separator()); continue; }
                    // Keep the session alive until Click queues invocation; WPF normally closes first.
                    var item = new MenuItem { Header = entry.Title.Replace("_", "__"), StaysOpenOnClick = true };
                    if (entry.Icon is not null) item.Icon = new Image { Source = entry.Icon, Width = 16, Height = 16 };
                    item.Click += async (_, args) =>
                    {
                        args.Handled = true;
                        var invocation = _jumpLists.InvokeAsync(result.SessionId, entry.CommandId, _source?.Handle ?? 0);
                        menu.IsOpen = false;
                        if (!await invocation && IsLoaded)
                            ShowStatus("Uygulama görevi çalıştırılamadı. Menüyü yeniden açıp deneyin.");
                    };
                    items.Add(item);
                }
            }
            if (result.Status != JumpListStatus.Ready)
            {
                var message = result.Status switch
                {
                    JumpListStatus.Empty => UiText.Get("TasksEmpty"),
                    JumpListStatus.IdentityUnavailable => UiText.Get("TasksIdentityUnavailable"),
                    JumpListStatus.Unsupported => UiText.Get("TasksUnsupported"),
                    _ => UiText.Get("TasksReadError")
                };
                items.Add(new MenuItem { Header = message, IsEnabled = false });
            }
            ReplaceJumpListItems(menu, boundary, items);
        }
        catch (OperationCanceledException)
        {
            if (menu.IsOpen && ReferenceEquals(_jumpListLoad, load))
            {
                ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksTimeout"), IsEnabled = false }]);
            }
        }
        catch (COMException ex)
        {
            Trace.TraceWarning("Shell worker failed: 0x{0:X8}", ex.HResult);
            if (menu.IsOpen && ReferenceEquals(_jumpListLoad, load))
            {
                ReplaceJumpListItems(menu, boundary, [new MenuItem { Header = UiText.Get("TasksServiceError"), IsEnabled = false }]);
            }
        }
    }

    private static void ReplaceJumpListItems(ContextMenu menu, Separator boundary, IReadOnlyList<Control> items)
    {
        foreach (var previous in menu.Items.OfType<Control>().Where(item => item.Tag as string == "JumpListEntry").ToArray())
            menu.Items.Remove(previous);
        var index = menu.Items.IndexOf(boundary);
        foreach (var item in items)
        {
            item.Tag = "JumpListEntry";
            menu.Items.Insert(index++, item);
        }
        boundary.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void FocusContextMenu(ContextMenu menu)
    {
        if (PresentationSource.FromVisual(menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
        menu.Focus();
    }

    private async void DockContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _openContextMenu)) return;
        _openContextMenu = null;
        _autoHideController?.EvaluateState();
        _jumpListLoad?.Cancel();
        if (_jumpListSession is { } session)
        {
            _jumpListSession = null;
            await _jumpLists.ReleaseAsync(session);
        }
    }

    private void DockAppContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: DockApp app })
        {
            if (_contextTargetWasForeground && FindWindowForExecutable(app.ExecutablePath) is { } window)
                NativeWindowTracker.Activate(window.Handle);
            OpenDockApp(app, toggleIfForeground: _contextTargetWasForeground);
        }
    }

    private void PinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: DockApp app })
        {
            return;
        }

        var (shortcutPath, error) = WindowsTaskbarPinImporter.AddPin(app.Name, app.ExecutablePath, app.LaunchArguments);
        if (error is not null || shortcutPath is null)
        {
            ShowStatus(error ?? "Uygulama sabitlenemedi.");
            return;
        }

        ClearStatus();

        var pinnedApp = new DockApp(app.Name, app.ExecutablePath)
        {
            Icon = app.Icon,
            LaunchArguments = app.LaunchArguments,
            ShortcutPath = shortcutPath,
            HasNotification = app.HasNotification,
            IsForeground = app.IsForeground,
            WindowCount = app.WindowCount
        };

        _runningApps.Remove(app);
        _pinnedApps.Add(pinnedApp);

        SavePinOrder();
        ResizeDockToContent();
        _replacementSession?.RefreshPosition();
    }

    private void SavePinOrder()
    {
        var order = _pinnedApps.Select(app => app.ExecutablePath).ToList();
        var error = _preferences.Save(_preferences.Current with { PinOrder = order });
        if (error is not null) ShowStatus(error);
    }

    private void UnpinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: DockApp { ShortcutPath: not null } app })
        {
            return;
        }

        var error = WindowsTaskbarPinImporter.RemovePin(app.ShortcutPath);
        if (error is not null)
        {
            ShowStatus(error);
            return;
        }

        ClearStatus();
        AnimateDockAppRemoval(PinnedApps, _pinnedApps, app, () =>
        {
            SavePinOrder();
            if (_windowTracker is not null)
            {
                UpdateRunningApps(_windowTracker.Snapshot);
            }

            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        });
    }

    private void CloseDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        var closedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.CloseMainWindow())
                {
                    closedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (closedCount > 0 && failedCount == 0)
        {
            ClearStatus();
        }
        else if (closedCount == 0)
        {
            ShowStatus("Uygulamaya normal kapanma isteği gönderilemedi. Zorla kapatmak için süreç sonlandırma seçeneğini kullanın.");
        }
        else
        {
            ShowStatus("Bazı uygulama süreçlerine normal kapanma isteği gönderilemedi.");
        }
    }

    private void TerminateDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: string executablePath })
        {
            return;
        }

        var (processes, inaccessibleCount) = FindProcessesForExecutable(executablePath);
        if (processes.Count == 0)
        {
            ShowStatus(inaccessibleCount == 0
                ? "Uygulamanın çalışan süreci bulunamadı."
                : "Uygulama süreçlerine erişilemiyor; uygulama yöneticisi iznini denetleyin.");
            return;
        }

        var appName = _pinnedApps.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))?.Name
            ?? Path.GetFileNameWithoutExtension(executablePath);
        var confirmation = MessageBox.Show(
            UiText.Format("TerminatePrompt", appName, processes.Count),
            UiText.Get("TerminateTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            DisposeProcesses(processes);
            return;
        }

        var terminatedCount = 0;
        var failedCount = inaccessibleCount;
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    failedCount++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                terminatedCount++;
            }
            catch (InvalidOperationException)
            {
                // The process exited while the menu command was running.
            }
            catch (Win32Exception)
            {
                failedCount++;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (failedCount == 0 && terminatedCount > 0)
        {
            ClearStatus();
        }
        else if (terminatedCount == 0)
        {
            ShowStatus("Uygulama süreçleri sonlandırılamadı. Yetkiyi ve uygulamanın tekrar başlamadığını denetleyin.");
        }
        else
        {
            ShowStatus("Bazı uygulama süreçleri sonlandırılamadı.");
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_preferences);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        _dockPosition = _preferences.Current.Position;
        _thumbnailPreview.AnimationsEnabled = _preferences.Current.AnimationsEnabled;
        UpdateClock();
        UpdateSpotlightHotkey();
        ApplyBarWidgetsVisibility();
        _ = RefreshDockStatusAsync();
        _autoHideController?.SetMode(_preferences.Current.AutoHide);
        _replacementSession?.UpdateAutoHide(_preferences.Current.AutoHide != AutoHideMode.Disabled);
        ApplyEdgeAndLayout();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        ResetMagnification();
        if (_windowTracker is not null)
        {
            UpdateNotificationBadges(_pinnedApps, _windowTracker.Snapshot);
            UpdateNotificationBadges(_runningApps, _windowTracker.Snapshot);
        }
        if (_replacementSession?.IsActive == true)
        {
            ApplyDockAlignment();
        }
        else
        {
            PositionDock();
        }

    }

    private static MenuItem? FindMenuItem(ItemsControl owner, string tag)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                return item;
            }

            var nested = FindMenuItem(item, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetMenuItemVisibility(ItemsControl owner, string tag, bool visible)
    {
        var item = FindMenuItem(owner, tag);
        if (item is not null)
        {
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetSeparatorVisibility(ItemsControl owner, string tag, bool visible)
    {
        var separator = owner.Items.OfType<Separator>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
        if (separator is not null)
        {
            separator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetMenuCommandParameter(ItemsControl owner, string executablePath)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            item.CommandParameter = executablePath;
            SetMenuCommandParameter(item, executablePath);
        }
    }

    private static (List<Process> Processes, int InaccessibleCount) FindProcessesForExecutable(string executablePath)
    {
        var matchingProcesses = new List<Process>();
        var inaccessibleCount = 0;
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    matchingProcesses.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (Win32Exception)
            {
                inaccessibleCount++;
                process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        return (matchingProcesses, inaccessibleCount);
    }

    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private void ApplyDockAlignment()
    {
        var alignment = _dockPosition switch
        {
            DockPosition.Left => HorizontalAlignment.Left,
            DockPosition.Center => HorizontalAlignment.Center,
            DockPosition.Right => HorizontalAlignment.Right,
            _ => throw new InvalidOperationException("Dock konumu desteklenmiyor.")
        };

        var full = _preferences.Current.Layout == DockLayout.FullWidth;
        DockSurface.HorizontalAlignment = full ? HorizontalAlignment.Stretch : alignment;
        AppDockContainer.HorizontalAlignment = full ? alignment : HorizontalAlignment.Center;
        _replacementSession?.RefreshPosition();
    }

    private void ApplyTaskbarLayout(Size area)
    {
        _dockArea = area;
        Application.Current.Resources["DockCellHeight"] = 40.0;
        Application.Current.Resources["DockIconSize"] = 30.0;
        ApplyEdgeAndLayout();
    }

    private void OnFullScreenChanged(bool suppressed)
    {
        if (!suppressed) return;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        ResetMagnification();
    }

    private void ResizeDockToContent()
    {
        // Fit app cells and auto-size the floating dock pill cleanly around enabled widgets + apps.
        var availableWidth = _dockArea.Width > 0 ? _dockArea.Width : SystemParameters.PrimaryScreenWidth;
        var count = Math.Max(1, _pinnedApps.Count + _runningApps.Count);
        LeftZonePanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        SystemTrayPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var sideZonesWidth = LeftZonePanel.DesiredSize.Width + SystemTrayPanel.DesiredSize.Width + 48;
        var statusWidth = string.IsNullOrEmpty(StatusText.Text) ? 0 : 180;
        var centerAvailable = Math.Max(96, availableWidth - sideZonesWidth - statusWidth - 24);
        Application.Current.Resources["DockCellSize"] = Math.Min(42, Math.Max(28, centerAvailable / count));

        DockContents.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var padding = DockSurface.Padding;
        var margin = DockSurface.Margin;
        var border = DockSurface.BorderThickness;
        var desiredWidth = Math.Ceiling(DockContents.DesiredSize.Width + padding.Left + padding.Right
            + margin.Left + margin.Right + border.Left + border.Right);
        var maximumWidth = Math.Max(16, availableWidth);
        var dockWidth = Math.Clamp(desiredWidth, 16, maximumWidth);
        if (_preferences.Current.Layout == DockLayout.FullWidth)
        {
            DockSurface.Width = double.NaN;
            if (_dockArea.Width <= 0) Width = availableWidth;
            return;
        }
        DockSurface.Width = Math.Max(16, dockWidth - margin.Left - margin.Right);
        if (_dockArea.Width <= 0) Width = dockWidth;
    }

    private static bool ExcludeFromTaskSwitcher(nint windowHandle)
    {
        Marshal.SetLastPInvokeError(0);
        var currentStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
        if (currentStyle == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            return false;
        }

        var style = new nint((currentStyle.ToInt64() | WsExToolWindow) & ~WsExAppWindow);
        if (style != currentStyle)
        {
            Marshal.SetLastPInvokeError(0);
            var previousStyle = SetWindowLongPtr(windowHandle, GwlExStyle, style);
            if (previousStyle == 0 && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }
        }

        return SetWindowPos(windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void ExitOpenTaskbar_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal string? RestoreWindowsTaskbar()
    {
        var session = _replacementSession;
        if (session is null) return null;
        var error = session.Stop();
        if (session.IsActive) return error;
        session.OperationFailed -= ShowStatus;
        session.LayoutChanged -= ApplyTaskbarLayout;
        session.FullScreenChanged -= OnFullScreenChanged;
        session.Dispose();
        _replacementSession = null;
        return error;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var error = RestoreWindowsTaskbar();
        if (error is not null) Trace.TraceWarning(error);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _preferences.Changed -= Preferences_Changed;
        _settingsWindow?.CloseForShutdown();
        _spotlightWindow?.Close();
        _widgetsWindow?.Close();
        _spotlightEngine.Dispose();
        _audioService.Changed -= UpdateDockStatusIcons;
        _mediaService.Changed -= UpdateDockMediaCapsule;
        _productivity.PomodoroChanged -= UpdateDockPomodoroCapsule;
        _productivity.Dispose();
        _audioService.Dispose();
        _dockStatusTimer.Stop();
        _dockTelemetryTimer.Stop();
        if (_hotkeyRegistered && _source is { IsDisposed: false })
        {
            UnregisterHotKey(_source.Handle, SpotlightHotkeyId);
            _hotkeyRegistered = false;
        }
        _jumpListLoad?.Cancel();
        _jumpListLoad?.Dispose();
        if (_openContextMenu is not null) _openContextMenu.IsOpen = false;
        _openContextMenu = null;
        _magnifiedIcons.Clear();
        _source?.RemoveHook(WindowProc);
        _source = null;

        if (_windowTracker is not null)
        {
            _windowTracker.WindowsChanged -= WindowTracker_WindowsChanged;
            _windowTracker.ForegroundWindowChanged -= WindowTracker_ForegroundWindowChanged;
            _windowTracker.Dispose();
            _windowTracker = null;
        }

        _previewCloseTimer.Stop();
        _previewCloseTimer.Tick -= PreviewCloseTimer_Tick;
        _clockTimer.Stop();
        _thumbnailPreview.PointerEntered -= ThumbnailPreview_PointerEntered;
        _thumbnailPreview.PointerLeft -= ThumbnailPreview_PointerLeft;
        _thumbnailPreview.PreviewClicked -= ThumbnailPreview_PreviewClicked;
        _thumbnailPreview.CloseRequested -= ThumbnailPreview_CloseRequested;
        _thumbnailPreview.Dispose();
        _autoHideController?.Dispose();
        _autoHideController = null;
        _jumpLists.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
