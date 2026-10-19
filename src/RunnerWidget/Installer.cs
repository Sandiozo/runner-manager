using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RunnerWidget;

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
    private static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Runner Manager.lnk");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Runner Manager.lnk");

    /// <summary>Run the setup wizard when started as RunnerManagerSetup.exe or with --setup.</summary>
    public static bool IsSetupLaunch(string[] args)
        => args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase))
           || Path.GetFileName(Environment.ProcessPath ?? string.Empty).StartsWith("RunnerManagerSetup", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(AppUpdater.InstalledExe) && Registry.CurrentUser.OpenSubKey(UninstallKey) != null;

    public static async Task<string> InstallAsync(SetupChoices choices, Action<string> log)
    {
        CloseRunningDashboards(log);
        var exe = await AppUpdater.InstallCurrentBuildAsync(log);

        log("Adding shortcuts...");
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (choices.DesktopShortcut) await CreateShortcutAsync(DesktopShortcut, exe);
        else TryDelete(DesktopShortcut);

        log("Registering Runner Manager in Settings → Apps...");
        RegisterUninstallEntry(exe);

        if (choices.StartWithWindows)
            await ScheduledTasks.RegisterLogonTaskAsync(ScheduledTasks.DashboardTaskName, exe, "--minimized",
                "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);
        else
            await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);

        // An agent from an earlier install keeps running from the old place; point it at the new copy.
        if (await MachineRelay.IsAgentInstalledAsync())
        {
            log("Updating the fleet agent...");
            await MachineRelay.UpdateLocalAgentAsync(log);
        }

        var settings = AppSettings.Load();
        settings.SetupCompleted = true;
        settings.StartMinimizedHint = choices.StartWithWindows;
        settings.Save();
        log("Installed.");
        return exe;
    }

    /// <summary>Closes an already installed dashboard so it can be replaced (the agent is updated separately).</summary>
    private static void CloseRunningDashboards(Action<string> log)
    {
        foreach (var process in Process.GetProcessesByName("RunnerWidget"))
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                var path = process.MainModule?.FileName ?? string.Empty;
                if (!path.StartsWith(AppUpdater.InstallDir, StringComparison.OrdinalIgnoreCase) || process.MainWindowHandle == IntPtr.Zero) continue;
                log("Closing Runner Manager...");
                process.Kill();
                process.WaitForExit(5000);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

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
        if (FleetConfig.Current.FleetKey != null || await MachineRelay.IsAgentInstalledAsync())
            await MachineRelay.LeaveFleetAsync(log);
        log("Removing the startup task and shortcuts...");
        await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }

        // The running exe can't delete its own folder; a helper does it once this process has exited.
        var folders = new List<string> { AppUpdater.InstallDir };
        if (removeData) folders.Add(FleetConfig.StateDir);
        var script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; " +
                     string.Join("; ", folders.Select(f => $"Remove-Item -LiteralPath '{f.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue"));
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        });
        log("Runner Manager was removed.");
    }

    private static async Task CreateShortcutAsync(string shortcut, string target)
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
