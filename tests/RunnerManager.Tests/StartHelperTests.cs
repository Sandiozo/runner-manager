using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace RunnerManager.Tests;

/// <summary>
/// An install folder in %TEMP% for the start helper's tests: RunnerManager.exe (an empty file) and, unless left out,
/// the start helper (RunnerManagerLauncher.exe, a few bytes) beside it. Deleted again when disposed.
/// </summary>
internal sealed class HelperFolder : IDisposable
{
    public static readonly byte[] Bytes = "the start helper"u8.ToArray();

    public HelperFolder(bool helper = true, string name = "start-helper")
    {
        Path = InstallEvidenceTests.NewFolder(name);
        Exe = System.IO.Path.Combine(Path, AppPaths.ExeName);
        Helper = Launcher.PathIn(Path);
        File.WriteAllText(Exe, string.Empty);
        if (helper) File.WriteAllBytes(Helper, Bytes);
    }

    public string Path { get; }
    public string Exe { get; }
    public string Helper { get; }

    public void RemoveHelper() => File.Delete(Helper);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The start helper's path and the mapping both ways: a task that runs the start helper in a folder starts that folder's
/// RunnerManager.exe, and the command for an installed exe is the start helper beside it while it is in place.
/// </summary>
public class StartHelperMappingTests
{
    [Fact]
    public void ATaskThatRunsTheStartHelper_StartsTheExeBesideIt()
    {
        Assert.Equal(@"C:\Program Files\RunnerManager\RunnerManager.exe", Launcher.ExeOf(@"C:\Program Files\RunnerManager\RunnerManagerLauncher.exe"));
        Assert.Equal(@"D:\Apps\RunnerManager\RunnerManager.exe", Launcher.ExeOf(@"D:\Apps\RunnerManager\runnermanagerlauncher.EXE"));
        // An exe is itself, and a name without its folder is no start helper.
        Assert.Equal(@"D:\Apps\RunnerManager\RunnerManager.exe", Launcher.ExeOf(@"D:\Apps\RunnerManager\RunnerManager.exe"));
        Assert.Equal(Launcher.FileName, Launcher.ExeOf(Launcher.FileName));
        Assert.True(Launcher.IsLauncherPath(@"C:\X\RunnerManagerLauncher.exe"));
        Assert.False(Launcher.IsLauncherPath(@"C:\X\RunnerManager.exe"));
        Assert.False(Launcher.IsLauncherPath(@"C:\X\RunnerManagerLauncher.exe.old"));
        Assert.Equal(@"C:\X\RunnerManagerLauncher.exe", Launcher.PathBeside(@"C:\X\RunnerManager.exe"));
    }

    [Fact]
    public void TheCommandForAnExe_IsTheStartHelperBesideIt_OnlyWhileItIsInPlace()
    {
        using var folder = new HelperFolder(helper: false);
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));

        File.WriteAllBytes(folder.Helper, HelperFolder.Bytes);
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe));
        Assert.Equal(folder.Exe, Launcher.ExeOf(Launcher.CommandFor(folder.Exe)));

        // A PC that runs without it.
        File.WriteAllText(Launcher.OffMarkerPath(folder.Path), AppUpdater.CurrentVersion);
        Assert.True(Launcher.RunsWithout(folder.Path));
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));
        File.Delete(Launcher.OffMarkerPath(folder.Path));

        // Only for RunnerManager.exe by its full path.
        var legacy = Path.Combine(folder.Path, AppPaths.LegacyExeName);
        Assert.Equal(legacy, Launcher.CommandFor(legacy));
        Assert.Equal(AppPaths.ExeName, Launcher.CommandFor(AppPaths.ExeName));

        // A folder in its place is not in place, and cannot start.
        folder.RemoveHelper();
        Directory.CreateDirectory(folder.Helper);
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));
        Assert.False(Launcher.CanStart(folder.Helper));
    }

    [Fact]
    public void AStartHelperCommand_CanStartOnlyWhileItsFileIsThere()
    {
        using var folder = new HelperFolder();
        Assert.True(Launcher.CanStart(folder.Helper));
        // An exe is taken as it is, as before.
        Assert.True(Launcher.CanStart(@"C:\Nowhere\RunnerManager.exe"));
        folder.RemoveHelper();
        Assert.False(Launcher.CanStart(folder.Helper));
    }

    [Fact]
    public void TheAppStartsItselfThroughTheStartHelper_InTheInstallFolder_WithTheSameArguments()
    {
        using var folder = new HelperFolder();
        var start = Launcher.StartInfo(folder.Exe, new[] { "--after=12", "a b" }, elevate: true);
        Assert.Equal(folder.Helper, start.FileName);
        Assert.Equal(folder.Path, start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(new[] { "--after=12", "a b" }, start.ArgumentList);

        folder.RemoveHelper();
        var direct = Launcher.StartInfo(folder.Exe, Array.Empty<string>());
        Assert.Equal(folder.Exe, direct.FileName);
        Assert.Equal(folder.Path, direct.WorkingDirectory);
        Assert.Equal(string.Empty, direct.Verb);
    }

    [Fact]
    public void ATaskForTheStartHelper_RunsItQuoted_InTheInstallFolder()
    {
        using var folder = new HelperFolder();
        var xml = ScheduledTasks.TaskXml(WindowsIdentity.GetCurrent().Name, folder.Helper, "--agent", "Test task", alwaysOn: true, atLogon: true, parallel: false);
        Assert.Contains($"<Command>\"{folder.Helper}\"</Command>", xml);
        Assert.Contains("<Arguments>--agent</Arguments>", xml);
        Assert.Contains($"<WorkingDirectory>{folder.Path}</WorkingDirectory>", xml);
    }

    [Fact]
    public void SetupsCleanUp_KeepsTheStartHelperAndWhatTheExeUnpacked_AndOldCopiesOfItAreOwnFiles()
    {
        Assert.True(AppUpdater.IsKeptBySingleFileInstall(Launcher.ExtractionFolderName, isFolder: true));
        Assert.True(AppUpdater.IsKeptBySingleFileInstall(Launcher.FileName, isFolder: false));
        Assert.True(AppUpdater.IsKeptBySingleFileInstall(Launcher.FileName + ".off", isFolder: false));
        Assert.False(AppUpdater.IsKeptBySingleFileInstall("other.dll", isFolder: false));
        Assert.False(AppUpdater.IsKeptBySingleFileInstall("other", isFolder: true));
        Assert.True(AppUpdater.IsOldFile("RunnerManagerLauncher.638000000000000000.old.exe", anyAppFile: false));
        Assert.False(AppUpdater.IsOldFile("OtherLauncher.638000000000000000.old.exe", anyAppFile: false));
    }
}

