using System.Collections.Concurrent;
using System.Diagnostics;

namespace RunnerManager.Tests;

public class GitHubNameTests
{
    [Theory]
    [InlineData("octo/app")]
    [InlineData("Octo-Org/My.Repo_1")]
    [InlineData("a/b")]
    [InlineData("user_corp/.github")]
    [InlineData("octo/-")]
    [InlineData("octo/...")]
    [InlineData("0123456789012345678901234567890123456789/app", false)]
    [InlineData("012345678901234567890123456789012345678/app")]
    public void RepositoryPaths_AreOwnerSlashName(string path, bool valid = true)
        => Assert.Equal(valid, GitHubNames.IsRepositoryPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("octo")]
    [InlineData("octo/")]
    [InlineData("/app")]
    [InlineData("octo/app/")]
    [InlineData("octo/app/extra")]
    [InlineData("octo/.")]
    [InlineData("octo/..")]
    [InlineData("../app")]
    [InlineData("o/r/../..")]
    [InlineData("me/repo/collaborators/mallory --method PUT -H X:")]
    [InlineData("octo/app --method DELETE")]
    [InlineData("octo /app")]
    [InlineData(" octo/app")]
    [InlineData("octo/app ")]
    [InlineData("octo/app\n")]
    [InlineData("octo\n/app")]
    [InlineData("octo/app\t-X")]
    [InlineData("-octo/app")]
    [InlineData("_octo/app")]
    [InlineData("oc.to/app")]
    [InlineData("octo/app?x=1")]
    [InlineData("octo/app#x")]
    [InlineData("octo/a%20b")]
    [InlineData("octo/%2e%2e")]
    [InlineData("{owner}/{repo}")]
    [InlineData("octo/\"app\"")]
    [InlineData("octo\\app")]
    [InlineData("öcto/app")]
    [InlineData("octo/äpp")]
    [InlineData("https://github.com/octo/app")]
    [InlineData("github.com/octo/app")]
    public void OtherText_IsNotARepositoryPath(string? path)
        => Assert.False(GitHubNames.IsRepositoryPath(path));

    [Fact]
    public void RepositoryNames_AreAtMost100Characters()
    {
        Assert.True(GitHubNames.IsRepositoryPath("octo/" + new string('a', 100)));
        Assert.False(GitHubNames.IsRepositoryPath("octo/" + new string('a', 101)));
    }

    [Fact]
    public void Names_WithANul_AreRefused()
    {
        Assert.False(GitHubNames.IsRepositoryPath("octo/app\0"));
        Assert.False(GitHubNames.IsScope("orgs/octo\0"));
        Assert.False(GitHubNames.IsApiEndpoint("repos/octo/app\0/actions/runs"));
        Assert.False(GitHubNames.IsETag("W/\"abc\0\""));
    }

