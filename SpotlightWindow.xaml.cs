using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OpenTaskbar.Services;
using OpenTaskbar.Services.Search;

namespace OpenTaskbar;

public partial class SpotlightWindow : Window
{
    private readonly SpotlightEngine _engine;
    private readonly List<SearchResult> _currentResults = [];
    private DateTime _lastClosedUtc;
    private bool _suppressDeactivate;

    public SpotlightWindow(SpotlightEngine engine)
    {
        _engine = engine;
        InitializeComponent();
        _engine.IndexUpdated += OnIndexUpdated;
    }

    public bool WasJustClosed => (DateTime.UtcNow - _lastClosedUtc).TotalMilliseconds < 220;

    public void OpenCentered()
    {
        _engine.WarmIndexes();
        PositionOnActiveMonitor();
        SearchBox.Text = "";
        RefreshResults();
        Show();
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            ClosePopup();
        }
        else if (!WasJustClosed)
        {
            OpenCentered();
        }
    }

    private void ClosePopup()
    {
        if (!IsVisible) return;
        _lastClosedUtc = DateTime.UtcNow;
        Hide();
    }

    private void OnIndexUpdated()
    {
        if (IsVisible) RefreshResults();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(SearchBox.Text);
        PlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        RefreshResults();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void RefreshResults()
    {
        var results = _engine.Search(SearchBox.Text);
        _currentResults.Clear();
        _currentResults.AddRange(results);

        var view = CollectionViewSource.GetDefaultView(_currentResults);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchResult.GroupName)));
        ResultsList.ItemsSource = view;
        view.Refresh();

        if (_currentResults.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            ResultsList.ScrollIntoView(_currentResults[0]);
            ResultsList.Visibility = Visibility.Visible;
            EmptyStateText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyStateText.Text = _engine.IsAppIndexReady ? UiText.Get("NoResults") : UiText.Get("IndexingApps");
            EmptyStateText.Visibility = Visibility.Visible;
        }
    }

    private void ExecuteSelected(bool revealOnly = false)
    {
        if (ResultsList.SelectedItem is not SearchResult item) return;

        if (revealOnly && item.Reveal is not null)
        {
            ClosePopup();
            item.Reveal();
            return;
        }

        _suppressDeactivate = true;
        try
        {
            if (item.Execute())
            {
                ClosePopup();
            }
        }
        finally
        {
            _suppressDeactivate = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                ClosePopup();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                MoveSelection(5);
                break;
            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-5);
                break;
            case Key.Enter:
                e.Handled = true;
                var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                ExecuteSelected(revealOnly: ctrl);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_currentResults.Count == 0) return;
        var next = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _currentResults.Count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(_currentResults[next]);
    }

    private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { DataContext: SearchResult item })
        {
            ResultsList.SelectedItem = item;
            ExecuteSelected();
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelected();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_suppressDeactivate) ClosePopup();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Application.Current.Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            ClosePopup();
            return;
        }

        base.OnClosing(e);
    }

    private void PositionOnActiveMonitor()
    {
        if (!GetCursorPos(out var cursor))
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + area.Height * 0.16;
            return;
        }

        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + area.Height * 0.16;
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        var workLeft = info.Work.Left / scaleX;
        var workTop = info.Work.Top / scaleY;
        var workWidth = (info.Work.Right - info.Work.Left) / scaleX;
        var workHeight = (info.Work.Bottom - info.Work.Top) / scaleY;

        Left = workLeft + (workWidth - Width) / 2;
        Top = workTop + Math.Max(48, workHeight * 0.14);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
