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
        RunsTwice = r.RunsTwice, ConsoleAtSignIn = r.ConsoleAtSignIn, ServiceAccount = r.ServiceAccount, WorkFolderOwner = r.WorkFolderOwner,
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
        // 4. The work folder: the service runs as NETWORK SERVICE, and the folders in it are this user's.
        Assert.Equal(RunnerService.AccountName(NetworkService), runner.ServiceAccount);
        Assert.NotEqual(string.Empty, runner.WorkFolderOwner);
        Assert.Equal(OwnerOf(Path.Combine(root, "_work", "project-Livonia")), runner.WorkFolderOwner);
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
        Assert.Contains("belong to", status.WorkFolderText);
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
        Assert.Equal(runner.WorkFolderOwner, sent.WorkFolderOwner);
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

        // From a fleet agent before 2.9.1, or with JSON null.
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
