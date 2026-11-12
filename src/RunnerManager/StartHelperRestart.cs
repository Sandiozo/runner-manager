using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RunnerManager;

/// <summary>
/// A dashboard that one of this user's own tasks started as RunnerManager.exe itself, not through the start helper,
/// starts itself again through the start helper, once, before it opens its window (see <see cref="TryAsync"/>). A task
/// is registered again only while it does not run (see <see cref="Installer.RegisterIfIdleAsync"/>), and a dashboard that
/// such a task started may run for days, so without this the task would go on starting the exe itself. After the
/// restart, the task's instance has ended with the dashboard before, and the new dashboard moves the task to the start
/// helper once Task Scheduler no longer counts it as running (see <see cref="WaitForTasksAsync"/>).
/// </summary>
internal static class StartHelperRestart
{
    /// <summary>Passed to the dashboard that the restart starts, so that one never restarts itself again.</summary>
    internal const string Argument = "--through-start-helper";

    /// <summary>How long the restarted dashboard waits for the task instance of the dashboard before to end.</summary>
    internal static readonly TimeSpan TaskEndWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the dashboard that restarts watches the start helper it started. The start helper runs as long as the
    /// dashboard it started, and the new dashboard waits for this one to exit first, so a start helper that exits within
    /// this time did not start it.
    /// </summary>
    internal static readonly TimeSpan StartCheck = TimeSpan.FromSeconds(2);

    /// <summary>Starts a program (Process.Start). Tests pass their own.</summary>
    internal static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = Process.Start;

    /// <summary>Whether this dashboard was started by such a restart.</summary>
    internal static bool IsRestart(IEnumerable<string> args) => args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the dashboard restarts itself through the start helper. All of these must hold, and they are read in this
    /// order, so the first one that does not hold ends the check, and a start through the start helper reads no task and
    /// runs no probe: it was not started by such a restart (<paramref name="afterRestart"/>); the start helper did not start
    /// it; it decides about the start helper (the installed file with administrator rights, see
    /// <see cref="Launcher.Decides"/>); no update holds the update lock; one of this user's own tasks that starts
    /// RunnerManager.exe itself runs now, as it does when it started this dashboard; and the start helper is in place and
    /// was seen to start Runner Manager.
    /// </summary>
    internal static async Task<bool> RestartsAsync(bool afterRestart, Func<bool> startedByStartHelper, Func<bool> decides,
        Func<bool> updateRuns, Func<Task<bool>> taskRunsTheExe, Func<Task<bool>> startHelperWorks)
        => !afterRestart && !startedByStartHelper() && decides() && !updateRuns() && await taskRunsTheExe() && await startHelperWorks();

    /// <summary>
    /// The arguments of the restarted dashboard: the same as this one's, with <see cref="App.AfterArgument"/> and the id of
    /// this process, so it waits until this one has exited, as after "Restart now", and with <see cref="Argument"/>.
    /// </summary>
    internal static List<string> RestartArguments(IEnumerable<string> args, int processId)
        => args.Where(a => !a.StartsWith(App.AfterArgument, StringComparison.OrdinalIgnoreCase) && !a.Equals(Argument, StringComparison.OrdinalIgnoreCase))
            .Append(App.AfterArgument + processId).Append(Argument).ToList();

    /// <summary>
    /// Restarts the dashboard of the install folder <paramref name="folder"/> through its start helper when
    /// <see cref="RestartsAsync"/> says so, with <see cref="RestartArguments"/>. Returns true when the new dashboard was
    /// started, so this one exits without opening a window. Returns false when there is nothing to do, or when the start
    /// helper could not be started or exited at once; this dashboard then carries on.
    /// </summary>
    internal static async Task<bool> TryAsync(string[] args, string folder, Action<string> log)
    {
        var exe = Path.Combine(folder, AppPaths.ExeName);
        var running = new List<string>();
        if (!await RestartsAsync(IsRestart(args),
                () => Launcher.StartedFrom(folder),
                Launcher.Decides,
                () => Launcher.UnderUpdateLock(TimeSpan.Zero, () => Launcher.EnsureResult.Kept) == null,
                async () => (running = await RunningTasksStartingAsync(exe)).Count > 0,
                async () => Launcher.IsInPlace(folder) && (await Launcher.ProveAsync(folder, TimeSpan.Zero)).Outcome == Launcher.Proof.Works))
            return false;
        var start = Launcher.StartInfo(exe, RestartArguments(args, Environment.ProcessId));
        if (!Launcher.SamePath(start.FileName, Launcher.PathIn(folder))) return false;
        try
        {
            using var helper = StartProcess(start);
            if (helper == null) return false;
            using var watch = new CancellationTokenSource(StartCheck);
            try
            {
                await helper.WaitForExitAsync(watch.Token);
                log($"The start helper {start.FileName} exited right away (exit code 0x{helper.ExitCode:X8}), so Runner Manager carries on without it.");
                return false;
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log($"Could not start Runner Manager again through the start helper ({ex.Message}), so it carries on without it.");
            return false;
        }
        log($"Runner Manager was started without its start helper, by {TaskList(running)}. It started again through the start helper, so the task can move to it.");
        return true;
    }

    /// <summary>
    /// After a restart: waits until no task of this user's that starts <paramref name="exe"/> itself runs any more (the
    /// task instance of the dashboard before ends a moment after it), for at most <paramref name="timeout"/>, so the tasks
    /// can move to the start helper right after. Returns false when one still ran at the end; it then moves on a later
    /// start, or when the fleet agent checks.
    /// </summary>
    internal static async Task<bool> WaitForTasksAsync(string exe, TimeSpan timeout, Action<string> log)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var running = await RunningTasksStartingAsync(exe);
            if (running.Count == 0) return true;
            if (DateTime.UtcNow >= deadline)
            {
                log(running.Count == 1
                    ? $"The scheduled task \"{running[0]}\" still runs, so it moves to the start helper later."
                    : $"The scheduled tasks {string.Join(" and ", running.Select(n => $"\"{n}\""))} still run, so they move to the start helper later.");
                return false;
            }
            await Task.Delay(250);
        }
    }

    /// <summary>This user's own tasks that start <paramref name="exe"/> itself (see <see cref="Installer.TasksStartingExeAsync"/>) and run now.</summary>
    private static async Task<List<string>> RunningTasksStartingAsync(string exe)
    {
        var running = new List<string>();
        foreach (var name in await Installer.TasksStartingExeAsync(exe))
            if (await ScheduledTasks.IsRunningAsync(name) == true) running.Add(name);
        return running;
    }

    private static string TaskList(IReadOnlyList<string> names)
        => names.Count == 1 ? $"the scheduled task \"{names[0]}\"" : $"the scheduled tasks {string.Join(" and ", names.Select(n => $"\"{n}\""))}";
}
