using System.IO;
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
        var built = new MachineStatusPayload { MachineName = "PC1", UpdatedUtc = DateTime.UtcNow };
        var moves = AgentWork.Moves;
        AgentWork.MovedOn();
        // The status the agent sends, as a dashboard reads it.
        var answer = MachineRelay.AgentAnswer(built, DateTime.UtcNow, ["cmd-1"], []);
        var read = JsonSerializer.Deserialize<MachineStatusPayload>(JsonSerializer.Serialize(answer))!;
        Assert.True(read.WorkMoves > moves);
        Assert.True(read.Busy);
        Assert.Equal(new[] { "cmd-1" }, read.PendingCommandIds);
        // Work moves on again: the next status says so.
        AgentWork.MovedOn();
        Assert.True(MachineRelay.AgentAnswer(built, DateTime.UtcNow, [], []).WorkMoves > read.WorkMoves);

        var status = JsonSerializer.Deserialize<MachineStatusPayload>("""{"Busy":true}""")!;
        Assert.Equal(0, status.WorkMoves);
    }
}

/// <summary>
/// The last seconds of the fleet agent's own update (Update on the card of another PC): no command that the agent took
/// is lost, an agent that cannot restart does not refuse commands for good, and a failure to finish the update is shown
/// as the result of the update's command.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
public class AgentUpdateExitTests
{
    /// <summary>The agent's queue, as the agent's loop has it.</summary>
    private sealed class Queue
    {
        public readonly System.Threading.Channels.Channel<MachineCommand> Commands = System.Threading.Channels.Channel.CreateUnbounded<MachineCommand>();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);

        public void Take(string id)
            => MachineRelay.TakeCommand(new MachineCommand { Id = id, Target = "runner", Action = "Start", TargetKey = "r1" }, Pending, Commands.Writer, Done.Contains);

