using System.IO;
using System.Runtime.InteropServices;

namespace RunnerManager.Tests;

public class WorkflowLineEndingTests
{
    [Fact]
    public void CrlfAndLfWorkflows_AreRewrittenTheSameWay()
    {
        const string lf = "jobs:\n  build:\n    runs-on: windows-latest # main build\n  test:\n    runs-on: windows-2022\n  lint:\n    runs-on: ubuntu-latest\n";
        var crlf = lf.Replace("\n", "\r\n");
        var selector = WorkflowManager.BuildSelector(new[] { "gpu" });

        var fromLf = WorkflowManager.Transform(lf, selector, new[] { "gpu" });
        var fromCrlf = WorkflowManager.Transform(crlf, selector, new[] { "gpu" });

        Assert.True(fromLf.Changed);
        Assert.True(fromCrlf.Changed);
        Assert.Equal(fromLf.Content, fromCrlf.Content.Replace("\r\n", "\n"));
        Assert.Contains("runs-on: [self-hosted, Windows, X64, gpu] # main build\r\n", fromCrlf.Content);
        Assert.Contains("    runs-on: [self-hosted, Windows, X64, gpu]\r\n  lint:", fromCrlf.Content);
        Assert.DoesNotContain("\r\r", fromCrlf.Content);
    }

    [Fact]
    public void CrlfWorkflowWithoutTrailingNewline_IsRewritten()
    {
        var result = WorkflowManager.Transform("jobs:\r\n  a:\r\n    runs-on: windows-latest",
            WorkflowManager.BuildSelector(Array.Empty<string>()), Array.Empty<string>());
        Assert.True(result.Changed);
        Assert.EndsWith("runs-on: [self-hosted, Windows, X64]", result.Content);
    }
}

