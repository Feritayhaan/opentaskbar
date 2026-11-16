using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OpenTaskbar;

/// <summary>Small non-activating notification card shown near the dock for automations and timers.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private readonly Action? _onClick;
    private bool _closing;

    public ToastWindow(string title, string message, string glyphKey, Rect anchorArea, bool fromTop, Action? onClick)
    {
        InitializeComponent();
        _onClick = onClick;
        ToastTitle.Text = title;
        ToastMessage.Text = message;
        ToastGlyph.Data = FindResource(glyphKey) as Geometry ?? FindResource("IconSparkles") as Geometry;

        _closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(7) };
        _closeTimer.Tick += (_, _) => Dismiss();

        Loaded += (_, _) =>
        {
            UpdateLayout();
            Left = Math.Max(anchorArea.Left, anchorArea.Right - ActualWidth - 6);
            Top = fromTop ? anchorArea.Top + 2 : Math.Max(anchorArea.Top, anchorArea.Bottom - ActualHeight - 2);
            var slide = fromTop ? -14 : 14;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            CardTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slide, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            _closeTimer.Start();
        };
    }

    // The toast must never steal focus from the user's current application.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int gwlExStyle = -20;
        var style = GetWindowLongPtr(handle, gwlExStyle).ToInt64();
        SetWindowLongPtr(handle, gwlExStyle, new nint(style | 0x08000000L | 0x00000080L)); // NOACTIVATE | TOOLWINDOW
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _closeTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _onClick?.Invoke();
        Dismiss();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
