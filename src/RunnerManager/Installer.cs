using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace RunnerManager;

public enum FleetChoice { SinglePc, NewFleet, JoinFleet }

/// <summary>The choices made in setup. <see cref="StartWithWindows"/> is null when setup leaves the startup task as it is.</summary>
public sealed record SetupChoices(bool DesktopShortcut, bool? StartWithWindows, FleetChoice Fleet);

/// <summary>
/// Per-user install of Runner Manager: copies the app to %LOCALAPPDATA%\Programs\RunnerManager, adds shortcuts and an
/// entry in Settings → Apps (with an uninstaller), and records the choices made in the setup wizard.
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
        // The copy that is installed now, when setup moves it to another folder.
        var previous = RegisteredInstallDir()
                       ?? (File.Exists(Path.Combine(AppUpdater.DefaultInstallDir, AppPaths.ExeName)) ? AppUpdater.DefaultInstallDir : null);
        var moving = previous != null && !AppUpdater.SameFolder(previous, AppUpdater.InstallDir);
        // An AI tool's host keeps running the replaced exe and needs nothing from setup; only a move takes its exe away.
        CloseRunningDashboards(log, moving ? new[] { AppUpdater.InstallDir, previous! } : new[] { AppUpdater.InstallDir }, endAssistants: moving);
        var replacing = File.Exists(AppUpdater.InstalledExe) && !AppUpdater.IsRunningInstalledCopy;
        // Setup warns on its first page when it is older than the installed copy, so going ahead is the user's choice.
        var exe = await AppUpdater.InstallCurrentBuildAsync(log, createShortcut: false, allowOlder: true);
        try
        {
            await FinishInstallAsync(choices, exe, moving ? previous : null, log);
        }
        catch (Exception ex)
        {
            // A half-done setup is undone, so the installed copy keeps working as before.
            log($"Setup failed: {ex.Message} Undoing it...");
            await UndoInstallAsync(replacing, moving ? previous : null, log);
            throw;
        }
        return exe;
    }

    private static async Task FinishInstallAsync(SetupChoices choices, string exe, string? previous, Action<string> log)
    {
        var moving = previous != null;
        log("Adding shortcuts...");
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (choices.DesktopShortcut) await CreateShortcutAsync(DesktopShortcut, exe);
        else TryDelete(DesktopShortcut);

        log("Registering Runner Manager in Settings → Apps...");
        RegisterUninstallEntry(exe);

        await ScheduledTasks.RegisterLaunchTaskAsync(exe);
        await MigrateSharedTasksAsync(exe);
        if (choices.StartWithWindows == true) await RegisterStartupTaskAsync(exe);
        else if (choices.StartWithWindows == false) await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);
        // Unknown (the task could not be read): kept as it is, but pointed at this copy when it exists.
        else if (!await UpdateStartupTaskAsync(exe)) log("The startup task could not be updated now. Runner Manager updates it on a later start.");
        await LegacyInstall.MigrateAsync(log, fromSetup: true);

        // An agent from an earlier install keeps running from the old place; point it at the new copy. Another Windows
        // user's agent is left alone.
        if (await MachineRelay.IsAgentInstalledAsync())
        {
            if (await AgentOwnership.OtherUserAsync() is { } other)
                log($"The fleet agent on this PC runs for {other}, so it was left alone.");
            else
            {
                log("Updating the fleet agent...");
                await MachineRelay.UpdateLocalAgentAsync(log);
            }
        }

        if (moving)
        {
            // The old copy may still be in use until this process exits (its agent was just switched over).
            log($"Removing the copy in {previous}...");
            AppUpdater.RunAfterExit(UnlessInstalledIn(previous!, RemoveProgramFilesScript(previous!)), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3));
        }

        var settings = AppSettings.Load();
        settings.SetupCompleted = true;
        if (choices.StartWithWindows is { } startsWithWindows) settings.StartMinimizedHint = startsWithWindows;
        settings.Save();
        InstallChecks.MarkDone(LaunchTaskCheck);
        log("Installed.");
    }

    /// <summary>
    /// Puts back the exe that setup replaced (kept as RunnerManager.exe.previous). When setup was moving the install,
    /// the shortcuts, Settings → Apps entry and launch task point at the old folder again, and the new copy is removed.
    /// </summary>
    private static async Task UndoInstallAsync(bool replaced, string? previousFolder, Action<string> log)
    {
        try
        {
            if (replaced) AppUpdater.RestorePrevious(new SwapMarker(SwapKind.Undo, AppUpdater.CurrentVersion, string.Empty));
        }
        catch (Exception ex) { log($"Could not put the previous version back: {ex.Message}"); }
        if (previousFolder == null) return;
        var newFolder = AppUpdater.InstallDir;
        var oldExe = Path.Combine(previousFolder, AppPaths.ExeName);
        try
        {
            AppUpdater.UseInstallDir(previousFolder);
            await RefreshShortcutsAsync(oldExe);
            await ScheduledTasks.RegisterLaunchTaskAsync(oldExe);
            // Kept when setup is tried again and installs there after all.
            AppUpdater.RunAfterExit(UnlessInstalledIn(newFolder, RemoveProgramFilesScript(newFolder)), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { log($"Could not point Runner Manager back to {previousFolder}: {ex.Message}"); }
    }

    /// <summary>
    /// PowerShell that runs <paramref name="script"/> unless the Settings → Apps entry points at <paramref name="folder"/>
    /// by then: a setup that is tried again in the same window may have installed there after all.
    /// </summary>
    internal static string UnlessInstalledIn(string folder, string script)
    {
        var path = Path.TrimEndingDirectorySeparator(folder).Replace("'", "''");
        return $"$at = (Get-ItemProperty -LiteralPath 'HKCU:\\{UninstallKey}' -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation; " +
               $"if (-not $at -or $at.TrimEnd([char]92) -ne '{path}') {{ {script} }}";
    }

    internal static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <summary>
    /// Whether Runner Manager starts with Windows for this user: by its own startup task, or by one of an older version
    /// that setup moves over (the shared task of 2.3 and earlier when it is this user's, or RunnerWidget's). Null when a
    /// task could not be read, so setup leaves the startup task as it is.
    /// </summary>
    internal static async Task<bool?> StartsWithWindowsAsync() => AnyStartupTask(
        await ScheduledTasks.ExistsOrUnknownAsync(ScheduledTasks.DashboardTaskName),
        await IsOwnTaskOrUnknownAsync(ScheduledTasks.SharedDashboardTaskName),
        await ScheduledTasks.ExistsOrUnknownAsync(ScheduledTasks.LegacyDashboardTaskName));

    /// <summary>True when any startup task was found, otherwise null when one could not be read, otherwise false.</summary>
    internal static bool? AnyStartupTask(params bool?[] found)
        => found.Any(f => f == true) ? true : found.Any(f => f == null) ? null : false;

    public static Task RegisterStartupTaskAsync(string exe) => ScheduledTasks.RegisterLogonTaskAsync(
        ScheduledTasks.DashboardTaskName, exe, "--minimized",
        "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);

    private const string LaunchTaskCheck = "launch-task";

    /// <summary>
    /// The installed dashboard opens through the launch task, so make sure this user's task exists, is turned on and starts
    /// the installed exe, and keep the version in Settings → Apps current after updates. A launch or "start with Windows"
    /// task that an older version registered below normal priority is registered again. The tasks are checked once per
    /// version, or again when <paramref name="force"/> is set (the dashboard had to ask with UAC). A task that runs now (the
    /// dashboard itself may have been started by it) is left for a later start, by the dashboard or by the fleet agent.
    /// </summary>
    public static async Task EnsureLaunchTaskAsync(bool force = false)
    {
        if (!AppUpdater.IsRunningInstalledCopy) return;
        RefreshUninstallEntryVersion();
        if (!force && InstallChecks.IsDone(LaunchTaskCheck)) return;
        var exe = AppUpdater.InstalledExe;
        var done = true;
        if (LaunchTaskNeedsRegistering(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.LaunchTaskName), exe))
            done &= await RegisterIfIdleAsync(ScheduledTasks.LaunchTaskName, () => ScheduledTasks.RegisterLaunchTaskAsync(exe));
        done &= await UpdateStartupTaskAsync(exe);
        done &= await MigrateSharedTasksAsync(exe);
        if (done) InstallChecks.MarkDone(LaunchTaskCheck);
    }

    /// <summary>Registers this user's "start with Windows" task again when it needs it (see <see cref="StartupTaskNeedsRegistering"/>). False when that waits.</summary>
    internal static async Task<bool> UpdateStartupTaskAsync(string exe)
        => !StartupTaskNeedsRegistering(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.DashboardTaskName), exe)
           || await RegisterIfIdleAsync(ScheduledTasks.DashboardTaskName, () => RegisterStartupTaskAsync(exe));

    /// <summary>
    /// Changes a task (<paramref name="change"/> registers it again or deletes it) only while no instance of it runs:
    /// that can end the running instance, and the dashboard that asks may be one. Returns false when it was left for later.
    /// </summary>
    internal static async Task<bool> RegisterIfIdleAsync(string name, Func<Task> change)
    {
        if (await ScheduledTasks.IsRunningAsync(name) != false) return false;
        await change();
        return true;
    }

    /// <summary>Whether the launch task must be registered: it is missing, cannot start <paramref name="exe"/>, or runs below normal priority.</summary>
    internal static bool LaunchTaskNeedsRegistering(string xml, string exe)
        => xml.Length == 0 || !Elevation.IsUsableLaunchTask(xml, exe) || !ScheduledTasks.HasCurrentPriority(xml);

    /// <summary>
    /// Whether this user's "start with Windows" task must be registered again: it is turned on but starts another exe or
    /// runs below normal priority. A missing or turned-off task is left as the user chose.
    /// </summary>
    internal static bool StartupTaskNeedsRegistering(string xml, string exe)
        => xml.Length > 0 && ScheduledTasks.IsEnabled(xml) && ScheduledTasks.IsCurrentUser(ScheduledTasks.PrincipalOf(xml))
           && (!ScheduledTasks.HasCurrentPriority(xml) || !string.Equals(ScheduledTasks.CommandOf(xml), exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 2.3 and earlier named the launch and startup tasks the same for every Windows user, so a second user's setup took
    /// them over. When they are this user's, they move to this user's own names; another user's are left alone. A shared
    /// task that runs now is moved on a later start. Returns false when something was left for later.
    /// </summary>
    private static async Task<bool> MigrateSharedTasksAsync(string exe)
    {
        var done = true;
        if (await IsOwnTaskAsync(ScheduledTasks.SharedDashboardTaskName))
            done &= await RegisterIfIdleAsync(ScheduledTasks.SharedDashboardTaskName, async () =>
            {
                await RegisterStartupTaskAsync(exe);
                await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.SharedDashboardTaskName);
            });
        if (await IsOwnTaskAsync(ScheduledTasks.SharedLaunchTaskName))
            done &= await RegisterIfIdleAsync(ScheduledTasks.SharedLaunchTaskName,
                () => ScheduledTasks.DeleteTaskAsync(ScheduledTasks.SharedLaunchTaskName));
        return done;
    }

    private static async Task<bool> IsOwnTaskAsync(string name) => await IsOwnTaskOrUnknownAsync(name) == true;

    /// <summary>Whether the task exists and is this user's; null when it could not be read.</summary>
    private static async Task<bool?> IsOwnTaskOrUnknownAsync(string name)
    {
        var xml = await ScheduledTasks.QueryXmlAsync(name);
        if (xml.Length == 0) return ScheduledTasks.IsRegistered(name) == false ? false : null;
        var principal = ScheduledTasks.PrincipalOf(xml);
        return principal.Length > 0 && ScheduledTasks.IsCurrentUser(principal);
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
    /// Closes every dashboard that runs from the install folder, including one hidden in the notification area, so it
    /// can be replaced or removed and its single-instance lock is freed. AI tools' hosts (--mcp, ai) are ended too when
    /// <paramref name="endAssistants"/> is set. The fleet agent (--agent) is handled separately, and setup, uninstall and
    /// agent installs are left to finish.
    /// </summary>
    private static void CloseRunningDashboards(Action<string> log, IEnumerable<string> folders, bool endAssistants)
    {
        var installDirs = folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar).ToList();
        var dashboards = new List<Process>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    var id = Convert.ToInt32(item["ProcessId"]);
                    var path = item["ExecutablePath"]?.ToString() ?? string.Empty;
                    if (id == Environment.ProcessId || !IsClosedBySetup(item["CommandLine"]?.ToString(), endAssistants, path)) continue;
                    if (!installDirs.Any(dir => path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))) continue;
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

    /// <summary>Whether setup or uninstall ends a Runner Manager process with this command line before it replaces the exe.</summary>
    internal static bool IsClosedBySetup(string? commandLine, bool endAssistants, string? programPath = null) => RunningCopies.RoleOf(commandLine, programPath) switch
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
    /// Removes Runner Manager from this PC: leaves the fleet (the other PCs forget it), removes the agent, the start-up
    /// task, shortcuts and the Apps entry, then deletes the program folder (and optionally settings and logs) after exit.
    /// GitHub runners are not touched. Returns a sentence to add to the result, or empty.
    /// </summary>
    public static async Task<string> UninstallAsync(bool removeData, Action<string> log)
    {
        // Read before the Settings → Apps entry that records a chosen folder is deleted below.
        var installDir = AppUpdater.InstallDir;
        CloseRunningDashboards(log, new[] { installDir }, endAssistants: true);
        // Another Windows user's agent stays: it is their fleet membership, not this user's.
        if (await AgentOwnership.OtherUserAsync() is { } other)
            log($"The fleet agent on this PC runs for {other}, so it was left alone.");
        else if (FleetConfig.Current.FleetKey != null || await MachineRelay.IsAgentInstalledAsync())
            await MachineRelay.LeaveFleetAsync(log);
        log("Removing the scheduled tasks and shortcuts...");
        foreach (var task in new[] { ScheduledTasks.DashboardTaskName, ScheduledTasks.LaunchTaskName, ScheduledTasks.LegacyDashboardTaskName })
            await ScheduledTasks.DeleteTaskAsync(task);
        foreach (var task in new[] { ScheduledTasks.SharedDashboardTaskName, ScheduledTasks.SharedLaunchTaskName })
            if (await IsOwnTaskAsync(task)) await ScheduledTasks.DeleteTaskAsync(task);
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        // The running exe can't delete its own folder; a helper does it once this process has exited.
        var script = RemoveProgramFilesScript(installDir);
        var note = string.Empty;
        if (removeData)
        {
            // Only together with the settings: a later install would otherwise move into the registry what fleet.json holds.
            try { AssistantSwitches.Remove(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                log($"The AI assistant settings could not be removed: {ex.Message}");
            }
            // Startup scripts that converting a runner to a service turned off exist nowhere else, so they are never deleted.
            var scripts = KeepDisabledStartupScripts(log);
            var data = FleetConfig.StateDir.Replace("'", "''");
            script += scripts.Moved || scripts.Folder == null
                ? $"; Remove-Item -LiteralPath '{data}' -Recurse -Force -ErrorAction SilentlyContinue"
                : $"; Get-ChildItem -LiteralPath '{data}' -Force -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -ne '{DisabledStartupFolderName}' }} | " +
                  "Remove-Item -Recurse -Force -ErrorAction SilentlyContinue";
            if (scripts.Folder != null)
                note = $" Startup scripts that Runner Manager had turned off are {(scripts.Moved ? "now" : "kept")} in {scripts.Folder}.";
        }
        // No time limit: the "uninstalled" message keeps this process (and so its exe) until it is closed.
        AppUpdater.RunAfterExit(script, timeout: null, TimeSpan.FromSeconds(1));
        log("Runner Manager was removed.");
        return note;
    }

    private const string DisabledStartupFolderName = "disabled-startup";

    /// <summary>
    /// Moves the startup scripts that Runner Manager turned off (when a console runner became a service) from its data
    /// folder to Documents\Runner Manager startup scripts. Returns where they are, and whether they were moved; the folder
    /// is null when there are none.
    /// </summary>
    internal static (string? Folder, bool Moved) KeepDisabledStartupScripts(Action<string> log, string? documents = null)
    {
        var source = Path.Combine(FleetConfig.StateDir, DisabledStartupFolderName);
        try
        {
            if (!Directory.Exists(source) || !Directory.EnumerateFileSystemEntries(source).Any()) return (null, false);
        }
        catch { return (null, false); }
        try
        {
            documents ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var target = Path.Combine(documents, "Runner Manager startup scripts");
            for (var i = 2; Directory.Exists(target) || File.Exists(target); i++)
                target = Path.Combine(documents, $"Runner Manager startup scripts ({i})");
            Directory.CreateDirectory(target);
            // Copied, not moved: Documents may be on another drive.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
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
    /// PowerShell that removes Runner Manager from a program folder. The default folder only holds this app and is removed
    /// completely; a folder chosen in setup loses only Runner Manager's own files, and is removed when it is then empty.
    /// </summary>
    internal static string RemoveProgramFilesScript(string folder)
    {
        var path = folder.Replace("'", "''");
        // Retried for about 20 seconds: a process that just exited, or one using the folder as its working folder, can
        // still hold it for a moment.
        var remove = AppUpdater.IsDefaultInstallDir(folder)
            ? "Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction SilentlyContinue"
            : "foreach ($p in 'RunnerManager.exe', 'RunnerManager.exe.new', 'RunnerManager.exe.new.partial', 'RunnerManager.exe.previous', 'RunnerManager.exe.swapping', 'RunnerManager.*.old.exe') " +
              "{ Get-ChildItem -LiteralPath $f -Filter $p -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }; " +
              "$left = @(Get-ChildItem -LiteralPath $f -Force -ErrorAction SilentlyContinue); " +
              "if ($left.Count -eq 0) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue } " +
              // Only other files are left: the folder stays, and there is nothing more to wait for.
              "elseif (-not ($left | Where-Object { $_.Name -like 'RunnerManager*' })) { break }";
        return $"$f = '{path}'; for ($i = 0; $i -lt 10 -and (Test-Path -LiteralPath $f); $i++) " +
               $"{{ {remove}; if (Test-Path -LiteralPath $f) {{ Start-Sleep -Seconds 2 }} }}";
    }

    /// <summary>Creates or updates a shortcut to <paramref name="target"/>.</summary>
    internal static async Task CreateShortcutAsync(string shortcut, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{shortcut.Replace("'", "''")}');" +
                     $"$s.TargetPath='{target.Replace("'", "''")}';$s.WorkingDirectory='{Path.GetDirectoryName(target)!.Replace("'", "''")}';" +
                     "$s.Description='Runner Manager';$s.Save()";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process != null) await process.WaitForExitAsync();
    }

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
            try
            {
                var entries = Read();
                entries[check] = AppUpdater.CurrentVersion;
                Directory.CreateDirectory(FleetConfig.StateDir);
                // Written to a temporary file first, so an interrupted write never leaves an empty file behind.
                var temp = $"{MarkerPath}.{Environment.ProcessId}.tmp";
                File.WriteAllLines(temp, entries.Select(e => $"{e.Key}={e.Value}"));
                File.Move(temp, MarkerPath, overwrite: true);
            }
            catch { }
        }
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
