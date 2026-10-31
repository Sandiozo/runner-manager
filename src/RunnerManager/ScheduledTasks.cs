using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>
/// Scheduled tasks: the fleet agent and the optional "start with Windows" dashboard run at logon, and the launch task
/// starts the dashboard with administrator rights without a UAC prompt.
/// </summary>
public static class ScheduledTasks
{
    /// <summary>One per PC: the agent's ports and single-instance lock are shared by all Windows users (see <see cref="AgentOwnership"/>).</summary>
    public const string AgentTaskName = "Runner Manager Fleet Agent";
    /// <summary>The dashboard's tasks are per Windows user, so a second user's setup never takes over the first user's.</summary>
    public static string DashboardTaskName => $"Runner Manager Startup ({UserTaskSuffix})";
    public static string LaunchTaskName => $"Runner Manager ({UserTaskSuffix})";
    /// <summary>The dashboard's task names before they were per user (2.3 and earlier).</summary>
    public const string SharedDashboardTaskName = "Runner Manager Startup";
    public const string SharedLaunchTaskName = "Runner Manager";
    /// <summary>Task names used before the app was renamed from RunnerWidget.</summary>
    public const string LegacyAgentTaskName = "RunnerWidget Fleet Agent";
    public const string LegacyDashboardTaskName = "RunnerWidget Dashboard";

    /// <summary>The Windows user name, without characters a task name may not contain.</summary>
    internal static string UserTaskSuffix => TaskSafe(Environment.UserName);