/// <summary>
/// Every decision that reads a task's command, for a task at the exe (with and without the start helper beside it), at
/// the start helper, and at a start helper that is gone. Tasks and records in memory (see <see cref="TestInstallState"/>).
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperTaskTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private static readonly string Launch = ScheduledTasks.LaunchTaskName;
    private static readonly string Startup = ScheduledTasks.DashboardTaskName;
    private static readonly string Agent = ScheduledTasks.AgentTaskName;

    /// <summary>Where a task points, and whether the start helper is there.</summary>
    public enum At
    {
        /// <summary>At the exe, with the start helper beside it (the tasks of 2.9.0 after the update).</summary>
        ExeWithHelper,
        /// <summary>At the exe, without a start helper (2.9.0, or a PC that runs without it).</summary>
        ExeWithoutHelper,
        /// <summary>At the start helper, which is there.</summary>
        Helper,
        /// <summary>At the start helper, which is gone (antivirus software removed it).</summary>
        MissingHelper
    }

    private static HelperFolder Folder(At at) => new(helper: at is At.ExeWithHelper or At.Helper);

    private static string CommandIn(HelperFolder folder, At at) => at is At.Helper or At.MissingHelper ? folder.Helper : folder.Exe;

    private static string TaskOf(string user, string command, bool highest = true) => InstallEvidenceTests.TaskOf(user, command, highest);

    private static string CommandOf(TestInstallState state, string name) => ScheduledTasks.CommandOfOwnElevatedTask(state.Tasks.Definitions[name]!)!;

    private static string Disabled(string xml) => Regex.Replace(xml, @"<Enabled>true</Enabled>(\s*)<Hidden>", "<Enabled>false</Enabled>$1<Hidden>");

    [Theory]
    [InlineData(At.ExeWithHelper, true, true)]
    [InlineData(At.ExeWithoutHelper, true, false)]
    [InlineData(At.Helper, true, false)]
    [InlineData(At.MissingHelper, false, true)]
    public void TheLaunchAndStartupTasks_TakeTheStartHelperForTheExeBesideIt(At at, bool usable, bool registerAgain)
    {
        using var folder = Folder(at);
        var xml = TaskOf(Me, CommandIn(folder, at));
        Assert.Equal(folder.Exe, ScheduledTasks.ExeOfOwnElevatedTask(xml));
        Assert.Equal(CommandIn(folder, at), ScheduledTasks.CommandOfOwnElevatedTask(xml));
        Assert.True(ScheduledTasks.IsOwnElevatedTask(xml));
        // A start helper that is gone starts nothing: the Start menu asks with UAC right away, and the task is registered again.
        Assert.Equal(usable, Elevation.IsUsableLaunchTask(xml, folder.Exe));
        // A task at the exe moves to the start helper beside it, and one at a start helper that is gone back to the exe.
        Assert.Equal(registerAgain, Installer.LaunchTaskNeedsRegistering(xml, folder.Exe));
        Assert.Equal(registerAgain, Installer.StartupTaskNeedsRegistering(xml, folder.Exe));
        // Not for another copy's exe, and not when any program of the user could have created it.
        var other = Path.Combine(folder.Path, "other", AppPaths.ExeName);
        Assert.False(Elevation.IsUsableLaunchTask(xml, other));
        Assert.True(Installer.StartupTaskNeedsRegistering(xml, other));
        var planted = TaskOf(Me, CommandIn(folder, at), highest: false);
        Assert.Null(ScheduledTasks.ExeOfOwnElevatedTask(planted));
        Assert.False(Elevation.IsUsableLaunchTask(planted, folder.Exe));
        Assert.False(Installer.StartupTaskNeedsRegistering(planted, folder.Exe));
        // A turned-off startup task is left as the user chose.
        Assert.False(Installer.StartupTaskNeedsRegistering(Disabled(xml), folder.Exe));
    }

    [Theory]
    [InlineData(At.ExeWithHelper)]
    [InlineData(At.ExeWithoutHelper)]
    [InlineData(At.Helper)]
    [InlineData(At.MissingHelper)]
    public async Task TheAgentTaskAndTheRecord_UseTheExeBesideTheStartHelper(At at)
    {
        using var state = new TestInstallState();
        using var folder = Folder(at);
        state.Tasks.Definitions[Agent] = TaskOf(Me, CommandIn(folder, at));
        Assert.Equal(folder.Exe, await MachineRelay.AgentTaskExecutableAsync());
        Assert.True(await MachineRelay.IsAgentInstalledAsync());
        var exes = await MachineInstall.OwnTaskExesAsync();
        Assert.Equal(new[] { folder.Exe }, exes);
        Assert.Equal(folder.Path, MachineInstall.FolderToRecord(@"D:\src\bin\Release\RunnerManager.exe", exes, Directory.Exists));
        Assert.Equal(folder.Path, MachineInstall.FolderToRecord(folder.Exe, exes, _ => false));
    }

    [Fact]
    public async Task TheAgentsVersion_IsTheVersionOfTheExeBesideTheStartHelper()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder(helper: false, "version");
        // RunnerManager.dll has this build's version; the test assembly another one, as the start helper (1.0.0.0) has.
        File.Copy(typeof(Launcher).Assembly.Location, folder.Exe, overwrite: true);
        File.Copy(typeof(HelperFolder).Assembly.Location, folder.Helper);
        Assert.True(AppUpdater.IsNewer(AppUpdater.CurrentVersion, FileVersionInfo.GetVersionInfo(folder.Helper).ProductVersion ?? string.Empty));
        state.Tasks.Definitions[Agent] = TaskOf(Me, folder.Helper);
        Assert.True(await MachineRelay.LocalAgentIsCurrentAsync());
        // An older exe beside it is older, whichever file the task names.
        File.Copy(typeof(HelperFolder).Assembly.Location, folder.Exe, overwrite: true);
        Assert.False(await MachineRelay.LocalAgentIsCurrentAsync());
    }

    [Theory]
    [InlineData(At.ExeWithHelper, true)]
    [InlineData(At.ExeWithoutHelper, false)]
    [InlineData(At.Helper, true)]
    [InlineData(At.MissingHelper, false)]
    public async Task AnOutdatedAgentTask_IsRegisteredAgainForItsExe_ThroughTheStartHelperOnlyWhileItIsThere(At at, bool throughHelper)
    {
        using var state = new TestInstallState();
        using var folder = Folder(at);
        // Without the keep-alive trigger, as earlier versions registered it.
        state.Tasks.Definitions[Agent] = TaskOf(Me, CommandIn(folder, at));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(state.Tasks.Definitions[Agent]!));

        Assert.True(await ScheduledTasks.UpdateAgentTaskIfOutdatedAsync(fromAgent: false));

        Assert.True(ScheduledTasks.IsCurrentAgentDefinition(state.Tasks.Definitions[Agent]!));
        Assert.Equal(throughHelper ? folder.Helper : folder.Exe, CommandOf(state, Agent));
        Assert.Equal(new[] { $"Create {Agent}", $"Run {Agent}" }, state.Tasks.Calls);
    }

    [Theory]
    [InlineData(At.Helper, true)]
    [InlineData(At.MissingHelper, false)]
    public async Task AnAgentTaskWithOldSettings_KeepsItsExe_AndAStartHelperOnlyWhileItIsThere(At at, bool throughHelper)
    {
        using var state = new TestInstallState();
        using var folder = Folder(at);
        state.Tasks.Definitions[Agent] = TaskOf(Me, CommandIn(folder, at))
            .Replace("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>");

        Assert.True(await MachineRelay.RepairAgentTaskSettingsAsync());

        Assert.Equal(throughHelper ? folder.Helper : folder.Exe, CommandOf(state, Agent));
        Assert.Equal(new[] { $"Create {Agent}", $"Run {Agent}" }, state.Tasks.Calls);
    }

    [Theory]
    [InlineData(At.ExeWithHelper, false, true)]
    [InlineData(At.ExeWithHelper, true, false)]
    [InlineData(At.Helper, true, true)]
    [InlineData(At.Helper, false, true)]
    [InlineData(At.MissingHelper, false, false)]
    [InlineData(At.MissingHelper, true, false)]
    [InlineData(At.ExeWithoutHelper, false, false)]
    public async Task TheAgentTask_MovesToTheStartHelper_OnlyWhenNoUpdateWaitsToProveItself(At at, bool updateWatched, bool throughHelper)
    {
        // While an update waits to prove itself, a rollback to 2.9.0 must find the agent's task as 2.9.0 left it.
        using var state = new TestInstallState();
        using var folder = Folder(at);
        state.Tasks.Definitions[Agent] = TaskOf(Me, CommandIn(folder, at));
        var before = MachineRelay.UpdateWatched;
        MachineRelay.UpdateWatched = () => updateWatched;
        try { await MachineRelay.RegisterAgentTaskAsync(folder.Exe); }
        finally { MachineRelay.UpdateWatched = before; }
        Assert.Equal(throughHelper ? folder.Helper : folder.Exe, CommandOf(state, Agent));
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData(@"C:\Program Files\RunnerManager\RunnerManager.exe", true, false)]
    [InlineData(@"C:\Program Files\RunnerManager\RunnerManagerLauncher.exe", true, true)]
    [InlineData(@"C:\Program Files\RunnerManager\RunnerManager.exe", false, true)]
    public void AnAgentRegistration_UsesTheStartHelper_UnlessAnUpdateWaitsAndTheTaskDoesNotYet(string? current, bool updateWatched, bool helper)
        => Assert.Equal(helper, MachineRelay.AgentTaskUsesStartHelper(current, updateWatched));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedMove_PointsTasksThatRunTheNewStartHelperBack_ThroughTheOldOneOnlyWhenItIsThere(bool oldHasHelper)
    {
        using var state = new TestInstallState();
        using var old = new HelperFolder(helper: oldHasHelper, "old");
        using var moved = new HelperFolder(helper: true, "new");
        state.Tasks.Definitions[Startup] = TaskOf(Me, moved.Helper);
        state.Tasks.Definitions[Agent] = TaskOf(Me, moved.Helper);
        var agentPointedAt = new List<string>();

        await Installer.PointTasksBackAsync(old.Exe, moved.Path, exe => { agentPointedAt.Add(exe); return Task.CompletedTask; });

        // The old copy may be 2.9.0, which has no start helper: then its exe.
        Assert.Equal(oldHasHelper ? old.Helper : old.Exe, CommandOf(state, Startup));
        Assert.Equal(new[] { old.Exe }, agentPointedAt);
        // The agent's own registration does the same.
        await MachineRelay.RegisterAgentTaskAsync(old.Exe);
        Assert.Equal(oldHasHelper ? old.Helper : old.Exe, CommandOf(state, Agent));
    }

    [Fact]
    public async Task ATaskThatStillStartsTheExe_IsMovedToTheStartHelper_WhateverTheOncePerVersionMarkSays()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Helper);
        Assert.False(await Installer.SkipsStartHelperAsync(folder.Exe));
        // A turned-off startup task is left as the user chose.
        state.Tasks.Definitions[Startup] = Disabled(TaskOf(Me, folder.Exe));
        Assert.False(await Installer.SkipsStartHelperAsync(folder.Exe));
        state.Tasks.Definitions[Startup] = TaskOf(Me, folder.Exe);
        Assert.True(await Installer.SkipsStartHelperAsync(folder.Exe));
        state.Tasks.Definitions[Startup] = TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Exe);
        Assert.True(await Installer.SkipsStartHelperAsync(folder.Exe));
        // A task that any program of the user can create does not count.
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Exe, highest: false);
        Assert.False(await Installer.SkipsStartHelperAsync(folder.Exe));
        // Without the start helper there is nothing to move to, and no task is read.
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Exe);
        folder.RemoveHelper();
        Assert.False(await Installer.SkipsStartHelperAsync(folder.Exe));
    }

    [Fact]
    public async Task TheStartupTask_MovesToTheStartHelper_ButNotWhileItRuns()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        state.Tasks.Definitions[Startup] = TaskOf(Me, folder.Exe);
        // The dashboard may have been started by it: left for a later start, by the dashboard or the fleet agent.
        state.Tasks.Running.Add(Startup);
        Assert.False(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Equal(folder.Exe, CommandOf(state, Startup));
        state.Tasks.Running.Clear();
        Assert.True(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Equal(folder.Helper, CommandOf(state, Startup));
        Assert.Equal(new[] { $"Create {Startup}" }, state.Tasks.Calls);
    }

    [Fact]
    public async Task TheLaunchTask_IsRegisteredForTheStartHelper_WhileItIsInPlace()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        await ScheduledTasks.RegisterLaunchTaskAsync(folder.Exe);
        Assert.Equal(folder.Helper, CommandOf(state, Launch));
        Assert.Contains($"<Arguments>{App.FromTaskArgument}</Arguments>", state.Tasks.Definitions[Launch]);
        Assert.True(Elevation.IsUsableLaunchTask(state.Tasks.Definitions[Launch]!, folder.Exe));
        folder.RemoveHelper();
        await ScheduledTasks.RegisterLaunchTaskAsync(folder.Exe);
        Assert.Equal(folder.Exe, CommandOf(state, Launch));
    }

    [Fact]
    public async Task WhenTheStartHelperIsGone_ThisUsersTasksStartTheExeAgain()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[Startup] = TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[Agent] = TaskOf(Me, folder.Helper);
        var log = new List<string>();
        // While it is in place, nothing moves.
        Assert.Empty(await Launcher.PointOwnTasksAtExeAsync(folder.Exe, log.Add));
        Assert.Empty(state.Tasks.Calls);

        folder.RemoveHelper();
        var moved = await Launcher.PointOwnTasksAtExeAsync(folder.Exe, log.Add);

        Assert.Equal(new[] { Launch, Startup, Agent }, moved);
        foreach (var name in moved) Assert.Equal(folder.Exe, CommandOf(state, name));
        // The fleet agent is started again; when it runs, its single-instance lock makes this a no-op.
        Assert.Equal($"Run {Agent}", state.Tasks.Calls.Last());
        Assert.Contains($"start {folder.Exe} again, without the start helper", Assert.Single(log));
    }

    [Fact]
    public async Task OnAPcThatRunsWithoutTheStartHelper_ItsTasksStartTheExe_ButATurnedOffStartupTaskStaysAsTheUserChose()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        Launcher.TurnOff(folder.Path, AppUpdater.CurrentVersion, "it was removed right after it was written");
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[Startup] = Disabled(TaskOf(Me, folder.Helper));
        // Another Windows user's fleet agent is never registered by this user.
        state.Tasks.Definitions[Agent] = TaskOf(OtherSid, folder.Helper);

        var moved = await Launcher.PointOwnTasksAtExeAsync(folder.Exe, _ => { });

        Assert.Equal(new[] { Launch }, moved);
        Assert.Equal(folder.Exe, CommandOf(state, Launch));
        Assert.Equal(new[] { $"Create {Launch}" }, state.Tasks.Calls);
    }

    [Fact]
    public async Task AnotherUsersTaskThatRunsTheStartHelper_UsesTheInstall_SoUninstallKeepsItsFiles()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        // One user: only this user's own tasks run it, so uninstall removes the program files.
        state.Tasks.Definitions[Launch] = TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[Agent] = TaskOf(Me, folder.Helper);
        Assert.False((await MachineInstall.OtherUsersOfAsync(folder.Path)).KeepFiles);

        // A second user's task names the same start helper.
        state.Tasks.Definitions["Runner Manager (bob)"] = TaskOf(OtherSid, folder.Helper);
        var scan = await MachineInstall.RunnerManagerTasksAsync();
        var bob = Assert.Single(scan.Uses, u => !u.IsOwn);
        Assert.Equal(folder.Exe, bob.Command);
        var others = await MachineInstall.OtherUsersOfAsync(folder.Path);
        Assert.True(others.KeepFiles);
        Assert.Equal(new[] { MachineInstall.AccountOf(OtherSid) }, others.Names);
    }

    [Fact]
    public async Task ARunnerWidgetInstall_MovesItsAgentToTheStartHelperBesideTheNewExe()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        var legacyExe = Path.Combine(folder.Path, AppPaths.LegacyExeName);
        File.WriteAllText(legacyExe, string.Empty);
        // As RunnerWidget 2.1.0 registered it: with the highest rights, below normal priority.
        state.Tasks.Definitions[ScheduledTasks.LegacyAgentTaskName] = TaskOf(Me, legacyExe).Replace("<Priority>4</Priority>", "<Priority>7</Priority>");

        await LegacyInstall.MigrateAsync(_ => { }, fromSetup: true, folder.Exe, legacyExe, Path.Combine(folder.Path, "RunnerWidget"));

        Assert.Equal(folder.Helper, CommandOf(state, Agent));
        Assert.Equal(folder.Exe, ScheduledTasks.ExeOfOwnElevatedTask(state.Tasks.Definitions[Agent]!));
        Assert.False(state.Tasks.Definitions.ContainsKey(ScheduledTasks.LegacyAgentTaskName));
    }
}

