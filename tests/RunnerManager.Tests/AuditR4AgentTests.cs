using System.Text.Json;

namespace RunnerManager.Tests;

/// <summary>
/// The wait before the fleet agent restarts for an update: long enough for an AI assistant's call that runs, and not
/// longer than <see cref="MachineRelay.UpdateRestartLimit"/> in all. All on a fake clock.
/// </summary>
public class UpdateRestartWaitTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Runs <see cref="MachineRelay.WaitWhileBusyAsync"/> on a clock that moves on by <paramref name="step"/> at each
    /// status read. Returns what it logged and how long it waited.
    /// </summary>
    private static async Task<(List<string> Log, TimeSpan Waited)> WaitAsync(TimeSpan step, Func<TimeSpan, MachineStatusPayload> status)
    {
        var clock = Start;
        var log = new List<string>();
        await MachineRelay.WaitWhileBusyAsync(() =>
        {
            clock += step;
            // A wait without end would hang the test run: after a day the agent is idle, and the test fails instead.
            var waited = clock - Start;
            return Task.FromResult<MachineStatusPayload?>(waited > TimeSpan.FromDays(1) ? new MachineStatusPayload() : status(waited));
        }, () => clock, () => Task.CompletedTask, log.Add);
        return (log, clock - Start);
    }

    private static FleetCommandResult[] Finished(int count)
        => Enumerable.Range(0, count).Select(n => new FleetCommandResult { Id = $"c{n}", Result = $"Runner created: r{n}" }).ToArray();

    [Fact]
    public void TheWait_IsAsLongAsAnAiAssistantsCall_AndTheLimitIsThreeHours()
    {
        Assert.True(MachineRelay.UpdateRestartWait >= AssistantHost.DrainLimit,
            $"An update waits {MachineRelay.UpdateRestartWait} for work, but an AI assistant's call may take {AssistantHost.DrainLimit}.");
        Assert.Equal(TimeSpan.FromHours(3), MachineRelay.UpdateRestartLimit);
        Assert.True(MachineRelay.UpdateRestartLimit >= MachineRelay.UpdateRestartWait);
    }

    [Fact]
    public async Task AnUpdateRestart_DoesNotCutOffAnAiAssistantsCall_ThatWaitsForItsApprovalAndThenACreate()
    {
        // An AI assistant's call is not a command: the agent says it is busy, and finishes no command, until the call
        // ends after its approval and a runner create on another PC.
        var (log, waited) = await WaitAsync(TimeSpan.FromMinutes(1), elapsed => elapsed < AssistantHost.DrainLimit
            ? new MachineStatusPayload { Busy = true, RecentCommands = Finished(1) }
            : new MachineStatusPayload());
        Assert.Equal(AssistantHost.DrainLimit, waited);
        Assert.Equal("Waiting for the fleet agent to finish its current work…", Assert.Single(log));
    }

    [Fact]
    public void InTheAgent_AnAiAssistantsCall_IsWaitedFor_ThoughNoCommandFinishes()
    {
        var now = Start;
        var clock = new UpdateRestartClock(() => now, MachineRelay.UpdateRestartWait, MachineRelay.UpdateRestartLimit);
        // The update asks; then only the call runs, for as long as it may.
        Assert.False(clock.Due(quiet: false));
        now += AssistantHost.DrainLimit - TimeSpan.FromSeconds(1);
        Assert.False(clock.Due(quiet: false));
        Assert.True(clock.Due(quiet: true));
    }

    [Fact]
    public async Task AnUpdateRestart_HappensAfterThreeHours_ThoughCommandsKeepFinishing()
    {
        // A create finishes every 30 minutes, and another one is always queued.
        var (log, waited) = await WaitAsync(TimeSpan.FromMinutes(10), elapsed => new MachineStatusPayload
        {
            Busy = true, PendingCommandIds = new[] { "next" }, RecentCommands = Finished(1 + (int)(elapsed / TimeSpan.FromMinutes(30)))
        });
        // Counted from the first status read, the first check after the update asked.
        Assert.Equal(TimeSpan.FromMinutes(10) + MachineRelay.UpdateRestartLimit, waited);
        Assert.Equal("The fleet agent is still busy after 3 hours. Restarting it anyway.", log[^1]);
    }

    [Fact]
    public void InTheAgent_TheRestartHappensAtTheLimit_AndWorkThatIsStuckIsToldApart()
    {
        var now = Start;
        var clock = new UpdateRestartClock(() => now, MachineRelay.UpdateRestartWait, MachineRelay.UpdateRestartLimit);
        Assert.False(clock.Due(quiet: false));
        // A command finishes every 30 minutes, without end.
        while (now - Start < MachineRelay.UpdateRestartLimit - TimeSpan.FromMinutes(30))
        {
            now += TimeSpan.FromMinutes(30);
            clock.WorkFinished();
            Assert.False(clock.Due(quiet: false));
        }
        now = Start + MachineRelay.UpdateRestartLimit;
        clock.WorkFinished();
        Assert.True(clock.Due(quiet: false));
        Assert.True(clock.PastLimit);

        // Work that finishes nothing for the whole wait is stuck, which is not the limit.
        var stuck = new UpdateRestartClock(() => now, MachineRelay.UpdateRestartWait, MachineRelay.UpdateRestartLimit);
        Assert.False(stuck.Due(quiet: false));
        now += MachineRelay.UpdateRestartWait;
        Assert.True(stuck.Due(quiet: false));
        Assert.False(stuck.PastLimit);
    }

    [Fact]
    public void WhileTheOldAgentRunsOnForTheWholeWait_TheUpdateIsNotUndone()
    {
        var installed = Start;
        var pending = new PendingUpdate { Version = "9.9.0", PreviousVersion = "9.8.0", InstalledUtc = installed };
        // The old agent still runs (it is not down), waiting for work, until the limit.
        Assert.Equal(DashboardUpdateDecision.Nothing, UpdateGuard.DecideForDashboard(pending, "9.8.0", "9.9.0", agentInstalled: true,
            agentDownFor: TimeSpan.Zero, installed + MachineRelay.UpdateRestartLimit, uptime: TimeSpan.FromHours(5)));
        // Then it restarts into the new version, which takes seconds, not the minutes a rollback needs.
        Assert.Equal(DashboardUpdateDecision.Nothing, UpdateGuard.DecideForDashboard(pending, "9.8.0", "9.9.0", agentInstalled: true,
            agentDownFor: TimeSpan.FromSeconds(10), installed + MachineRelay.UpdateRestartLimit, uptime: TimeSpan.FromHours(5)));
    }
}
