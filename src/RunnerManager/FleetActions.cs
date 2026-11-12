using System.Net.Http;

namespace RunnerManager;

/// <summary>
/// Runner operations across the fleet, shared by the dashboard and the AI assistant tools. Work on another PC is sent to
/// its fleet agent. Work on this PC runs directly when the caller has administrator rights (the dashboard), or goes
/// through this PC's fleet agent when it does not (assistant tools).
/// </summary>
public sealed class FleetActions
{
    private readonly bool _viaLocalAgent;

    public FleetActions(bool viaLocalAgent) => _viaLocalAgent = viaLocalAgent;

    /// <summary>
    /// The longest a runner create may take on a PC, and so how long a create on another PC (also the one of a move) is
    /// waited for. RunnerService gives the download up to 45 minutes and config.cmd up to 10; finding the runner
    /// package, the checksum, unpacking, the registration token and the workflow check take a few minutes more. No other
    /// command takes longer, so the waits for commands and for an update restart are as long (see
    /// <see cref="MachineRelay.MaxCommandWait"/>).
    /// </summary>
    internal static readonly TimeSpan CreateWait = TimeSpan.FromMinutes(60);

    /// <summary>Runs an action on this PC directly, or sends it to a fleet agent and waits for its result.</summary>
    public async Task RunOnMachineAsync(MachineInfo machine, string target, string targetKey, string action, Action<string> log,
        Func<Task> local, TimeSpan timeout, string runnerName = "", string[]? labels = null, string value = "",
        RunnerRegistrationOptions? options = null)
    {
        if (machine.IsLocal && !_viaLocalAgent)
        {
            await local();
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        try { await MachineRelay.SendCommandAsync(machine.MachineKey, target, targetKey, action, runnerName, labels, value, id, options); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // The agent may have queued it before its answer was lost. Sending the same ID again is safe: each ID runs once.
            log($"{machine.MachineName} did not answer. Sending the request again…");
            await MachineRelay.SendCommandAsync(machine.MachineKey, target, targetKey, action, runnerName, labels, value, id, options);
        }
        var result = await MachineRelay.WaitForCommandAsync(machine.MachineKey, id, timeout);
        log($"{machine.MachineName}: {result}");
    }

    /// <summary>The registration URL for a runner scope ("repos/owner/repo" or "orgs/name").</summary>
    public static string RegistrationUrl(string scope)
        => scope.StartsWith("repos/", StringComparison.OrdinalIgnoreCase) ? $"https://github.com/{scope[6..]}"
            : scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase) ? $"https://github.com/{scope[5..]}"
            : string.Empty;

    public Task CreateRunnerAsync(MachineInfo machine, string url, string name, string[] labels, Action<string> log)
        => RunOnMachineAsync(machine, "runner", url, "Create", log,
            () => RunnerService.CreateRunnerAsync(new RunnerSetup
            {
                RepositoryUrl = url,
                RunnerName = name,
                InstallPath = RunnerService.SuggestInstallPath(url),
                Labels = labels
            }, log),
            CreateWait, runnerName: name, labels: labels);

    /// <summary>
    /// Why Start, Stop or Restart can't be used on a runner, or null. A runner can have a Windows service and still run in
    /// a console window instead: starting the service would run it twice, and stopping it would not stop the runner.
    /// A runner that a startup entry opens in a console window at sign-in would also run twice, even while that window
    /// is not open, and so would one that runs twice already when its service is restarted. Stop is always possible then.
    /// </summary>
    /// <param name="consoleAtSignIn">A startup entry that is turned on opens it in a console window at sign-in.</param>
    /// <param name="runsTwice">Its Windows service runs, and a console window runs it too or opens it at sign-in.</param>
    internal static string? ConsoleRunnerProblem(bool interactive, bool serviceInstalled, string action,
        bool consoleAtSignIn = false, bool runsTwice = false)
    {
        if (!serviceInstalled) return null;
        if (action == "Stop")
            return interactive ? "This runner runs in a console window, not as its Windows service. Close the console window to stop it." : null;
        if (runsTwice)
            return "This runner runs twice: as its Windows service and in a console window. Starting or restarting the service keeps it so. " +
                   "Stop the Windows service to keep the console window, or turn off “Start when you sign in” and close the console window, then start the service.";
        if (interactive)
            return "This runner runs in a console window. Starting its Windows service would run it twice. Close the console window first.";
        return consoleAtSignIn
            ? "This runner is set to open in a console window when you sign in. Starting its Windows service would run it twice. " +
              "Turn off “Start when you sign in” first, or use the console window."
            : null;
    }

