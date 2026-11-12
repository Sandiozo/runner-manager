using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace RunnerManager.Tests;

/// <summary>
/// One runner that runs twice on a PC: its Windows service, and a console window in a user's session that a startup
/// entry opens at sign-in. Only one copy gets jobs, and in the service the user's work folder makes git fail them.
/// Everything here is built from lists and temporary folders: no real service, task or registry value is read.
/// </summary>
[Collection(DashboardCollection.Name)]
public class RunsTwiceTests : IDisposable
{
    private const string ServiceName = "actions.runner.Sandiozo-project-Livonia.livonia-backup";
    private static readonly SecurityIdentifier NetworkService = new(WellKnownSidType.NetworkServiceSid, null);
    private readonly string _temp = Directory.CreateTempSubdirectory("rm-twice-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A runner folder as config.cmd leaves it, with a work folder that this test's user made.</summary>
    private string MakeRunner(string name = "livonia-backup", bool workFolder = true)
    {
        var root = Path.Combine(_temp, name);
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, ".runner"),
            $$"""{"agentId":17,"agentName":"{{name}}","gitHubUrl":"https://github.com/Sandiozo/project-Livonia","workFolder":"_work"}""");
        if (workFolder) Directory.CreateDirectory(Path.Combine(root, "_work", "project-Livonia"));
        return root;
    }

    private static RunnerServiceRecord Service(string root, string state, string startMode = "Manual", string account = @"NT AUTHORITY\NetworkService",
        string name = ServiceName)
        => new() { Name = name, DisplayName = "GitHub Actions Runner (Sandiozo-project-Livonia.livonia-backup)", State = state,
                   StartMode = startMode, Account = account, Root = root, ProcessId = state == "Running" ? 4100u : 0u };

    /// <summary>The runner's listener: the service's in session 0, a console window's in a user's session.</summary>
    private static ProcessInfo Listener(string root, uint id, uint session)
        => new() { ProcessId = id, ParentProcessId = session == 0 ? 4100u : 9000u, Name = "Runner.Listener.exe",
                   ExecutablePath = Path.Combine(root, "bin", "Runner.Listener.exe"), SessionId = session };

    /// <summary>
    /// What Runner Manager reads from the Startup folder shortcut of the incident: conhost.exe is the target, and the
    /// runner folder is only in its arguments.
    /// </summary>
    private static List<string> StartupShortcut(string root)
    {
        var arguments = System.Text.Encoding.Unicode.GetBytes($"--headless cmd.exe /d /c \"{root}\\run-in-session.cmd\"");
        var target = System.Text.Encoding.Unicode.GetBytes(@"C:\Windows\System32\conhost.exe");
        var bytes = new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02 }.Concat(target).Concat(new byte[6]).Concat(arguments).Concat(new byte[4]).ToArray();
        return new List<string> { RunnerService.ReadStartupText(bytes, shortcut: true) };
    }

    private static Task<RunnerInfo> Read(string root, List<RunnerServiceRecord> services, List<ProcessInfo> processes, List<string> startup)
        => RunnerService.BuildRunnerInfoAsync(root, services, processes, queryGitHub: false, startup, new[] { root });

    /// <summary>The runner as the fleet agent sends it to the dashboards (as MachineRelay.BuildPayload does).</summary>
    private static MachineRunnerStatus Status(RunnerInfo r, bool manuallyStopped = false) => new()
    {
        ServiceName = r.Name, InstallPath = r.InstallPath, IsInteractive = r.IsInteractive, DisplayName = r.DisplayName,
        RepositoryPath = r.RepositoryPath, Scope = r.ApiScope, AgentName = r.AgentName, AgentId = r.AgentId,
        IsRunning = r.IsRunning, ActivityText = r.ActivityText, ServiceInstalled = r.ServiceInstalled, StartsAt = r.StartsAt,
        RunsTwice = r.RunsTwice, TwoConsoleWindows = r.TwoConsoleWindows, ConsoleAtSignIn = r.ConsoleAtSignIn, ServiceAccount = r.ServiceAccount, WorkFolderOwner = r.WorkFolderOwner,
        ManuallyStopped = manuallyStopped, MachineName = "SANDIS", MachineKey = "sandis", IsLocalMachine = true, MachineOnline = true,
        AgentProtocolVersion = MachineRelay.ProtocolVersion
    };

    private static MachineInfo Pc(params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload("sandis", new MachineStatusPayload
        {
            MachineName = "SANDIS", AgentProtocolVersion = MachineRelay.ProtocolVersion, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = runners
        });

    private static string OwnerOf(string folder)
        => RunnerService.AccountName((SecurityIdentifier)new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier))!);

    // ----- The incident -----

    [Fact]
    public async Task TheIncident_IsSeenSaidAndKeptFromStartingAgain()
    {
        var root = MakeRunner();
        var services = new List<RunnerServiceRecord> { Service(root, "Running") };
        // The service's listener, and a second one of the same folder in session 1, opened by the shortcut.
        var processes = new List<ProcessInfo> { Listener(root, 4200, session: 0), Listener(root, 9100, session: 1) };
        var startup = StartupShortcut(root);

        var runner = await Read(root, services, processes, startup);

        // 1. Seen: it runs twice, from the service and the startup entry, and the service has the session.
        Assert.True(runner.RunsTwice);
        Assert.True(runner.ConsoleAtSignIn);
        Assert.True(runner.IsRunning);
        Assert.False(runner.IsInteractive);
        Assert.True(runner.HasConsoleCopy);
        Assert.Equal(RunnerStart.SignIn, runner.StartsAt);
        Assert.Equal(RunnerStart.Off, runner.ServiceStart);
        // 4. The service runs as NETWORK SERVICE, and the folders in the work folder are this user's. That is not called a
        // work folder of another account while the startup entry opens the console window: "Runs twice" says why jobs fail.
        Assert.Equal(RunnerService.AccountName(NetworkService), runner.ServiceAccount);
        Assert.Equal(string.Empty, runner.WorkFolderOwner);
        // An edit keeps the service starting manually, as it did: it does not start with Windows as well.
        Assert.True(runner.RegistrationOptions.ManualStart);

        // 2. Said: the card's state and details, the Needs attention tile, one alert, and the fleet status.
        var status = Status(runner);
        Assert.Equal(RunnerConcern.RunsTwice, status.Health.Concern);
        Assert.Equal("Runs twice", status.StatusText);
        Assert.Contains("startup entry also opens it in a console window", status.Health.Detail);
        Assert.Contains("Stop the Windows service", status.Health.Detail);
        Assert.Contains("turn off “Start when you sign in”", status.Health.Detail);
        Assert.Contains("git refuses", status.Health.Detail);
        Assert.Contains("console window", status.ServiceText);
        Assert.DoesNotContain("belong to", status.WorkFolderText);
        var pc = Pc(status);
        var attention = FleetAttention.Count(new[] { pc });
        Assert.Equal(1, attention.RunningTwice);
        Assert.Equal(1, attention.Total);
        Assert.Contains("1 runner runs twice", attention.Summary);
        Assert.True(FleetAttention.ShowsRunner(pc, pc.Runners[0]));
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SANDIS: livonia-backup — Runs twice.", Assert.Single(MainWindow.NewProblemAlerts(new[] { pc }, reported)));
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(Status(runner)) }, reported));
        var sent = JsonSerializer.Deserialize<MachineStatusPayload>(JsonSerializer.Serialize(new MachineStatusPayload { Runners = new[] { status } }))!.Runners[0];
        Assert.True(sent.RunsTwice);
        Assert.True(sent.ConsoleAtSignIn);
        Assert.Equal(runner.ServiceAccount, sent.ServiceAccount);

        // 3. Prevented: Restart is refused everywhere, Stop is not.
        Assert.NotNull(RunnerService.ServiceStartProblem(services[0], processes, startup, new[] { root }, "Restart"));
        Assert.NotNull(RunnerService.ServiceStartProblem(services[0], processes, startup, new[] { root }, "Start"));
        Assert.Null(RunnerService.ServiceStartProblem(services[0], processes, startup, new[] { root }, "Stop"));
        Assert.NotNull(FleetActions.ConsoleRunnerProblem(runner.IsInteractive, runner.ServiceInstalled, "Restart", runner.ConsoleAtSignIn, runner.RunsTwice));
        Assert.Null(FleetActions.ConsoleRunnerProblem(runner.IsInteractive, runner.ServiceInstalled, "Stop", runner.ConsoleAtSignIn, runner.RunsTwice));
        Assert.False(status.CanRestart);
        Assert.True(status.CanPower);
        Assert.Equal("Stop", status.PowerButtonText);
        var refused = Assert.Throws<AssistantException>(() => AssistantTools.RequireService(pc, status, "Restart"));
        Assert.Contains("runs twice", refused.Message);
        AssistantTools.RequireService(pc, status, "Stop");
    }

    [Fact]
    public async Task AfterTheServiceIsStopped_ItIsNotStartedAgain_AlsoWhileNoConsoleWindowRuns()
    {
        var root = MakeRunner();
        var startup = StartupShortcut(root);
        var stopped = new List<RunnerServiceRecord> { Service(root, "Stopped") };

        // Signed out: no console window runs, but the shortcut opens one at the next sign-in.
        var runner = await Read(root, stopped, new List<ProcessInfo>(), startup);
        Assert.False(runner.RunsTwice);
        Assert.False(runner.IsInteractive);
        Assert.True(runner.ConsoleAtSignIn);
        Assert.True(runner.HasConsoleCopy);
        var problem = RunnerService.ServiceStartProblem(stopped[0], new List<ProcessInfo>(), startup, new[] { root }, "Start");
        Assert.Contains("when you sign in", problem);
        Assert.Contains("Turn off “Start when you sign in”", problem);
        Assert.NotNull(RunnerService.ServiceStartProblem(stopped[0], new List<ProcessInfo>(), startup, new[] { root }, "Restart"));
        Assert.Null(RunnerService.ServiceStartProblem(stopped[0], new List<ProcessInfo>(), startup, new[] { root }, "Stop"));

        // The card offers neither Start nor Restart, and its switch is the console's.
        var status = Status(runner);
        Assert.False(status.CanPower);
        Assert.False(status.CanRestart);
        Assert.Equal("Start when you sign in", status.StartupHeader);
        Assert.Contains("starting it would run the runner twice", status.StatusTip);
        Assert.False(status.Health.IsProblem);
        Assert.False(RunnerService.StartsFromService(runner));
        Assert.Throws<AssistantException>(() => AssistantTools.RequireService(Pc(status), status, "Start"));

        // Auto-recovery, also in a fleet agent that has just started: the startup entry is enough to leave it alone.
        var recovery = new RunnerRecovery();
        var now = DateTime.UtcNow;
        var running = await Read(root, new List<RunnerServiceRecord> { Service(root, "Running") }, new List<ProcessInfo>(), startup);
        Assert.Equal(RunnerRecovery.Step.None, MachineRelay.CheckRecovery(recovery, running, watchdogOn: true, held: false, now));
        for (var i = 1; i <= 3; i++)
            Assert.Equal(RunnerRecovery.Step.None, MachineRelay.CheckRecovery(recovery, runner, watchdogOn: true, held: false, now.AddSeconds(30 * i)));
    }

    [Fact]
    public async Task AServiceRunnerWithoutAConsoleCopy_IsStillRecoveredAndStarted()
    {
        var root = MakeRunner(workFolder: false);
        var recovery = new RunnerRecovery();
        var now = DateTime.UtcNow;
        var running = await Read(root, new List<RunnerServiceRecord> { Service(root, "Running", "Auto") }, new List<ProcessInfo> { Listener(root, 4200, 0) }, new List<string>());
        var stopped = await Read(root, new List<RunnerServiceRecord> { Service(root, "Stopped", "Auto") }, new List<ProcessInfo>(), new List<string>());

        Assert.False(running.HasConsoleCopy);
        Assert.Equal(RunnerRecovery.Step.None, MachineRelay.CheckRecovery(recovery, running, true, false, now));
        Assert.Equal(RunnerRecovery.Step.None, MachineRelay.CheckRecovery(recovery, stopped, true, false, now.AddSeconds(30)));
        Assert.Equal(RunnerRecovery.Step.Start, MachineRelay.CheckRecovery(recovery, stopped, true, false, now.AddSeconds(60)));
        Assert.Null(RunnerService.ServiceStartProblem(Service(root, "Stopped", "Auto"), new List<ProcessInfo>(), new List<string>(), new[] { root }, "Start"));
        Assert.True(RunnerService.StartsFromService(stopped));
    }

    // ----- What is not reported -----

    [Fact]
    public async Task AHealthyServiceRunner_AndAHealthyConsoleRunner_AreNotReported()
    {
        // A service runner whose listener runs in session 0, and whose work folder belongs to its own account.
        var service = MakeRunner("service-runner");
        var account = WindowsIdentity.GetCurrent().Name;
        var healthy = await Read(service, new List<RunnerServiceRecord> { Service(service, "Running", "Auto", account, "actions.runner.Sandiozo-api.service-runner") },
            new List<ProcessInfo> { Listener(service, 4200, 0) }, new List<string>());
        Assert.False(healthy.RunsTwice);
        Assert.False(healthy.ConsoleAtSignIn);
        Assert.Equal(string.Empty, healthy.WorkFolderOwner);
        Assert.Equal(RunnerStart.WithWindows, healthy.StartsAt);

        // A console runner without a service, opened at sign-in.
        var console = MakeRunner("console-runner");
        var consoleRunner = await Read(console, new List<RunnerServiceRecord>(), new List<ProcessInfo> { Listener(console, 9100, 1) }, StartupShortcut(console));
        Assert.True(consoleRunner.IsInteractive);
        Assert.False(consoleRunner.RunsTwice);
        Assert.Equal(string.Empty, consoleRunner.WorkFolderOwner);

        // A console runner with an unused, stopped service: it runs once, from the console window.
        var withService = MakeRunner("console-with-service");
        var stoppedService = Service(withService, "Stopped");
        var consoleProcesses = new List<ProcessInfo> { Listener(withService, 9200, 1) };
        var unused = await Read(withService, new List<RunnerServiceRecord> { stoppedService }, consoleProcesses, StartupShortcut(withService));
        Assert.True(unused.IsInteractive);
        Assert.False(unused.RunsTwice);
        Assert.Equal(string.Empty, unused.WorkFolderOwner);
        Assert.Contains("console window", RunnerService.ServiceStartProblem(stoppedService, consoleProcesses, StartupShortcut(withService), new[] { withService }, "Start"));

        var pc = Pc(Status(healthy), Status(consoleRunner), Status(unused));
        Assert.All(pc.Runners, r => Assert.False(r.Health.IsProblem));
        Assert.Equal(0, FleetAttention.Count(new[] { pc }).Total);
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { pc }, new HashSet<string>()));
        Assert.True(pc.Runners.Single(r => r.ServiceName == "actions.runner.Sandiozo-api.service-runner").CanRestart);
    }

    [Fact]
    public void RunsTwice_NeedsTheServiceRunning_AndAConsoleCopy()
    {
        Assert.True(RunnerService.RunsTwice(serviceRunning: true, listenerInSession: true, consoleAtSignIn: false));
        Assert.True(RunnerService.RunsTwice(serviceRunning: true, listenerInSession: false, consoleAtSignIn: true));
        Assert.False(RunnerService.RunsTwice(serviceRunning: true, listenerInSession: false, consoleAtSignIn: false));
        Assert.False(RunnerService.RunsTwice(serviceRunning: false, listenerInSession: true, consoleAtSignIn: true));
    }

    [Fact]
    public async Task AConsoleCopyWithoutAStartupEntry_IsSeenToo_AndIsToldToCloseTheWindow()
    {
        var root = MakeRunner(workFolder: false);
        var processes = new List<ProcessInfo> { Listener(root, 4200, 0), Listener(root, 9100, 2) };
        var runner = await Read(root, new List<RunnerServiceRecord> { Service(root, "Running", "Auto") }, processes, new List<string>());

        Assert.True(runner.RunsTwice);
        Assert.False(runner.ConsoleAtSignIn);
        var health = Status(runner).Health;
        Assert.Contains("a console window runs it too", health.Detail);
        Assert.Contains("close the console window to keep the service", health.Detail);
        Assert.DoesNotContain("Start when you sign in", health.Detail);
    }

    [Fact]
    public void ARunnerThatRunsTwice_IsReportedWhenItBegins_AndAgainOnlyAfterItWasFixed()
    {
        var twice = new MachineRunnerStatus { ServiceName = ServiceName, DisplayName = "livonia-backup", ServiceInstalled = true, IsRunning = true,
                                              ActivityText = "Idle", RunsTwice = true, ConsoleAtSignIn = true, StartsAt = RunnerStart.SignIn };
        var fixedRunner = new MachineRunnerStatus { ServiceName = ServiceName, DisplayName = "livonia-backup", ServiceInstalled = true, IsRunning = true, ActivityText = "Idle" };
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Single(MainWindow.NewProblemAlerts(new[] { Pc(twice) }, reported));
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(twice) }, reported));
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(fixedRunner) }, reported));
        Assert.Single(MainWindow.NewProblemAlerts(new[] { Pc(twice) }, reported));
        // A concern the dashboard's check found earlier is not kept on the card: the card takes it from its status.
        var earlier = JobHealth.ForSetup(twice);
        Assert.Equal(RunnerHealth.Healthy, MainWindow.StillApplies(earlier, fixedRunner));
    }

    [Fact]
    public void ARunnerStoppedWithStop_DoesNotNeedAttention_ForItsWorkFolder()
    {
        var running = new MachineRunnerStatus { ServiceName = ServiceName, DisplayName = "api", ServiceInstalled = true, IsRunning = true,
                                                ActivityText = "Idle", ServiceAccount = @"NT AUTHORITY\NETWORK SERVICE", WorkFolderOwner = @"SANDIS\sandis" };
        var stopped = new MachineRunnerStatus { ServiceName = ServiceName, DisplayName = "api", ServiceInstalled = true, IsRunning = false,
                                                ManuallyStopped = true, ServiceAccount = @"NT AUTHORITY\NETWORK SERVICE", WorkFolderOwner = @"SANDIS\sandis" };

        Assert.Equal(RunnerConcern.WorkFolderOwner, running.Health.Concern);
        Assert.Contains(@"belong to SANDIS\sandis", running.Health.Detail);
        Assert.Contains("Clean work folder", running.Health.Detail);
        var attention = FleetAttention.Count(new[] { Pc(running) });
        Assert.Equal(1, attention.FolderOwners);
        Assert.Contains("1 work folder of another account", attention.SubText);
        Assert.Equal(0, FleetAttention.Count(new[] { Pc(stopped) }).Total);
        // The details still say it.
        Assert.Contains(@"belong to SANDIS\sandis, not to NT AUTHORITY\NETWORK SERVICE", stopped.WorkFolderText);
    }

    // ----- The owner's console runners: a Windows service that is installed but not used -----

    private const string CoreService = "actions.runner.Sandiozo-project-Livonia.livonia-core";

    /// <summary>The five runner folders on one of the owner's PCs: the first name is the start of the other four.</summary>
    private string[] OwnersRunnerFolders()
        => new[] { "project-Livonia", "project-Livonia-2", "project-Livonia-3", "project-Livonia-4", "project-Livonia-5" }
            .Select(name => MakeRunner(name)).ToArray();

    private static Task<RunnerInfo> ReadOnPc(string root, RunnerServiceRecord service, List<ProcessInfo> processes, List<string> startup, string[] roots)
        => RunnerService.BuildRunnerInfoAsync(root, new List<RunnerServiceRecord> { service }, processes, queryGitHub: false, startup, roots);

    [Theory]
    [InlineData("Manual")]
    [InlineData("Disabled")]
    public async Task AConsoleRunnerWhoseWindowIsClosed_DoesNotWarnAboutItsWorkFolder(string startMode)
    {
        // livonia-core as it is now: its service stopped, the shortcut turned on, no Runner.Listener.exe. The folders in
        // _work belong to the user, which is right for a runner that runs in a console window.
        var roots = OwnersRunnerFolders();
        var core = roots[1];
        var runner = await ReadOnPc(core, Service(core, "Stopped", startMode, name: CoreService), new List<ProcessInfo>(), StartupShortcut(core), roots);

        Assert.True(runner.ConsoleAtSignIn);
        Assert.False(runner.IsInteractive);
        Assert.False(runner.RunsTwice);
        Assert.Equal(string.Empty, runner.WorkFolderOwner);
        var status = Status(runner);
        Assert.DoesNotContain("belong to", status.WorkFolderText);
        Assert.NotEqual(Palette.Warning, status.WorkFolderBrush);
        Assert.False(status.Health.IsProblem);

        // With "Start when you sign in" turned off, the service is what runs it, and the details say whose the folders are.
        var serviceOnly = await ReadOnPc(core, Service(core, "Stopped", startMode, name: CoreService), new List<ProcessInfo>(), new List<string>(), roots);
        Assert.Equal(OwnerOf(Path.Combine(core, "_work", "project-Livonia")), serviceOnly.WorkFolderOwner);
        Assert.Contains("belong to", Status(serviceOnly).WorkFolderText);
        // The shortcut of project-Livonia-2 does not open project-Livonia, whose name is the start of it.
        var first = await ReadOnPc(roots[0], Service(roots[0], "Stopped", startMode, name: "actions.runner.Sandiozo-project-Livonia.livonia"),
            new List<ProcessInfo>(), StartupShortcut(core), roots);
        Assert.False(first.ConsoleAtSignIn);
        Assert.NotEqual(string.Empty, first.WorkFolderOwner);
    }

    [Fact]
    public void AnEditLeavesTheWorkFolderToTheConsoleWindow_AndAConversionGivesIt()
    {
        var roots = OwnersRunnerFolders();
        var core = roots[1];
        var none = new List<ProcessInfo>();
        // There is something to give: the folders are this test's user's, and the service runs as NETWORK SERVICE.
        Assert.Equal(Path.Combine(core, "_work"), RunnerService.WorkFolderToGive(core, "_work"));

        // An edit of livonia-core as it is now: no console window, but the shortcut opens one at the next sign-in, and
        // the edit leaves it on. The runner stays a console runner, so its folders stay the user's.
        var kept = RunnerService.WorkFolderKeptReason(core, none, StartupShortcut(core), roots);
        Assert.NotNull(kept);
        Assert.Contains("a startup entry opens this runner in a console window when you sign in", kept);
        // An edit while the service runs and a console window runs the runner too: the edit does not close that window.
        Assert.Contains("a console window runs this runner",
            RunnerService.WorkFolderKeptReason(core, new List<ProcessInfo> { Listener(core, 4200, 0), Listener(core, 9100, 1) }, new List<string>(), roots));
        // A startup entry that also starts another runner opens it too, and is left in place.
        var shared = new List<string> { $"start \"\" /d \"{core}\" run.cmd\r\nstart \"\" /d \"{roots[2]}\" run.cmd\r\n" };
        Assert.NotNull(RunnerService.WorkFolderKeptReason(core, none, shared, roots));

        // Run as Windows service, Repair Windows service, and an edit of a runner whose console window ran: the window
        // was closed and the shortcut moved aside first, so nothing opens the runner in a console window, and the
        // service runs it from now on. The folder is given.
        Assert.Null(RunnerService.WorkFolderKeptReason(core, none, new List<string>(), roots));
        // The service's own listener runs in session 0: it is not a console window.
        Assert.Null(RunnerService.WorkFolderKeptReason(core, new List<ProcessInfo> { Listener(core, 4200, 0) }, new List<string>(), roots));
        // The shortcut of project-Livonia-2 keeps nothing of project-Livonia.
        Assert.Null(RunnerService.WorkFolderKeptReason(roots[0], none, StartupShortcut(core), roots));
    }

    [Fact]
    public async Task AnEdit_KeepsADisabledServiceDisabled_AndLeavesTheMovedRunnerAsBefore()
    {
        var root = MakeRunner("project-Livonia-2");
        Task<RunnerInfo> With(string startMode)
            => Read(root, new List<RunnerServiceRecord> { Service(root, "Stopped", startMode, name: CoreService) }, new List<ProcessInfo>(), StartupShortcut(root));

        // Disabled by hand, the console window closed: config.cmd installs the service again to start with Windows, so
        // it is disabled again afterwards (in the rollback of a failed edit too), and it is never set to start with Windows.
        var disabled = await With("Disabled");
        Assert.True(RunnerService.KeepsServiceDisabled(disabled));
        Assert.Null(RunnerService.DisabledServiceEditProblem(disabled));
        // A service that starts manually or with Windows keeps that, as before.
        var manual = await With("Manual");
        Assert.False(RunnerService.KeepsServiceDisabled(manual));
        Assert.True(manual.RegistrationOptions.ManualStart);
        var automatic = await Read(root, new List<RunnerServiceRecord> { Service(root, "Stopped", "Auto", name: CoreService) }, new List<ProcessInfo>(), new List<string>());
        Assert.False(RunnerService.KeepsServiceDisabled(automatic));
        Assert.False(automatic.RegistrationOptions.ManualStart);

        // The old copy of a moved runner: the move disabled its service, and the marker says how it started before.
        var marker = Path.Combine(root, RunnerService.MovedAwayMarker);
        File.WriteAllText(marker, "Moved to another PC.\r\nstart=manual");
        var movedManual = await With("Disabled");
        Assert.True(movedManual.MovedAway);
        Assert.False(RunnerService.KeepsServiceDisabled(movedManual));
        Assert.True(movedManual.RegistrationOptions.ManualStart);
        File.WriteAllText(marker, "Moved to another PC.");
        var movedAutomatic = await With("Disabled");
        Assert.False(RunnerService.KeepsServiceDisabled(movedAutomatic));
        Assert.False(movedAutomatic.RegistrationOptions.ManualStart);
    }

    [Fact]
    public async Task AnEditOfARunnerWhoseConsoleWindowRuns_IsRefused_WhenItsServiceIsDisabled()
    {
        var root = MakeRunner("project-Livonia-3");
        var console = new List<ProcessInfo> { Listener(root, 9100, 1) };
        var disabled = await Read(root, new List<RunnerServiceRecord> { Service(root, "Stopped", "Disabled") }, console, StartupShortcut(root));
        Assert.True(disabled.IsInteractive);
        var refused = RunnerService.DisabledServiceEditProblem(disabled);
        Assert.NotNull(refused);
        Assert.Contains("nothing was changed", refused);

        // A service that starts manually: the edit closes the console window and lets the service run the runner, as before.
        var other = MakeRunner("project-Livonia-4");
        var manual = await Read(other, new List<RunnerServiceRecord> { Service(other, "Stopped", "Manual") },
            new List<ProcessInfo> { Listener(other, 9200, 1) }, StartupShortcut(other));
        Assert.True(manual.IsInteractive);
        Assert.Null(RunnerService.DisabledServiceEditProblem(manual));
    }

    // ----- A failed move puts the Windows service back as it was -----

    [Theory]
    // The owner's console runners: the service disabled by hand, and the console window open, closed (a startup entry
    // opens it at sign-in), or not used at all.
    [InlineData("Disabled", "open", ServiceStart.Disabled, false)]
    [InlineData("Disabled", "at sign-in", ServiceStart.Disabled, false)]
    [InlineData("Disabled", "none", ServiceStart.Disabled, false)]
    // A service runner that starts manually or with Windows.
    [InlineData("Manual", "none", ServiceStart.Manual, true)]
    [InlineData("Auto", "none", ServiceStart.Automatic, true)]
    // A console runner whose service is installed, stopped and not disabled.
    [InlineData("Manual", "open", ServiceStart.Manual, false)]
    [InlineData("Auto", "at sign-in", ServiceStart.Automatic, false)]
    public async Task AFailedMove_PutsTheServiceBackAsItWas_AndStartsItOnlyWhereItRanTheRunner(string startMode, string console, string putBack, bool started)
    {
        var root = MakeRunner("project-Livonia-3");
        var startup = console == "none" ? new List<string>() : StartupShortcut(root);
        var processes = console == "open" ? new List<ProcessInfo> { Listener(root, 9100, 1) } : new List<ProcessInfo>();
        var state = startMode != "Disabled" && console == "none" ? "Running" : "Stopped";
        var before = await Read(root, new List<RunnerServiceRecord> { Service(root, state, startMode, name: CoreService) }, processes, startup);
        Assert.False(before.RunsTwice);

        // Step 1 records how the runner ran in the marker, closes the console window and disables the service.
        var recorded = FleetActions.BeforeMove(before);
        var marker = Path.Combine(root, RunnerService.MovedAwayMarker);
        File.WriteAllText(marker, RunnerService.MovedAwayText(DateTime.UtcNow, recorded.ManualStart, recorded.ServiceStart, recorded.Console));
        var moving = await Read(root, new List<RunnerServiceRecord> { Service(root, "Stopped", "Disabled", name: CoreService) }, new List<ProcessInfo>(), startup);
        Assert.True(moving.MovedAway);
        // A second try (after a move that got no answer) records the same, not that the move disabled the service.
        Assert.Equal(recorded, FleetActions.BeforeMove(moving));
        // The new copy on the other PC is registered as on the first try.
        Assert.Equal(before.RegistrationOptions, moving.RegistrationOptions);

        // The move fails: the service starts as it did before, and is started only where it ran the runner.
        var undo = FleetActions.AfterFailedMove(moving);
        Assert.Equal(putBack, FleetActions.StartMode(undo.ServiceStart));
        Assert.Equal(started, undo.Start);
        Assert.StartsWith(started ? "The runner was started again." : "The runner is as it was before the move.", undo.Text);
        if (console != "none") Assert.Contains("console window", undo.Text);
        else if (!started) Assert.Contains("disabled, as before, and was not started", undo.Text);
    }

    [Fact]
    public void TheMoveMarker_IsStillReadBy290_AndMarkersOf290AreReadAsBefore()
    {
        // 2.9.0 reads only whether a line is "start=manual": the service then starts manually after a failed move, and
        // with Windows otherwise. That line is written as before.
        static bool ManualFor290(string text) => text.Split('\n').Any(l => l.Trim() == "start=manual");
        var now = DateTime.UtcNow;
        var manual = RunnerService.MovedAwayText(now, manualStart: true, RunnerStart.Off, console: false);
        Assert.StartsWith("Moved to another PC on ", manual);
        Assert.True(ManualFor290(manual));
        Assert.False(ManualFor290(RunnerService.MovedAwayText(now, manualStart: false, RunnerStart.WithWindows, console: false)));
        Assert.False(ManualFor290(RunnerService.MovedAwayText(now, manualStart: false, RunnerStart.Disabled, console: true)));

        // What this version writes, it reads back.
        foreach (var start in new[] { RunnerStart.WithWindows, RunnerStart.Off, RunnerStart.Disabled })
        foreach (var console in new[] { false, true })
            Assert.Equal((start == RunnerStart.Off, start, console),
                RunnerService.ParseMovedAway(RunnerService.MovedAwayText(now, start == RunnerStart.Off, start, console).Split(Environment.NewLine)));
        // A marker of 2.9.0 has no service line: the service is put back as 2.9.0 put it back.
        Assert.Equal((true, RunnerStart.Off, false), RunnerService.ParseMovedAway(new[] { "Moved to another PC on 2026-11-01T10:00:00Z.", "start=manual" }));
        Assert.Equal((false, RunnerStart.WithWindows, false), RunnerService.ParseMovedAway(new[] { "Moved to another PC on 2026-11-01T10:00:00Z." }));
        // Without a marker (none could be read), as before: with Windows, and started.
        Assert.Equal((RunnerStart.WithWindows, true), (FleetActions.AfterFailedMove(null).ServiceStart, FleetActions.AfterFailedMove(null).Start));
        Assert.Equal(ServiceStart.Disabled, FleetActions.StartMode(RunnerStart.Disabled));
        Assert.Equal(ServiceStart.Manual, FleetActions.StartMode(RunnerStart.Off));
        Assert.Equal(ServiceStart.Automatic, FleetActions.StartMode(RunnerStart.WithWindows));
    }

    [Fact]
    public void AFailedMoveOfARunnerInAConsoleWindow_IsUndoneOnlyByAPcThatDoesNotStartItsService()
    {
        var console = new MachineRunnerStatus { ServiceName = CoreService, ServiceInstalled = true, IsInteractive = true };
        var service = new MachineRunnerStatus { ServiceName = CoreService, ServiceInstalled = true, IsRunning = true };
        MachineInfo Source(string version) => MachineInfo.FromPayload("sandis", new MachineStatusPayload { MachineName = "SANDIS", AppVersion = version });

        Assert.True(FleetActions.UndoesFailedMove(service, Source("2.9.0"), runsHere: false));
        Assert.False(FleetActions.UndoesFailedMove(console, Source("2.9.0"), runsHere: false));
        Assert.False(FleetActions.UndoesFailedMove(console, Source(""), runsHere: false));
        Assert.True(FleetActions.UndoesFailedMove(console, Source("2.10.0"), runsHere: false));
        Assert.True(FleetActions.UndoesFailedMove(console, Source("2.11.1"), runsHere: false));
        Assert.True(FleetActions.UndoesFailedMove(console, Source("2.9.0"), runsHere: true));
    }

    // ----- Which concern a runner shows -----

    private static RunnerHealth StuckJob(string subject = "job-1")
        => JobHealth.ForBusyRunner(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-40), null, TimeSpan.FromMinutes(15), subject, DateTime.UtcNow);

    /// <summary>A running service runner whose work folder has folders of the console window's user.</summary>
    private static MachineRunnerStatus OwnerRunner(bool busy = true, bool runsTwice = false, string name = "livonia-backup")
        => new()
        {
            ServiceName = $"actions.runner.Sandiozo-project-Livonia.{name}", DisplayName = name, AgentName = name, ServiceInstalled = true,
            IsRunning = true, ActivityText = busy ? "Busy" : "Idle", ServiceAccount = @"NT AUTHORITY\NETWORK SERVICE",
            WorkFolderOwner = runsTwice ? string.Empty : @"SANDIS\sandis", RunsTwice = runsTwice, ConsoleAtSignIn = runsTwice,
            StartsAt = runsTwice ? RunnerStart.SignIn : RunnerStart.WithWindows, JobStartedUtc = busy ? DateTime.UtcNow.AddMinutes(-40) : null,
            QuietSinceUtc = busy ? DateTime.UtcNow.AddMinutes(-30) : null, IsLocalMachine = true, MachineOnline = true,
            AgentProtocolVersion = MachineRelay.ProtocolVersion
        };

    [Fact]
    public void AJobConcern_IsShownBeforeTheWorkFolderOwner_WithTheStopJobButton()
    {
        var runner = OwnerRunner();
        var pc = Pc(runner);
        Assert.Equal(RunnerConcern.WorkFolderOwner, runner.Health.Concern);
        Assert.Equal(System.Windows.Visibility.Collapsed, runner.StopJobVisibility);

        // The dashboard's check finds the job stuck: that is shown, with its button, and counted as a stuck job.
        runner.Health = StuckJob();
        Assert.Equal(RunnerConcern.Stuck, runner.Health.Concern);
        Assert.Equal("Stuck", runner.StatusText);
        Assert.Equal(System.Windows.Visibility.Visible, runner.StopJobVisibility);
        Assert.True(runner.CanStopJob);
        var attention = FleetAttention.Count(new[] { pc });
        Assert.Equal(1, attention.StuckJobs);
        Assert.Equal(0, attention.FolderOwners);
        // A job that runs long, and jobs that wait while it idles, come first too.
        runner.Health = JobHealth.ForBusyRunner(null, DateTime.UtcNow.AddHours(-3), null, TimeSpan.FromMinutes(15), "job-1", DateTime.UtcNow);
        Assert.Equal(RunnerConcern.RunningLong, runner.Health.Concern);
        Assert.Equal(System.Windows.Visibility.Visible, runner.StopJobVisibility);
        runner.Health = new RunnerHealth(RunnerConcern.IdleWithWaitingJobs, "Idle · jobs waiting", "A job it can run has waited 6 min.", "waiting|1");
        Assert.Equal(RunnerConcern.IdleWithWaitingJobs, runner.Health.Concern);

        // Without a job concern, the work folder owner is shown again.
        runner.Health = RunnerHealth.Healthy;
        Assert.Equal(RunnerConcern.WorkFolderOwner, runner.Health.Concern);
        Assert.Equal(1, FleetAttention.Count(new[] { pc }).FolderOwners);

        // A runner that runs twice shows that before anything else, whatever its job does.
        var twice = OwnerRunner(runsTwice: true);
        twice.Health = StuckJob();
        Assert.Equal(RunnerConcern.RunsTwice, twice.Health.Concern);
        Assert.Equal(System.Windows.Visibility.Collapsed, twice.StopJobVisibility);
    }

    [Fact]
    public async Task TheJobCheck_AndTheAiTools_PutAJobConcernBeforeTheWorkFolderOwner_AndRunsTwiceBeforeBoth()
    {
        var stuck = OwnerRunner(name: "stuck");
        var idle = OwnerRunner(busy: false, name: "idle");
        var twice = OwnerRunner(runsTwice: true, name: "twice");
        var pc = Pc(stuck, idle, twice);

        var health = await JobHealth.EvaluateAsync(new[] { pc }, TimeSpan.FromMinutes(15), null, useGitHub: false, DateTime.UtcNow);

        Assert.Equal(RunnerConcern.Stuck, health.Runners[JobHealth.Key(stuck)].Concern);
        Assert.Equal(RunnerConcern.WorkFolderOwner, health.Runners[JobHealth.Key(idle)].Concern);
        Assert.Equal(RunnerConcern.RunsTwice, health.Runners[JobHealth.Key(twice)].Concern);
        // What the AI tools list as the runner's problem.
        Assert.Contains("\"kind\":\"Stuck\"", JsonSerializer.Serialize(AssistantTools.DescribeRunner(pc, stuck, online: true, health)));
        Assert.Contains("\"kind\":\"Work folder owner\"", JsonSerializer.Serialize(AssistantTools.DescribeRunner(pc, idle, online: true, health)));
        // The card shows what the check found, as the dashboard applies it.
        stuck.Health = MainWindow.StillApplies(health.Runners[JobHealth.Key(stuck)], stuck);
        Assert.Equal(RunnerConcern.Stuck, stuck.Health.Concern);
        idle.Health = MainWindow.StillApplies(health.Runners[JobHealth.Key(idle)], idle);
        Assert.Equal(RunnerConcern.WorkFolderOwner, idle.Health.Concern);
    }

    [Fact]
    public void TheWorkFolderOwner_IsReportedOnce_WhileJobConcernsComeAndGo()
    {
        var runner = OwnerRunner();
        var pc = Pc(runner);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Work folder owner", Assert.Single(MainWindow.NewProblemAlerts(new[] { pc }, reported)));
        // A stuck job hides it for a while, and is reported itself.
        runner.Health = StuckJob();
        Assert.Contains("Stuck", Assert.Single(MainWindow.NewProblemAlerts(new[] { pc }, reported)));
        // The job goes on: the work folder owner is shown again, but not reported again.
        runner.Health = RunnerHealth.Healthy;
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { pc }, reported));
        runner.Health = StuckJob("job-2");
        Assert.Contains("Stuck", Assert.Single(MainWindow.NewProblemAlerts(new[] { pc }, reported)));
        runner.Health = RunnerHealth.Healthy;
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { pc }, reported));

        // Fixed with Clean work folder: it is forgotten, and reported again only when it comes back.
        var cleaned = new MachineRunnerStatus
        {
            ServiceName = runner.ServiceName, DisplayName = runner.DisplayName, AgentName = runner.AgentName, ServiceInstalled = true,
            IsRunning = true, ActivityText = "Idle", ServiceAccount = runner.ServiceAccount
        };
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(cleaned) }, reported));
        Assert.Single(MainWindow.NewProblemAlerts(new[] { Pc(OwnerRunner(busy: false)) }, reported));
    }

    // ----- What counts as a startup entry of a runner -----

    [Theory]
    // A cleanup task for the runner's logs, a script run out of a checkout, the work folder as a working directory.
    [InlineData(@"powershell.exe -NoProfile -Command Remove-Item C:\actions-runner\_diag\*.log -Force")]
    [InlineData(@"cmd.exe /c C:\actions-runner\_work\project-Livonia\project-Livonia\tools\nightly.cmd C:\actions-runner\_work\project-Livonia")]
    [InlineData(@"C:\Tools\clean.cmd  C:\actions-runner\_work")]
    [InlineData("forfiles /p \"C:\\actions-runner\\_diag\" /m *.log /d -7 /c \"cmd /c del @path\"")]
    [InlineData(@"robocopy C:/actions-runner/_DIAG D:\backup\diag /MIR")]
    public void AnEntryThatOnlyNamesTheWorkOrLogFolder_DoesNotStartTheRunner(string entry)
        => Assert.Equal(RunnerService.StartupMatch.None, RunnerService.MatchStartupEntry(entry, @"C:\actions-runner", new[] { @"C:\actions-runner-2" }));

    [Theory]
    [InlineData(@"cmd.exe /c C:\actions-runner\run.cmd")]
    [InlineData(@"C:\Windows\System32\conhost.exe --headless cmd.exe /d /c ""C:\actions-runner\run-in-session.cmd""")]
    // A scheduled task's action: run.cmd, with the runner folder as its working directory.
    [InlineData(@"cmd.exe /c run.cmd C:\actions-runner")]
    [InlineData(@"C:\actions-runner\bin\Runner.Listener.exe run --startuptype service")]
    // A folder that only starts like the work or log folder is not one of them.
    [InlineData(@"C:\actions-runner\_work-tools\start.cmd")]
    [InlineData(@"C:\actions-runner\_diag.cmd")]
    // run.cmd, together with a cleanup of the logs.
    [InlineData(@"cmd.exe /c ""del /q C:\actions-runner\_diag\*.log & C:\actions-runner\run.cmd""")]
    public void AnEntryThatRunsTheRunner_StillStartsIt(string entry)
        => Assert.Equal(RunnerService.StartupMatch.ThisRunner, RunnerService.MatchStartupEntry(entry, @"C:\actions-runner", new[] { @"C:\actions-runner-2" }));

    [Fact]
    public async Task AHealthyServiceRunner_WithACleanupTaskForItsLogs_DoesNotRunTwice()
    {
        var root = MakeRunner("service-runner", workFolder: false);
        var cleanup = new List<string> { $@"powershell.exe -NoProfile -Command Remove-Item {root}\_diag\*.log  {root}\_work" };
        var service = Service(root, "Running", "Auto", name: "actions.runner.Sandiozo-api.service-runner");
        var runner = await Read(root, new List<RunnerServiceRecord> { service }, new List<ProcessInfo> { Listener(root, 4200, 0) }, cleanup);

        Assert.False(runner.ConsoleAtSignIn);
        Assert.False(runner.RunsTwice);
        Assert.Equal(RunnerStart.WithWindows, runner.StartsAt);
        Assert.True(RunnerService.StartsFromService(runner));
        Assert.Null(RunnerService.ServiceStartProblem(service, new List<ProcessInfo> { Listener(root, 4200, 0) }, cleanup, new[] { root }, "Restart"));
        Assert.True(Status(runner).CanRestart);
        // Nested runners: a mention of the inner runner's work folder belongs to neither of them.
        Assert.Equal(RunnerService.StartupMatch.None,
            RunnerService.MatchStartupEntry(@"del D:\Git-Runners\Study-Tool\_work\x.log", @"D:\Git-Runners", new[] { @"D:\Git-Runners\Study-Tool" }));
        Assert.Equal(RunnerService.StartupMatch.None,
            RunnerService.MatchStartupEntry(@"del D:\Git-Runners\Study-Tool\_work\x.log", @"D:\Git-Runners\Study-Tool", new[] { @"D:\Git-Runners" }));
    }

    // ----- Two console windows of one runner -----

    /// <summary>A listener of the runner in this folder, in a session, started at <paramref name="startedUtc"/> (null: not read).</summary>
    private static ProcessInfo StartedListener(string root, uint id, uint session, DateTime? startedUtc)
        => new() { ProcessId = id, ParentProcessId = 9000u + session, Name = "Runner.Listener.exe",
                   ExecutablePath = Path.Combine(root, "bin", "Runner.Listener.exe"), SessionId = session, StartedUtc = startedUtc };

    /// <summary>A console runner as its PC reports it, with or without a second console window.</summary>
    private static MachineRunnerStatus ConsoleRunner(string root, bool twoConsoles, bool busy = false) => new()
    {
        InstallPath = root, DisplayName = Path.GetFileName(root), AgentName = Path.GetFileName(root), IsInteractive = true, IsRunning = true,
        ActivityText = busy ? "Busy" : "Idle", StartsAt = RunnerStart.SignIn, TwoConsoleWindows = twoConsoles,
        JobStartedUtc = busy ? DateTime.UtcNow.AddMinutes(-40) : null, QuietSinceUtc = busy ? DateTime.UtcNow.AddMinutes(-30) : null,
        MachineName = "SANDIS", MachineKey = "sandis", IsLocalMachine = true, MachineOnline = true, AgentProtocolVersion = MachineRelay.ProtocolVersion
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASecondConsoleWindow_IsShownAndReportedOnce_AndChangesNoAction(bool disabledService)
    {
        // The incident: the startup shortcut opened the runner in a second Windows session too. The listener there tries to
        // connect for a few minutes, exits, and is started again. The owner's console runners have a disabled service.
        var root = MakeRunner("livonia-core");
        var services = disabledService ? new List<RunnerServiceRecord> { Service(root, "Stopped", "Disabled", name: CoreService) } : new List<RunnerServiceRecord>();
        var startup = StartupShortcut(root);
        var now = DateTime.UtcNow;
        var oneWindow = new List<ProcessInfo> { StartedListener(root, 9100, 1, now.AddHours(-5)) };
        var twoWindows = new List<ProcessInfo> { oneWindow[0], StartedListener(root, 9300, 2, now.AddMinutes(-3)) };
        var once = await Read(root, services, oneWindow, startup);
        var twice = await Read(root, services, twoWindows, startup);

        Assert.False(once.TwoConsoleWindows);
        Assert.True(twice.TwoConsoleWindows);
        Assert.False(twice.RunsTwice);
        Assert.True(twice.IsInteractive);
        Assert.True(twice.IsRunning);

        // Shown: the card, the Needs attention tile, one alert, the fleet status and the AI tools.
        var status = Status(twice);
        Assert.Equal(RunnerConcern.TwoConsoleWindows, status.Health.Concern);
        Assert.Equal("Two console windows", status.StatusText);
        Assert.StartsWith("livonia-core, Two console windows.", status.HeaderName);
        Assert.Contains("A second console window runs the same runner", status.Health.Detail);
        Assert.Contains("only one of them gets jobs", status.Health.Detail);
        Assert.Contains("Close one of the console windows, or sign out of the other Windows session.", status.Health.Detail);
        Assert.Equal(status.Health.Detail, status.StatusTip);
        var pc = Pc(status);
        var attention = FleetAttention.Count(new[] { pc });
        Assert.Equal(1, attention.TwoConsoles);
        Assert.Equal(1, attention.Total);
        Assert.Contains("1 runner runs in two console windows", attention.Summary);
        Assert.Contains("1 in two console windows", attention.SubText);
        Assert.True(FleetAttention.ShowsRunner(pc, pc.Runners[0]));
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("SANDIS: livonia-core — Two console windows. A second console window",
            Assert.Single(MainWindow.NewProblemAlerts(new[] { pc }, reported)));
        Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(Status(twice)) }, reported));
        var json = JsonSerializer.Serialize(new MachineStatusPayload { Runners = new[] { status } });
        Assert.Contains("\"TwoConsoleWindows\":true", json);
        Assert.True(JsonSerializer.Deserialize<MachineStatusPayload>(json)!.Runners[0].TwoConsoleWindows);
        var health = await JobHealth.EvaluateAsync(new[] { pc }, TimeSpan.FromMinutes(15), null, useGitHub: false, DateTime.UtcNow);
        Assert.Contains("\"kind\":\"Two console windows\"", JsonSerializer.Serialize(AssistantTools.DescribeRunner(pc, pc.Runners[0], online: true, health)));
        Assert.Equal(RunnerHealth.Healthy, MainWindow.StillApplies(health.Runners[JobHealth.Key(status)], status));

        // Only shown: what can be done with the runner, and auto-recovery, are as with one console window.
        var single = Status(once);
        Assert.False(single.Health.IsProblem);
        Assert.Equal(once.HasConsoleCopy, twice.HasConsoleCopy);
        Assert.Equal(once.StartsAt, twice.StartsAt);
        Assert.Equal(once.RegistrationOptions, twice.RegistrationOptions);
        Assert.Equal(RunnerService.StartsFromService(once), RunnerService.StartsFromService(twice));
        Assert.Equal((single.CanControl, single.CanPower, single.CanRestart, single.CanEdit, single.CanCleanWork, single.CanMove, single.CanChangeStartup, single.PowerVisibility),
            (status.CanControl, status.CanPower, status.CanRestart, status.CanEdit, status.CanCleanWork, status.CanMove, status.CanChangeStartup, status.PowerVisibility));
        foreach (var action in new[] { "Start", "Stop", "Restart" })
        {
            Assert.Equal(FleetActions.ConsoleRunnerProblem(once.IsInteractive, once.ServiceInstalled, action, once.ConsoleAtSignIn, once.RunsTwice),
                FleetActions.ConsoleRunnerProblem(twice.IsInteractive, twice.ServiceInstalled, action, twice.ConsoleAtSignIn, twice.RunsTwice));
            Assert.Equal(Record.Exception(() => AssistantTools.RequireService(Pc(single), single, action))?.Message,
                Record.Exception(() => AssistantTools.RequireService(pc, status, action))?.Message);
            if (disabledService)
                Assert.Equal(RunnerService.ServiceStartProblem(services[0], oneWindow, startup, new[] { root }, action),
                    RunnerService.ServiceStartProblem(services[0], twoWindows, startup, new[] { root }, action));
        }
        var recovery = new RunnerRecovery();
        for (var i = 0; i < 4; i++)
            Assert.Equal(RunnerRecovery.Step.None, MachineRelay.CheckRecovery(recovery, i % 2 == 0 ? once : twice, watchdogOn: true, held: false, now.AddSeconds(30 * i)));
    }

    [Fact]
    public void AShortOverlap_WhenARunnerRestarts_IsNotTwoConsoleWindows()
    {
        var root = MakeRunner("restarts", workFolder: false);
        var now = DateTime.UtcNow;
        var old = StartedListener(root, 9100, 1, now.AddHours(-5));
        var restarted = StartedListener(root, 9300, 1, now);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The old listener and the new one side by side for a minute, read every 10 seconds, then the new one alone.
        for (var s = 0; s <= 600; s += 10)
        {
            var two = RunnerService.InTwoConsoleWindows(root, s <= 60 ? new List<ProcessInfo> { old, restarted } : new List<ProcessInfo> { restarted }, now.AddSeconds(s));
            Assert.False(two);
            Assert.Empty(MainWindow.NewProblemAlerts(new[] { Pc(ConsoleRunner(root, two)) }, reported));
        }
        Assert.Equal(0, FleetAttention.Count(new[] { Pc(ConsoleRunner(root, twoConsoles: false)) }).Total);

        // A listener whose start could not be read counts as just started. The service's listener in session 0 and the
        // listener of another runner are not a second console window.
        var later = now.AddHours(1);
        Assert.False(RunnerService.InTwoConsoleWindows(root, new List<ProcessInfo> { old, StartedListener(root, 9400, 2, null) }, later));
        Assert.False(RunnerService.InTwoConsoleWindows(root, new List<ProcessInfo> { old, StartedListener(root, 4200, 0, now.AddHours(-5)) }, later));
        var other = MakeRunner("restarts-2", workFolder: false);
        Assert.False(RunnerService.InTwoConsoleWindows(root, new List<ProcessInfo> { old, StartedListener(other, 9500, 2, now.AddHours(-5)) }, later));
        // Two that have both run for two minutes are.
        Assert.True(RunnerService.InTwoConsoleWindows(root, new List<ProcessInfo> { old, StartedListener(root, 9600, 2, later - RunnerService.SecondConsoleAfter) }, later));
    }

    [Fact]
    public void ASecondConsoleWindowThatStartsItsListenerAgainAndAgain_IsReportedOnce_AndEndsWhenItIsClosed()
    {
        var root = MakeRunner("loop", workFolder: false);
        var start = DateTime.UtcNow;
        var first = StartedListener(root, 9100, 1, start.AddHours(-5));
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alerts = 0;
        bool Reading(DateTime at, List<ProcessInfo> processes)
        {
            var two = RunnerService.InTwoConsoleWindows(root, processes, at);
            alerts += MainWindow.NewProblemAlerts(new[] { Pc(ConsoleRunner(root, two)) }, reported).Count;
            return two;
        }

        // Two hours, read every 20 seconds: the second window's listener runs for 4 minutes, exits, and is started again
        // 30 seconds later. It counts from its third reading on, and goes on counting while it is started again.
        var seen = new List<bool>();
        var lastTwo = start;
        for (var s = 0; s < 2 * 3600; s += 20)
        {
            var at = start.AddSeconds(s);
            var cycle = s % 270;
            var processes = new List<ProcessInfo> { first };
            if (cycle < 240)
            {
                processes.Add(StartedListener(root, 9300u + (uint)(s / 270), 2, at.AddSeconds(-cycle)));
                if (cycle >= RunnerService.SecondConsoleAfter.TotalSeconds) lastTwo = at;
            }
            seen.Add(Reading(at, processes));
        }
        Assert.All(seen.Take(6), Assert.False);
        Assert.All(seen.Skip(6), Assert.True);
        Assert.Equal(1, alerts);

        // The second window is closed: it still counts for a while, as between its listener's starts, then it ends.
        Assert.True(Reading(lastTwo + RunnerService.SecondConsoleHold - TimeSpan.FromSeconds(20), new List<ProcessInfo> { first }));
        Assert.False(Reading(lastTwo + RunnerService.SecondConsoleHold, new List<ProcessInfo> { first }));
        Assert.Equal(1, alerts);
        // When it comes back, it is reported again.
        var back = lastTwo + TimeSpan.FromHours(1);
        Assert.True(Reading(back, new List<ProcessInfo> { first, StartedListener(root, 9900, 2, back - TimeSpan.FromMinutes(3)) }));
        Assert.Equal(2, alerts);
    }

    [Fact]
    public async Task AJobConcern_IsShownBeforeTwoConsoleWindows_AndRunsTwiceBeforeBoth()
    {
        var busy = ConsoleRunner(Path.Combine(_temp, "gpu"), twoConsoles: true, busy: true);
        Assert.Equal(RunnerConcern.TwoConsoleWindows, busy.Health.Concern);

        // The copy that has the session runs its jobs as usual: a stuck job is shown, with its button.
        busy.Health = StuckJob();
        Assert.Equal(RunnerConcern.Stuck, busy.Health.Concern);
        Assert.Equal(System.Windows.Visibility.Visible, busy.StopJobVisibility);
        busy.Health = RunnerHealth.Healthy;
        var health = await JobHealth.EvaluateAsync(new[] { Pc(busy) }, TimeSpan.FromMinutes(15), null, useGitHub: false, DateTime.UtcNow);
        Assert.Equal(RunnerConcern.Stuck, health.Runners[JobHealth.Key(busy)].Concern);

        // With its Windows service running as well, it runs twice, and that is shown first.
        var three = new MachineRunnerStatus
        {
            ServiceName = ServiceName, InstallPath = Path.Combine(_temp, "three"), DisplayName = "three", ServiceInstalled = true, IsRunning = true,
            ActivityText = "Busy", RunsTwice = true, TwoConsoleWindows = true, IsLocalMachine = true, MachineOnline = true
        };
        three.Health = StuckJob();
        Assert.Equal(RunnerConcern.RunsTwice, three.Health.Concern);
    }

    // ----- The first runner list -----

    [Fact]
    public void TheFirstRunnerList_WaitsForTheScheduledTasks_OnlyWhereARunnerMayRunFromAConsoleWindow()
    {
        var a = MakeRunner("a", workFolder: false);
        var b = MakeRunner("b", workFolder: false);
        var services = new List<RunnerServiceRecord> { Service(a, "Running", "Auto", name: "actions.runner.o-r.a"), Service(b, "Stopped", "Auto", name: "actions.runner.o-r.b") };

        // Service runners only, their listeners in session 0: the list does not wait for PowerShell.
        Assert.False(RunnerService.MayRunFromConsole(new[] { a, b }, services, new List<ProcessInfo> { Listener(a, 4200, 0) }));
        // A runner without a service, or a listener in a user's session.
        Assert.True(RunnerService.MayRunFromConsole(new[] { a, b, MakeRunner("c", workFolder: false) }, services, new List<ProcessInfo>()));
        Assert.True(RunnerService.MayRunFromConsole(new[] { a, b }, services, new List<ProcessInfo> { Listener(b, 9100, 1) }));
    }

    // ----- The fleet status -----

    [Fact]
    public void TheNewFields_AreSentAndRead_AndOlderStatusesReadAsBefore()
    {
        var json = JsonSerializer.Serialize(new MachineStatusPayload
        {
            MachineName = "SANDIS",
            Runners = new[] { new MachineRunnerStatus { ServiceName = ServiceName, RunsTwice = true, ConsoleAtSignIn = true,
                                                        ServiceAccount = @"NT AUTHORITY\NETWORK SERVICE", WorkFolderOwner = @"SANDIS\sandis" } }
        });
        Assert.Contains("\"RunsTwice\":true", json);
        Assert.Contains("\"ConsoleAtSignIn\":true", json);
        Assert.Contains("\"WorkFolderOwner\"", json);
        var wire = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var read = JsonSerializer.Deserialize<MachineStatusPayload>(json, wire)!.Runners[0];
        Assert.True(read.RunsTwice);
        Assert.Equal(@"SANDIS\sandis", read.WorkFolderOwner);

        // From a fleet agent before 2.10.0, or with JSON null.
        var older = JsonSerializer.Deserialize<MachineStatusPayload>(
            $$"""{"MachineName":"OLD","Runners":[{"ServiceName":"{{ServiceName}}","ServiceInstalled":true,"IsRunning":true,"ServiceAccount":null,"WorkFolderOwner":null}]}""", wire)!.Runners[0];
        Assert.False(older.RunsTwice);
        Assert.False(older.ConsoleAtSignIn);
        Assert.Equal(string.Empty, older.ServiceAccount);
        Assert.Equal(string.Empty, older.WorkFolderOwner);
        Assert.False(older.Health.IsProblem);
    }

    // ----- The work folder owner -----

    [Theory]
    [InlineData(@"NT AUTHORITY\NetworkService", WellKnownSidType.NetworkServiceSid)]
    [InlineData(@"NT AUTHORITY\NETWORK SERVICE", WellKnownSidType.NetworkServiceSid)]
    [InlineData(@"NT AUTHORITY\LocalService", WellKnownSidType.LocalServiceSid)]
    [InlineData("LocalSystem", WellKnownSidType.LocalSystemSid)]
    public void ServiceAccounts_AreKnownByTheNamesServicesGive(string name, WellKnownSidType account)
        => Assert.Equal(new SecurityIdentifier(account, null), RunnerService.ServiceAccountSid(name));

    [Fact]
    public void AnUnknownServiceAccount_IsNotJudged()
    {
        Assert.Null(RunnerService.ServiceAccountSid(""));
        Assert.Null(RunnerService.ServiceAccountSid(@".\no-such-account-" + Guid.NewGuid().ToString("N")[..12]));
    }

    [Fact]
    public void FoldersOfAnotherAccount_AreFound_AsGitJudgesThem()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.Null(RunnerService.ForeignOwner(NetworkService, new[] { NetworkService }));
        Assert.Equal(user, RunnerService.ForeignOwner(NetworkService, new[] { NetworkService, user }));
        // NETWORK SERVICE is not an administrator, so git refuses a folder of the Administrators group for it.
        Assert.Equal(administrators, RunnerService.ForeignOwner(NetworkService, new[] { administrators }));
        Assert.Null(RunnerService.ForeignOwner(system, new[] { administrators }));
        Assert.Equal(user, RunnerService.ForeignOwner(system, new[] { user }));
    }

    [Fact]
    public void Owners_AreReadFromTheWorkFoldersFolders_AndAMissingFolderHasNone()
    {
        var work = Path.Combine(MakeRunner(), "_work");
        Directory.CreateDirectory(Path.Combine(work, "_actions"));
        var owners = RunnerService.ReadWorkFolderOwners(work);
        Assert.Contains(owners, o => RunnerService.AccountName(o) == OwnerOf(Path.Combine(work, "project-Livonia")));
        Assert.Empty(RunnerService.ReadWorkFolderOwners(Path.Combine(_temp, "missing")));
        Assert.Empty(RunnerService.ReadWorkFolderOwners(@"\\?\Z:\<>|"));
    }

    [Fact]
    public void AWorkFolderOfAnotherAccount_IsGivenToTheService_WhenItIsInTheRunnerFolder()
    {
        var root = MakeRunner();
        // This test's user owns the folders, and the service runs as NETWORK SERVICE.
        Assert.Equal(Path.Combine(root, "_work"), RunnerService.WorkFolderToGive(root, "_work"));
        // A work folder elsewhere is left to its owner, as "Clean work folder" leaves it.
        var outside = Directory.CreateDirectory(Path.Combine(_temp, "shared-work", "repo")).Parent!.FullName;
        Assert.Null(RunnerService.WorkFolderToGive(root, outside));
        Assert.Null(RunnerService.WorkFolderToGive(MakeRunner("empty", workFolder: false), "_work"));
    }

    [Fact]
    public void GivingTheWorkFolder_ChangesEverythingInIt_ButFollowsNoLink()
    {
        var work = Directory.CreateDirectory(Path.Combine(_temp, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "repo", ".git"));
        File.WriteAllText(Path.Combine(work, "repo", "file.txt"), "x");
        var cache = Directory.CreateDirectory(Path.Combine(_temp, "tool-cache")).FullName;
        File.WriteAllText(Path.Combine(cache, "tool.exe"), "x");
        MakeJunction(Path.Combine(work, "tools"), cache);

        var given = new List<string>();
        var (changed, failed) = RunnerService.GiveToAccount(work, NetworkService, (entry, account) =>
        {
            Assert.Equal(NetworkService, account);
            given.Add(Path.GetRelativePath(work, entry.FullName));
            return true;
        });

        Assert.Equal(new[] { ".", "repo", @"repo\.git", @"repo\file.txt" }, given.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(4, changed);
        Assert.Equal(0, failed);
        // A work folder that is itself a link is not changed at all.
        var linked = Path.Combine(_temp, "linked-work");
        MakeJunction(linked, cache);
        Assert.Equal((0, 1), RunnerService.GiveToAccount(linked, NetworkService, (_, _) => throw new InvalidOperationException("followed")));
        // An item that cannot be changed is counted, and the others still are.
        var (some, failures) = RunnerService.GiveToAccount(work, NetworkService,
            (entry, _) => entry.Name == "file.txt" ? throw new UnauthorizedAccessException("denied") : true);
        Assert.Equal(3, some);
        Assert.Equal(1, failures);
    }

    /// <summary>A directory junction, which needs no administrator rights (unlike a symbolic link).</summary>
    private static void MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit(10000);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "Could not create a junction for the test.");
    }
}
