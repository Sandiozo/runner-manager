namespace RunnerManager.Tests;

public class RunnerRecoveryTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private const string Key = "actions.runner.owner-repo.pc";

    /// <summary>One watchdog check of a runner service that is not a console runner and may be recovered.</summary>
    private static RunnerRecovery.Step Check(RunnerRecovery recovery, DateTime now, bool running = false, bool pending = false, bool mayRecover = true)
        => recovery.Check(Key, interactive: false, running, pending, mayRecover, now);

    [Fact]
    public void AStoppedRunner_IsStarted_OnlyWhenItIsStillStoppedAtTheNextCheck()
    {
        var recovery = new RunnerRecovery();
        Check(recovery, Start, running: true);

        // A Restart made on the dashboard passes through stopped: one reading is not enough.
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(10)));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(20), running: true));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(30)));
        Assert.Equal(RunnerRecovery.Step.Start, Check(recovery, Start.AddSeconds(40)));
    }

    [Fact]
    public void AServiceThatIsStartingOrStopping_IsLeftAlone()
    {
        var recovery = new RunnerRecovery();
        Check(recovery, Start, running: true);

        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(10), pending: true));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(20), pending: true));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(30)));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(40), pending: true));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(50)));
        Assert.Equal(RunnerRecovery.Step.Start, Check(recovery, Start.AddSeconds(60)));
    }

    [Fact]
    public void AfterThreeAttempts_RecoveryStops_IsReportedOnce_AndResumesWhenTheAttemptsExpire()
    {
        var recovery = new RunnerRecovery();
        var now = Start;
        Check(recovery, now, running: true);
        var steps = new List<RunnerRecovery.Step>();
        // The service does not stay up: it is stopped at every check.
        for (var i = 0; i < 60; i++)
        {
            now = now.AddSeconds(10);
            steps.Add(Check(recovery, now));
        }
        Assert.Equal(RunnerRecovery.MaxAttempts, steps.Count(s => s == RunnerRecovery.Step.Start));
        Assert.Single(steps, s => s == RunnerRecovery.Step.GiveUp);

        // An hour after the first attempt, it is tried again; the give-up is reported again when those run out.
        steps.Clear();
        now = Start.AddHours(1).AddSeconds(30);
        for (var i = 0; i < 60; i++)
        {
            now = now.AddSeconds(10);
            steps.Add(Check(recovery, now));
        }
        Assert.Contains(RunnerRecovery.Step.Start, steps);
        Assert.Single(steps, s => s == RunnerRecovery.Step.GiveUp);
    }

    [Fact]
    public void ARunnerThatStopsSoonAfterEachStart_IsStillStartedOnlyThreeTimesAnHour()
    {
        var recovery = new RunnerRecovery();
        var now = Start;
        var starts = 0;
        Check(recovery, now, running: true);
        for (var i = 0; i < 100; i++)
        {
            now = now.AddSeconds(10);
            if (Check(recovery, now) == RunnerRecovery.Step.Start)
            {
                starts++;
                now = now.AddSeconds(10);
                Check(recovery, now, running: true);
            }
        }
        Assert.Equal(RunnerRecovery.MaxAttempts, starts);
    }

    [Fact]
    public void ConsoleRunners_RunnersStoppedOnPurpose_AndRunnersNeverSeenRunning_AreLeftAlone()
    {
        var recovery = new RunnerRecovery();
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(10)));

        Check(recovery, Start.AddSeconds(20), running: true);
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(30), mayRecover: false));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(40), mayRecover: false));

        Check(recovery, Start.AddSeconds(50), running: true);
        recovery.Forget(Key);
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(60)));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(70)));

        Check(recovery, Start.AddSeconds(80), running: true);
        Assert.Equal(RunnerRecovery.Step.None, recovery.Check(Key, interactive: true, running: false, pending: false, mayRecover: true, Start.AddSeconds(90)));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(100)));
        Assert.Equal(RunnerRecovery.Step.None, Check(recovery, Start.AddSeconds(110)));
    }

    [Theory]
    [InlineData("Start Pending", true)]
    [InlineData("Stop Pending", true)]
    [InlineData("Stopped", false)]
    [InlineData("Running", false)]
    public void PendingServiceStates_AreRecognized(string state, bool pending)
        => Assert.Equal(pending, MachineRelay.IsPending(new RunnerInfo { StatusText = state }));
}
