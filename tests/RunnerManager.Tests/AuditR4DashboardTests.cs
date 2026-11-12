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
}
