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

    /// <summary>Runs an action on this PC directly, or sends it to a fleet agent and waits for its result.</summary>
    public async Task RunOnMachineAsync(MachineInfo machine, string target, string targetKey, string action, Action<string> log,
        Func<Task> local, TimeSpan timeout, string runnerName = "", string[]? labels = null, string value = "")
    {
        if (machine.IsLocal && !_viaLocalAgent)
        {
            await local();
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        try { await MachineRelay.SendCommandAsync(machine.MachineKey, target, targetKey, action, runnerName, labels, value, id); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // The agent may have queued it before its answer was lost. Sending the same ID again is safe: each ID runs once.
            log($"{machine.MachineName} did not answer. Sending the request again…");
            await MachineRelay.SendCommandAsync(machine.MachineKey, target, targetKey, action, runnerName, labels, value, id);
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
            TimeSpan.FromMinutes(10), runnerName: name, labels: labels);

    /// <param name="action">"Start", "Stop" or "Restart".</param>
    public Task ControlRunnerAsync(MachineInfo machine, MachineRunnerStatus runner, string action, Action<string> log)
        => RunOnMachineAsync(machine, "runner", runner.CommandKey, action, log,
            async () =>
            {
                await RunnerService.ControlServiceAsync(runner.CommandKey, action);
                FleetConfig.SetManuallyStopped(runner.CommandKey, action == "Stop");
            },
            TimeSpan.FromMinutes(2));

    public async Task RemoveRunnerAsync(MachineInfo machine, MachineRunnerStatus runner, bool deleteFiles, Action<string> log)
    {
        await RunOnMachineAsync(machine, "runner", runner.CommandKey, "Remove", log,
            () => RunnerService.RemoveRunnerAsync(runner.CommandKey, deleteFiles, log),
            TimeSpan.FromMinutes(3), value: deleteFiles ? "delete" : "keep");
        FleetConfig.SetManuallyStopped(runner.CommandKey, false);
    }

    /// <summary>
    /// Moves a runner: stops it, registers it on the target PC with the same name and labels (replacing the old
    /// registration), then removes the old copy. If registering fails, the old copy is started again.
    /// </summary>
    public async Task MoveRunnerAsync(MachineInfo source, MachineRunnerStatus runner, MachineInfo target, bool deleteOldFiles,
        string[]? knownLabels, Action<string> log)
    {
        var url = RegistrationUrl(runner.Scope);
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("The runner's repository or organization is unknown.");

        var labels = knownLabels is { Length: > 0 } ? knownLabels
            : (await GitHubCli.GetRunnerStateAsync(runner.Scope, runner.AgentId))?.Labels ?? Array.Empty<string>();
        log(labels.Length > 0 ? $"Keeping labels: {string.Join(", ", labels)}" : "No custom labels to keep.");

        // 1. Stop the old copy so it cannot pick up a job during the move (and so auto-recovery leaves it alone).
        log($"Stopping the runner on {source.MachineName}…");
        await RunOnMachineAsync(source, "runner", runner.CommandKey, "Stop", log,
            () => { FleetConfig.SetManuallyStopped(runner.CommandKey, true); return RunnerService.ControlServiceAsync(runner.CommandKey, "Stop"); },
            TimeSpan.FromMinutes(2));

        // 2. Register it on the target, replacing the old registration.
        try
        {
            log($"Installing and registering the runner on {target.MachineName}. The download takes about 1–3 minutes…");
            await RunOnMachineAsync(target, "runner", url, "Create", log,
                () => RunnerService.CreateRunnerAsync(new RunnerSetup
                {
                    RepositoryUrl = url,
                    RunnerName = runner.AgentName,
                    InstallPath = RunnerService.SuggestInstallPath(url),
                    Labels = labels,
                    ReplaceExisting = true
                }, log),
                TimeSpan.FromMinutes(10), runnerName: runner.AgentName, labels: labels, value: "replace");
        }
        catch
        {
            if (!runner.IsInteractive)
            {
                log($"Move failed. Starting the runner on {source.MachineName} again…");
                try
                {
                    await RunOnMachineAsync(source, "runner", runner.CommandKey, "Start", log,
                        () => { FleetConfig.SetManuallyStopped(runner.CommandKey, false); return RunnerService.ControlServiceAsync(runner.CommandKey, "Start"); },
                        TimeSpan.FromMinutes(2));
                }
                catch (Exception restartError) { log($"Could not start the runner again: {restartError.Message}"); }
            }
            throw;
        }

        // 3. Remove the old copy (its registration now belongs to the target).
        log($"Removing the old copy from {source.MachineName}…");
        await RunOnMachineAsync(source, "runner", runner.CommandKey, "Retire", log,
            () => RunnerService.RetireMovedRunnerAsync(runner.CommandKey, deleteOldFiles, log),
            TimeSpan.FromMinutes(3), value: deleteOldFiles ? "delete" : "keep");
        FleetConfig.SetManuallyStopped(runner.CommandKey, false);
        log($"{runner.TitleText} now runs on {target.MachineName}.");
    }
}
