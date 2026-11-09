using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RunnerManager.Tests;

/// <summary>Setup, scheduled tasks, updates and start-up, from the 2.8.2 audit.</summary>
public class AuditSetupTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\RunnerManager\RunnerManager.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string CurrentUser
    {
        get
        {
            using var me = WindowsIdentity.GetCurrent();
            return me.Name;
        }
    }

    private static string AgentXml() => ScheduledTasks.TaskXml(@"PC\me", Exe, "--agent", "Agent", alwaysOn: true, atLogon: true, parallel: false);
    private static string LaunchXml(string exe = Exe) => ScheduledTasks.TaskXml(CurrentUser, exe, "--from-task", "Launch", alwaysOn: false, atLogon: false, parallel: true);
    private static string StartupXml(string? user = null, string exe = Exe)
        => ScheduledTasks.TaskXml(user ?? CurrentUser, exe, "--minimized", "Startup", alwaysOn: false, atLogon: true, parallel: false);

    /// <summary>As earlier versions registered it (Task Scheduler's default priority).</summary>
    private static string Old(string xml) => xml.Replace("<Priority>4</Priority>", "<Priority>7</Priority>");
    /// <summary>As Task Scheduler exports a task with the default priority: without the element.</summary>
    private static string WithoutPriority(string xml) => Regex.Replace(xml, @"\s*<Priority>\d+</Priority>", string.Empty);

    // ================= Task priority =================

    [Fact]
    public void EveryTask_IsRegisteredAtNormalPriority()
    {
        foreach (var xml in new[] { AgentXml(), LaunchXml(), StartupXml() })
        {
            var settings = XDocument.Parse(xml).Root!.Element(Ns + "Settings")!;
            // 4 to 6 are normal priority; 7, the default, is below normal.
            Assert.Equal("4", settings.Element(Ns + "Priority")!.Value);
            Assert.True(ScheduledTasks.HasCurrentPriority(xml));
        }
    }

    [Fact]
    public void AnAgentTaskBelowNormalPriority_IsRegisteredAgain()
    {
        Assert.True(ScheduledTasks.IsCurrentAgentDefinition(AgentXml()));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(Old(AgentXml())));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(WithoutPriority(AgentXml())));
        Assert.False(ScheduledTasks.HasCurrentPriority(string.Empty));
        Assert.False(ScheduledTasks.HasCurrentPriority("not xml"));
    }

    [Fact]
    public void ALaunchTaskBelowNormalPriority_IsRegisteredAgain()
    {
        Assert.False(Installer.LaunchTaskNeedsRegistering(LaunchXml(), Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(Old(LaunchXml()), Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(WithoutPriority(LaunchXml()), Exe));
        // As before: a missing task, or one that starts another exe.
        Assert.True(Installer.LaunchTaskNeedsRegistering(string.Empty, Exe));
        Assert.True(Installer.LaunchTaskNeedsRegistering(LaunchXml(@"D:\Old\RunnerManager.exe"), Exe));
    }

    [Fact]
    public void AStartupTask_IsRegisteredAgainOnlyWhenItIsOursTurnedOnAndOutdated()
    {
        Assert.False(Installer.StartupTaskNeedsRegistering(StartupXml(), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(Old(StartupXml()), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(WithoutPriority(StartupXml()), Exe));
        Assert.True(Installer.StartupTaskNeedsRegistering(StartupXml(exe: @"D:\Old\RunnerManager.exe"), Exe));
        // Not there, turned off by the user, or another user's: left alone.
        Assert.False(Installer.StartupTaskNeedsRegistering(string.Empty, Exe));
        var turnedOff = Regex.Replace(Old(StartupXml()), @"<Enabled>true</Enabled>(\s*<Hidden>)", "<Enabled>false</Enabled>$1");
        Assert.False(Installer.StartupTaskNeedsRegistering(turnedOff, Exe));
        Assert.False(Installer.StartupTaskNeedsRegistering(Old(StartupXml("S-1-5-21-1-2-3-4242")), Exe));
    }

    [Fact]
    public void AProcessStartedBelowNormal_RaisesItselfToNormal()
    {
        try
        {
            ProcessPriority.LowerForTest();
            var lowered = ProcessPriority.Read();
            Assert.Equal(ProcessPriorityClass.BelowNormal, lowered.Cpu);
            Assert.Equal(1, lowered.Io);
            Assert.Equal(2, lowered.Memory);

            Assert.True(ProcessPriority.RaiseToNormal());
            var raised = ProcessPriority.Read();
            Assert.Equal(ProcessPriorityClass.Normal, raised.Cpu);
            Assert.Equal(2, raised.Io);
            Assert.Equal(5, raised.Memory);
            // Already normal: nothing to do.
            Assert.False(ProcessPriority.RaiseToNormal());
        }
        finally { ProcessPriority.RaiseToNormal(); }
    }

    // ================= Processes setup ends =================

    [Theory]
    [InlineData(null, CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\"", CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --minimized", CopyRole.Dashboard)]
    [InlineData("\"C:\\My ai tools\\RunnerManager.exe\" --from-task", CopyRole.Dashboard)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --mcp", CopyRole.Assistant)]
    [InlineData("C:\\Apps\\RunnerManager.exe ai list-pcs", CopyRole.Assistant)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --agent", CopyRole.Agent)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --setup", CopyRole.Other)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --uninstall --quiet", CopyRole.Other)]
    [InlineData("\"C:\\Apps\\RunnerManager.exe\" --install-agent", CopyRole.Other)]
    public void ProcessRoles_AreToldFromTheCommandLine(string? commandLine, CopyRole expected)
        => Assert.Equal(expected, RunningCopies.RoleOf(commandLine));

    [Fact]
    public void Setup_EndsAssistantHostsOnlyWhenTheyLoseTheirExe()
    {
        const string mcp = "\"C:\\Apps\\RunnerManager.exe\" --mcp";
        Assert.False(Installer.IsClosedBySetup(mcp, endAssistants: false));
        Assert.True(Installer.IsClosedBySetup(mcp, endAssistants: true));
        Assert.True(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --minimized", endAssistants: false));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --agent", endAssistants: true));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Apps\\RunnerManager.exe\" --install-agent", endAssistants: true));
    }

    // ================= Helpers that run after exit =================

    [Fact]
    public void TheUninstallHelper_WaitsForTheProcessWithoutATimeLimit()
    {
        var unlimited = AppUpdater.AfterExitScript("Write-Output done", 1234, timeout: null, TimeSpan.FromSeconds(1));
        Assert.StartsWith("Wait-Process -Id 1234 -ErrorAction SilentlyContinue;", unlimited);
        Assert.DoesNotContain("-Timeout", unlimited);
        Assert.Contains("-Timeout 120", AppUpdater.AfterExitScript("x", 1234, TimeSpan.FromSeconds(120), TimeSpan.Zero));
    }

    [Fact]
    public void RemovingAFolder_IsSkippedWhenSetupInstalledThereAfterAll()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-unless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var marker = Path.Combine(root, "ran.txt").Replace("'", "''");
            var write = $"Set-Content -LiteralPath '{marker}' -Value ran";
            // The Settings → Apps entry points elsewhere (or there is none): the script runs.
            RunPowerShell(Installer.UnlessInstalledIn(Path.Combine(root, "RunnerManager"), write));
            Assert.True(File.Exists(Path.Combine(root, "ran.txt")));
            File.Delete(Path.Combine(root, "ran.txt"));

            // Only read, never written: the entry of the Runner Manager installed on this PC, if any.
            if (Installer.RegisteredInstallDir() is { } installed)
            {
                RunPowerShell(Installer.UnlessInstalledIn(installed + Path.DirectorySeparatorChar, write));
                Assert.False(File.Exists(Path.Combine(root, "ran.txt")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunPowerShell(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        Assert.True(process.WaitForExit(30_000), "The script did not finish within 30 seconds.");
    }

    // ================= Update health =================

    [Fact]
    public async Task AnUpdate_WorksOnlyOnceTheAgentAnswers()
    {
        var asked = 0;
        Assert.True(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(++asked >= 3),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(10)));
        Assert.Equal(3, asked);

        // Never answers: not marked as working.
        Assert.False(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(false),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)));

        // Without a pairing code nothing can ask it, so staying up is enough.
        Assert.True(await UpdateGuard.WaitUntilAnsweringAsync(() => Task.FromResult<bool?>(null),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.Zero));
    }
}
