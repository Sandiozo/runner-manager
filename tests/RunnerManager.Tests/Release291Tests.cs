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

/// <summary>
/// After an update, the fleet agent's task is registered for the new copy only once the agent finished its work:
/// registering may end the running agent, and with it a runner create it runs for another PC.
/// </summary>
public class AgentTaskSwitchTests
{
    private static MachineRelay.AgentRestartSteps Steps(List<string> done, Exception? registerFails = null) => new(
        () => { done.Add("wait"); return Task.CompletedTask; },
        () => { done.Add("stop"); return Task.CompletedTask; },
        exe => { done.Add($"register {exe}"); return registerFails == null ? Task.CompletedTask : Task.FromException(registerFails); },
        () => { done.Add("start"); return Task.CompletedTask; });

    [Fact]
    public async Task AfterAnUpdate_TheTaskIsRegistered_OnlyAfterTheWaitAndTheStop()
    {
        var done = new List<string>();
        var log = new List<string>();
        await MachineRelay.RestartAgentAsync(Steps(done), @"C:\RM\RunnerManager.exe", log.Add);
        Assert.Equal(new[] { "wait", "stop", @"register C:\RM\RunnerManager.exe", "start" }, done);
        Assert.Equal("Switching the fleet agent to the new version...", Assert.Single(log));
    }

    [Fact]
    public async Task ARestartWithoutAnUpdate_DoesNotRegister()
    {
        var done = new List<string>();
        await MachineRelay.RestartAgentAsync(Steps(done), switchTo: null, log: null);
        Assert.Equal(new[] { "wait", "stop", "start" }, done);
    }

    [Fact]
    public async Task WhenRegisteringFails_TheAgentIsStillStarted_AndTheFailureIsShown()
    {
        var done = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MachineRelay.RestartAgentAsync(Steps(done, new InvalidOperationException("Access is denied.")), @"C:\RM\RunnerManager.exe", null));
        Assert.Equal("start", done[^1]);
        Assert.Contains("started again", error.Message);
        Assert.Contains("Access is denied.", error.Message);
    }

    [Fact]
    public async Task TheAgentItself_StartsTheHelperFirst_ThenRegistersBeforeItExits()
    {
        var done = new List<string>();
        var log = new List<string>();
        await MachineRelay.SwitchTaskBeforeExitAsync(@"C:\RM\RunnerManager.exe", () => done.Add("helper"),
            exe => { done.Add($"register {exe}"); return Task.CompletedTask; }, log.Add);
        Assert.Equal(new[] { "helper", @"register C:\RM\RunnerManager.exe" }, done);
        Assert.Empty(log);
    }

    [Fact]
    public async Task TheAgentItself_LogsAFailedRegistration_AndStillExits()
    {
        var log = new List<string>();
        await MachineRelay.SwitchTaskBeforeExitAsync(@"C:\RM\RunnerManager.exe", () => { },
            _ => Task.FromException(new InvalidOperationException("Access is denied.")), log.Add);
        Assert.Equal("Could not switch the fleet agent's task to the new version: Access is denied.", Assert.Single(log));

        var helper = 0;
        await MachineRelay.SwitchTaskBeforeExitAsync(null, () => helper++, _ => throw new InvalidOperationException("not called"), log.Add);
        Assert.Equal(1, helper);
    }
}

/// <summary>
/// "Add a PC": a PC that joined with the fleet code but could not be saved here. The error is shown, and the state stays
/// one the window knows: a newer PC is told it was not added, and an older one, which kept the code, still counts.
/// </summary>
public class JoinNotSavedTests
{
    [Fact]
    public void APcThatConfirms_IsToldItWasNotAdded()
    {
        var failure = PairingHostWindow.JoinNotSaved("LAPTOP", confirms: true, "The file is in use.");
        Assert.True(failure.TellThePc);
        Assert.Equal("LAPTOP was not added. This PC could not save it.", failure.Result);
        Assert.Contains("The file is in use.", failure.Message);
        Assert.Contains("Try again.", failure.Message);
    }

    [Fact]
    public void AnOlderPc_ThatKeptTheCode_IsInTheFleet_AndTheUserIsTold()
    {
        var failure = PairingHostWindow.JoinNotSaved("LAPTOP", confirms: false, "Access is denied.");
        Assert.False(failure.TellThePc);
        Assert.Equal("✓  LAPTOP joined the fleet, but this PC could not save it.", failure.Result);
        Assert.Contains("has the fleet code and is in the fleet", failure.Message);
        Assert.Contains("Access is denied.", failure.Message);
        Assert.Contains("Settings → Fleet", failure.Message);
        foreach (var text in new[] { failure.Message, failure.Result, PairingHostWindow.JoinNotSaved("X", true, "e").Message })
        {
            Assert.DoesNotContain("'", text);
            Assert.DoesNotContain("pairing code", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ANextRequestWithItsName_IsStillWarnedAbout()
    {
        // The window checks a request also against the PCs that joined but were not saved.
        var check = PairingHostWindow.CheckRequest(new FleetConfig(), "LAPTOP", "2222", [("LAPTOP", "1111")], "HOST-PC", "9999");
        Assert.True(check.CanAllow);
        Assert.Contains("A PC named LAPTOP was allowed a moment ago", check.Warning);
    }
}
