using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RunnerManager.Tests;

/// <summary>Setup, scheduled tasks, updates and start-up, from the 2.8.2 audit.</summary>
public class AuditSetupTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\RunnerManager\RunnerManager.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string CurrentUser
    {
        get
        {
            using var me = WindowsIdentity.GetCurrent();
            return me.Name;
        }
    }

    private static string AgentXml() => ScheduledTasks.TaskXml(@"PC\me", Exe, "--agent", "Agent", alwaysOn: true, atLogon: true, parallel: false);
    private static string LaunchXml(string exe = Exe) => ScheduledTasks.TaskXml(CurrentUser, exe, "--from-task", "Launch", alwaysOn: false, atLogon: false, parallel: true);
    private static string StartupXml(string? user = null, string exe = Exe)
        => ScheduledTasks.TaskXml(user ?? CurrentUser, exe, "--minimized", "Startup", alwaysOn: false, atLogon: true, parallel: false);

    /// <summary>As earlier versions registered it (Task Scheduler's default priority).</summary>
    private static string Old(string xml) => xml.Replace("<Priority>4</Priority>", "<Priority>7</Priority>");
    /// <summary>As Task Scheduler exports a task with the default priority: without the element.</summary>
    private static string WithoutPriority(string xml) => Regex.Replace(xml, @"\s*<Priority>\d+</Priority>", string.Empty);

    // ================= Task priority =================

    [Fact]
    public void EveryTask_IsRegisteredAtNormalPriority()
    {
        foreach (var xml in new[] { AgentXml(), LaunchXml(), StartupXml() })
        {
            var settings = XDocument.Parse(xml).Root!.Element(Ns + "Settings")!;
            // 4 to 6 are normal priority; 7, the default, is below normal.
            Assert.Equal("4", settings.Element(Ns + "Priority")!.Value);
            Assert.True(ScheduledTasks.HasCurrentPriority(xml));
        }
    }

    [Fact]
    public void AnAgentTaskBelowNormalPriority_IsRegisteredAgain()
    {
        Assert.True(ScheduledTasks.IsCurrentAgentDefinition(AgentXml()));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(Old(AgentXml())));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(WithoutPriority(AgentXml())));
        Assert.False(ScheduledTasks.HasCurrentPriority(string.Empty));
        Assert.False(ScheduledTasks.HasCurrentPriority("not xml"));
    }

    [Fact]
    public void ALaunchTaskBelowNormalPriority_IsRegisteredAgain()
    {
        Assert.False(Installer.LaunchTaskNeedsRegistering(LaunchXml(), Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(Old(LaunchXml()), Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(WithoutPriority(LaunchXml()), Exe));
        // As before: a missing task, or one that starts another exe.
        Assert.True(Installer.LaunchTaskNeedsRegistering(string.Empty, Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(LaunchXml(@"D:\Old\RunnerManager.exe"), Exe));
    }

    [Fact]
    public void AStartupTask_IsRegisteredAgainOnlyWhenItIsOursTurnedOnAndOutdated()
    {
        Assert.False(Installer.StartupTaskNeedsRegistering(StartupXml(), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(Old(StartupXml()), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(WithoutPriority(StartupXml()), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(StartupXml(exe: @"D:\Old\RunnerManager.exe"), Exe));
        // Not there, turned off by the user, or another user's: left alone.
        Assert.False(Installer.StartupTaskNeedsRegistering(string.Empty, Exe));
        var turnedOff = Regex.Replace(Old(StartupXml()), @"<Enabled>true</Enabled>(\s*<Hidden>)", "<Enabled>false</Enabled>$1");
        Assert.False(Installer.StartupTaskNeedsRegistering(turnedOff, Exe));
        Assert.False(Installer.StartupTaskNeedsRegistering(Old(StartupXml("S-1-5-21-1-2-3-4242")), Exe));
    }

    [Fact]
    public void AProcessStartedBelowNormal_RaisesItselfToNormal()
    {
        try
        {
            ProcessPriority.LowerForTest();
            var lowered = ProcessPriority.Read();
            Assert.Equal(ProcessPriorityClass.BelowNormal, lowered.Cpu);
            Assert.Equal(1, lowered.Io);
            Assert.Equal(2, lowered.Memory);

            Assert.True(ProcessPriority.RaiseToNormal());
            var raised = ProcessPriority.Read();
            Assert.Equal(ProcessPriorityClass.Normal, raised.Cpu);
            Assert.Equal(2, raised.Io);
            Assert.Equal(5, raised.Memory);
            // Already normal: nothing to do.
            Assert.False(ProcessPriority.RaiseToNormal());
        }
        finally { ProcessPriority.RaiseToNormal(); }
    }

    // ================= Processes setup ends =================

    [Theory]
    [InlineData(null, CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\"", CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --minimized", CopyRole.Dashboard)]
    [InlineData("\"C:\\My ai tools\\RunnerManager.exe\" --from-task", CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --mcp", CopyRole.Assistant)]
    [InlineData("C:\\Apps\\RunnerManager.exe ai list-pcs", CopyRole.Assistant)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --agent", CopyRole.Agent)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --setup", CopyRole.Other)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --uninstall --quiet", CopyRole.Other)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --install-agent", CopyRole.Other)]
    public void ProcessRoles_AreToldFromTheCommandLine(string? commandLine, CopyRole expected)
        => Assert.Equal(expected, RunningCopies.RoleOf(commandLine));

    [Fact]
    public void Setup_EndsAssistantHostsOnlyWhenTheyLoseTheirExe()
    {
        const string mcp = "\"C:\\Apps\\RunnerManager.exe\" --mcp";
        Assert.False(Installer.IsClosedBySetup(mcp, endAssistants: false));
        Assert.True(Installer.IsClosedBySetup(mcp, endAssistants: true));
        Assert.True(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --minimized", endAssistants: false));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --agent", endAssistants: true));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --install-agent", endAssistants: true));
    }

    // ================= Helpers that run after exit =================

    [Fact]
    public void TheUninstallHelper_WaitsForTheProcessWithoutATimeLimit()
    {
        var unlimited = AppUpdater.AfterExitScript("Write-Output done", 1234, timeout: null, TimeSpan.FromSeconds(1));
        Assert.StartsWith("Wait-Process -Id 1234 -ErrorAction SilentlyContinue;", unlimited);
        Assert.DoesNotContain("-Timeout", unlimited);
        Assert.Contains("-Timeout 120", AppUpdater.AfterExitScript("x", 1234, TimeSpan.FromSeconds(120), TimeSpan.Zero));
    }

    [Fact]
    public void RemovingAFolder_IsSkippedWhenSetupInstalledThereAfterAll()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-unless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "ran.txt").Replace("'", "''");
            var write = $"Set-Content -LiteralPath '{marker}' -Value ran";
            // The Settings → Apps entry points elsewhere (or there is none): the script runs.
            RunPowerShell(Installer.UnlessInstalledIn(write), Installer.FolderEnvironment(Path.Combine(root, "RunnerManager")));
            Assert.True(File.Exists(Path.Combine(root, "ran.txt")));
            File.Delete(Path.Combine(root, "ran.txt"));

            // Only read, never written: the entry of the Runner Manager installed on this PC, if any.
            if (Installer.RegisteredInstallDir() is { } installed)
            {
                RunPowerShell(Installer.UnlessInstalledIn(write), new Dictionary<string, string> { [Installer.FolderVariable] = installed + Path.DirectorySeparatorChar });
                Assert.False(File.Exists(Path.Combine(root, "ran.txt")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunPowerShell(string script, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30_000), "The script did not finish within 30 seconds.");
    }

    // ================= Exe swap =================

    [Fact]
    public void AnExeSwap_KeepsThePreviousVersion_AndLeavesNoTemporaryFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-swap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "download.exe");
            var exe = Path.Combine(folder, "RunnerManager.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(exe, "old");

            AppUpdater.ReplaceFile(source, exe, keepPrevious: true);

            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".previous"));
            Assert.False(File.Exists(exe + ".new"));
            Assert.False(File.Exists(exe + ".new.partial"));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AMissingExe_IsPutBackOnlyAfterAnInterruptedSwap()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "RunnerManager.exe");
            // No install there: nothing to do.
            Assert.Equal(RepairSource.None, AppUpdater.RepairMissingExe(exe, () => false).Source);
            File.WriteAllText(exe + ".new", "new");
            File.WriteAllText(exe + ".previous", "old");
            Assert.Equal(RepairSource.None, AppUpdater.RepairMissingExe(exe, () => false).Source);

            // Missing without a swap (quarantined, deleted by hand): no other version is put in its place.
            Assert.Equal(RepairSource.Unexplained, AppUpdater.RepairMissingExe(exe, () => true).Source);
            Assert.False(File.Exists(exe));

            // Cut off between the renames of an update: the new file goes in, and the marker goes.
            AppUpdater.WriteSwapMarker(exe, new SwapMarker(SwapKind.Update, "2.8.2", "2.9.0"));
            var update = AppUpdater.RepairMissingExe(exe, () => true);
            Assert.Equal(RepairSource.New, update.Source);
            Assert.Equal(new SwapMarker(SwapKind.Update, "2.8.2", "2.9.0"), update.Marker);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".previous"));
            Assert.False(File.Exists(AppUpdater.SwapMarkerPath(exe)));
            Assert.Equal(RepairSource.None, AppUpdater.RepairMissingExe(exe, () => true).Source);

            // Cut off in a rollback: the previous version goes in.
            File.Delete(exe);
            AppUpdater.WriteSwapMarker(exe, new SwapMarker(SwapKind.Rollback, "2.9.0", "2.8.2"));
            Assert.Equal(RepairSource.Previous, AppUpdater.RepairMissingExe(exe, () => true).Source);
            Assert.Equal("old", File.ReadAllText(exe));

            // A marker left by a swap that ended is removed, and nothing is moved.
            AppUpdater.WriteSwapMarker(exe, new SwapMarker(SwapKind.Update, "2.8.2", "2.9.0"));
            Assert.Equal(RepairSource.None, AppUpdater.RepairMissingExe(exe, () => true).Source);
            Assert.False(File.Exists(AppUpdater.SwapMarkerPath(exe)));
            Assert.Equal("old", File.ReadAllText(exe));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(true, true, true, SwapKind.Update, RepairSource.None)]
    [InlineData(false, true, true, null, RepairSource.Unexplained)]
    [InlineData(false, false, true, null, RepairSource.Unexplained)]
    [InlineData(false, true, true, SwapKind.Update, RepairSource.New)]
    [InlineData(false, true, true, SwapKind.Install, RepairSource.New)]
    [InlineData(false, false, true, SwapKind.Update, RepairSource.Previous)]
    [InlineData(false, true, true, SwapKind.Rollback, RepairSource.Previous)]
    [InlineData(false, true, true, SwapKind.Undo, RepairSource.Previous)]
    [InlineData(false, true, true, SwapKind.Unknown, RepairSource.Previous)]
    [InlineData(false, false, false, SwapKind.Rollback, RepairSource.Unexplained)]
    public void TheRepairOfAMissingExe_FollowsTheSwapThatWasCutOff(bool exe, bool next, bool previous, SwapKind? kind, RepairSource expected)
        => Assert.Equal(expected, AppUpdater.DecideRepair(exe, next, previous, kind is { } k ? new SwapMarker(k, "1.0.0", "1.1.0") : null));

    [Fact]
    public void ASwapMarkerThatCannotBeRead_StillCountsAsASwap()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"rm-marker-{Guid.NewGuid():N}.exe");
        try
        {
            Assert.Null(AppUpdater.ReadSwapMarker(exe));
            File.WriteAllText(AppUpdater.SwapMarkerPath(exe), "{ cut off");
            Assert.Equal(SwapKind.Unknown, AppUpdater.ReadSwapMarker(exe)!.Kind);
        }
        finally { File.Delete(AppUpdater.SwapMarkerPath(exe)); }
    }

    [Fact]
    public void AFailedSwap_LeavesNoNewFileForARepairToPutInPlace()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-failed-swap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "download.exe");
            var exe = Path.Combine(folder, "RunnerManager.exe");
            File.WriteAllText(source, "new");
            File.WriteAllText(exe, "old");
            var swap = new SwapMarker(SwapKind.Update, "2.8.2", "2.9.0");
            // Open without delete sharing, so the current exe cannot be renamed away.
            using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsAny<IOException>(() => AppUpdater.ReplaceFile(source, exe, keepPrevious: true, swap));
            Assert.Equal("old", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".new"));
            Assert.False(File.Exists(AppUpdater.SwapMarkerPath(exe)));

            // A swap that works leaves no marker either.
            AppUpdater.ReplaceFile(source, exe, keepPrevious: true, swap);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.False(File.Exists(AppUpdater.SwapMarkerPath(exe)));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ASwap_WaitsWhileAnotherProgramHoldsTheNewFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"rm-held-{Guid.NewGuid():N}.exe");
        File.WriteAllText(file, "new");
        try
        {
            Assert.True(AppUpdater.WaitUntilUnused(file, TimeSpan.FromSeconds(1)));
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.False(AppUpdater.WaitUntilUnused(file, TimeSpan.FromMilliseconds(500)));

            var scanner = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var letGo = Task.Run(async () =>
            {
                await Task.Delay(400);
                scanner.Dispose();
            });
            Assert.True(AppUpdater.WaitUntilUnused(file, TimeSpan.FromSeconds(10)));
            await letGo;
        }
        finally { File.Delete(file); }
    }

    // ================= Old files =================

    [Theory]
    [InlineData("RunnerManager.638662000000000000.old.exe", true, true)]
    [InlineData("RUNNERMANAGER.638662000000000000.OLD.EXE", true, true)]
    [InlineData("RunnerManager.Core.dll.638662000000000000.old.dll", false, true)]
    [InlineData("Microsoft.Foo.638662000000000000.old.dll", false, true)]
    [InlineData("MyTool.old.exe", false, false)]
    [InlineData("notes.old.txt", false, false)]
    [InlineData("RunnerManager.backup.old.exe", false, false)]
    [InlineData("RunnerManager.exe.previous", false, false)]
    public void OldFileCleanup_TouchesOnlyRunnerManagersOwnNames(string name, bool inAnyFolder, bool inDefaultFolder)
    {
        Assert.Equal(inAnyFolder, AppUpdater.IsOldFile(name, anyAppFile: false));
        Assert.Equal(inDefaultFolder, AppUpdater.IsOldFile(name, anyAppFile: true));
    }

    // ================= Downloads =================

    [Fact]
    public async Task ADownload_IsCopiedWhole()
    {
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        using var source = new MemoryStream(data);
        using var destination = new MemoryStream();
        await AppUpdater.CopyDownloadAsync(source, destination, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(data, destination.ToArray());
    }

    [Fact]
    public async Task ADownloadThatStops_FailsAfterTheStallLimit()
    {
        using var source = new StallingStream(new byte[1000]);
        using var destination = new MemoryStream();
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            AppUpdater.CopyDownloadAsync(source, destination, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Contains("no data", error.Message);
        Assert.Equal(1000, destination.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ADownloadOverItsOverallLimit_IsCancelled()
    {
        using var source = new StallingStream(new byte[10]);
        using var destination = new MemoryStream();
        using var overall = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppUpdater.CopyDownloadAsync(source, destination, TimeSpan.FromMinutes(1), overall.Token));
    }

    /// <summary>Sends its data, then nothing more until cancelled (a connection that stopped).</summary>
    private sealed class StallingStream(byte[] data) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                data.CopyTo(buffer);
                return data.Length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    // ================= Update health =================

    [Fact]
    public async Task AnUpdate_WorksOnlyOnceTheAgentAnswers()
    {
        var asked = 0;
        Assert.True(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(++asked >= 3),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(10)));
        Assert.Equal(3, asked);

        // Never answers: not marked as working.
        Assert.False(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(false),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)));

        // Without a pairing code nothing can ask it, so staying up is enough.
        Assert.True(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(null),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.Zero));
    }

    [Fact]
    public void AnUpdateWhoseAgentStaysUpWithoutAnswering_IsNoLongerWatched()
    {
        var pending = new PendingUpdate { Version = "2.9.0", PreviousVersion = "2.8.2", InstalledUtc = DateTime.UtcNow, Running = true };
        var state = new UpdateState { Pending = pending };
        // After the wait, later stops (a restart of the PC) are no failed starts: there is nothing left to count them on.
        var stopped = UpdateGuard.StopWatching(state, "2.9.0");
        Assert.Null(stopped.Pending);
        Assert.Equal(AgentStartDecision.Nothing, UpdateGuard.DecideAgentStart(stopped.Pending, "2.9.0", runningInstalledCopy: true).Decision);
        // Another version's pending update stays.
        Assert.Same(pending, UpdateGuard.StopWatching(state, "2.9.1").Pending);

        // Within the wait, an agent that stops before it worked still counts.
        var (decision, watched) = UpdateGuard.DecideAgentStart(pending, "2.9.0", runningInstalledCopy: true);
        Assert.Equal(AgentStartDecision.Watch, decision);
        Assert.Equal(1, watched!.FailedStarts);
        Assert.Equal(1, watched.Starts);
    }

    [Fact]
    public void RollbackReasons_SayWhatHappened()
    {
        Assert.Equal("the fleet agent stopped 3 times before it answered a status request on this PC", UpdateGuard.AgentStopsReason(3, answersStatus: true));
        Assert.Equal("the fleet agent stopped 3 times within 2 minutes of starting", UpdateGuard.AgentStopsReason(3, answersStatus: false));
        var neverStarted = new PendingUpdate { Version = "2.9.0" };
        Assert.Equal("its fleet agent did not start within 10 minutes", UpdateGuard.DashboardRollbackReason(neverStarted));
        Assert.StartsWith("its fleet agent stopped before it worked", UpdateGuard.DashboardRollbackReason(neverStarted with { Starts = 1 }));
        Assert.StartsWith("its fleet agent stopped before it worked", UpdateGuard.DashboardRollbackReason(neverStarted with { Running = true }));
    }

    // ================= Tasks that run =================

    [Theory]
    [InlineData("4\r\n", true)]
    [InlineData("2", true)]
    [InlineData("3", false)]
    [InlineData("1", false)]
    [InlineData("-1", false)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData("Exception calling GetTask", null)]
    public void ATaskIsRegisteredAgainOnlyWhenItIsKnownNotToRun(string output, bool? running)
        => Assert.Equal(running, ScheduledTasks.ParseTaskState(output));

    // ================= Setup =================

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, null, false, true)]
    [InlineData(false, true, null, true)]
    [InlineData(false, null, false, null)]
    [InlineData(null, false, false, null)]
    public void AStartupTaskThatCannotBeRead_IsLeftAsItIs(bool? own, bool? shared, bool? legacy, bool? expected)
        => Assert.Equal(expected, Installer.AnyStartupTask(own, shared, legacy));

    [Theory]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager.exe --mcp", null, CopyRole.Assistant)]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager.exe --mcp", "C:\\Program Files\\RunnerManager\\RunnerManager.exe", CopyRole.Assistant)]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager.exe ai list-pcs", null, CopyRole.Assistant)]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager.exe --agent", null, CopyRole.Agent)]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager.exe", null, CopyRole.Dashboard)]
    [InlineData("C:\\Program Files\\RunnerManager\\RunnerManager --minimized", "C:\\Program Files\\RunnerManager\\RunnerManager", CopyRole.Dashboard)]
    [InlineData("RunnerManager --mcp", null, CopyRole.Assistant)]
    // An argument that ends in .exe is not taken for the program.
    [InlineData("RunnerManager ai run --path C:\\x.exe", null, CopyRole.Assistant)]
    [InlineData("C:\\Apps\\RunnerManager ai run --path C:\\x.exe", null, CopyRole.Assistant)]
    public void AProgramPathWithSpacesButWithoutQuotes_IsNotTakenForArguments(string commandLine, string? programPath, CopyRole expected)
        => Assert.Equal(expected, RunningCopies.RoleOf(commandLine, programPath));

    [Fact]
    public void ACopyStartedByAPathWithSpacesButWithoutQuotes_SeesOnlyItsRealArguments()
    {
        const string exe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
        // As Windows splits it: "C:\Program" is the program, "Files\RunnerManager\RunnerManager.exe" the first argument.
        Assert.Equal(new[] { "--mcp" },
            RunningCopies.WithoutProgramPathPieces(new[] { @"Files\RunnerManager\RunnerManager.exe", "--mcp" }, exe + " --mcp", exe));
        Assert.Empty(RunningCopies.WithoutProgramPathPieces(new[] { @"Files\RunnerManager\RunnerManager.exe" }, exe, exe));
        // Quoted, or without spaces: unchanged.
        Assert.Equal(new[] { "--mcp" }, RunningCopies.WithoutProgramPathPieces(new[] { "--mcp" }, $"\"{exe}\" --mcp", exe));
        Assert.Equal(new[] { "--mcp" },
            RunningCopies.WithoutProgramPathPieces(new[] { "--mcp" }, @"C:\Apps\RunnerManager.exe --mcp", @"C:\Apps\RunnerManager.exe"));
        // Started some other way: unchanged.
        Assert.Equal(new[] { "Files", "--mcp" }, RunningCopies.WithoutProgramPathPieces(new[] { "Files", "--mcp" }, "RunnerManager Files --mcp", exe));
    }
}