    /// <param name="action">"Start", "Stop" or "Restart".</param>
    public Task ControlRunnerAsync(MachineInfo machine, MachineRunnerStatus runner, string action, Action<string> log)
        => RunOnMachineAsync(machine, "runner", runner.CommandKey, action, log,
            async () =>
            {
                if (ConsoleRunnerProblem(runner.IsInteractive, runner.ServiceInstalled, action, runner.ConsoleAtSignIn, runner.RunsTwice) is { } problem)
                    throw new InvalidOperationException(problem);
                await RunnerService.ControlServiceAsync(runner.CommandKey, action);
                FleetConfig.SetManuallyStopped(runner.CommandKey, action == "Stop");
            },
            TimeSpan.FromMinutes(2));

    public async Task RemoveRunnerAsync(MachineInfo machine, MachineRunnerStatus runner, bool deleteFiles, Action<string> log)
        => await RunOnMachineAsync(machine, "runner", runner.CommandKey, "Remove", log,
            async () =>
            {
                await RunnerService.RemoveRunnerAsync(runner.CommandKey, deleteFiles, log);
                // Only here: a runner on another PC is cleared by its agent, and the same service name may exist on this PC.
                FleetConfig.SetManuallyStopped(runner.CommandKey, false);
            },
            TimeSpan.FromMinutes(3), value: deleteFiles ? "delete" : "keep");

