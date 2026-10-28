using System.Security.Principal;
using System.Xml.Linq;

namespace RunnerManager.Tests;

public class ScheduledTaskXmlTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\RunnerManager\RunnerManager.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static string AgentXml(string user = @"PC\me")
        => ScheduledTasks.TaskXml(user, Exe, "--agent", "Fleet agent & co", alwaysOn: true, atLogon: true, parallel: false);

    [Fact]
    public void AgentTask_HasALogonAndAKeepAliveTrigger()
    {
        var task = XDocument.Parse(AgentXml()).Root!;
        var triggers = task.Element(Ns + "Triggers")!;
        Assert.NotNull(triggers.Element(Ns + "LogonTrigger"));
        var keepAlive = triggers.Element(Ns + "TimeTrigger")!;
        Assert.Equal("PT5M", keepAlive.Element(Ns + "Repetition")!.Element(Ns + "Interval")!.Value);
        // No duration: it repeats for as long as the task exists.
        Assert.Null(keepAlive.Element(Ns + "Repetition")!.Element(Ns + "Duration"));
        Assert.NotNull(keepAlive.Element(Ns + "StartBoundary"));
        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.NotNull(settings.Element(Ns + "RestartOnFailure"));
        // Signed-in only: the fleet key is protected for the user, so the logon type stays interactive.
        Assert.Equal("InteractiveToken", task.Element(Ns + "Principals")!.Element(Ns + "Principal")!.Element(Ns + "LogonType")!.Value);
        Assert.Equal("Fleet agent & co", task.Element(Ns + "RegistrationInfo")!.Element(Ns + "Description")!.Value);
    }

    [Fact]
    public void LaunchTask_HasNoTriggers()
    {
        var xml = ScheduledTasks.TaskXml(@"PC\me", Exe, "--from-task", "Launch", alwaysOn: false, atLogon: false, parallel: true);
        var task = XDocument.Parse(xml).Root!;
        Assert.Empty(task.Element(Ns + "Triggers")!.Elements());
        Assert.Equal("Parallel", task.Element(Ns + "Settings")!.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.Null(task.Element(Ns + "Settings")!.Element(Ns + "RestartOnFailure"));
        Assert.Equal(Exe, ScheduledTasks.CommandOf(xml));
    }

    [Fact]
    public void CurrentAgentDefinition_IsRecognized_AndAnOlderOneIsNot()
    {
        var current = AgentXml();
        Assert.True(ScheduledTasks.IsCurrentAgentDefinition(current));
        var withoutKeepAlive = System.Text.RegularExpressions.Regex.Replace(current, "<TimeTrigger>.*?</TimeTrigger>", string.Empty);
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(withoutKeepAlive));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(current.Replace("<ExecutionTimeLimit>PT0S", "<ExecutionTimeLimit>PT72H")));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition(string.Empty));
        Assert.False(ScheduledTasks.IsCurrentAgentDefinition("not xml"));
    }

    [Fact]
    public void PrincipalAndEnabled_AreReadFromTheDefinition()
    {
        var xml = AgentXml(@"PC\someone");
        Assert.Equal(@"PC\someone", ScheduledTasks.PrincipalOf(xml));
        Assert.True(ScheduledTasks.IsEnabled(xml));
        // Only the task's own Enabled counts, not its triggers'.
        var disabled = System.Text.RegularExpressions.Regex.Replace(xml, @"<Enabled>true</Enabled>(\s*<Hidden>)", "<Enabled>false</Enabled>$1");
        Assert.False(ScheduledTasks.IsEnabled(disabled));
        Assert.True(ScheduledTasks.IsEnabled(xml.Replace("<LogonTrigger><Enabled>true", "<LogonTrigger><Enabled>false")));
        Assert.Equal(string.Empty, ScheduledTasks.PrincipalOf(string.Empty));
    }

    [Fact]
    public void Accounts_AreComparedBySidOrName()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        Assert.True(ScheduledTasks.IsSameAccount(sid, sid, @"PC\me"));
        Assert.False(ScheduledTasks.IsSameAccount("S-1-5-21-1-2-3-1002", sid, @"PC\me"));
        Assert.False(ScheduledTasks.IsSameAccount(string.Empty, sid, @"PC\me"));
        // Accounts that do not resolve fall back to their names.
        Assert.True(ScheduledTasks.IsSameAccount(@"NOSUCHDOMAIN-X\nosuchuser-x", sid, @"NOSUCHDOMAIN-X\nosuchuser-x"));
        Assert.False(ScheduledTasks.IsSameAccount(@"NOSUCHDOMAIN-X\other-x", sid, @"NOSUCHDOMAIN-X\nosuchuser-x"));

        using var me = WindowsIdentity.GetCurrent();
        Assert.True(ScheduledTasks.IsCurrentUser(me.Name));
        Assert.True(ScheduledTasks.IsCurrentUser(me.User!.Value));
    }

    [Fact]
    public void LaunchTask_IsUsableOnlyWhenOursEnabledAndPointingAtTheExe()
    {
        using var me = WindowsIdentity.GetCurrent();
        var ours = ScheduledTasks.TaskXml(me.Name, Exe, "--from-task", "Launch", alwaysOn: false, atLogon: false, parallel: true);
        Assert.True(Elevation.IsUsableLaunchTask(ours, Exe));
        Assert.False(Elevation.IsUsableLaunchTask(ours, @"D:\Old\RunnerManager.exe"));
        Assert.False(Elevation.IsUsableLaunchTask(ours.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>"), Exe));
        var theirs = ScheduledTasks.TaskXml("S-1-5-21-1-2-3-4242", Exe, "--from-task", "Launch", alwaysOn: false, atLogon: false, parallel: true);
        Assert.False(Elevation.IsUsableLaunchTask(theirs, Exe));
    }

    [Fact]
    public void ElevatedCopy_NoticesWhenItRunsAsAnotherUser()
    {
        Assert.Null(Elevation.OtherUserThanStarted(Array.Empty<string>()));
        Assert.Null(Elevation.OtherUserThanStarted(new[] { "--setup", Elevation.UserSidArgument + Elevation.CurrentUserSid }));
        Assert.Equal("S-1-5-21-1-2-3-4242", Elevation.OtherUserThanStarted(new[] { Elevation.UserSidArgument + "S-1-5-21-1-2-3-4242" }));
        Assert.Null(Elevation.StartedBySid(new[] { Elevation.UserSidArgument }));
    }

    [Theory]
    [InlineData("anna", "anna")]
    [InlineData("a/b:c*d", "a_b_c_d")]
    public void UserTaskNames_AvoidCharactersTasksCannotHave(string user, string expected)
        => Assert.Equal(expected, ScheduledTasks.TaskSafe(user));

    [Fact]
    public void DashboardTasks_ArePerUser_AndTheAgentTaskIsNot()
    {
        Assert.Contains(Environment.UserName, ScheduledTasks.LaunchTaskName);
        Assert.Contains(Environment.UserName, ScheduledTasks.DashboardTaskName);
        Assert.NotEqual(ScheduledTasks.SharedLaunchTaskName, ScheduledTasks.LaunchTaskName);
        Assert.Equal("Runner Manager Fleet Agent", ScheduledTasks.AgentTaskName);
    }
}
