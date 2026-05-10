using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ClaudeSessionsSidekick_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // We don't own the mutex (another instance does); discard the handle so OnExit
            // doesn't call ReleaseMutex on a non-owned mutex (throws ApplicationException).
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // Catch unhandled exceptions to log them instead of crashing silently
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI thread exception", args.Exception);
            args.Handled = true; // Keep app running
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled AppDomain exception", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
