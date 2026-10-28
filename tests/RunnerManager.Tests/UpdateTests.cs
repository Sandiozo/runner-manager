namespace RunnerManager.Tests;

public class UpdateCheckTests
{
    private const string Release = """
        {
          "tag_name": "v2.4.0",
          "html_url": "https://github.com/Sandiozo/runner-manager/releases/tag/v2.4.0",
          "assets": [
            { "name": "RunnerManagerSetup.exe", "browser_download_url": "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManagerSetup.exe", "size": 10 },
            { "name": "RunnerManager.exe", "browser_download_url": "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe",
              "size": 1234, "digest": "sha256:ABCDEF" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_ReadsTheExeAsset()
    {
        var check = AppUpdater.ParseRelease(Release);
        Assert.NotNull(check.Release);
        Assert.Equal("v2.4.0", check.Release!.Tag);
        Assert.Equal("2.4.0", check.Release.Version);
        Assert.Equal("ABCDEF", check.Release.Sha256);
        Assert.Equal(1234, check.Release.Size);
        Assert.EndsWith("/v2.4.0/RunnerManager.exe", check.Release.DownloadUrl);
    }

    [Fact]
    public void ParseRelease_WithoutTheExe_IsAFailureWithAReason()
    {
        var check = AppUpdater.ParseRelease("""{ "tag_name": "v2.4.0", "assets": [] }""");
        Assert.Null(check.Release);
        Assert.Contains("v2.4.0", check.Problem);
        Assert.False(check.UpdateAvailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("""{ "tag_name": "v2.4.0", "assets": 5 }""")]
    public void ParseRelease_UnreadableAnswer_IsAFailure(string json)
    {
        var check = AppUpdater.ParseRelease(json);
        Assert.Null(check.Release);
        Assert.False(string.IsNullOrEmpty(check.Problem));
    }

    [Fact]
    public void DescribeFailure_RateLimit_SaysWhenToTryAgain()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddMinutes(20).ToUnixTimeSeconds().ToString();
        var text = AppUpdater.DescribeFailure(403, "0", reset, null, now);
        Assert.Contains("limits", text);
        Assert.Contains(now.AddMinutes(20).ToLocalTime().ToString("HH:mm"), text);
        Assert.Contains("limits", AppUpdater.DescribeFailure(429, null, null, TimeSpan.FromSeconds(60), now));
    }

    [Theory]
    [InlineData(404, "No Runner Manager release")]
    [InlineData(500, "HTTP 500")]
    [InlineData(403, "HTTP 403")]
    public void DescribeFailure_OtherErrors(int status, string expected)
        => Assert.Contains(expected, AppUpdater.DescribeFailure(status, "42", null, null, DateTimeOffset.UtcNow));

    [Fact]
    public void DownloadUrl_OnlyUsesThisRepositorysDownloads()
    {
        var release = new ReleaseInfo { Tag = "v2.4.0", DownloadUrl = "https://example.com/RunnerManager.exe" };
        Assert.Equal("https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe", AppUpdater.DownloadUrlFor(release));
        var own = new ReleaseInfo { Tag = "v2.4.0", DownloadUrl = "https://github.com/Sandiozo/runner-manager/releases/download/v2.4.0/RunnerManager.exe" };
        Assert.Equal(own.DownloadUrl, AppUpdater.DownloadUrlFor(own));
    }
}

public class UpdateRollbackTests
{
    private static readonly DateTime Installed = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static PendingUpdate Pending(int failed = 0, bool running = false)
        => new() { Version = "2.4.0", PreviousVersion = "2.3.2", InstalledUtc = Installed, FailedStarts = failed, Running = running };

    [Fact]
    public void AgentStart_OfThePendingVersion_IsWatched()
    {
        var (decision, pending) = UpdateGuard.DecideAgentStart(Pending(), "2.4.0", runningInstalledCopy: true);
        Assert.Equal(AgentStartDecision.Watch, decision);
        Assert.True(pending!.Running);
        Assert.Equal(0, pending.FailedStarts);
    }

    [Fact]
    public void AgentStart_AfterARunThatDidNotFinish_CountsAFailure()
    {
        var (decision, pending) = UpdateGuard.DecideAgentStart(Pending(failed: 1, running: true), "2.4.0", runningInstalledCopy: true);
        Assert.Equal(AgentStartDecision.Watch, decision);
        Assert.Equal(2, pending!.FailedStarts);
    }

    [Fact]
    public void AgentStart_AfterACleanExit_DoesNotCountAFailure()
    {
        var (_, pending) = UpdateGuard.DecideAgentStart(Pending(failed: 2, running: false), "2.4.0", runningInstalledCopy: true);
        Assert.Equal(2, pending!.FailedStarts);
    }

    [Fact]
    public void AgentStart_AfterRepeatedCrashes_RollsBack()
    {
        var (decision, pending) = UpdateGuard.DecideAgentStart(Pending(failed: UpdateGuard.MaxFailedStarts - 1, running: true), "2.4.0", true);
        Assert.Equal(AgentStartDecision.RollBack, decision);
        Assert.False(pending!.Running);
    }

    [Theory]
    [InlineData("2.3.2", true)]
    [InlineData("2.4.0", false)]
    public void AgentStart_OfAnotherVersionOrCopy_IsIgnored(string running, bool installedCopy)
    {
        var start = Pending(failed: 5, running: true);
        var (decision, pending) = UpdateGuard.DecideAgentStart(start, running, installedCopy);
        Assert.Equal(AgentStartDecision.Nothing, decision);
        Assert.Same(start, pending);
        Assert.Equal(AgentStartDecision.Nothing, UpdateGuard.DecideAgentStart(null, "2.4.0", true).Decision);
    }

    private static DashboardUpdateDecision Dashboard(PendingUpdate? pending, string installed = "2.4.0", bool agentInstalled = true,
        bool agentRunning = false, double minutesSinceInstall = 30, double uptimeMinutes = 10, string running = "2.4.0")
        => UpdateGuard.DecideForDashboard(pending, running, installed, agentInstalled, agentRunning,
            Installed.AddMinutes(minutesSinceInstall), TimeSpan.FromMinutes(uptimeMinutes));

    [Fact]
    public void Dashboard_AgentThatNeverStarted_RollsBack()
        => Assert.Equal(DashboardUpdateDecision.RollBack, Dashboard(Pending()));

    [Fact]
    public void Dashboard_WaitsForTheAgent()
    {
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), agentRunning: true));
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), minutesSinceInstall: 5));
        // Just signed in: the agent's logon task may not have started yet.
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), uptimeMinutes: 1));
    }

    [Fact]
    public void Dashboard_WithoutAnAgent_MarksTheUpdateHealthyAfterRunning()
    {
        Assert.Equal(DashboardUpdateDecision.MarkHealthy, Dashboard(Pending(), agentInstalled: false, uptimeMinutes: 3));
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), agentInstalled: false, uptimeMinutes: 1));
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), agentInstalled: false, running: "2.3.2"));
    }

    [Fact]
    public void Dashboard_ForgetsAnUpdateReplacedSince()
    {
        Assert.Equal(DashboardUpdateDecision.Forget, Dashboard(Pending(), installed: "2.5.0"));
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(null));
    }
}
