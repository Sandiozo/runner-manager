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
            RunPowerShell(Installer.UnlessInstalledIn(Path.Combine(root, "RunnerManager"), write));
            Assert.True(File.Exists(Path.Combine(root, "ran.txt")));
            File.Delete(Path.Combine(root, "ran.txt"));

            // Only read, never written: the entry of the Runner Manager installed on this PC, if any.
            if (Installer.RegisteredInstallDir() is { } installed)
            {
                RunPowerShell(Installer.UnlessInstalledIn(installed + Path.DirectorySeparatorChar, write));
                Assert.False(File.Exists(Path.Combine(root, "ran.txt")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunPowerShell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
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
    public void AMissingExe_IsPutBackFromTheCompleteNewFile_OrElseFromThePreviousOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(folder, "RunnerManager.exe");
            // Nothing to repair, or no install there.
            Assert.Null(AppUpdater.RepairMissingExe(exe, () => true));
            File.WriteAllText(exe + ".new", "new");
            Assert.Null(AppUpdater.RepairMissingExe(exe, () => false));

            // Cut off between the renames of an update: the new file goes in.
            File.WriteAllText(exe + ".previous", "old");
            Assert.Equal("RunnerManager.exe.new", AppUpdater.RepairMissingExe(exe, () => true));
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".previous"));
            Assert.Null(AppUpdater.RepairMissingExe(exe, () => true));

            // Cut off in a rollback: the previous version goes in.
            File.Delete(exe);
            Assert.Equal("RunnerManager.exe.previous", AppUpdater.RepairMissingExe(exe, () => true));
            Assert.Equal("old", File.ReadAllText(exe));
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
}
