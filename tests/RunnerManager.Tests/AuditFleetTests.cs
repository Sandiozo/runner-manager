using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

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

public class ClockOffsetTests
{
    private static readonly DateTime Received = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static MachineStatusPayload Status(DateTime agentNow, DateTime? jobStarted = null, DateTime? answered = null, DateTime? updated = null) => new()
    {
        MachineName = "SLOWCLOCK",
        FleetAgentRunning = true,
        UpdatedUtc = updated ?? agentNow,
        AnsweredUtc = answered ?? default,
        BootUtc = agentNow.AddDays(-1),
        Runners = new[]
        {
            new MachineRunnerStatus { ServiceName = "actions.runner.a", IsRunning = true, ActivityText = "Busy", JobStartedUtc = jobStarted, QuietSinceUtc = jobStarted },
            new MachineRunnerStatus { ServiceName = "actions.runner.b", IsRunning = true, ActivityText = "Idle" }
        }
    };

    private static string NewKey() => "PC_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AClockThatIsBehind_IsCorrected_AndThePcCountsAsJustUpdated()
    {
        var agentNow = Received.AddSeconds(-90);
        var status = MachineRelay.ByLocalClock(NewKey(), Status(agentNow, jobStarted: agentNow.AddMinutes(-5)), Received);

        Assert.Equal(Received, status.UpdatedUtc);
        Assert.Equal(Received.AddMinutes(-5), status.Runners[0].JobStartedUtc);
        Assert.Equal(Received.AddMinutes(-5), status.Runners[0].QuietSinceUtc);
        Assert.Equal("actions.runner.a", status.Runners[0].ServiceName);
        Assert.Equal(Received.AddDays(-1), status.BootUtc);
        Assert.Null(status.Runners[1].JobStartedUtc);
    }

    [Fact]
    public void TheCorrection_StaysTheSame_WhileTheClockStaysWhereItIs()
    {
        var key = NewKey();
        var started = Received.AddSeconds(-90).AddMinutes(-5);
        var first = MachineRelay.ByLocalClock(key, Status(Received.AddSeconds(-90), jobStarted: started), Received);
        // The next answer took a little longer over the network.
        var second = MachineRelay.ByLocalClock(key, Status(Received.AddSeconds(8 - 90), jobStarted: started), Received.AddSeconds(10));

        Assert.Equal(first.Runners[0].JobStartedUtc, second.Runners[0].JobStartedUtc);
        Assert.Equal(Received.AddSeconds(10), second.UpdatedUtc);
    }

    [Fact]
    public void ClocksThatAreClose_AreNotCorrected()
    {
        var payload = Status(Received.AddSeconds(-2), jobStarted: Received.AddMinutes(-3));
        var status = MachineRelay.ByLocalClock(NewKey(), payload, Received);

        Assert.Equal(Received, status.UpdatedUtc);
        Assert.Same(payload.Runners, status.Runners);
        Assert.Equal(payload.BootUtc, status.BootUtc);
    }

    [Fact]
    public void AnOldStatus_FromANewerAgent_KeepsItsAge_AndIsNotOnline()
    {
        var agentNow = Received.AddSeconds(-60);
        var status = MachineRelay.ByLocalClock(NewKey(), Status(agentNow, answered: agentNow, updated: agentNow.AddSeconds(-45)), Received);

        Assert.Equal(Received.AddSeconds(-45), status.UpdatedUtc);
        // By this PC's clock, it is 45 seconds old now.
        Assert.False(MachineInfo.FromPayload("PC", status with { UpdatedUtc = DateTime.UtcNow.AddSeconds(-45) }, address: "pc").IsOnline);
    }

    [Fact]
    public void ATimeThatCannotBeAnAnswerTime_IsKeptAsItIs()
    {
        var empty = new MachineStatusPayload { MachineName = "PC" };
        Assert.Same(empty, MachineRelay.ByLocalClock(NewKey(), empty, Received));
        var old = Status(Received.AddHours(-3));
        Assert.Same(old, MachineRelay.ByLocalClock(NewKey(), old, Received));
    }

    [Fact]
    public void TheAnswerTime_DoesNotChangeTheFingerprint()
    {
        var payload = Status(Received);
        Assert.Equal(payload.Fingerprint(), (payload with { AnsweredUtc = Received.AddSeconds(3), UpdatedUtc = Received.AddSeconds(3) }).Fingerprint());
    }

    /// <summary>What a dashboard from before the answer time reads.</summary>
    private sealed record OlderPayload
    {
        public string MachineName { get; init; } = string.Empty;
        public DateTime UpdatedUtc { get; init; }
    }