    /// <summary>
    /// Moves a runner: stops and disables it, registers it on the target PC with the same name and labels (replacing
    /// the old registration), then removes the old copy. If the target reports that registering failed, the old copy's
    /// service starts again as it did before the move, and is started again unless it was disabled, a console window
    /// ran the runner, or it was stopped (<see cref="StartAfterFailedMoveAsync"/>).
    /// </summary>
    public async Task MoveRunnerAsync(MachineInfo source, MachineRunnerStatus runner, MachineInfo target, bool deleteOldFiles,
        string[]? knownLabels, Action<string> log)
    {
        var url = RegistrationUrl(runner.Scope);
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("The runner's repository or organization is unknown.");

        var labels = await LabelsToKeepAsync(runner, knownLabels);
        log(labels.Length > 0 ? $"Keeping labels: {string.Join(", ", labels)}" : "No custom labels to keep.");
        var options = runner.RegistrationOptions ?? RunnerRegistrationOptions.None;

        // 1. Stop the old copy so it cannot pick up a job during the move (and so auto-recovery leaves it alone), and
        //    disable its service so it doesn't start again at the next restart. Agents before protocol 12 only stop it.
        log($"Stopping the runner on {source.MachineName}…");
        await RunOnMachineAsync(source, "runner", runner.CommandKey, "Stop", log,
            () => StopForMoveAsync(runner.CommandKey), TimeSpan.FromMinutes(2), value: "disable");

        // 2. Register it on the target, replacing the old registration.
        try
        {
            log($"Installing and registering the runner on {target.MachineName}. The download usually takes 1–3 minutes, and longer on a slow connection…");
            await RunOnMachineAsync(target, "runner", url, "Create", log,
                () => RunnerService.CreateRunnerAsync(new RunnerSetup
                {
                    RepositoryUrl = url,
                    RunnerName = runner.AgentName,
                    InstallPath = RunnerService.SuggestInstallPath(url),
                    Labels = labels,
                    ReplaceExisting = true,
                    Options = options
                }, log),
                CreateWait, runnerName: runner.AgentName, labels: labels, value: "replace", options: options);
        }
        catch (Exception ex) when (!RunsHere(target) && ex is TimeoutException or OperationCanceledException)
        {
            // No answer: the target may still finish registering with --replace, which takes the registration from the
            // old copy. Starting the old copy now could leave neither of them working.
            log($"{target.MachineName} did not report whether the runner was registered. The runner on {source.MachineName} stays stopped. " +
                $"If {runner.TitleText} appears on {target.MachineName}, remove the old copy from {source.MachineName}; otherwise move it again.");
            throw;
        }
        catch (Exception ex)
        {
            if (UndoesFailedMove(runner, source, RunsHere(source)))
            {
                log($"Move failed. Setting up the runner on {source.MachineName} as it was before…");
                try
                {
                    await RunOnMachineAsync(source, "runner", runner.CommandKey, "Start", log,
                        async () => log(await StartAfterFailedMoveAsync(runner.CommandKey)), TimeSpan.FromMinutes(2), value: "enable");
                }
                catch (Exception restartError) { log($"Could not set up the runner as it was before: {restartError.Message}"); }
            }
            else log(NotUndoneText(source.MachineName));
            // config.cmd on the target replaced the registration before the install failed. The old copy is set up again as
            // before the move, so it can be edited, but GitHub no longer accepts it.
            if (ex.Message.Contains(RunnerService.ReplacedRegistrationNote, StringComparison.Ordinal))
                log($"{runner.TitleText} on {source.MachineName} must be registered again before it can take jobs. " +
                    "Move it again, or edit it and change its name or labels.");
            throw;
        }

        // 3. Remove the old copy (its registration now belongs to the target).
        log($"Removing the old copy from {source.MachineName}…");
        try
        {
            await RunOnMachineAsync(source, "runner", runner.CommandKey, "Retire", log,
                async () =>
                {
                    await RunnerService.RetireMovedRunnerAsync(runner.CommandKey, deleteOldFiles, log);
                    FleetConfig.SetManuallyStopped(runner.CommandKey, false);
                },
                TimeSpan.FromMinutes(3), value: deleteOldFiles ? "delete" : "keep");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{runner.TitleText} now runs on {target.MachineName}, but the old copy on {source.MachineName} " +
                $"could not be removed: {ex.Message} It stays stopped and its service disabled. {RetireByHand(source.MachineName, target.MachineName)}", ex);
        }
        log($"{runner.TitleText} now runs on {target.MachineName}.");
    }

    private bool RunsHere(MachineInfo machine) => machine.IsLocal && !_viaLocalAgent;

    /// <summary>What to tell the user when the old copy of a moved runner is still there.</summary>
    internal static string RetireByHand(string source, string target)
        => $"Delete its Windows service and folder on {source} by hand. Do not unregister it from GitHub: the registration now belongs to {target}.";

    /// <summary>
    /// The labels to register a moved runner with. They are read from GitHub when not known; when GitHub can't be read
    /// the move stops, because without its custom labels the runner would not get the jobs that need them.
    /// </summary>
    private static async Task<string[]> LabelsToKeepAsync(MachineRunnerStatus runner, string[]? knownLabels)
    {
        if (knownLabels is { Length: > 0 }) return knownLabels;
        if (runner.CustomLabels.Length > 0) return runner.CustomLabels;
        // Without a runner ID there is no registration on GitHub to read labels from.
        if (runner.AgentId <= 0) return Array.Empty<string>();
        var lookup = await GitHubCli.LookUpRunnerAsync(runner.Scope, runner.AgentId);
        if (lookup.State != null) return lookup.State.Labels;
        if (lookup.NotFound) return Array.Empty<string>();
        throw new InvalidOperationException("Could not read the runner's labels from GitHub, so it was not moved. Try again.");
    }

    /// <summary>
    /// Step 1 of a move, on the PC the runner leaves: stops it when idle, keeps auto-recovery away, disables its service so
    /// it doesn't start at the next restart, and marks it as the old copy (so deleting it later doesn't unregister the
    /// moved runner). The marker says how the service started, whether a console window ran the runner and whether it was
    /// stopped, for a failed move to put back (<see cref="BeforeMove"/>). Undone when any part fails: the service starts as
    /// it did before.
    /// </summary>
    internal static async Task StopForMoveAsync(string key)
    {
        var wasStopped = FleetConfig.Current.ManuallyStopped.Contains(key, StringComparer.OrdinalIgnoreCase);
        var runner = await RunnerService.FindRunnerAsync(key);
        var before = BeforeMove(runner, wasStopped);
        FleetConfig.SetManuallyStopped(key, true);
        try
        {
            await RunnerService.StopIfIdleAsync(key);
            await SetServiceEnabledAsync(key, false);
            await RunnerService.MarkMovedAwayAsync(key, true, before.ManualStart, before.ServiceStart, before.Console, before.Stopped);
        }
        catch
        {
            // As the service was when this step began: an earlier move may have disabled it already.
            if (runner?.ServiceStart is { } start) try { await SetServiceStartAsync(key, start); } catch { }
            FleetConfig.SetManuallyStopped(key, wasStopped);
            throw;
        }
    }

    /// <summary>
    /// What the marker of a move records (<see cref="RunnerService.MovedAwayText"/>): whether the runner was set not to
    /// start with Windows, as before, how its Windows service started (see <see cref="ServiceBeforeMove"/>), whether a
    /// console window ran it or a startup entry opens it in one, and whether it was stopped in Runner Manager
    /// (<paramref name="wasStopped"/>). A runner that an earlier move left disabled and stopped keeps what that move
    /// recorded, not that it is disabled and stopped now.
    /// </summary>
    internal static (bool ManualStart, string? ServiceStart, bool Console, bool Stopped) BeforeMove(RunnerInfo? runner, bool wasStopped)
    {
        if (runner == null) return (false, null, false, wasStopped);
        var console = runner.ConsoleBeforeMove || runner.HasConsoleCopy;
        return (runner.RegistrationOptions.ManualStart, ServiceBeforeMove(runner, console) ?? runner.ServiceStart, console,
            runner.MovedAway ? runner.StoppedBeforeMove : wasStopped);
    }

    /// <summary>
    /// How the service of a runner that an earlier move marked started before that move, or null without such a mark. A
    /// mark of 2.9.0 does not say: 2.9.0 did not undo a failed move of a runner in a console window, so the service of
    /// such a runner is still disabled, and stays so while a console window runs the runner or a startup entry opens it
    /// (<paramref name="console"/>); starting it with Windows would run the runner twice. Otherwise as 2.9.0 put it back.
    /// </summary>
    internal static string? ServiceBeforeMove(RunnerInfo runner, bool console)
        => runner.ServiceStartBeforeMove != null && !runner.ServiceRecordedBeforeMove && console
            ? RunnerStart.Disabled
            : runner.ServiceStartBeforeMove;

    /// <summary>
    /// Undoes <see cref="StopForMoveAsync"/> after a move failed: the service starts as it did before the move, with
    /// Windows, manually or not at all (disabled), and is started again only when that is how the runner ran
    /// (<see cref="AfterFailedMove"/>). A runner that was stopped before the move stays stopped. Returns what was done.
    /// </summary>
    internal static async Task<string> StartAfterFailedMoveAsync(string key)
    {
        var undo = AfterFailedMove(await RunnerService.FindRunnerAsync(key));
        FleetConfig.SetManuallyStopped(key, undo.Stopped);
        await SetServiceStartAsync(key, undo.ServiceStart);
        // Removed after the service is set, so a retry still knows how it started.
        await RunnerService.MarkMovedAwayAsync(key, false);
        if (undo.Start) await RunnerService.ControlServiceAsync(key, "Start");
        return undo.Text;
    }

    /// <summary>
    /// How a failed move leaves the old copy (as its marker says): how its service starts, whether the service is started
    /// again, whether the runner stays stopped, and what to say. It is not started when it was disabled before the move,
    /// not when a console window ran the runner or a startup entry opens it in one (the move closed that window, and
    /// starting the service would run the runner as a service instead, or twice), and not when it was stopped in Runner
    /// Manager before the move. Without a marker, the service starts with Windows and is started, as before.
    /// </summary>
    internal static (string ServiceStart, bool Start, bool Stopped, string Text) AfterFailedMove(RunnerInfo? runner)
    {
        var console = runner != null && (runner.ConsoleBeforeMove || runner.HasConsoleCopy);
        var service = (runner == null ? null : ServiceBeforeMove(runner, console)) ?? RunnerStart.WithWindows;
        var stopped = runner?.StoppedBeforeMove == true;
        // A runner without a service runs in a console window only.
        if (console || runner is { ServiceInstalled: false })
            return (service, false, stopped, "The runner is as it was before the move. Its Windows service was not started, because the runner runs in a console window. " +
                                             "Open the console window again if the move closed it.");
        if (service == RunnerStart.Disabled)
            return (service, false, stopped, "The runner is as it was before the move. Its Windows service is disabled, as before, and was not started.");
        return stopped
            ? (service, false, true, "The runner is as it was before the move. It was stopped before the move, so it was not started.")
            : (service, true, false, "The runner was started again.");
    }

    /// <summary>
    /// Whether the dashboard undoes a failed move on the runner's PC. A PC with a version before 2.10.0 sets the service
    /// to start with Windows (or manually) and starts it when it undoes a move, so it is not asked for a runner that a
    /// console window ran, which would then run twice, nor for a runner whose Windows service was disabled before the
    /// move (<paramref name="runner"/> is its status from before), such as a runner that runs in a console window that is
    /// not open now: its disabled service would start. Such a runner is left as the move left it there, stopped and with
    /// its service disabled. From 2.10.0 the PC sets the service back as it was and does not start it in those cases
    /// (<see cref="AfterFailedMove"/>).
    /// </summary>
    /// <param name="runsHere">The dashboard undoes it itself, with this version.</param>
    internal static bool UndoesFailedMove(MachineRunnerStatus runner, MachineInfo source, bool runsHere)
        => runsHere || AppUpdater.CompareVersions(source.AppVersion, ConsoleMoveUndoSince) >= 0
           || !runner.IsInteractive && runner.StartsAt != RunnerStart.Disabled;

    private const string ConsoleMoveUndoSince = "2.10.0";

    /// <summary>What the dashboard says when it does not ask the runner's PC to undo a failed move (see <see cref="UndoesFailedMove"/>).</summary>
    internal static string NotUndoneText(string source)
        => $"Move failed. The runner on {source} stays stopped, as the move left it: {source} runs a version of Runner Manager before " +
           $"{ConsoleMoveUndoSince}, which would start its Windows service. Update Runner Manager on {source}, then start the runner there as before.";

    /// <summary>
    /// Disables a runner's Windows service (so it can't start, also not at the next restart), or sets it back to
    /// start with Windows, or manually when it was set not to. Runners without a service are left as they are.
    /// </summary>
    internal static async Task SetServiceEnabledAsync(string key, bool enabled, bool manualStart = false)
    {
        if (!key.StartsWith("actions.runner.", StringComparison.OrdinalIgnoreCase)) return;
        await RunnerService.SetServiceStartModeAsync(key, StartMode(enabled, manualStart));
    }

    /// <summary>
    /// The sc.exe start= value for <see cref="SetServiceEnabledAsync"/>. Starting with Windows is "delayed-auto", as
    /// config.cmd installs runner services, so turning a service on again leaves it as it was installed.
    /// </summary>
    internal static string StartMode(bool enabled, bool manualStart)
        => !enabled ? ServiceStart.Disabled : manualStart ? ServiceStart.Manual : ServiceStart.Automatic;

    /// <summary>Sets a runner's Windows service to start as a <see cref="RunnerStart"/> value says (see <see cref="StartMode(string)"/>).</summary>
    private static async Task SetServiceStartAsync(string key, string start)
    {
        if (!key.StartsWith("actions.runner.", StringComparison.OrdinalIgnoreCase)) return;
        await RunnerService.SetServiceStartModeAsync(key, StartMode(start));
    }

    /// <summary>The sc.exe start= value for a <see cref="RunnerStart"/> value: disabled, manually, or else with Windows.</summary>
    internal static string StartMode(string start)
        => start == RunnerStart.Disabled ? ServiceStart.Disabled : StartMode(enabled: true, manualStart: start == RunnerStart.Off);
}
