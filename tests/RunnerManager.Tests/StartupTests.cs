using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RunnerManager.Tests;

public class StartupTests
{
    [Theory]
    [InlineData("Auto", RunnerStart.WithWindows)]
    [InlineData("Manual", RunnerStart.Off)]
    [InlineData("Disabled", RunnerStart.Disabled)]
    [InlineData("", RunnerStart.Unknown)]
    public void ServiceStartMode_MapsToWhatStartsTheRunner(string startMode, string expected)
        => Assert.Equal(expected, RunnerService.ServiceStartsAt(startMode));

    [Fact]
    public void ConsoleRunner_StartsAtSignIn_OnlyWhenAnEntryNamesItsFolder()
    {
        var root = @"C:\GitHub-Runners\startup-test-" + Guid.NewGuid().ToString("N");
        var entries = new List<string> { $"conhost.exe --headless cmd.exe /d /c \"{root}\\run-in-session.cmd\"" };

        Assert.Equal(RunnerStart.SignIn, RunnerService.ConsoleStartsAt(root, entries, new[] { root }));
        Assert.Equal(RunnerStart.Unknown, RunnerService.ConsoleStartsAt(root + "-2", entries, new[] { root, root + "-2" }));
        Assert.Equal(RunnerStart.Unknown, RunnerService.ConsoleStartsAt(root, new List<string>(), new[] { root }));

        var other = root + "-other";
        var shared = new List<string> { $"start \"{root}\\run.cmd\"\r\nstart \"{other}\\run.cmd\"" };
        Assert.Equal(RunnerStart.SignInShared, RunnerService.ConsoleStartsAt(root, shared, new[] { root, other }));
    }

    [Fact]
    public async Task Restore_CountsEntriesThatAreAlreadyBack_AndReturnsTheOnesThatFailed()
    {
        var dir = Directory.CreateTempSubdirectory("rm-startup-");
        try
        {
            string P(string name) => Path.Combine(dir.FullName, name);
            File.WriteAllText(P("moved-a.lnk"), "a");
            File.WriteAllText(P("b.lnk"), "made again");
            File.WriteAllText(P("moved-b.lnk"), "b");
            var changes = new RunnerService.StartupChanges();
            changes.Files.Add((P("a.lnk"), P("moved-a.lnk")));   // put back
            changes.Files.Add((P("b.lnk"), P("moved-b.lnk")));   // already there again
            changes.Files.Add((P("c.lnk"), P("moved-c.lnk")));   // the turned-off copy is gone

            var failed = await changes.RestoreAsync(_ => { });

            Assert.True(File.Exists(P("a.lnk")));
            Assert.Equal("made again", File.ReadAllText(P("b.lnk")));
            Assert.Equal((P("c.lnk"), P("moved-c.lnk")), Assert.Single(failed.Files));
            Assert.Empty(failed.Tasks);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void StartupOffRecord_IsKeptPerRunnerFolder()
    {
        var a = RunnerService.StartupOffPath(@"C:\GitHub-Runners\a");
        Assert.Equal(a, RunnerService.StartupOffPath(@"c:\github-runners\A\"));
        Assert.NotEqual(a, RunnerService.StartupOffPath(@"C:\GitHub-Runners\b"));
        Assert.EndsWith("startup-off.json", a);
    }

    [Fact]
    public void StartupOffRecord_RoundTripsTheEntriesToPutBack()
    {
        var changes = new RunnerService.StartupChanges();
        changes.Files.Add((@"C:\Users\u\Startup\runner.lnk", @"C:\state\off-1\runner.lnk"));
        changes.Tasks.Add(@"\Runner at logon");
        changes.RunValues.Add((Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "runner",
            @"%USERPROFILE%\run.cmd", RegistryValueKind.ExpandString, @"C:\state\off-1\run-runner.json"));

        var json = JsonSerializer.Serialize(RunnerService.StartupOffRecord.From(@"C:\r", changes));
        var back = JsonSerializer.Deserialize<RunnerService.StartupOffRecord>(json)!.ToChanges();

        Assert.Equal(changes.Files, back.Files);
        Assert.Equal(changes.Tasks, back.Tasks);
        var value = Assert.Single(back.RunValues);
        Assert.Same(Registry.CurrentUser, value.Hive);
        Assert.Equal(("runner", (object)@"%USERPROFILE%\run.cmd", RegistryValueKind.ExpandString), (value.Name, value.Value, value.Kind));
    }

    [Fact]
    public void RegistrationOptions_FromOlderAgents_StartWithWindows()
    {
        var options = JsonSerializer.Deserialize<RunnerRegistrationOptions>("""{"RunnerGroup":"Default","Ephemeral":false}""")!;
        Assert.False(options.ManualStart);
        Assert.True(new RunnerInfo { StartsAt = RunnerStart.Off }.RegistrationOptions.ManualStart);
        Assert.False(new RunnerInfo { StartsAt = RunnerStart.Disabled }.RegistrationOptions.ManualStart);
        // The old copy left disabled by a move that timed out keeps its manual start for the next move.
        Assert.True(new RunnerInfo { StartsAt = RunnerStart.Disabled, ManualStartBeforeMove = true }.RegistrationOptions.ManualStart);
    }

    [Fact]
    public void StartupOption_NeedsProtocol13OnOtherPcs()
    {
        MachineRunnerStatus Service(string startsAt, int protocol, bool local = false) => new()
        {
            ServiceName = "actions.runner.o-r.a", ServiceInstalled = true, StartsAt = startsAt,
            AgentProtocolVersion = protocol, MachineOnline = true, IsLocalMachine = local
        };

        Assert.True(Service(RunnerStart.WithWindows, 13).CanChangeStartup);
        Assert.True(Service(RunnerStart.Off, 13).CanChangeStartup);
        Assert.False(Service(RunnerStart.Unknown, 12).CanChangeStartup);
        Assert.Equal("Update the fleet agent to see this", Service(RunnerStart.Unknown, 12).StartsAtText);
        Assert.True(Service(RunnerStart.WithWindows, 13).StartsAutomatically);
        Assert.False(Service(RunnerStart.Disabled, 13).StartsAutomatically);
        Assert.Equal("Start with Windows", Service(RunnerStart.Off, 13).StartupHeader);
        Assert.False(new MachineRunnerStatus
        {
            ServiceName = "actions.runner.o-r.a", ServiceInstalled = true, StartsAt = RunnerStart.Disabled, MovedAway = true, IsLocalMachine = true
        }.CanChangeStartup);
    }

    [Fact]
    public void StoppedConsoleRunner_IsNotShownAsMissingItsService()
    {
        var runner = new MachineRunnerStatus { InstallPath = @"C:\r", StartsAt = RunnerStart.Off, IsLocalMachine = true };

        Assert.Equal("Console · stopped", runner.StatusText);
        Assert.Equal(System.Windows.Visibility.Collapsed, runner.RepairVisibility);
        Assert.Equal(System.Windows.Visibility.Visible, runner.ConvertVisibility);
        Assert.True(runner.CanChangeStartup);
        Assert.Equal("Start when you sign in", runner.StartupHeader);

        var shared = new MachineRunnerStatus { InstallPath = @"C:\r", StartsAt = RunnerStart.SignInShared, IsLocalMachine = true };
        Assert.Equal("Console · stopped", shared.StatusText);
        Assert.True(shared.StartsAutomatically);
        Assert.False(shared.CanChangeStartup);

        var unknown = new MachineRunnerStatus { InstallPath = @"C:\r", IsLocalMachine = true };
        Assert.Equal("No service", unknown.StatusText);
        Assert.False(unknown.CanChangeStartup);
    }
}
