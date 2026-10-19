using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace RunnerManager;

public enum FleetChoice { SinglePc, NewFleet, JoinFleet }
public enum RdcChoice { NotUsed, AlreadySetUp, SetUpNow }

public sealed record SetupChoices(bool DesktopShortcut, bool StartWithWindows, FleetChoice Fleet, RdcChoice Rdc);

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

    public static bool IsInstalled
    {
        get
        {
            if (!File.Exists(AppUpdater.InstalledExe)) return false;
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key != null;
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
        CloseRunningDashboards(log, moving ? new[] { AppUpdater.InstallDir, previous! } : new[] { AppUpdater.InstallDir });
        var exe = await AppUpdater.InstallCurrentBuildAsync(log, createShortcut: false);

        log("Adding shortcuts...");
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (choices.DesktopShortcut) await CreateShortcutAsync(DesktopShortcut, exe);
        else TryDelete(DesktopShortcut);

        log("Registering Runner Manager in Settings → Apps...");
        RegisterUninstallEntry(exe);

        if (choices.StartWithWindows) await RegisterStartupTaskAsync(exe);
        else await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);
        await ScheduledTasks.RegisterLaunchTaskAsync(exe);
        await LegacyInstall.MigrateAsync(log, fromSetup: true);

        // An agent from an earlier install keeps running from the old place; point it at the new copy.
        if (await MachineRelay.IsAgentInstalledAsync())
        {
            log("Updating the fleet agent...");
            await MachineRelay.UpdateLocalAgentAsync(log);
        }

        if (moving)
        {
            // The old copy may still be in use until this process exits (its agent was just switched over).
            log($"Removing the copy in {previous}...");
            AppUpdater.RunAfterExit(RemoveProgramFilesScript(previous!), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3));
        }

        var settings = AppSettings.Load();
        settings.SetupCompleted = true;
        settings.StartMinimizedHint = choices.StartWithWindows;
        settings.Save();
        InstallChecks.MarkDone(LaunchTaskCheck);
        log("Installed.");
        return exe;
    }

    public static Task RegisterStartupTaskAsync(string exe) => ScheduledTasks.RegisterLogonTaskAsync(
        ScheduledTasks.DashboardTaskName, exe, "--minimized",
        "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);

    private const string LaunchTaskCheck = "launch-task";

    /// <summary>
    /// The installed dashboard opens through the launch task, so make sure it exists (older installs lack it), and keep
    /// the version in Settings → Apps current after updates. The task is checked once per version.
    /// </summary>
    public static async Task EnsureLaunchTaskAsync()
    {
        if (!AppUpdater.IsRunningInstalledCopy) return;
        RefreshUninstallEntryVersion();
        if (InstallChecks.IsDone(LaunchTaskCheck)) return;
        var command = ScheduledTasks.CommandOf(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.LaunchTaskName));
        if (!string.Equals(command, AppUpdater.InstalledExe, StringComparison.OrdinalIgnoreCase))
            await ScheduledTasks.RegisterLaunchTaskAsync(AppUpdater.InstalledExe);
        InstallChecks.MarkDone(LaunchTaskCheck);
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
    /// can be replaced or removed and its single-instance lock is freed. The fleet agent (--agent) is handled separately.
    /// </summary>
    private static void CloseRunningDashboards(Action<string> log) => CloseRunningDashboards(log, new[] { AppUpdater.InstallDir });

    private static void CloseRunningDashboards(Action<string> log, IEnumerable<string> folders)
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
                    var commandLine = item["CommandLine"]?.ToString() ?? string.Empty;
                    if (id == Environment.ProcessId || IsAgentCommandLine(commandLine)) continue;
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
        foreach (var process in dashboards)
        {
            try { process.Kill(); } catch { }
        }
        foreach (var process in dashboards)
        {
            try { process.WaitForExit(5000); } catch { }
            process.Dispose();
        }
    }

    private static bool IsAgentCommandLine(string commandLine)
        => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Trim('"').Equals("--agent", StringComparison.OrdinalIgnoreCase));

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
    /// GitHub runners and Remote Desktop Commander are not touched.
    /// </summary>
    public static async Task UninstallAsync(bool removeData, Action<string> log)
    {
        // Read before the Settings → Apps entry that records a chosen folder is deleted below.
        var installDir = AppUpdater.InstallDir;
        CloseRunningDashboards(log);
        if (FleetConfig.Current.FleetKey != null || await MachineRelay.IsAgentInstalledAsync())
            await MachineRelay.LeaveFleetAsync(log);
        log("Removing the scheduled tasks and shortcuts...");
        foreach (var task in new[] { ScheduledTasks.DashboardTaskName, ScheduledTasks.LaunchTaskName, ScheduledTasks.LegacyDashboardTaskName })
            await ScheduledTasks.DeleteTaskAsync(task);
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        // The running exe can't delete its own folder; a helper does it once this process has exited.
        var script = RemoveProgramFilesScript(installDir);
        if (removeData) script += $"; Remove-Item -LiteralPath '{FleetConfig.StateDir.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue";
        AppUpdater.RunAfterExit(script, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1));
        log("Runner Manager was removed.");
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
            : "foreach ($p in 'RunnerManager.exe', 'RunnerManager.exe.new', 'RunnerManager.*.old.exe') " +
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
                File.WriteAllLines(MarkerPath, entries.Select(e => $"{e.Key}={e.Value}"));
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