    internal static string TaskSafe(string text)
        => string.Concat(text.Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c)).Trim();

    /// <summary>
    /// Registers a logon task that runs elevated for the current user, keeps running on battery and has no 72-hour time
    /// limit. A <paramref name="restartOnFailure"/> task (the always-on fleet agent) also restarts after a failure and is started again
    /// every five minutes while the user is signed in, in case it exited.
    /// </summary>
    public static Task RegisterLogonTaskAsync(string name, string exe, string arguments, string description, bool restartOnFailure)
        => RegisterTaskAsync(name, exe, arguments, description, alwaysOn: restartOnFailure, atLogon: true, parallel: false);

    /// <summary>
    /// Registers a task without a trigger that starts the dashboard elevated when the Start menu shortcut runs it.
    /// Instances may run in parallel, so a second start reaches the running dashboard and brings it to the front.
    /// </summary>
    public static Task RegisterLaunchTaskAsync(string exe) => RegisterTaskAsync(LaunchTaskName, exe, App.FromTaskArgument,
        "Starts Runner Manager with administrator rights, without a UAC prompt.", alwaysOn: false, atLogon: false, parallel: true);

    private static async Task RegisterTaskAsync(string name, string exe, string arguments, string description,
        bool alwaysOn, bool atLogon, bool parallel)
    {
        // A task of another Windows user is never taken over (the names are machine-wide).
        if (await OtherOwnerAsync(name) is { } other)
            throw new InvalidOperationException(name == AgentTaskName
                ? AgentOwnership.Describe(other)
                : $"The scheduled task \"{name}\" belongs to {other}, so it was not changed.");

        var xml = TaskXml(WindowsIdentity.GetCurrent().Name, exe, arguments, description, alwaysOn, atLogon, parallel);
        try
        {
            using (await LockAsync())
            {
                await CreateAsync(name, xml);
                // A registration that raced with another one can be left out of Task Scheduler's index, and then the task
                // never runs ("Element not found"). Registering it again from scratch fixes that.
                if (IsIndexed(name) == false)
                {
                    await RunAsync("schtasks.exe", $"/Delete /F /TN \"{name}\"", ShortTimeout);
                    await CreateAsync(name, xml);
                    if (IsIndexed(name) == false)
                        throw new InvalidOperationException($"Task Scheduler registered \"{name}\" but cannot run it. Restart the PC and try again.");
                }
            }
        }
        finally
        {
            if (name == AgentTaskName) AgentOwnership.Forget();
        }
    }

    /// <summary>
    /// Registers a task again from its own definition when Task Scheduler cannot run it because it is missing from the
    /// scheduler's index: two registrations of the same task at the same moment (2.4 to 2.6 at update time) could leave it
    /// so. Needs administrator rights; returns whether it was repaired.
    /// </summary>
    public static async Task<bool> RepairIfUnindexedAsync(string name)
    {
        if (IsIndexed(name) != false) return false;
        using (await LockAsync())
        {
            if (IsIndexed(name) != false) return false;
            var xml = await QueryXmlAsync(name);
            if (string.IsNullOrWhiteSpace(xml)) return false;
            await RunAsync("schtasks.exe", $"/Delete /F /TN \"{name}\"", ShortTimeout);
            await CreateAsync(name, xml);
            return IsIndexed(name) != false;
        }
    }

    /// <summary>
    /// Repairs this user's Runner Manager tasks that Task Scheduler cannot run (see <see cref="RepairIfUnindexedAsync"/>).
    /// The agent leaves its own task alone: registering it again could end the running agent. Returns the repaired tasks.
    /// </summary>
    public static async Task<List<string>> RepairOwnTasksAsync(bool fromAgent)
    {
        var repaired = new List<string>();
        var names = fromAgent ? new[] { LaunchTaskName, DashboardTaskName } : new[] { AgentTaskName, LaunchTaskName, DashboardTaskName };
        foreach (var name in names)
        {
            try
            {
                if (name == AgentTaskName && await OtherOwnerAsync(name) != null) continue;
                if (await RepairIfUnindexedAsync(name)) repaired.Add(name);
            }
            catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not repair the scheduled task \"{name}\": {ex.Message}"); }
        }
        return repaired;
    }

    private const string TaskCacheKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";

    /// <summary>
    /// Whether Task Scheduler can find the task to run it: every registered task is listed in one of its indexes. Null when
    /// the task does not exist or the index cannot be read (it needs administrator rights).
    /// </summary>
    internal static bool? IsIndexed(string name)
    {
        try
        {
            using var tree = Registry.LocalMachine.OpenSubKey($@"{TaskCacheKey}\Tree\{name}");
            if (tree?.GetValue("Id") is not string id || id.Length == 0) return null;
            foreach (var index in new[] { "Logon", "Boot", "Plain", "Maintenance" })
            {
                using var entry = Registry.LocalMachine.OpenSubKey($@"{TaskCacheKey}\{index}\{id}");
                if (entry != null) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>
    /// Task registrations and deletions run one at a time across Runner Manager's processes (the agent, the dashboard and
    /// setup may each register the same task after an update).
    /// </summary>
    private static async Task<IDisposable> LockAsync()
    {
        Semaphore semaphore;
        try { semaphore = new Semaphore(1, 1, @"Global\RunnerManagerScheduledTasks"); }
        // Created by an elevated process, so a process without administrator rights cannot open it; such a process
        // cannot register these tasks either.
        catch (UnauthorizedAccessException) { return new Release(null); }
        // A process that ended while holding it never releases it, so a long wait goes ahead without it.
        var acquired = await Task.Run(() => semaphore.WaitOne(TimeSpan.FromMinutes(2)));
        if (acquired) return new Release(semaphore);
        semaphore.Dispose();
        return new Release(null);
    }

    private sealed class Release(Semaphore? semaphore) : IDisposable
    {
        public void Dispose()
        {
            if (semaphore == null) return;
            semaphore.Release();
            semaphore.Dispose();
        }
    }

    /// <summary>Registers (or replaces) a task from its XML definition.</summary>
    private static async Task CreateAsync(string name, string xml)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"runnermanager-task-{Guid.NewGuid():N}.xml");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        try
        {
            await using (var write = new FileStream(xmlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await write.WriteAsync(bytes);
            // Opened again read-only and checked, then held open while schtasks reads it: other programs of this user
            // (it is their temp folder) can then neither change nor replace the definition that is registered elevated.
            await using var read = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var written = new byte[bytes.Length + 1];
            var length = 0;
            int count;
            while (length < written.Length && (count = await read.ReadAsync(written.AsMemory(length))) > 0) length += count;
            if (length != bytes.Length || !written.AsSpan(0, length).SequenceEqual(bytes))
                throw new InvalidOperationException($"The definition of the scheduled task \"{name}\" was changed by another program, so it was not registered.");
            var (code, output) = await RunAsync("schtasks.exe", $"/Create /F /TN \"{name}\" /XML \"{xmlPath}\"", ShortTimeout);
            if (code != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(output) ? $"Could not register the scheduled task \"{name}\"." : output.Trim());
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    /// <summary>The task definition. The repeating trigger of an always-on task only starts it while the user is signed in.</summary>
    internal static string TaskXml(string user, string exe, string arguments, string description, bool alwaysOn, bool atLogon, bool parallel)
    {
        var userId = SecurityElement.Escape(user);
        var command = SecurityElement.Escape(exe);
        var args = SecurityElement.Escape(arguments);
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? string.Empty);
        var restart = alwaysOn
            ? "<RestartOnFailure>\n      <Interval>PT1M</Interval>\n      <Count>999</Count>\n    </RestartOnFailure>"
            : string.Empty;
        var trigger = atLogon
            ? $"<LogonTrigger><Enabled>true</Enabled><UserId>{userId}</UserId></LogonTrigger>"
            : string.Empty;
        // Starts an agent that exited (for example after updating itself). A running one is left alone (IgnoreNew).
        if (alwaysOn)
            trigger += $"<TimeTrigger><Repetition><Interval>{KeepAliveInterval}</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                       "<StartBoundary>2020-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger>";
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{SecurityElement.Escape(description)}</Description>
  </RegistrationInfo>
  <Triggers>{trigger}</Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userId}</UserId>
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
    }

    private const string KeepAliveInterval = "PT5M";

    /// <summary>Whether the agent task's definition has everything the current version registers (older ones lack the keep-alive trigger).</summary>
    internal static bool IsCurrentAgentDefinition(string xml)
    {
        if (ParseTask(xml) is not { } task) return false;
        var ns = task.Name.Namespace;
        var settings = task.Element(ns + "Settings");
        var keepAlive = task.Element(ns + "Triggers")?.Elements(ns + "TimeTrigger")
            .Any(t => t.Element(ns + "Repetition")?.Element(ns + "Interval")?.Value == KeepAliveInterval
                      && t.Element(ns + "Enabled")?.Value != "false") == true;
        return keepAlive
               && task.Element(ns + "Triggers")?.Element(ns + "LogonTrigger") != null
               && settings?.Element(ns + "MultipleInstancesPolicy")?.Value == "IgnoreNew"
               && settings.Element(ns + "DisallowStartIfOnBatteries")?.Value == "false"
               && settings.Element(ns + "StopIfGoingOnBatteries")?.Value == "false"
               && settings.Element(ns + "ExecutionTimeLimit")?.Value == "PT0S"
               && settings.Element(ns + "RestartOnFailure") != null;
    }

    /// <summary>
    /// Registers the agent task again when its definition is from an older version, keeping the exe it runs. Re-registering
    /// may end the running agent, so it is started again afterwards (by a helper when the agent does this itself).
    /// </summary>
    public static async Task<bool> UpdateAgentTaskIfOutdatedAsync(bool fromAgent)
    {
        var xml = await QueryXmlAsync(AgentTaskName);
        if (string.IsNullOrWhiteSpace(xml) || IsCurrentAgentDefinition(xml)) return false;
        if (await OtherOwnerAsync(AgentTaskName) != null) return false;
        var exe = CommandOf(xml);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (fromAgent) AppUpdater.ScheduleAgentTaskRestart(AgentTaskName);
        await MachineRelay.RegisterAgentTaskAsync(exe);
        if (!fromAgent) await RunTaskAsync(AgentTaskName);
        return true;
    }

    public static async Task<bool> ExistsAsync(string name) => (await RunAsync("schtasks.exe", $"/Query /TN \"{name}\"", ShortTimeout)).ExitCode == 0;

    public static async Task<string> QueryXmlAsync(string name)
    {
        var (code, output) = await RunAsync("schtasks.exe", $"/Query /TN \"{name}\" /XML", ShortTimeout);
        return code == 0 ? output : string.Empty;
    }

    public static Task<(int ExitCode, string Output)> RunTaskAsync(string name) => RunAsync("schtasks.exe", $"/Run /TN \"{name}\"", ShortTimeout);
    public static Task<(int ExitCode, string Output)> EndTaskAsync(string name) => RunAsync("schtasks.exe", $"/End /TN \"{name}\"", ShortTimeout);
    public static async Task<(int ExitCode, string Output)> DeleteTaskAsync(string name)
    {
        using (await LockAsync()) return await RunAsync("schtasks.exe", $"/Delete /F /TN \"{name}\"", ShortTimeout);
    }

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

    /// <summary>The account a task runs as (its principal's UserId: a SID or DOMAIN\name), or empty.</summary>
    internal static string PrincipalOf(string xml)
    {
        if (ParseTask(xml) is not { } task) return string.Empty;
        var ns = task.Name.Namespace;
        return task.Element(ns + "Principals")?.Element(ns + "Principal")?.Element(ns + "UserId")?.Value.Trim() ?? string.Empty;
    }

    /// <summary>Whether the task is turned on (Settings → Enabled is not false).</summary>
    internal static bool IsEnabled(string xml)
    {
        if (ParseTask(xml) is not { } task) return false;
        var ns = task.Name.Namespace;
        return task.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value.Trim() != "false";
    }

    private static XElement? ParseTask(string xml)
    {
        try { return string.IsNullOrWhiteSpace(xml) ? null : XDocument.Parse(xml.Trim('﻿', ' ', '\r', '\n')).Root; }
        catch { return null; }
    }

    /// <summary>
    /// The account of another Windows user that a task belongs to, or null when the task does not exist or is this user's.
    /// </summary>
    public static async Task<string?> OtherOwnerAsync(string name)
    {
        var principal = PrincipalOf(await QueryXmlAsync(name));
        if (principal.Length == 0) return null;
        return IsCurrentUser(principal) ? null : DisplayName(principal);
    }

    /// <summary>Whether a task principal or process owner (a SID, DOMAIN\name or name) is the user running this process.</summary>
    internal static bool IsCurrentUser(string account)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return IsSameAccount(account, identity.User?.Value ?? string.Empty, identity.Name);
    }

    internal static bool IsSameAccount(string account, string currentSid, string currentName)
    {
        account = account.Trim();
        if (account.Length == 0) return false;
        if (account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return account.Equals(currentSid, StringComparison.OrdinalIgnoreCase);
        try
        {
            var sid = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value;
            return sid.Equals(currentSid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The account cannot be resolved (for example a removed user): compare names.
            var shortName = currentName.Contains('\\') ? currentName[(currentName.IndexOf('\\') + 1)..] : currentName;
            return account.Equals(currentName, StringComparison.OrdinalIgnoreCase)
                   || !account.Contains('\\') && account.Equals(shortName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string DisplayName(string account)
    {
        if (!account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return account;
        try { return new SecurityIdentifier(account).Translate(typeof(NTAccount)).Value; }
        catch { return account; }
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

/// <summary>
/// One Windows user per PC runs the fleet agent: its task name, ports and single-instance lock are shared by everyone
/// on the PC, and it uses that user's pairing code. Other users of the PC can use the dashboard for this PC's runners,
/// but never take the agent over.
/// </summary>
public static class AgentOwnership
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);
    /// <summary>The usual answer (no other user) is kept longer: it costs a process query.</summary>
    private static readonly TimeSpan NoOtherUserCacheTime = TimeSpan.FromMinutes(5);
    private static (string? Other, DateTime CheckedUtc)? _cached;

    public static string Describe(string other)
        => $"Runner Manager's fleet agent on this PC runs for {other}. Only one Windows user per PC can run it. " +
           $"Sign in to Windows as {other} to manage the fleet from this PC, or uninstall Runner Manager there first.";

    /// <summary>
    /// The other Windows user whose agent task or running agent this is, or null when it is this user's (or there is none).
    /// Cached for a minute, or five when there is none; registering the agent task clears it.
    /// </summary>
    public static async Task<string?> OtherUserAsync()
    {
        if (_cached is { } cached && DateTime.UtcNow - cached.CheckedUtc < (cached.Other != null ? CacheTime : NoOtherUserCacheTime))
            return cached.Other;
        var other = await ScheduledTasks.OtherOwnerAsync(ScheduledTasks.AgentTaskName) ?? await Task.Run(OtherUserOfRunningAgent);
        _cached = (other, DateTime.UtcNow);
        return other;
    }

    /// <summary>Throws with an explanation when another Windows user runs the fleet agent on this PC.</summary>
    public static async Task EnsureOursAsync()
    {
        if (await OtherUserAsync() is { } other) throw new InvalidOperationException(Describe(other));
    }

    internal static void Forget() => _cached = null;

    /// <summary>The owner of a running agent process when it is another user (read with WMI; needs administrator rights).</summary>
    private static string? OtherUserOfRunningAgent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
            foreach (ManagementObject process in searcher.Get())
            {
                using (process)
                {
                    if (Convert.ToInt32(process["ProcessId"]) == Environment.ProcessId) continue;
                    if (process["CommandLine"]?.ToString()?.Contains("--agent", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var owner = new object?[2];
                    if (Convert.ToInt32(process.InvokeMethod("GetOwner", owner)) != 0 || owner[0] is not string user) continue;
                    var account = owner[1] is string { Length: > 0 } domain ? $@"{domain}\{user}" : user;
                    if (!ScheduledTasks.IsCurrentUser(account)) return account;
                }
            }
        }
        catch { }
        return null;
    }
}
