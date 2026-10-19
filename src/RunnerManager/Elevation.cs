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

    /// <summary>Starts the elevated dashboard through the launch task. Returns false when the task is not registered.</summary>
    public static async Task<bool> TryStartLaunchTaskAsync()
    {
        // The task's process is not our child, so allow it to take the foreground explicitly.
        AllowSetForegroundWindow(AsfwAny);
        var (code, _) = await ScheduledTasks.RunTaskAsync(ScheduledTasks.LaunchTaskName);
        return code == 0;
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
