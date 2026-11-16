using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class DockAutoHideController : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;

    private readonly Window _window;
    private readonly nint _windowHandle;
    private readonly TranslateTransform _dockTranslate;
    private readonly FrameworkElement _dockSurface;
    private readonly Func<bool> _isInteractionLocked;
    private readonly DispatcherTimer _hideTimer;
    private bool _disposed;

    public AutoHideMode Mode { get; private set; } = AutoHideMode.Disabled;
    public DockEdge Edge { get; private set; } = DockEdge.Bottom;
    public bool IsHidden { get; private set; }
    public bool IsMouseOverDock { get; private set; }
    public bool IsMouseAtEdge { get; private set; }
    public bool HasOverlap { get; private set; }

    public event Action<bool>? VisibilityChanged;

    public DockAutoHideController(
        Window window,
        nint windowHandle,
        TranslateTransform dockTranslate,
        FrameworkElement dockSurface,
        Func<bool> isInteractionLocked)
    {
        _window = window;
        _windowHandle = windowHandle;
        _dockTranslate = dockTranslate;
        _dockSurface = dockSurface;
        _isInteractionLocked = isInteractionLocked;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _hideTimer.Tick += HideTimer_Tick;
    }

    public void SetEdge(DockEdge edge)
    {
        if (Edge == edge) return;
        Edge = edge;
        // Park the surface at its new resting position; hidden docks slide off the matching screen edge.
        _dockTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _dockTranslate.Y = IsHidden ? HideOffset : 0;
    }

    private double HideOffset
    {
        get
        {
            var distance = Math.Max(72, _dockSurface.ActualHeight + 16);
            return Edge == DockEdge.Top ? -distance : distance;
        }
    }

    public void SetMode(AutoHideMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        if (Mode == AutoHideMode.Disabled)
        {
            _hideTimer.Stop();
            AnimateReveal();
        }
        else
        {
            EvaluateState();
        }
    }

    public void OnMouseEnterDock()
    {
        IsMouseOverDock = true;
        _hideTimer.Stop();
        if (IsHidden)
        {
            AnimateReveal();
        }
    }

    public void OnMouseLeaveDock()
    {
        IsMouseOverDock = false;
        EvaluateState();
    }

    public void OnMouseEnterEdge()
    {
        IsMouseAtEdge = true;
        _hideTimer.Stop();
        if (IsHidden)
        {
            AnimateReveal();
        }
    }

    public void OnMouseLeaveEdge()
    {
        IsMouseAtEdge = false;
        EvaluateState();
    }

    public void CheckForegroundWindow(nint foregroundHwnd)
    {
        if (Mode != AutoHideMode.Smart) return;
        HasOverlap = EvaluateWindowOverlap(foregroundHwnd);
        EvaluateState();
    }

    public void EvaluateState()
    {
        if (_disposed || Mode == AutoHideMode.Disabled) return;

        if (ShouldHide())
        {
            if (!IsHidden && !_hideTimer.IsEnabled)
            {
                _hideTimer.Start();
            }
        }
        else
        {
            _hideTimer.Stop();
            if (IsHidden)
            {
                AnimateReveal();
            }
        }
    }

    private bool ShouldHide()
    {
        if (Mode == AutoHideMode.Disabled) return false;
        if (IsMouseOverDock || IsMouseAtEdge) return false;
        if (_isInteractionLocked()) return false;
        if (Mode == AutoHideMode.Always) return true;
        if (Mode == AutoHideMode.Smart) return HasOverlap;
        return false;
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (ShouldHide() && !IsHidden)
        {
            AnimateHide();
        }
    }

    public void AnimateReveal()
    {
        IsHidden = false;
        _hideTimer.Stop();
        var anim = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _dockTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        VisibilityChanged?.Invoke(true);
    }

    public void AnimateHide()
    {
        IsHidden = true;
        var anim = new DoubleAnimation
        {
            To = HideOffset,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        _dockTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        VisibilityChanged?.Invoke(false);
    }

    private bool EvaluateWindowOverlap(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        if (hwnd == _windowHandle)
            return false;

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, 64);
        var cls = className.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;

        var dockMonitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var windowMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (dockMonitor != windowMonitor)
            return false;

        if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (DwmGetWindowAttribute(hwnd, 9, out Rect winRect, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(hwnd, out winRect))
            return false;

        if (GetWindowRect(_windowHandle, out var dockRect))
        {
            if (Edge == DockEdge.Top)
            {
                int effectiveDockBottom = dockRect.Bottom - 28;
                return winRect.Left < dockRect.Right
                    && winRect.Right > dockRect.Left
                    && winRect.Top < effectiveDockBottom
                    && winRect.Bottom > dockRect.Top;
            }

            int effectiveDockTop = dockRect.Top + 28;
            return winRect.Left < dockRect.Right
                && winRect.Right > dockRect.Left
                && winRect.Bottom > effectiveDockTop
                && winRect.Top < dockRect.Bottom;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hideTimer.Stop();
        _hideTimer.Tick -= HideTimer_Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint windowHandle, int attribute, out Rect value, int size);
}
