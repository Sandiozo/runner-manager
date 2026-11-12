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
        GitHubCli.ForgetGhFeatures();
        GitHubCli.ForgetProblems();
        GitHubFailures.UtcNow = () => Now;
        GitHubCli.StartProcess = (startInfo, _, input) =>
        {
            var arguments = startInfo.ArgumentList.ToArray();
            _starts.Enqueue((arguments, startInfo.Arguments, input));
            return Task.FromResult(answer(arguments));
        };
    }

    /// <summary>The time the failure memory and the job cache see. It only moves when a test moves it.</summary>
    internal DateTime Now { get; set; } = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The arguments of each start, in order.</summary>
    internal string[][] Calls => _starts.Select(s => s.Arguments).ToArray();
    /// <summary>The endpoints of the gh api starts, in order.</summary>
    internal string[] Endpoints => Calls.Where(c => c.Length > 1 && c[0] == "api").Select(c => c[1]).ToArray();
    /// <summary>Starts that passed one command-line string instead of a list of arguments.</summary>
    internal string[] CommandLines => _starts.Select(s => s.CommandLine).Where(c => c.Length > 0).ToArray();
    internal string?[] Inputs => _starts.Select(s => s.Input).ToArray();

    /// <summary>What gh api -i prints for a response. Each of the <paramref name="headers"/> ends with "\r\n".</summary>
    internal static CommandResult Http(int status, string body = "{}", string headers = "", string error = "")
        => new(status is >= 200 and < 300 ? 0 : 1,
            $"HTTP/2.0 {status} Status\r\nEtag: W/\"etag-{status}\"\r\n{headers}\r\n{body}",
            status is >= 200 and < 300 ? string.Empty : error.Length > 0 ? error : $"gh: HTTP {status}");

    public void Dispose()
    {
        GitHubCli.StartProcess = _previous;
        GitHubCli.ForgetGhFeatures();
        GitHubCli.ForgetProblems();
        GitHubFailures.UtcNow = () => DateTime.UtcNow;
        for (var i = 0; i < TokenVariables.Length; i++) Environment.SetEnvironmentVariable(TokenVariables[i], _tokens[i]);
    }
}

[Collection(GitHubCliCollection.Name)]
public class GitHubActiveAccountTests
{
    private static readonly string[] ActiveStatus = { "auth", "status", "--active", "--hostname", "github.com" };
    private static readonly string[] AllStatus = { "auth", "status", "--hostname", "github.com" };
    private static readonly string[] SignedInUser = { "api", "user", "--jq", ".login" };
    private static readonly CommandResult Works = new(0, "github.com\n  ✓ Logged in to github.com account octo (keyring)\n", string.Empty);
    private static readonly CommandResult OtherAccountExpired = new(1, string.Empty,
        "github.com\n  ✓ Logged in to github.com account octo (keyring)\n  X Failed to log in to github.com account old-octo (keyring)\n");
    private static readonly CommandResult UnknownFlag = new(1, string.Empty, "unknown flag: --active\n\nUsage:  gh auth status [flags]\n");

