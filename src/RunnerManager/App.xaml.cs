using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace RunnerManager;

public partial class App : Application
{
    /// <summary>Exits right away. An update is test-started with it, to check that its .NET runtime is installed.</summary>
    public const string ProbeArgument = "--probe";
    /// <summary>Passed by the launch task, so a copy that still is not elevated asks with UAC instead of looping.</summary>
    public const string FromTaskArgument = "--from-task";
    /// <summary>Added when a copy asks for administrator rights, so a copy that still is not elevated stops instead of looping.</summary>
    private const string ElevationAttemptedArgument = "--elevation-attempted";
    // Kept from the RunnerWidget versions, so an older and a newer dashboard never run side by side.
    private const string DashboardMutexName = @"Local\RunnerWidgetDashboard";
    /// <summary>
    /// Set by a second launch to bring the running dashboard to the front. An event, because a named pipe limited to the
    /// current user refuses elevated dashboards (their objects are owned by the Administrators group, not the user).
    /// </summary>
    private const string ActivateEventName = @"Local\RunnerManagerDashboard.Activate";
    /// <summary>Set by setup and uninstall to ask the dashboard to exit, before they end it.</summary>
    internal const string ExitEventName = @"Local\RunnerManagerDashboard.Exit";
    /// <summary>How long a new dashboard waits for one that is exiting (for example after "Restart now").</summary>
    private static readonly TimeSpan PreviousDashboardExitTimeout = TimeSpan.FromSeconds(10);
    private Mutex? _dashboardMutex;
    private bool _ownsDashboardMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;