/// <summary>Writing the start helper into an install folder (see <see cref="Launcher.EnsureFileAsync"/>), in temp folders.</summary>
public class StartHelperFileTests
{
    private static readonly byte[] NewBytes = "the start helper of this version"u8.ToArray();

    private static Task NoSettle() => Task.CompletedTask;

    private static string[] Leftovers(HelperFolder folder)
        => Directory.EnumerateFileSystemEntries(folder.Path)
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("RunnerManagerLauncher.", StringComparison.OrdinalIgnoreCase) && !n.Equals(Launcher.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .ToArray();

    [Fact]
    public async Task AMissingStartHelper_IsWritten()
    {
        using var folder = new HelperFolder(helper: false);
        var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);
        Assert.Equal(new Launcher.EnsureResult(true, true, null), result);
        Assert.Equal(NewBytes, File.ReadAllBytes(folder.Helper));
        Assert.Empty(Leftovers(folder));
    }

    [Fact]
    public async Task ADifferentStartHelper_IsReplaced()
    {
        using var folder = new HelperFolder();
        var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);
        Assert.Equal(new Launcher.EnsureResult(true, true, null), result);
        Assert.Equal(NewBytes, File.ReadAllBytes(folder.Helper));
        // The old one was not running, so it is gone at once.
        Assert.Empty(Leftovers(folder));
    }

    [Fact]
    public async Task TheSameStartHelper_IsLeftAsItIs()
    {
        using var folder = new HelperFolder();
        File.WriteAllBytes(folder.Helper, NewBytes);
        var written = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(folder.Helper, written);
        var settled = 0;
        var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, () => { settled++; return Task.CompletedTask; },
            _ => throw new InvalidOperationException("The update lock is only taken to write."));
        Assert.Equal(Launcher.EnsureResult.Kept, result);
        Assert.Equal(written, File.GetLastWriteTimeUtc(folder.Helper));
        Assert.Equal(0, settled);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    /// <summary>Maps the file as a program image, as Windows does for a running exe: it can be renamed, but not changed or deleted.</summary>
    private const uint LoadLibraryAsImageResource = 0x20;

    [Fact]
    public async Task ARunningStartHelper_IsRenamedOutOfTheWay_AndRemovedOnceItNoLongerRuns()
    {
        using var folder = new HelperFolder(helper: false);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), folder.Helper);
        var module = LoadLibraryExW(folder.Helper, IntPtr.Zero, LoadLibraryAsImageResource);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            // It is in use: it cannot be written.
            Assert.ThrowsAny<SystemException>(() => new FileStream(folder.Helper, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());

            var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);

            Assert.Equal(new Launcher.EnsureResult(true, true, null), result);
            Assert.Equal(NewBytes, File.ReadAllBytes(folder.Helper));
            var old = Assert.Single(Leftovers(folder));
            Assert.Matches(@"^RunnerManagerLauncher\.\d{15,}\.old\.exe$", old);
        }
        finally { FreeLibrary(module); }

        // A later check removes it.
        Assert.Equal(Launcher.EnsureResult.Kept, await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle));
        Assert.Empty(Leftovers(folder));
    }

    [Fact]
    public async Task AStartHelperThatCannotBeReplaced_IsNotKept_AndLeavesNothingBehind()
    {
        using var folder = new HelperFolder();
        Launcher.EnsureResult result;
        // Another program holds it open and lets no one rename or delete it.
        using (new FileStream(folder.Helper, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);
        Assert.False(result.InPlace);
        Assert.False(result.Wrote);
        Assert.StartsWith("it could not be written: ", result.Problem);
        Assert.Equal(HelperFolder.Bytes, File.ReadAllBytes(folder.Helper));
        Assert.Empty(Leftovers(folder));
    }

    [Fact]
    public async Task AFolderInItsPlace_IsLeft_AndTheStartHelperIsNotKept()
    {
        using var folder = new HelperFolder(helper: false);
        Directory.CreateDirectory(Path.Combine(folder.Helper, "inside"));
        var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);
        Assert.Equal(new Launcher.EnsureResult(false, false, $"a folder named {Launcher.FileName} is in its place"), result);
        Assert.True(Directory.Exists(Path.Combine(folder.Helper, "inside")));
        Assert.Empty(Leftovers(folder));
    }

    [JunctionFact]
    public async Task ALinkInItsPlace_IsRemovedItself_NeverFollowed()
    {
        using var folder = new HelperFolder(helper: false);
        var elsewhere = InstallEvidenceTests.NewFolder("elsewhere");
        var partial = folder.Helper + ".new.partial";
        try
        {
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
            Junctions.Create(folder.Helper, elsewhere);
            // A write that was cut off left a link where the new file is written first.
            Junctions.Create(partial, elsewhere);

            var result = await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle);

            Assert.Equal(new Launcher.EnsureResult(true, true, null), result);
            Assert.True(Launcher.IsRegularFile(folder.Helper));
            Assert.Equal(NewBytes, File.ReadAllBytes(folder.Helper));
            Assert.Equal(new[] { "keep.txt" }, Directory.EnumerateFileSystemEntries(elsewhere).Select(Path.GetFileName));
            Assert.Empty(Leftovers(folder));
        }
        finally
        {
            Junctions.Delete(folder.Helper, partial);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task AStartHelperRemovedOrChangedRightAfterItWasWritten_IsNotKept()
    {
        using var folder = new HelperFolder(helper: false);
        var removed = await Launcher.EnsureFileAsync(folder.Path, NewBytes, () => { File.Delete(folder.Helper); return Task.CompletedTask; });
        Assert.Equal(new Launcher.EnsureResult(false, true, "it was removed right after it was written"), removed);

        var changed = await Launcher.EnsureFileAsync(folder.Path, NewBytes, () => { File.WriteAllText(folder.Helper, "quarantined"); return Task.CompletedTask; });
        Assert.Equal(new Launcher.EnsureResult(false, true, "it was changed right after it was written"), changed);
    }

    [Fact]
    public async Task WhileAnUpdateHoldsTheLock_NothingIsWritten_AndAStartHelperThatIsThereStillCounts()
    {
        using var folder = new HelperFolder(helper: false);
        Assert.Equal(new Launcher.EnsureResult(false, false, null), await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle, _ => null));
        Assert.False(File.Exists(folder.Helper));

        File.WriteAllBytes(folder.Helper, HelperFolder.Bytes);
        Assert.Equal(new Launcher.EnsureResult(true, false, null), await Launcher.EnsureFileAsync(folder.Path, NewBytes, NoSettle, _ => null));
        Assert.Equal(HelperFolder.Bytes, File.ReadAllBytes(folder.Helper));
    }

    [Fact]
    public async Task OnlyAnInstallFolderThatExistsByItsFullPath_GetsAStartHelper()
    {
        Assert.False((await Launcher.EnsureFileAsync(@"Apps\RunnerManager", NewBytes, NoSettle)).InPlace);
        var missing = Path.Combine(Path.GetTempPath(), $"rm-missing-{Guid.NewGuid():N}");
        var result = await Launcher.EnsureFileAsync(missing, NewBytes, NoSettle);
        Assert.Equal("the install folder does not exist", result.Problem);
        Assert.False(Directory.Exists(missing));
    }

    [JunctionFact]
    public async Task AnInstallFolderThatIsALink_GetsNoStartHelper()
    {
        var target = InstallEvidenceTests.NewFolder("target");
        var link = Path.Combine(Path.GetTempPath(), $"rm-link-{Guid.NewGuid():N}");
        try
        {
            Junctions.Create(link, target);
            var result = await Launcher.EnsureFileAsync(link, NewBytes, NoSettle);
            Assert.Equal("the install folder is a link", result.Problem);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            Junctions.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void WhatEarlierVersionsUnpacked_IsRemoved_UnlessItIsInUse()
    {
        using var folder = new HelperFolder();
        var builds = Path.Combine(folder.Path, Launcher.ExtractionFolderName, "RunnerManager");
        string Build(string name)
        {
            var build = Path.Combine(builds, name);
            Directory.CreateDirectory(Path.Combine(build, "sub"));
            File.WriteAllText(Path.Combine(build, "a.dll"), "x");
            File.WriteAllText(Path.Combine(build, "sub", "native.dll"), "x");
            return build;
        }
        var old = Build("old");
        var current = Build("current");
        var open = Build("open");

        using (new FileStream(Path.Combine(open, "sub", "native.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Launcher.RemoveOldExtractions(folder.Path, new[] { current + Path.DirectorySeparatorChar });

        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(current, "sub", "native.dll")));
        Assert.True(File.Exists(Path.Combine(open, "sub", "native.dll")));
    }

    [JunctionFact]
    public void WhatEarlierVersionsUnpacked_IsRemovedWithoutGoingIntoLinks()
    {
        using var folder = new HelperFolder();
        using var other = new HelperFolder(name: "other-install");
        var elsewhere = InstallEvidenceTests.NewFolder("elsewhere");
        var builds = Path.Combine(folder.Path, Launcher.ExtractionFolderName, "RunnerManager");
        var old = Path.Combine(builds, "old");
        var inner = Path.Combine(old, "inner");
        var linkedBuild = Path.Combine(builds, "linked");
        var linkedRoot = Path.Combine(other.Path, Launcher.ExtractionFolderName);
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(Path.Combine(elsewhere, "RunnerManager", "build"));
        File.WriteAllText(Path.Combine(elsewhere, "RunnerManager", "build", "keep.dll"), "keep");
        try
        {
            Junctions.Create(inner, elsewhere);
            Junctions.Create(linkedBuild, elsewhere);
            // A .net folder that is a link is left as it is.
            Junctions.Create(linkedRoot, elsewhere);

            Launcher.RemoveOldExtractions(folder.Path, Array.Empty<string>());
            Launcher.RemoveOldExtractions(other.Path, Array.Empty<string>());

            Assert.False(Directory.Exists(old));
            Assert.False(Directory.Exists(linkedBuild));
            Assert.True(Directory.Exists(linkedRoot));
            Assert.True(File.Exists(Path.Combine(elsewhere, "RunnerManager", "build", "keep.dll")));
        }
        finally
        {
            Junctions.Delete(inner, linkedBuild, linkedRoot);
            Directory.Delete(elsewhere, recursive: true);
        }
    }
}

/// <summary>
/// Keeping the start helper for this version (see <see cref="Launcher.EnsureAsync"/>): written when it is missing or
/// different, put back once per version, and given up for the version when it goes again. Tasks in memory.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperEnsureTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static readonly byte[] Bytes = "the start helper of this version"u8.ToArray();

    /// <summary>Gives the start helper's seams for one test, and puts back the ones before.</summary>
    private sealed class Seams : IDisposable
    {
        private readonly Func<byte[]?> _embedded = Launcher.Embedded;
        private readonly Func<Task> _settle = Launcher.Settle;
        private readonly Func<TimeSpan, Func<Launcher.EnsureResult?>, Launcher.EnsureResult?> _lock = Launcher.UnderUpdateLock;

        public Seams(byte[]? bytes)
        {
            Launcher.Embedded = () => bytes;
            InstallChecks.Forget(Launcher.RepairedCheck);
        }

        public void Dispose()
        {
            Launcher.Embedded = _embedded;
            Launcher.Settle = _settle;
            Launcher.UnderUpdateLock = _lock;
            InstallChecks.Forget(Launcher.RepairedCheck);
        }
    }

    [Fact]
    public async Task AStartHelperThatNoTaskNamesYet_IsWritten()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        var log = new List<string>();
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));
        Assert.Equal(Bytes, File.ReadAllBytes(folder.Helper));
        Assert.Equal($"Wrote the start helper {folder.Helper}.", Assert.Single(log));
        Assert.False(InstallChecks.IsDone(Launcher.RepairedCheck));
        // Checked again on the next start: nothing to do.
        log.Clear();
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public async Task AStartHelperThatGoesMissing_IsPutBackOnce_ThenThisVersionRunsWithoutIt()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        var log = new List<string>();

        // Antivirus software removed it: put back once.
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));
        Assert.Equal(Bytes, File.ReadAllBytes(folder.Helper));
        Assert.Contains(log, line => line.StartsWith($"{Launcher.FileName} was missing from {folder.Path}", StringComparison.Ordinal));
        Assert.True(InstallChecks.IsDone(Launcher.RepairedCheck));

        // Removed again: this version runs without it, and says so in the fleet agent log.
        folder.RemoveHelper();
        log.Clear();
        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));
        Assert.False(File.Exists(folder.Helper));
        Assert.Equal(AppUpdater.CurrentVersion, Launcher.OffVersion(folder.Path));
        Assert.Equal(Launcher.OffLogText($"{Launcher.FileName} was removed again after it was put back"), Assert.Single(log));
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));
        // Its tasks start the exe again.
        Assert.Equal(new[] { ScheduledTasks.LaunchTaskName }, await Launcher.PointOwnTasksAtExeAsync(folder.Exe, log.Add));

        // Later starts of this version leave it so.
        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));
        Assert.False(File.Exists(folder.Helper));

        // Setup, or the next update, tries again.
        log.Clear();
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: true, log.Add));
        Assert.Equal(Bytes, File.ReadAllBytes(folder.Helper));
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe));
    }

    [Fact]
    public async Task AStartHelperRemovedRightAfterItWasWritten_TurnsItOffForThisVersion()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        Launcher.Settle = () => { File.Delete(folder.Helper); return Task.CompletedTask; };
        var log = new List<string>();

        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: true, log.Add));

        Assert.True(Launcher.RunsWithout(folder.Path));
        Assert.Equal(Launcher.OffLogText("it was removed right after it was written"), Assert.Single(log));
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));
    }

    [Fact]
    public async Task AStartHelperThatCannotBeWritten_TurnsItOffForThisVersion()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        Directory.CreateDirectory(folder.Helper);
        var log = new List<string>();

        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: false, log.Add));

        Assert.Equal(AppUpdater.CurrentVersion, Launcher.OffVersion(folder.Path));
        Assert.Equal(Launcher.OffLogText($"a folder named {Launcher.FileName} is in its place"), Assert.Single(log));
    }

    [Fact]
    public async Task AnEarlierVersionThatRanWithoutIt_DoesNotStopThisOne()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        Launcher.TurnOff(folder.Path, "2.9.9", "it was removed right after it was written");
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.Equal(Bytes, File.ReadAllBytes(folder.Helper));
    }

    [Fact]
    public async Task ABuildWithoutTheStartHelper_WritesNone_AndSaysSoOnce()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(null);
        using var folder = new HelperFolder(helper: false);
        var log = new List<string>();
        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: true, log.Add));
        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: true, log.Add));
        Assert.False(File.Exists(folder.Helper));
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.True(log.Count <= 1);
        Assert.All(log, line => Assert.Equal("This build of Runner Manager has no start helper, so its scheduled tasks start RunnerManager.exe directly.", line));
    }

    [Fact]
    public async Task WhileAnUpdateHoldsTheLock_TheStartHelperIsLeftForALaterStart()
    {
        using var state = new TestInstallState();
        using var seams = new Seams(Bytes);
        using var folder = new HelperFolder(helper: false);
        TimeSpan? waited = null;
        Launcher.UnderUpdateLock = (wait, _) => { waited = wait; return null; };

        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        // A start waits only briefly; it is not given up on.
        Assert.Equal(TimeSpan.FromSeconds(2), waited);
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.False(File.Exists(folder.Helper));

        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: true, _ => { }));
        Assert.Equal(TimeSpan.FromSeconds(30), waited);
    }
}

