using System.Diagnostics;
using System.Threading;
using System.Windows;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    public ApplicationPreferences Preferences { get; } = new(new DockSettingsStore());

    protected override void OnStartup(StartupEventArgs e)
    {
        UiText.Current.SetLanguage(Preferences.Current.Language);
        ThemeManager.Apply(Preferences.Current);
        _instanceMutex = new Mutex(initiallyOwned: true, name: "Local\\OpenTaskbar.Singleton", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(UiText.Get("AlreadyRunning"), "OpenTaskbar", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var error = (MainWindow as OpenTaskbar.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null)
        {
            Trace.TraceError(error);
            e.ApplicationExitCode = 1;
        }
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        var error = (MainWindow as OpenTaskbar.MainWindow)?.RestoreWindowsTaskbar();
        if (error is not null) Trace.TraceError(error);
        base.OnSessionEnding(e);
    }
}
