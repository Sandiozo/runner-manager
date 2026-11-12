using System.IO;
using System.Windows;
using System.Windows.Controls;

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
        var wait = MachineRelay.WaitForReadAsync(stuck.Task, () => limit.Task);
        Assert.False(wait.IsCompleted);
        limit.SetResult();
        Assert.False(await wait);

        // A read that ends first, also when it fails.
        var read = new TaskCompletionSource<List<MachineInfo>>();
        wait = MachineRelay.WaitForReadAsync(read.Task, () => never.Task);
        Assert.False(wait.IsCompleted);
        read.SetException(new IOException("The network is not reachable."));
        Assert.True(await wait);

        // No read runs: nothing to wait for, and the limit is not started.
        var limitStarted = false;
        Task Limit() { limitStarted = true; return never.Task; }
        Assert.True(await MachineRelay.WaitForReadAsync(null, Limit));
        Assert.True(await MachineRelay.WaitForReadAsync(Task.CompletedTask, Limit));
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

    // ----- 12: this PC's card on a PC managed one way names its access code -----

    [Fact]
    public void ThisPcsCard_OnAPcManagedOneWay_NamesItsAccessCode()
    {
        // fleet.json of the test state folder; other tests that use it do not run beside the dashboard tests.
        var path = Path.Combine(FleetConfig.StateDir, "fleet.json");
        Directory.CreateDirectory(FleetConfig.StateDir);
        var saved = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            var local = MachineInfo.FromPayload(Environment.MachineName.ToLowerInvariant(),
                new MachineStatusPayload { MachineName = Environment.MachineName, SigningKeyId = "d4e5f6" });
            var other = MachineInfo.FromPayload("build-55", new MachineStatusPayload { MachineName = "BUILD-55", SigningKeyId = "a1b2c3" });
            Assert.True(local.IsLocal);
            Assert.False(local.IsManaged);

            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            Assert.Equal("Paired. Connections to other PCs are encrypted with the fleet code (fingerprint d4e5f6).", local.SignedTip);

            FleetConfig.BecomeManaged(CommandSigner.NewKey(), "HOSTPC");
            Assert.Equal("Managed one way. Connections to this PC are encrypted with its own access code (fingerprint d4e5f6), "
                         + "which it cannot use to reach the other PCs.", local.SignedTip);
            // Another PC's card says what that PC is, not what this PC is.
            Assert.Equal("Paired. Connections to other PCs are encrypted with the fleet code (fingerprint a1b2c3).", other.SignedTip);
        }
        finally
        {
            if (saved != null)
            {
                File.WriteAllText(path, saved);
                // A new time, so the cached settings are read again.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1).AddTicks(Random.Shared.Next()));
            }
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    // ----- 14: back-to-back jobs do not show the last job's output as live -----

    private static readonly DateTime JobA = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime JobB = JobA.AddMinutes(12);

    private static MachineRunnerStatus OpenRunner(string output, DateTime? workerStarted, bool busy = true, string url = "") => new()
    {
        ServiceName = "actions.runner.api", DisplayName = "api", MachineKey = "BUILD-56", MachineName = "BUILD-56",
        ServiceInstalled = true, IsRunning = true, ActivityText = busy ? "Busy" : "Idle", IsExpanded = true,
        JobStartedUtc = busy ? workerStarted : null, LiveJob = busy ? new LiveJobView { Output = output, Url = url } : null
    };

    private static (Dictionary<string, MainWindow.OutputCopy> Live, Dictionary<string, MainWindow.OutputCopy> Finished) Outputs()
        => (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void JobOutput_OfTheLastJob_IsNotShownAsLive_WhenTheNextJobStartsRightAfterIt()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("error CS1002: ; expected", JobA) }, live, finished);
        Assert.Single(live);

        // No reading showed the runner idle in between: job B has started and has no output yet.
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", JobB) }, live, finished);
        Assert.Empty(live);
        Assert.Empty(finished);

        // Job B's output, when it comes, is live.
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("Restoring packages", JobB) }, live, finished);
        Assert.Equal("Restoring packages", Assert.Single(live).Value.Text);
    }

    [Fact]
    public void JobOutput_StaysLive_WhileTheSameJobStillShows()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("error MSB3073: exited with code 1", JobA) }, live, finished);
        // Its worker has ended, so there is no output, but the runner still shows the same job.
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", JobA) }, live, finished);
        Assert.Equal("error MSB3073: exited with code 1", Assert.Single(live).Value.Text);
        // A reading that does not tell which job it is changes nothing.
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", null) }, live, finished);
        Assert.Single(live);
        // Then the runner is idle: the output is that of a job that has finished.
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", null, busy: false) }, live, finished);
        Assert.Empty(live);
        Assert.Equal("error MSB3073: exited with code 1", Assert.Single(finished).Value.Text);
    }

    [Fact]
    public void JobOutput_FromAnOlderAgent_TellsTheJobByItsPageOnGitHub()
    {
        const string first = "https://github.com/octo/api/actions/runs/7/job/70";
        const string next = "https://github.com/octo/api/actions/runs/8/job/80";
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("Build failed", null, url: first) }, live, finished);
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", null, url: first) }, live, finished);
        Assert.Single(live);
        MainWindow.KeepFinishedOutput(new[] { OpenRunner("", null, url: next) }, live, finished);
        Assert.Empty(live);
        Assert.Empty(finished);
    }

    // ----- 8: an open runner's details line up with its title -----

    [Fact]
    public void OpenRunner_DetailsStartWhereItsTitleStarts()
    {
        var runner = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.api", DisplayName = "api", ServiceInstalled = true, IsRunning = true, ActivityText = "Idle"
        };
        var pc = PcWith("BUILD-57", runner);
        var window = WpfTestApp.Run(() => new MainWindow());
        WpfTestApp.Run(() =>
        {
            runner.IsExpanded = true;
            window.Machines.Add(pc);
            var content = (UIElement)window.Content;
            window.Content = null;
            // The cards' items come from the window, which is not around them any more.
            var host = new Border { Child = content, DataContext = window };
            host.Measure(new Size(1200, 900));
            host.Arrange(new Rect(0, 0, 1200, 900));
            host.UpdateLayout();

            var row = WpfTestApp.Descendants<Border>(host)
                .First(b => ReferenceEquals(b.DataContext, runner) && b.Child is Grid { RowDefinitions.Count: 2 });
            var card = (Grid)row.Child;
            var header = card.Children.OfType<Button>().First(b => Grid.GetColumnSpan(b) == 3);
            var title = WpfTestApp.Descendants<TextBlock>(header).First(t => t.Text == "api");
            var glyph = WpfTestApp.Descendants<TextBlock>(header).First(t => t.Text == runner.ExpandGlyph);
            var details = card.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 1);
            Assert.Equal(Visibility.Visible, details.Visibility);

            var titleLeft = title.TranslatePoint(new Point(0, 0), card).X;
            var detailsLeft = details.TranslatePoint(new Point(0, 0), card).X;
            Assert.True(titleLeft > 10, $"The title starts at {titleLeft}");
            Assert.Equal(titleLeft, detailsLeft, 1);
            // The chevron keeps its whole width in front of the title.
            Assert.True(glyph.TranslatePoint(new Point(glyph.ActualWidth, 0), card).X <= titleLeft,
                $"The chevron ends at {glyph.TranslatePoint(new Point(glyph.ActualWidth, 0), card).X}, the title starts at {titleLeft}");
            Assert.True(glyph.ActualWidth >= glyph.DesiredSize.Width - glyph.Margin.Left - glyph.Margin.Right - 0.5,
                $"The chevron is {glyph.ActualWidth} wide and wants {glyph.DesiredSize.Width}");
        });
    }

    // ----- 10: a sign-in check that failed -----

    [Fact]
    public void SignInCheck_ThatFailed_HasStillAnswered()
    {
        // gh gave no answer: what was shown stays, and the check counts as answered, so "Checking…" ends.
        Assert.False(MainWindow.SignInToShow(wasAuthenticated: false, answer: null, changedMeanwhile: false));
        Assert.True(MainWindow.SignInToShow(wasAuthenticated: true, answer: null, changedMeanwhile: false));
    }
}
