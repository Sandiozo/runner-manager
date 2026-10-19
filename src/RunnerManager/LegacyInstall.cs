using System.IO;

namespace RunnerManager;

/// <summary>
/// Moves an install made by the RunnerWidget versions (before 2.2) to the current names: the scheduled tasks,
/// shortcuts and the Settings → Apps entry, and removes the old exe. <see cref="AppPaths"/> moves the data folder.
/// Runs from setup and from the installed dashboard, and is safe to run more than once.
/// </summary>
public static class LegacyInstall
{
    private const string Check = "legacy-install";
    // Versions before 2.2 always installed to the default folder.
    private static string LegacyExe => Path.Combine(AppUpdater.DefaultInstallDir, AppPaths.LegacyExeName);

    public static Task MigrateAsync(Action<string> log) => MigrateAsync(log, fromSetup: false);

    /// <summary>
    /// Setup passes true for <paramref name="fromSetup"/>: it always checks, and it registers the launch and start-up
    /// tasks and the shortcuts itself, so only the legacy tasks are moved here.
    /// </summary>
    internal static async Task MigrateAsync(Action<string> log, bool fromSetup)
    {
        // Nothing to point the tasks at yet (for example a build folder); setup migrates when it installs.
        if (!File.Exists(AppUpdater.InstalledExe)) return;
        // Querying scheduled tasks is slow, so the dashboard only does it while legacy files remain, or once per version.
        var legacyFilesExist = File.Exists(LegacyExe) || Directory.Exists(AppPaths.LegacyStateDir);
        if (!fromSetup && !legacyFilesExist && InstallChecks.IsDone(Check)) return;

        var legacyAgentTask = await ScheduledTasks.ExistsAsync(ScheduledTasks.LegacyAgentTaskName);
        var legacyDashboardTask = await ScheduledTasks.ExistsAsync(ScheduledTasks.LegacyDashboardTaskName);
        if (!File.Exists(LegacyExe) && !legacyAgentTask && !legacyDashboardTask)
        {
            InstallChecks.MarkDone(Check);
            return;
        }

        var exe = AppUpdater.InstalledExe;
        log("Moving the install from RunnerWidget to Runner Manager...");
        if (legacyAgentTask)
        {
            await ScheduledTasks.EndTaskAsync(ScheduledTasks.LegacyAgentTaskName);
            // Give the old agent time to exit and free its single-instance lock, or the new agent finds it and exits.
            await Task.Delay(1500);
            await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.LegacyAgentTaskName);
            await MachineRelay.RegisterAgentTaskAsync(exe);
            await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        }
        if (legacyDashboardTask)
        {
            await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.LegacyDashboardTaskName);
            if (!fromSetup) await Installer.RegisterStartupTaskAsync(exe);
        }
        if (!fromSetup)
        {
            await ScheduledTasks.RegisterLaunchTaskAsync(exe);
            await Installer.RefreshShortcutsAsync(exe);
        }

        // The old exe may still be running (an old dashboard); the next start removes it.
        try { File.Delete(LegacyExe); } catch { }
        InstallChecks.MarkDone(Check);
        log("Install moved to Runner Manager.");
    }
}
