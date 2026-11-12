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

/// <summary>
/// "Add a PC": a name with letters of another alphabet that look like Latin ones (a Cyrillic О for a Latin O), or a
/// full-width form of this PC's name, is warned about. A name honestly written in one other alphabet is not.
/// </summary>
public class LookAlikeNameTests
{
    private static FleetConfig Fleet() => new()
    {
        KnownPeers = { new PeerRecord { Name = "OFFICE-PC", MachineId = "1111" }, new PeerRecord { Name = "DEV" } },
        ManagedPcs = { new ManagedPcRecord { Name = "COM", MachineId = "2222" } }
    };

    private static PairingHostWindow.RequestCheck Check(string name, params (string Name, string? MachineId)[] allowed)
        => PairingHostWindow.CheckRequest(Fleet(), name, "9999", allowed, "HOST-PC", "8888");

    [Fact]
    public void ACyrillicLetterInTheNameOfASavedPc_IsWarnedAbout()
    {
        // OFFICE-PC with a Cyrillic О.
        var check = Check("\u041EFFICE-PC");
        Assert.True(check.CanAllow);
        Assert.Equal("\u041EFFICE-PC looks like OFFICE-PC, a PC in this fleet, but some of its letters only look alike, so it is not the same name. " +
                     "Allow only if you expect a PC with this name.", check.Warning);
    }

    [Fact]
    public void ALowercaseGreekLetter_IsReadAsTheLatinLetterItLooksLike()
        // de + Greek nu, which looks like v.
        => Assert.StartsWith("de\u03BD looks like DEV, a PC in this fleet", Check("de\u03BD").Warning);

    [Fact]
    public void AFullWidthFormOfThisPcsName_IsWarnedAbout_ButNotBlocked()
    {
        var check = Check("\uFF28\uFF2F\uFF33\uFF34-\uFF30\uFF23");
        Assert.True(check.CanAllow);
        Assert.Equal("\uFF28\uFF2F\uFF33\uFF34-\uFF30\uFF23 looks like HOST-PC, the name of this PC, but some of its letters only look alike, " +
                     "so it is another PC. Allow only if you expect a PC with this name.", check.Warning);
        // A Cyrillic С and Р in this PC's name.
        Assert.Contains("the name of this PC", Check("HOST-\u0420\u0421").Warning);
    }

    [Fact]
    public void ANameThatMixesAlphabets_IsWarnedAbout_ThoughItLooksLikeNoSavedName()
        // LAPTOP with a Cyrillic Т.
        => Assert.Equal("The name LAP\u0422OP mixes Latin and Cyrillic letters, so it can look like the name of another PC. " +
                        "Allow only if you expect a PC with this name.", Check("LAP\u0422OP").Warning);

    [Fact]
    public void ANameThatLooksLikeAPcManagedOneWayOrAllowedAMomentAgo_NamesIt()
    {
        // COM in Cyrillic letters only: honestly written, but it looks like COM.
        Assert.StartsWith("\u0421\u041E\u041C looks like COM, a PC that this fleet manages one way", Check("\u0421\u041E\u041C").Warning);
        Assert.StartsWith("L\u0410PTOP looks like LAPTOP, which was allowed a moment ago", Check("L\u0410PTOP", ("LAPTOP", "3333")).Warning);
    }

    [Theory]
    [InlineData("\u041E\u0424\u0418\u0421-\u041F\u041A")] // ОФИС-ПК: Cyrillic
    [InlineData("\u0395\u03BB\u03BB\u03AC\u03B4\u03B1")] // Ελλάδα: Greek
    [InlineData("\u6771\u4EAC-PC")]                       // Tokyo in Japanese, with Latin letters
    [InlineData("J\u0100NIS-PC")]                         // JĀNIS-PC: Latin with a macron
    [InlineData("LAPTOP")]
    public void ANameInOneAlphabet_IsNotWarnedAbout(string name)
        => Assert.Equal(new PairingHostWindow.RequestCheck(null, CanAllow: true), Check(name));

    [Fact]
    public void TheSameNameInAnotherForm_KeepsItsOwnWarning_AndIsNotWarnedAboutTwice()
    {
        // Full-width OFFICE-PC is the same name for the warnings already.
        var warning = Check("\uFF2F\uFF26\uFF26\uFF29\uFF23\uFF25-PC").Warning;
        Assert.Equal("A PC named \uFF2F\uFF26\uFF26\uFF29\uFF23\uFF25-PC is already in this fleet. Allow only if this is the same PC.", warning);
    }

    [Fact]
    public void Alphabets_AreFoundByLetter()
    {
        Assert.Equal(new[] { LookAlikes.Alphabet.Latin, LookAlikes.Alphabet.Cyrillic }, LookAlikes.AlphabetsIn("PC-\u0430"));
        Assert.Equal(new[] { LookAlikes.Alphabet.Latin }, LookAlikes.AlphabetsIn("\u6771\u4EAC-PC-2"));
        Assert.Empty(LookAlikes.AlphabetsIn("123-_"));
        Assert.Equal(LookAlikes.AsItLooks("OFFICE-PC"), LookAlikes.AsItLooks("\u043Effice-\u0440c"));
        Assert.NotEqual(LookAlikes.AsItLooks("OFFICE-PC"), LookAlikes.AsItLooks("\u041E\u0424\u0418\u0421-\u041F\u041A"));
    }
}