    [Fact]
    public async Task SecondAccountWithAProblem_DoesNotDisconnectGitHub()
    {
        // What gh 2.57 and later do: all accounts fail the check, the active one alone passes it.
        using var gh = new FakeGh(arguments => arguments.Contains("--active") ? Works : OtherAccountExpired);

        Assert.True(await GitHubCli.IsAuthenticatedAsync());

        Assert.Equal(ActiveStatus, Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task ActiveAccountWithAProblem_IsNotConnected()
    {
        using var gh = new FakeGh(_ => new CommandResult(1, string.Empty, "github.com\n  X Failed to log in to github.com account octo (keyring)\n"));

        Assert.False(await GitHubCli.IsAuthenticatedAsync());

        Assert.Equal(ActiveStatus, Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task OlderGh_WithASecondAccountProblem_AsksGitHubWhoIsSignedIn()
    {
        using var gh = new FakeGh(arguments => arguments.Contains("--active") ? UnknownFlag
            : arguments[0] == "auth" ? OtherAccountExpired
            : new CommandResult(0, "octo\n", string.Empty));

        Assert.True(await GitHubCli.IsAuthenticatedAsync());
        Assert.Equal(new[] { ActiveStatus, AllStatus, SignedInUser }, gh.Calls);

        // --active is not tried again at every check.
        Assert.True(await GitHubCli.IsAuthenticatedAsync());
        Assert.Equal(new[] { ActiveStatus, AllStatus, SignedInUser, AllStatus, SignedInUser }, gh.Calls);
    }

    [Fact]
    public async Task OlderGh_WithAWorkingSignIn_NeedsNoFurtherCheck()
    {
        using var gh = new FakeGh(arguments => arguments.Contains("--active") ? UnknownFlag : Works);

        Assert.True(await GitHubCli.IsAuthenticatedAsync());

        Assert.Equal(new[] { ActiveStatus, AllStatus }, gh.Calls);
    }

    [Fact]
    public async Task OlderGh_WithoutASignIn_IsNotConnected()
    {
        using var gh = new FakeGh(arguments => arguments.Contains("--active") ? UnknownFlag
            : arguments[0] == "auth" ? new CommandResult(1, string.Empty, "You are not logged into any GitHub hosts. To log in, run: gh auth login\n")
            : new CommandResult(4, string.Empty, "To get started with GitHub CLI, please run:  gh auth login\n"));

        Assert.False(await GitHubCli.IsAuthenticatedAsync());

        Assert.Equal(new[] { ActiveStatus, AllStatus }, gh.Calls);
    }

    [Fact]
    public async Task OlderGh_WhereNoAccountWorks_IsNotConnected()
    {
        using var gh = new FakeGh(arguments => arguments.Contains("--active") ? UnknownFlag
            : arguments[0] == "auth" ? new CommandResult(1, string.Empty, "github.com\n  X Failed to log in to github.com account octo (keyring)\n")
            : new CommandResult(1, string.Empty, "gh: Bad credentials (HTTP 401)\n"));

        Assert.False(await GitHubCli.IsAuthenticatedAsync());

        Assert.Equal(new[] { ActiveStatus, AllStatus, SignedInUser }, gh.Calls);
    }

    [Fact]
    public async Task MissingGh_IsNotConnected_AfterOneTry()
    {
        using var gh = new FakeGh(_ => throw new System.ComponentModel.Win32Exception(2));

        Assert.False(await GitHubCli.IsAuthenticatedAsync());

        Assert.Single(gh.Calls);
    }

    [Fact]
    public async Task OrganizationPermissionCheck_ReadsTheActiveAccount()
    {
        using var gh = new FakeGh(arguments => arguments[0] == "api"
            ? new CommandResult(1, string.Empty, "gh: Not Found (HTTP 404)")
            : new CommandResult(0, "github.com\n  ✓ Logged in to github.com account octo (keyring)\n  - Token scopes: 'gist', 'repo'\n", string.Empty));

        var error = await Assert.ThrowsAsync<GitHubPermissionException>(() => GitHubCli.GetRunnerTokenAsync("orgs/acme", removal: false));

        Assert.Equal(GitHubCli.OrgScopeMessage, error.Message);
        Assert.Equal(ActiveStatus, gh.Calls[^1]);
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

[Collection(GitHubCliCollection.Name)]
public class GitHubBackOffTests
{
    private static string NewRepository() => $"octo/b-{Guid.NewGuid():N}";
    private static readonly DateTime Noon = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private static CommandResult Failure(string error, int exitCode = 1) => new(exitCode, string.Empty, error);

    [Fact]
    public async Task FailedEndpoint_IsNotAskedAgain_UntilItsPauseEnds()
    {
        var scope = $"repos/{NewRepository()}";
        using var gh = new FakeGh(_ => FakeGh.Http(502, error: "gh: Bad Gateway (HTTP 502)"));

        Assert.Equal(502, (await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", null)).StatusCode);
        var again = await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", "W/\"abc\"");
        Assert.False(again.Ok);
        Assert.False(again.NotModified);
        Assert.Single(gh.Calls);

        // An error of one endpoint says nothing about another one.
        await GitHubCli.GetConditionalAsync($"{scope}/actions/runners?per_page=100", null);
        Assert.Equal(2, gh.Calls.Length);

        gh.Now += TimeSpan.FromSeconds(44);
        await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", null);
        Assert.Equal(2, gh.Calls.Length);
        gh.Now += TimeSpan.FromSeconds(2);
        await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", null);
        Assert.Equal(3, gh.Calls.Length);
    }

    [Fact]
    public async Task Problem_SaysWhyAndUntilWhen_ForItsScopeOnly()
    {
        var repository = NewRepository();
        using var gh = new FakeGh(_ => FakeGh.Http(404, error: "gh: Not Found (HTTP 404)\n"));
        Assert.Null(GitHubCli.CurrentProblem);

        await GitHubCli.GetConditionalAsync($"repos/{repository}/actions/runs?per_page=6", null);

        var problem = GitHubApi.ProblemFor(repository);
        Assert.NotNull(problem);
        Assert.Equal("GitHub did not find this, or the sign-in has no access to it.", problem.Reason);
        Assert.Equal("gh: Not Found (HTTP 404)", problem.Detail);
        Assert.Equal(Noon.AddSeconds(45), problem.RetryUtc);
        Assert.False(problem.RateLimited);
        Assert.Same(problem, GitHubCli.ProblemFor($"repos/{repository}"));
        Assert.Same(problem, GitHubCli.CurrentProblem);
        Assert.Null(GitHubApi.ProblemFor(repository + "x"));
        Assert.Null(GitHubCli.ProblemFor("orgs/octo"));
        Assert.Null(GitHubCli.ProblemFor(null));
        Assert.Null(GitHubCli.ProblemFor("not a scope"));

        // It stays readable while GitHub is asked again, and is forgotten when nothing asks any more.
        gh.Now += TimeSpan.FromSeconds(60);
        Assert.Same(problem, GitHubApi.ProblemFor(repository));
        gh.Now += TimeSpan.FromMinutes(2);
        Assert.Null(GitHubApi.ProblemFor(repository));
    }

    [Fact]
    public async Task AnswerAfterAFailure_ClearsTheProblem()
    {
        var repository = NewRepository();
        var status = 500;
        using var gh = new FakeGh(_ => FakeGh.Http(status));

        await GitHubCli.GetConditionalAsync($"repos/{repository}/actions/runs?per_page=6", null);
        Assert.NotNull(GitHubApi.ProblemFor(repository));

        status = 200;
        gh.Now += TimeSpan.FromSeconds(46);
        Assert.True((await GitHubCli.GetConditionalAsync($"repos/{repository}/actions/runs?per_page=6", null)).Ok);
        Assert.Null(GitHubApi.ProblemFor(repository));
        Assert.Null(GitHubCli.CurrentProblem);
    }

    [Fact]
    public async Task RateLimit_PausesEveryPoll_UntilItResets()
    {
        var first = $"repos/{NewRepository()}";
        var second = $"repos/{NewRepository()}";
        var reset = new DateTimeOffset(Noon.AddMinutes(20)).ToUnixTimeSeconds();
        var limited = true;
        using var gh = new FakeGh(arguments => !limited || !arguments.Contains("-i") ? FakeGh.Http(200)
            : FakeGh.Http(403, headers: $"X-Ratelimit-Remaining: 0\r\nX-Ratelimit-Reset: {reset}\r\n",
                error: "gh: API rate limit exceeded for user ID 1. (HTTP 403)"));

        Assert.Equal(403, (await GitHubCli.GetConditionalAsync($"{first}/actions/runs?per_page=6", null)).StatusCode);
        Assert.Equal(0, (await GitHubCli.GetConditionalAsync($"{second}/actions/runs?per_page=6", null)).StatusCode);
        Assert.Null(await GitHubCli.GetRunnerStateAsync(second, 7));
        Assert.Null(await GitHubApi.GetRecentRunsAsync(second["repos/".Length..]));
        Assert.Single(gh.Calls);

        var problem = GitHubCli.ProblemFor(second);
        Assert.NotNull(problem);
        Assert.True(problem.RateLimited);
        Assert.Equal("GitHub's request limit is reached.", problem.Reason);
        Assert.Equal(Noon.AddMinutes(20).AddSeconds(2), problem.RetryUtc);
        Assert.Same(problem, GitHubCli.ProblemFor(null));

        // What the user starts is still sent: GitHub's own answer is better than a remembered one.
        await GitHubCli.LookUpRunnerAsync(second, 7);
        Assert.Equal(2, gh.Calls.Length);

        gh.Now = Noon.AddMinutes(20);
        await GitHubCli.GetConditionalAsync($"{second}/actions/runs?per_page=6", null);
        Assert.Equal(2, gh.Calls.Length);

        limited = false;
        gh.Now = Noon.AddMinutes(20).AddSeconds(3);
        Assert.True((await GitHubCli.GetConditionalAsync($"{second}/actions/runs?per_page=6", null)).Ok);
        Assert.Equal(3, gh.Calls.Length);
        Assert.Null(GitHubCli.CurrentProblem);
    }

    [Fact]
    public async Task LostConnection_PausesEveryPoll_ForHalfAMinute()
    {
        var first = $"repos/{NewRepository()}";
        var second = $"repos/{NewRepository()}";
        using var gh = new FakeGh(_ => Failure("error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com\n"));

        await GitHubCli.GetConditionalAsync($"{first}/actions/runs?per_page=6", null);
        await GitHubCli.GetConditionalAsync($"{second}/actions/runs?per_page=6", null);
        Assert.Single(gh.Calls);
        Assert.Equal("GitHub could not be reached. Check the internet connection.", GitHubCli.CurrentProblem!.Reason);
        Assert.Equal("error connecting to api.github.com", GitHubCli.CurrentProblem.Detail);

        gh.Now += TimeSpan.FromSeconds(31);
        await GitHubCli.GetConditionalAsync($"{second}/actions/runs?per_page=6", null);
        Assert.Equal(2, gh.Calls.Length);
    }

    [Fact]
    public async Task SignInProblem_EndsWhenTheSignInWorksAgain()
    {
        var scope = $"repos/{NewRepository()}";
        using var gh = new FakeGh(arguments => arguments[0] == "auth" ? new CommandResult(0, string.Empty, string.Empty)
            : FakeGh.Http(401, error: "gh: Bad credentials (HTTP 401)"));

        await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", null);
        await GitHubCli.GetConditionalAsync($"{scope}/actions/runners?per_page=100", null);
        Assert.Single(gh.Calls);
        Assert.Equal("GitHub did not accept the sign-in. Connect GitHub again.", GitHubCli.CurrentProblem!.Reason);

        Assert.True(await GitHubCli.IsAuthenticatedAsync());
        Assert.Null(GitHubCli.CurrentProblem);
        await GitHubCli.GetConditionalAsync($"{scope}/actions/runners?per_page=100", null);
        Assert.Equal(3, gh.Calls.Length);
    }

    [Fact]
    public async Task JobView_DoesNotStartGhOnEveryTick_WhileOneRequestKeepsFailing()
    {
        var repository = NewRepository();
        using var gh = new FakeGh(arguments => arguments[1].Contains("/jobs?", StringComparison.Ordinal)
            ? FakeGh.Http(502, error: "gh: Bad Gateway (HTTP 502)")
            : arguments[1].Contains("status=in_progress", StringComparison.Ordinal)
                ? FakeGh.Http(200, """{"total_count":1,"workflow_runs":[{"id":11,"name":"CI","run_number":3}]}""")
                : FakeGh.Http(200, """{"total_count":0,"workflow_runs":[]}"""));

        // The job view asks every 3 seconds for every busy runner.
        for (var tick = 0; tick < 5; tick++)
        {
            var (read, job) = await GitHubApi.GetCurrentJobAsync(repository, 7);
            Assert.False(read);
            Assert.Null(job);
            gh.Now += TimeSpan.FromSeconds(3);
        }
        Assert.Equal(3, gh.Calls.Length);
        Assert.Equal("GitHub has a problem at the moment (HTTP 502).", GitHubApi.ProblemFor(repository)!.Reason);

        // While the failed request is not sent again, reading the repository again could only fail at it.
        gh.Now = Noon.AddSeconds(44);
        await GitHubApi.GetCurrentJobAsync(repository, 7);
        Assert.Equal(3, gh.Calls.Length);

        gh.Now = Noon.AddSeconds(46);
        await GitHubApi.GetCurrentJobAsync(repository, 7);
        Assert.Equal(6, gh.Calls.Length);
        Assert.Equal(2, gh.Endpoints.Count(e => e.Contains("/jobs?", StringComparison.Ordinal)));
    }

    /// <summary>A repository with one running run (11), whose job runs on runner 7. The job request fails while <paramref name="failing"/> says so.</summary>
    private static FakeGh OneRunningJob(Func<bool> failing) => new(arguments => arguments[1].Contains("/jobs?", StringComparison.Ordinal)
        ? failing()
            ? FakeGh.Http(502, error: "gh: Bad Gateway (HTTP 502)")
            : FakeGh.Http(200, """{"total_count":1,"jobs":[{"id":21,"name":"build","status":"in_progress","runner_id":7}]}""")
        : arguments[1].Contains("status=in_progress", StringComparison.Ordinal)
            ? FakeGh.Http(200, """{"total_count":1,"workflow_runs":[{"id":11,"name":"CI","run_number":3}]}""")
            : FakeGh.Http(200, """{"total_count":0,"workflow_runs":[]}"""));

    [Fact]
    public async Task JobView_AfterOneFailedRequest_IsReadAgainAsSoonAsThatRequestIsSentAgain()
    {
        var repository = NewRepository();
        var failing = true;
        using var gh = OneRunningJob(() => failing);

        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        failing = false;

        // Unavailable for as long as the one request is paused (45 seconds), and not a second time after that.
        gh.Now = Noon.AddSeconds(44);
        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        Assert.Equal(3, gh.Calls.Length);

        gh.Now = Noon.AddSeconds(46);
        var (read, job) = await GitHubApi.GetCurrentJobAsync(repository, 7);
        Assert.True(read);
        Assert.Equal("build", job!.Name);
        Assert.Null(GitHubApi.ProblemFor(repository));
    }

    [Fact]
    public async Task ForgettingTheProblems_ReadsTheJobViewAgainAtOnce()
    {
        var repository = NewRepository();
        var failing = true;
        using var gh = OneRunningJob(() => failing);

        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        failing = false;
        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);

        // What the lead calls after "Connect GitHub" and a refresh by hand. No time has passed.
        GitHubCli.ForgetProblems();

        Assert.True((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        Assert.Equal(6, gh.Calls.Length);
    }

    [Fact]
    public async Task JobView_PausedForTheSignIn_IsReadAgainWhenTheSignInWorks()
    {
        var repository = NewRepository();
        var signedIn = false;
        using var gh = new FakeGh(arguments => arguments[0] == "auth" ? new CommandResult(0, string.Empty, string.Empty)
            : signedIn ? FakeGh.Http(200, """{"total_count":0,"workflow_runs":[]}""")
            : FakeGh.Http(401, error: "gh: Bad credentials (HTTP 401)"));

        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
        Assert.Single(gh.Calls);

        signedIn = true;
        Assert.True(await GitHubCli.IsAuthenticatedAsync());
        Assert.True((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
    }

    [Fact]
    public void FullMemory_DropsTheFailuresThatAreAskedAgainSoonest_NotAllOfThem()
    {
        using var gh = new FakeGh(_ => FakeGh.Http(404));
        var prefix = $"repos/octo/m-{Guid.NewGuid():N}";
        string Endpoint(int number) => $"{prefix}-{number}/actions/runs?per_page=6";
        void Fail(int number)
        {
            GitHubFailures.Failed(Endpoint(number), 404, default, FakeGh.Http(404));
            gh.Now += TimeSpan.FromMilliseconds(10);
        }
        bool IsPaused(int number) => GitHubFailures.Blocking(Endpoint(number)) != null;
        const int max = GitHubFailures.MaxEndpoints;

        // Two failures that nothing has asked about for a long time, then as many fresh ones as fit.
        Fail(-2);
        Fail(-1);
        gh.Now += TimeSpan.FromMinutes(5);
        for (var number = 1; number <= max - 2; number++) Fail(number);

        // The next two take the place of the two old ones.
        Fail(max - 1);
        Fail(max);
        Assert.All(Enumerable.Range(1, max), number => Assert.True(IsPaused(number)));

        // With only fresh ones, each new failure costs the one whose pause ends first.
        Fail(max + 1);
        Fail(max + 2);
        Assert.False(IsPaused(1));
        Assert.False(IsPaused(2));
        Assert.All(Enumerable.Range(3, max), number => Assert.True(IsPaused(number)));
    }

    [Fact]
    public async Task TimedOutRequest_WhileOthersAreAnswered_PausesOnlyItself()
    {
        var slow = $"repos/{NewRepository()}";
        var other = $"repos/{NewRepository()}";
        // A start that returns nothing is a gh that did not finish in time.
        using var gh = new FakeGh(arguments => arguments[1].StartsWith(slow, StringComparison.Ordinal) ? null : FakeGh.Http(200));

        Assert.True((await GitHubCli.GetConditionalAsync($"{other}/actions/runs?per_page=6", null)).Ok);
        gh.Now += TimeSpan.FromSeconds(40);
        Assert.Equal(0, (await GitHubCli.GetConditionalAsync($"{slow}/actions/runs?per_page=6", null)).StatusCode);

        var problem = GitHubCli.ProblemFor(slow);
        Assert.Equal("GitHub did not answer in time.", problem!.Reason);
        Assert.Equal(gh.Now.AddSeconds(45), problem.RetryUtc);
        Assert.Null(GitHubCli.ProblemFor(other));

        // The request that hung is left alone; the others are still sent.
        await GitHubCli.GetConditionalAsync($"{slow}/actions/runs?per_page=6", null);
        Assert.True((await GitHubCli.GetConditionalAsync($"{other}/actions/runners?per_page=100", null)).Ok);
        Assert.Equal(3, gh.Calls.Length);
    }

    [Fact]
    public async Task TimedOutRequest_WhenNothingWasAnsweredMeanwhile_PausesEveryPoll()
    {
        var slow = $"repos/{NewRepository()}";
        var other = $"repos/{NewRepository()}";
        var reachable = true;
        using var gh = new FakeGh(_ => reachable ? FakeGh.Http(200) : null);

        // The last answer is older than the time the request waited, so it says nothing about now.
        await GitHubCli.GetConditionalAsync($"{other}/actions/runs?per_page=6", null);
        reachable = false;
        gh.Now += TimeSpan.FromSeconds(50);
        await GitHubCli.GetConditionalAsync($"{slow}/actions/runs?per_page=6", null);

        Assert.Equal("GitHub did not answer in time. Check the internet connection.", GitHubCli.ProblemFor(other)!.Reason);
        await GitHubCli.GetConditionalAsync($"{other}/actions/runs?per_page=6", null);
        Assert.Equal(2, gh.Calls.Length);

        gh.Now += TimeSpan.FromSeconds(31);
        await GitHubCli.GetConditionalAsync($"{other}/actions/runs?per_page=6", null);
        Assert.Equal(3, gh.Calls.Length);
    }

    [Fact]
    public async Task RateLimit_IsWaitedOut_ByGitHubsClock_NotThisPcs()
    {
        var scope = $"repos/{NewRepository()}";
        // This PC's clock is 5 minutes behind GitHub's. The limit resets 20 minutes after GitHub sent the answer.
        var sent = Noon.AddMinutes(5);
        var reset = new DateTimeOffset(sent.AddMinutes(20)).ToUnixTimeSeconds();
        using var gh = new FakeGh(_ => FakeGh.Http(403,
            headers: $"Date: {sent:r}\r\nX-Ratelimit-Remaining: 0\r\nX-Ratelimit-Reset: {reset}\r\n",
            error: "gh: API rate limit exceeded for user ID 1. (HTTP 403)"));

        await GitHubCli.GetConditionalAsync($"{scope}/actions/runs?per_page=6", null);

        Assert.Equal(Noon.AddMinutes(20).AddSeconds(2), GitHubCli.ProblemFor(scope)!.RetryUtc);
    }

    [Fact]
    public async Task FailedRunnerList_AsksOncePerRunner_NotOnEveryRefresh()
    {
        var scope = $"repos/{NewRepository()}";
        using var gh = new FakeGh(_ => FakeGh.Http(503));

        for (var refresh = 0; refresh < 4; refresh++)
            foreach (var runner in new long[] { 1, 2, 3 })
                Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, runner));

        // The list once, then each runner's own lookup once.
        Assert.Equal(new[]
        {
            $"{scope}/actions/runners?per_page=100", $"{scope}/actions/runners/1", $"{scope}/actions/runners/2", $"{scope}/actions/runners/3"
        }, gh.Endpoints);
    }

    [Fact]
    public async Task JobView_WithAnAnswerThatCannotBeRead_IsPausedLikeAFailedRead()
    {
        var repository = NewRepository();
        using var gh = new FakeGh(arguments => arguments[1].Contains("/jobs?", StringComparison.Ordinal)
            ? FakeGh.Http(200, "<html>Unicorn</html>")
            : arguments[1].Contains("status=in_progress", StringComparison.Ordinal)
                ? FakeGh.Http(200, """{"total_count":1,"workflow_runs":[{"id":11,"name":"CI","run_number":3}]}""")
                : FakeGh.Http(200, """{"total_count":0,"workflow_runs":[]}"""));

        for (var tick = 0; tick < 5; tick++)
        {
            Assert.False((await GitHubApi.GetCurrentJobAsync(repository, 7)).Read);
            gh.Now += TimeSpan.FromSeconds(3);
        }
        Assert.Equal(3, gh.Calls.Length);
        Assert.Equal("GitHub's answer could not be read.", GitHubApi.ProblemFor(repository)!.Reason);

        gh.Now = Noon.AddSeconds(46);
        await GitHubApi.GetCurrentJobAsync(repository, 7);
        Assert.Equal(6, gh.Calls.Length);
        // The answer that could not be read was not kept, so its ETag is not sent: a 304 would only bring it back.
        var jobRequest = gh.Calls.Last(c => c[1].Contains("/jobs?", StringComparison.Ordinal));
        Assert.DoesNotContain("-H", jobRequest);
    }

    [Fact]
    public async Task UsualDuration_WithARunThatCouldNotBeRead_IsNotNoHistory()
    {
        var repository = NewRepository();
        var failing = true;
        using var gh = new FakeGh(arguments => arguments[1].Contains("/runs/93002/jobs", StringComparison.Ordinal) && failing
            ? FakeGh.Http(502, error: "gh: Bad Gateway (HTTP 502)")
            : arguments[1].Contains("/jobs?", StringComparison.Ordinal)
                ? FakeGh.Http(200, """{"total_count":1,"jobs":[{"id":1,"name":"build","status":"completed","conclusion":"success","started_at":"2026-11-09T10:00:00Z","completed_at":"2026-11-09T10:20:00Z"}]}""")
                : FakeGh.Http(200, """{"total_count":3,"workflow_runs":[{"id":93001,"name":"CI"},{"id":93002,"name":"CI"},{"id":93003,"name":"CI"}]}"""));

        // Two of the three runs give too few durations, but only because the third could not be read.
        Assert.Equal((false, (TimeSpan?)null), await GitHubApi.ReadUsualJobDurationAsync(repository, "CI", "build"));
        Assert.Null(await GitHubApi.GetUsualJobDurationAsync(repository, "CI", "build"));

        // Not remembered: once the run can be read, the usual duration is there.
        failing = false;
        gh.Now += TimeSpan.FromSeconds(46);
        Assert.Equal((true, (TimeSpan?)TimeSpan.FromMinutes(20)), await GitHubApi.ReadUsualJobDurationAsync(repository, "CI", "build"));
    }

    [Fact]
    public async Task RunnerList_ThatCannotBeRead_IsAskedOncePerRunner_NotOnEveryRefresh()
    {
        var scope = $"repos/{NewRepository()}";
        using var gh = new FakeGh(_ => FakeGh.Http(200, "Service temporarily unavailable"));

        for (var refresh = 0; refresh < 4; refresh++)
            foreach (var runner in new long[] { 1, 2 })
                Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, runner));

        Assert.Equal(new[] { $"{scope}/actions/runners?per_page=100", $"{scope}/actions/runners/1", $"{scope}/actions/runners/2" }, gh.Endpoints);
    }

    [Fact]
    public async Task RateLimitedRunnerList_DoesNotFallBackToALookupPerRunner()
    {
        var scope = $"orgs/b{Guid.NewGuid():N}";
        using var gh = new FakeGh(_ => FakeGh.Http(429, headers: "Retry-After: 120\r\n"));

        foreach (var runner in new long[] { 1, 2, 3 })
            Assert.Null(await GitHubCli.GetRunnerStateAsync(scope, runner));

        Assert.Equal(new[] { $"{scope}/actions/runners?per_page=100" }, gh.Endpoints);
        Assert.Equal(Noon.AddSeconds(122), GitHubCli.ProblemFor(scope)!.RetryUtc);
    }

    [Fact]
    public void Failures_AreDescribedInTheAppsWords()
    {
        (GitHubProblem Problem, GitHubFailures.FailureScope Scope) Describe(int status, CommandResult result,
            string? remaining = null, string? reset = null, string? retryAfter = null, string? date = null, bool othersAnswered = false)
            => GitHubFailures.Describe(status, new LimitHeaders(remaining, reset, retryAfter, date), result, Noon, othersAnswered);
        static string Epoch(DateTime time) => new DateTimeOffset(time).ToUnixTimeSeconds().ToString();

        var missing = Describe(0, Failure(GitHubFailures.GhMissingMessage, -1));
        Assert.Equal((GitHubFailures.GhMissingMessage, GitHubFailures.FailureScope.Everything), (missing.Problem.Reason, missing.Scope));

        var timedOut = Failure("GitHub CLI timed out (gh api repos/octo/app). Check the internet connection and try again.", -1);
        var slow = Describe(0, timedOut);
        Assert.Equal(("GitHub did not answer in time. Check the internet connection.", GitHubFailures.FailureScope.Everything),
            (slow.Problem.Reason, slow.Scope));
        Assert.Equal(Noon.AddSeconds(30), slow.Problem.RetryUtc);
        var oneSlow = Describe(0, timedOut, othersAnswered: true);
        Assert.Equal(("GitHub did not answer in time.", GitHubFailures.FailureScope.Endpoint), (oneSlow.Problem.Reason, oneSlow.Scope));
        Assert.Equal(Noon.AddSeconds(45), oneSlow.Problem.RetryUtc);
        // Only a request that hung is helped by the others' answers.
        Assert.Equal(GitHubFailures.FailureScope.Everything,
            Describe(0, Failure("error connecting to api.github.com"), othersAnswered: true).Scope);

        var signedOut = Describe(0, Failure("To get started with GitHub CLI, please run:  gh auth login\n", 4));
        Assert.Equal(("GitHub is not connected.", GitHubFailures.FailureScope.SignIn), (signedOut.Problem.Reason, signedOut.Scope));

        Assert.Equal(GitHubFailures.FailureScope.Endpoint, Describe(0, new CommandResult(0, "not an answer", string.Empty)).Scope);
        Assert.Equal(GitHubFailures.FailureScope.SignIn, Describe(401, Failure("gh: Bad credentials (HTTP 401)")).Scope);

        // Until the reset, never shorter than another failure, and never longer than GitHub's limits last.
        var limit = Failure("gh: API rate limit exceeded for user ID 1. (HTTP 403)");
        Assert.Equal(Noon.AddMinutes(10).AddSeconds(2), Describe(403, limit, "0", Epoch(Noon.AddMinutes(10))).Problem.RetryUtc);
        Assert.Equal(Noon.AddSeconds(45), Describe(403, limit, "0", Epoch(Noon.AddMinutes(-1))).Problem.RetryUtc);
        Assert.Equal(Noon.AddMinutes(61), Describe(403, limit, "0", Epoch(Noon.AddHours(5))).Problem.RetryUtc);
        Assert.Equal(Noon.AddSeconds(62), Describe(403, limit, "0", "99999999999999999999").Problem.RetryUtc);
        Assert.Equal(Noon.AddSeconds(122), Describe(429, Failure("gh: HTTP 429"), retryAfter: "120").Problem.RetryUtc);

        // The reset time is GitHub's, so the wait is counted from the time on GitHub's answer when it has one.
        var inTen = Epoch(Noon.AddMinutes(10));
        Assert.Equal(Noon.AddMinutes(7).AddSeconds(2), Describe(403, limit, "0", inTen, date: Noon.AddMinutes(3).ToString("r")).Problem.RetryUtc);
        Assert.Equal(Noon.AddMinutes(13).AddSeconds(2), Describe(403, limit, "0", inTen, date: Noon.AddMinutes(-3).ToString("r")).Problem.RetryUtc);
        Assert.Equal(Noon.AddMinutes(10).AddSeconds(2), Describe(403, limit, "0", inTen, date: "yesterday").Problem.RetryUtc);

        var secondary = Describe(403, Failure("gh: You have exceeded a secondary rate limit. Please wait a few minutes. (HTTP 403)"), "4990");
        Assert.True(secondary.Problem.RateLimited);
        Assert.Equal(GitHubFailures.FailureScope.Everything, secondary.Scope);
        Assert.Equal(Noon.AddSeconds(62), secondary.Problem.RetryUtc);

        var refused = Describe(403, Failure("gh: Resource not accessible by personal access token (HTTP 403)\n"), "4990");
        Assert.False(refused.Problem.RateLimited);
        Assert.Equal(GitHubFailures.FailureScope.Endpoint, refused.Scope);
        Assert.Equal("The GitHub sign-in is not allowed to read this.", refused.Problem.Reason);
        Assert.Equal("gh: Resource not accessible by personal access token (HTTP 403)", refused.Problem.Detail);

        Assert.Equal("GitHub answered with an error (HTTP 422).", Describe(422, Failure("gh: HTTP 422")).Problem.Reason);
        Assert.Equal(GitHubFailures.FailureScope.Endpoint, Describe(500, Failure("gh: HTTP 500")).Scope);
    }
}

[Collection(GitHubCliCollection.Name)]
public class GitHubPagingTests
{
    private static string NewRepository() => $"octo/p-{Guid.NewGuid():N}";
    private static readonly DateTime Start = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A repository with queued runs 1 (oldest) to <paramref name="queuedRuns"/> (newest), each with one waiting job
    /// named after its run, and running runs from 1000 (oldest) on, each with <paramref name="runningJobs"/> jobs.
    /// </summary>
    private static FakeGh Repository(int queuedRuns, int runningJobs = 1, Func<string, bool>? fails = null, int runningRuns = 1) => new(arguments =>
    {
        var endpoint = arguments[1];
        if (fails?.Invoke(endpoint) == true) return FakeGh.Http(502);
        var query = endpoint[(endpoint.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        var size = int.Parse(query["per_page"]);
        var skip = (int.Parse(query.GetValueOrDefault("page", "1")) - 1) * size;
        if (endpoint.Contains("/jobs?", StringComparison.Ordinal))
        {
            var run = long.Parse(endpoint.Split('/')[5]);
            var jobs = run >= 1000
                ? Enumerable.Range(1, runningJobs).Select(n =>
                    $$"""{"id":{{run * 1000 + n}},"name":"shard-{{n}}","status":"in_progress","runner_id":{{run * 1000 + n}},"labels":["self-hosted"]}""")
                : new[]
                {
                    $$"""{"id":{{run}},"name":"job-{{run}}","status":"queued","labels":["self-hosted"],"created_at":"{{Start.AddMinutes(run):O}}"}"""
                };
            return FakeGh.Http(200, $$"""{"total_count":{{jobs.Count()}},"jobs":[{{string.Join(',', jobs.Skip(skip).Take(size))}}]}""");
        }
        // GitHub lists the newest runs first.
        var runs = (query["status"] == "queued" ? Enumerable.Range(1, queuedRuns) : Enumerable.Range(1000, runningRuns)).Reverse();
        var page = runs.Skip(skip).Take(size).Select(n => $$"""{"id":{{n}},"name":"CI","run_number":{{n}}}""");
        return FakeGh.Http(200, $$"""{"total_count":{{runs.Count()}},"workflow_runs":[{{string.Join(',', page)}}]}""");
    });

    /// <summary>The runner that runs job <paramref name="shard"/> of a running run in <see cref="Repository"/>.</summary>
    private static long RunnerOf(long run, int shard) => run * 1000 + shard;

    private static bool IsJobRequest(string endpoint) => endpoint.Contains("/jobs?", StringComparison.Ordinal);

    private static IEnumerable<int> QueuedRunNumbers(RepositoryWorkState state)
        => state.Jobs.Where(j => j.Status == "queued").Select(j => j.RunNumber).Order();

    [Fact]
    public async Task QueuedRuns_BeyondTheLimit_KeepTheOnesThatWaitedLongest()
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns: 130);

        var state = await GitHubApi.GetRepositoryJobsAsync(repository);

        Assert.Equal(Enumerable.Range(1, GitHubApi.RunPageSize), QueuedRunNumbers(state!));
        // The newest page says how many there are; the oldest runs are on the last page and the end of the one before.
        Assert.Equal(new[]
        {
            $"repos/{repository}/actions/runs?status=queued&per_page=20",
            $"repos/{repository}/actions/runs?status=queued&per_page=20&page=7",
            $"repos/{repository}/actions/runs?status=queued&per_page=20&page=6"
        }, gh.Endpoints.Where(e => e.Contains("status=queued", StringComparison.Ordinal)));

        var work = await GitHubApi.GetRunnerWorkAsync(repository, 99, Array.Empty<string>());
        Assert.Equal(20, work!.QueueCount);
        Assert.Equal("20 waiting  ·  next: CI → job-1", work.QueueText);
    }

    [Fact]
    public async Task QueuedRuns_OnTwoPages_NeedNoThirdRequest()
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns: 30);

        var state = await GitHubApi.GetRepositoryJobsAsync(repository);

        Assert.Equal(Enumerable.Range(1, 20), QueuedRunNumbers(state!));
        Assert.Equal(2, gh.Endpoints.Count(e => e.Contains("status=queued", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task QueuedRuns_WithinTheLimit_AreReadWithOneRequest(int queuedRuns)
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns);

        var state = await GitHubApi.GetRepositoryJobsAsync(repository);

        Assert.Equal(Enumerable.Range(1, queuedRuns), QueuedRunNumbers(state!));
        Assert.Equal(new[] { $"repos/{repository}/actions/runs?status=queued&per_page=20" },
            gh.Endpoints.Where(e => e.Contains("status=queued", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JobsOfARun_BeyondOnePage_AreAllRead()
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns: 0, runningJobs: 230);

        var (read, job) = await GitHubApi.GetCurrentJobAsync(repository, RunnerOf(1000, 230));

        Assert.True(read);
        Assert.Equal("shard-230", job!.Name);
        Assert.Equal(new[]
        {
            $"repos/{repository}/actions/runs/1000/jobs?filter=latest&per_page=100",
            $"repos/{repository}/actions/runs/1000/jobs?filter=latest&per_page=100&page=2",
            $"repos/{repository}/actions/runs/1000/jobs?filter=latest&per_page=100&page=3"
        }, gh.Endpoints.Where(IsJobRequest));
    }

    [Fact]
    public async Task JobsOfARun_ThatFillOnePageExactly_NeedOneRequest()
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns: 0, runningJobs: 100);

        var state = await GitHubApi.GetRepositoryJobsAsync(repository);

        Assert.Equal(100, state!.Jobs.Count);
        Assert.Single(gh.Endpoints, IsJobRequest);
    }

    [Fact]
    public async Task JobRequests_OfOneRead_AreNoMoreThanOnePerRunThatIsRead()
    {
        var repository = NewRepository();
        // More runs than are read in both lists, and every running run has three pages of jobs.
        using var gh = Repository(queuedRuns: 130, runningRuns: 45, runningJobs: 230);

        var state = await GitHubApi.GetRepositoryJobsAsync(repository);

        Assert.NotNull(state);
        Assert.Equal(GitHubApi.MaxJobRequests, gh.Endpoints.Count(IsJobRequest));
        Assert.DoesNotContain(gh.Endpoints.Where(IsJobRequest), e => e.Contains("&page=", StringComparison.Ordinal));
        // Two lists of runs, read from their ends, on top of that.
        Assert.Equal(GitHubApi.MaxJobRequests + 6, gh.Calls.Length);
        // The longest-waiting and the longest-running runs are the ones that are read.
        Assert.Equal(Enumerable.Range(1, 20), QueuedRunNumbers(state));
        Assert.Equal(Enumerable.Range(1000, 20), state.Jobs.Where(j => j.Status == "in_progress").Select(j => j.RunNumber).Distinct().Order());
    }

    [Fact]
    public async Task FurtherJobPages_GoToTheOldestRun_WhileRequestsAreLeft()
    {
        var repository = NewRepository();
        // 20 queued and 18 running runs leave two requests, which is the rest of one run with three pages.
        using var gh = Repository(queuedRuns: 20, runningRuns: 18, runningJobs: 230);

        var (read, job) = await GitHubApi.GetCurrentJobAsync(repository, RunnerOf(1000, 230));

        Assert.True(read);
        Assert.Equal("shard-230", job!.Name);
        Assert.Equal(GitHubApi.MaxJobRequests, gh.Endpoints.Count(IsJobRequest));
        Assert.Equal(new[]
        {
            $"repos/{repository}/actions/runs/1000/jobs?filter=latest&per_page=100&page=2",
            $"repos/{repository}/actions/runs/1000/jobs?filter=latest&per_page=100&page=3"
        }, gh.Endpoints.Where(e => IsJobRequest(e) && e.Contains("&page=", StringComparison.Ordinal)));

        // A newer run is read up to its first page, as every run was before.
        var newer = await GitHubApi.GetCurrentJobAsync(repository, RunnerOf(1001, 230));
        Assert.True(newer.Read);
        Assert.Null(newer.Job);
        Assert.Equal("shard-100", (await GitHubApi.GetCurrentJobAsync(repository, RunnerOf(1001, 100))).Job!.Name);
    }

    [Fact]
    public async Task FailedPage_FailsTheRead_InsteadOfShowingPartOfTheQueue()
    {
        var repository = NewRepository();
        using var gh = Repository(queuedRuns: 130, fails: endpoint => endpoint.EndsWith("&page=6", StringComparison.Ordinal));

        Assert.Null(await GitHubApi.GetRepositoryJobsAsync(repository));
    }
}