        public List<string> Read()
        {
            var ids = new List<string>();
            while (Commands.Reader.TryRead(out var command)) ids.Add(command.Id);
            return ids;
        }
    }

    [Fact]
    public async Task ACommandTakenJustBeforeTheAgentStopsTakingCommands_StillRuns()
    {
        var queue = new Queue();
        try
        {
            // The loop read the queue and found it empty. Then a command came in, before the restart was due.
            queue.Take("late");
            var ran = new List<string>();
            await MachineRelay.StopTakingCommandsAsync(() =>
            {
                // When the agent reads the queue again, it already takes no new command, so none can come in after it.
                var refused = Assert.Throws<InvalidOperationException>(() => queue.Take("new"));
                Assert.Equal(MachineRelay.RestartingNowText, refused.Message);
                ran.AddRange(queue.Read());
                return Task.CompletedTask;
            });
            Assert.Equal(new[] { "late" }, ran);
        }
        finally { MachineRelay.TakeCommandsAgain(); }
    }

    [Fact]
    public async Task WhileTheAgentExits_ANewCommandIsRefused_ButOneSentAgainIsTaken()
    {
        var queue = new Queue();
        queue.Take("queued");
        queue.Done.Add("done");
        try
        {
            await MachineRelay.StopTakingCommandsAsync(() => Task.CompletedTask);
            var refused = Assert.Throws<InvalidOperationException>(() => queue.Take("new"));
            Assert.Equal(MachineRelay.RestartingNowText, refused.Message);
            Assert.False(queue.Pending.ContainsKey("new"));
            // Sent again after a lost answer: a command that is queued is queued once, and one that is done is taken.
            queue.Take("queued");
            queue.Take("done");
            Assert.Equal(new[] { "queued", "done" }, queue.Read());
        }
        finally { MachineRelay.TakeCommandsAgain(); }
        queue.Take("new");
        Assert.Equal(new[] { "new" }, queue.Read());
    }

    [Fact]
    public async Task WhenTheHelperCannotStart_TheAgentStays_TakesCommandsAgain_AndSaysWhy()
    {
        var queue = new Queue();
        AgentWork.AskRestart();
        try
        {
            await MachineRelay.StopTakingCommandsAsync(() => Task.CompletedTask);
            var failures = new List<string>();
            var registered = 0;
            var exits = await MachineRelay.SwitchTaskBeforeExitAsync(@"C:\RM\RunnerManager.exe",
                () => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified."),
                _ => { registered++; return Task.CompletedTask; }, failures.Add);

            Assert.False(exits);
            // Nothing would start the agent again, so its task is left as it is.
            Assert.Equal(0, registered);
            Assert.Equal($"The fleet agent could not restart to finish the update, so it still runs v{AppUpdater.CurrentVersion}: " +
                         "The system cannot find the file specified.", Assert.Single(failures));
            // It takes commands and AI assistants' changes again.
            queue.Take("next");
            Assert.Equal(new[] { "next" }, queue.Read());
            Assert.False(AgentWork.RestartAsked);
        }
        finally { MachineRelay.TakeCommandsAgain(); }
    }

    [Fact]
    public void AFailedTaskSwitch_IsTheResultOfTheUpdate_InTheFirstStatusAfterTheRestart()
    {
        var folder = Directory.CreateTempSubdirectory("rm-291-state-").FullName;
        var path = Path.Combine(folder, "agent-state.json");
        try
        {
            const string failure = "agent Update failed: Could not switch the fleet agent's task to the new version: Access is denied.";
            var state = new MachineRelay.AgentState();
            state.MarkProcessed("update-1", "Updated to v9.9.9. Restarting.");
            state.MarkProcessed("start-2", "Runner start completed");
            Assert.Equal(failure, MachineRelay.UpdateFailure(state, "update-1",
                "Could not switch the fleet agent's task to the new version: Access is denied.", afterRestart: true));
            MachineRelay.SaveAgentState(state, path);

            // The agent exits, and the helper starts it again from the saved state.
            var next = MachineRelay.LoadAgentState(path);
            var shown = MachineRelay.StartResult(next, path);
            Assert.Equal(failure, shown);
            Assert.True(MachineRelay.IsFailedResult(shown));
            // The first status after the restart, as the PC's card shows it.
            var machine = MachineInfo.FromPayload("OTHERPC", new MachineStatusPayload
            {
                MachineName = "OTHERPC", LastCommandId = next.LastCommandId, LastCommandResult = shown, RecentCommands = next.Results
            });
            Assert.Equal(failure, machine.LastResultText);
            Assert.Equal(failure, machine.CommandResult("update-1"));
            Assert.Equal("Runner start completed", machine.CommandResult("start-2"));
            // Shown once: the start after that says that the agent started.
            Assert.Equal($"Fleet agent v{AppUpdater.CurrentVersion} started", MachineRelay.StartResult(MachineRelay.LoadAgentState(path), path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AFailedRestart_IsTheResultOfTheUpdate_WhileTheAgentRuns()
    {
        var state = new MachineRelay.AgentState();
        state.MarkProcessed("update-1", "Updated to v9.9.9. Restarting.");
        var result = MachineRelay.UpdateFailure(state, "update-1", "The fleet agent could not restart to finish the update.", afterRestart: false);
        Assert.Equal("agent Update failed: The fleet agent could not restart to finish the update.", result);
        Assert.Equal(result, state.Results.Single(r => r.Id == "update-1").Result);
        Assert.Equal("update-1", state.LastCommandId);
        // The agent did not exit, so its next start says that it started.
        Assert.Null(state.ResultAtStart);
    }

    [Fact]
    public void TheAgentLoop_StopsTakingCommandsBeforeItWaits_AndKeepsAFailure()
    {
        var code = File.ReadAllText(Path.Combine(DashboardSource.Dir, "MachineRelay.cs"));
        var stop = code.IndexOf("await StopTakingCommandsAsync(RunQueuedCommandsAsync);", StringComparison.Ordinal);
        Assert.True(stop > 0);
        Assert.True(stop < code.IndexOf("await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None); // let the dashboard read the result", StringComparison.Ordinal));
        Assert.Contains("if (failure != null) KeepUpdateFailure(failure, exiting);", code);
        Assert.Contains("if (exiting) Environment.Exit(0);", code);
        Assert.Contains("var lastResult = StartResult(state);", code);
        Assert.DoesNotContain("_exitingForUpdate = true;", code.Replace("lock (CommandGate) _exitingForUpdate = true;", string.Empty));
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
        Assert.True(await MachineRelay.SwitchTaskBeforeExitAsync(@"C:\RM\RunnerManager.exe", () => done.Add("helper"),
            exe => { done.Add($"register {exe}"); return Task.CompletedTask; }, log.Add));
        Assert.Equal(new[] { "helper", @"register C:\RM\RunnerManager.exe" }, done);
        Assert.Empty(log);
    }

    [Fact]
    public async Task TheAgentItself_ReportsAFailedRegistration_AndStillExits()
    {
        var log = new List<string>();
        Assert.True(await MachineRelay.SwitchTaskBeforeExitAsync(@"C:\RM\RunnerManager.exe", () => { },
            _ => Task.FromException(new InvalidOperationException("Access is denied.")), log.Add));
        Assert.Equal("Could not switch the fleet agent's task to the new version: Access is denied.", Assert.Single(log));

        var helper = 0;
        Assert.True(await MachineRelay.SwitchTaskBeforeExitAsync(null, () => helper++, _ => throw new InvalidOperationException("not called"), log.Add));
        Assert.Equal(1, helper);
    }
}

/// <summary>
/// "Add a PC": a PC that joined with the fleet code but could not be saved here. The error is shown, and the state stays
/// one the window knows: a newer PC is told it was not added, and an older one, which kept the code, still counts.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
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

    private static object? Call(PairingHostWindow window, string method, object argument)
        => typeof(PairingHostWindow).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [argument]);

    [Fact]
    public void ANextRequestWithItsName_IsStillWarnedAbout()
        => WpfTestApp.Run(() =>
        {
            // This PC is not in a fleet, so the window cannot save a PC that joined.
            FleetConfig.Update(c =>
            {
                c.KnownPeers.Clear();
                c.ProtectedFleetKey = string.Empty;
            });
            var window = new PairingHostWindow(CommandSigner.NewKey());
            var errors = new List<string>();
            window.ShowError = (_, message) => errors.Add(message);
            try
            {
                // An older PC, which kept the fleet code as it arrived.
                Call(window, "OnJoined", new Pairing.JoinRequest { Name = "LAPTOP", MachineId = "1111", Address = "10.0.0.5", Confirms = false });
                Assert.Equal(new[] { "LAPTOP" }, window.JoinedPcs);
                Assert.Contains("has the fleet code and is in the fleet", Assert.Single(errors));
                Assert.Empty(FleetConfig.Current.KnownPeers);

                // The window checks the next request also against the PCs that joined but were not saved.
                var check = (PairingHostWindow.RequestCheck)Call(window, "Check", new Pairing.JoinRequest { Name = "LAPTOP", MachineId = "2222" })!;
                Assert.True(check.CanAllow);
                Assert.Contains("A PC named LAPTOP was allowed a moment ago", check.Warning);
                Assert.Null(((PairingHostWindow.RequestCheck)Call(window, "Check", new Pairing.JoinRequest { Name = "DESKTOP", MachineId = "3333" })!).Warning);
            }
            finally
            {
                // The window was never shown: its timer would close it later.
                ((System.Windows.Threading.DispatcherTimer)typeof(PairingHostWindow)
                    .GetField("_expiry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!).Stop();
            }
        });
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
    [InlineData("\u043E\u0444\u0438\u0441-2")]            // офис-2: Cyrillic, in small letters
    [InlineData("\u0395\u03BB\u03BB\u03AC\u03B4\u03B1")] // Ελλάδα: Greek
    [InlineData("\u6771\u4EAC-2")]                        // 東京-2: Tokyo in Japanese, with a digit
    [InlineData("\u0540\u0561\u0575-1")]                  // Հայ-1: Armenian
    [InlineData("J\u0100NIS-PC")]                         // JĀNIS-PC: Latin with a macron
    [InlineData("LAPTOP")]
    [InlineData("build-01")]
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
        // The letters of all other scripts count as one more alphabet.
        Assert.Equal(new[] { LookAlikes.Alphabet.Latin, LookAlikes.Alphabet.Other }, LookAlikes.AlphabetsIn("\u6771\u4EAC-PC-2"));
        Assert.Equal(new[] { LookAlikes.Alphabet.Other }, LookAlikes.AlphabetsIn("\u6771\u4EAC-\u3068\u3046\u304D\u3087\u3046"));
        // Modifier letters, such as the apostrophe of O'NEIL, are Latin.
        Assert.Equal(new[] { LookAlikes.Alphabet.Latin }, LookAlikes.AlphabetsIn("O\u02BCNEIL"));
        Assert.Empty(LookAlikes.AlphabetsIn("123-_"));
        Assert.Equal(LookAlikes.AsItLooks("OFFICE-PC"), LookAlikes.AsItLooks("\u043Effice-\u0440c"));
        Assert.NotEqual(LookAlikes.AsItLooks("OFFICE-PC"), LookAlikes.AsItLooks("\u041E\u0424\u0418\u0421-\u041F\u041A"));
    }

    private static FleetConfig HomeFleet() => new()
    {
        KnownPeers =
        {
            new PeerRecord { Name = "HOME-PC" }, new PeerRecord { Name = "GAME-PC" }, new PeerRecord { Name = "DEV-VM" },
            new PeerRecord { Name = "FILE-SRV" }, new PeerRecord { Name = "BUILD-3" }
        }
    };

    private static string? HomeWarning(string name)
        => PairingHostWindow.CheckRequest(HomeFleet(), name, null, [], "HOST-PC", "8888").Warning;

    [Theory]
    [InlineData("\u0397\u039F\u039C\u0395-\u03A1\u03F9", "HOME-PC")]  // ΗΟΜΕ-ΡϹ: Greek, with a capital lunate sigma
    [InlineData("\u03B7\u03BF\u03BC\u03B5-\u03C1\u03F2", "HOME-PC")]  // ηομε-ρϲ: the same in small letters
    [InlineData("\u050C\u0410\u041C\u0415-\u0420\u0421", "GAME-PC")]  // ԌАМЕ-РС: Cyrillic, with a Komi Sje
    [InlineData("\u043D\u043E\u043C\u0435-\u0440\u0441", "HOME-PC")]  // номе-рс: Cyrillic in small letters
    [InlineData("DEV-\u0474M", "DEV-VM")]                             // DEV-ѴM: a Cyrillic izhitsa
    [InlineData("\u03DCILE-SRV", "FILE-SRV")]                         // ϜILE-SRV: a Greek digamma
    [InlineData("BUILD-\u0417", "BUILD-3")]                           // BUILD-З: a Cyrillic Ze for the digit 3
    public void ANameWhoseLettersOnlyLookLikeASavedName_IsWarnedAbout(string name, string saved)
        => Assert.Equal($"{name} looks like {saved}, a PC in this fleet, but some of its letters only look alike, so it is not the same name. " +
                        "Allow only if you expect a PC with this name.", HomeWarning(name));

    [Theory]
    // Letters that look like a Latin letter or a digit.
    [InlineData("\u0405\u0406\u0408\u051A\u051C\u04BA\u04AE\u037F", "SIJQWHYJ")]
    [InlineData("\u050C\u0474\u0417\u042C\u04D4\u04D8\u04E8\u0472\u04E0\u04AA", "GV3B\u00C6\u018F\u019F\u019F\u01B7\u00C7")]
    [InlineData("\u0475\u0461\u045B\u0431\u04D5\u04D9\u04E9\u0473\u04E1\u04AB", "VW\u01266\u00C6\u018F\u019F\u019F\u01B7\u00C7")]
    [InlineData("\u03DC\u03FA\u03F7\u03F9", "FM\u00DEC")]
    [InlineData("\u03B3\u03C3\u03B2\u03F8\u03F2", "YO\u00DF\u00DEC")]
    // Accented letters: a Cyrillic Ё, Ї and Ӧ, a Greek Ά and ό.
    [InlineData("\u0401\u0407\u04E6\u0386\u03CC", "\u00CB\u00CF\u00D6\u00C1\u00D3")]
    public void LookAlikeLetters_AreReadAsTheLatinLetterOrDigitTheyLookLike(string letters, string latin)
        => Assert.True(LookAlikes.LookAlike(letters, latin), $"{letters} {LookAlikes.AsItLooks(letters)} {LookAlikes.AsItLooksInCapitals(letters)}");

    [Fact]
    public void SmallLetters_AreAlsoReadInCapitals()
    {
        // н and м look like small capitals; in capitals, Н and М look like H and M.
        Assert.NotEqual("HOME_PC", LookAlikes.AsItLooks("\u043D\u043E\u043C\u0435-\u0440\u0441"));
        Assert.Equal("HOME_PC", LookAlikes.AsItLooksInCapitals("\u043D\u043E\u043C\u0435-\u0440\u0441"));
        // A Greek ν looks like v as it is written; its capital Ν looks like N.
        Assert.Equal("DEV", LookAlikes.AsItLooks("de\u03BD"));
        Assert.Equal("DEN", LookAlikes.AsItLooksInCapitals("de\u03BD"));
        Assert.True(LookAlikes.LookAlike("de\u03BD", "DEV"));
        Assert.False(LookAlikes.LookAlike("\u041E\u0424\u0418\u0421", "OFIS"));
    }

    [Theory]
    [InlineData("H\u0555ME-PC")]      // HՕME-PC: an Armenian Oh
    [InlineData("HOME-\u13E2C")]      // HOME-ᏢC: a Cherokee Tlv, which looks like P
    [InlineData("HOME-\uA4D1C")]      // HOME-ꓑC: a Lisu Pa, which looks like P
    [InlineData("\u6771\u4EAC-PC")]   // 東京-PC: Tokyo in Japanese, with Latin letters
    [InlineData("\u0E01\u0E34\u0E19-PC")] // กิน-PC: Thai, with Latin letters
    public void LatinLettersWithLettersOfAnyOtherScript_GetTheMixedAlphabetsWarning(string name)
    {
        var check = PairingHostWindow.CheckRequest(HomeFleet(), name, null, [], "HOST-PC", "8888");
        Assert.True(check.CanAllow);
        Assert.Equal($"The name {name} mixes Latin letters with letters of another alphabet, so it can look like the name of another PC. " +
                     "Allow only if you expect a PC with this name.", check.Warning);
    }

    [Fact]
    public void TheMixedAlphabetsWarning_NamesTheAlphabetsItFound()
    {
        Assert.StartsWith("The name A\u0392\u0412 mixes Latin, Greek and Cyrillic letters, so", HomeWarning("A\u0392\u0412"));
        Assert.StartsWith("The name A\u0392\u0412\u6771 mixes Latin, Greek and Cyrillic letters with letters of another alphabet, so", HomeWarning("A\u0392\u0412\u6771"));
        Assert.StartsWith("The name \u0394\u0416 mixes Greek and Cyrillic letters, so", HomeWarning("\u0394\u0416"));
        Assert.StartsWith("The name \u0416\u6771 mixes Cyrillic letters with letters of another alphabet, so", HomeWarning("\u0416\u6771"));
    }
}

/// <summary>The job view and the job log: options that hide a key, and a silent step when the processes cannot be read.</summary>
public class JobOutputTests
{
    [Theory]
    // Kept for every program on purpose: a harmless value costs only the arguments after it.
    [InlineData("sort.exe --key 2 data.txt", "")]
    [InlineData("sort.exe -r --key=2 data.txt", "-r")]
    // A key for a program that no table lists.
    [InlineData("uploader.exe publish --key ABC123 pkg", "publish")]
    [InlineData("uploader.exe publish --api-key ABC123 pkg", "publish")]
    public void KeyOptions_AreLeftOutForEveryProgram(string commandLine, string expected)
    {
        var shown = RunnerActivity.ShortArguments(commandLine);
        Assert.Equal(expected, shown);
        Assert.DoesNotContain("ABC123", shown);
    }

    private static string NewRunnerRoot()
    {
        var root = Directory.CreateTempSubdirectory("rm-291-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "_diag", "pages"));
        return root;
    }

    private static void WritePage(string root, string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(root, "_diag", "pages", name);
        File.WriteAllText(path, text);
        File.SetCreationTimeUtc(path, writtenUtc);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static RunnerActivity.ProcessSnapshot Worker(string root, DateTime startedUtc) => new(new[]
    {
        new RunnerActivity.ProcessRow(1, 0, "Runner.Worker.exe", Path.Combine(root, "bin", "Runner.Worker.exe"), string.Empty, 0, 0,
            System.Management.ManagementDateTimeConverter.ToDmtfDateTime(startedUtc.ToLocalTime()))
    });

    private static RunnerActivity.ProcessSnapshot NoProcessList() => throw new System.Runtime.InteropServices.COMException("WMI is not available.");

    [Fact]
    public void ASilentStep_KeepsItsOutput_WhenTheProcessListCannotBeReadLater()
    {
        var root = NewRunnerRoot();
        try
        {
            var now = DateTime.UtcNow;
            WritePage(root, "step_1.log", "Compiling\n", now.AddMinutes(-1));
            // The process list shows the page as the running job's.
            Assert.Equal(new[] { "Compiling", "" }, RunnerActivity.ReadJobOutput(root, 10, () => Worker(root, now.AddMinutes(-2)), now).Lines);

            // Three hours later the step is still silent, and the process list cannot be read.
            var later = RunnerActivity.ReadJobOutput(root, 10, NoProcessList, now.AddHours(3));
            Assert.Equal(new[] { "Compiling", "" }, later.Lines);
            Assert.False(later.ProcessesUnreadable);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnOldPageThatWasNeverShownAsTheJobs_IsNotShown_AndTheLogSaysWhy()
    {
        var root = NewRunnerRoot();
        try
        {
            var now = DateTime.UtcNow;
            WritePage(root, "earlier_1.log", "an earlier job\n", now.AddHours(-1));
            var output = RunnerActivity.ReadJobOutput(root, 10, NoProcessList, now);
            Assert.Empty(output.Lines);
            Assert.True(output.ProcessesUnreadable);
            var text = LogReader.JobLogText(output);
            Assert.StartsWith("(The processes on this PC could not be read", text);
            Assert.Contains("older than 5 minutes is not shown", text);

            // Once the runner runs no job, the page that was the job's is forgotten.
            WritePage(root, "step_1.log", "this job\n", now.AddMinutes(-1));
            Assert.Equal(new[] { "this job", "" }, RunnerActivity.ReadJobOutput(root, 10, () => Worker(root, now.AddMinutes(-2)), now).Lines);
            Assert.Empty(RunnerActivity.ReadJobOutput(root, 10, () => new RunnerActivity.ProcessSnapshot([]), now).Lines);
            Assert.True(RunnerActivity.ReadJobOutput(root, 10, NoProcessList, now.AddHours(1)).ProcessesUnreadable);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheJobLog_WithProcessesThatCanBeRead_SaysThatThereIsNoOutputYet()
        => Assert.StartsWith("(No output yet.", LogReader.JobLogText(new RunnerActivity.JobOutput([], ProcessesUnreadable: false)));
}

/// <summary>An answer that cannot be read, which arrives after the failures were forgotten (see <see cref="FakeGh"/>).</summary>
[Collection(GitHubCliCollection.Name)]
public class UnreadableAfterResetTests
{
    [Fact]
    public async Task AnUnreadableAnswer_ToAPollSentBeforeForgetProblems_DoesNotPauseTheEndpoint()
    {
        var scope = $"orgs/r291-{Guid.NewGuid():N}"[..30];
        var list = $"{scope}/actions/runners?per_page=100";
        var runner = $"{scope}/actions/runners/3";
        var first = true;
        using var gh = new FakeGh(_ =>
        {
            // The user connects GitHub or refreshes while the list waits for GitHub's answer.
            if (first) GitHubCli.ForgetProblems();
            first = false;
            return FakeGh.Http(200, "not JSON");
        });

        Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, 3));

        // The list was asked before the reset: its unreadable answer is not kept, so the next refresh asks again.
        Assert.Null(GitHubFailures.Blocking(list));
        // The runner was asked one by one after the reset: that answer pauses it, as before.
        Assert.NotNull(GitHubFailures.Blocking(runner));
        Assert.Equal(2, gh.Calls.Length);
    }
}

/// <summary>Tests start pairing hosts on port 0: the system picks free ports as the host binds them, so no other test takes them first.</summary>
public class PortZeroTests
{
    [Fact]
    public async Task APairingHostOnPortZero_ReportsThePortsItGot_AndAnnouncesItsPairingPort()
    {
        using var first = FreePorts.Host((p, a) => new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", p, a), out var port);
        using var second = FreePorts.Host((p, a) => new Pairing.PairingHost(CommandSigner.NewKey(), "OTHERPC", p, a), out var otherPort);
        Assert.NotEqual(0, port);
        Assert.Equal(port, first.Port);
        Assert.NotEqual(0, first.AnnouncePortInUse);
        Assert.NotEqual(port, otherPort);
        Assert.NotEqual(first.AnnouncePortInUse, second.AnnouncePortInUse);
        // The host holds the port from the moment it got it.
        var other = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
        Assert.Throws<System.Net.Sockets.SocketException>(() => other.Start());

        // A find-probe is answered with the pairing port it got, not with 0.
        var found = await Pairing.FindHostsAsync(TimeSpan.FromSeconds(1), first.AnnouncePortInUse, new[] { "127.0.0.1" });
        Assert.Contains(found, h => h.Name == "HOSTPC" && h.Address.EndsWith($":{port}", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostWithAFixedPort_ReportsThatPort()
    {
        var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", 41234, 41235);
        Assert.Equal(41234, host.Port);
        Assert.Equal(41235, host.AnnouncePortInUse);
        host.Dispose();
    }
}

/// <summary>The release workflow gives gh the release notes in a file, so their quotes and line breaks reach GitHub as written.</summary>
public class ReleaseNotesTests
{
    private static string RepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RunnerManager.sln"))) return File.ReadAllText(Path.Combine(dir.FullName, name));
        throw new DirectoryNotFoundException("The repository folder was not found.");
    }

    [Fact]
    public void TheReleaseStep_PassesTheNotesInAFile_WithoutABom()
    {
        var workflow = RepoFile(Path.Combine(".github", "workflows", "release.yml"));
        var create = System.Text.RegularExpressions.Regex.Match(workflow, @"gh release create [^\n]*`\r?\n[^\n]*");
        Assert.True(create.Success);
        Assert.Contains("--notes-file $notesFile", create.Value);
        Assert.DoesNotMatch(@"--notes\s+\$notes\b", workflow);
        Assert.Contains("[System.IO.File]::WriteAllText($notesFile, $notes, [System.Text.UTF8Encoding]::new($false))", workflow);
        Assert.Contains("Join-Path $env:RUNNER_TEMP", workflow);
    }

    [Fact]
    public void TheChangelog_HasASectionForThisVersion_ThatTheWorkflowFinds()
    {
        var changelog = RepoFile("CHANGELOG.md");
        var version = System.Text.RegularExpressions.Regex.Match(RepoFile(Path.Combine("src", "RunnerManager", "RunnerManager.csproj")),
            @"<Version>([^<]+)</Version>").Groups[1].Value;
        // The pattern of the workflow's notes step.
        var section = System.Text.RegularExpressions.Regex.Match(changelog,
            $@"(?ms)^## {System.Text.RegularExpressions.Regex.Escape(version)}\s*\r?\n(.*?)(?=^## |\z)");
        Assert.True(section.Success, version);
        Assert.NotEmpty(section.Groups[1].Value.Trim());
    }
}