    [Theory]
    [InlineData("repos/octo/app")]
    [InlineData("repos/Octo-Org/My.Repo_1")]
    [InlineData("orgs/octo")]
    [InlineData("orgs/user_corp")]
    public void Scopes_AreARepositoryOrAnOrganization(string scope)
        => Assert.True(GitHubNames.IsScope(scope));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("octo/app")]
    [InlineData("repos/")]
    [InlineData("repos/octo")]
    [InlineData("repos/octo/")]
    [InlineData("repos/octo/app/")]
    [InlineData("repos/octo/app/extra")]
    [InlineData("repos/octo/..")]
    [InlineData("repos/o/r/../..")]
    [InlineData("repos/me/repo/collaborators/mallory --method PUT -H X:")]
    [InlineData("repos/octo/app --method DELETE")]
    [InlineData("repos/octo/app\n")]
    [InlineData("Repos/octo/app")]
    [InlineData("orgs/")]
    [InlineData("orgs/..")]
    [InlineData("orgs/octo/app")]
    [InlineData("orgs/octo --method DELETE")]
    [InlineData("orgs/octo\n")]
    [InlineData("orgs/-X")]
    [InlineData("ORGS/octo")]
    [InlineData("users/octo")]
    [InlineData("enterprises/octo")]
    [InlineData("user")]
    [InlineData("graphql")]
    [InlineData(" repos/octo/app")]
    public void OtherText_IsNotAScope(string? scope)
        => Assert.False(GitHubNames.IsScope(scope));

    [Theory]
    [InlineData("user")]
    [InlineData("repos/octo/app/actions/runs?status=queued&per_page=50&page=2")]
    [InlineData("repos/octo/app/contents/.github/workflows/my%20build.yml")]
    [InlineData("orgs/octo/actions/runners/12")]
    public void Endpoints_OfThisApp_AreAccepted(string endpoint)
        => Assert.True(GitHubNames.IsApiEndpoint(endpoint));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("graphql")]
    [InlineData("users/octo")]
    [InlineData("/repos/octo/app")]
    [InlineData("--method")]
    [InlineData("-X")]
    [InlineData("repos/octo/app --method DELETE")]
    [InlineData("repos/octo/app\n--method")]
    [InlineData("repos/octo/app/../../../user")]
    [InlineData("repos/octo/app/./actions")]
    [InlineData("repos/octo/app/%2e%2e/%2E%2E/user")]
    [InlineData("repos/octo/app/contents/a%2f..%2fb")]
    [InlineData("repos/octo/app/contents/a%5cb")]
    [InlineData("repos/octo//actions")]
    [InlineData("repos/octo/app/")]
    [InlineData("repos/{owner}/{repo}/actions/runs")]
    [InlineData("repos/octo/app/actions/runs?q=\"x\"")]
    [InlineData("repos/octo/app#fragment")]
    [InlineData("https://example.com/repos/octo/app")]
    public void OtherText_IsNotAnEndpoint(string? endpoint)
        => Assert.False(GitHubNames.IsApiEndpoint(endpoint));

    [Theory]
    [InlineData("W/\"8be36ab681d06987\"", true)]
    [InlineData("\"abc\"", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("W/\"abc\"\r\nX-Other: 1", false)]
    [InlineData("W/\"äbc\"", false)]
    public void ETags_ArePrintableAscii(string? etag, bool valid)
        => Assert.Equal(valid, GitHubNames.IsETag(etag));
}

/// <summary>Tests that replace gh run one at a time, and not next to tests that may start it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class GitHubCliCollection
{
    public const string Name = "GitHub CLI";
}

/// <summary>Stands in for gh.exe: records how each start was asked for and answers from a function.</summary>
internal sealed class FakeGh : IDisposable
{
    private static readonly string[] TokenVariables = { "GH_TOKEN", "GITHUB_TOKEN" };
    private readonly Func<ProcessStartInfo, TimeSpan, string?, Task<CommandResult?>> _previous = GitHubCli.StartProcess;
    private readonly string?[] _tokens = TokenVariables.Select(Environment.GetEnvironmentVariable).ToArray();
    private readonly ConcurrentQueue<(string[] Arguments, string CommandLine, string? Input)> _starts = new();

    internal FakeGh(Func<string[], CommandResult?> answer)
    {
        // With a token variable set, the first gh call of a process checks the sign-in first, which no test here counts.
        foreach (var name in TokenVariables) Environment.SetEnvironmentVariable(name, null);
        GitHubCli.StartProcess = (startInfo, _, input) =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            _starts.Enqueue((arguments, startInfo.Arguments, input));
            return Task.FromResult(answer(arguments));
        };
    }

    /// <summary>The arguments of each start, in order.</summary>
    internal string[][] Calls => _starts.Select(s => s.Arguments).ToArray();
    /// <summary>The endpoints of the gh api starts, in order.</summary>
    internal string[] Endpoints => Calls.Where(c => c.Length > 1 && c[0] == "api").Select(c => c[1]).ToArray();
    /// <summary>Starts that passed one command-line string instead of a list of arguments.</summary>
    internal string[] CommandLines => _starts.Select(s => s.CommandLine).Where(c => c.Length > 0).ToArray();
    internal string?[] Inputs => _starts.Select(s => s.Input).ToArray();

    /// <summary>What gh api -i prints for a response.</summary>
    internal static CommandResult Http(int status, string body = "{}", string headers = "", string error = "")
        => new(status is >= 200 and < 300 ? 0 : 1,
            $"HTTP/2.0 {status} Status\r\nEtag: W/\"etag-{status}\"\r\n{headers}\r\n{body}",
            status is >= 200 and < 300 ? string.Empty : error.Length > 0 ? error : $"gh: HTTP {status}");

    public void Dispose()
    {
        GitHubCli.StartProcess = _previous;
        for (var i = 0; i < TokenVariables.Length; i++) Environment.SetEnvironmentVariable(TokenVariables[i], _tokens[i]);
    }
}

