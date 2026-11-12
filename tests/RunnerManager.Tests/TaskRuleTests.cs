using System.IO;
using System.Security.Principal;

namespace RunnerManager.Tests;

/// <summary>Small rules about the tasks: starting one again, and tasks of other Windows users.</summary>
[Collection(InstallStateCollection.Name)]
public class TaskRuleTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static string Agent => ScheduledTasks.AgentTaskName;

    private sealed class Pause : IDisposable
    {
        private readonly Func<TimeSpan, Task> _before = ScheduledTasks.RetryPause;

        public Pause() => ScheduledTasks.RetryPause = _ => Task.Delay(20);

        public void Dispose() => ScheduledTasks.RetryPause = _before;
    }

    [Fact]
    public async Task ATaskThatItsOwnTriggerStartedMeanwhile_CountsAsStarted()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Apps\RunnerManager\RunnerManagerLauncher.exe");
        state.Tasks.Running.Add(Agent);
        using var pause = new Pause();

        var (code, output) = await ScheduledTasks.StartAgainAsync(Agent, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));

        Assert.Equal(0, code);
        Assert.Equal($"The scheduled task \"{Agent}\" runs: it was started again meanwhile.", output);
        Assert.Empty(state.Tasks.Calls);
    }

    [Fact]
    public async Task ATaskThatRunsOnlyForAMoment_DoesNotCountAsStarted()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Apps\RunnerManager\RunnerManagerLauncher.exe");
        state.Tasks.Running.Add(Agent);
        using var pause = new Pause();

        // It still runs at the end, but not for as long as it takes to count as started.
        var (code, _) = await ScheduledTasks.StartAgainAsync(Agent, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(15));

        Assert.NotEqual(0, code);
        Assert.Empty(state.Tasks.Calls);
    }

    [Fact]
    public async Task OnlyATaskOfAnotherUserWithTheHighestRights_BelongsToThatUser()
    {
        using var state = new TestInstallState();
        const string other = "S-1-5-21-1-2-3-1005";
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(other, @"C:\Program Files\RunnerManager\RunnerManager.exe", highest: false);
        Assert.Null(await ScheduledTasks.OtherOwnerAsync(Agent));

        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(other, @"C:\Program Files\RunnerManager\RunnerManager.exe");
        Assert.NotNull(await ScheduledTasks.OtherOwnerAsync(Agent));

        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, @"C:\Program Files\RunnerManager\RunnerManager.exe");
        Assert.Null(await ScheduledTasks.OtherOwnerAsync(Agent));
    }

    [Fact]
    public async Task AnAgentTaskWhoseExeIsGone_GetsTheInstalledExe_NeverThisProcess()
    {
        using var state = new TestInstallState();
        var gone = Path.Combine(Path.GetTempPath(), $"rm-gone-{Guid.NewGuid():N}", AppPaths.ExeName);
        // Without the always-on settings, so it needs a repair.
        state.Tasks.Definitions[Agent] = InstallEvidenceTests.TaskOf(Me, gone)
            .Replace("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>");

        var repaired = await MachineRelay.RepairAgentTaskSettingsAsync();

        var command = ScheduledTasks.CommandOfOwnElevatedTask(state.Tasks.Definitions[Agent]!);
        Assert.NotEqual(Environment.ProcessPath, command);
        if (File.Exists(AppUpdater.InstalledExe))
        {
            Assert.True(repaired);
            Assert.Equal(AppUpdater.InstalledExe, Launcher.ExeOf(command!), ignoreCase: true);
        }
        else
        {
            Assert.False(repaired);
            Assert.Equal(gone, command);
            Assert.Empty(state.Tasks.Calls);
        }
    }
}

/// <summary>A folder that a running program has loaded a DLL from counts as in use (see <see cref="Launcher.IsInUse"/>).</summary>
public class UnpackFolderInUseTests
{
    [Fact]
    public void AFolderWithALoadedDll_IsInUse_AndIsKept()
    {
        using var folder = new HelperFolder();
        var build = Path.Combine(folder.Path, Launcher.ExtractionFolderName, "RunnerManager", "earlier-build");
        Directory.CreateDirectory(build);
        var dll = Path.Combine(build, $"rm-test-{Guid.NewGuid():N}.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "msimg32.dll"), dll);
        var module = System.Runtime.InteropServices.NativeLibrary.Load(dll);
        try
        {
            Assert.True(Launcher.IsInUse(new DirectoryInfo(build)));
            Launcher.RemoveOldExtractions(folder.Path, Array.Empty<string>());
            Assert.True(File.Exists(dll));
        }
        finally { System.Runtime.InteropServices.NativeLibrary.Free(module); }

        Assert.False(Launcher.IsInUse(new DirectoryInfo(build)));
        Launcher.RemoveOldExtractions(folder.Path, Array.Empty<string>());
        Assert.False(Directory.Exists(build));
    }
}