/// <summary>
/// The fleet agent's own move to the start helper and its restart through it (see <see cref="Launcher.AgentStep"/> and
/// <see cref="MachineRelay.RestartThroughStartHelperAsync"/>). The agent's command gate is process-wide, so these run
/// one at a time with the other tests of the agent's exit.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
public class StartHelperAgentTests
{
    private const string Exe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
    private const string Helper = @"C:\Program Files\RunnerManager\RunnerManagerLauncher.exe";
    private const string OtherCopy = @"D:\Other\RunnerManager.exe";

    [Theory]
    // The task starts the exe: it moves to the start helper and the agent restarts, whatever the mark in the data folder says.
    [InlineData(false, true, Exe, false, false, "MoveTaskAndRestart")]
    [InlineData(false, true, Exe, false, true, "MoveTaskAndRestart")]
    // Registered while this agent ran: it restarts once per version, so a start that is not recognized never loops.
    [InlineData(false, true, Helper, false, false, "Restart")]
    [InlineData(false, true, Helper, false, true, "Nothing")]
    // Started by the start helper: nothing to do.
    [InlineData(true, true, Helper, false, false, "Nothing")]
    // No start helper in place, or an update that waits to prove itself.
    [InlineData(false, false, Exe, false, false, "Nothing")]
    [InlineData(false, true, Exe, true, false, "Nothing")]
    [InlineData(false, true, Helper, true, false, "Nothing")]
    // A task that starts another copy, or none.
    [InlineData(false, true, OtherCopy, false, false, "Nothing")]
    [InlineData(false, true, "", false, false, "Nothing")]
    public void WhatTheAgentDoesAboutItsStart(bool startedByHelper, bool helperInPlace, string command, bool updateWatched, bool restarted, string step)
        => Assert.Equal(Enum.Parse<Launcher.AgentStartStep>(step),
            Launcher.AgentStep(startedByHelper, helperInPlace, command, Exe, updateWatched, restarted));

