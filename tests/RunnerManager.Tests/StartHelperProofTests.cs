using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace RunnerManager.Tests;

/// <summary>
/// The start helper's seams for one test, with the real check of whether it works (<see cref="Launcher.ProvenNow"/>):
/// this process was not started by it, and each probe is recorded and answered by <see cref="ProbeResult"/>. Puts back
/// the seams before it when disposed.
/// </summary>
internal sealed class ProofSeams : IDisposable
{
    private readonly Func<byte[]?> _embedded = Launcher.Embedded;
    private readonly Func<Task> _settle = Launcher.Settle;
    private readonly Func<TimeSpan, Func<Launcher.EnsureResult?>, Launcher.EnsureResult?> _lock = Launcher.UnderUpdateLock;
    private readonly Func<bool> _decides = Launcher.Decides;
    private readonly Func<string, bool> _proven = Launcher.IsProven;
    private readonly Func<string, bool> _startedFrom = Launcher.StartedFrom;
    private readonly Func<string, string?> _probe = Launcher.RunProbe;
    private readonly Func<Task> _pause = Launcher.ProbePause;

    public ProofSeams(byte[]? bytes)
    {
        Launcher.Embedded = () => bytes;
        Launcher.IsProven = Launcher.ProvenNow;
        Launcher.StartedFrom = _ => false;
        Launcher.RunProbe = helper =>
        {
            lock (Probes) Probes.Add(helper);
            return ProbeResult(helper);
        };
        Launcher.ProbePause = () =>
        {
            Pauses++;
            return Task.CompletedTask;
        };
        Launcher.ForgetProbes();
        InstallChecks.Forget(Launcher.RepairedCheck);
    }

    /// <summary>The start helpers probed, in order.</summary>
    public List<string> Probes { get; } = new();

    /// <summary>What a probe gives: null when the start helper started Runner Manager, otherwise why not.</summary>
    public Func<string, string?> ProbeResult { get; set; } = _ => null;

    public int Pauses { get; private set; }

    public void Dispose()
    {
        Launcher.Embedded = _embedded;
        Launcher.Settle = _settle;
        Launcher.UnderUpdateLock = _lock;
        Launcher.Decides = _decides;
        Launcher.IsProven = _proven;
        Launcher.StartedFrom = _startedFrom;
        Launcher.RunProbe = _probe;
        Launcher.ProbePause = _pause;
        Launcher.ForgetProbes();
        InstallChecks.Forget(Launcher.RepairedCheck);
    }
}

