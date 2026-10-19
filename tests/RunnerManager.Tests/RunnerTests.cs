using System.IO;

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

    [Fact]
    public void Scripts_FallBackToLatin1WhenNotUtf8()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("cd /d C:\\Läufer\\foo\r\n");
        Assert.Contains(@"C:\Läufer\foo", RunnerService.ReadStartupText(bytes, shortcut: false));
        var utf8 = System.Text.Encoding.UTF8.GetBytes("cd /d C:\\Läufer\\foo\r\n");
        Assert.Contains(@"C:\Läufer\foo", RunnerService.ReadStartupText(utf8, shortcut: false));
    }
}
