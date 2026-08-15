using System.Windows;
using BCSTool.Infrastructure;
using BCSTool.Services;
using BCSTool.ViewModels;

namespace BCSTool;

/// <summary>
/// Application entry point for BCS Tool.
///
/// WPF creates this class from App.xaml. Its responsibilities are deliberately
/// limited to application-level concerns:
///
/// 1. Make sure only one copy of BCS Tool is running.
/// 2. Construct the shared services used by the rest of the application.
/// 3. Create the MainViewModel and MainWindow.
/// 4. Dispose application-wide resources when the program closes.
///
/// Keeping this logic here prevents MainWindow from becoming responsible for
/// dependency creation and global application lifetime management.
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private SingleInstanceGuard? _legacySingleInstance;
    private MainViewModel? _viewModel;
    private UpdateService? _updateService;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Always allow WPF to perform its normal startup work first.
        base.OnStartup(e);

        // A named Windows mutex acts as a machine-wide "only one instance"
        // lock. This prevents two watchdogs from both trying to manage the
        // same Bannerlord server.
        // Use the canonical BCS Tool mutex. Also acquire the pre-rename mutex
        // for cross-version safety so an older build cannot manage the same
        // server at the same time during the transition.
        _singleInstance = new SingleInstanceGuard("BCS Tool_v1");
        _legacySingleInstance = new SingleInstanceGuard(
            "BCS_" + "Server" + "Tool_v1");

        if (!_singleInstance.TryAcquire() ||
            !_legacySingleInstance.TryAcquire())
        {
            MessageBox.Show(
                "BCS Tool is already running.",
                "BCS Tool",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // Create the application's services.
        //
        // We are doing simple manual dependency injection here rather than
        // bringing in a third-party DI framework. That keeps the application
        // easy to understand and avoids extra NuGet dependencies.
        var settingsService = new SettingsService();
        var themeService = new ThemeService(settingsService);
        themeService.Initialize();
        var logService = new LogService();
        _updateService = new UpdateService(logService);
        var portMonitor = new PortMonitor();
        var processManager = new ServerProcessManager(logService);
        var restartScheduler = new RestartScheduler();
        var playerRosterTracker = new PlayerRosterTracker();
        var serverExecutableLocator = new ServerExecutableLocator();
        var coopConfigService = new CoopConfigService();

        // The log-driven runtime requires server-config.json logFile=true.
        // Re-enable it on every BCS Tool launch if a previously generated
        // configuration was externally changed to false. A missing config is
        // normal before the server's first launch and is simply ignored.
        try
        {
            if (coopConfigService.EnsureServerLoggingEnabled())
            {
                logService.Write(
                    "Required server logging was disabled; restored server-config.json logFile=true.");
            }
        }
        catch (Exception ex)
        {
            logService.Write(
                $"Could not enforce required server logging at startup: {ex.Message}");
        }

        var serverLogMonitor = new ServerLogMonitor(
            logService,
            coopConfigService);
        var nativeSaveBackupService = new NativeSaveBackupService(
            coopConfigService);
        var playerAccessService = new PlayerAccessService(
            logService,
            coopConfigService);

        // MainViewModel coordinates the UI with all server-management logic.
        _viewModel = new MainViewModel(
            settingsService,
            logService,
            portMonitor,
            processManager,
            restartScheduler,
            playerRosterTracker,
            serverLogMonitor,
            serverExecutableLocator,
            nativeSaveBackupService,
            playerAccessService);

        var window = new MainWindow(
            _viewModel,
            coopConfigService,
            _updateService,
            themeService);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose long-lived objects such as timers, mutexes, and Process
        // wrappers before allowing the application to fully exit.
        _viewModel?.Dispose();
        _updateService?.Dispose();
        _legacySingleInstance?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