/// <summary>Which file a running program runs from, told by the file and not by its path (see <see cref="ProcessImage"/>).</summary>
public class ProcessImageTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    private static Process StartPing(string exe)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-n", "60", "127.0.0.1" }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    /// <summary>
    /// Waits until Windows has loaded <paramref name="process"/> and lists <paramref name="exe"/> first among its modules,
    /// for up to 10 seconds. Right after the start, the list can be empty, so that <see cref="Process.MainModule"/> is
    /// null, or begin with ntdll.dll, which MainModule then gives: of 1500 starts of ping, read at once, 466 gave null and
    /// 2 gave ntdll.dll. Its output cannot tell when it is loaded: a copy of ping under another name prints nothing.
    /// </summary>
    private static void WaitUntilLoaded(Process process, string exe)
    {
        var waited = Stopwatch.StartNew();
        string first;
        while (!string.Equals(first = StartedBy(process), exe, StringComparison.OrdinalIgnoreCase))
        {
            if (waited.Elapsed >= TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Windows listed {first} first among the modules of process {process.Id}, not {exe}, after 10 seconds.");
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// The path that <paramref name="process"/> was started by, from its list of modules, once it is loaded (see
    /// <see cref="WaitUntilLoaded"/>). Reading the list fails (Win32Exception) while Windows changes it, or finds it
    /// empty right after the start, so it is read again for up to 10 seconds.
    /// </summary>
    private static string StartedBy(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                process.Refresh();
                if (process.MainModule?.FileName is { Length: > 0 } path) return path;
            }
            catch (Win32Exception) when (deadline.Elapsed < TimeSpan.FromSeconds(10)) { }
            if (deadline.Elapsed >= TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Windows listed no modules of process {process.Id} within 10 seconds.");
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// A change of a file that was just written, tried again for a few seconds: antivirus software opens a new exe to scan
    /// it, and while it has it open, the file cannot be moved or deleted.
    /// </summary>
    internal static void Retried(Action change)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                change();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void ARunningProgram_IsTheFileItRunsFrom_NotThePathItWasStartedBy()
    {
        var folder = InstallEvidenceTests.NewFolder("image");
        var installed = Path.Combine(folder, AppPaths.ExeName);
        File.Copy(Ping, installed);
        var running = StartPing(installed);
        try
        {
            WaitUntilLoaded(running, installed);
            Assert.True(ProcessImage.RunsFrom(running.SafeHandle, installed));

            // An update renames the running exe out of the way and puts another at its path: here a build with the same
            // bytes, so also the same version.
            var aside = Path.Combine(folder, "RunnerManager.638000000000000000.old.exe");
            Retried(() => File.Move(installed, aside));
            Retried(() => File.Copy(Ping, installed));
            Assert.False(ProcessImage.RunsFrom(running.SafeHandle, installed));
            Assert.True(ProcessImage.RunsFrom(running.SafeHandle, aside));
            // The path it was started by still names the installed exe, so a path says nothing.
            Assert.Equal(installed, StartedBy(running), ignoreCase: true);
            Assert.Equal(aside, ProcessImage.CurrentPath(running.SafeHandle), ignoreCase: true);

            // A rollback puts it back: it is the installed file again.
            Retried(() => File.Delete(installed));
            Retried(() => File.Move(aside, installed));
            Assert.True(ProcessImage.RunsFrom(running.SafeHandle, installed));
            Assert.True(ProcessImage.RunsFrom(running.SafeHandle, installed.ToUpperInvariant()));
            Assert.False(ProcessImage.RunsFrom(running.SafeHandle, Path.Combine(folder, "missing.exe")));
        }
        finally
        {
            try { running.Kill(); } catch (InvalidOperationException) { }
            running.WaitForExit(5000);
            running.Dispose();
            Retried(() => Directory.Delete(folder, recursive: true));
        }
    }

    [Fact]
    public void ThisProcess_RunsFromItsOwnFile_AndNotFromACopyOfIt()
    {
        var folder = InstallEvidenceTests.NewFolder("image-self");
        try
        {
            Assert.True(ProcessImage.ThisProcessRunsFrom(Environment.ProcessPath!));
            var copy = Path.Combine(folder, Path.GetFileName(Environment.ProcessPath!));
            File.Copy(Environment.ProcessPath!, copy);
            Assert.False(ProcessImage.ThisProcessRunsFrom(copy));
            Assert.Null(ProcessImage.IdOf(Path.Combine(folder, "missing.exe")));
            Assert.Null(ProcessImage.IdOf("relative.exe"));
        }
        finally { Retried(() => Directory.Delete(folder, recursive: true)); }
    }
}

/// <summary>
/// Only the installed build looks after the install (see <see cref="Launcher.Decides"/>): an earlier copy that still runs
/// after an update or a rollback leaves the start helper, its marker, the tasks and the unpack folders alone.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperIdentityTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static readonly byte[] NewBytes = "the start helper of this version"u8.ToArray();

    private static string Unpacked(HelperFolder folder)
    {
        var build = Path.Combine(folder.Path, Launcher.ExtractionFolderName, "RunnerManager", "earlier-build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "native.dll"), "x");
        return build;
    }

    private static Task RemoveUnpacked(string folder)
    {
        Launcher.RemoveOldExtractions(folder, Array.Empty<string>());
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnEarlierCopyThatStillRuns_LeavesADifferentStartHelperAndTheTasksAlone()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(NewBytes);
        using var folder = new HelperFolder();
        var unpacked = Unpacked(folder);
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        // What AppUpdater.IsInstalledBuild gives for a copy whose file an update renamed out of the way.
        Launcher.Decides = () => false;
        var log = new List<string>();

        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, RemoveUnpacked);

        Assert.Equal(HelperFolder.Bytes, File.ReadAllBytes(folder.Helper));
        Assert.False(File.Exists(Launcher.OffMarkerPath(folder.Path)));
        Assert.True(Directory.Exists(unpacked));
        Assert.Empty(seams.Probes);
        Assert.Empty(log);

        // Its own update steps leave the tasks as they are: the startup task stays at the exe although the start helper is
        // there, and a task at a start helper that is gone stays there too.
        Assert.True(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Equal(folder.Exe, Launcher.StartInfo(folder.Exe, new[] { "--after=1" }).FileName);
        folder.RemoveHelper();
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        Assert.True(await Installer.UpdateStartupTaskAsync(folder.Exe));
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe, folder.Helper));
        Assert.Empty(state.Tasks.Calls);
    }

    [Fact]
    public async Task TheInstalledBuild_ReplacesADifferentStartHelper_AndRemovesWhatEarlierBuildsUnpacked()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(NewBytes);
        using var folder = new HelperFolder();
        var unpacked = Unpacked(folder);
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        Launcher.Decides = () => true;
        var log = new List<string>();

        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, RemoveUnpacked);

        Assert.Equal(NewBytes, File.ReadAllBytes(folder.Helper));
        Assert.Equal(new[] { folder.Helper }, seams.Probes);
        Assert.False(Directory.Exists(unpacked));
        Assert.Equal($"Wrote the start helper {folder.Helper}.", Assert.Single(log));
        Assert.Empty(state.Tasks.Calls);
    }
}

