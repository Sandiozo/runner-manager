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
    /// </summary>
    internal static string? ConsoleRunnerProblem(bool interactive, bool serviceInstalled, string action)
        => !interactive || !serviceInstalled ? null
            : action == "Stop" ? "This runner runs in a console window, not as its Windows service. Close the console window to stop it."
            : "This runner runs in a console window. Starting its Windows service would run it twice. Close the console window first.";

    /// <param name="action">"Start", "Stop" or "Restart".</param>
    public Task ControlRunnerAsync(MachineInfo machine, MachineRunnerStatus runner, string action, Action<string> log)
        => RunOnMachineAsync(machine, "runner", runner.CommandKey, action, log,
            async () =>
            {
                if (ConsoleRunnerProblem(runner.IsInteractive, runner.ServiceInstalled, action) is { } problem)
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
    /// the old registration), then removes the old copy. If the target reports that registering failed, the old copy
    /// is enabled and started again.
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
            if (!runner.IsInteractive)
            {
                log($"Move failed. Starting the runner on {source.MachineName} again…");
                try
                {
                    await RunOnMachineAsync(source, "runner", runner.CommandKey, "Start", log,
                        () => StartAfterFailedMoveAsync(runner.CommandKey), TimeSpan.FromMinutes(2), value: "enable");
                }
                catch (Exception restartError) { log($"Could not start the runner again: {restartError.Message}"); }
            }
            // config.cmd on the target replaced the registration before the install failed. The old copy is still enabled
            // and started, so it can be edited, but GitHub no longer accepts it.
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
    /// moved runner). Undone when any part fails.
    /// </summary>
    internal static async Task StopForMoveAsync(string key)
    {
        var wasStopped = FleetConfig.Current.ManuallyStopped.Contains(key, StringComparer.OrdinalIgnoreCase);
        // Also when an earlier move left it disabled: the marker remembers that it started manually before.
        var manualStart = (await RunnerService.FindRunnerAsync(key))?.RegistrationOptions.ManualStart == true;
        FleetConfig.SetManuallyStopped(key, true);
        try
        {
            await RunnerService.StopIfIdleAsync(key);
            await SetServiceEnabledAsync(key, false);
            await RunnerService.MarkMovedAwayAsync(key, true, manualStart);
        }
        catch
        {
            try { await SetServiceEnabledAsync(key, true, manualStart); } catch { }
            FleetConfig.SetManuallyStopped(key, wasStopped);
            throw;
        }
    }

    /// <summary>Undoes <see cref="StopForMoveAsync"/> after a move failed, and starts the runner again.</summary>
    internal static async Task StartAfterFailedMoveAsync(string key)
    {
        var manualStart = (await RunnerService.FindRunnerAsync(key))?.ManualStartBeforeMove == true;
        FleetConfig.SetManuallyStopped(key, false);
        await SetServiceEnabledAsync(key, true, manualStart);
        // Removed after the service is enabled, so a retry still knows how it started.
        await RunnerService.MarkMovedAwayAsync(key, false);
        await RunnerService.ControlServiceAsync(key, "Start");
    }

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
}
