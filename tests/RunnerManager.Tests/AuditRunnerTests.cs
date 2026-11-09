using System.IO;

namespace RunnerManager.Tests;

/// <summary>Runner jobs, health checks and runner setup: the fixes from the 2.8.2 audit.</summary>
public class AuditRunnerTests
{
    private static string NewRunnerRoot()
    {
        var root = Directory.CreateTempSubdirectory("rm-audit-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "_diag", "pages"));
        return root;
    }

    private static string WritePage(string root, string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(root, "_diag", "pages", name);
        File.WriteAllText(path, text);
        File.SetCreationTimeUtc(path, writtenUtc);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void OutputFile_OfAHungStep_IsStillFound_AfterHalfAnHour()
    {
        var root = NewRunnerRoot();
        try
        {
            var hung = WritePage(root, "a_1.log", "2026-10-19T12:00:00.0000000Z Compiling…\n", DateTime.UtcNow.AddHours(-2));

            var file = RunnerActivity.CurrentOutputFile(root);

            Assert.NotNull(file);
            Assert.Equal(hung, file!.Path);
            Assert.Equal(new[] { "Compiling…", "" }, RunnerActivity.ReadOutput(root, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputFile_IsTheNewestPage_AndOlderJobsPagesAreLeftOut()
    {
        var root = NewRunnerRoot();
        try
        {
            WritePage(root, "old_1.log", "old job\n", DateTime.UtcNow.AddHours(-5));
            var current = WritePage(root, "new_1.log", "this job\n", DateTime.UtcNow.AddHours(-1));

            Assert.Equal(current, RunnerActivity.CurrentOutputFile(root)!.Path);
            // The worker started after both pages were written: neither belongs to its job.
            Assert.Null(RunnerActivity.CurrentOutputFile(root, DateTime.UtcNow.AddMinutes(-30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputState_ChangesWhileTheRunnerWritesToAnOpenPage()
    {
        var root = NewRunnerRoot();
        try
        {
            var path = Path.Combine(root, "_diag", "pages", "a_1.log");
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer.Write("first\n"u8);
            writer.Flush();
            var before = RunnerActivity.CurrentOutputFile(root)!.State;
            writer.Write("second\n"u8);
            writer.Flush();

            Assert.NotEqual(before, RunnerActivity.CurrentOutputFile(root)!.State);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RunnerActivity.ProcessRow Row(int id, int parent, string name, string path, DateTime startedUtc)
        => new(id, parent, name, path, string.Empty, 0, 0, System.Management.ManagementDateTimeConverter.ToDmtfDateTime(startedUtc.ToLocalTime()));

    private static RunnerInfo Busy(string root) => new() { InstallPath = root, IsRunning = true, ActivityText = "Busy" };

    [Fact]
    public void JobProcesses_LeaveOutAnOlderProcess_WhoseEndedParentHadTheWorkersId()
    {
        const string root = @"C:\GitHub-Runners\app";
        var started = DateTime.UtcNow.AddHours(-1);
        var rows = new[]
        {
            Row(100, 50, "Runner.Worker.exe", root + @"\bin\Runner.Worker.exe", started),
            Row(200, 100, "dotnet.exe", @"C:\dotnet\dotnet.exe", started.AddSeconds(10)),
            Row(201, 200, "MSBuild.exe", @"C:\dotnet\MSBuild.exe", started.AddSeconds(20)),
            // explorer.exe outlives userinit.exe, whose ID Windows later gave to the worker.
            Row(300, 100, "explorer.exe", @"C:\Windows\explorer.exe", started.AddDays(-1)),
            Row(301, 300, "notepad.exe", @"C:\Windows\notepad.exe", started.AddMinutes(5)),
            // Another runner's worker.
            Row(400, 51, "Runner.Worker.exe", @"C:\GitHub-Runners\other\bin\Runner.Worker.exe", started)
        };

        var job = RunnerActivity.JobRows(root, rows);

        Assert.Equal(new[] { 100, 200, 201 }, job.Select(p => p.Id).Order());
    }

    [Fact]
    public void RememberedRunnerFolder_IsUsed_OnlyWhileItStillHoldsTheRunner()
    {
        var root = Directory.CreateTempSubdirectory("rm-audit-").FullName;
        try
        {
            const string service = "actions.runner.owner-repo.build-01";
            File.WriteAllText(Path.Combine(root, ".runner"), "{}");
            File.WriteAllText(Path.Combine(root, ".service"), service);

            Assert.True(RunnerActivity.StillHoldsRunner(root, service));
            Assert.True(RunnerActivity.StillHoldsRunner(root, root + "\\"));
            Assert.False(RunnerActivity.StillHoldsRunner(root, "actions.runner.owner-repo.other"));
            // "Remove and keep files" deletes the registration.
            File.Delete(Path.Combine(root, ".runner"));
            Assert.False(RunnerActivity.StillHoldsRunner(root, service));
            Assert.False(RunnerActivity.StillHoldsRunner(root, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void JobWatch_ReadsTheProcessesOnce_ForAllBusyRunners()
    {
        var a = NewRunnerRoot();
        var b = NewRunnerRoot();
        try
        {
            var started = DateTime.UtcNow.AddMinutes(-3);
            var snapshot = new RunnerActivity.ProcessSnapshot(new[]
            {
                Row(1, 0, "Runner.Worker.exe", Path.Combine(a, "bin", "Runner.Worker.exe"), started),
                Row(2, 0, "Runner.Worker.exe", Path.Combine(b, "bin", "Runner.Worker.exe"), started)
            });
            var reads = 0;
            RunnerActivity.ProcessSnapshot Read() { reads++; return snapshot; }

            JobWatch.Update(new[] { Busy(a), Busy(b) }, Read);
            Assert.Equal(1, reads);
            Assert.NotNull(JobWatch.For(a).StartedUtc);
            Assert.NotNull(JobWatch.For(b).StartedUtc);

            // Both readings are fresh, so nothing is read again.
            JobWatch.Update(new[] { Busy(a), Busy(b) }, Read);
            Assert.Equal(1, reads);
        }
        finally
        {
            JobWatch.Update(Array.Empty<RunnerInfo>());
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public void JobWatch_DoesNotCountASleep_AsQuietTime()
    {
        const ulong second = 10_000_000;
        var worker = DateTime.UtcNow.AddHours(-3);
        RunnerActivity.JobSample Sample(ulong cpu) => new(worker, new Dictionary<string, ulong> { ["1|a"] = cpu }, "page|10|1");
        var now = DateTime.UtcNow;
        var quietRoot = @"C:\audit-sleep\quiet";
        var busyRoot = @"C:\audit-sleep\busy";
        try
        {
            // Two readings 30 s apart, then the PC sleeps for two hours and is awake for 60 s before the next reading.
            foreach (var root in new[] { quietRoot, busyRoot })
            {
                JobWatch.Record(root, Sample(0), now.AddHours(-2).AddSeconds(-30), 1_000 * second);
                JobWatch.Record(root, Sample(0), now.AddHours(-2), 1_030 * second);
            }
            JobWatch.Record(quietRoot, Sample(0), now, 1_090 * second);
            // 3.1 s of CPU in the 60 s the PC was awake is more than 5% of a core.
            JobWatch.Record(busyRoot, Sample(31 * second / 10), now, 1_090 * second);

            // Quiet for the 90 s the PC was awake, not since before the sleep.
            var quietSince = JobWatch.For(quietRoot).QuietSinceUtc;
            Assert.NotNull(quietSince);
            Assert.InRange(quietSince!.Value, now.AddMinutes(-2), now.AddMinutes(-1));
            Assert.Null(JobWatch.For(busyRoot).QuietSinceUtc);
        }
        finally { JobWatch.Update(Array.Empty<RunnerInfo>()); }
    }

    [Fact]
    public void OutputFile_ThatWentAway_IsNoProgress()
    {
        var cpu = new Dictionary<string, ulong> { ["10|a"] = 1_000_000 };
        var elapsed = TimeSpan.FromSeconds(30);

        Assert.False(JobWatch.MadeProgress(cpu, cpu, "a_1.log|10|5", string.Empty, elapsed));
        Assert.True(JobWatch.MadeProgress(cpu, cpu, string.Empty, "a_1.log|10|5", elapsed));
        Assert.True(JobWatch.MadeProgress(cpu, cpu, "a_1.log|10|5", "a_1.log|20|6", elapsed));
    }
}
