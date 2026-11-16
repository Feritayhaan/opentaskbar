using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows.Threading;

namespace OpenTaskbar.Services.Widgets;

public sealed record TodoItem(string Id, string Text, bool Done);

public sealed record NotesState(List<TodoItem> Todos, string QuickNote);

/// <summary>Shared Pomodoro focus timer and local-first Notes/To-Do store synced between the bar and Widget Hub.</summary>
public sealed class ProductivityService : IDisposable
{
    private const int FocusMinutes = 25;
    private const int BreakMinutes = 5;

    private readonly DispatcherTimer _timer;
    private readonly string _notesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "notes.json");

    public ProductivityService(Dispatcher dispatcher)
    {
        RemainingSeconds = FocusMinutes * 60;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _toolTimer.Tick += ToolTimer_Tick;
        LoadNotes();
    }

    public bool IsRunning { get; private set; }
    public bool IsBreak { get; private set; }
    public int RemainingSeconds { get; private set; }
    public string FormattedTime => $"{RemainingSeconds / 60:D2}:{RemainingSeconds % 60:D2}";

    public List<TodoItem> Todos { get; private set; } = [];
    public string QuickNote { get; private set; } = "";

    public event Action? PomodoroChanged;
    public event Action? TimerChanged;

    /// <summary>Raised when a pomodoro phase or the countdown timer completes (title, message). Routed through quiet hours.</summary>
    public Action<string, string>? Alert { get; set; }

    // ---- Countdown timer & stopwatch --------------------------------------------------------------------
    private readonly DispatcherTimer _toolTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _timerEndUtc;
    private DateTime _stopwatchStartUtc;
    private TimeSpan _stopwatchAccumulated = TimeSpan.Zero;
    private TimeSpan _timerPaused = TimeSpan.Zero;

    public int TimerPresetMinutes { get; private set; } = 10;
    public bool IsTimerRunning { get; private set; }
    public bool IsStopwatchRunning { get; private set; }

    public TimeSpan TimerRemaining => IsTimerRunning
        ? (_timerEndUtc - DateTime.UtcNow is { } r && r > TimeSpan.Zero ? r : TimeSpan.Zero)
        : (_timerPaused > TimeSpan.Zero ? _timerPaused : TimeSpan.FromMinutes(TimerPresetMinutes));

    public TimeSpan StopwatchElapsed => IsStopwatchRunning
        ? _stopwatchAccumulated + (DateTime.UtcNow - _stopwatchStartUtc)
        : _stopwatchAccumulated;

    public void SetTimerPreset(int minutes)
    {
        TimerPresetMinutes = Math.Clamp(minutes, 1, 600);
        if (!IsTimerRunning) _timerPaused = TimeSpan.Zero;
        TimerChanged?.Invoke();
    }

    public void ToggleTimer()
    {
        if (IsTimerRunning)
        {
            _timerPaused = TimerRemaining;
            IsTimerRunning = false;
        }
        else
        {
            var remaining = TimerRemaining;
            if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMinutes(TimerPresetMinutes);
            _timerEndUtc = DateTime.UtcNow + remaining;
            IsTimerRunning = true;
        }
        UpdateToolTimer();
        TimerChanged?.Invoke();
    }

    public void ResetTimer()
    {
        IsTimerRunning = false;
        _timerPaused = TimeSpan.Zero;
        UpdateToolTimer();
        TimerChanged?.Invoke();
    }

    public void ToggleStopwatch()
    {
        if (IsStopwatchRunning)
        {
            _stopwatchAccumulated += DateTime.UtcNow - _stopwatchStartUtc;
            IsStopwatchRunning = false;
        }
        else
        {
            _stopwatchStartUtc = DateTime.UtcNow;
            IsStopwatchRunning = true;
        }
        UpdateToolTimer();
        TimerChanged?.Invoke();
    }

    public void ResetStopwatch()
    {
        IsStopwatchRunning = false;
        _stopwatchAccumulated = TimeSpan.Zero;
        UpdateToolTimer();
        TimerChanged?.Invoke();
    }

    private void UpdateToolTimer()
    {
        if (IsTimerRunning || IsStopwatchRunning) _toolTimer.Start();
        else _toolTimer.Stop();
    }

    private void ToolTimer_Tick(object? sender, EventArgs e)
    {
        if (IsTimerRunning && DateTime.UtcNow >= _timerEndUtc)
        {
            IsTimerRunning = false;
            _timerPaused = TimeSpan.Zero;
            UpdateToolTimer();
            Alert?.Invoke(UiText.Get("TimerDoneTitle"), UiText.Format("TimerDoneMessage", TimerPresetMinutes));
        }
        TimerChanged?.Invoke();
    }

    public static string FormatSpan(TimeSpan span, bool tenths = false)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var text = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes:D2}:{span.Seconds:D2}";
        return tenths ? $"{text}.{span.Milliseconds / 100}" : text;
    }

    public void TogglePomodoro()
    {
        IsRunning = !IsRunning;
        if (IsRunning) _timer.Start();
        else _timer.Stop();
        PomodoroChanged?.Invoke();
    }

    public void ResetPomodoro()
    {
        IsRunning = false;
        _timer.Stop();
        RemainingSeconds = (IsBreak ? BreakMinutes : FocusMinutes) * 60;
        PomodoroChanged?.Invoke();
    }

    public void SetBreakMode(bool isBreak)
    {
        IsBreak = isBreak;
        ResetPomodoro();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (RemainingSeconds > 1)
        {
            RemainingSeconds--;
            PomodoroChanged?.Invoke();
            return;
        }

        IsRunning = false;
        _timer.Stop();
        var wasFocus = !IsBreak;
        IsBreak = !IsBreak;
        RemainingSeconds = (IsBreak ? BreakMinutes : FocusMinutes) * 60;
        if (Alert is not null)
            Alert(UiText.Get(wasFocus ? "PomodoroFocusDone" : "PomodoroBreakDone"), UiText.Get(wasFocus ? "PomodoroTakeBreak" : "PomodoroBackToFocus"));
        else
            try { SystemSounds.Asterisk.Play(); } catch { }
        PomodoroChanged?.Invoke();
    }

    public void AddTodo(string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0) return;
        Todos.Insert(0, new TodoItem(Guid.NewGuid().ToString("N"), clean, false));
        SaveNotes();
    }

    public void ToggleTodo(string id)
    {
        var idx = Todos.FindIndex(t => t.Id == id);
        if (idx < 0) return;
        var item = Todos[idx];
        Todos[idx] = item with { Done = !item.Done };
        SaveNotes();
    }

    public void RemoveTodo(string id)
    {
        Todos.RemoveAll(t => t.Id == id);
        SaveNotes();
    }

    public void UpdateQuickNote(string note)
    {
        if (string.Equals(QuickNote, note, StringComparison.Ordinal)) return;
        QuickNote = note;
        SaveNotes();
    }

    private void LoadNotes()
    {
        try
        {
            if (!File.Exists(_notesPath)) return;
            var state = JsonSerializer.Deserialize<NotesState>(File.ReadAllText(_notesPath));
            if (state is null) return;
            Todos = state.Todos ?? [];
            QuickNote = state.QuickNote ?? "";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Notes load failed: {0}", ex.Message);
        }
    }

    private void SaveNotes()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_notesPath)!);
            var json = JsonSerializer.Serialize(new NotesState(Todos, QuickNote), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_notesPath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Notes save failed: {0}", ex.Message);
        }
    }

    public void Dispose() { _timer.Stop(); _toolTimer.Stop(); }
}
