using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClaudeSessionsSidekick.Services;
using Velopack;

namespace ClaudeSessionsSidekick;

public partial class App : Application
{
    private Mutex? _mutex;

    [System.STAThread]
    public static void Main(string[] args)
    {
        // Velopack hooks (--veloapp-install / firstrun / uninstall / obsolete)
        // re-launch the process with these args; Build().Run() handles them and
        // calls Environment.Exit before returning. MUST happen before any WPF
        // resources are touched — or hook invocations leak a window flash.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

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

        // Clear stale Windows toast notifications from the previous installed
        // version. NotifyIcon balloons get duplicated into Action Center on
        // Win10+; without this, a user who saw "Update available: 1.0.0-rc2"
        // before updating still sees that stale notification after the update
        // finishes. Best-effort: dev builds without a registered AppUserModelID
        // will throw — swallow.
        try
        {
            // `global::` escape — WPF's `Application` has a `Windows` collection
            // property that otherwise shadows the WinRT `Windows.*` namespace.
            global::Windows.UI.Notifications.ToastNotificationManager.History.Clear();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not clear toast notification history: {ex.Message}");
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