    /// <summary>The agent's queue, as the agent's loop has it.</summary>
    private sealed class Commands
    {
        private readonly Channel<MachineCommand> _queue = Channel.CreateUnbounded<MachineCommand>();
        private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

        public void Take(string id)
            => MachineRelay.TakeCommand(new MachineCommand { Id = id, Target = "runner", Action = "Start", TargetKey = "r1" }, _pending, _queue.Writer, _ => false);

        public int Count => _pending.Count;
    }

    /// <summary>The steps of a restart, recorded in order.</summary>
    private sealed class Steps
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Logged = new();
        public bool Updating;
        public bool ScheduleFails;
        public bool MoveFails;
        public bool TaskStillAtExe;

        public MachineRelay.StartHelperRestartSteps Build() => new(
            () => Updating,
            () =>
            {
                Calls.Add("schedule");
                if (ScheduleFails) throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");
            },
            () =>
            {
                Calls.Add("move");
                return MoveFails ? Task.FromException(new InvalidOperationException("Access is denied.")) : Task.CompletedTask;
            },
            () =>
            {
                Calls.Add("check");
                return Task.FromResult(!TaskStillAtExe);
            },
            () => Calls.Add("mark"),
            Logged.Add);
    }

    private static Task<bool> Restart(Launcher.AgentStartStep step, Steps steps, Func<bool>? quiet = null, Action? ranQueued = null)
        => MachineRelay.RestartThroughStartHelperAsync(step, () => { ranQueued?.Invoke(); return Task.CompletedTask; }, quiet ?? (() => true), steps.Build());

    [Fact]
    public async Task AnAgentWhoseTaskStartsTheExe_MovesItsTask_ThenExitsToRestartThroughTheStartHelper()
    {
        var steps = new Steps();
        var commands = new Commands();
        try
        {
            var ran = 0;
            Assert.True(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps, ranQueued: () => ran++));
            // The commands it took ran first; the helper that starts the task again is started before the task is registered.
            Assert.Equal(1, ran);
            Assert.Equal(new[] { "schedule", "move", "check", "mark" }, steps.Calls);
            Assert.Equal("Moved the fleet agent task to the start helper. The fleet agent restarts through it, so it runs with a clean environment.",
                Assert.Single(steps.Logged));
            // Until it exits, a new command is refused, and the PC that sent it is told to try again in a minute.
            var refused = Assert.Throws<InvalidOperationException>(() => commands.Take("new"));
            Assert.Equal(MachineRelay.RestartingNowText, refused.Message);
            Assert.DoesNotContain("update", MachineRelay.RestartingNowText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MachineRelay.TakeCommandsAgain();
            MachineRelay.ForgetStartHelperMove();
        }
    }

    [Fact]
    public async Task AnAgentWhoseTaskAlreadyRunsTheStartHelper_OnlyRestarts()
    {
        var steps = new Steps();
        try
        {
            Assert.True(await Restart(Launcher.AgentStartStep.Restart, steps));
            Assert.Equal(new[] { "schedule", "mark" }, steps.Calls);
            Assert.Equal("The fleet agent restarts through the start helper, so it runs with a clean environment.", Assert.Single(steps.Logged));
        }
        finally { MachineRelay.TakeCommandsAgain(); }
    }

    [Fact]
    public async Task NoRestart_WhileAnUpdateIsInProgress()
    {
        var steps = new Steps { Updating = true };
        var commands = new Commands();
        Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps));
        Assert.False(await Restart(Launcher.AgentStartStep.Restart, steps));
        Assert.Empty(steps.Calls);
        commands.Take("next");
        Assert.Equal(1, commands.Count);
    }

    [Fact]
    public async Task NoRestart_WhenWorkCameInMeanwhile_AndTheAgentTakesCommandsAgain()
    {
        var steps = new Steps();
        var commands = new Commands();
        Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps, quiet: () => false));
        Assert.Empty(steps.Calls);
        commands.Take("next");
        Assert.Equal(1, commands.Count);
        Assert.False(await Restart(Launcher.AgentStartStep.Nothing, steps));
        Assert.Empty(steps.Calls);
    }

    [Fact]
    public async Task AMoveThatFails_KeepsTheAgentRunning_AndIsNotTriedAgainByThisProcess()
    {
        var steps = new Steps { MoveFails = true };
        var commands = new Commands();
        try
        {
            Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps));
            Assert.Equal(new[] { "schedule", "move" }, steps.Calls);
            Assert.Equal("Could not move the fleet agent task to the start helper (Access is denied.). The fleet agent keeps running as it is.",
                Assert.Single(steps.Logged));
            // It takes commands again, and the next check does not try again.
            commands.Take("next");
            Assert.Equal(1, commands.Count);
            steps.Calls.Clear();
            steps.MoveFails = false;
            Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps));
            Assert.Empty(steps.Calls);

            // A task that does not start the start helper after it was registered counts as a failed move too.
            MachineRelay.ForgetStartHelperMove();
            steps.TaskStillAtExe = true;
            steps.Logged.Clear();
            Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps));
            Assert.Contains("the task does not start it after it was registered", Assert.Single(steps.Logged));
            commands.Take("later");
            Assert.Equal(2, commands.Count);
        }
        finally
        {
            MachineRelay.TakeCommandsAgain();
            MachineRelay.ForgetStartHelperMove();
        }
    }

    [Fact]
    public async Task WhenTheRestartHelperCannotStart_TheTaskIsNotMoved_AndTheAgentStays()
    {
        var steps = new Steps { ScheduleFails = true };
        var commands = new Commands();
        Assert.False(await Restart(Launcher.AgentStartStep.MoveTaskAndRestart, steps));
        Assert.Equal(new[] { "schedule" }, steps.Calls);
        Assert.Equal("The fleet agent could not restart through the start helper: The system cannot find the file specified.", Assert.Single(steps.Logged));
        commands.Take("next");
        Assert.Equal(1, commands.Count);
    }
}