public class ConfigInputTests
{
    [Theory]
    [InlineData("BUILD-01")]
    [InlineData("runner_1.2")]
    [InlineData("owner-repo-2")]
    [InlineData("  padded  ")]
    public void ValidRunnerNames_AreAccepted(string name)
        => Assert.Null(RunnerService.RunnerNameProblem(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("a&calc")]
    [InlineData("a|calc")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("%PATH%")]
    [InlineData("a!b")]
    [InlineData("a\"b")]
    [InlineData("a,b")]
    [InlineData("päd")]
    public void InvalidRunnerNames_AreRejected(string name)
        => Assert.NotNull(RunnerService.RunnerNameProblem(name));

    [Fact]
    public void DefaultRunnerNames_AreAccepted()
    {
        Assert.Null(RunnerService.RunnerNameProblem("NewRunner"));
        var suggested = Path.GetFileName(RunnerService.SuggestInstallPath("https://github.com/Owner/my.repo-name_2"));
        Assert.Null(RunnerService.RunnerNameProblem(suggested));
    }

    [Fact]
    public void ValidLabels_AreAccepted()
    {
        Assert.Null(RunnerService.LabelsProblem(new[] { "gpu", "win-11", "a.b_c", " padded " }));
        Assert.Null(RunnerService.LabelsProblem(new[] { "gpu,cuda" }));
        Assert.Null(RunnerService.LabelsProblem(new[] { "", "  " }));
        Assert.Null(RunnerService.LabelsProblem(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("gpu&calc")]
    [InlineData("two words")]
    [InlineData("a|b")]
    [InlineData("%x%")]
    [InlineData("a\"b")]
    [InlineData("a!b")]
    public void InvalidLabels_AreRejected(string label)
        => Assert.NotNull(RunnerService.LabelsProblem(new[] { "gpu", label }));

    [Theory]
    [InlineData("https://github.com/Owner/repo")]
    [InlineData("https://github.com/my-org")]
    [InlineData("https://GitHub.com/Owner/my.repo_2")]
    public void ValidRegistrationUrls_AreAccepted(string url)
        => Assert.Null(RunnerService.RegistrationUrlProblem(url));

    [Theory]
    [InlineData("")]
    [InlineData("http://github.com/Owner/repo")]
    [InlineData("https://example.com/Owner/repo")]
    [InlineData("https://github.com.evil.com/x")]
    [InlineData("https://github.com/Owner/repo&calc")]
    [InlineData("https://github.com/Owner/repo\"")]
    [InlineData("https://github.com/Owner/repo name")]
    [InlineData("https://github.com/%PATH%")]
    public void InvalidRegistrationUrls_AreRejected(string url)
        => Assert.NotNull(RunnerService.RegistrationUrlProblem(url));

    [Fact]
    public void ConfigureArguments_QuoteEveryValue()
    {
        var args = RunnerService.BuildConfigureArguments("https://github.com/Owner/repo", "ABC123", "BUILD-01",
            @"D:\runner work\", new[] { "gpu", " cuda ", "gpu" }, runAsService: true, replace: true);
        Assert.Equal(
            "--unattended --url \"https://github.com/Owner/repo\" --token \"ABC123\" --name \"BUILD-01\" " +
            "--work \"D:\\runner work\\\\\" --labels \"gpu,cuda\" --runasservice --replace", args);
    }

    [Fact]
    public void ConfigureArguments_KeepGroupEphemeralAndUpdateSettings()
    {
        var args = RunnerService.BuildConfigureArguments("https://github.com/my-org", "ABC123", "BUILD-01", "_work",
            new[] { "gpu" }, runAsService: true, replace: false,
            new RunnerRegistrationOptions("GPU machines", Ephemeral: true, DisableUpdate: true, NoDefaultLabels: true));
        Assert.Equal(
            "--unattended --url \"https://github.com/my-org\" --token \"ABC123\" --name \"BUILD-01\" --work \"_work\" " +
            "--labels \"gpu\" --runnergroup \"GPU machines\" --no-default-labels --ephemeral --disableupdate --runasservice", args);
    }

    [Fact]
    public void ConfigureArguments_LeaveOutTheDefaultGroup()
    {
        var args = RunnerService.BuildConfigureArguments("https://github.com/Owner/repo", "ABC123", "BUILD-01", "_work",
            Array.Empty<string>(), runAsService: true, replace: false, new RunnerRegistrationOptions("Default", NoDefaultLabels: true));
        Assert.DoesNotContain("--runnergroup", args);
        // config.cmd needs at least one label without the default ones, so the flag is dropped when there are none.
        Assert.DoesNotContain("--no-default-labels", args);
        Assert.Throws<InvalidOperationException>(() => RunnerService.BuildConfigureArguments("https://github.com/Owner/repo",
            "ABC123", "BUILD-01", "_work", Array.Empty<string>(), true, false, new RunnerRegistrationOptions("a\"&calc")));
    }

    [Fact]
    public void RunnerFile_GivesGroupEphemeralAndUpdateSettings()
    {
        var config = RunnerService.ParseRunnerConfig("﻿{\"agentId\":42,\"agentName\":\"BUILD-01\",\"poolId\":3,\"poolName\":\"GPU machines\"," +
            "\"disableUpdate\":true,\"ephemeral\":true,\"gitHubUrl\":\"https://github.com/my-org\",\"workFolder\":\"_work\"}");
        Assert.Equal(42, config.AgentId);
        Assert.Equal("GPU machines", config.RunnerGroup);
        Assert.True(config.Ephemeral);
        Assert.True(config.DisableUpdate);

        var plain = RunnerService.ParseRunnerConfig("{\"agentId\":7,\"agentName\":\"a\",\"poolName\":\"Default\",\"gitHubUrl\":\"https://github.com/o/r\"}");
        Assert.False(plain.Ephemeral);
        Assert.False(plain.DisableUpdate);
        Assert.Equal("Default", plain.RunnerGroup);
    }

    [Theory]
    [InlineData("{\"id\":5,\"name\":\"a\",\"status\":\"online\",\"busy\":false,\"labels\":[{\"name\":\"self-hosted\",\"type\":\"read-only\"},{\"name\":\"gpu\",\"type\":\"custom\"}]}", true)]
    [InlineData("{\"id\":5,\"name\":\"a\",\"status\":\"online\",\"busy\":false,\"labels\":[{\"name\":\"gpu\",\"type\":\"custom\"}]}", false)]
    public void RunnerWithoutSelfHostedLabel_WasRegisteredWithoutDefaultLabels(string json, bool hasDefaults)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var state = GitHubCli.ReadRunnerState(document.RootElement);
        Assert.Equal(hasDefaults, state.HasDefaultLabels);
        Assert.Equal(new[] { "gpu" }, state.Labels);
    }

    [Fact]
    public void RunnerNames_AreMatchedIgnoringCase()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "{\"total_count\":3,\"runners\":[{\"id\":1,\"name\":\"build-01\"},{\"id\":2,\"name\":\"BUILD-02\"},{\"id\":3,\"name\":\"Build-01\"}]}");
        Assert.Equal(new long[] { 1, 3 }, GitHubCli.ReadRunnerIdsNamed(document.RootElement, "BUILD-01"));
        Assert.Empty(GitHubCli.ReadRunnerIdsNamed(document.RootElement, "other"));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void OnlyTheMarkedOldCopyOfAMovedRunner_IsInUseElsewhere(bool movedAway, bool runningHere, bool expected)
        => Assert.Equal(expected, RunnerService.IsInUseElsewhere(new RunnerInfo { MovedAway = movedAway, IsRunning = runningHere }));

    [Theory]
    [InlineData("https://github.com/Owner/repo", "BUILD&calc", "gpu")]
    [InlineData("https://github.com/Owner/repo", "BUILD-01", "gpu|calc")]
    [InlineData("https://github.com/Owner/repo\" & calc", "BUILD-01", "gpu")]
    public void ConfigureArguments_RejectInvalidInput(string url, string name, string label)
    {
        var error = Assert.Throws<InvalidOperationException>(() => RunnerService.BuildConfigureArguments(
            url, "ABC123", name, "_work", new[] { label }, runAsService: true, replace: false));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void ConfigureArguments_RejectUnsafeTokenAndWorkFolder()
    {
        Assert.Throws<InvalidOperationException>(() => RunnerService.BuildConfigureArguments(
            "https://github.com/Owner/repo", "ABC\"&calc", "BUILD-01", "_work", Array.Empty<string>(), true, false));
        Assert.Throws<InvalidOperationException>(() => RunnerService.BuildConfigureArguments(
            "https://github.com/Owner/repo", "ABC123", "BUILD-01", "%TEMP%", Array.Empty<string>(), true, false));
    }
}

public class GitHubSignInTests
{
    [Theory]
    [InlineData("github.com\n  ✓ Logged in to github.com account octo (keyring)\n  - Token scopes: 'gist', 'read:org', 'repo', 'workflow'", true)]
    [InlineData("github.com\n  ✓ Logged in to github.com account octo (keyring)\n  - Token scopes: 'admin:org', 'gist', 'repo'", false)]
    [InlineData("github.com\n  ✓ Logged in to github.com account octo (GH_TOKEN)\n  - Token: github_pat_****", false)]
    public void MissingOrgPermission_IsReadFromAuthStatus(string status, bool lacks)
        => Assert.Equal(lacks, GitHubCli.LacksOrgScope(status));

    [Fact]
    public void MissingOrgPermission_IsNotMistakenForADeletedOrganization()
    {
        Assert.False(RunnerService.IsGoneFromGitHub(new GitHubPermissionException(GitHubCli.OrgScopeMessage)));
        Assert.Contains("gh auth refresh -h github.com -s admin:org", GitHubCli.OrgScopeMessage);
    }
}

public class RunnerRemovalSafetyTests
{
    [Theory]
    [InlineData("gh: Not Found (HTTP 404)", true)]
    [InlineData("Http response code: NotFound from 'POST https://api.github.com/actions/runner-registration'", true)]
    [InlineData("Response status code does not indicate success: 404 (Not Found).", true)]
    [InlineData("config.cmd was not found in the runner folder.", false)]
    [InlineData("The specified service does not exist as an installed service.", false)]
    [InlineData("Not found", false)]
    [InlineData("gh: Resource not accessible by integration (HTTP 403)", false)]
    public void OnlyGitHubsOwn404_CountsAsGone(string message, bool expected)
        => Assert.Equal(expected, RunnerService.IsGoneFromGitHub(message));

    [Fact]
    public void AppErrors_NeverCountAsGone()
    {
        Assert.False(RunnerService.IsGoneFromGitHub(new FileNotFoundException("gh: Not Found (HTTP 404)")));
        Assert.True(RunnerService.IsGoneFromGitHub(new InvalidOperationException("gh: Not Found (HTTP 404)")));
    }

    [Theory]
    [InlineData("https://www.github.com/Owner/repo", "repos/Owner/repo")]
    [InlineData("https://www.github.com/my-org", "orgs/my-org")]
    [InlineData("https://github.example.com/Owner/repo", "")]
    public void WwwGitHub_IsGitHub(string url, string scope)
        => Assert.Equal(scope, RunnerService.ApiScope(url));

    [Fact]
    public void WwwGitHub_IsRegisteredAsGitHub()
        => Assert.Equal("https://github.com/Owner/repo", RunnerService.NormalizeRegistrationUrl("https://www.github.com/Owner/repo"));

    [Fact]
    public void EnterpriseRunners_SayTheirRegistrationStays()
    {
        Assert.Contains("github.example.com", RunnerService.UnknownScopeMessage("https://github.example.com/Owner/repo"));
        Assert.Contains("left in place", RunnerService.UnknownScopeMessage("https://github.example.com/Owner/repo"));
        Assert.DoesNotContain("left in place", RunnerService.UnknownScopeMessage(""));
    }
}

public class StartupScriptTests
{
    [Theory]
    [InlineData("cd /d C:\\GitHub-Runners\\foo\r\nrun.cmd", true)]
    [InlineData("\"C:\\GitHub-Runners\\foo\\run.cmd\"", true)]
    [InlineData("start \"\" \"C:\\GitHub-Runners\\foo\"", true)]
    [InlineData("C:\\github-runners\\FOO", true)]
    [InlineData("cd /d C:\\GitHub-Runners\\foo-2\r\nrun.cmd", false)]
    [InlineData("\"C:\\GitHub-Runners\\foobar\\run.cmd\"", false)]
    public void StartupScripts_MatchOnlyTheRunnerFolder(string content, bool expected)
        => Assert.Equal(expected, RunnerService.ReferencesFolder(content, @"C:\GitHub-Runners\foo"));

    [Fact]
    public void Shortcuts_AreSearchedAsUnicodeAndAnsi()
    {
        var unicode = System.Text.Encoding.Unicode.GetBytes("xx C:\\GitHub-Runners\\foo\\run.cmd\0");
        Assert.True(RunnerService.ReferencesFolder(RunnerService.ReadStartupText(unicode, shortcut: true), @"C:\GitHub-Runners\foo"));
        var ansi = System.Text.Encoding.Latin1.GetBytes("xx C:\\GitHub-Runners\\foo\0");
        Assert.True(RunnerService.ReferencesFolder(RunnerService.ReadStartupText(ansi, shortcut: true), @"C:\GitHub-Runners\foo"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShortcutArguments_AreFoundAtAnyOffset(int padding)
    {
        // Like "GitHub runner livonia-unity.lnk": conhost.exe is the target, and the runner folder is only in the
        // UTF-16 arguments, which can start at an odd byte offset in the file.
        var arguments = System.Text.Encoding.Unicode.GetBytes(
            "--headless cmd.exe /d /c \"C:\\GitHub-Runners\\project-Livonia\\run-in-session.cmd\"");
        var bytes = new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02 }.Concat(new byte[padding]).Concat(arguments).Concat(new byte[4]).ToArray();
        var text = RunnerService.ReadStartupText(bytes, shortcut: true);
        Assert.Equal(RunnerService.StartupMatch.ThisRunner,
            RunnerService.MatchStartupEntry(text, @"C:\GitHub-Runners\project-Livonia", new[] { @"C:\GitHub-Runners\other" }));
    }

    [Fact]
    public void StartupEntries_ForSeveralRunners_AreLeftInPlace()
    {
        const string script = "start \"\" /d \"C:\\GitHub-Runners\\a\" run.cmd\r\nstart \"\" /d \"C:\\GitHub-Runners\\b\" run.cmd\r\n";
        Assert.Equal(RunnerService.StartupMatch.SharedWithOtherRunners,
            RunnerService.MatchStartupEntry(script, @"C:\GitHub-Runners\a", new[] { @"C:\GitHub-Runners\b" }));
        Assert.Equal(RunnerService.StartupMatch.None,
            RunnerService.MatchStartupEntry(script, @"C:\GitHub-Runners\c", new[] { @"C:\GitHub-Runners\a", @"C:\GitHub-Runners\b" }));
    }

    [Fact]
    public void StartupEntries_OfNestedRunners_BelongOnlyToThatRunner()
    {
        // D:\Git-Runners is itself a runner folder and also holds D:\Git-Runners\Study-Tool.
        const string studyTool = "cd /d D:\\Git-Runners\\Study-Tool\r\nrun.cmd\r\n";
        Assert.Equal(RunnerService.StartupMatch.None,
            RunnerService.MatchStartupEntry(studyTool, @"D:\Git-Runners", new[] { @"D:\Git-Runners\Study-Tool" }));
        Assert.Equal(RunnerService.StartupMatch.ThisRunner,
            RunnerService.MatchStartupEntry(studyTool, @"D:\Git-Runners\Study-Tool", new[] { @"D:\Git-Runners" }));
        Assert.Equal(RunnerService.StartupMatch.ThisRunner,
            RunnerService.MatchStartupEntry("D:\\Git-Runners\\run.cmd", @"D:\Git-Runners", new[] { @"D:\Git-Runners\Study-Tool" }));
    }

    [Fact]
    public void ScheduledTaskList_IsParsed()
    {
        var tasks = RunnerService.ParseTaskList(
            "\\Runner livonia\tcmd.exe /c run.cmd C:\\GitHub-Runners\\project-Livonia\r\n\\Folder\\Other\tnotepad.exe  \r\nnoise\r\n");
        Assert.Equal(2, tasks.Count);
        Assert.Equal(@"\Runner livonia", tasks[0].Name);
        Assert.Equal(RunnerService.StartupMatch.ThisRunner,
            RunnerService.MatchStartupEntry(tasks[0].Actions, @"C:\GitHub-Runners\project-Livonia", Array.Empty<string>()));
    }

    [Fact]
    public void Scripts_FallBackToLatin1WhenNotUtf8()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("cd /d C:\\Läufer\\foo\r\n");
        Assert.Contains(@"C:\Läufer\foo", RunnerService.ReadStartupText(bytes, shortcut: false));
        var utf8 = System.Text.Encoding.UTF8.GetBytes("cd /d C:\\Läufer\\foo\r\n");
        Assert.Contains(@"C:\Läufer\foo", RunnerService.ReadStartupText(utf8, shortcut: false));
    }
}

public class RunnerListingTests
{
    [Fact]
    public void ConsoleRunner_ThatJustStopped_IsShownAsRestarting()
    {
        var root = $@"C:\GitHub-Runners\console-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        Assert.False(RunnerService.IsConsoleRestarting(root, listenerRunning: false, now));
        Assert.False(RunnerService.IsConsoleRestarting(root, listenerRunning: true, now));
        Assert.True(RunnerService.IsConsoleRestarting(root, listenerRunning: false, now.AddSeconds(20)));
        Assert.False(RunnerService.IsConsoleRestarting(root, listenerRunning: false, now.AddMinutes(10)));
    }

    [Theory]
    [InlineData(@"C:\GitHub-Runners\a\bin\Runner.Listener.exe", @"C:\GitHub-Runners\a")]
    [InlineData(@"C:\GitHub-Runners\a\bin.2.330.0\Runner.Listener.exe", @"C:\GitHub-Runners\a")]
    [InlineData(@"D:\Git-Runners\bin\RunnerService.exe", @"D:\Git-Runners")]
    [InlineData("", "")]
    public void RunnerFolder_IsFoundFromItsExecutable(string exe, string root)
        => Assert.Equal(root, RunnerService.RootFromExecutable(exe));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint length);

    [Fact]
    public void ShortPaths_AreExpanded()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"Long Runner Folder {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        try
        {
            var buffer = new System.Text.StringBuilder(520);
            Assert.NotEqual(0u, GetShortPathName(Path.Combine(folder, "bin"), buffer, (uint)buffer.Capacity));
            // The volume may have 8.3 names turned off; the long path must come back either way.
            Assert.Equal(Path.GetFullPath(folder), RunnerService.RootFromExecutable(Path.Combine(buffer.ToString(), "Runner.Listener.exe")),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(@"C:\NO~SUCH\x", RunnerService.LongPath(@"C:\NO~SUCH\x"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("{\"total_count\":2,\"runners\":[{\"id\":4,\"name\":\"a\",\"status\":\"online\"},{\"id\":9,\"name\":\"b\",\"status\":\"offline\"}]}", true)]
    [InlineData("{\"total_count\":150,\"runners\":[{\"id\":4,\"name\":\"a\",\"status\":\"online\"}]}", false)]
    public void RunnerList_KnowsWhenItHoldsEveryRunner(string json, bool complete)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var (ids, isComplete) = GitHubCli.ReadRunnerList(document.RootElement);
        Assert.Contains(4L, ids);
        Assert.Equal(complete, isComplete);
    }

    [Fact]
    public void DefaultLabels_MatchThisPcsArchitecture()
    {
        Assert.Contains(RunnerService.ArchitectureLabel, RunnerLabels.Defaults);
        Assert.Contains(RunnerService.ArchitectureLabel, new[] { "X64", "ARM64" });
    }
}

[Collection(FleetSettingsCollection.Name)]
public class RunnerFolderTests
{
    [Theory]
    [InlineData(@"\\server\share\runners")]
    [InlineData(@"//server/share/runners")]
    [InlineData(@"\\?\UNC\server\share\runners")]
    public void NetworkPaths_AreRefused(string path)
        => Assert.NotNull(RunnerService.NetworkFolderProblem(path, _ => false));

    [Fact]
    public void MappedDrives_AreRefused()
    {
        Assert.NotNull(RunnerService.NetworkFolderProblem(@"Z:\runners", root => root == @"Z:\"));
        Assert.NotNull(RunnerService.NetworkFolderProblem(@"\\?\z:\runners", root => root == @"Z:\"));
        Assert.Null(RunnerService.NetworkFolderProblem(@"C:\runners", root => root == @"Z:\"));
        Assert.Null(RunnerService.NetworkFolderProblem(@"\\?\C:\runners", root => root == @"Z:\"));
    }

    [Theory]
    [InlineData("tools.", "tools")]
    [InlineData("my.repo..", "my.repo")]
    [InlineData("...", "runner")]
    [InlineData("a:b", "a-b")]
    public void FolderNames_DropWhatWindowsDrops(string name, string expected)
        => Assert.Equal(expected, RunnerService.SafeFolderName(name));

    [Fact]
    public void RepositoryEndingInADot_GetsAFolderWithoutIt()
        => Assert.Equal("tools", Path.GetFileName(RunnerService.SuggestInstallPath("https://github.com/acme/tools.")));

    [Theory]
    [InlineData(@"D:\Runners", true)]
    [InlineData(@"C:\GitHub Runners\Build", true)]
    [InlineData(@"Runners", false)]
    [InlineData(@"C:\Runners%TEMP%", false)]
    [InlineData("", false)]
    public void RunnerFolder_IsChecked(string path, bool valid)
        => Assert.Equal(valid, RunnerService.RunnerFolderProblem(path) == null);

    [Fact]
    public void NewRunners_GoToTheConfiguredFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"runner-folder-test-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(Path.Combine(RunnerService.DefaultRoot, "web-app"), RunnerService.SuggestInstallPath("https://github.com/acme/web-app"));
            FleetConfig.Update(c => c.RunnerFolder = folder);
            Assert.Equal(Path.Combine(folder, "web-app"), RunnerService.SuggestInstallPath("https://github.com/acme/web-app"));
        }
        finally { FleetConfig.Update(c => c.RunnerFolder = string.Empty); }
    }
}
