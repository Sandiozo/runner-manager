using System.IO;

namespace RunnerManager.Tests;

public class JobHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

    [Fact]
    public void LongLimit_IsThreeTimesUsual_AtLeast20Minutes_Or2HoursWithoutHistory()
    {
        Assert.Equal(TimeSpan.FromMinutes(75), JobHealth.LongLimit(TimeSpan.FromMinutes(25)));
        Assert.Equal(TimeSpan.FromMinutes(20), JobHealth.LongLimit(TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromHours(2), JobHealth.LongLimit(null));
    }

    [Fact]
    public void Usual_IsTheMedian_OfAtLeastThreeRuns()
    {
        Assert.Null(JobHealth.Usual(new[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6) }));
        Assert.Equal(TimeSpan.FromMinutes(6),
            JobHealth.Usual(new[] { TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6) }));
    }

    [Fact]
    public void BusyRunner_IsStuckWhenQuietLongEnough_BeforeItCountsAsLong()
    {
        var started = Now.AddHours(-3);
        var stuck = JobHealth.ForBusyRunner(Now.AddMinutes(-16), started, TimeSpan.FromMinutes(10), Quiet, "job1", Now);
        Assert.Equal(RunnerConcern.Stuck, stuck.Concern);
        Assert.Contains("16 min", stuck.Detail);

        var quietButShort = JobHealth.ForBusyRunner(Now.AddMinutes(-5), Now.AddMinutes(-10), TimeSpan.FromMinutes(10), Quiet, "job1", Now);
        Assert.False(quietButShort.IsProblem);

        var running = JobHealth.ForBusyRunner(null, Now.AddMinutes(-40), TimeSpan.FromMinutes(10), Quiet, "job1", Now);
        Assert.Equal(RunnerConcern.RunningLong, running.Concern);
        Assert.Contains("usually takes 10 min", running.Detail);

        Assert.False(JobHealth.ForBusyRunner(null, Now.AddMinutes(-100), null, Quiet, "job1", Now).IsProblem);
        Assert.Equal(RunnerConcern.RunningLong, JobHealth.ForBusyRunner(null, Now.AddMinutes(-121), null, Quiet, "job1", Now).Concern);
    }

    [Fact]
    public void IdleRunner_IsFlagged_OnlyWhenAJobWaitedFiveMinutesWhileItWasIdle()
    {
        var job = new GitHubJobState { Id = 7, Status = "queued", CreatedUtc = Now.AddMinutes(-30) };

        // Idle for only two minutes: it may have just finished a job, and GitHub assigns within seconds.
        Assert.False(JobHealth.ForIdleRunner(new[] { job }, Now.AddMinutes(-2), Now).IsProblem);
        var waiting = JobHealth.ForIdleRunner(new[] { job }, Now.AddMinutes(-6), Now);
        Assert.Equal(RunnerConcern.IdleWithWaitingJobs, waiting.Concern);
        Assert.Contains("6 min", waiting.Detail);
        // Without knowing when it became idle, the wait counts from when the job was queued.
        Assert.True(JobHealth.ForIdleRunner(new[] { job }, null, Now).IsProblem);
        Assert.False(JobHealth.ForIdleRunner(new[] { new GitHubJobState { Id = 8, Status = "queued", CreatedUtc = Now.AddMinutes(-3) } }, null, Now).IsProblem);
    }

    [Fact]
    public void Progress_IsNewOutput_ANewProcess_OrEnoughCpu()
    {
        var before = new Dictionary<string, ulong> { ["10|a"] = 1_000_000 };
        var elapsed = TimeSpan.FromSeconds(30);
        // 5% of one core over 30 s is 1.5 s of processor time (15,000,000 ticks).
        Assert.True(JobWatch.MadeProgress(before, new Dictionary<string, ulong> { ["10|a"] = 1_000_000 + 15_000_000 }, "o", "o", elapsed));
        Assert.False(JobWatch.MadeProgress(before, new Dictionary<string, ulong> { ["10|a"] = 1_000_000 + 1_000_000 }, "o", "o", elapsed));
        Assert.True(JobWatch.MadeProgress(before, new Dictionary<string, ulong> { ["10|a"] = 1_000_000 }, "o", "o2", elapsed));
        Assert.True(JobWatch.MadeProgress(before, new Dictionary<string, ulong> { ["10|a"] = 1_000_000, ["11|b"] = 0 }, "o", "o", elapsed));
        // A process that ended is not progress.
        Assert.False(JobWatch.MadeProgress(before, new Dictionary<string, ulong>(), "o", "o", elapsed));
    }

    [Fact]
    public void Queue_ListsRunningJobsFirst_ThenWaitingOnesByAge()
    {
        var queue = JobHealth.QueueFor(new[]
        {
            new GitHubJobState { Name = "late", WorkflowName = "CI", RunNumber = 3, Status = "queued", CreatedUtc = Now.AddMinutes(-2) },
            new GitHubJobState { Name = "early", WorkflowName = "CI", RunNumber = 2, Status = "queued", CreatedUtc = Now.AddMinutes(-12) },
            new GitHubJobState { Name = "build", WorkflowName = "CI", RunNumber = 1, Status = "in_progress", RunnerName = "livonia-core", StartedUtc = Now.AddMinutes(-4) }
        }, Now);

        Assert.Equal(new[] { "build", "early", "late" }, queue.Select(q => q.Title.Split(" → ")[1].Split("  ")[0]));
        Assert.True(queue[0].Running);
        Assert.Equal("Running on livonia-core  ·  4 min", queue[0].Detail);
        Assert.Equal("Waiting  ·  12 min", queue[1].Detail);
    }

    [Fact]
    public async Task Fleet_WithoutGitHub_StillFindsStuckJobs()
    {
        var runner = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.o-r.a", IsRunning = true, ActivityText = "Busy", ServiceInstalled = true,
            JobStartedUtc = DateTime.UtcNow.AddMinutes(-40), QuietSinceUtc = DateTime.UtcNow.AddMinutes(-20), MachineKey = "pc"
        };
        var machine = MachineInfo.FromPayload("pc", new MachineStatusPayload { MachineName = "PC-A", Runners = new[] { runner } });

        var health = await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, useGitHub: false, DateTime.UtcNow);

        Assert.Equal(RunnerConcern.Stuck, health.Runners[JobHealth.Key(machine.Runners[0])].Concern);
        Assert.Empty(health.Unserved);
    }

    [Fact]
    public void StuckRunner_ShowsInItsStatus()
    {
        var runner = new MachineRunnerStatus { ServiceName = "s", IsRunning = true, ActivityText = "Busy", ServiceInstalled = true, IsLocalMachine = true };
        Assert.Equal("Busy", runner.StatusText);
        runner.Health = new RunnerHealth(RunnerConcern.Stuck, "Stuck", "No CPU for 20 min.");
        Assert.Equal("Stuck", runner.StatusText);
        Assert.Equal("No CPU for 20 min.", runner.StatusTip);
        Assert.True(runner.CanStopJob);
        Assert.Equal(System.Windows.Visibility.Visible, runner.StopJobVisibility);
    }
}

