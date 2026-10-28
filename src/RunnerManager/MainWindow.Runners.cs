using System.Windows;

namespace RunnerManager;

/// <summary>Runner lifecycle: delete, convert console runners to services, and move runners between PCs.</summary>
public partial class MainWindow
{
    // The dashboard has administrator rights, so work on this PC runs directly.
    private readonly FleetActions _fleetActions = new(viaLocalAgent: false);

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
                "The console window closes, and what starts it at sign-in is turned off so it does not start twice: startup entries are moved to " +
                "%LOCALAPPDATA%\\RunnerManager\\disabled-startup, and scheduled tasks that start it are disabled. " +
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

        if (string.IsNullOrEmpty(FleetActions.RegistrationUrl(runner.Scope)))
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
            await _fleetActions.MoveRunnerAsync(source, runner, target, deleteOld, runner.LocalRunner?.CustomLabels, log);
        });
        _pendingRunnerCommands.Remove(key);
    }
}
