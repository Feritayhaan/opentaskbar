using System.Media;
using System.Windows;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services.Widgets;

/// <summary>
/// Central place for dock alerts. Respects quiet hours: while they are active alerts are held and shown
/// (collapsed into one summary when there are several) once the quiet window ends.
/// </summary>
public sealed class AlertCenter
{
    private sealed record PendingAlert(string Title, string Message, string GlyphKey, Action? OnClick);

    private readonly Func<DockSettings> _settings;
    private readonly Func<(Rect Area, bool FromTop)> _anchor;
    private readonly List<PendingAlert> _held = [];
    private readonly DispatcherTimer _flushTimer;
    private ToastWindow? _current;

    public AlertCenter(Dispatcher dispatcher, Func<DockSettings> settings, Func<(Rect Area, bool FromTop)> anchor)
    {
        _settings = settings;
        _anchor = anchor;
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _flushTimer.Tick += (_, _) => FlushIfAllowed();
        _flushTimer.Start();
    }

    public bool IsQuiet => QuietHours.IsActive(_settings(), DateTime.Now);

    public void Notify(string title, string message, string glyphKey = "IconSparkles", Action? onClick = null, bool sound = false)
    {
        if (IsQuiet)
        {
            if (_held.Count < 20) _held.Add(new PendingAlert(title, message, glyphKey, onClick));
            return;
        }
        Show(new PendingAlert(title, message, glyphKey, onClick), sound);
    }

    /// <summary>Plays an alert sound unless quiet hours are active.</summary>
    public void PlaySound()
    {
        if (IsQuiet) return;
        try { SystemSounds.Asterisk.Play(); } catch { }
    }

    private void FlushIfAllowed()
    {
        if (_held.Count == 0 || IsQuiet) return;
        var held = _held.ToList();
        _held.Clear();
        if (held.Count == 1)
        {
            Show(held[0], sound: false);
            return;
        }
        Show(new PendingAlert(
            UiText.Format("QuietSummaryTitle", held.Count),
            string.Join(" · ", held.Take(3).Select(h => h.Title)),
            "IconSparkles", null), sound: false);
    }

    private void Show(PendingAlert alert, bool sound)
    {
        if (Application.Current is null || Application.Current.Dispatcher.HasShutdownStarted) return;
        _current?.Close();
        var (area, fromTop) = _anchor();
        var window = new ToastWindow(alert.Title, alert.Message, alert.GlyphKey, area, fromTop, alert.OnClick);
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };
        _current = window;
        window.Show();
        if (sound) PlaySound();
    }
}
