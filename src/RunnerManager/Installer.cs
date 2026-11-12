using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace RunnerManager;

public enum FleetChoice { SinglePc, NewFleet, JoinFleet }

/// <summary>The choices made in setup. <see cref="StartWithWindows"/> is null when setup leaves the startup task as it is.</summary>
public sealed record SetupChoices(bool DesktopShortcut, bool? StartWithWindows, FleetChoice Fleet);

/// <summary>
/// Installs Runner Manager: copies the app to its install folder (Program Files\RunnerManager on a PC without an install,
/// see <see cref="MachineInstall"/>), records that folder for administrators, adds this user's shortcuts and entry in
/// Settings → Apps (with an uninstaller), and records the choices made in the setup wizard. The program files may be
/// shared with other Windows users of the PC; the shortcuts, tasks and settings are each user's own.
/// </summary>
public static class Installer
{
    public const string SetupExeName = "RunnerManagerSetup.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RunnerManager";
    internal static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Runner Manager.lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Runner Manager.lnk");

    /// <summary>Run the setup wizard when started as RunnerManagerSetup.exe or with --setup.</summary>
    public static bool IsSetupLaunch(string[] args)
        => args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase))
           || Path.GetFileName(Environment.ProcessPath ?? string.Empty).StartsWith("RunnerManagerSetup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(AppUpdater.InstalledExe) && HasUninstallEntry;

    /// <summary>Whether this user has Runner Manager's entry in Settings → Apps.</summary>
    internal static bool HasUninstallEntry
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key != null;
            }
            catch { return false; }
        }
    }

    /// <summary>The install folder saved in the Settings → Apps entry, when it still exists.</summary>
    internal static string? RegisteredInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string { Length: > 0 } folder && Path.IsPathFullyQualified(folder) && Directory.Exists(folder)
                ? folder : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The folder to install into for a folder the user picked. It gets its own RunnerManager subfolder, unless it already
    /// is one, so uninstalling never touches other files.
    /// </summary>
    public static string InstallFolderFor(string chosen)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        return Path.GetFileName(full).Equals("RunnerManager", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(full, AppPaths.ExeName))
            ? full : Path.Combine(full, "RunnerManager");
    }

    /// <summary>Null when Runner Manager can be installed to the folder, otherwise the message to show.</summary>
    public static string? InstallFolderProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return @"Choose a full folder path, such as D:\Apps.";
        if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return "Choose a folder on this PC. Network folders are not supported.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && (Path.GetFullPath(folder) + Path.DirectorySeparatorChar)
                .StartsWith(Path.TrimEndingDirectorySeparator(windows) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "Choose a folder outside the Windows folder.";
        return null;
    }

    public static async Task<string> InstallAsync(SetupChoices choices, Action<string> log)
    {
        var target = AppUpdater.InstallDir;
        // The copy that is installed now, when setup moves it to another folder: only the recorded one (see OwnInstall).
        var previous = OwnInstall(MachineInstall.ReadOwnRecord());
        var moving = previous != null && !AppUpdater.SameFolder(previous, target);
        var recordBefore = MachineInstall.ReadOwnRecordRaw();
        // Program Files is shared by the Windows users of the PC. A copy that others use is never replaced by an older one.
        // Only users that are known count here: setup never takes a copy that is already there for the installed one
        // because some tasks could not be read.
        var sharedWith = await MachineInstall.OtherUsersOfAsync(target);
        var keepInstalled = KeepsInstalledCopy(sharedWith.Names.Count > 0, AppUpdater.InstalledVersion, AppUpdater.CurrentVersion);
        if (sharedWith.Names.Count > 0) log($"Other Windows users on this PC use Runner Manager in {target}: {string.Join(", ", sharedWith.Names)}.");
        // An AI tool's host keeps running the replaced exe and needs nothing from setup; only a move takes its exe away.
        CloseRunningDashboards(log, moving ? new[] { target, previous! } : new[] { target }, endAssistants: moving);
        var replacing = File.Exists(AppUpdater.InstalledExe) && !AppUpdater.IsRunningInstalledCopy && !keepInstalled;
        // Setup warns on its first page when it is older than this user's installed copy, so going ahead is the user's choice.
        var exe = await AppUpdater.InstallCurrentBuildAsync(log, createShortcut: false, allowOlder: !keepInstalled);
        // The installed copy run as setup copies nothing, but still puts its start helper in place, and checks it, before
        // the tasks; only while it is still the installed file (see AppUpdater.IsInstalledBuild).
        if (AppUpdater.IsRunningInstalledCopy) await AppUpdater.EnsureStartHelperAsync(log);
        try
        {
            await FinishInstallAsync(choices, exe, moving ? previous : null, log);
        }
        catch (Exception ex)
        {
            // A half-done setup is undone, so the installed copy keeps working as before.
            log($"Setup failed: {ex.Message} Undoing it...");
            await UndoInstallAsync(replacing, moving ? previous : null, recordBefore, keepNewCopy: sharedWith.KeepFiles, log);
            throw;
        }
        return exe;
    }

    /// <summary>
    /// This user's install before setup, which a move to another folder closes and removes: the folder recorded for this
    /// user (an install of an earlier version that this user's own tasks start is recorded before setup opens). Never the
    /// folder of the Settings → Apps entry or the default of earlier versions alone: any program of the user can change
    /// those, so a copy there that setup could not confirm is left where it is.
    /// </summary>
    internal static string? OwnInstall(string? ownRecord) => ownRecord;

    /// <summary>
    /// Whether setup keeps the copy in the install folder instead of putting its own build there: other Windows users use
    /// that copy (<paramref name="shared"/>) and it is newer than this setup. Never an older version over a newer one that
    /// someone else runs.
    /// </summary>
    internal static bool KeepsInstalledCopy(bool shared, string? installedVersion, string setupVersion)
        => shared && installedVersion != null && AppUpdater.IsOlder(setupVersion, installedVersion);

    private static async Task FinishInstallAsync(SetupChoices choices, string exe, string? previous, Action<string> log)
    {
        var moving = previous != null;
        log("Adding shortcuts...");
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (choices.DesktopShortcut) await CreateShortcutAsync(DesktopShortcut, exe);
        else TryDelete(DesktopShortcut);

        log("Registering Runner Manager in Settings → Apps...");
        RegisterUninstallEntry(exe);
        // The parts that run as administrator take the install folder from this record, never from Settings → Apps.
        MachineInstall.WriteOwnRecord(AppUpdater.InstallDir);

        await ScheduledTasks.RegisterLaunchTaskAsync(exe);
        await MigrateSharedTasksAsync(exe);
        if (choices.StartWithWindows == true) await RegisterStartupTaskAsync(exe);
        else if (choices.StartWithWindows == false) await ScheduledTasks.DeleteOwnTaskAsync(ScheduledTasks.DashboardTaskName);
        // Unknown (the task could not be read): kept as it is, but pointed at this copy when it exists.
        else if (!await UpdateStartupTaskAsync(exe)) log("The startup task could not be updated now. Runner Manager updates it on a later start.");
        await LegacyInstall.MigrateAsync(log, fromSetup: true);

        // An agent from an earlier install keeps running from the old place; point it at the new copy. Another Windows
        // user's agent is left alone, and so is a task under the agent's name that is not this user's own with the
        // highest rights (see IsAgentInstalledAsync).
        if (await AgentOwnership.OtherUserAsync() is { } other)
            log($"The fleet agent on this PC runs for {other}, so it was left alone.");
        else if (await MachineRelay.IsAgentInstalledAsync())
        {
            log("Updating the fleet agent...");
            await MachineRelay.UpdateLocalAgentAsync(log);
        }

        if (moving && await MachineInstall.OtherUsersOfAsync(previous!) is { KeepFiles: true } others)
            log($"The copy in {previous} was kept, because {others.KeptBecause("it")}.");
        else if (moving)
        {
            // The old copy may still be in use until this process exits (its agent was just switched over).
            log($"Removing the copy in {previous}...");
            AppUpdater.RunAfterExit(UnlessInstalledIn(RemoveProgramFilesScript(previous!)), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3),
                FolderEnvironment(previous!));
        }

        var settings = AppSettings.Load();
        settings.SetupCompleted = true;
        if (choices.StartWithWindows is { } startsWithWindows) settings.StartMinimizedHint = startsWithWindows;
        settings.Save();
        InstallChecks.MarkDone(LaunchTaskCheck);
        log("Installed.");
    }

    /// <summary>
    /// Puts back the exe that setup replaced (kept as RunnerManager.exe.previous) and this user's record of the install
    /// folder. When setup was moving the install, the shortcuts, Settings → Apps entry, launch task, startup task and fleet
    /// agent task point at the old folder again (see <see cref="PointTasksBackAsync"/>), and the new copy is removed,
    /// unless other Windows users use it (<paramref name="keepNewCopy"/>).
    /// </summary>
    private static async Task UndoInstallAsync(bool replaced, string? previousFolder, string? recordBefore, bool keepNewCopy, Action<string> log)
    {
        try
        {
            if (replaced) AppUpdater.RestorePrevious(new SwapMarker(SwapKind.Undo, AppUpdater.CurrentVersion, string.Empty));
        }
        catch (Exception ex) { log($"Could not put the previous version back: {ex.Message}"); }
        try { MachineInstall.WriteOwnRecord(recordBefore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log($"Could not record the install folder again: {ex.Message}");
        }
        if (previousFolder == null) return;
        var newFolder = AppUpdater.InstallDir;
        var oldExe = Path.Combine(previousFolder, AppPaths.ExeName);
        try
        {
            AppUpdater.UseInstallDir(previousFolder);
            await RefreshShortcutsAsync(oldExe);
            // The tasks go back to the old folder, through the start helper there when it is in place (the old folder is
            // only removed after a move that worked, so a start helper of a version that has one is still there). None is
            // written there: the old copy may be a version before the start helper, and it then starts its tasks at
            // RunnerManager.exe as before.
            await ScheduledTasks.RegisterLaunchTaskAsync(oldExe);
            await PointTasksBackAsync(oldExe, newFolder, exe => MachineRelay.ReplaceAndStartAgentTaskAsync(exe, log));
            // Kept when setup is tried again and installs there after all.
            if (!keepNewCopy)
                AppUpdater.RunAfterExit(UnlessInstalledIn(RemoveProgramFilesScript(newFolder)), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3),
                    FolderEnvironment(newFolder));
        }
        catch (Exception ex) { log($"Could not point Runner Manager back to {previousFolder}: {ex.Message}"); }
    }

    /// <summary>
    /// After a failed move: this user's startup task and fleet agent task start the old copy (<paramref name="oldExe"/>)
    /// again when setup had already pointed them at <paramref name="newFolder"/>, which is then removed. Only this user's
    /// own tasks with the highest rights (see <see cref="ScheduledTasks.ExeOfOwnElevatedTask"/>). The fleet agent is
    /// switched by <paramref name="pointAgent"/>, which also restarts it.
    /// </summary>
    internal static async Task PointTasksBackAsync(string oldExe, string newFolder, Func<string, Task> pointAgent)
    {
        bool InNewFolder(string xml) => ScheduledTasks.ExeOfOwnElevatedTask(xml) is { } exe
                                        && Path.GetDirectoryName(exe) is { Length: > 0 } dir && AppUpdater.SameFolder(dir, newFolder);
        if (InNewFolder(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.DashboardTaskName))) await RegisterStartupTaskAsync(oldExe);
        if (InNewFolder(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.AgentTaskName))) await pointAgent(oldExe);
    }

    /// <summary>The environment variable that gives the helper scripts below the program folder.</summary>
    internal const string FolderVariable = "RUNNER_MANAGER_FOLDER";
    /// <summary>The environment variable that gives the uninstall helper the data folder.</summary>
    internal const string DataVariable = "RUNNER_MANAGER_DATA";

    /// <summary>
    /// The environment for a helper script about <paramref name="folder"/> (see <see cref="FolderVariable"/>). Paths reach
    /// PowerShell only this way, never inside a script's text: PowerShell also ends a quoted string at ’ ‘ ‚ and ‛, so
    /// doubling ' is not enough to keep a folder name from changing what runs.
    /// </summary>
    internal static Dictionary<string, string> FolderEnvironment(string folder)
        => new() { [FolderVariable] = Path.TrimEndingDirectorySeparator(folder) };

    /// <summary>
    /// PowerShell that runs <paramref name="script"/> unless the Settings → Apps entry points at the folder in
    /// <see cref="FolderVariable"/> by then: a setup that is tried again in the same window may have installed there after all.
    /// </summary>
    internal static string UnlessInstalledIn(string script)
        => $"$at = (Get-ItemProperty -LiteralPath 'HKCU:\\{UninstallKey}' -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation; " +
           $"if (-not $at -or $at.TrimEnd([char]92) -ne ([string]$env:{FolderVariable}).TrimEnd([char]92)) {{ {script} }}";

    internal static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <summary>
    /// Whether Runner Manager starts with Windows for this user: by its own startup task, or by one of an older version
    /// that setup moves over (the shared task of 2.3 and earlier, or RunnerWidget's). Each counts only as this user's own
    /// task with the highest rights (see <see cref="ScheduledTasks.IsOwnElevatedTask"/>), so a task under the name that
    /// another program created never makes setup register the startup task. Null when a task could not be read, so setup
    /// leaves the startup task as it is.
    /// </summary>
    internal static async Task<bool?> StartsWithWindowsAsync() => AnyStartupTask(
        await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.DashboardTaskName),
        await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.SharedDashboardTaskName),
        await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.LegacyDashboardTaskName));

    /// <summary>True when any startup task was found, otherwise null when one could not be read, otherwise false.</summary>
    internal static bool? AnyStartupTask(params bool?[] found)
        => found.Any(f => f == true) ? true : found.Any(f => f == null) ? null : false;

    public static Task RegisterStartupTaskAsync(string exe) => ScheduledTasks.RegisterLogonTaskAsync(
        ScheduledTasks.DashboardTaskName, exe, "--minimized",
        "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);

    private const string LaunchTaskCheck = "launch-task";

    /// <summary>
    /// The installed dashboard opens through the launch task, so make sure this user's task exists, is turned on and starts
    /// the installed exe (through the start helper while it is in place), and keep the version in Settings → Apps current
    /// after updates. A launch or "start with Windows" task that an older version registered below normal priority, or at
    /// the exe while the start helper is in place, is registered again. The tasks are checked once per
    /// version, or again when <paramref name="force"/> is set (the dashboard had to ask with UAC). A task that runs now (the
    /// dashboard itself may have been started by it) is left for a later start, by the dashboard or by the fleet agent.
    /// </summary>
    public static async Task EnsureLaunchTaskAsync(bool force = false)
    {
        // Only the installed build: an earlier copy that still runs after an update leaves the tasks, the start helper and
        // the version in Settings → Apps to the copy that is installed now.
        if (!AppUpdater.IsInstalledBuild) return;
        RefreshUninstallEntryVersion();
        // On every start: antivirus software may remove the start helper at any time, and a task must never name a file
        // that is gone. Puts it back or points this user's tasks at the exe (see Launcher.EnsureForThisCopyAsync).
        await Launcher.EnsureForThisCopyAsync();
        var exe = AppUpdater.InstalledExe;
        // The mark is in the data folder, which any program of the user can change: a task that still starts the exe
        // while the start helper is in place is moved whatever the mark says.
        if (!force && InstallChecks.IsDone(LaunchTaskCheck) && !await SkipsStartHelperAsync(exe)) return;
        var done = true;
        if (LaunchTaskNeedsRegistering(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.LaunchTaskName), exe))
            done &= await RegisterIfIdleAsync(ScheduledTasks.LaunchTaskName, () => ScheduledTasks.RegisterLaunchTaskAsync(exe));
        done &= await UpdateStartupTaskAsync(exe);
        done &= await MigrateSharedTasksAsync(exe);
        if (done) InstallChecks.MarkDone(LaunchTaskCheck);
    }

    /// <summary>
    /// Whether this user's launch task, or turned-on startup task, starts <paramref name="exe"/> directly although the start
    /// helper beside it is in place and this process saw it work (see <see cref="Launcher.CommandFor"/>). Reads no task
    /// otherwise.
    /// </summary>
    internal static async Task<bool> SkipsStartHelperAsync(string exe)
        => string.Equals(Launcher.CommandFor(exe), Launcher.PathBeside(exe), StringComparison.OrdinalIgnoreCase)
           && (await TasksStartingExeAsync(exe)).Count > 0;

    /// <summary>
    /// This user's own launch task and turned-on startup task, with the highest rights, that start <paramref name="exe"/>
    /// itself rather than the start helper beside it.
    /// </summary>
    internal static async Task<List<string>> TasksStartingExeAsync(string exe)
    {
        var names = new List<string>();
        bool StartsExe(string xml) => ScheduledTasks.CommandOfOwnElevatedTask(xml) is { } command
                                      && string.Equals(command, exe, StringComparison.OrdinalIgnoreCase);
        if (StartsExe(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.LaunchTaskName))) names.Add(ScheduledTasks.LaunchTaskName);
        var startup = await ScheduledTasks.QueryXmlAsync(ScheduledTasks.DashboardTaskName);
        if (startup.Length > 0 && ScheduledTasks.IsEnabled(startup) && StartsExe(startup)) names.Add(ScheduledTasks.DashboardTaskName);
        return names;
    }

    /// <summary>
    /// Registers this user's "start with Windows" task again when it needs it (see <see cref="StartupTaskNeedsRegistering"/>).
    /// A task under its name that is not this user's own with the highest rights is left as it is, and logged. False when
    /// the registration waits.
    /// </summary>
    internal static async Task<bool> UpdateStartupTaskAsync(string exe)
    {
        var xml = await ScheduledTasks.QueryXmlAsync(ScheduledTasks.DashboardTaskName);
        if (xml.Length > 0 && !ScheduledTasks.IsOwnElevatedTask(xml)) ScheduledTasks.LogLeftAlone(ScheduledTasks.DashboardTaskName, xml);
        return !StartupTaskNeedsRegistering(xml, exe)
               || await RegisterIfIdleAsync(ScheduledTasks.DashboardTaskName, () => RegisterStartupTaskAsync(exe));
    }

    /// <summary>
    /// Changes a task (<paramref name="change"/> registers it again or deletes it) only while no instance of it runs.
    /// Registering a running task again does not end its instance on Windows 11 (measured; other Windows versions were
    /// not), but Task Scheduler then refuses a new start of the task until that instance ends. The dashboard that asks may
    /// be such an instance itself (see <see cref="StartHelperRestart"/>). Returns false when it was left for later.
    /// </summary>
    internal static async Task<bool> RegisterIfIdleAsync(string name, Func<Task> change)
    {
        if (await ScheduledTasks.IsRunningAsync(name) != false) return false;
        await change();
        return true;
    }

    /// <summary>
    /// Whether the launch task must be registered: it is missing, cannot start <paramref name="exe"/>, runs below normal
    /// priority, or does not run what <see cref="Launcher.CommandFor"/> gives for <paramref name="exe"/> (it moves to the
    /// start helper once that is in place, and back to the exe when this PC runs without it).
    /// </summary>
    internal static bool LaunchTaskNeedsRegistering(string xml, string exe)
        => xml.Length == 0 || !Elevation.IsUsableLaunchTask(xml, exe) || !ScheduledTasks.HasCurrentPriority(xml)
           || !RunsCommandFor(xml, exe);

    /// <summary>
    /// Whether this user's "start with Windows" task must be registered again: it is turned on but starts another exe,
    /// runs below normal priority, or does not run what <see cref="Launcher.CommandFor"/> gives for <paramref name="exe"/>
    /// (the start helper beside it, or the exe). A task that runs the start helper beside <paramref name="exe"/> starts
    /// that exe. A missing or turned-off task is left as the user chose, and so is a task under the name that is not this
    /// user's own with the highest rights (see <see cref="ScheduledTasks.IsOwnElevatedTask"/>): any program of the user
    /// can create one, and it must not make Runner Manager register its startup task.
    /// </summary>
    internal static bool StartupTaskNeedsRegistering(string xml, string exe)
        => xml.Length > 0 && ScheduledTasks.IsEnabled(xml) && ScheduledTasks.ExeOfOwnElevatedTask(xml) is { } started
           && (!ScheduledTasks.HasCurrentPriority(xml) || !string.Equals(started, exe, StringComparison.OrdinalIgnoreCase)
               || !RunsCommandFor(xml, exe));

    /// <summary>Whether a task runs exactly what <see cref="Launcher.CommandFor"/> gives for <paramref name="exe"/> and what it runs now.</summary>
    private static bool RunsCommandFor(string xml, string exe)
        => ScheduledTasks.CommandOfOwnElevatedTask(xml) is { } command
           && string.Equals(command, Launcher.CommandFor(exe, command), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 2.3 and earlier named the launch and startup tasks the same for every Windows user, so a second user's setup took
    /// them over. When they are this user's own with the highest rights, they move to this user's own names; another
    /// user's, and a task under the name that any program could have created, are left alone. A shared task that runs now
    /// is moved on a later start. Returns false when something was left for later.
    /// </summary>
    internal static async Task<bool> MigrateSharedTasksAsync(string exe)
    {
        var done = true;
        if (await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.SharedDashboardTaskName) == true)
            done &= await RegisterIfIdleAsync(ScheduledTasks.SharedDashboardTaskName, async () =>
            {
                await RegisterStartupTaskAsync(exe);
                await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.SharedDashboardTaskName);
            });
        if (await ScheduledTasks.IsOwnElevatedTaskAsync(ScheduledTasks.SharedLaunchTaskName) == true)
            done &= await RegisterIfIdleAsync(ScheduledTasks.SharedLaunchTaskName,
                () => ScheduledTasks.DeleteTaskAsync(ScheduledTasks.SharedLaunchTaskName));
        return done;
    }

    /// <summary>Setup writes DisplayVersion, and updates replace the exe without setup, so refresh it when it is outdated.</summary>
    private static void RefreshUninstallEntryVersion()
    {
        try
        {
            string? stored;
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (key == null) return;
                stored = key.GetValue("DisplayVersion") as string;
            }
            if (!string.Equals(stored, AppUpdater.CurrentVersion, StringComparison.Ordinal))
                RegisterUninstallEntry(AppUpdater.InstalledExe);
        }
        catch { }
    }

    /// <summary>Points the shortcuts that exist and the Settings → Apps entry at <paramref name="exe"/>.</summary>
    public static async Task RefreshShortcutsAsync(string exe)
    {
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (File.Exists(DesktopShortcut)) await CreateShortcutAsync(DesktopShortcut, exe);
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key != null) RegisterUninstallEntry(exe);
    }

    /// <summary>
    /// Closes every dashboard of this Windows session that runs from the install folder, including one hidden in the
    /// notification area, so it can be replaced or removed and its single-instance lock is freed. AI tools' hosts (--mcp,
    /// ai) are ended too when <paramref name="endAssistants"/> is set. The fleet agent (--agent) is handled separately, and
    /// setup, uninstall and agent installs are left to finish. Other Windows users' copies are theirs: a running exe can
    /// still be replaced, and uninstall keeps program files that other users use.
    /// </summary>
    private static void CloseRunningDashboards(Action<string> log, IEnumerable<string> folders, bool endAssistants)
    {
        var installDirs = folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar).ToList();
        var dashboards = new List<Process>();
        var session = Process.GetCurrentProcess().SessionId;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var id = Convert.ToInt32(item["ProcessId"]);
                    var path = item["ExecutablePath"]?.ToString() ?? string.Empty;
                    var inThisSession = Convert.ToInt32(item["SessionId"]) == session;
                    if (!ClosesProcess(id, item["CommandLine"]?.ToString(), path, inThisSession, installDirs, endAssistants)) continue;
                    try { dashboards.Add(Process.GetProcessById(id)); } catch (ArgumentException) { }
                }
            }
        }
        catch (Exception ex)
        {
            log($"Could not list running copies of Runner Manager: {ex.Message}");
        }
        if (dashboards.Count == 0) return;

        log("Closing Runner Manager...");
        // Asked first, so a dashboard saves its settings and removes its tray icon; ended only when it has not exited after a few seconds.
        EventWaitHandle? exit = null;
        try { if (EventWaitHandle.TryOpenExisting(App.ExitEventName, out var opened)) (exit = opened).Set(); }
        catch { }
        var deadline = DateTime.UtcNow + DashboardExitTimeout;
        foreach (var process in dashboards)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !process.WaitForExit(left)) process.Kill();
            }
            catch { }
        }
        foreach (var process in dashboards)
        {
            try { process.WaitForExit(5000); } catch { }
            process.Dispose();
        }
        // Not left set for a dashboard that starts later (setup starts the new one).
        try { exit?.Reset(); } catch { }
        exit?.Dispose();
    }

    private static readonly TimeSpan DashboardExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The filter of <see cref="CloseRunningDashboards"/>: a process that <see cref="IsClosedBySetup"/> ends, which runs
    /// from one of <paramref name="installDirs"/> (each ending in a folder separator), and never this process.
    /// </summary>
    internal static bool ClosesProcess(int processId, string? commandLine, string path, bool inThisSession, IReadOnlyList<string> installDirs, bool endAssistants)
        => processId != Environment.ProcessId && IsClosedBySetup(commandLine, endAssistants, path, inThisSession)
           && installDirs.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether setup or uninstall ends a Runner Manager process with this command line before it replaces the exe. Only
    /// processes in this Windows session (<paramref name="inThisSession"/>): another user's dashboard is never ended.
    /// </summary>
    internal static bool IsClosedBySetup(string? commandLine, bool endAssistants, string? programPath = null, bool inThisSession = true)
        => inThisSession && RunningCopies.RoleOf(commandLine, programPath) switch
        {
            CopyRole.Dashboard => true,
            CopyRole.Assistant => endAssistants,
            _ => false
        };

    private static void RegisterUninstallEntry(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Runner Manager");
        key.SetValue("DisplayVersion", AppUpdater.CurrentVersion);
        key.SetValue("Publisher", "Runner Manager");
        key.SetValue("DisplayIcon", $"{exe},0");
        key.SetValue("InstallLocation", AppUpdater.InstallDir);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
        key.SetValue("URLInfoAbout", $"https://github.com/{AppUpdater.ReleaseRepository}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            var size = new DirectoryInfo(AppUpdater.InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>
    /// Removes Runner Manager for this Windows user: leaves the fleet (the other PCs forget it), removes the agent, the
    /// start-up task, shortcuts, the Apps entry and the record of the install folder, then deletes the program folder (and
    /// optionally settings and logs) after exit. The program folder stays while other Windows users of the PC use it.
    /// GitHub runners are not touched. Returns a sentence to add to the result, or empty.
    /// </summary>
    public static async Task<string> UninstallAsync(bool removeData, Action<string> log)
    {
        // Read before the record and the Settings → Apps entry are deleted below.
        var installDir = AppUpdater.InstallDir;
        CloseRunningDashboards(log, new[] { installDir }, endAssistants: true);
        // Another Windows user's agent stays: it is their fleet membership, not this user's.
        if (await AgentOwnership.OtherUserAsync() is { } other)
            log($"The fleet agent on this PC runs for {other}, so it was left alone.");
        else if (FleetConfig.Current.FleetKey != null || await MachineRelay.IsAgentInstalledAsync())
            await MachineRelay.LeaveFleetAsync(log);
        log("Removing the scheduled tasks and shortcuts...");
        await RemoveOwnDashboardTasksAsync();
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        // Program Files is shared by the Windows users of the PC: the program files stay while another user's record or
        // tasks use them, or while that cannot be told. This user's record goes either way.
        var others = await MachineInstall.OtherUsersOfAsync(installDir);
        try { MachineInstall.WriteOwnRecord(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log($"Could not remove the record of the install folder: {ex.Message}");
        }
        // The running exe can't delete its own folder; a helper does it once this process has exited.
        var scripts = new List<string>();
        var environment = new Dictionary<string, string>();
        var note = string.Empty;
        if (!others.KeepFiles)
        {
            scripts.Add(RemoveProgramFilesScript(installDir));
            foreach (var (name, value) in FolderEnvironment(installDir)) environment[name] = value;
        }
        else
        {
            log($"The program files in {installDir} were kept, because {others.KeptBecause("them")}.");
            note = $" The program files in {installDir} were kept, because other Windows users on this PC {(others.Names.Count > 0 ? "use" : "may use")} them.";
        }
        if (removeData)
        {
            // Only together with the settings. The values are the only mark that the switches were moved from fleet.json,
            // so without them the move runs once more, as on a first install: at the next install, or when another Windows
            // user's Runner Manager on this PC starts next (the values are the same for every user). Whatever that
            // fleet.json holds then goes into the registry, also a copy the deletion below could not remove or another
            // program wrote. This risk is the same as on a first install, and it is accepted.
            try { AssistantSwitches.Remove(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                log($"The AI assistant settings could not be removed: {ex.Message}");
            }
            // Startup scripts that converting a runner to a service turned off exist nowhere else, so they are never deleted.
            var startup = KeepDisabledStartupScripts(log);
            scripts.Add(RemoveDataScript(keepDisabledStartup: !startup.Moved && startup.Folder != null));
            environment[DataVariable] = FleetConfig.StateDir;
            if (startup.Folder != null)
                note += $" Startup scripts that Runner Manager had turned off are {(startup.Moved ? "now" : "kept")} in {startup.Folder}.";
        }
        // No time limit: the "uninstalled" message keeps this process (and so its exe) until it is closed.
        AppUpdater.RunAfterExit(scripts.Count == 0 ? "$null" : string.Join("; ", scripts), timeout: null, TimeSpan.FromSeconds(1), environment);
        log("Runner Manager was removed.");
        return note;
    }

    /// <summary>
    /// Deletes this user's dashboard tasks for uninstall: the startup and launch tasks, those of 2.3 and earlier and
    /// RunnerWidget's startup task. Each only when it is this user's own task with the highest rights (see
    /// <see cref="ScheduledTasks.DeleteOwnTaskAsync"/>); another Windows user's task, or one that any program could have
    /// created under the name, is left as it is.
    /// </summary>
    internal static async Task RemoveOwnDashboardTasksAsync()
    {
        foreach (var task in new[] { ScheduledTasks.DashboardTaskName, ScheduledTasks.LaunchTaskName, ScheduledTasks.LegacyDashboardTaskName,
                     ScheduledTasks.SharedDashboardTaskName, ScheduledTasks.SharedLaunchTaskName })
            await ScheduledTasks.DeleteOwnTaskAsync(task);
    }

    /// <summary>
    /// PowerShell that deletes the data folder in <see cref="DataVariable"/>, or everything in it except the startup
    /// scripts Runner Manager had turned off (<paramref name="keepDisabledStartup"/>). It runs with administrator rights,
    /// and any program of the user can change the data folder, so nothing is deleted through a link (a junction or
    /// symbolic link): the script never goes into one, and deletes the link itself. A data folder that is a link itself
    /// is only unlinked, or with <paramref name="keepDisabledStartup"/> left as it is. A link swapped in while the script
    /// runs can still be followed.
    /// </summary>
    internal static string RemoveDataScript(bool keepDisabledStartup)
        => ("$d = $env:" + DataVariable + "; if ($d) { " +
            // Deletes what is in folder $p, except the entry named $keep, without going into links.
            "function Remove-Inside($p, $keep) { Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne $keep } | " +
            "ForEach-Object { $e = $_; if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { " +
            "try { if ($e.PSIsContainer) { [System.IO.Directory]::Delete($e.FullName) } else { [System.IO.File]::Delete($e.FullName) } } catch { } } " +
            "elseif ($e.PSIsContainer) { Remove-Inside $e.FullName ''; try { [System.IO.Directory]::Delete($e.FullName) } catch { } } " +
            "else { Remove-Item -LiteralPath $e.FullName -Force -ErrorAction SilentlyContinue } } }; " +
            "$i = Get-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue; " +
            "if ($i -and ($i.Attributes -band [IO.FileAttributes]::ReparsePoint)) { UNLINK} " +
            "elseif ($i) { Remove-Inside $d 'KEEP'; REMOVE} }")
            .Replace("UNLINK", keepDisabledStartup ? string.Empty : "try { [System.IO.Directory]::Delete($d) } catch { } ")
            .Replace("KEEP", keepDisabledStartup ? DisabledStartupFolderName : string.Empty)
            .Replace("REMOVE", keepDisabledStartup ? string.Empty : "try { [System.IO.Directory]::Delete($d) } catch { } ");

    private const string DisabledStartupFolderName = "disabled-startup";

    /// <summary>
    /// Moves the startup scripts that Runner Manager turned off (when a console runner became a service) from its data
    /// folder to Documents\Runner Manager startup scripts. Returns where they are, and whether they were moved; the folder
    /// is null when there are none.
    /// </summary>
    internal static (string? Folder, bool Moved) KeepDisabledStartupScripts(Action<string> log, string? documents = null)
        => KeepDisabledStartupScripts(log, documents, Path.Combine(FleetConfig.StateDir, DisabledStartupFolderName));

    /// <summary>
    /// <see cref="KeepDisabledStartupScripts(Action{string}, string?)"/> from <paramref name="source"/>. It runs with
    /// administrator rights, and any program of the user can change the data folder, so nothing is copied through a link
    /// (a junction or symbolic link): a link inside is left out, and a source that is a link itself stays where it is.
    /// </summary>
    internal static (string? Folder, bool Moved) KeepDisabledStartupScripts(Action<string> log, string? documents, string source)
    {
        try
        {
            if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any()) return (null, false);
            if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                log($"The folder of the startup scripts that Runner Manager had turned off is a link, so they were not moved. They stay in {source}.");
                return (source, false);
            }
        }
        catch { return (null, false); }
        try
        {
            documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var target = Path.Combine(documents, "Runner Manager startup scripts");
            for (var i = 2; Directory.Exists(target) || File.Exists(target); i++)
                target = Path.Combine(documents, $"Runner Manager startup scripts ({i})");
            Directory.CreateDirectory(target);
            // Copied, not moved: Documents may be on another drive. Hidden files are copied too, links are left out.
            var files = new System.IO.EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var file in Directory.EnumerateFiles(source, "*", files))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            log($"Moved the startup scripts that Runner Manager had turned off to {target}.");
            return (target, true);
        }
        catch (Exception ex)
        {
            log($"Could not move the startup scripts that Runner Manager had turned off ({ex.Message}). They stay in {source}.");
            return (source, false);
        }
    }

    /// <summary>
    /// PowerShell that removes Runner Manager from the program folder in <see cref="FolderVariable"/> (see
    /// <see cref="FolderEnvironment"/>). Program Files\RunnerManager only holds this app and only administrators can change
    /// it, so it is removed completely. Any other folder loses only Runner Manager's own files, and is removed when it is
    /// then empty: the default of earlier versions is in the user's profile, where a program without administrator rights
    /// could put a link in it that a recursive delete would follow, and a folder chosen in setup may hold other files.
    /// Runner Manager's own download folders there (see <see cref="AppUpdater.StagingFolder"/>) go too, when they hold
    /// only files and are no link themselves, and so do the start helper with its files and what a self-contained exe
    /// unpacked through it (.net\RunnerManager, without going into links; .net itself only when it is then empty).
    /// </summary>
    internal static string RemoveProgramFilesScript(string folder) => RemoveProgramFilesScript(AppUpdater.IsDefaultInstallDir(folder));

    /// <summary>
    /// <see cref="RemoveProgramFilesScript(string)"/>, with the whole folder removed when <paramref name="wholeFolder"/>
    /// is true and only Runner Manager's own files otherwise. Only tests choose the form themselves.
    /// </summary>
    internal static string RemoveProgramFilesScript(bool wholeFolder)
    {
        // Retried for about 20 seconds: a process that just exited, or one using the folder as its working folder, can
        // still hold it for a moment.
        var remove = wholeFolder
            ? "Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction SilentlyContinue"
            : "foreach ($p in " + string.Join(", ", OwnFilePatterns.Select(p => $"'{p}'")) + ") " +
              "{ Get-ChildItem -LiteralPath $f -Filter $p -File -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }; " +
              // What a self-contained exe unpacked through the start helper: removed without going into links.
              "function Remove-Tree($p) { Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue | ForEach-Object { $e = $_; " +
              "if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { try { if ($e.PSIsContainer) { [System.IO.Directory]::Delete($e.FullName) } else { [System.IO.File]::Delete($e.FullName) } } catch { } } " +
              "elseif ($e.PSIsContainer) { Remove-Tree $e.FullName; try { [System.IO.Directory]::Delete($e.FullName) } catch { } } " +
              "else { Remove-Item -LiteralPath $e.FullName -Force -ErrorAction SilentlyContinue } } }; " +
              "$n = Join-Path $f '" + Launcher.ExtractionFolderName + "'; $ni = Get-Item -LiteralPath $n -Force -ErrorAction SilentlyContinue; " +
              "if ($ni -and ($ni.Attributes -band [IO.FileAttributes]::ReparsePoint)) { try { [System.IO.Directory]::Delete($n) } catch { } } " +
              "elseif ($ni) { $a = Join-Path $n 'RunnerManager'; $ai = Get-Item -LiteralPath $a -Force -ErrorAction SilentlyContinue; " +
              "if ($ai -and ($ai.Attributes -band [IO.FileAttributes]::ReparsePoint)) { try { [System.IO.Directory]::Delete($a) } catch { } } " +
              "elseif ($ai) { Remove-Tree $a; try { [System.IO.Directory]::Delete($a) } catch { } }; " +
              "if (-not (Get-ChildItem -LiteralPath $n -Force -ErrorAction SilentlyContinue)) { try { [System.IO.Directory]::Delete($n) } catch { } } }; " +
              "Get-ChildItem -LiteralPath $f -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^RunnerManager\\.update-\\d+$' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | " +
              "ForEach-Object { $u = $_; $c = @(Get-ChildItem -LiteralPath $u.FullName -Force -ErrorAction SilentlyContinue); " +
              "if (-not ($c | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) })) " +
              "{ $c | Remove-Item -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath $u.FullName -Force -ErrorAction SilentlyContinue } }; " +
              "$left = @(Get-ChildItem -LiteralPath $f -Force -ErrorAction SilentlyContinue); " +
              "if ($left.Count -eq 0) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } " +
              // Only other files are left: the folder stays, and there is nothing more to wait for. The start helper
              // (RunnerManagerLauncher.exe) and what an exe unpacked (.net\RunnerManager) can still be in use for a moment.
              "elseif (-not ($left | Where-Object { $_.Name -like 'RunnerManager*' }) -and -not ($ai -and (Test-Path -LiteralPath $a))) { break }";
        return $"$f = $env:{FolderVariable}; if ($f) {{ for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $f); $i++) " +
               $"{{ {remove}; if (Test-Path -LiteralPath $f) {{ Start-Sleep -Seconds 2 }} }} }}";
    }

    /// <summary>
    /// Runner Manager's own files in an install folder, as file name patterns: the exe and the files beside it named after
    /// it, the start helper and its files, and the files that swaps renamed out of the way.
    /// </summary>
    internal static readonly string[] OwnFilePatterns =
    {
        "RunnerManager.exe", "RunnerManager.exe.new", "RunnerManager.exe.new.partial", "RunnerManager.exe.previous",
        "RunnerManager.exe.swapping", "RunnerManager.exe.pending", "RunnerManager.*.old.exe",
        Launcher.FileName, Launcher.FileName + ".new", Launcher.FileName + ".new.partial", Launcher.FileName + ".off",
        "RunnerManagerLauncher.*.old.exe"
    };

    /// <summary>Creates or updates a shortcut to <paramref name="target"/>. The paths reach PowerShell as environment variables (see <see cref="FolderEnvironment"/>).</summary>
    internal static async Task CreateShortcutAsync(string shortcut, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var start = new ProcessStartInfo(AppUpdater.PowerShellExe)
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ShortcutScript },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["RUNNER_MANAGER_SHORTCUT"] = shortcut;
        start.Environment["RUNNER_MANAGER_TARGET"] = target;
        start.Environment[FolderVariable] = Path.GetDirectoryName(target)!;
        using var process = Process.Start(start);
        if (process != null) await process.WaitForExitAsync();
    }

    internal const string ShortcutScript =
        "$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:RUNNER_MANAGER_SHORTCUT); $s.TargetPath = $env:RUNNER_MANAGER_TARGET; " +
        "$s.WorkingDirectory = $env:RUNNER_MANAGER_FOLDER; $s.Description = 'Runner Manager'; $s.Save()";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

