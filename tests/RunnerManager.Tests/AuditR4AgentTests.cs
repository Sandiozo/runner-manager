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

/// <summary>Another PC's status: what is logged about it, and how much of it is read.</summary>
[Collection(FleetSettingsCollection.Name)]
public class PeerStatusLogTests
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };

    private static string Unique(string from) => $"{from}-{Guid.NewGuid():N}";

    /// <summary>A status with one runner whose repository is not a GitHub name, which is logged.</summary>
    private static MachineStatusPayload Odd(string name) => new()
    {
        MachineName = name,
        Runners = new[] { new MachineRunnerStatus { ServiceName = "actions.runner.1", RepositoryPath = "not a repository" } }
    };

    [Fact]
    public void APcThatChangesItsName_IsLoggedOnce_ByWhereItAnswered()
    {
        var log = new List<string>();
        var from = Unique("10.0.0.5:47800");
        for (var n = 0; n < 5; n++) PeerStatus.Checked(Odd($"NAME{n}"), from, log.Add);
        Assert.Contains("NAME0", Assert.Single(log));
    }

    [Fact]
    public void APcThatAnswersUnderAnotherPcsName_DoesNotUseUpThatPcsLogLine()
    {
        var log = new List<string>();
        var name = Unique("OFFICE");
        PeerStatus.Checked(Odd(name), Unique("managed key"), log.Add);
        PeerStatus.Checked(Odd(name), Unique("10.0.0.7:47800"), log.Add);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void TheNameAPcSends_CannotWriteLinesOfItsOwnIntoTheLog()
    {
        var log = new List<string>();
        PeerStatus.Checked(Odd("PC\r\n2026-11-12 10:00:00  Update to v9.9.9 works on this PC.\u2028x"), Unique("10.0.0.8:47800"), log.Add);
        var line = Assert.Single(log);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\u2028', line);
        Assert.StartsWith("The status of PC 2026-11-12 10:00:00 Update to v9.9.9 works on this PC. x was not used in full", line);
    }

    [Fact]
    public void WhatWasLogged_IsRememberedForSoManyPcs_ThenTheOldestIsForgotten()
    {
        var log = new List<string>();
        var first = Unique("first");
        PeerStatus.Checked(Odd("FIRSTPC"), first, log.Add);
        PeerStatus.Checked(Odd("FIRSTPC"), first, log.Add);
        Assert.Single(log);
        for (var n = 0; n < PeerStatus.MaxLogged; n++) PeerStatus.Checked(Odd($"PC{n}"), Unique($"other{n}"), log.Add);
        Assert.Equal(1 + PeerStatus.MaxLogged, log.Count);
        // The first PC was forgotten to make room, so it is logged again.
        PeerStatus.Checked(Odd("FIRSTPC"), first, log.Add);
        Assert.Equal(2 + PeerStatus.MaxLogged, log.Count);
    }

    [Fact]
    public void AHugeRunnerList_IsReadNoFurtherThanOneEntryMoreThanIsUsed()
    {
        var used = Enumerable.Range(0, PeerStatus.MaxRunners + 1).Select(n => $$"""{"ServiceName":"actions.runner.{{n}}"}""");
        // Entries after those are skipped without being read: these would each count as unreadable.
        var skipped = Enumerable.Range(0, 5000).Select(n => n % 2 == 0 ? "5" : """{"AgentId":"not a number","Labels":[1,{"a":[2]}]}""");
        var json = $$"""{"MachineName":"HUGEPC","Runners":[{{string.Join(",", used.Concat(skipped))}}],"MachineId":"id-after"}""";

        var status = JsonSerializer.Deserialize<MachineStatusPayload>(json, WireJson)!;
        Assert.Equal(PeerStatus.MaxRunners + 1, status.Runners.Length);
        Assert.Equal(0, status.UnreadableRunners);
        Assert.Equal("actions.runner.200", status.Runners[^1].ServiceName);
        // What follows the list is read as before.
        Assert.Equal("id-after", status.MachineId);

        var log = new List<string>();
        Assert.Equal(PeerStatus.MaxRunners, PeerStatus.Checked(status, Unique("huge"), log.Add).Runners.Length);
        Assert.Contains($"more than {PeerStatus.MaxRunners} runners", Assert.Single(log));
    }

    [Fact]
    public void AListOfEntriesThatCannotBeRead_StillSaysThatThePcSentTooMany()
    {
        var json = $$"""{"MachineName":"ODDPC","Runners":[{{string.Join(",", Enumerable.Repeat("5", PeerStatus.MaxRunners + 50))}}]}""";
        var status = JsonSerializer.Deserialize<MachineStatusPayload>(json, WireJson)!;
        Assert.Empty(status.Runners);
        Assert.Equal(PeerStatus.MaxRunners + 1, status.UnreadableRunners);

        var log = new List<string>();
        PeerStatus.Checked(status, Unique("odd"), log.Add);
        Assert.Contains($"more than {PeerStatus.MaxRunners} runners", Assert.Single(log));
    }
}