    [Fact]
    public void OlderDashboards_IgnoreTheAnswerTime()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(MachineRelay.AsAnswer(Status(Received), Received));
        Assert.Contains("AnsweredUtc", json);
        var older = System.Text.Json.JsonSerializer.Deserialize<OlderPayload>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(Received, older.UpdatedUtc);
    }

    [Fact]
    public void AnAnswer_IsStampedWithItsTime_UnlessNoStatusCouldBeBuiltForAWhile()
    {
        var fresh = MachineRelay.AsAnswer(new MachineStatusPayload { UpdatedUtc = Received.AddSeconds(-10) }, Received);
        Assert.Equal((Received, Received), (fresh.UpdatedUtc, fresh.AnsweredUtc));

        var stale = MachineRelay.AsAnswer(new MachineStatusPayload { UpdatedUtc = Received.AddSeconds(-40) }, Received);
        Assert.Equal((Received.AddSeconds(-40), Received), (stale.UpdatedUtc, stale.AnsweredUtc));
    }
}

[Collection(FleetSettingsCollection.Name)]
public class ClockOffsetFleetTests
{
    [Fact]
    public async Task APcWhoseClockIsBehind_IsListedOnline()
    {
        await AuditFleetConfig.WithConfigAsync(async () =>
        {
            using var agent = new TestAgent("BEHINDPC");
            agent.Status = s => s with { UpdatedUtc = DateTime.UtcNow.AddSeconds(-100) };
            FleetConfig.SetFleetKey(agent.Key);
            FleetConfig.Update(c => c.KnownPeers.Add(new PeerRecord { Name = "BEHINDPC", Address = agent.Address, LastSeenUtc = DateTime.UtcNow }));

            var listed = Assert.Single(await MachineRelay.ReadFleetAsync(), m => m.MachineName == "BEHINDPC");

            Assert.Empty(listed.ConnectionProblem);
            Assert.True(listed.IsOnline);
            Assert.True(DateTime.UtcNow - listed.UpdatedUtc < TimeSpan.FromSeconds(10));
        });
    }
}

[Collection(FleetSettingsCollection.Name)]
public class SalvagedSettingsTests
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    [Fact]
    public async Task SettingsSalvagedFromADamagedFile_AreStillThereAtTheNextRead()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var key = CommandSigner.NewKey();
            var protectedKey = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            // Cut off, so not even valid JSON.
            var damaged = $"{{\"ProtectedFleetKey\":\"{protectedKey}\",\"MachineId\":\"abc123\",\"ManualAddresses\":[";
            File.WriteAllText(ConfigPath, damaged);
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddDays(-2).AddTicks(Random.Shared.Next()));
            try
            {
                Assert.Equal(key, FleetConfig.Current.FleetKey);
                Assert.Equal(key, FleetConfig.Current.FleetKey);
                Assert.Equal("abc123", FleetConfig.Current.MachineId);
                // What was salvaged replaced the damaged file, and a copy of that is kept.
                using (JsonDocument.Parse(File.ReadAllText(ConfigPath))) { }
                var backup = Assert.Single(Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*"));
                Assert.Equal(damaged, File.ReadAllText(backup));
            }
            finally
            {
                foreach (var file in Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*")) File.Delete(file);
            }
            return Task.CompletedTask;
        });
    }
}

[Collection(FleetSettingsCollection.Name)]
public class PendingCodeTests
{
    [Fact]
    public async Task APendingCode_IsForgotten_WhenThisPcGetsAnotherCode()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var (old, sent, replaced) = (CommandSigner.NewKey(), CommandSigner.NewKey(), CommandSigner.NewKey());
            FleetConfig.SetFleetKey(old);
            var keys = new ConfigAgentKeys();
            keys.SetPending(sent);
            Assert.Equal(sent, keys.Pending);
            // A Runner Manager before 2.4 saved fleet.json and dropped it there: the copy in memory still counts.
            FleetConfig.Update(c => c.ProtectedPendingFleetKey = string.Empty);
            Assert.Equal(sent, keys.Pending);

            // Replace code, or Enter pairing code, on this PC.
            FleetConfig.SetFleetKey(replaced);
            Assert.Null(keys.Pending);
            keys.UsePending(sent);
            Assert.Equal(replaced, FleetConfig.Current.FleetKey);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task APendingCode_Expires()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var (old, sent) = (CommandSigner.NewKey(), CommandSigner.NewKey());
            var now = DateTime.UtcNow;
            FleetConfig.SetFleetKey(old);
            var keys = new ConfigAgentKeys(() => now);
            keys.SetPending(sent);

            FleetConfig.Update(c => c.PendingFleetKeyUtc = DateTime.UtcNow - FleetConfig.PendingCodeLifetime - TimeSpan.FromMinutes(1));
            Assert.Null(FleetConfig.Current.PendingFleetKey);
            Assert.Equal(sent, keys.Pending);
            now += FleetConfig.PendingCodeLifetime + TimeSpan.FromMinutes(1);
            Assert.Null(keys.Pending);