/// <summary>
/// Remembers which install checks already ran for this version (in install-checked.txt), so the dashboard does not
/// query scheduled tasks on every start. A new version runs each check once more.
/// </summary>
internal static class InstallChecks
{
    private static readonly string MarkerPath = Path.Combine(FleetConfig.StateDir, "install-checked.txt");
    private static readonly object Gate = new();

    public static bool IsDone(string check)
    {
        lock (Gate) return Read().TryGetValue(check, out var version) && version == AppUpdater.CurrentVersion;
    }

    public static void MarkDone(string check)
    {
        lock (Gate)
        {
            var entries = Read();
            entries[check] = AppUpdater.CurrentVersion;
            Write(entries);
        }
    }

    /// <summary>Forgets that a check ran, so it runs again (for tests).</summary>
    internal static void Forget(string check)
    {
        lock (Gate)
        {
            var entries = Read();
            if (entries.Remove(check)) Write(entries);
        }
    }

    private static void Write(Dictionary<string, string> entries)
    {
        try
        {
            Directory.CreateDirectory(FleetConfig.StateDir);
            // Written to a temporary file first, so an interrupted write never leaves an empty file behind.
            var temp = $"{MarkerPath}.{Environment.ProcessId}.tmp";
            File.WriteAllLines(temp, entries.Select(e => $"{e.Key}={e.Value}"));
            File.Move(temp, MarkerPath, overwrite: true);
        }
        catch { }
    }

    private static Dictionary<string, string> Read()
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(MarkerPath)) return entries;
            foreach (var line in File.ReadAllLines(MarkerPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2) entries[parts[0].Trim()] = parts[1].Trim();
            }
        }
        catch { }
        return entries;
    }
}
