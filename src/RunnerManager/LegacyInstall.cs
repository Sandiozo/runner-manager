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
    // Versions before 2.2 always installed to the default folder of that time, in the user's profile.
    internal static string LegacyExe => Path.Combine(MachineInstall.OldDefaultDir, AppPaths.LegacyExeName);

    public static Task MigrateAsync(Action<string> log) => MigrateAsync(log, fromSetup: false);

    /// <summary>
    /// Setup passes true for <paramref name="fromSetup"/>: it always checks, and it registers the launch and start-up
    /// tasks and the shortcuts itself, so only the legacy tasks are moved here.
    /// </summary>
    internal static Task MigrateAsync(Action<string> log, bool fromSetup)
        => MigrateAsync(log, fromSetup, AppUpdater.InstalledExe, LegacyExe, AppPaths.LegacyStateDir);

    /// <summary>The migration with the installed exe and RunnerWidget's exe and data folder given (tests pass folders of their own).</summary>
    internal static async Task MigrateAsync(Action<string> log, bool fromSetup, string exe, string legacyExe, string legacyStateDir)
    {
        // Nothing to point the tasks at yet (for example a build folder); setup migrates when it installs.
        if (!File.Exists(exe)) return;
        // Querying scheduled tasks is slow, so the dashboard only does it while legacy files remain, or once per version.
        var legacyFilesExist = File.Exists(legacyExe) || Directory.Exists(legacyStateDir);
        if (!fromSetup && !legacyFilesExist && InstallChecks.IsDone(Check)) return;

        // RunnerWidget registered its tasks with the highest rights. A task under its names that does not run with them,
        // or is another Windows user's, may have been created by any program, so it is left as it is (and logged).
        var legacyAgentTask = await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.LegacyAgentTaskName) == true;
        var legacyDashboardTask = await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.LegacyDashboardTaskName) == true;
        if (!File.Exists(legacyExe) && !legacyAgentTask && !legacyDashboardTask)
        {
            InstallChecks.MarkDone(Check);
            return;
        }

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
            // Not while it runs: this dashboard may have been started by it (the dashboard checks the task again later).
            await Installer.RegisterIfIdleAsync(ScheduledTasks.LaunchTaskName, () => ScheduledTasks.RegisterLaunchTaskAsync(exe));
            await Installer.RefreshShortcutsAsync(exe);
        }

        // The old exe may still be running (an old dashboard); the next start removes it.
        try { File.Delete(legacyExe); } catch { }
        InstallChecks.MarkDone(Check);
        log("Install moved to Runner Manager.");
    }
}
