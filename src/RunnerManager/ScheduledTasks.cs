using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace RunnerManager;

/// <summary>
/// Scheduled tasks: the fleet agent and the optional "start with Windows" dashboard run at logon, and the launch task
/// starts the dashboard with administrator rights without a UAC prompt.
/// </summary>
public static class ScheduledTasks
{
    public const string AgentTaskName = "Runner Manager Fleet Agent";
    public const string DashboardTaskName = "Runner Manager Startup";
    public const string LaunchTaskName = "Runner Manager";
    /// <summary>Task names used before the app was renamed from RunnerWidget.</summary>
    public const string LegacyAgentTaskName = "RunnerWidget Fleet Agent";
    public const string LegacyDashboardTaskName = "RunnerWidget Dashboard";

    /// <summary>
    /// Registers an always-on logon task: runs elevated for the current user, keeps running on battery,
    /// has no 72-hour time limit, and restarts after a failure.
    /// </summary>
    public static Task RegisterLogonTaskAsync(string name, string exe, string arguments, string description, bool restartOnFailure)
        => RegisterTaskAsync(name, exe, arguments, description, restartOnFailure, atLogon: true, parallel: false);

    /// <summary>
    /// Registers a task without a trigger that starts the dashboard elevated when the Start menu shortcut runs it.
    /// Instances may run in parallel, so a second start reaches the running dashboard and brings it to the front.
    /// </summary>
    public static Task RegisterLaunchTaskAsync(string exe) => RegisterTaskAsync(LaunchTaskName, exe, App.FromTaskArgument,
        "Starts Runner Manager with administrator rights, without a UAC prompt.", restartOnFailure: false, atLogon: false, parallel: true);

    private static async Task RegisterTaskAsync(string name, string exe, string arguments, string description,
        bool restartOnFailure, bool atLogon, bool parallel)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var command = SecurityElement.Escape(exe);
        var args = SecurityElement.Escape(arguments);
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? string.Empty);
        var restart = restartOnFailure
            ? "<RestartOnFailure>\n      <Interval>PT1M</Interval>\n      <Count>999</Count>\n    </RestartOnFailure>"
            : string.Empty;
        var trigger = atLogon
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>"
            : string.Empty;
        var xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{SecurityElement.Escape(description)}</Description>
  </RegistrationInfo>
  <Triggers>{trigger}</Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>{(parallel ? "Parallel" : "IgnoreNew")}</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    {restart}
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"{command}"</Command>
      <Arguments>{args}</Arguments>
      <WorkingDirectory>{workingDirectory}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
        var xmlPath = Path.Combine(Path.GetTempPath(), $"runnermanager-task-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode);
            var (code, output) = await RunAsync("schtasks.exe", $"/Create /F /TN \"{name}\" /XML \"{xmlPath}\"", ShortTimeout);
            if (code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"Could not register the scheduled task \"{name}\"." : output.Trim());
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    public static async Task<bool> ExistsAsync(string name) => (await RunAsync("schtasks.exe", $"/Query /TN \"{name}\"", ShortTimeout)).ExitCode == 0;

    public static async Task<string> QueryXmlAsync(string name)
    {
        var (code, output) = await RunAsync("schtasks.exe", $"/Query /TN \"{name}\" /XML", ShortTimeout);
        return code == 0 ? output : string.Empty;
    }

    public static Task<(int ExitCode, string Output)> RunTaskAsync(string name) => RunAsync("schtasks.exe", $"/Run /TN \"{name}\"", ShortTimeout);
    public static Task<(int ExitCode, string Output)> EndTaskAsync(string name) => RunAsync("schtasks.exe", $"/End /TN \"{name}\"", ShortTimeout);
    public static Task<(int ExitCode, string Output)> DeleteTaskAsync(string name) => RunAsync("schtasks.exe", $"/Delete /F /TN \"{name}\"", ShortTimeout);

    /// <summary>The executable a task runs, from its XML definition.</summary>
    public static string CommandOf(string xml)
    {
        var start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        start += "<Command>".Length;
        var end = xml.IndexOf("</Command>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return string.Empty;
        return xml[start..end].Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&amp;", "&").Trim().Trim('"');
    }

    /// <summary>Time limit for quick system tools such as schtasks.exe and sc.exe.</summary>
    internal static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Default time limit when the caller does not pass one; long enough for installers.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs a program hidden and returns its exit code with its output (or its error text when it failed).
    /// When it does not finish within the timeout, its process tree is killed and the exit code is -1.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        try
        {
            var result = await ProcessRunner.RunAsync(new ProcessStartInfo { FileName = fileName, Arguments = arguments }, limit);
            if (result == null) return (-1, $"{Path.GetFileName(fileName)} did not finish within {ProcessRunner.Describe(limit)}.");
            return (result.ExitCode, result.ExitCode == 0 ? result.Output
                : string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