/// <summary>
/// The start helper exits a moment after the RunnerManager.exe it started, and until then its file is in use and Task
/// Scheduler counts its task as running. What starts a task or removes files after the app exits copes with that.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperTimingTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static readonly string Agent = ScheduledTasks.AgentTaskName;

    /// <summary>Task Scheduler that refuses the first starts, as it does while the instance before still runs.</summary>
    private sealed class RefusingScheduler(MemoryTasks inner, int refusals) : ITaskScheduler
    {
        public int Runs { get; private set; }

        public Task<(int ExitCode, string Output)> SchtasksAsync(string arguments)
        {
            if (!arguments.StartsWith("/Run ", StringComparison.Ordinal)) return inner.SchtasksAsync(arguments);
            if (++Runs <= refusals) return Task.FromResult((1, "ERROR: The operator or administrator has refused the request. (0x800710E0)"));
            return inner.SchtasksAsync(arguments);
        }

        public bool? IsIndexed(string name) => inner.IsIndexed(name);
        public bool? IsRegistered(string name) => inner.IsRegistered(name);
        public Task<bool?> IsRunningAsync(string name) => inner.IsRunningAsync(name);
        public Task<IDisposable> LockAsync() => inner.LockAsync();
    }

    /// <summary>Sets the pause between tries for one test, and puts back the one before.</summary>
    private sealed class Pause : IDisposable
    {
        private readonly Func<TimeSpan, Task> _before = ScheduledTasks.RetryPause;

        public Pause(Func<TimeSpan, Task> pause) => ScheduledTasks.RetryPause = pause;

        public void Dispose() => ScheduledTasks.RetryPause = _before;
    }

    [Fact]
    public async Task AStartAfterTheAgentEnded_WaitsWhileTheTaskStillRuns()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Apps\RunnerManager\RunnerManagerLauncher.exe");
        state.Tasks.Running.Add(Agent);
        var pauses = 0;
        using var pause = new Pause(_ =>
        {
            if (++pauses == 3) state.Tasks.Running.Remove(Agent);
            return Task.CompletedTask;
        });

        var (code, _) = await ScheduledTasks.StartAgainAsync(Agent, TimeSpan.FromSeconds(30));

        Assert.Equal(0, code);
        Assert.Equal(3, pauses);
        Assert.Equal(new[] { $"Run {Agent}" }, state.Tasks.Calls);
    }

    [Fact]
    public async Task AStartThatTaskSchedulerRefuses_IsTriedAgain()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Apps\RunnerManager\RunnerManagerLauncher.exe");
        var refusing = new RefusingScheduler(state.Tasks, refusals: 2);
        ScheduledTasks.Scheduler = refusing;
        using var pause = new Pause(_ => Task.CompletedTask);

        var (code, _) = await ScheduledTasks.StartAgainAsync(Agent, TimeSpan.FromSeconds(30));

        Assert.Equal(0, code);
        Assert.Equal(3, refusing.Runs);
        Assert.Contains(Agent, state.Tasks.Running);
    }

    [Fact]
    public async Task ATaskThatStillRunsAtTheEnd_IsNotStarted_AndTheFailureIsReturned()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Apps\RunnerManager\RunnerManagerLauncher.exe");
        state.Tasks.Running.Add(Agent);
        using var pause = new Pause(_ => Task.Delay(20));

        var (code, output) = await ScheduledTasks.StartAgainAsync(Agent, TimeSpan.FromSeconds(1));

        Assert.NotEqual(0, code);
        Assert.Equal($"The scheduled task \"{Agent}\" still ran after 1 seconds, so it was not started again.", output);
        Assert.Empty(state.Tasks.Calls);
    }

    [Fact]
    public void TheHelperThatRunsAfterTheAppExits_WaitsForTheStartHelperToo()
    {
        Assert.StartsWith("Wait-Process -Id 1234,5678 -Timeout 120 -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; ",
            AppUpdater.AfterExitScript("x", 1234, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1), 5678));
        Assert.StartsWith("Wait-Process -Id 1234 -ErrorAction SilentlyContinue;", AppUpdater.AfterExitScript("x", 1234, null, TimeSpan.Zero));
        Assert.StartsWith("Wait-Process -Id 1234 -ErrorAction", AppUpdater.AfterExitScript("x", 1234, null, TimeSpan.Zero, 1234));
    }

    [Fact]
    public void TheHelperThatRunsAfterTheAppExits_WaitsUntilBothHaveExited()
    {
        var folder = InstallEvidenceTests.NewFolder("after-exit");
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        Process Start(int count)
        {
            var start = new ProcessStartInfo(ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(count.ToString());
            start.ArgumentList.Add("127.0.0.1");
            return Process.Start(start)!;
        }
        using var app = Start(1);
        using var helper = Start(4);
        try
        {
            var done = Path.Combine(folder, "done.txt");
            InstallFolderTests.RunPowerShell(
                AppUpdater.AfterExitScript("Set-Content -LiteralPath $env:RM_TEST_DONE -Value done", app.Id, TimeSpan.FromSeconds(25), TimeSpan.Zero, helper.Id),
                new Dictionary<string, string> { ["RM_TEST_DONE"] = done });
            Assert.True(File.Exists(done));
            Assert.True(helper.HasExited);
        }
        finally
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Dictionary<string, string> RestartEnvironment(string schtasks, string log) => new()
    {
        // A name that no task has: Task Scheduler is only asked for its state.
        [ScheduledTasks.TaskNameVariable] = $"Runner Manager test {Guid.NewGuid():N}",
        [AppUpdater.SchtasksVariable] = schtasks,
        [AppUpdater.LogVariable] = log
    };

    [Fact]
    public void TheRestartHelper_TriesAgainWhileTaskSchedulerRefusesTheStart()
    {
        var folder = InstallEvidenceTests.NewFolder("restart");
        try
        {
            var fake = Path.Combine(folder, "schtasks.cmd");
            // Refuses the first start, as Task Scheduler does while the start helper still runs, and takes the next.
            File.WriteAllText(fake, "@echo off\r\necho %* >>\"%~dp0calls.txt\"\r\nif exist \"%~dp0refused\" exit /b 0\r\ntype nul >\"%~dp0refused\"\r\nexit /b 1\r\n");
            var log = Path.Combine(folder, "agent.log");
            File.WriteAllText(log, string.Empty);

            InstallFolderTests.RunPowerShell(AppUpdater.RestartTaskScript(20), RestartEnvironment(fake, log));

            var calls = File.ReadAllLines(Path.Combine(folder, "calls.txt"));
            Assert.Equal(2, calls.Length);
            Assert.All(calls, call => Assert.StartsWith("/Run /TN \"Runner Manager test ", call));
            Assert.Equal(string.Empty, File.ReadAllText(log));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheRestartHelper_SaysSoInTheLog_WhenTheTaskDidNotStart()
    {
        var folder = InstallEvidenceTests.NewFolder("restart-fails");
        try
        {
            var fake = Path.Combine(folder, "schtasks.cmd");
            File.WriteAllText(fake, "@echo off\r\necho %* >>\"%~dp0calls.txt\"\r\nexit /b 1\r\n");
            var log = Path.Combine(folder, "agent.log");
            File.WriteAllText(log, string.Empty);

            InstallFolderTests.RunPowerShell(AppUpdater.RestartTaskScript(2), RestartEnvironment(fake, log));

            Assert.NotEmpty(File.ReadAllLines(Path.Combine(folder, "calls.txt")));
            Assert.EndsWith("The fleet agent did not start again after it exited to restart: Task Scheduler did not start its task within 2 seconds. " +
                            "Its task starts it again within five minutes." + Environment.NewLine, File.ReadAllText(log));

            // A log that is not there is not created.
            var missing = Path.Combine(folder, "missing.log");
            InstallFolderTests.RunPowerShell(AppUpdater.RestartTaskScript(1), RestartEnvironment(fake, missing));
            Assert.False(File.Exists(missing));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

/// <summary>Uninstall and the removal after a move remove the start helper and what the exe unpacked through it.</summary>
public class StartHelperRemovalTests
{
    private static void CreateInstall(string folder)
    {
        var build = Path.Combine(folder, Launcher.ExtractionFolderName, "RunnerManager", "build1", "sub");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "native.dll"), "x");
        foreach (var name in new[] { AppPaths.ExeName, Launcher.FileName, Launcher.FileName + ".off", Launcher.FileName + ".new",
                     Launcher.FileName + ".new.partial", "RunnerManagerLauncher.638000000000000000.old.exe" })
            File.WriteAllText(Path.Combine(folder, name), "x");
    }

    [Fact]
    public void TheRemovalScript_RemovesTheStartHelperAndWhatTheExeUnpacked_ButKeepsOtherFiles()
    {
        var root = InstallEvidenceTests.NewFolder("remove-helper");
        var own = Path.Combine(root, "Apps", "RunnerManager");
        var shared = Path.Combine(root, "Shared", "RunnerManager");
        try
        {
            CreateInstall(own);
            CreateInstall(shared);
            File.WriteAllText(Path.Combine(shared, "notes.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(shared, Launcher.ExtractionFolderName, "Other"));

            InstallFolderTests.RunPowerShell(Installer.RemoveProgramFilesScript(own), Installer.FolderEnvironment(own));
            InstallFolderTests.RunPowerShell(Installer.RemoveProgramFilesScript(shared), Installer.FolderEnvironment(shared));

            Assert.False(Directory.Exists(own));
            Assert.Equal(new[] { Launcher.ExtractionFolderName, "notes.txt" },
                Directory.EnumerateFileSystemEntries(shared).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            // Only the app's own folder in .net goes.
            Assert.Equal(new[] { "Other" }, Directory.EnumerateFileSystemEntries(Path.Combine(shared, Launcher.ExtractionFolderName)).Select(Path.GetFileName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheRemovalScript_KnowsEveryOwnFile()
    {
        var script = Installer.RemoveProgramFilesScript(@"D:\Apps\RunnerManager");
        foreach (var pattern in Installer.OwnFilePatterns) Assert.Contains($"'{pattern}'", script);
        Assert.Contains(Launcher.FileName, Installer.OwnFilePatterns);
        Assert.Contains(Launcher.FileName + ".off", Installer.OwnFilePatterns);
        Assert.Contains("RunnerManagerLauncher.*.old.exe", Installer.OwnFilePatterns);
    }

    [JunctionFact]
    public void TheRemovalScript_RemovesLinksInWhatTheExeUnpacked_WithoutGoingIntoThem()
    {
        var root = InstallEvidenceTests.NewFolder("remove-helper-links");
        var own = Path.Combine(root, "Apps", "RunnerManager");
        var linkedRoot = Path.Combine(root, "Linked", "RunnerManager");
        var elsewhere = Path.Combine(root, "elsewhere");
        var inner = Path.Combine(own, Launcher.ExtractionFolderName, "RunnerManager", "build1", "link");
        var dotNet = Path.Combine(linkedRoot, Launcher.ExtractionFolderName);
        try
        {
            CreateInstall(own);
            Directory.CreateDirectory(linkedRoot);
            File.WriteAllText(Path.Combine(linkedRoot, AppPaths.ExeName), "x");
            Directory.CreateDirectory(Path.Combine(elsewhere, "RunnerManager"));
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(elsewhere, "RunnerManager", "keep.dll"), "keep");
            Junctions.Create(inner, elsewhere);
            Junctions.Create(dotNet, elsewhere);

            InstallFolderTests.RunPowerShell(Installer.RemoveProgramFilesScript(own), Installer.FolderEnvironment(own));
            InstallFolderTests.RunPowerShell(Installer.RemoveProgramFilesScript(linkedRoot), Installer.FolderEnvironment(linkedRoot));

            Assert.False(Directory.Exists(own));
            Assert.False(Directory.Exists(linkedRoot));
            Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(elsewhere, "RunnerManager", "keep.dll")));
        }
        finally
        {
            Junctions.Delete(inner, dotNet);
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// Whether programs without administrator rights can change the install folder (see <see cref="InstallFolderSafety"/>),
/// read from real temp folders whose access lists the tests change.
/// </summary>
public class InstallFolderSafetyTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>Trusted as Runner Manager trusts, and the user who runs the tests, who owns the test folders.</summary>
    private static bool Trusted(SecurityIdentifier sid) => InstallFolderSafety.IsTrusted(sid) || sid == Me;

    /// <summary>A folder in %TEMP% that only Administrators, SYSTEM and the user who runs the tests can change.</summary>
    private static string ProtectedFolder()
    {
        var folder = InstallEvidenceTests.NewFolder("acl");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Me, Administrators, System })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);
        return folder;
    }

    private static void Allow(string folder, SecurityIdentifier sid, FileSystemRights rights, bool inheritOnly = false)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(inheritOnly
            ? new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow)
            : new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private static string? FolderProblem(string folder)
        => InstallFolderSafety.ProblemOf(InstallFolderSafety.Read(folder, isFolder: true, InstallFolderSafety.FolderRights), Trusted);

    [Fact]
    public void AFolderThatOnlyAdministratorsAndItsOwnerCanChange_IsSafe()
    {
        var folder = ProtectedFolder();
        try { Assert.Null(FolderProblem(folder)); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AFolderThatUsersCanWriteTo_IsNotSafe_ButOneTheyCanOnlyRead_Is()
    {
        var folder = ProtectedFolder();
        try
        {
            Allow(folder, Users, FileSystemRights.ReadAndExecute);
            Assert.Null(FolderProblem(folder));
            Allow(folder, Users, FileSystemRights.Write);
            var problem = FolderProblem(folder);
            Assert.NotNull(problem);
            Assert.EndsWith($"can change {folder}", problem);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RightsThatOnlyPassOnToWhatIsCreatedInside_CountForThoseFiles()
    {
        var folder = ProtectedFolder();
        try
        {
            Allow(folder, Users, FileSystemRights.Modify, inheritOnly: true);
            Allow(folder, CreatorOwner, FileSystemRights.FullControl, inheritOnly: true);
            Assert.Null(FolderProblem(folder));
            var exe = Path.Combine(folder, AppPaths.ExeName);
            File.WriteAllText(exe, string.Empty);
            Assert.NotNull(InstallFolderSafety.ProblemOf(InstallFolderSafety.Read(exe, isFolder: false, InstallFolderSafety.FileRights), Trusted));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AnExeThatUsersCanDelete_IsNotSafe()
    {
        var folder = ProtectedFolder();
        try
        {
            var exe = Path.Combine(folder, Launcher.FileName);
            File.WriteAllText(exe, string.Empty);
            FileProblem(exe, expectProblem: false);
            var info = new FileInfo(exe);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Delete, AccessControlType.Allow));
            info.SetAccessControl(security);
            FileProblem(exe, expectProblem: true);
        }
        finally { Directory.Delete(folder, recursive: true); }

        static void FileProblem(string file, bool expectProblem)
            => Assert.Equal(expectProblem, InstallFolderSafety.ProblemOf(InstallFolderSafety.Read(file, isFolder: false, InstallFolderSafety.FileRights), Trusted) != null);
    }

    [Fact]
    public void AFolderAbove_CountsOnlyForRemovingOrRenamingWhatIsInIt()
    {
        var parent = ProtectedFolder();
        try
        {
            // Users may create folders there, as in C:\, which does not let them change what is in them.
            Allow(parent, Users, FileSystemRights.AppendData | FileSystemRights.WriteData);
            Assert.Null(InstallFolderSafety.ProblemOf(InstallFolderSafety.Read(parent, isFolder: true, InstallFolderSafety.AncestorRights), Trusted));
            Allow(parent, Users, FileSystemRights.DeleteSubdirectoriesAndFiles);
            Assert.NotNull(InstallFolderSafety.ProblemOf(InstallFolderSafety.Read(parent, isFolder: true, InstallFolderSafety.AncestorRights), Trusted));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void AnOwnerThatIsNotTrusted_CanAlwaysChangeTheRights()
    {
        var acl = new InstallFolderSafety.Acl(@"D:\Apps\RunnerManager", Users, Array.Empty<InstallFolderSafety.Entry>(), InstallFolderSafety.FolderRights);
        Assert.Equal($"{Users.Translate(typeof(NTAccount)).Value} owns D:\\Apps\\RunnerManager", InstallFolderSafety.ProblemOf(acl, Trusted));
        Assert.Equal(@"the owner of D:\Apps\RunnerManager could not be read", InstallFolderSafety.ProblemOf(acl with { Owner = null }, Trusted));
        Assert.Null(InstallFolderSafety.ProblemOf(acl with { Owner = Administrators }, Trusted));
        // Denying entries and entries of trusted accounts are not counted.
        var entries = new[]
        {
            new InstallFolderSafety.Entry(Users, unchecked((int)FileSystemRights.FullControl), Allow: false, InheritOnly: false),
            new InstallFolderSafety.Entry(System, unchecked((int)FileSystemRights.FullControl), Allow: true, InheritOnly: false)
        };
        Assert.Null(InstallFolderSafety.ProblemOf(acl with { Owner = Administrators, Entries = entries }, Trusted));
    }

    [Fact]
    public void TheWholeWayDown_IsChecked()
    {
        using var folder = new HelperFolder();
        var objects = InstallFolderSafety.Objects(folder.Path);
        Assert.Equal((folder.Path, true, InstallFolderSafety.FolderRights), objects[0]);
        Assert.Contains((folder.Exe, false, InstallFolderSafety.FileRights), objects);
        Assert.Contains((folder.Helper, false, InstallFolderSafety.FileRights), objects);
        Assert.Contains((Path.GetDirectoryName(folder.Path)!, true, InstallFolderSafety.AncestorRights), objects);
        // No one can remove the root itself.
        Assert.Equal((Path.GetPathRoot(folder.Path)!, true, InstallFolderSafety.AncestorRights & ~0x10000), objects[^1]);
        folder.RemoveHelper();
        Assert.DoesNotContain(InstallFolderSafety.Objects(folder.Path), o => o.Path == folder.Helper);
    }

    [Fact]
    public void AWindowsFolder_IsSafe_AndAFolderInTheUsersProfile_IsNot()
    {
        Assert.Null(InstallFolderSafety.Check(Environment.SystemDirectory));
        var folder = InstallEvidenceTests.NewFolder("profile");
        try { Assert.NotNull(InstallFolderSafety.Check(folder)); }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void TheTextsForTheUser_SayStartHelper_InPlainWords()
    {
        Assert.Equal($"Runner Manager is installed in a folder that programs without administrator rights can change. To move it, run setup again and choose {MachineInstall.ProgramFilesDir}.",
            InstallFolderSafety.UnsafeActivityText);
        foreach (var text in new[]
                 {
                     InstallFolderSafety.UnsafeActivityText, InstallFolderSafety.UnsafeNoteText, InstallFolderSafety.WithoutStartHelperActivityText,
                     InstallFolderSafety.WithoutStartHelperNoteText, Launcher.OffLogText("it was removed right after it was written"), MachineRelay.RestartingNowText
                 })
        {
            Assert.DoesNotContain("launcher", text.Replace(Launcher.FileName, string.Empty), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("n't", text);
        }
        Assert.Contains("start helper", InstallFolderSafety.WithoutStartHelperActivityText);
        Assert.Contains(Launcher.FileName, InstallFolderSafety.WithoutStartHelperActivityText);
    }
}

/// <summary>The build: the start helper in the app, and the release's requirement that it be there.</summary>
public class StartHelperBuildTests
{
    [Fact]
    public void TheAppCarriesTheStartHelperThatWasBuilt()
    {
        // Copied next to the tests by the test project from the same build output the app embeds.
        var built = Path.Combine(AppContext.BaseDirectory, "launcher", Launcher.FileName);
        var embedded = Launcher.ReadEmbedded();
        if (!File.Exists(built))
        {
            // Without the Visual C++ tools there is none, and the app registers its tasks at RunnerManager.exe.
            Assert.Null(embedded);
            return;
        }
        Assert.NotNull(embedded);
        Assert.Equal(File.ReadAllBytes(built), embedded);
    }

    private static (int ExitCode, string Output) Dotnet(params string[] arguments)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(180_000), "dotnet msbuild did not finish within three minutes.");
        return (process.ExitCode, output + error.Result);
    }

    [Fact]
    public void AReleaseBuild_StopsWithoutTheStartHelper_AnyOtherBuildGoesOn()
    {
        var project = Path.Combine(InstallEvidenceTests.SourceDir, "RunnerManager.csproj");
        var missing = Path.Combine(Path.GetTempPath(), $"rm-no-helper-{Guid.NewGuid():N}", Launcher.FileName);
        string[] Arguments(params string[] more) =>
            new[] { "msbuild", project, "-t:CheckLauncher", $"-p:LauncherExe={missing}", "-nologo", "-nodeReuse:false", "-v:m" }.Concat(more).ToArray();

        var (release, output) = Dotnet(Arguments("-p:RequireLauncher=true"));
        Assert.NotEqual(0, release);
        Assert.Contains("RequireLauncher is set, but the start helper", output);

        var (other, otherOutput) = Dotnet(Arguments());
        Assert.True(other == 0, otherOutput);
    }

    [Theory]
    [InlineData(@"D:\dev\state", false, null, @"D:\dev\state")]
    [InlineData(@"D:\dev\state", true, null, null)]
    [InlineData(null, false, null, null)]
    [InlineData("  ", false, null, null)]
    [InlineData(@"D:\dev\state", true, @"C:\tests\state", @"C:\tests\state")]
    [InlineData(null, true, @"C:\tests\state", @"C:\tests\state")]
    public void ADataFolderFromTheEnvironment_CountsOnlyWithoutAdministratorRights(string? variable, bool elevated, string? testStateDir, string? expected)
        => Assert.Equal(expected, AppPaths.StateDirOverride(variable, elevated, testStateDir));
}
