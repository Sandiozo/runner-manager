using System.Reflection;
using System.Security.Cryptography;

namespace RunnerManager.Tests;

/// <summary>
/// A runner create on another PC may take as long as RunnerService allows its download and registration. No wait in
/// the chain (the create's own, the wait for a queued command, an update restart, an AI tool call) is shorter.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
public class LongCreateWaitTests
{
    private static TimeSpan RunnerServiceLimit(string name)
        => (TimeSpan)typeof(RunnerService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void NoWaitForACreate_IsShorterThanTheDownloadAndTheRegistration()
    {
        var download = RunnerServiceLimit("DownloadLimit");
        var register = RunnerServiceLimit("ConfigTimeout");
        Assert.True(FleetActions.CreateWait >= download + register + TimeSpan.FromMinutes(5),
            $"A create waits {FleetActions.CreateWait}, but its download may take {download} and config.cmd {register}.");
        Assert.True(MachineRelay.MaxCommandWait >= FleetActions.CreateWait);
        Assert.True(MachineRelay.UpdateRestartWait >= FleetActions.CreateWait);
        Assert.True(AssistantHost.DrainLimit >= AssistantHost.ApprovalTimeout + FleetActions.CreateWait);
    }

    /// <summary>A PC whose status moves the clock on by 20 minutes at each poll, so long waits take no time.</summary>
    private static async Task WithSlowPcAsync(Func<MachineStatusPayload, int, MachineStatusPayload> status, Func<Func<DateTime>, Task> test)
    {
        var previous = MachineRelay.CommandPollInterval;
        MachineRelay.CommandPollInterval = TimeSpan.FromMilliseconds(10);
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var polls = 0;
        var gate = new object();
        using var agent = new TestAgent("SLOWPC", machineId: "id-slow");
        agent.Status = s =>
        {
            lock (gate)
            {
                clock += TimeSpan.FromMinutes(20);
                return status(s, ++polls);
            }
        };
        try
        {
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = Convert.ToBase64String(ProtectedData.Protect(agent.Key, null, DataProtectionScope.CurrentUser));
                c.KnownPeers.Clear();
                c.KnownPeers.Add(new PeerRecord { Name = "SLOWPC", Address = agent.Address, MachineId = "id-slow" });
            });
            await test(() => { lock (gate) return clock; });
        }
        finally
        {
            MachineRelay.CommandPollInterval = previous;
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = string.Empty;
                c.KnownPeers.Clear();
            });
        }
    }

    private static FleetCommandResult Done(string id, string result) => new() { Id = id, Result = result };

    [Fact]
    public Task ACreateQueuedBehindLongWork_IsWaitedFor_WhileTheWorkAheadFinishes() => WithSlowPcAsync(
        (s, poll) => poll switch
        {
            // Another create runs first, for 60 minutes in all.
            <= 2 => s with { PendingCommandIds = new[] { "ahead", "mine" }, Busy = true },
            <= 4 => s with { PendingCommandIds = new[] { "mine" }, RecentCommands = new[] { Done("ahead", "Runner created: r0") }, Busy = true },
            _ => s with { RecentCommands = new[] { Done("ahead", "Runner created: r0"), Done("mine", "Runner created: r1") } }
        },
        async clock => Assert.Equal("Runner created: r1",
            await MachineRelay.WaitForCommandAsync("SLOWPC", "mine", TimeSpan.FromMinutes(1), clock)));

    [Fact]
    public Task ACommandThatNeverMovesOn_IsWaitedForAsLongAsTheLongestCommand() => WithSlowPcAsync(
        (s, _) => s with { PendingCommandIds = new[] { "stuck" }, Busy = true },
        async clock =>
        {
            var started = clock();
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                MachineRelay.WaitForCommandAsync("SLOWPC", "stuck", TimeSpan.FromMinutes(1), clock));
            Assert.Contains("is still running the command after 60 minutes", error.Message);
            Assert.Equal(MachineRelay.MaxCommandWait, clock() - started);
        });

    /// <summary>Runs <see cref="MachineRelay.WaitWhileBusyAsync"/> on statuses that move the clock on by 20 minutes each.</summary>
    private static async Task<(List<string> Log, int Reads)> WaitForUpdateRestartAsync(Func<int, MachineStatusPayload> status)
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reads = 0;
        var log = new List<string>();
        await MachineRelay.WaitWhileBusyAsync(() =>
        {
            clock += TimeSpan.FromMinutes(20);
            return Task.FromResult<MachineStatusPayload?>(status(++reads));
        }, () => clock, () => Task.CompletedTask, log.Add);
        return (log, reads);
    }

    private static FleetCommandResult[] Finished(int count)
        => Enumerable.Range(0, count).Select(n => Done($"c{n}", $"Runner created: r{n}")).ToArray();

    [Fact]
    public async Task AnUpdateRestart_WaitsWhileTheAgentFinishesCommands()
    {
        // Three hours of creates, one finished every 40 minutes, then the agent is idle.
        var (log, reads) = await WaitForUpdateRestartAsync(read => read < 9
            ? new MachineStatusPayload { Busy = true, PendingCommandIds = new[] { "next" }, RecentCommands = Finished(1 + read / 2) }
            : new MachineStatusPayload());
        Assert.Equal(9, reads);
        Assert.Equal("Waiting for the fleet agent to finish its current work…", Assert.Single(log));
    }

    [Fact]
    public async Task AnUpdateRestart_DoesNotWaitForeverForWorkThatIsStuck()
    {
        var (log, reads) = await WaitForUpdateRestartAsync(_ =>
            new MachineStatusPayload { Busy = true, PendingCommandIds = new[] { "stuck" }, RecentCommands = Finished(1) });
        // 20 minutes for the first read, then 60 minutes in which nothing finished.
        Assert.Equal(4, reads);
        Assert.Contains("finished nothing in 60 minutes. Restarting it anyway.", log[^1]);
    }

    [Fact]
    public void TheUpdateRestartClock_StartsAgainWhenWorkFinishes()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = new UpdateRestartClock(() => now, TimeSpan.FromMinutes(60));
        Assert.False(clock.Due(quiet: false));
        now += TimeSpan.FromMinutes(59);
        Assert.False(clock.Due(quiet: false));
        clock.WorkFinished();
        now += TimeSpan.FromMinutes(59);
        Assert.False(clock.Due(quiet: false));
        now += TimeSpan.FromMinutes(1);
        Assert.True(clock.Due(quiet: false));
        Assert.True(new UpdateRestartClock(() => now, TimeSpan.FromMinutes(60)).Due(quiet: true));
    }
}
