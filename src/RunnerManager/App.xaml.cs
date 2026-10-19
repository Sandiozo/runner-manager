using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;

namespace RunnerManager;

public partial class App : Application
{
    /// <summary>Exits right away. An update is test-started with it, to check that its .NET runtime is installed.</summary>
    public const string ProbeArgument = "--probe";
    /// <summary>Passed by the launch task, so a copy that still is not elevated asks with UAC instead of looping.</summary>
    public const string FromTaskArgument = "--from-task";
    // Kept from the RunnerWidget versions, so an older and a newer dashboard never run side by side.
    private const string DashboardMutexName = @"Local\RunnerWidgetDashboard";
    private const string ActivatePipeName = "RunnerWidgetDashboard.Activate";
    private Mutex? _dashboardMutex;
    private CancellationTokenSource? _pipeCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains(ProbeArgument, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(0);
            return;
        }
        if (!Elevation.IsElevated)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await HandOffToElevatedAsync(e.Args);
            Shutdown();
            return;
        }
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            MachineRelay.AppendAgentLog($"Unobserved task error: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };

        if (e.Args.Any(a => a.Equals("--install-agent", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await MachineRelay.InstallAndStartAgentTaskAsync(); }
            catch (Exception ex) { MachineRelay.AppendAgentLog($"Agent install failed: {ex.Message}"); }
            finally { Shutdown(); }
            return;
        }

        if (e.Args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (_, args) =>
            {
                MachineRelay.AppendAgentLog($"Unhandled agent error: {args.Exception}");
                args.Handled = true;
            };
            try { await MachineRelay.RunAgentAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                MachineRelay.AppendAgentLog($"Agent stopped after an unexpected error: {ex}");
                // A non-zero exit lets the scheduled task's restart-on-failure policy bring it back.
                Shutdown(1);
                return;
            }
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(AppSettings.Load().Theme);
            await UninstallAsync(quiet: e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)));
            Shutdown();
            return;
        }

        if (Installer.IsSetupLaunch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            ThemeManager.Apply(AppTheme.Dark);
            DispatcherUnhandledException += OnUiUnhandledException;
            var setup = new SetupWindow();
            MainWindow = setup;
            setup.Show();
            return;
        }

        // One dashboard per user: a second launch brings the running one to the front instead.
        _dashboardMutex = new Mutex(initiallyOwned: true, DashboardMutexName, out var createdNew);
        if (!createdNew)
        {
            await SignalRunningDashboardAsync();
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        DispatcherUnhandledException += OnUiUnhandledException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow();
        MainWindow = window;
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--fleet=", StringComparison.OrdinalIgnoreCase)) window.PostSetupFleet = arg[8..].ToLowerInvariant();
            if (arg.Equals("--rdc=setup", StringComparison.OrdinalIgnoreCase)) window.PostSetupRdc = true;
        }
        window.Closed += (_, _) => ExitApplication();
        ListenForActivation(window);
        if (e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            await window.StartAsync();
        else
            window.Show();
    }

    /// <summary>
    /// Opening the installed dashboard (Start menu, desktop shortcut) goes through the launch task, which runs it elevated
    /// without a UAC prompt. Setup, uninstall, other copies, or a missing launch task ask with UAC.
    /// </summary>
    private static async Task HandOffToElevatedAsync(string[] args)
    {
        var fromTask = args.Contains(FromTaskArgument, StringComparer.OrdinalIgnoreCase);
        if (args.Length == 0 && AppUpdater.IsRunningInstalledCopy && await Elevation.TryStartLaunchTaskAsync()) return;
        Elevation.RelaunchElevated(args.Where(a => !a.Equals(FromTaskArgument, StringComparison.OrdinalIgnoreCase)));
        if (fromTask) MachineRelay.AppendAgentLog("The launch task started Runner Manager without administrator rights. Asked with UAC instead.");
    }

    private static async Task UninstallAsync(bool quiet)
    {
        bool removeData;
        if (quiet) removeData = false;
        else
        {
            const string remove = "Uninstall";
            const string removeAll = "Uninstall and delete settings";
            var choice = DialogWindow.Show(null, DialogTone.Danger, "Uninstall Runner Manager?",
                "This removes Runner Manager, the fleet agent, shortcuts and startup task, and removes this PC from the fleet. " +
                "GitHub runners and Remote Desktop Commander keep running.",
                remove, removeAll);
            if (choice == null) return;
            removeData = choice == removeAll;
        }
        try
        {
            await Installer.UninstallAsync(removeData, message => MachineRelay.AppendAgentLog($"Uninstall: {message}"));
            if (!quiet) DialogWindow.Info(null, "Runner Manager is uninstalled", "Your runners continue to run as Windows services.");
        }
        catch (Exception ex)
        {
            if (!quiet) DialogWindow.Error(null, "Could not uninstall Runner Manager", ex.Message);
        }
    }

    /// <summary>Exits the dashboard even when it is set to keep running in the tray.</summary>
    public void ExitApplication()
    {
        if (MainWindow is MainWindow window) window.PrepareExit();
        _pipeCts?.Cancel();
        try { _dashboardMutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    private void ListenForActivation(MainWindow window)
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(ActivatePipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    await Dispatcher.InvokeAsync(window.ShowFromTray);
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(1000, CancellationToken.None); }
            }
        });
    }

    private static async Task SignalRunningDashboardAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", ActivatePipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(2000);
        }
        catch { }
    }

    private void OnUiUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the dashboard alive: a failed button handler should show an error, not close the app.
        e.Handled = true;
        var message = e.Exception.GetBaseException().Message;
        MachineRelay.AppendAgentLog($"Dashboard error: {e.Exception}");
        if (MainWindow is MainWindow window) window.ReportError(message);
        else MessageBox.Show(message, "Runner Manager", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
