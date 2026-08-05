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
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Always allow WPF to perform its normal startup work first.
        base.OnStartup(e);

        // A named Windows mutex acts as a machine-wide "only one instance"
        // lock. This prevents two watchdogs from both trying to manage the
        // same Bannerlord server.
        // Keep the legacy mutex identifier so an older BCS Tool build and
        // this renamed build cannot both manage the same server concurrently.
        _singleInstance = new SingleInstanceGuard("BCS_ServerTool_v1");

        if (!_singleInstance.TryAcquire())
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
        // bringing in a third-party DI framework. That keeps v1.0 easy to
        // understand and avoids extra NuGet dependencies.
        var settingsService = new SettingsService();
        var logService = new LogService();
        var portMonitor = new PortMonitor();
        var processManager = new ServerProcessManager(logService);
        var restartScheduler = new RestartScheduler();
        var playerRosterTracker = new PlayerRosterTracker();
        var serverExecutableLocator = new ServerExecutableLocator();
        var coopConfigService = new CoopConfigService();
        var saveBackupService = new SaveBackupService(coopConfigService);

        // MainViewModel coordinates the UI with all server-management logic.
        _viewModel = new MainViewModel(
            settingsService,
            logService,
            portMonitor,
            processManager,
            restartScheduler,
            playerRosterTracker,
            serverExecutableLocator,
            saveBackupService);

        var window = new MainWindow(
            _viewModel,
            coopConfigService);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose long-lived objects such as timers, mutexes, and Process
        // wrappers before allowing the application to fully exit.
        _viewModel?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
