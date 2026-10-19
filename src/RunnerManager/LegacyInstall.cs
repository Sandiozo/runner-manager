using System.IO;

namespace RunnerManager;

/// <summary>
/// Moves an install made by the RunnerWidget versions (before 2.2) to the current names: the scheduled tasks,
/// shortcuts and the Settings → Apps entry, and removes the old exe. <see cref="AppPaths"/> moves the data folder.
/// Runs from setup and from the installed dashboard, and is safe to run more than once.
/// </summary>
public static class LegacyInstall
{
    private static string LegacyExe => Path.Combine(AppUpdater.InstallDir, AppPaths.LegacyExeName);

    public static async Task MigrateAsync(Action<string> log)
    {
        // Nothing to point the tasks at yet (for example a build folder); setup migrates when it installs.
        if (!File.Exists(AppUpdater.InstalledExe)) return;
        var legacyAgentTask = await ScheduledTasks.ExistsAsync(ScheduledTasks.LegacyAgentTaskName);
        var legacyDashboardTask = await ScheduledTasks.ExistsAsync(ScheduledTasks.LegacyDashboardTaskName);
        if (!File.Exists(LegacyExe) && !legacyAgentTask && !legacyDashboardTask) return;

        var exe = AppUpdater.InstalledExe;
        log("Moving the install from RunnerWidget to Runner Manager...");
        if (legacyAgentTask)
        {
            await ScheduledTasks.EndTaskAsync(ScheduledTasks.LegacyAgentTaskName);
            await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.LegacyAgentTaskName);
            await MachineRelay.RegisterAgentTaskAsync(exe);
            await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        }
        if (legacyDashboardTask)
        {
            await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.LegacyDashboardTaskName);
            await Installer.RegisterStartupTaskAsync(exe);
        }
        await ScheduledTasks.RegisterLaunchTaskAsync(exe);
        await Installer.RefreshShortcutsAsync(exe);

        // The old exe may still be running (an old dashboard); the next start removes it.
        try { File.Delete(LegacyExe); } catch { }
        log("Install moved to Runner Manager.");
    }
}