/// <summary>
/// Nothing depends on the start helper before this process saw it start Runner Manager (see
/// <see cref="Launcher.ProveAsync"/>): it started this process, or a probe of it exited with 0.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class StartHelperProofTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private static readonly byte[] NewBytes = "the start helper of this version"u8.ToArray();
    private const string Failure = "it did not start Runner Manager (exit code 0x524C0003)";

    private static string? CommandOf(TestInstallState state, string name)
        => ScheduledTasks.CommandOfOwnElevatedTask(state.Tasks.Definitions[name] ?? string.Empty);

    [Fact]
    public async Task AStartHelperThatDoesNotStartRunnerManager_IsTriedOnceMore_ThenGivenUp_AndTheTasksStartTheExe()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes) { ProbeResult = _ => Failure };
        using var folder = new HelperFolder();
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        var log = new List<string>();

        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, _ => Task.CompletedTask);

        Assert.Equal(new[] { folder.Helper, folder.Helper }, seams.Probes);
        Assert.Equal(1, seams.Pauses);
        // Given up as for a start helper that was removed: the marker with the version and why, and the log line.
        Assert.Equal(new[] { AppUpdater.CurrentVersion, Failure }, File.ReadAllLines(Launcher.OffMarkerPath(folder.Path)));
        Assert.Contains(Launcher.OffLogText(Failure), log);
        Assert.True(Launcher.RunsWithout(folder.Path));
        foreach (var name in new[] { ScheduledTasks.LaunchTaskName, ScheduledTasks.DashboardTaskName, ScheduledTasks.AgentTaskName })
            Assert.Equal(folder.Exe, CommandOf(state, name));
        Assert.Contains($"Run {ScheduledTasks.AgentTaskName}", state.Tasks.Calls);

        // Not probed again by this process: this version runs without it.
        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, _ => Task.CompletedTask);
        Assert.Equal(2, seams.Probes.Count);
    }

    [Fact]
    public async Task AProbeThatFailsOnce_AndWorksTheSecondTime_KeepsTheStartHelper()
    {
        using var state = new TestInstallState();
        var probes = 0;
        using var seams = new ProofSeams(HelperFolder.Bytes) { ProbeResult = _ => ++probes == 1 ? Failure : null };
        using var folder = new HelperFolder();

        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.Equal(2, probes);
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.True(Launcher.IsProven(folder.Path));
    }

    [Fact]
    public async Task TheStartHelperIsProbedOncePerProcess_AndAgainWhenItsFileOrTheExeChanged()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var folder = new HelperFolder();

        Assert.False(Launcher.IsProven(folder.Path));
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.True((await Launcher.ProveAsync(folder.Path, TimeSpan.Zero)).Outcome == Launcher.Proof.Works);
        Assert.Single(seams.Probes);
        Assert.True(Launcher.IsProven(folder.Path));

        // Written again with other bytes (an update brings its own start helper): probed again.
        Launcher.Embedded = () => NewBytes;
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.Equal(2, seams.Probes.Count);

        // Another exe beside it, of the same size and bytes (an update or a rollback put another file there).
        File.Delete(folder.Exe);
        File.WriteAllText(folder.Exe, string.Empty);
        Assert.False(Launcher.IsProven(folder.Path));
        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.Equal(3, seams.Probes.Count);
    }

    [Fact]
    public async Task AProcessThatTheStartHelperStarted_DoesNotProbe()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var folder = new HelperFolder();
        Launcher.StartedFrom = path => string.Equals(path, folder.Path, StringComparison.OrdinalIgnoreCase);

        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: false, _ => { }));
        Assert.Equal(Launcher.Proof.Works, (await Launcher.ProveAsync(folder.Path, TimeSpan.Zero)).Outcome);
        Assert.True(Launcher.IsProven(folder.Path));
        Assert.True(seams.Probes.Count == 0, $"Probed: {string.Join(", ", seams.Probes)}");
    }

    [Fact]
    public async Task WhileAnUpdateHoldsTheLock_NothingIsProbedOrDecided_AndALaterCheckDoes()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes) { ProbeResult = _ => Failure };
        using var folder = new HelperFolder();
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Helper);
        Launcher.UnderUpdateLock = (_, _) => null;
        var log = new List<string>();

        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, _ => Task.CompletedTask);

        Assert.Empty(seams.Probes);
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.Empty(state.Tasks.Calls);
        Assert.Empty(log);
        Assert.Equal(Launcher.Proof.NotYet, (await Launcher.ProveAsync(folder.Path, TimeSpan.Zero)).Outcome);
        // A task registered meanwhile keeps what it runs: the start helper was not seen to work yet.
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe, folder.Helper));
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));

        // The update is done: the next check probes, and gives up.
        Launcher.UnderUpdateLock = (_, write) => write();
        await Launcher.EnsureForCopyAsync(folder.Path, log.Add, _ => Task.CompletedTask);
        Assert.Equal(2, seams.Probes.Count);
        Assert.True(Launcher.RunsWithout(folder.Path));
        Assert.Equal(folder.Exe, CommandOf(state, ScheduledTasks.LaunchTaskName));
    }

    [Fact]
    public async Task Setup_ChecksTheStartHelperAfterWritingIt_AndBeforeAnyTaskNamesIt()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(NewBytes);
        using var folder = new HelperFolder(helper: false);
        // An earlier setup gave up on it for this version; the marker stays until the new one was seen to work.
        Launcher.TurnOff(folder.Path, AppUpdater.CurrentVersion, "it was removed right after it was written");
        var seen = new List<string>();
        seams.ProbeResult = helper =>
        {
            seen.Add($"bytes {(File.ReadAllBytes(helper).SequenceEqual(NewBytes) ? "new" : "other")}, marker {(Launcher.RunsWithout(folder.Path) ? "there" : "gone")}, " +
                     $"task {Launcher.CommandFor(folder.Exe)}");
            return null;
        };

        Assert.True(await Launcher.EnsureAsync(folder.Path, afterInstall: true, _ => { }));
        await ScheduledTasks.RegisterLaunchTaskAsync(folder.Exe);

        Assert.Equal($"bytes new, marker there, task {folder.Exe}", Assert.Single(seen));
        Assert.False(Launcher.RunsWithout(folder.Path));
        Assert.Equal(folder.Helper, CommandOf(state, ScheduledTasks.LaunchTaskName));
    }

    [Fact]
    public async Task Setup_RegistersItsTasksAtTheExe_WhenTheStartHelperDoesNotStartRunnerManager()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(NewBytes) { ProbeResult = _ => Failure };
        using var folder = new HelperFolder(helper: false);
        var log = new List<string>();

        Assert.False(await Launcher.EnsureAsync(folder.Path, afterInstall: true, log.Add));
        await ScheduledTasks.RegisterLaunchTaskAsync(folder.Exe);
        await Installer.RegisterStartupTaskAsync(folder.Exe);

        Assert.Equal(AppUpdater.CurrentVersion, Launcher.OffVersion(folder.Path));
        Assert.Equal(folder.Exe, CommandOf(state, ScheduledTasks.LaunchTaskName));
        Assert.Equal(folder.Exe, CommandOf(state, ScheduledTasks.DashboardTaskName));
        Assert.Contains(Launcher.OffLogText(Failure), log);
    }

    [Fact]
    public async Task TheFleetAgent_ProbesBeforeItMovesItsTask_AndGivesUpWhenTheProbeFails()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes) { ProbeResult = _ => Failure };
        using var folder = new HelperFolder();
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = InstallEvidenceTests.TaskOf(Me, folder.Exe);
        var log = new List<string>();
        Task GiveUp() => Launcher.EnsureForCopyAsync(folder.Path, log.Add, _ => Task.CompletedTask);

        Assert.False(await MachineRelay.StartHelperWorksAsync(folder.Path, GiveUp));
        // Probed twice, and given up with that result, without a third probe.
        Assert.Equal(2, seams.Probes.Count);
        Assert.Equal(AppUpdater.CurrentVersion, Launcher.OffVersion(folder.Path));
        Assert.Contains(Launcher.OffLogText(Failure), log);
        Assert.Equal(folder.Exe, CommandOf(state, ScheduledTasks.AgentTaskName));

        File.Delete(Launcher.OffMarkerPath(folder.Path));
        seams.ProbeResult = _ => null;
        Launcher.ForgetProbes();
        Assert.True(await MachineRelay.StartHelperWorksAsync(folder.Path, () => throw new InvalidOperationException("Not given up.")));
        Assert.Equal(3, seams.Probes.Count);
    }

    [Fact]
    public void TheProbe_StartsTheStartHelperWithTheProbeArgument_AndNeedsExitCodeZero()
    {
        var launcher = Path.Combine(AppContext.BaseDirectory, "launcher", Launcher.FileName);
        var stub = Path.Combine(AppContext.BaseDirectory, "launcher", "LauncherTestStub.exe");
        Assert.True(File.Exists(launcher) && File.Exists(stub), "The start helper and its test stub were not built (see LauncherTests).");
        var folder = InstallEvidenceTests.NewFolder("probe");
        try
        {
            var helper = Launcher.PathIn(folder);
            File.Copy(launcher, helper);
            // No RunnerManager.exe beside it: the start helper's own failure.
            Assert.Matches(@"^it did not start Runner Manager \(exit code 0x524C00[0-9A-F]{2}\)$", Launcher.Probe(helper));

            File.Copy(stub, Path.Combine(folder, AppPaths.ExeName));
            Assert.Null(Launcher.Probe(helper));
            var run = File.ReadAllText(Path.Combine(folder, "RunnerManager.out"), System.Text.Encoding.Unicode).Split('\0');
            Assert.EndsWith(" " + App.ProbeArgument, run[1]);

            File.WriteAllText(Path.Combine(folder, "RunnerManager.control"), "exit 7\r\n");
            Assert.Equal("it did not start Runner Manager (exit code 0x00000007)", Launcher.Probe(helper));

            Assert.StartsWith("it could not be started: ", Launcher.Probe(Path.Combine(folder, "missing", Launcher.FileName)));
        }
        finally
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(250); }
            }
        }
    }

    [Fact]
    public async Task ATaskNamesTheStartHelper_OnlyOnceThisProcessSawItWork()
    {
        using var state = new TestInstallState();
        using var seams = new ProofSeams(HelperFolder.Bytes);
        using var folder = new HelperFolder();

        // In place, not checked yet: a new task gets the exe, and a task at the start helper keeps it.
        Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe));
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe, folder.Helper));
        Assert.Equal(folder.Exe, Launcher.StartInfo(folder.Exe, Array.Empty<string>()).FileName);

        Assert.Equal(Launcher.Proof.Works, (await Launcher.ProveAsync(folder.Path, TimeSpan.Zero)).Outcome);
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe));
        Assert.Equal(folder.Helper, Launcher.CommandFor(folder.Exe, folder.Exe));
        Assert.Equal(folder.Helper, Launcher.StartInfo(folder.Exe, Array.Empty<string>()).FileName);

        // Only beside the installed exe.
        Launcher.IsInstalledExe = _ => false;
        try { Assert.Equal(folder.Exe, Launcher.CommandFor(folder.Exe)); }
        finally { Launcher.IsInstalledExe = exe => exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase); }
    }
}

