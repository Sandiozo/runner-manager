using System.IO;

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
        double agentDownMinutes = 5, double minutesSinceInstall = 30, double uptimeMinutes = 10, string running = "2.4.0")
        => UpdateGuard.DecideForDashboard(pending, running, installed, agentInstalled, TimeSpan.FromMinutes(agentDownMinutes),
            Installed.AddMinutes(minutesSinceInstall), TimeSpan.FromMinutes(uptimeMinutes));

    [Fact]
    public void Dashboard_AgentThatNeverStarted_RollsBack()
        => Assert.Equal(DashboardUpdateDecision.RollBack, Dashboard(Pending()));

    [Fact]
    public void Dashboard_WaitsForTheAgent()
    {
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), agentDownMinutes: 0));
        // Seen stopped only briefly: it may be restarting.
        Assert.Equal(DashboardUpdateDecision.Nothing, Dashboard(Pending(), agentDownMinutes: 1));
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

/// <summary>
/// update-state.json is in the data folder, which any program of the user can change. A pending update from it counts
/// only with what the install folder shows (see <see cref="UpdateGuard.PendingCounts"/>), and the counts beside the exe
/// decide, so a program without administrator rights cannot make the fleet agent go back to the previous version of a
/// Program Files install, nor stop the automatic update. The tests share update-state.json, so they are in one class.
/// </summary>
public class UpdateMarkerTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PendingUpdate Planted = new() { Version = "2.11.0", PreviousVersion = "2.10.0", InstalledUtc = Now, FailedStarts = 3 };

    private static UpdateGuard.InstallFacts Facts(string marker = "", string previous = "2.10.0", double hoursSinceWritten = 1)
        => new(marker, previous, Now.AddHours(-hoursSinceWritten));

    [Fact]
    public void APendingUpdate_CountsOnlyWhenTheMarkerBesideTheExeNamesIt()
    {
        Assert.True(UpdateGuard.PendingCounts(Planted, Facts(marker: "2.11.0"), Now));
        // Written by hand into the update state: the install folder has no marker, or one of another update.
        Assert.False(UpdateGuard.PendingCounts(Planted, Facts(), Now));
        Assert.False(UpdateGuard.PendingCounts(Planted, Facts(marker: "2.12.0"), Now));
    }

    [Fact]
    public void WithTheMarker_TheUpdateIsUndone_AlsoWhenTheNewVersionNeverStarted()
    {
        // The version that installed the update wrote the marker, so its dashboard can go back however long the new
        // version has failed to start (the PC may have been off since).
        Assert.True(UpdateGuard.PendingCounts(Planted, Facts(marker: "2.11.0", hoursSinceWritten: 72), Now));
        Assert.True(UpdateGuard.PendingCounts(Planted, new UpdateGuard.InstallFacts("2.11.0", "2.10.0", null), Now));
    }

    [Theory]
    [InlineData("2.9.0", 1, true)]
    [InlineData("2.9.0", 23.9, true)]
    [InlineData("2.9.0", 24.1, false)]
    [InlineData("2.10.0", 1, false)]
    [InlineData("2.10.1", 1, false)]
    [InlineData("", 1, false)]
    public void TheUpdateFrom290_CountsWithoutAMarker_OnlyForADayAfterTheExeWasWritten(string previous, double hours, bool counts)
    {
        // 2.9.0 wrote no marker when it installed 2.10.0. The previous exe (2.9.0) and the time the installed exe was written
        // are in the install folder, which only administrators can change in Program Files.
        var fromOld = new PendingUpdate { Version = "2.10.0", PreviousVersion = "2.9.0", InstalledUtc = Now.AddHours(-hours) };
        Assert.Equal(counts, UpdateGuard.PendingCounts(fromOld, Facts(previous: previous, hoursSinceWritten: hours), Now));
        Assert.False(UpdateGuard.PendingCounts(fromOld, new UpdateGuard.InstallFacts(string.Empty, previous, null), Now));
    }

    [Fact]
    public void TheInstallFolder_IsReadForTheFacts_AndTheMarkerIsWrittenAndDeleted()
    {
        var folder = InstallEvidenceTests.NewFolder("pending");
        try
        {
            var exe = Path.Combine(folder, AppPaths.ExeName);
            File.WriteAllText(exe, string.Empty);
            var written = new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(exe, written);
            Assert.Equal(new UpdateGuard.InstallFacts(string.Empty, string.Empty, written), UpdateGuard.ReadInstallFacts(exe));

            File.Copy(typeof(UpdateGuard).Assembly.Location, exe + ".previous");
            UpdateGuard.WritePendingMarker(exe, "2.11.0");
            Assert.Equal(Path.Combine(folder, "RunnerManager.exe.pending"), UpdateGuard.PendingMarkerPath(exe));
            Assert.Equal(new UpdateGuard.InstallFacts("2.11.0", AppUpdater.CurrentVersion, written), UpdateGuard.ReadInstallFacts(exe));

            UpdateGuard.DeletePendingMarker(exe);
            Assert.False(File.Exists(UpdateGuard.PendingMarkerPath(exe)));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void APlantedPendingUpdate_IsRemovedAndLogged_AndAMarkedOneIsKept()
    {
        var folder = InstallEvidenceTests.NewFolder("planted");
        var statePath = Path.Combine(FleetConfig.StateDir, "update-state.json");
        try
        {
            var exe = Path.Combine(folder, AppPaths.ExeName);
            File.WriteAllText(exe, string.Empty);
            // The previous exe is a version that writes the marker, so a pending update needs one.
            File.Copy(typeof(UpdateGuard).Assembly.Location, exe + ".previous");
            Directory.CreateDirectory(FleetConfig.StateDir);
            File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(new UpdateState { Pending = Planted }));

            Assert.False(UpdateGuard.Counts(Planted, exe, Now));
            Assert.Null(UpdateGuard.Load().Pending);
            Assert.Contains(UpdateGuard.NotCountedText("2.11.0", folder), File.ReadAllText(MachineRelay.AgentLogPath));

            File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(new UpdateState { Pending = Planted }));
            UpdateGuard.WritePendingMarker(exe, "2.11.0");
            Assert.True(UpdateGuard.Counts(Planted, exe, Now));
            Assert.Equal(Planted, UpdateGuard.Load().Pending);
            Assert.True(File.Exists(UpdateGuard.PendingMarkerPath(exe)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            File.Delete(statePath);
        }
    }

    // ----- The counts beside the exe decide, and the marker goes when the watch ends -----

    private static readonly PendingUpdate Fresh = new() { Version = "2.11.0", PreviousVersion = "2.10.0", InstalledUtc = Now };
    private static string StatePath => Path.Combine(FleetConfig.StateDir, "update-state.json");

    /// <summary>A program of the user writes update-state.json, which is in the data folder.</summary>
    private static void WriteState(UpdateState state)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        File.WriteAllText(StatePath, System.Text.Json.JsonSerializer.Serialize(state));
    }

    /// <summary>An install folder after an update by 2.10.0 or later: the exe, and a previous exe of a version that writes the marker.</summary>
    private static string NewInstall(string name)
    {
        var exe = Path.Combine(InstallEvidenceTests.NewFolder(name), AppPaths.ExeName);
        File.WriteAllText(exe, string.Empty);
        File.Copy(typeof(UpdateGuard).Assembly.Location, exe + ".previous");
        return exe;
    }

    private static void Remove(string exe)
    {
        Directory.Delete(Path.GetDirectoryName(exe)!, recursive: true);
        File.Delete(StatePath);
    }

    private static (AgentStartDecision Decision, PendingUpdate? Pending) Start(string exe, DateTime? at = null)
        => UpdateGuard.AgentStarting(exe, "2.11.0", runningInstalledCopy: true, at ?? Now);

    [Fact]
    public void FailedStartsWrittenIntoTheUpdateState_BeforeTheAgentRestarts_DoNotUndoTheUpdate()
    {
        var exe = NewInstall("counts-planted");
        try
        {
            UpdateGuard.MarkPending(exe, "2.11.0", "2.10.0", Now);
            // Before the new fleet agent starts, a program of the user writes three failed starts into the update state.
            WriteState(new UpdateState { Pending = Fresh with { FailedStarts = 3, Running = true } });

            var (decision, pending) = Start(exe);
            Assert.Equal(AgentStartDecision.Watch, decision);
            Assert.Equal((0, true, 1), (pending!.FailedStarts, pending.Running, pending.Starts));
            Assert.Equal(new UpdateGuard.UpdateMark { Pending = "2.11.0", InstalledUtc = Now, Starts = 1, Running = true }, UpdateGuard.ReadMark(exe));
        }
        finally { Remove(exe); }
    }

    [Fact]
    public void ThreeStartsThatEndBeforeTheAgentWorked_UndoTheUpdate_WhateverTheUpdateStateSays()
    {
        var exe = NewInstall("counts-crashes");
        try
        {
            UpdateGuard.MarkPending(exe, "2.11.0", "2.10.0", Now);
            // A start that ends with a clean exit does not count.
            Assert.Equal(AgentStartDecision.Watch, Start(exe).Decision);
            UpdateGuard.AgentExiting(exe, "2.11.0");
            Assert.False(UpdateGuard.ReadMark(exe)!.Running);

            // Then each start ends before the agent worked. A program of the user clears the counts in the update state
            // every time, which changes nothing.
            var failed = new List<int>();
            var decisions = new List<AgentStartDecision>();
            for (var start = 0; start < 4; start++)
            {
                WriteState(new UpdateState { Pending = Fresh });
                var (decision, pending) = Start(exe);
                decisions.Add(decision);
                failed.Add(pending!.FailedStarts);
            }
            Assert.Equal(new[] { AgentStartDecision.Watch, AgentStartDecision.Watch, AgentStartDecision.Watch, AgentStartDecision.RollBack }, decisions);
            Assert.Equal(new[] { 0, 1, 2, 3 }, failed);
            Assert.Equal((3, false), (UpdateGuard.ReadMark(exe)!.FailedStarts, UpdateGuard.ReadMark(exe)!.Running));
        }
        finally { Remove(exe); }
    }

    [Fact]
    public void AnUpdateStateDeletedWhileTheUpdateIsWatched_CannotUndoTheUpdateLater()
    {
        var exe = NewInstall("state-deleted");
        try
        {
            UpdateGuard.MarkPending(exe, "2.11.0", "2.10.0", Now);
            Assert.Equal(AgentStartDecision.Watch, Start(exe).Decision);
            // During the watch a program of the user deletes the update state; then the agent answers.
            File.Delete(StatePath);
            UpdateGuard.MarkHealthy(exe, "2.11.0");

            // Days later the program writes the update back, with three failed starts.
            WriteState(new UpdateState { Pending = Fresh with { FailedStarts = 3, Running = true } });
            Assert.Equal(AgentStartDecision.Nothing, Start(exe, Now.AddDays(3)).Decision);
            Assert.Null(UpdateGuard.Load().Pending);
        }
        finally { Remove(exe); }
    }

    [Theory]
    [InlineData("answered", false)]
    [InlineData("answered", true)]
    [InlineData("no answer", false)]
    [InlineData("no answer", true)]
    [InlineData("next start", true)]
    public void TheMarkerGoes_WhenTheWatchEnds_WhateverTheUpdateStateSays(string end, bool stateDeleted)
    {
        var exe = NewInstall("watch-ends");
        try
        {
            UpdateGuard.MarkPending(exe, "2.11.0", "2.10.0", Now);
            Assert.Equal(AgentStartDecision.Watch, Start(exe).Decision);
            if (stateDeleted) File.Delete(StatePath);

            switch (end)
            {
                case "answered": UpdateGuard.MarkHealthy(exe, "2.11.0"); break;
                // It ran for the whole wait without answering a status request: kept, and no longer watched.
                case "no answer": UpdateGuard.StopWatching(exe, "2.11.0"); break;
                // The update state has no pending update when the next agent starts.
                default: Assert.Equal(AgentStartDecision.Nothing, Start(exe).Decision); break;
            }

            Assert.False(File.Exists(UpdateGuard.PendingMarkerPath(exe)));
            Assert.Null(UpdateGuard.Load().Pending);
            Assert.Equal(end == "answered" && !stateDeleted ? "2.11.0" : string.Empty, UpdateGuard.Load().GoodVersion);
        }
        finally { Remove(exe); }
    }

    [Fact]
    public void AMarkerOfTheFirstForm_CountsAsNoStarts()
    {
        var exe = NewInstall("first-form");
        try
        {
            var written = new DateTime(2026, 11, 12, 11, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(exe, written);
            WriteState(new UpdateState { Pending = Fresh with { FailedStarts = 3, Running = true, Starts = 7, InstalledUtc = Now.AddDays(-30) } });
            // Written by an earlier build of 2.10.0: the version only.
            File.WriteAllText(UpdateGuard.PendingMarkerPath(exe), "2.11.0");
            Assert.Equal(new UpdateGuard.UpdateMark { Pending = "2.11.0" }, UpdateGuard.ReadMark(exe));

            var (decision, pending) = Start(exe);
            Assert.Equal(AgentStartDecision.Watch, decision);
            Assert.Equal((0, true, 1, written), (pending!.FailedStarts, pending.Running, pending.Starts, pending.InstalledUtc));
            Assert.Equal(new UpdateGuard.UpdateMark { Pending = "2.11.0", InstalledUtc = written, Starts = 1, Running = true }, UpdateGuard.ReadMark(exe));
        }
        finally { Remove(exe); }
    }

    [Fact]
    public void TheMarker_IsReadBackAsWritten_AndUnknownLinesAreSkipped()
    {
        var mark = new UpdateGuard.UpdateMark { Pending = "2.11.0", InstalledUtc = Now, Starts = 2, FailedStarts = 1, Running = true };
        Assert.Equal(mark, UpdateGuard.ParseMark(UpdateGuard.MarkText(mark)));
        Assert.Equal(mark, UpdateGuard.ParseMark(UpdateGuard.MarkText(mark).Replace("\n", "\r\n") + "later=1\n"));
        Assert.Equal(new UpdateGuard.UpdateMark { Pending = "2.11.0" }, UpdateGuard.ParseMark("2.11.0\nfailed=-1\nstarts=many\nrunning=yes\n"));
        var undone = new UpdateGuard.UpdateMark { RolledBackFrom = "2.11.0", RolledBackTo = "2.10.0" };
        Assert.Equal(undone, UpdateGuard.ParseMark(UpdateGuard.MarkText(undone)));
        Assert.Null(UpdateGuard.ParseMark(string.Empty));
    }

    [Fact]
    public void OnlyTheRecordBesideTheExe_StopsTheAutomaticUpdateToAnUndoneVersion()
    {
        var exe = NewInstall("rolled-back");
        try
        {
            // Written by hand into the update state: no reason to stop the update.
            WriteState(new UpdateState { RolledBackFrom = "2.11.0", RolledBackTo = "2.10.0" });
            Assert.False(UpdateGuard.WasRolledBack(exe, "2.11.0", installedVersion: "2.10.0"));

            UpdateGuard.WriteRollbackMark(exe, "2.11.0", "2.10.0");
            File.Delete(StatePath);
            Assert.True(UpdateGuard.WasRolledBack(exe, "2.11.0", installedVersion: "2.10.0"));
            Assert.False(UpdateGuard.WasRolledBack(exe, "2.12.0", installedVersion: "2.10.0"));
            // A version that does not write the record (2.9.0) installed the undone version again.
            Assert.False(UpdateGuard.WasRolledBack(exe, "2.11.0", installedVersion: "2.11.0"));
            // The record is no pending update, and deleting the marker of one leaves it.
            Assert.Equal(string.Empty, UpdateGuard.ReadInstallFacts(exe).MarkerVersion);
            UpdateGuard.DeletePendingMarker(exe);
            Assert.True(UpdateGuard.WasRolledBack(exe, "2.11.0", installedVersion: "2.10.0"));

            // An update by hand replaces it.
            UpdateGuard.MarkPending(exe, "2.11.0", "2.10.0", Now);
            Assert.False(UpdateGuard.WasRolledBack(exe, "2.11.0", installedVersion: "2.10.0"));
            Assert.Equal("2.11.0", UpdateGuard.ReadInstallFacts(exe).MarkerVersion);
        }
        finally { Remove(exe); }
    }
}