    /// <summary>
    /// This copy got administrator rights from a UAC prompt, not from the launch task: the task may be missing, turned off
    /// or point at another exe, so the dashboard checks it again.
    /// </summary>
    internal static bool ElevatedByPrompt { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ElevatedByPrompt = e.Args.Contains(ElevationAttemptedArgument, StringComparer.OrdinalIgnoreCase);
        if (e.Args.Contains(ProbeArgument, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(0);
            return;
        }
        // Started by an AI tool, without administrator rights: changes go through the fleet agent.
        if (AssistantHost.IsAssistantLaunch(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(AppSettings.Load().Theme);
            DispatcherUnhandledException += (_, args) =>
            {
                MachineRelay.AppendAgentLog($"Assistant error: {args.Exception.GetBaseException().Message}");
                args.Handled = true;
            };
            int exitCode;
            try { exitCode = await AssistantHost.RunAsync(e.Args); }
            catch (Exception ex)
            {
                MachineRelay.AppendAgentLog($"Assistant stopped after an unexpected error: {ex.Message}");
                exitCode = 1;
            }
            Shutdown(exitCode);
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
        // Runner Manager installs per user. Elevated with another account's password, it would set itself up in that
        // account's profile (its own %LOCALAPPDATA%, registry and tasks), so it stops instead.
        if (Elevation.OtherUserThanStarted(e.Args) is { } started)
        {
            var message = $"Runner Manager needs a Windows account with administrator rights. It was started by {started}, " +
                          $"but with the password of {Elevation.CurrentUserName}, so it would set itself up for {Elevation.CurrentUserName} instead. " +
                          "Sign in to Windows with an administrator account and start Runner Manager there.";
            MachineRelay.AppendAgentLog($"Stopped: elevated as {Elevation.CurrentUserName}, started by {started}.");
            if (!e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show(message, "Runner Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

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
            // An update that keeps crashing is undone here; the task then starts the previous version again. A second
            // agent exits right away (only one may run), so it is not counted.
            var another = await MachineRelay.IsAgentRunningAsync();
            if (!another && !UpdateGuard.OnAgentStarting())
            {
                AppUpdater.ScheduleAgentTaskRestart(ScheduledTasks.AgentTaskName);
                Shutdown(1);
                return;
            }
            // A task registered by an older version lacks the keep-alive trigger. Checked once the agent has proven
            // itself, because re-registering may restart it.
            if (!another)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(UpdateGuard.HealthyAfter + TimeSpan.FromMinutes(1));
                    try
                    {
                        if (await ScheduledTasks.UpdateAgentTaskIfOutdatedAsync(fromAgent: true))
                            MachineRelay.AppendAgentLog("Updated the fleet agent task: it now starts again by itself when it stopped.");
                    }
                    catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not update the fleet agent task: {ex.Message}"); }
                });
            try { await MachineRelay.RunAgentAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                MachineRelay.AppendAgentLog($"Agent stopped after an unexpected error: {ex}");
                // A non-zero exit lets the scheduled task's restart-on-failure policy bring it back.
                Shutdown(1);
                return;
            }
            if (!another) UpdateGuard.OnAgentExiting();
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(AppSettings.Load().Theme);
            await UninstallAsync(e.Args);
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

        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // "Restart now": the dashboard that started this one is exiting. Wait for it, so it is not asked to come to the front.
        if (AfterProcessId(e.Args) is { } previous) await WaitForExitAsync(previous, PreviousDashboardExitTimeout);

        // One dashboard per user: a second launch brings the running one to the front instead.
        _dashboardMutex = new Mutex(initiallyOwned: false, DashboardMutexName);
        _ownsDashboardMutex = TryOwnDashboardMutex();
        if (!_ownsDashboardMutex)
        {
            if (await SignalRunningDashboardAsync())
            {
                Shutdown();
                return;
            }
            // Nobody answered: the other dashboard is exiting. Wait for it, then take over.
            // This stays on the UI thread, because a mutex belongs to the thread that acquired it.
            _ownsDashboardMutex = await WaitForDashboardMutexAsync(PreviousDashboardExitTimeout);
            // Still running: it may have started listening meanwhile. Never open a second dashboard if it answers.
            if (!_ownsDashboardMutex && await SignalRunningDashboardAsync())
            {
                Shutdown();
                return;
            }
            // It holds the lock but does not answer (it hangs). Never open a second dashboard beside it.
            if (!_ownsDashboardMutex) _ownsDashboardMutex = !minimized && await OfferToCloseHungDashboardAsync();
            if (!_ownsDashboardMutex)
            {
                MachineRelay.AppendAgentLog("Another dashboard is running but does not respond. This one was not opened.");
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUiUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--fleet=", StringComparison.OrdinalIgnoreCase)) window.PostSetupFleet = arg[8..].ToLowerInvariant();
        }
        window.Closed += (_, _) => ExitApplication();
        ListenForActivation(window);
        if (minimized)
            await window.StartAsync();
        else
            window.Show();
    }

    /// <summary>Passed by "Restart now" with the id of the dashboard that exits, so the new one waits for it.</summary>
    internal const string AfterArgument = "--after=";

    internal static int? AfterProcessId(string[] args)
        => args.LastOrDefault(a => a.StartsWith(AfterArgument, StringComparison.OrdinalIgnoreCase)) is { } arg
           && int.TryParse(arg[AfterArgument.Length..], out var id) && id > 0 ? id : null;

    /// <summary>Starts the installed dashboard again (after an update) and exits this one.</summary>
    public async void RestartDashboard()
    {
        // Leaving while this dashboard restarts the fleet agent could leave the agent stopped.
        if (MainWindow is MainWindow { IsAgentWorkBusy: true } main)
        {
            main.AddActivity("Runner Manager restarts once the fleet agent has restarted.");
            await main.WaitForAgentWorkAsync();
        }
        var start = new ProcessStartInfo(AppUpdater.InstalledExe) { UseShellExecute = true, WorkingDirectory = AppUpdater.InstallDir };
        start.ArgumentList.Add(AfterArgument + Environment.ProcessId);
        Process.Start(start);
        ExitApplication();
    }

    private static async Task WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var wait = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(wait.Token);
        }
        catch { }
    }

    private async Task<bool> WaitForDashboardMutexAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            if (TryOwnDashboardMutex()) return true;
        }
        return false;
    }

    /// <summary>
    /// A dashboard that holds the single-instance lock but does not answer: offers to end it, then takes over. Returns
    /// whether this copy now owns the lock.
    /// </summary>
    private async Task<bool> OfferToCloseHungDashboardAsync()
    {
        const string close = "End it and open";
        if (DialogWindow.Show(null, DialogTone.Warning, "Runner Manager does not respond",
                "Runner Manager is already running, but it does not respond. End it and open a new window?", close, topmost: true) != close)
            return false;
        foreach (var process in OtherDashboardsInThisSession())
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch { }
            }
        }
        return await WaitForDashboardMutexAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Other Runner Manager processes in this Windows session that are dashboards (not the agent or an AI tool).</summary>
    private static List<Process> OtherDashboardsInThisSession()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = new List<Process>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, SessionId, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
            foreach (System.Management.ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var id = Convert.ToInt32(item["ProcessId"]);
                    // Split loosely: only whole flags are compared, so a path with spaces does no harm.
                    var arguments = (item["CommandLine"]?.ToString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(a => a.Trim('"').ToLowerInvariant()).ToArray();
                    if (id == Environment.ProcessId || Convert.ToInt32(item["SessionId"]) != session) continue;
                    if (arguments.Any(a => a is "--agent" or "--setup" or "--uninstall" or "--install-agent" or AssistantHost.McpArgument or AssistantHost.CliArgument)) continue;
                    try { found.Add(Process.GetProcessById(id)); } catch (ArgumentException) { }
                }
            }
        }
        catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not list running dashboards: {ex.Message}"); }
        return found;
    }

    /// <summary>
    /// Opening the installed dashboard (Start menu, desktop shortcut) goes through the launch task, which runs it elevated
    /// without a UAC prompt. Setup, uninstall, other copies, or a missing launch task ask with UAC.
    /// </summary>
    private static async Task HandOffToElevatedAsync(string[] args)
    {
        // A standard user with UAC turned off gets another copy without administrator rights; stop instead of looping.
        if (args.Contains(ElevationAttemptedArgument, StringComparer.OrdinalIgnoreCase))
        {
            MachineRelay.AppendAgentLog("Runner Manager could not get administrator rights.");
            if (!args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
                MessageBox.Show("Runner Manager needs administrator rights. Sign in with an administrator account and try again.",
                    "Runner Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var fromTask = args.Contains(FromTaskArgument, StringComparer.OrdinalIgnoreCase);
        if (args.Length == 0 && AppUpdater.IsRunningInstalledCopy && await Elevation.TryStartLaunchTaskAsync()) return;
        // The elevated copy checks that it still runs as this user (a standard user may type an administrator's password).
        var relaunchArgs = args.Where(a => !a.Equals(FromTaskArgument, StringComparison.OrdinalIgnoreCase))
            .Append(ElevationAttemptedArgument).Append(Elevation.UserSidArgument + Elevation.CurrentUserSid);
        if (Elevation.RelaunchElevated(relaunchArgs) && fromTask)
            MachineRelay.AppendAgentLog("The launch task started Runner Manager without administrator rights. Asked with UAC instead.");
    }

    /// <summary>Passed by Settings after the user confirmed there, so the uninstaller does not ask again.</summary>
    internal const string ConfirmedArgument = "--confirmed";
    internal const string RemoveDataArgument = "--remove-data";

    /// <summary>Asks whether to uninstall. Null when cancelled, otherwise whether to delete settings and logs too.</summary>
    internal static bool? AskUninstall(Window? owner)
    {
        const string remove = "Uninstall";
        const string removeAll = "Uninstall and delete settings";
        var choice = DialogWindow.Show(owner, DialogTone.Danger, "Uninstall Runner Manager?",
            "This removes Runner Manager, the fleet agent, shortcuts and startup task, and removes this PC from the fleet. " +
            "GitHub runners keep running.",
            remove, removeAll);
        return choice == null ? null : choice == removeAll;
    }

    private static async Task UninstallAsync(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
        bool removeData;
        if (quiet) removeData = false;
        else if (args.Contains(ConfirmedArgument, StringComparer.OrdinalIgnoreCase))
            removeData = args.Contains(RemoveDataArgument, StringComparer.OrdinalIgnoreCase);
        else if (AskUninstall(null) is { } choice) removeData = choice;
        else return;
        try
        {
            var note = await Installer.UninstallAsync(removeData, message => MachineRelay.AppendAgentLog($"Uninstall: {message}"));
            if (!quiet) DialogWindow.Info(null, "Runner Manager is uninstalled", $"Your runners continue to run as Windows services.{note}");
        }
        catch (Exception ex)
        {
            if (!quiet) DialogWindow.Error(null, "Could not uninstall Runner Manager", ex.Message);
        }
    }

    /// <summary>
    /// Exit from the tray: asks first while an operation runs, because stopping halfway (for example between stopping and
    /// starting the fleet agent) can leave things stopped.
    /// </summary>
    public void RequestExit()
    {
        if (MainWindow is MainWindow window && !window.ConfirmExitDuringOperation()) return;
        ExitApplication();
    }

    /// <summary>Exits the dashboard even when it is set to keep running in the tray.</summary>
    public void ExitApplication()
    {
        // Stop answering activation first, so a dashboard that starts now takes over instead of signalling this one.
        _activateWait?.Unregister(null);
        _activateWait = null;
        _activateEvent?.Dispose();
        _activateEvent = null;
        _exitWait?.Unregister(null);
        _exitWait = null;
        _exitEvent?.Dispose();
        _exitEvent = null;
        if (MainWindow is MainWindow window) window.PrepareExit();
        if (_ownsDashboardMutex)
        {
            try { _dashboardMutex?.ReleaseMutex(); } catch { }
            _ownsDashboardMutex = false;
        }
        _dashboardMutex?.Dispose();
        _dashboardMutex = null;
        Shutdown();
    }

    /// <summary>Whether a dashboard runs in this Windows session (also from a copy without administrator rights).</summary>
    internal static bool DashboardIsRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(DashboardMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        // It exists, but belongs to the elevated dashboard.
        catch (UnauthorizedAccessException) { return true; }
        catch { return false; }
    }

    private bool TryOwnDashboardMutex()
    {
        try { return _dashboardMutex!.WaitOne(0); }
        // The previous dashboard ended without releasing it; the mutex now belongs to this process.
        catch (AbandonedMutexException) { return true; }
    }

    private void ListenForActivation(MainWindow window)
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
                (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not listen for activation: {ex.Message}"); }
        // Setup replaces the exe, and uninstall removes it: they ask the dashboard to exit before they would end it.
        try
        {
            _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
                (_, _) => Dispatcher.BeginInvoke(ExitApplication), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not listen for exit requests: {ex.Message}"); }
    }

    /// <summary>Asks the running dashboard to come to the front. Returns false when no dashboard is listening.</summary>
    private static Task<bool> SignalRunningDashboardAsync()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            return Task.FromResult(activate.Set());
        }
        catch { return Task.FromResult(false); }
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
