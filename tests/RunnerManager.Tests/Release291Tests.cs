using System.Text.Json;

namespace RunnerManager.Tests;

/// <summary>
/// Once an update asked the fleet agent to restart, an AI assistant's change is refused, so no new work begins that the
/// restart would cut off. A read still runs, and does not hold up the restart.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
public class RestartAskedTests
{
    [Fact]
    public void BeforeTheRestartIsAsked_EveryCallIsTheAgentsWork()
    {
        foreach (var kind in Enum.GetValues<AssistantToolKind>())
        {
            var moves = AgentWork.Moves;
            using (var work = AssistantTools.BeginWork(kind, restartAsked: false))
            {
                Assert.NotNull(work);
                Assert.True(AgentWork.Count >= 1);
            }
            // It started and ended: the agent's work moved on twice.
            Assert.True(AgentWork.Moves >= moves + 2);
        }
    }

    [Theory]
    [InlineData(AssistantToolKind.Change)]
    [InlineData(AssistantToolKind.Destructive)]
    public void AfterTheRestartIsAsked_AChangeIsRefused(AssistantToolKind kind)
    {
        var refused = Assert.Throws<AssistantException>(() => AssistantTools.BeginWork(kind, restartAsked: true));
        Assert.Equal(AgentWork.RestartingText, refused.Message);
    }

    [Fact]
    public void AfterTheRestartIsAsked_AReadRuns_WithoutHoldingUpTheRestart()
        => Assert.Null(AssistantTools.BeginWork(AssistantToolKind.Read, restartAsked: true));

    [Fact]
    public async Task AChangeTool_IsRefusedBeforeItDoesAnything_OnceTheRestartIsAsked()
    {
        using var switches = new TestSwitches(enabled: true, askBeforeChanges: false);
        using var document = JsonDocument.Parse("""{"pc":"NOSUCHPC","runner":"r1","action":"stop"}""");
        AgentWork.AskRestart();
        try
        {
            var refused = await Assert.ThrowsAsync<AssistantException>(() =>
                AssistantTools.CallAsync("control_runner", document.RootElement, (_, _) => Task.FromResult(true)));
            Assert.Equal(AgentWork.RestartingText, refused.Message);
        }
        finally { AgentWork.ForgetRestart(); }
    }

    [Fact]
    public void TheRefusal_SaysWhatHappensAndWhatToDo()
    {
        Assert.Contains("restarts to finish an update", AgentWork.RestartingText);
        Assert.Contains("Try again in a few minutes.", AgentWork.RestartingText);
        Assert.DoesNotContain("'", AgentWork.RestartingText);
        Assert.DoesNotContain("'", MachineRelay.RestartingNowText);
    }

    [Fact]
    public void WorkThatMovesOn_ShowsInTheStatus_AndOlderAgentsSendNone()
    {
        var status = JsonSerializer.Deserialize<MachineStatusPayload>("""{"Busy":true}""")!;
        Assert.Equal(0, status.WorkMoves);
        var moves = AgentWork.Moves;
        AgentWork.MovedOn();
        Assert.True(AgentWork.Moves > moves);
    }
}
