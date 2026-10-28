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

    /// <summary>
    /// Starts the elevated dashboard through this user's launch task (or the shared one of 2.3 and earlier, when it is this
    /// user's). Returns false when there is no usable task, or when the task ran but no dashboard appeared (a disabled
    /// task, or one pointing at a removed exe), so the caller asks with UAC instead.
    /// </summary>
    public static async Task<bool> TryStartLaunchTaskAsync()
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
            if (alreadyRunning || await WaitForDashboardAsync(TimeSpan.FromSeconds(20))) return true;
            MachineRelay.AppendAgentLog($"The launch task \"{name}\" ran, but Runner Manager did not start. Asking with UAC instead.");
            return false;
        }
        return false;
    }

    /// <summary>Whether a launch task's definition starts <paramref name="exe"/> for this user and is turned on.</summary>
    internal static bool IsUsableLaunchTask(string xml, string exe)
        => ScheduledTasks.IsEnabled(xml)
           && ScheduledTasks.IsCurrentUser(ScheduledTasks.PrincipalOf(xml))
           && string.Equals(ScheduledTasks.CommandOf(xml), exe, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Starts this exe again with the same arguments and a UAC prompt. Returns false when the prompt is declined.</summary>
    public static bool RelaunchElevated(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try
        {
            Process.Start(start);
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