[Collection(GitHubCliCollection.Name)]
public class GitHubArgumentTests
{
    /// <summary>A repository no other test uses, so the caches of GitHubApi start empty.</summary>
    private static string NewRepository() => $"octo/t-{Guid.NewGuid():N}";

    public static TheoryData<string> HostileRepositories => new()
    {
        "me/repo/collaborators/mallory --method PUT -H X:",
        "me/repo/collaborators/mallory",
        "o/r/../..",
        "me/repo --method DELETE",
        "me/repo\" --method DELETE \"",
        "me/repo\n--method\nDELETE",
        "-X/DELETE",
        "{owner}/{repo}",
        "me/repo?x=1",
        "me/%2e%2e",
        "me/..",
        "https://github.com/me/repo"
    };

    [Theory]
    [MemberData(nameof(HostileRepositories))]
    public async Task HostileRepository_NeverStartsGh(string path)
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "{}", string.Empty));

        Assert.Null(await GitHubApi.GetRecentRunsAsync(path));
        Assert.Null(await GitHubApi.GetRepositoryJobsAsync(path));
        Assert.Null(await GitHubApi.GetRunnerWorkAsync(path, 7, Array.Empty<string>()));
        var (read, job) = await GitHubApi.GetCurrentJobAsync(path, 7);
        Assert.False(read);
        Assert.Null(job);
        Assert.Null(await GitHubApi.GetUsualJobDurationAsync(path, "CI", "build"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.RerunFailedJobsAsync(path, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.GetWorkflowFilesAsync(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.UpdateRepositoryFileAsync(
            path, new RepositoryFile { Path = ".github/workflows/ci.yml", Sha = "abc" }, "on: push", "Update"));

        var scope = $"repos/{path}";
        Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, 7));
        var lookup = await GitHubCli.LookUpRunnerAsync(scope, 7);
        Assert.Null(lookup.State);
        Assert.False(lookup.NotFound);
        Assert.Null(await GitHubCli.FindRunnerIdsByNameAsync(scope, "BUILD-01"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubCli.GetRunnerTokenAsync(scope, removal: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubCli.GetRunnerTokenAsync(scope, removal: true));

        Assert.Empty(gh.Calls);
    }

    [Theory]
    [InlineData("orgs/acme --method DELETE")]
    [InlineData("orgs/acme/../../user")]
    [InlineData("orgs/-X")]
    [InlineData("orgs/acme\" -X DELETE \"")]
    [InlineData("acme")]
    [InlineData("")]
    public async Task HostileScope_NeverStartsGh(string scope)
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "{}", string.Empty));

        Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, 7));
        Assert.Null((await GitHubCli.LookUpRunnerAsync(scope, 7)).State);
        Assert.Null(await GitHubCli.FindRunnerIdsByNameAsync(scope, "BUILD-01"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubCli.GetRunnerTokenAsync(scope, removal: true));
        // A removal skips GitHub when the runner is gone there. A refused name must not look like that.
        Assert.False(RunnerService.IsGoneFromGitHub(refused));

        Assert.Empty(gh.Calls);
    }

    [Theory]
    [InlineData("repos/octo/app --method DELETE")]
    [InlineData("repos/octo/app/../../../user")]
    [InlineData("repos/octo/app/%2e%2e/%2e%2e/user")]
    [InlineData("repos/{owner}/{repo}/actions/runs")]
    [InlineData("--method")]
    [InlineData("graphql")]
    public async Task EndpointThisAppDoesNotUse_IsAnsweredAsFailed_WithoutGh(string endpoint)
    {
        using var gh = new FakeGh(_ => FakeGh.Http(200));

        var response = await GitHubCli.GetConditionalAsync(endpoint, "W/\"abc\"");

        Assert.False(response.Ok);
        Assert.False(response.NotModified);
        Assert.False((await GitHubCli.ApiAsync(endpoint, "--method", "DELETE")).ExitCode == 0);
        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task StatusOfAnotherPc_WithAHostileRepository_NeverStartsGh()
    {
        using var gh = new FakeGh(_ => FakeGh.Http(200));
        var runner = new MachineRunnerStatus
        {
            ServiceName = "actions.runner.me-repo.a", IsRunning = true, ActivityText = "Busy", ServiceInstalled = true, AgentId = 7,
            RepositoryPath = "me/repo/collaborators/mallory --method PUT -H X:",
            Scope = "repos/me/repo/collaborators/mallory --method PUT -H X:"
        };
        var machine = MachineInfo.FromPayload("managed", new MachineStatusPayload { MachineName = "MANAGED-PC", Runners = new[] { runner } });

        var health = await JobHealth.EvaluateAsync(new[] { machine }, TimeSpan.FromMinutes(15), null, useGitHub: true, DateTime.UtcNow);

        Assert.Empty(health.Queues);
        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task RunnerName_StaysInsideTheEndpoint()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, """{"runners":[{"id":4,"name":"BUILD 01 --method DELETE"}]}""", string.Empty));

        var ids = await GitHubCli.FindRunnerIdsByNameAsync("repos/octo/app", "BUILD 01 --method DELETE");

        Assert.Equal(new long[] { 4 }, ids);
        Assert.Equal(new[] { "api", "repos/octo/app/actions/runners?name=BUILD%2001%20--method%20DELETE&per_page=100" }, Assert.Single(gh.Calls));
        Assert.Empty(gh.CommandLines);
    }

    [Fact]
    public async Task ETag_IsOneHeaderArgument_WhateverItContains()
    {
        using var gh = new FakeGh(_ => FakeGh.Http(304));

        var response = await GitHubCli.GetConditionalAsync("repos/octo/app/actions/runs?per_page=6", "W/\"abc\" -X DELETE \\");

        Assert.True(response.NotModified);
        Assert.Equal(new[] { "api", "repos/octo/app/actions/runs?per_page=6", "-i", "-H", "If-None-Match: W/\"abc\" -X DELETE \\" },
            Assert.Single(gh.Calls));
        Assert.Empty(gh.CommandLines);
    }

    [Fact]
    public async Task ETagThatCouldEndTheHeader_IsNotSent()
    {
        using var gh = new FakeGh(_ => FakeGh.Http(200, """{"total_count":0}"""));

        var response = await GitHubCli.GetConditionalAsync("repos/octo/app/actions/runs?per_page=6", "W/\"abc\"\r\nAuthorization: x");

        Assert.True(response.Ok);
        Assert.Equal("W/\"etag-200\"", response.ETag);
        Assert.Equal(new[] { "api", "repos/octo/app/actions/runs?per_page=6", "-i" }, Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task RunnerToken_IsRequestedWithSeparateArguments()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "TOKEN123\n", string.Empty));

        Assert.Equal("TOKEN123", await GitHubCli.GetRunnerTokenAsync("orgs/acme", removal: true));
        Assert.Equal("TOKEN123", await GitHubCli.GetRunnerTokenAsync("repos/octo/app", removal: false));

        Assert.Equal(new[]
        {
            new[] { "api", "orgs/acme/actions/runners/remove-token", "--method", "POST", "--jq", ".token" },
            new[] { "api", "repos/octo/app/actions/runners/registration-token", "--method", "POST", "--jq", ".token" }
        }, gh.Calls);
        Assert.Empty(gh.CommandLines);
    }

    [Fact]
    public async Task Rerun_PostsToTheRunOfAValidRepository()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, string.Empty, string.Empty));

        await GitHubApi.RerunFailedJobsAsync("octo/app", 42);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.RerunFailedJobsAsync("octo/app", 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.RerunFailedJobsAsync("octo/app", -5));

        Assert.Equal(new[] { "api", "repos/octo/app/actions/runs/42/rerun-failed-jobs", "--method", "POST" }, Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task WorkflowFile_IsUpdatedThroughAnEscapedPath_WithTheBodyOnStandardInput()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "{}", string.Empty));
        var file = new RepositoryFile { Path = ".github/workflows/my build --method DELETE.yml", Sha = "abc" };

        await GitHubApi.UpdateRepositoryFileAsync("octo/app", file, "on: push", "Update");

        Assert.Equal(new[]
        {
            "api", "repos/octo/app/contents/.github/workflows/my%20build%20--method%20DELETE.yml", "--method", "PUT", "--input", "-"
        }, Assert.Single(gh.Calls));
        Assert.Contains("\"sha\":\"abc\"", Assert.Single(gh.Inputs));
        Assert.Empty(gh.CommandLines);
    }

    [Theory]
    [InlineData(".github/workflows/../../README.md")]
    [InlineData("../../../user")]
    [InlineData(".github//ci.yml")]
    public async Task WorkflowFile_OutsideItsFolder_IsNotUpdated(string path)
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "{}", string.Empty));

        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubApi.UpdateRepositoryFileAsync(
            "octo/app", new RepositoryFile { Path = path, Sha = "abc" }, "on: push", "Update"));

        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task WorkflowFiles_WithAPathGitHubWouldNotList_AreSkipped()
    {
        const string listing = """
            [{"type":"file","path":".github/workflows/ci.yml"},
             {"type":"file","path":".github/workflows/../../secrets.yml"}]
            """;
        var content = Convert.ToBase64String("on: push"u8.ToArray());
        using var gh = new FakeGh(arguments => arguments[1].EndsWith("/contents/.github/workflows", StringComparison.Ordinal)
            ? new CommandResult(0, listing, string.Empty)
            : new CommandResult(0, $$"""{"sha":"abc","content":"{{content}}"}""", string.Empty));

        var files = await GitHubApi.GetWorkflowFilesAsync("octo/app");

        Assert.Equal(".github/workflows/ci.yml", Assert.Single(files.Files).Path);
        Assert.Equal(".github/workflows/../../secrets.yml", Assert.Single(files.Skipped));
        Assert.Equal(new[] { "repos/octo/app/contents/.github/workflows", "repos/octo/app/contents/.github/workflows/ci.yml" }, gh.Endpoints);
    }

    [Fact]
    public async Task Disconnect_SignsOutTheAccountGitHubNames_AndNothingElse()
    {
        using (var gh = new FakeGh(arguments => new CommandResult(0, arguments[0] == "api" ? "octo-cat\n" : string.Empty, string.Empty)))
        {
            await GitHubCli.DisconnectAsync();
            Assert.Equal(new[]
            {
                new[] { "api", "user", "--jq", ".login" },
                new[] { "auth", "logout", "--hostname", "github.com", "--user", "octo-cat" }
            }, gh.Calls);
        }

        using (var gh = new FakeGh(_ => new CommandResult(0, "octo --hostname example.com\n", string.Empty)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(GitHubCli.DisconnectAsync);
            Assert.Equal(new[] { "api", "user", "--jq", ".login" }, Assert.Single(gh.Calls));
        }
    }

    [Fact]
    public async Task RecentRuns_OfAValidRepository_AreReadWithOneEndpointArgument()
    {
        var repository = NewRepository();
        using var gh = new FakeGh(_ => FakeGh.Http(200,
            """{"workflow_runs":[{"id":5,"name":"CI","run_number":9,"status":"completed","conclusion":"failure"}]}"""));

        var runs = await GitHubApi.GetRecentRunsAsync(repository);

        Assert.Equal(5, Assert.Single(runs!).Id);
        Assert.Equal(new[] { "api", $"repos/{repository}/actions/runs?per_page=6&exclude_pull_requests=true", "-i" }, Assert.Single(gh.Calls));
        Assert.Empty(gh.CommandLines);
    }

    [Fact]
    public async Task TextCommandLine_IsStillPassedAsItIs_ForCallersWithFixedText()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, "octo\n", string.Empty));

        await GitHubCli.RunAsync("api user --jq .login");

        Assert.Equal("api user --jq .login", Assert.Single(gh.CommandLines));
    }

    [Fact]
    public async Task ArgumentWithANul_IsNotStarted()
    {
        using var gh = new FakeGh(_ => new CommandResult(0, string.Empty, string.Empty));

        var result = await GitHubCli.RunAsync(new[] { "auth", "logout", "--user", "octo\0", "--hostname", "github.com" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(gh.Calls);
    }
}
