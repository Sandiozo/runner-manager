using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RunnerManager;

/// <summary>
/// The app starts without administrator rights (no UAC prompt) and hands over to an elevated copy: through the launch
/// task that setup registers, or with a UAC prompt when that task is missing.
/// </summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>What <see cref="TryStartLaunchTaskAsync"/> did: a dashboard runs, there was no usable task, or the task ran and no dashboard appeared.</summary>
    internal enum LaunchTaskOutcome { Started, NoTask, NothingAppeared }

    /// <summary>
    /// Starts the elevated dashboard through this user's launch task (or the shared one of 2.3 and earlier, when it is this
    /// user's). <see cref="LaunchTaskOutcome.NothingAppeared"/> when the task ran but no dashboard appeared within the
    /// wait (a disabled task, one pointing at a removed exe, or a start helper that does not start Runner Manager), so the
    /// caller asks with UAC for RunnerManager.exe itself instead.
    /// </summary>
    internal static async Task<LaunchTaskOutcome> TryStartLaunchTaskAsync()
    {
        // The task's process is not our child, so allow it to take the foreground explicitly.
        AllowSetForegroundWindow(AsfwAny);
        foreach (var name in new[] { ScheduledTasks.LaunchTaskName, ScheduledTasks.SharedLaunchTaskName })
        {
            var xml = await ScheduledTasks.QueryXmlAsync(name);
            // The shared name may belong to another user, so it is only used when its definition shows it is ours.
            if (xml.Length == 0 ? name == ScheduledTasks.SharedLaunchTaskName : !IsUsableLaunchTask(xml, Environment.ProcessPath ?? string.Empty))
                continue;
            var alreadyRunning = App.DashboardIsRunning();
            var (code, _) = await ScheduledTasks.RunTaskAsync(name);
            if (code != 0) continue;
            if (alreadyRunning || await WaitForDashboardAsync(TimeSpan.FromSeconds(20))) return LaunchTaskOutcome.Started;
            MachineRelay.AppendAgentLog($"The launch task \"{name}\" ran, but Runner Manager did not start. Asking with UAC for {AppPaths.ExeName} itself instead.");
            return LaunchTaskOutcome.NothingAppeared;
        }
        return LaunchTaskOutcome.NoTask;
    }

    /// <summary>
    /// How a copy without administrator rights gets an elevated one (see App.HandOffToElevatedAsync): through the launch
    /// task when <paramref name="tryLaunchTask"/> (<paramref name="launchTask"/> runs it), otherwise, or when there is no
    /// usable task, with a UAC prompt (<see cref="RelaunchElevated(string, IEnumerable{string}, bool)"/>). When the launch
    /// task ran but no dashboard appeared, the UAC prompt is for <paramref name="exe"/> itself, not for the start helper
    /// that the task may have started: such a copy cannot check whether the start helper works. Returns whether an elevated
    /// copy was started (false when the prompt was declined), and whether that was with a UAC prompt.
    /// </summary>
    internal static async Task<(bool Started, bool AskedWithUac)> HandOffAsync(string exe, IEnumerable<string> relaunchArgs, bool tryLaunchTask,
        Func<Task<LaunchTaskOutcome>> launchTask)
    {
        var outcome = tryLaunchTask ? await launchTask() : LaunchTaskOutcome.NoTask;
        if (outcome == LaunchTaskOutcome.Started) return (true, false);
        return (RelaunchElevated(exe, relaunchArgs, direct: outcome == LaunchTaskOutcome.NothingAppeared), true);
    }

    /// <summary>
    /// Whether a launch task's definition starts <paramref name="exe"/> for this user with the highest rights and is turned
    /// on. A launch task without the highest rights, which any program of the user can create under a free name, would
    /// start Runner Manager without administrator rights, so it is not used, and the dashboard registers its own instead.
    /// A task that runs the start helper beside <paramref name="exe"/> starts it too, but only while the start helper is
    /// there: one that antivirus software removed starts nothing, so the Start menu asks with UAC right away instead of
    /// waiting for a dashboard that never comes, and the elevated copy repairs it.
    /// </summary>
    internal static bool IsUsableLaunchTask(string xml, string exe)
        => ScheduledTasks.IsEnabled(xml)
           && ScheduledTasks.CommandOfOwnElevatedTask(xml) is { } command
           && string.Equals(Launcher.ExeOf(command), exe, StringComparison.OrdinalIgnoreCase)
           && Launcher.CanStart(command);

    private static async Task<bool> WaitForDashboardAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (App.DashboardIsRunning()) return true;
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>Passed to the elevated copy with the SID of the user who started it.</summary>
    internal const string UserSidArgument = "--user-sid=";

    internal static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? string.Empty;
        }
    }

    internal static string CurrentUserName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
    }

    /// <summary>
    /// The user who started this copy (by <see cref="UserSidArgument"/>), when the UAC prompt elevated it as someone else;
    /// otherwise null.
    /// </summary>
    internal static string? OtherUserThanStarted(string[] args)
    {
        var started = StartedBySid(args);
        if (started == null || started.Equals(CurrentUserSid, StringComparison.OrdinalIgnoreCase)) return null;
        try { return new SecurityIdentifier(started).Translate(typeof(NTAccount)).Value; }
        catch { return started; }
    }

    internal static string? StartedBySid(string[] args)
        => args.LastOrDefault(a => a.StartsWith(UserSidArgument, StringComparison.OrdinalIgnoreCase)) is { } arg && arg.Length > UserSidArgument.Length
            ? arg[UserSidArgument.Length..] : null;

    /// <summary>The Windows error of a UAC prompt that the user declined (ERROR_CANCELLED).</summary>
    internal const int ErrorCancelled = 1223;

    /// <summary>Starts a program (Process.Start). Tests pass their own.</summary>
    internal static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = Process.Start;

    /// <summary>
    /// Starts <paramref name="exe"/> (this copy's exe) again with the same arguments and a UAC prompt, through the start
    /// helper beside the installed exe while that is in place (see <see cref="Launcher.UacCommandFor"/>), so the elevated
    /// copy gets a clean environment; with <paramref name="direct"/>, the exe itself. When the start helper cannot be
    /// started for any reason but a declined prompt (antivirus software may block it), the exe itself is asked for
    /// instead. Returns false when the prompt is declined or nothing could be started.
    /// </summary>
    internal static bool RelaunchElevated(string exe, IEnumerable<string> args, bool direct = false)
    {
        var arguments = args.ToList();
        var start = Launcher.StartInfo(exe, arguments, elevate: true, direct);
        try
        {
            StartProcess(start)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode != ErrorCancelled && !string.Equals(start.FileName, exe, StringComparison.OrdinalIgnoreCase))
        {
            MachineRelay.AppendAgentLog($"The start helper {start.FileName} could not be started ({ex.Message}). Asking with UAC for {exe} itself instead.");
            try
            {
                StartProcess(Launcher.StartInfo(exe, arguments, elevate: true, direct: true))?.Dispose();
                return true;
            }
            catch (Win32Exception) { return false; }
        }
        catch (Win32Exception) { return false; }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