            keys.UsePending(sent);
            Assert.Equal(old, FleetConfig.Current.FleetKey);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ACodeSentBeforeReplaceCode_CannotSwitchThePcLater()
    {
        await AuditFleetConfig.WithConfigAsync(async () =>
        {
            var (old, sent, replaced) = (CommandSigner.NewKey(), CommandSigner.NewKey(), CommandSigner.NewKey());
            FleetConfig.SetFleetKey(old);
            var keys = new ConfigAgentKeys();
            var port = TestPorts.FreeRange(2);
            using var server = new AgentServer(() => Task.FromResult(new MachineStatusPayload { MachineName = "PENDINGPC" }), c => c.Id,
                _ => Task.FromResult(new LogPayload()), prefix: $"http://localhost:{port}/rw/", discoveryPort: port + 1, machineKey: "PENDINGPC", keys: keys);
            server.Start();
            // A fleet member sent a new code with Change fleet code, and left the fleet before using it.
            keys.SetPending(sent);
            FleetConfig.SetFleetKey(replaced);

            await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync($"localhost:{port}", "PENDINGPC", new FleetRequest { Op = "status" }, pairingKey: sent));
            Assert.Equal(replaced, FleetConfig.Current.FleetKey);
            await FleetNetwork.CallAsync($"localhost:{port}", "PENDINGPC", new FleetRequest { Op = "status" }, pairingKey: replaced);
        });
    }
}

public class FleetConnectionTests
{
    [Fact]
    public void Requests_GoStraightToThePc_WithoutAProxyOrRedirects()
    {
        using var handler = FleetNetwork.CreateHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.InRange(handler.ConnectTimeout, TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task APcThatIsOff_FailsAtTheConnectTimeout_NotAtTheRequestTimeout()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // TEST-NET-1: nothing answers there.
        await Assert.ThrowsAnyAsync<Exception>(() => FleetNetwork.CallAsync("192.0.2.1:47821", "OFFPC",
            new FleetRequest { Op = "status" }, TimeSpan.FromSeconds(30), pairingKey: CommandSigner.NewKey()));
        Assert.True(watch.Elapsed < FleetNetwork.ConnectTimeout + TimeSpan.FromSeconds(4), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}

/// <summary>Runs a test with its own fleet.json in the test state folder, and puts back the one before it.</summary>
internal static class AuditFleetConfig
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    public static async Task WithConfigAsync(Func<Task> test)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        var saved = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        if (saved != null) File.Delete(ConfigPath);
        try { await test(); }
        finally
        {
            if (saved != null)
            {
                File.WriteAllText(ConfigPath, saved);
                File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddDays(-1).AddTicks(Random.Shared.Next()));
            }
            else if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        }
    }
}

public class StatusCacheBuildTests
{
    private static MachineStatusPayload Payload(int n) => new() { MachineName = "PC", LastCommandId = n.ToString(), UpdatedUtc = DateTime.UtcNow };

    [Fact]
    public async Task AFailingBuild_IsLoggedOnce_AndSoIsItsRecovery()
    {
        var builds = 0;
        var fail = true;
        var log = new List<string>();
        var cache = new StatusCache(() =>
        {
            var n = Interlocked.Increment(ref builds);
            return fail ? throw new InvalidOperationException("WMI is broken") : Task.FromResult(Payload(n));
        }, message => { lock (log) log.Add(message); });

        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync());
        Assert.True(builds >= 3);
        Assert.Single(log, m => m.Contains("WMI is broken"));

        fail = false;
        await cache.GetAsync();
        Assert.Equal(2, log.Count);
        Assert.Contains("works again", log[1]);
    }

    [Fact]
    public async Task AHungBuild_IsGivenUp_AndAnotherOneIsStarted()
    {
        var builds = 0;
        var hang = new TaskCompletionSource<MachineStatusPayload>();
        var log = new List<string>();
        var cache = new StatusCache(() =>
        {
            var n = Interlocked.Increment(ref builds);
            return n == 2 ? hang.Task : Task.FromResult(Payload(n));
        }, message => { lock (log) log.Add(message); }) { BuildTimeout = TimeSpan.FromMilliseconds(300) };
        Assert.Equal("1", (await cache.GetAsync()).LastCommandId);

        await Task.Delay(StatusCache.FreshFor + TimeSpan.FromMilliseconds(100));
        Assert.Equal("1", (await cache.GetAsync()).LastCommandId);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref builds) == 2, TimeSpan.FromSeconds(1)));

        // Still hung after the timeout: the next request starts another build, which shows up.
        await Task.Delay(400);
        await cache.GetAsync();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref builds) == 3, TimeSpan.FromSeconds(1)));
        await Task.Delay(200);
        Assert.Equal("3", (await cache.GetAsync()).LastCommandId);
        Assert.Contains(log, m => m.Contains("took more than"));

        // The hung build finishing late does not bring back its older status.
        hang.SetResult(Payload(2));
        await Task.Delay(100);
        Assert.Equal("3", (await cache.GetAsync()).LastCommandId);
    }
}