public class StopJobProcessesTests
{
    [Fact]
    public void StopsWhatTheJobStarted_ButNotTheRunnersWorker()
    {
        var root = Directory.CreateTempSubdirectory("rm-job-").FullName;
        System.Diagnostics.Process? worker = null;
        try
        {
            // A stand-in for the runner's job process: cmd.exe named Runner.Worker.exe, running a long ping.
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            var exe = Path.Combine(root, "bin", "Runner.Worker.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe);
            worker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/d /c ping -n 60 127.0.0.1 >nul & ping -n 60 127.0.0.1 >nul")
            {
                UseShellExecute = false, CreateNoWindow = true
            })!;
            RunnerActivity.JobSample? sample = null;
            for (var i = 0; i < 50 && (sample == null || sample.ProcessorTime.Count < 2); i++)
            {
                Thread.Sleep(100);
                sample = RunnerActivity.ReadJobSample(root);
            }
            Assert.NotNull(sample);
            Assert.True(sample!.ProcessorTime.Count >= 2, "The worker and its ping should both be found.");

            var stopped = RunnerActivity.StopJobProcesses(root);

            Assert.Contains("PING", stopped, StringComparer.OrdinalIgnoreCase);
            Assert.False(worker.HasExited);
        }
        finally
        {
            try { worker?.Kill(entireProcessTree: true); worker?.WaitForExit(5000); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