/// <summary>
/// A copy without administrator rights never checks or decides anything about the start helper; when it does not work,
/// the UAC prompt is for RunnerManager.exe itself (see <see cref="Elevation.HandOffAsync"/>).
/// </summary>
[Collection(InstallStateCollection.Name)]
public class ElevationFallbackTests
{
    private sealed class Starts : IDisposable
    {
        private readonly Func<ProcessStartInfo, Process?> _before = Elevation.StartProcess;

        public Starts(Func<ProcessStartInfo, int?> fail)
        {
            Elevation.StartProcess = start =>
            {
                Seen.Add(start);
                if (fail(start) is { } error) throw new Win32Exception(error);
                return null;
            };
        }

        public List<ProcessStartInfo> Seen { get; } = new();

        public void Dispose() => Elevation.StartProcess = _before;
    }

    private static readonly string[] Args = { "--elevation-attempted" };

    [Fact]
    public async Task WhenTheLaunchTaskRanAndNoDashboardCame_UacIsAskedForTheExeItself()
    {
        using var folder = new HelperFolder();
        using var starts = new Starts(_ => null);

        var result = await Elevation.HandOffAsync(folder.Exe, Args, tryLaunchTask: true,
            () => Task.FromResult(Elevation.LaunchTaskOutcome.NothingAppeared));

        Assert.Equal((true, true), result);
        var start = Assert.Single(starts.Seen);
        Assert.Equal(folder.Exe, start.FileName);
        Assert.Equal("runas", start.Verb);
        Assert.Equal(Args, start.ArgumentList);
    }

