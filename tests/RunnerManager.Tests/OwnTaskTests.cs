using System.IO;
using System.Security.Principal;

namespace RunnerManager.Tests;

/// <summary>
/// Code with administrator rights takes a scheduled task under one of Runner Manager's names as Runner Manager's only
/// when it is this user's own task with the highest rights. Any program of the user can create a task under a free name,
/// of any run level, but only a program with administrator rights can register one with the highest rights. These tests
/// use scheduled tasks in memory (see <see cref="TestInstallState"/>), never the real ones.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class OwnTaskTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private const string OtherSid = "S-1-5-21-1-2-3-1002";

    private static string TaskOf(string user, string exe, bool highest = true) => InstallEvidenceTests.TaskOf(user, exe, highest);

    /// <summary>A task as RunnerWidget 2.1.0 registered it: with the highest rights, below normal priority, without the keep-alive trigger.</summary>
    private static string RunnerWidgetTask(string exe) => TaskOf(Me, exe).Replace("<Priority>4</Priority>", "<Priority>7</Priority>");

    /// <summary>What the test wrote to the fleet agent's log since <paramref name="from"/> (see <see cref="LogLength"/>).</summary>
    private static string LogSince(long from)
    {
        var text = TestLogs.AgentLog();
        return from <= text.Length ? text[(int)from..] : text;
    }

    private static long LogLength() => TestLogs.AgentLog().Length;

    private sealed class Folder : IDisposable
    {
        public Folder()
        {
            Path = InstallEvidenceTests.NewFolder("own-task");
            Exe = System.IO.Path.Combine(Path, AppPaths.ExeName);
            File.WriteAllText(Exe, string.Empty);
        }

        public string Path { get; }
        public string Exe { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void OneRule_ThisUsersTask_WithTheHighestRights()
    {
        const string exe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
        Assert.True(ScheduledTasks.IsOwnElevatedTask(TaskOf(Me, exe)));
        Assert.False(ScheduledTasks.IsOwnElevatedTask(TaskOf(Me, exe, highest: false)));
        Assert.False(ScheduledTasks.IsOwnElevatedTask(TaskOf(OtherSid, exe)));
        Assert.False(ScheduledTasks.IsOwnElevatedTask(string.Empty));
        // RunnerWidget 2.1.0 registered its tasks with the highest rights too.
        Assert.True(ScheduledTasks.IsOwnElevatedTask(RunnerWidgetTask(Path.Combine(MachineInstall.OldDefaultDir, AppPaths.LegacyExeName))));
    }

    [Fact]
    public async Task AnAgentTaskWithoutTheHighestRights_IsNoFleetAgent()
    {
        // On a PC where the owner never turned the fleet on, a program creates a task under the fleet agent's name.
        using var state = new TestInstallState();
        using var folder = new Folder();
        var log = LogLength();
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, folder.Exe, highest: false);
        // Setup, updates and the dashboard (also its check after the start, which starts a stopped agent and updates an
        // outdated one) all ask this first, so none of them starts it or registers the fleet agent in its place.
        Assert.False(await MachineRelay.IsAgentInstalledAsync());
        Assert.Equal(string.Empty, await MachineRelay.AgentTaskExecutableAsync());
        Assert.Contains(ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.AgentTaskName), LogSince(log));
        // Leaving the fleet and uninstall leave it as it is.
        await MachineRelay.RemoveOwnAgentTasksAsync();
        Assert.Empty(state.Tasks.Calls);
        Assert.True(state.Tasks.Definitions.ContainsKey(ScheduledTasks.AgentTaskName));
        Assert.False(await ScheduledTasks.RepairIfUnindexedAsync(ScheduledTasks.AgentTaskName));

        // Another Windows user's fleet agent is not this user's either.
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(OtherSid, folder.Exe);
        MachineRelay.ForgetAgentInstalled();
        Assert.False(await MachineRelay.IsAgentInstalledAsync());
        await MachineRelay.RemoveOwnAgentTasksAsync();
        Assert.Empty(state.Tasks.Calls);

        // This user's own fleet agent, as Runner Manager registers it.
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, folder.Exe);
        MachineRelay.ForgetAgentInstalled();
        Assert.True(await MachineRelay.IsAgentInstalledAsync());
        Assert.Equal(folder.Exe, await MachineRelay.AgentTaskExecutableAsync());
        await MachineRelay.RemoveOwnAgentTasksAsync();
        Assert.Equal(new[] { $"End {ScheduledTasks.AgentTaskName}", $"Delete {ScheduledTasks.AgentTaskName}" }, state.Tasks.Calls);
    }

    [Fact]
    public async Task RunnerWidgetTasks_OfAnotherProgram_AreNotMoved()
    {
        // The dashboard's check runs again when %LOCALAPPDATA%\RunnerWidget is there, which any program can create.
        using var state = new TestInstallState();
        using var folder = new Folder();
        var legacyExe = Path.Combine(folder.Path, "old", AppPaths.LegacyExeName);
        var log = LogLength();
        state.Tasks.Definitions[ScheduledTasks.LegacyAgentTaskName] = TaskOf(Me, legacyExe, highest: false);
        state.Tasks.Definitions[ScheduledTasks.LegacyDashboardTaskName] = TaskOf(Me, legacyExe, highest: false);

        await LegacyInstall.MigrateAsync(_ => { }, fromSetup: true, folder.Exe, legacyExe, Path.Combine(folder.Path, "RunnerWidget"));

        Assert.Empty(state.Tasks.Calls);
        Assert.False(state.Tasks.Definitions.ContainsKey(ScheduledTasks.AgentTaskName));
        var written = LogSince(log);
        Assert.Contains(ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.LegacyAgentTaskName), written);
        Assert.Contains(ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.LegacyDashboardTaskName), written);
    }

    [Fact]
    public async Task ARunnerWidgetInstall_StillMovesItsTasks()
    {
        using var state = new TestInstallState();
        using var folder = new Folder();
        var legacyExe = Path.Combine(folder.Path, AppPaths.LegacyExeName);
        File.WriteAllText(legacyExe, string.Empty);
        state.Tasks.Definitions[ScheduledTasks.LegacyAgentTaskName] = RunnerWidgetTask(legacyExe);
        state.Tasks.Definitions[ScheduledTasks.LegacyDashboardTaskName] = RunnerWidgetTask(legacyExe);
        var log = new List<string>();

        await LegacyInstall.MigrateAsync(log.Add, fromSetup: true, folder.Exe, legacyExe, Path.Combine(folder.Path, "RunnerWidget"));

        Assert.Equal(new[]
        {
            $"End {ScheduledTasks.LegacyAgentTaskName}", $"Delete {ScheduledTasks.LegacyAgentTaskName}",
            $"Create {ScheduledTasks.AgentTaskName}", $"Run {ScheduledTasks.AgentTaskName}", $"Delete {ScheduledTasks.LegacyDashboardTaskName}"
        }, state.Tasks.Calls);
        Assert.Equal(folder.Exe, ScheduledTasks.ExeOfOwnElevatedTask(state.Tasks.Definitions[ScheduledTasks.AgentTaskName]!));
        Assert.False(File.Exists(legacyExe));
        Assert.Equal("Install moved to Runner Manager.", log.Last());
    }

    [Fact]
    public async Task AStartupTaskOfAnotherProgram_IsNeitherRegisteredAgainNorCounted()
    {
        using var state = new TestInstallState();
        using var folder = new Folder();
        var log = LogLength();
        // Below normal priority and another exe: the startup task of this user would be registered again.
        var planted = TaskOf(Me, @"C:\Users\me\Downloads\RunnerManager.exe", highest: false).Replace("<Priority>4</Priority>", "<Priority>7</Priority>");
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = planted;

        Assert.False(Installer.StartupTaskNeedsRegistering(planted, folder.Exe));
        Assert.True(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Empty(state.Tasks.Calls);
        Assert.Contains(ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.DashboardTaskName), LogSince(log));
        // Setup does not take it for "Start with Windows" either, so it does not register the startup task for it.
        Assert.False(await Installer.StartsWithWindowsAsync());

        // This user's own startup task of an older version is registered again for the installed exe.
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, @"D:\Old\RunnerManager.exe");
        Assert.True(await Installer.StartsWithWindowsAsync());
        Assert.True(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Equal(new[] { $"Create {ScheduledTasks.DashboardTaskName}" }, state.Tasks.Calls);
        Assert.Equal(folder.Exe, ScheduledTasks.ExeOfOwnElevatedTask(state.Tasks.Definitions[ScheduledTasks.DashboardTaskName]!));

        // A task that cannot be read is unknown, so setup leaves the startup task as it is.
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = null;
        Assert.Null(await Installer.StartsWithWindowsAsync());
    }

    [Fact]
    public async Task TheSharedTasksOf23_MoveOnlyAsThisUsersOwnWithTheHighestRights()
    {
        using var state = new TestInstallState();
        using var folder = new Folder();
        state.Tasks.Definitions[ScheduledTasks.SharedDashboardTaskName] = TaskOf(Me, folder.Exe, highest: false);
        state.Tasks.Definitions[ScheduledTasks.SharedLaunchTaskName] = TaskOf(Me, folder.Exe, highest: false);
        Assert.True(await Installer.MigrateSharedTasksAsync(folder.Exe));
        Assert.Empty(state.Tasks.Calls);
        Assert.False(state.Tasks.Definitions.ContainsKey(ScheduledTasks.DashboardTaskName));

        state.Tasks.Definitions[ScheduledTasks.SharedDashboardTaskName] = TaskOf(Me, folder.Exe);
        state.Tasks.Definitions[ScheduledTasks.SharedLaunchTaskName] = TaskOf(Me, folder.Exe);
        Assert.True(await Installer.MigrateSharedTasksAsync(folder.Exe));
        Assert.Equal(new[]
        {
            $"Create {ScheduledTasks.DashboardTaskName}", $"Delete {ScheduledTasks.SharedDashboardTaskName}", $"Delete {ScheduledTasks.SharedLaunchTaskName}"
        }, state.Tasks.Calls);
    }

    [Fact]
    public async Task Uninstall_DeletesOnlyThisUsersOwnTasks()
    {
        using var state = new TestInstallState();
        using var folder = new Folder();
        var log = LogLength();
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, folder.Exe);
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = TaskOf(Me, folder.Exe, highest: false);
        // The names of RunnerWidget and of 2.3 and earlier were the same for every Windows user.
        state.Tasks.Definitions[ScheduledTasks.LegacyDashboardTaskName] = TaskOf(OtherSid, folder.Exe);
        state.Tasks.Definitions[ScheduledTasks.SharedDashboardTaskName] = TaskOf(OtherSid, folder.Exe);
        state.Tasks.Definitions[ScheduledTasks.SharedLaunchTaskName] = TaskOf(Me, folder.Exe);

        await Installer.RemoveOwnDashboardTasksAsync();

        Assert.Equal(new[] { $"Delete {ScheduledTasks.DashboardTaskName}", $"Delete {ScheduledTasks.SharedLaunchTaskName}" }, state.Tasks.Calls);
        Assert.Equal(new[] { ScheduledTasks.LaunchTaskName, ScheduledTasks.LegacyDashboardTaskName, ScheduledTasks.SharedDashboardTaskName }.Order(StringComparer.Ordinal),
            state.Tasks.Definitions.Keys.Order(StringComparer.Ordinal));
        var written = LogSince(log);
        Assert.Contains(ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.LaunchTaskName), written);
        Assert.Contains($"The scheduled task \"{ScheduledTasks.LegacyDashboardTaskName}\" belongs to {MachineInstall.AccountOf(OtherSid)}, so Runner Manager left it as it is.", written);
    }

    [Fact]
    public async Task OnlyThisUsersOwnTask_IsRepairedWhenWindowsCannotStartIt()
    {
        using var state = new TestInstallState();
        using var folder = new Folder();
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, folder.Exe, highest: false);
        state.Tasks.Unindexed.Add(ScheduledTasks.DashboardTaskName);
        Assert.False(await ScheduledTasks.RepairIfUnindexedAsync(ScheduledTasks.DashboardTaskName));
        Assert.Empty(state.Tasks.Calls);

        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, folder.Exe);
        Assert.True(await ScheduledTasks.RepairIfUnindexedAsync(ScheduledTasks.DashboardTaskName));
        Assert.Equal(new[] { $"Delete {ScheduledTasks.DashboardTaskName}", $"Create {ScheduledTasks.DashboardTaskName}" }, state.Tasks.Calls);
    }

    [Fact]
    public async Task AFailedMove_PointsTheStartupAndAgentTasksBackAtTheOldCopy()
    {
        // Setup had pointed both tasks at the new folder before the move failed; the new copy is then removed.
        using var state = new TestInstallState();
        using var old = new Folder();
        const string newFolder = @"C:\Program Files\RunnerManager";
        var newExe = Path.Combine(newFolder, AppPaths.ExeName);
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, newExe);
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, newExe);
        var agentPointedAt = new List<string>();

        await Installer.PointTasksBackAsync(old.Exe, newFolder, exe => { agentPointedAt.Add(exe); return Task.CompletedTask; });

        Assert.Equal(old.Exe, ScheduledTasks.ExeOfOwnElevatedTask(state.Tasks.Definitions[ScheduledTasks.DashboardTaskName]!));
        Assert.Equal(new[] { old.Exe }, agentPointedAt);

        // Tasks that start another copy, and tasks that are not this user's own with the highest rights, stay as they are.
        state.Tasks.Calls.Clear();
        agentPointedAt.Clear();
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(Me, newExe, highest: false);
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, @"D:\Other\RunnerManager.exe");
        await Installer.PointTasksBackAsync(old.Exe, newFolder, exe => { agentPointedAt.Add(exe); return Task.CompletedTask; });
        Assert.Empty(state.Tasks.Calls);
        Assert.Empty(agentPointedAt);
    }

    [Fact]
    public void ALaunchTaskWithoutTheHighestRights_IsNotUsed_AndIsRegisteredAgain()
    {
        // It would start the dashboard without administrator rights.
        const string exe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
        var planted = TaskOf(Me, exe, highest: false);
        Assert.False(Elevation.IsUsableLaunchTask(planted, exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(planted, exe));
        Assert.True(Elevation.IsUsableLaunchTask(TaskOf(Me, exe), exe));
    }
}
