using System.Windows;

namespace RunnerManager;

/// <summary>Runner lifecycle: delete, convert console runners to services, and move runners between PCs.</summary>
public partial class MainWindow
{
    private async void RunnerRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { CanRemove: true } runner) return;
        const string deleteFiles = "Delete runner and files";
        const string keepFiles = "Delete runner, keep files";
        var where = runner.IsLocalMachine ? "this PC" : runner.MachineName;
        var choice = DialogWindow.Show(this, DialogTone.Danger, $"Delete {runner.TitleText}?",
            $"The runner is unregistered from {runner.RepositoryText} on GitHub, and its Windows service is removed from {where}. " +
            "If the repository no longer exists on GitHub, the runner is only removed from the PC.",
            deleteFiles, keepFiles, detail: string.IsNullOrEmpty(runner.InstallPath) ? runner.LocalRunner?.InstallPath : runner.InstallPath);
        if (choice == null) return;
        var deleteFolder = choice == deleteFiles;
        await RunRunnerActionAsync(runner, "Remove", $"Delete {runner.TitleText}",
            log => RunnerService.RemoveRunnerAsync(runner.CommandKey, deleteFolder, log), value: deleteFolder ? "delete" : "keep");
    }

    private async void RunnerConvert_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { CanConvert: true } runner) return;
        if (!DialogWindow.Confirm(this, $"Run {runner.TitleText} as a Windows service?",
                "The console window closes, and its startup entry is moved to %LOCALAPPDATA%\\RunnerManager\\disabled-startup so it does not start twice. " +
                "The runner is registered again as a Windows service with the same name and labels. It then starts with Windows, before sign-in.",
                "Run as service")) return;
        await RunRunnerActionAsync(runner, "Convert", $"Convert {runner.TitleText}",
            log => RunnerService.ConvertToServiceAsync(runner.CommandKey, log));
    }

    private async void RunnerMove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { CanMove: true } runner) return;
        var source = Machines.FirstOrDefault(m => m.MachineKey == runner.MachineKey);
        if (source == null) return;
        var candidates = Machines
            .Where(m => m.MachineKey != source.MachineKey && (m.IsLocal || (m.IsOnline && m.AgentProtocolVersion >= 9)))
            .ToList();
        if (candidates.Count == 0)
        {
            DialogWindow.Info(this, "No PC available for this runner",
                "Moving a runner requires another PC in the fleet that is online and runs Runner Manager v2 or later. To add a PC, use “Connect another PC” in the sidebar on that PC.");
            return;
        }

        var dialog = new MoveRunnerWindow(runner, candidates) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Target is not { } target) return;
        if (!CanSendTo(source.MachineKey) || !CanSendTo(target.MachineKey)) return;

        var url = RegistrationUrl(runner.Scope);
        if (string.IsNullOrEmpty(url))
        {
            DialogWindow.Error(this, "Cannot move this runner", "Its repository or organization is unknown.");
            return;
        }
        var deleteOld = dialog.DeleteOldFiles;
        var key = RunnerKey(runner);

        await RunOperationAsync($"Move {runner.TitleText} to {target.MachineName}", async log =>
        {
            MarkUserAction(key);
            runner.SetPending("Move");
            var labels = runner.LocalRunner?.CustomLabels;
            if (labels == null || labels.Length == 0)
                labels = (await GitHubCli.GetRunnerStateAsync(runner.Scope, runner.AgentId))?.Labels ?? Array.Empty<string>();
            log(labels.Length > 0 ? $"Keeping labels: {string.Join(", ", labels)}" : "No custom labels to keep.");

            // 1. Stop the old copy so it can't pick up a job mid-move (and so auto-recovery leaves it alone).
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
                () => RunnerService.RetireMovedRunnerAsync(runner.CommandKey, deleteOld, log),
                TimeSpan.FromMinutes(3), value: deleteOld ? "delete" : "keep");
            FleetConfig.SetManuallyStopped(runner.CommandKey, false);
            log($"{runner.TitleText} now runs on {target.MachineName}.");
        });
        _pendingRunnerCommands.Remove(key);
    }

    /// <summary>Runs an action on this PC directly, or sends it to another PC's agent and waits for its result.</summary>
    private async Task RunOnMachineAsync(MachineInfo machine, string target, string targetKey, string action, Action<string> log,
        Func<Task> local, TimeSpan timeout, string runnerName = "", string[]? labels = null, string value = "")
    {
        if (machine.IsLocal)
        {
            await local();
            return;
        }
        var id = await MachineRelay.SendCommandAsync(machine.MachineKey, target, targetKey, action, runnerName, labels, value);
        var result = await MachineRelay.WaitForCommandAsync(machine.MachineKey, id, timeout);
        log($"{machine.MachineName}: {result}");
    }

    private static string RegistrationUrl(string scope)
        => scope.StartsWith("repos/", StringComparison.OrdinalIgnoreCase) ? $"https://github.com/{scope[6..]}"
            : scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase) ? $"https://github.com/{scope[5..]}"
            : string.Empty;
}
