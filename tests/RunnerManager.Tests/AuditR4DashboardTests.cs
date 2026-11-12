using System.Windows;

namespace RunnerManager.Tests;

/// <summary>The dashboard's fixes of round 4 of the audit (release 2.9.0).</summary>
[Collection(DashboardCollection.Name)]
public class AuditR4DashboardTests
{
    private static MachineInfo PcWith(string name, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name.ToLowerInvariant(), new MachineStatusPayload
        {
            MachineName = name, AgentProtocolVersion = 14, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = runners
        });

    // ----- 1: a runner stopped on purpose does not need attention -----

    [Fact]
    public void NeedsAttention_LeavesOutARunnerStoppedWithStop_ButCountsAStoppedConsoleRunner()
    {
        var spare = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.spare", DisplayName = "spare", ServiceInstalled = true, IsRunning = false, ManuallyStopped = true
        };
        var crashed = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.api", DisplayName = "api", ServiceInstalled = true, IsRunning = false
        };
        // Opens in a console window when someone signs in, and nobody is signed in on the PC.
        var console = new MachineRunnerStatus
        {
            InstallPath = @"C:\runners\unity", DisplayName = "unity", IsRunning = false, StartsAt = RunnerStart.SignIn
        };
        var running = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.web", DisplayName = "web", ServiceInstalled = true, IsRunning = true, ActivityText = "Idle"
        };
        Assert.Equal("Console · stopped", console.StatusText);
        var pc = PcWith("BUILD-50", spare, crashed, console, running);

        var attention = FleetAttention.Count(new[] { pc });
        Assert.Equal(2, attention.StoppedRunners);
        Assert.Equal("2 runners are stopped.", attention.Summary);
        // The filter shows what the tile counts.
        Assert.Equal(new[] { "api", "unity" }, pc.Runners.Where(r => FleetAttention.ShowsRunner(pc, r)).Select(r => r.DisplayName).OrderBy(n => n));

        // Only the spare runner is stopped: nothing needs attention, and the PC is not shown by the filter.
        var quiet = PcWith("BUILD-51", spare, running);
        Assert.Equal(0, FleetAttention.Count(new[] { quiet }).Total);
        Assert.False(FleetAttention.PcNeedsAttention(quiet));
        Assert.False(FleetAttention.RunnerNeedsAttention(spare));
        // A runner that stopped without Stop does.
        Assert.True(FleetAttention.RunnerNeedsAttention(crashed));
    }

    // ----- 2: adding a runner on another PC does not freeze this PC's view -----

    [Theory]
    [InlineData(false, false, false)]
    // Delete, Move, Join fleet, Add runner on this PC, ...: this PC is read again when it ends.
    [InlineData(true, false, true)]
    // Add runner on another PC: it waits for that PC, maybe for many minutes, and changes nothing here.
    [InlineData(true, true, false)]
    public void Refreshes_ReadThisPc_WhileOnlyAnotherPcIsWorkedOn(bool operationRunning, bool onOtherPcOnly, bool holdsBack)
        => Assert.Equal(holdsBack, MainWindow.HoldsThisPcBack(operationRunning, onOtherPcOnly));

    // ----- 3: this PC's runners are read again when the sign-in changed since they were read -----

    [Fact]
    public void ThisPcsRunners_AreReadAgain_WhenTheyWereReadWithAnotherSignIn()
    {
        var justNow = TimeSpan.FromSeconds(2);
        // Start-up: the first refresh read them without GitHub, then the sign-in check found GitHub connected.
        Assert.True(MainWindow.ReadsLocalRunners(forced: false, noneKnown: false, lastReadWithGitHub: false, gitHubConnected: true, justNow));
        // Disconnected since they were read with GitHub.
        Assert.True(MainWindow.ReadsLocalRunners(false, false, lastReadWithGitHub: true, gitHubConnected: false, justNow));
        // Read the way GitHub is now: again only after 30 seconds.
        Assert.False(MainWindow.ReadsLocalRunners(false, false, lastReadWithGitHub: true, gitHubConnected: true, justNow));
        Assert.False(MainWindow.ReadsLocalRunners(false, false, lastReadWithGitHub: false, gitHubConnected: false, justNow));
        Assert.True(MainWindow.ReadsLocalRunners(false, false, true, true, TimeSpan.FromSeconds(30)));
        // Forced, none known, or not read yet.
        Assert.True(MainWindow.ReadsLocalRunners(forced: true, false, true, true, justNow));
        Assert.True(MainWindow.ReadsLocalRunners(false, noneKnown: true, true, true, justNow));
        Assert.True(MainWindow.ReadsLocalRunners(false, false, lastReadWithGitHub: null, gitHubConnected: false, justNow));
    }

    // ----- 4: a sign-in check that started before Disconnect or Reconnect -----

    [Fact]
    public void SignInCheck_ThatStartedBeforeDisconnect_ThrowsItsAnswerAway()
    {
        // gh still said "connected" to the check that started before Disconnect: it does not show "Connected" again.
        Assert.Null(MainWindow.SignInToShow(wasAuthenticated: true, answer: true, changedMeanwhile: true));
        Assert.Null(MainWindow.SignInToShow(wasAuthenticated: true, answer: null, changedMeanwhile: true));
        // A check that started after it shows its answer.
        Assert.False(MainWindow.SignInToShow(wasAuthenticated: false, answer: false, changedMeanwhile: false));
        Assert.True(MainWindow.SignInToShow(wasAuthenticated: false, answer: true, changedMeanwhile: false));
    }

    // ----- 5: job alerts are those the cards show -----

    private static MachineRunnerStatus ServiceRunner(string name, bool busy) => new()
    {
        ServiceName = $"actions.runner.{name}", DisplayName = name, ServiceInstalled = true, IsRunning = true, ActivityText = busy ? "Busy" : "Idle"
    };

    [Fact]
    public void JobAlerts_LeaveOutAJobThatEndedWhileTheCheckAskedGitHub()
    {
        var stuck = new RunnerHealth(RunnerConcern.Stuck, "Stuck", "The job has used no CPU and printed no output for 30 min.", "job-7");
        // The check found both jobs stuck. A newer reading came while it asked GitHub: the api job has ended.
        var ended = ServiceRunner("api", busy: false);
        var running = ServiceRunner("build", busy: true);
        ended.Health = MainWindow.StillApplies(stuck, ended);
        running.Health = MainWindow.StillApplies(stuck, running);
        var pc = PcWith("BUILD-52", ended, running);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new[] { "BUILD-52: build — Stuck. The job has used no CPU and printed no output for 30 min." },
            MainWindow.NewProblemAlerts(new[] { pc }, reported));
        // Each concern is reported once.
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { pc }, reported));
    }

    // ----- 7: Join and Leave wait for a fleet read at most 30 seconds -----

    [Fact]
    public async Task JoinOrLeave_WaitsForAFleetReadAtMostItsLimit()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), MainWindow.FleetReadWait);
        // The limit stands in for the 30 seconds.
        var never = new TaskCompletionSource();

        // A read that does not end: the wait ends when the limit has passed.
        var stuck = new TaskCompletionSource<List<MachineInfo>>();
        var limit = new TaskCompletionSource();
        var wait = MainWindow.WaitForReadAsync(stuck.Task, () => limit.Task);
        Assert.False(wait.IsCompleted);
        limit.SetResult();
        Assert.False(await wait);

        // A read that ends first, also when it fails.
        var read = new TaskCompletionSource<List<MachineInfo>>();
        wait = MainWindow.WaitForReadAsync(read.Task, () => never.Task);
        Assert.False(wait.IsCompleted);
        read.SetException(new System.IO.IOException("The network is not reachable."));
        Assert.True(await wait);

        // No read runs: nothing to wait for, and the limit is not started.
        var limitStarted = false;
        Task Limit() { limitStarted = true; return never.Task; }
        Assert.True(await MainWindow.WaitForReadAsync(null, Limit));
        Assert.True(await MainWindow.WaitForReadAsync(Task.CompletedTask, Limit));
        Assert.False(limitStarted);
    }

    // ----- 9: adding a runner on another PC does not need this PC's GitHub sign-in -----

    [Fact]
    public void AddRunner_AsksForThisPcsSignInFirst_OnlyWhenNoOtherPcCanBeChosen()
    {
        var local = PcWith(Environment.MachineName);
        var online = PcWith("BUILD-53");
        var offline = MachineInfo.Placeholder("build-54", "BUILD-54", DateTime.UtcNow.AddHours(-1), "10.0.0.54", "No answer.");
        Assert.True(MainWindow.OnlyThisPcToAddOn(new[] { local }));
        Assert.True(MainWindow.OnlyThisPcToAddOn(new[] { local, offline }));
        // Another PC can be chosen, and it registers its runner with its own sign-in.
        Assert.False(MainWindow.OnlyThisPcToAddOn(new[] { local, online, offline }));
    }

    [Fact]
    public void AddRunner_OnAnotherPc_SkipsTheWorkflowCheckWithoutThisPcsSignIn()
    {
        var (check, note) = MainWindow.WorkflowCheckAfterRemoteAdd(asked: true, "octo/api", signedIn: false);
        Assert.False(check);
        Assert.Equal("Warning: the workflow check was skipped because GitHub is not connected on this PC. The runner was created.", note);

        Assert.Equal((true, ""), MainWindow.WorkflowCheckAfterRemoteAdd(asked: true, "octo/api", signedIn: true));
        // Not asked for, or an organization runner: nothing to check or say.
        Assert.Equal((false, ""), MainWindow.WorkflowCheckAfterRemoteAdd(asked: false, "octo/api", signedIn: false));
        Assert.Equal((false, ""), MainWindow.WorkflowCheckAfterRemoteAdd(asked: true, "", signedIn: true));
    }

    // ----- 11: the job panel before the first sign-in check has answered -----

    [Theory]
    [InlineData(false, false, "Checking the GitHub connection…")]
    [InlineData(true, false, "Connect GitHub to see the job and its steps.")]
    [InlineData(true, true, null)]
    public void JobPanel_AsksToConnectGitHub_OnlyAfterTheCheckFoundNoSignIn(bool signInChecked, bool signedIn, string? note)
        => Assert.Equal(note, MainWindow.JobNoteWithoutSignIn(signInChecked, signedIn));

    // ----- 10: a sign-in check that failed -----

    [Fact]
    public void SignInCheck_ThatFailed_HasStillAnswered()
    {
        // gh gave no answer: what was shown stays, and the check counts as answered, so "Checking…" ends.
        Assert.False(MainWindow.SignInToShow(wasAuthenticated: false, answer: null, changedMeanwhile: false));
        Assert.True(MainWindow.SignInToShow(wasAuthenticated: true, answer: null, changedMeanwhile: false));
    }
}
