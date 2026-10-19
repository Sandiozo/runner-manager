using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace RunnerWidget;

/// <summary>Logon tasks for the fleet agent and the optional "start with Windows" dashboard.</summary>
public static class ScheduledTasks
{
    public const string AgentTaskName = "RunnerWidget Fleet Agent";
    public const string DashboardTaskName = "RunnerWidget Dashboard";

    /// <summary>
    /// Registers an always-on logon task: runs elevated for the current user, keeps running on battery,
    /// has no 72-hour time limit, and restarts after a failure.
    /// </summary>
    public static async Task RegisterLogonTaskAsync(string name, string exe, string arguments, string description, bool restartOnFailure)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var command = SecurityElement.Escape(exe);
        var args = SecurityElement.Escape(arguments);
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? string.Empty);
        var restart = restartOnFailure
            ? "<RestartOnFailure>\n      <Interval>PT1M</Interval>\n      <Count>999</Count>\n    </RestartOnFailure>"
            : string.Empty;
        var xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{SecurityElement.Escape(description)}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
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
        var xmlPath = Path.Combine(Path.GetTempPath(), $"runnerwidget-task-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(xmlPath, xml, Encoding.Unicode);
            var (code, output) = await RunAsync("schtasks.exe", $"/Create /F /TN \"{name}\" /XML \"{xmlPath}\"");
            if (code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"Could not register the scheduled task \"{name}\"." : output.Trim());
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    public static async Task<bool> ExistsAsync(string name) => (await RunAsync("schtasks.exe", $"/Query /TN \"{name}\"")).ExitCode == 0;

    public static async Task<string> QueryXmlAsync(string name)
    {
        var (code, output) = await RunAsync("schtasks.exe", $"/Query /TN \"{name}\" /XML");
        return code == 0 ? output : string.Empty;
    }

    public static Task<(int ExitCode, string Output)> RunTaskAsync(string name) => RunAsync("schtasks.exe", $"/Run /TN \"{name}\"");
    public static Task<(int ExitCode, string Output)> EndTaskAsync(string name) => RunAsync("schtasks.exe", $"/End /TN \"{name}\"");
    public static Task<(int ExitCode, string Output)> DeleteTaskAsync(string name) => RunAsync("schtasks.exe", $"/Delete /F /TN \"{name}\"");

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

    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName, Arguments = arguments, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process == null) return (-1, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await stderr;
            var output = await stdout;
            return (process.ExitCode, process.ExitCode == 0 ? output : string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
