using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace RunnerManager.Tests;

/// <summary>
/// A dashboard that one of this user's tasks started as RunnerManager.exe itself starts again through the start helper,
/// once, so the task can move to it (see <see cref="StartHelperRestart"/>).
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperRestartTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static string Launch => ScheduledTasks.LaunchTaskName;
    private static string Startup => ScheduledTasks.DashboardTaskName;

    /// <summary>What the restart starts, recorded; each start runs a harmless program of its own instead, ended when disposed.</summary>
    private sealed class Starts : IDisposable
    {
        private readonly Func<ProcessStartInfo, Process?> _before = StartHelperRestart.StartProcess;
        private readonly List<Process> _programs = new();

        public Starts()
            => StartHelperRestart.StartProcess = info =>
            {
                Seen.Add(info);
                return Answer(info);
            };

        public List<ProcessStartInfo> Seen { get; } = new();

        /// <summary>What a start gives: by default a program that keeps running, as a start helper does while its dashboard runs.</summary>
        public Func<ProcessStartInfo, Process?> Answer { get; set; } = _ => null;

        public Process Run(string exe, params string[] arguments)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var program = Process.Start(start)!;
            _programs.Add(program);
            // The restart disposes what it gets, so it gets an object of its own.
            return Process.GetProcessById(program.Id);
        }

        public Process KeepsRunning() => Run(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n", "30", "127.0.0.1");

        public void Dispose()
        {
            StartHelperRestart.StartProcess = _before;
            foreach (var program in _programs)
            {
                try { program.Kill(); } catch (InvalidOperationException) { }
                program.WaitForExit(5000);
                program.Dispose();
            }
        }
    }

    private static string Disabled(string xml) => Regex.Replace(xml, @"<Enabled>true</Enabled>(\s*)<Hidden>", "<Enabled>false</Enabled>$1<Hidden>");

    /// <summary>The probes of this test's own start helper: work that an earlier test left running may probe its own.</summary>
    private static int ProbesOf(ProofSeams seams, HelperFolder folder)
    {
        lock (seams.Probes) return seams.Probes.Count(p => p.Equals(folder.Helper, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheDecision_NeedsEveryFact_AndReadsEachOneOnlyWhileAllBeforeItHeld()
    {
        for (var facts = 0; facts < 64; facts++)
        {
            var bits = facts;
            bool Fact(int i) => (bits & (1 << i)) != 0;
            var read = new List<string>();
            var restarts = await StartHelperRestart.RestartsAsync(Fact(0),
                () => { read.Add("started by the start helper"); return Fact(1); },
                () => { read.Add("decides"); return Fact(2); },
                () => { read.Add("update"); return Fact(3); },
                () => { read.Add("task"); return Task.FromResult(Fact(4)); },
                () => { read.Add("start helper works"); return Task.FromResult(Fact(5)); });

            Assert.Equal(!Fact(0) && !Fact(1) && Fact(2) && !Fact(3) && Fact(4) && Fact(5), restarts);
            var expected = new List<string>();
            if (!Fact(0))
            {
                expected.Add("started by the start helper");
                if (!Fact(1))
                {
                    expected.Add("decides");
                    if (Fact(2))
                    {
                        expected.Add("update");
                        if (!Fact(3))
                        {
                            expected.Add("task");
                            if (Fact(4)) expected.Add("start helper works");
                        }
                    }
                }
            }
            Assert.Equal(expected, read);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADashboardThatItsTaskStartedAsTheExe_StartsAgainThroughTheStartHelper_WithTheSameArguments(bool launchTask)
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var starts = new Starts();
        starts.Answer = _ => starts.KeepsRunning();
        var task = launchTask ? Launch : Startup;
        state.Tasks.Definitions[task] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Add(task);
        var log = new List<string>();
        string[] args = launchTask ? [App.FromTaskArgument] : ["--minimized", App.AfterArgument + "4242"];

        Assert.True(await StartHelperRestart.TryAsync(args, folder.Path, log.Add));

        var start = Assert.Single(starts.Seen);
        Assert.Equal(folder.Helper, start.FileName, ignoreCase: true);
        var same = args.Where(a => !a.StartsWith(App.AfterArgument, StringComparison.Ordinal));
        Assert.Equal(same.Append(App.AfterArgument + Environment.ProcessId).Append(StartHelperRestart.Argument), start.ArgumentList);
        // It saw the start helper work first, and leaves the task to the new dashboard.
        Assert.Equal(1, ProbesOf(seams, folder));
        Assert.Empty(state.Tasks.Calls);
        Assert.Contains(log, line => line.Contains($"\"{task}\"") && line.Contains("through the start helper"));
    }

    [Fact]
    public async Task TheRestartedDashboard_NeverRestartsAgain()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var starts = new Starts();
        starts.Answer = _ => starts.KeepsRunning();
        state.Tasks.Definitions[Launch] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Add(Launch);
        Assert.True(await StartHelperRestart.TryAsync([App.FromTaskArgument], folder.Path, _ => { }));
        var restarted = Assert.Single(starts.Seen).ArgumentList.ToArray();

        // The task still counts as running, and even a dashboard that does not know that the start helper started it
        // does not restart again: the argument stops it.
        Assert.False(await StartHelperRestart.TryAsync(restarted, folder.Path, _ => { }));
        // A start through the start helper does not restart either, without the argument.
        Launcher.StartedFrom = path => path == folder.Path;
        Assert.False(await StartHelperRestart.TryAsync([App.FromTaskArgument], folder.Path, _ => { }));

        Assert.Single(starts.Seen);
        Assert.Equal(1, ProbesOf(seams, folder));
    }

    [Fact]
    public async Task ADashboard_DoesNotRestart_WhenItsTaskDoesNotStartTheExeItselfNowOrTheStartHelperIsNotUsable()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var starts = new Starts();
        starts.Answer = _ => starts.KeepsRunning();
        async Task NoRestart(bool probes)
        {
            var before = ProbesOf(seams, folder);
            Assert.False(await StartHelperRestart.TryAsync([], folder.Path, _ => { }));
            Assert.Empty(starts.Seen);
            Assert.True(probes == ProbesOf(seams, folder) > before, $"Probed: {string.Join(", ", seams.Probes)}");
            Launcher.ForgetProbes();
        }

        // A task that starts the start helper already; one that starts the exe but does not run; a turned-off startup task.
        state.Tasks.Definitions[Launch] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        state.Tasks.Running.Add(Launch);
        await NoRestart(probes: false);
        state.Tasks.Definitions[Launch] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Clear();
        await NoRestart(probes: false);
        state.Tasks.Definitions.Remove(Launch);
        state.Tasks.Definitions[Startup] = Disabled(InstallEvidenceTests.TaskOf(Me, folder.Exe));
        state.Tasks.Running.Add(Startup);
        await NoRestart(probes: false);

        // From here a running task starts the exe itself.
        state.Tasks.Definitions[Startup] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        // An update holds the update lock: nothing is probed.
        Launcher.UnderUpdateLock = (_, _) => null;
        await NoRestart(probes: false);
        Launcher.UnderUpdateLock = (_, write) => write();
        // An earlier copy that still runs after an update.
        Launcher.Decides = () => false;
        await NoRestart(probes: false);
        Launcher.Decides = () => true;
        // This PC runs without the start helper.
        Launcher.TurnOff(folder.Path, AppUpdater.CurrentVersion, "it was removed right after it was written");
        await NoRestart(probes: false);
        File.Delete(Launcher.OffMarkerPath(folder.Path));
        // The start helper does not start Runner Manager: tried twice, and the dashboard carries on.
        seams.ProbeResult = helper => helper == folder.Helper ? "it did not start Runner Manager (exit code 0x524C0005)" : null;
        await NoRestart(probes: true);
        Assert.Equal(1, seams.Pauses);
        seams.ProbeResult = _ => null;
        // The restart only ever goes through the start helper: when a start would run the exe itself, there is none.
        var installed = Launcher.IsInstalledExe;
        Launcher.IsInstalledExe = _ => false;
        try { await NoRestart(probes: true); }
        finally { Launcher.IsInstalledExe = installed; }

        // Otherwise it restarts.
        Assert.True(await StartHelperRestart.TryAsync([], folder.Path, _ => { }));
    }

    [Fact]
    public async Task WhenTheStartHelperCannotBeStartedOrStopsAtOnce_TheDashboardCarriesOn()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var starts = new Starts();
        state.Tasks.Definitions[Launch] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Add(Launch);
        var log = new List<string>();

        starts.Answer = _ => throw new Win32Exception(5);
        Assert.False(await StartHelperRestart.TryAsync([], folder.Path, log.Add));
        Assert.Contains(log, line => line.StartsWith("Could not start Runner Manager again through the start helper", StringComparison.Ordinal));

        starts.Answer = _ => starts.Run(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/c", "exit 5");
        Assert.False(await StartHelperRestart.TryAsync([], folder.Path, log.Add));
        Assert.Contains(log, line => line.Contains("exited right away (exit code 0x00000005)"));
        Assert.Equal(2, starts.Seen.Count);
    }

    [Fact]
    public async Task AfterTheRestart_TheTaskMovesToTheStartHelper_OnceTheInstanceBeforeEnded()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        // The restarted dashboard, which the start helper started.
        Launcher.StartedFrom = path => path == folder.Path;
        state.Tasks.Definitions[Launch] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Add(Launch);
        var log = new List<string>();

        var wait = StartHelperRestart.WaitForTasksAsync(folder.Exe, TimeSpan.FromSeconds(20), log.Add);
        await Task.Delay(600);
        Assert.False(wait.IsCompleted);
        // The dashboard before has exited, and Task Scheduler ended the task's instance.
        state.Tasks.Running.Remove(Launch);
        Assert.True(await wait);

        // What the dashboard's start does next (Installer.EnsureLaunchTaskAsync) moves the task now.
        var xml = state.Tasks.Definitions[Launch]!;
        Assert.True(Installer.LaunchTaskNeedsRegistering(xml, folder.Exe));
        Assert.True(await Installer.RegisterIfIdleAsync(Launch, () => ScheduledTasks.RegisterLaunchTaskAsync(folder.Exe)));
        Assert.Equal(folder.Helper, ScheduledTasks.CommandOfOwnElevatedTask(state.Tasks.Definitions[Launch]!), ignoreCase: true);
        Assert.Empty(log);
    }

    [Fact]
    public async Task AfterTheRestart_ATaskThatStillRuns_IsLeftForALaterStart()
    {
        using var state = new TestInstallState();
        using var folder = new HelperFolder();
        state.Tasks.Definitions[Startup] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        state.Tasks.Running.Add(Startup);
        var log = new List<string>();

        Assert.False(await StartHelperRestart.WaitForTasksAsync(folder.Exe, TimeSpan.FromMilliseconds(500), log.Add));

        Assert.Equal($"The scheduled task \"{Startup}\" still runs, so it moves to the start helper later.", Assert.Single(log));
        Assert.False(await Installer.RegisterIfIdleAsync(Startup, () => Installer.RegisterStartupTaskAsync(folder.Exe)));
        Assert.Empty(state.Tasks.Calls);
    }
}