    [Fact]
    public async Task WithoutAUsableLaunchTask_UacIsAskedForTheStartHelper_AndAStartedDashboardNeedsNoPrompt()
    {
        using var folder = new HelperFolder();
        using var starts = new Starts(_ => null);

        Assert.Equal((true, true), await Elevation.HandOffAsync(folder.Exe, Args, tryLaunchTask: true,
            () => Task.FromResult(Elevation.LaunchTaskOutcome.NoTask)));
        Assert.Equal(folder.Helper, Assert.Single(starts.Seen).FileName);

        starts.Seen.Clear();
        Assert.Equal((true, false), await Elevation.HandOffAsync(folder.Exe, Args, tryLaunchTask: true,
            () => Task.FromResult(Elevation.LaunchTaskOutcome.Started)));
        Assert.Empty(starts.Seen);
    }

    [Fact]
    public void WhenTheStartHelperCannotBeStartedWithUac_TheExeItselfIsAsked()
    {
        using var folder = new HelperFolder();
        // Antivirus software blocks it, or it is gone (ERROR_FILE_NOT_FOUND, ERROR_ACCESS_DENIED).
        foreach (var error in new[] { 2, 5 })
        {
            using var starts = new Starts(start => start.FileName == folder.Helper ? error : null);
            Assert.True(Elevation.RelaunchElevated(folder.Exe, Args));
            Assert.Equal(new[] { folder.Helper, folder.Exe }, starts.Seen.Select(s => s.FileName));
            Assert.All(starts.Seen, s => Assert.Equal("runas", s.Verb));
        }
    }

    [Fact]
    public void ADeclinedUacPrompt_IsNotAskedAgain()
    {
        using var folder = new HelperFolder();
        using (var starts = new Starts(_ => Elevation.ErrorCancelled))
        {
            Assert.False(Elevation.RelaunchElevated(folder.Exe, Args));
            Assert.Equal(folder.Helper, Assert.Single(starts.Seen).FileName);
        }
        using (var starts = new Starts(_ => 2))
        {
            Assert.False(Elevation.RelaunchElevated(folder.Exe, Args, direct: true));
            Assert.Equal(folder.Exe, Assert.Single(starts.Seen).FileName);
        }
    }

    [Fact]
    public void ACopyWithoutAdministratorRights_AsksForTheStartHelperOnlyWhileItIsInPlace()
    {
        using var folder = new HelperFolder();
        Assert.Equal(folder.Helper, Launcher.UacCommandFor(folder.Exe));
        Launcher.TurnOff(folder.Path, AppUpdater.CurrentVersion, "it was removed right after it was written");
        Assert.Equal(folder.Exe, Launcher.UacCommandFor(folder.Exe));
    }
}
