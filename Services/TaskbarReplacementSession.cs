using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class TaskbarReplacementSession : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbmGetState = 0x00000004;
    private const uint AbeTop = 1;
    private const uint AbeBottom = 3;
    private const uint AbmSetState = 0x0000000A;
    private const uint AbsAutoHide = 0x00000001;
    private const uint AbsAlwaysOnTop = 0x00000002;
    private const uint SupportedTaskbarState = AbsAutoHide | AbsAlwaysOnTop;
    private const uint AbnStateChange = 0;
    private const uint AbnPosChanged = 1;
    private const uint AbnFullScreenApp = 2;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint CallbackMessage = 0x8000 + 0x52;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint GaRoot = 2;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsChild = 0x40000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly nint _handle;
    private bool _registered;
    private bool _active;
    private bool _stateChangeStarted;
    private uint _previousTaskbarState;
    private bool _disposed;
    private bool _fullScreenSuppressed;
    private double _nativeTaskbarHeight = 48;
    private const double MinDockRowHeightDips = 50;
    private const double DockHeadroomDips = 20;
    private bool _autoHideEnabled;
    private DockEdge _edge = DockEdge.Bottom;
    private Size _lastLayout;
    private int _stopInProgress;

    public TaskbarReplacementSession(Window window, nint handle)
    {
        _window = window;
        _handle = handle;
        AppDomain.CurrentDomain.ProcessExit += Process_Exit;
    }

    public bool IsActive => _active || _stateChangeStarted;

    public event Action<string>? OperationFailed;
    public event Action<Size>? LayoutChanged;
    public event Action<bool>? FullScreenChanged;

    public void UpdateForeground(nint windowHandle)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == Environment.ProcessId)
        {
            SetFullScreenSuppression(false);
            return;
        }
        var fullScreen = IsFullScreen(windowHandle);
        SetFullScreenSuppression(fullScreen);
        if (!fullScreen)
        {
            _ = SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }
    }

    private static bool HasPendingRecovery(out string? error)
    {
        error = null;
        if (!File.Exists(JournalPath))
        {
            return false;
        }

        try
        {
            _ = ReadJournal();
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Görev çubuğu kurtarma kaydı bozuk. Windows ayarlarını elle denetleyin.";
        }
        catch (IOException)
        {
            error = "Görev çubuğu kurtarma kaydı okunamadı.";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Görev çubuğu kurtarma kaydına erişim izni yok.";
        }

        return false;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            return;
        }

        var pending = HasPendingRecovery(out var recoveryError);
        if (recoveryError is not null)
        {
            throw new InvalidOperationException(recoveryError);
        }

        _previousTaskbarState = pending ? ReadJournal() : GetTaskbarState();
        var nativeTaskbar = FindWindow("Shell_TrayWnd", null);
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
        }
        try
        {
            if (!pending)
            {
                WriteJournal(_previousTaskbarState);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydı yazılamadı.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydına yazma izni yok.", ex);
        }

        try
        {
            _stateChangeStarted = true;
            var replacementState = _previousTaskbarState & AbsAlwaysOnTop;
            _ = SetTaskbarState(replacementState, _handle);

            if (!HideNativeTaskbar())
            {
                throw new InvalidOperationException("Windows görev çubuğu gizlenemedi.");
            }

            var data = CreateData();
            data.uCallbackMessage = CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                throw new InvalidOperationException("OpenTaskbar Windows'a AppBar olarak kaydedilemedi.");
            }

            _registered = true;
            _active = true;
            PositionDockWindow();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void HandleTaskbarCreated()
    {
        if (!_active)
        {
            return;
        }

        _registered = false;
        var nativeTaskbar = FindWindow("Shell_TrayWnd", null);
        if (nativeTaskbar != 0 && GetWindowRect(nativeTaskbar, out var taskbarRect))
        {
            var nativeDpi = GetDpiForWindow(nativeTaskbar);
            if (nativeDpi > 0 && taskbarRect.Bottom > taskbarRect.Top
                && taskbarRect.Right - taskbarRect.Left > taskbarRect.Bottom - taskbarRect.Top)
            {
                _nativeTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) * 96d / nativeDpi;
            }
        }

        SetTaskbarState(TargetTaskbarState(), _handle);
        HideNativeTaskbar();

        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;
        if (SHAppBarMessage(AbmNew, ref data) != 0)
        {
            _registered = true;
        }

        PositionDockWindow();
    }

    public bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0)
        {
            return false;
        }

        if (message == CallbackMessage)
        {
            switch (unchecked((uint)wParam.ToInt64()))
            {
                case AbnPosChanged:
                    EnforceNativeTaskbarHidden();
                    PositionDockWindow();
                    return true;
                case AbnFullScreenApp:
                    if (lParam != 0)
                    {
                        UpdateForeground(GetForegroundWindow());
                    }
                    else
                    {
                        SetFullScreenSuppression(false);
                        PositionDockWindow();
                    }
                    return true;
                case AbnStateChange:
                    EnforceNativeTaskbarHidden();
                    return true;
            }
        }

        if (message is WmDisplayChange or WmDpiChanged)
        {
            PositionDockWindow();
        }

        return false;
    }

    public string? Stop()
    {
        if (Interlocked.CompareExchange(ref _stopInProgress, 1, 0) != 0)
        {
            return "Windows görev çubuğu geri yükleme işlemi devam ediyor.";
        }

        try
        {
            if (!_active && !_registered && !_stateChangeStarted) return null;

            // Stop enforcing hidden state before Shell calls can reenter our AppBar callback.
            _active = false;
            if (_registered)
            {
                var data = CreateData();
                SHAppBarMessage(AbmRemove, ref data);
                _registered = false;
            }

            _ = SetTaskbarState(_previousTaskbarState, _handle);
            var taskbarShown = ShowNativeTaskbar();
            _stateChangeStarted = false;
            var journalError = DeleteJournal();
            if (!taskbarShown)
                return "Windows görev çubuğu geri yüklenemedi. Kapatmayı yeniden deneyin veya Windows görev çubuğu ayarlarını denetleyin.";

            return journalError;
        }
        finally
        {
            Volatile.Write(ref _stopInProgress, 0);
        }
    }

    public void RefreshPosition()
    {
        PositionDockWindow();
    }

    /// <summary>
    /// The native taskbar auto-hides while a top-placed dock is active so its reserved strip at the bottom disappears;
    /// the top dock reserves its own strip through an AppBar instead.
    /// </summary>
    private uint TargetTaskbarState() =>
        (_previousTaskbarState & AbsAlwaysOnTop) | (_autoHideEnabled || _edge == DockEdge.Top ? AbsAutoHide : 0);

    public void UpdateEdge(DockEdge edge)
    {
        if (_edge == edge) return;
        _edge = edge;
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;

        // Re-register so Windows drops the old reservation before the new edge claims its strip.
        if (_registered)
        {
            var remove = CreateData();
            SHAppBarMessage(AbmRemove, ref remove);
            _registered = false;
        }
        var data = CreateData();
        data.uCallbackMessage = CallbackMessage;
        if (SHAppBarMessage(AbmNew, ref data) != 0) _registered = true;
        SetTaskbarState(TargetTaskbarState(), _handle);
        HideNativeTaskbar();
        PositionDockWindow();
    }

    public void UpdateAutoHide(bool autoHide)
    {
        _autoHideEnabled = autoHide;
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        SetTaskbarState(TargetTaskbarState(), _handle);
        PositionDockWindow();
    }

    private void PositionDockWindow()
    {
        if ((!_active && !_registered) || Volatile.Read(ref _stopInProgress) != 0)
        {
            return;
        }

        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Ekran boyutları alınamadı.");
        }

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0) return;
        var reservedHeight = monitorInfo.rcMonitor.Bottom - monitorInfo.rcWork.Bottom;
        var minRowPixels = (int)Math.Round(MinDockRowHeightDips * dpi / 96d);
        var preferredHeightPixels = (int)Math.Round(_nativeTaskbarHeight * dpi / 96d);
        var height = Math.Max(minRowPixels, Math.Max(reservedHeight, preferredHeightPixels));
        var headroom = (int)Math.Round(DockHeadroomDips * dpi / 96d);
        var totalHeight = height + headroom;
        var layout = new Size((monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left) * 96d / dpi,
            height * 96d / dpi);
        var totalHeightDips = totalHeight * 96d / dpi;
        if (_lastLayout != layout)
        {
            _lastLayout = layout;
            _window.Width = layout.Width;
            _window.Height = totalHeightDips;
            LayoutChanged?.Invoke(layout);
        }
        var monitorLeft = monitorInfo.rcMonitor.Left;
        var monitorRight = monitorInfo.rcMonitor.Right;
        var width = monitorRight - monitorLeft;
        var left = monitorLeft;
        UpdateForeground(GetForegroundWindow());
        // Bottom: Explorer already reserves the hidden native taskbar's area; another appbar position would reserve a second strip.
        var top = monitorInfo.rcMonitor.Bottom - totalHeight;
        if (_edge == DockEdge.Top)
        {
            // Top: reserve a strip with an AppBar (nothing while the dock itself auto-hides) so windows maximize below it.
            var bar = CreateData();
            bar.uEdge = AbeTop;
            bar.rc = new Rect(monitorLeft, monitorInfo.rcMonitor.Top, monitorRight, monitorInfo.rcMonitor.Top + height);
            SHAppBarMessage(AbmQueryPos, ref bar);
            bar.rc.Bottom = bar.rc.Top + (_autoHideEnabled ? 0 : height);
            SHAppBarMessage(AbmSetPos, ref bar);
            top = monitorInfo.rcMonitor.Top;
        }
        if (!SetWindowPos(_handle, _fullScreenSuppressed ? new nint(1) : HwndTopmost, left, top,
                width, totalHeight,
                SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException("OpenTaskbar ekranın alt kenarına yerleştirilemedi.");
        }
    }

    private bool IsFullScreen(nint windowHandle)
    {
        if (windowHandle == 0) return false;
        var root = GetAncestor(windowHandle, GaRoot);
        if (root != 0) windowHandle = root;

        if (!IsWindowVisible(windowHandle) || IsIconic(windowHandle) || IsZoomed(windowHandle))
            return false;

        if (DwmGetWindowAttribute(windowHandle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        var exStyle = GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64();
        if ((style & WsChild) != 0 || (style & WsCaption) == WsCaption)
            return false;
        if ((exStyle & (WsExToolWindow | WsExTransparent | WsExNoActivate)) != 0)
            return false;

        if (MonitorFromWindow(windowHandle, MonitorDefaultToNearest) != MonitorFromWindow(_handle, MonitorDefaultToNearest))
            return false;

        var className = new StringBuilder(128);
        GetClassName(windowHandle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow"
            or "ForegroundStaging" or "EdgeUiInputTopWndClass")
            return false;

        var monitor = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(windowHandle, MonitorDefaultToNearest), ref monitor))
            return false;

        if (DwmGetWindowAttribute(windowHandle, DwmwaExtendedFrameBounds, out Rect rect, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(windowHandle, out rect))
            return false;

        return rect.Left <= monitor.rcMonitor.Left && rect.Top <= monitor.rcMonitor.Top
            && rect.Right >= monitor.rcMonitor.Right && rect.Bottom >= monitor.rcMonitor.Bottom;
    }

    private void SetFullScreenSuppression(bool suppressed)
    {
        if (_fullScreenSuppressed == suppressed) return;
        _fullScreenSuppressed = suppressed;
        ShowWindow(_handle, suppressed ? SwHide : SwShowNoActivate);
        FullScreenChanged?.Invoke(suppressed);
    }

    private static uint GetTaskbarState()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        return unchecked((uint)SHAppBarMessage(AbmGetState, ref data));
    }

    private static bool HideNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        if (IsWindowVisible(taskbarHandle))
        {
            ShowWindow(taskbarHandle, SwHide);
        }

        return !IsWindowVisible(taskbarHandle);
    }

    private static bool ShowNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        ShowWindow(taskbarHandle, SwShowNoActivate);
        return IsWindowVisible(taskbarHandle);
    }

    private void EnforceNativeTaskbarHidden()
    {
        if (!_active || Volatile.Read(ref _stopInProgress) != 0) return;
        _ = SetTaskbarState(TargetTaskbarState(), _handle);
        if (!HideNativeTaskbar())
        {
            OperationFailed?.Invoke("Windows görev çubuğu yeniden gizlenemedi.");
        }
    }

    private static bool SetTaskbarState(uint state, nint windowHandle)
    {
        var data = new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = windowHandle,
            lParam = unchecked((nint)state)
        };
        SHAppBarMessage(AbmSetState, ref data);
        return (GetTaskbarState() & SupportedTaskbarState) == (state & SupportedTaskbarState);
    }

    private AppBarData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "taskbar-state.txt");

    private static void WriteJournal(uint state)
    {
        var path = JournalPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new IOException("Görev çubuğu kurtarma kaydı için yol oluşturulamadı.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, state.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static uint ReadJournal()
    {
        var text = File.ReadAllText(JournalPath).Trim();
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            || (state & ~SupportedTaskbarState) != 0)
        {
            throw new InvalidDataException("Görev çubuğu kurtarma kaydı geçersiz.");
        }

        return state;
    }

    private static string? DeleteJournal()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }

            return null;
        }
        catch (IOException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydı silinemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydını silme izni yok.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var error = Stop();
        if (error is not null) Trace.TraceError(error);
        if (IsActive) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= Process_Exit;
    }

    private void Process_Exit(object? sender, EventArgs e)
    {
        var error = Stop();
        if (error is not null) Trace.TraceError(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder name, int length);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out Rect value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref Rect pvParam, uint winIni);
}
