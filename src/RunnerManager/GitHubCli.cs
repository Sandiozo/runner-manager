using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RunnerManager;

public static class GitHubCli
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    /// <summary>Status polls share this gate, so a refresh never starts more than four gh processes at a time.</summary>
    private static readonly SemaphoreSlim PollGate = new(4);

    public static string ExecutablePath
    {
        get
        {
            var installed = @"C:\Program Files\GitHub CLI\gh.exe";
            return File.Exists(installed) ? installed : "gh.exe";
        }
    }

    /// <summary>Environment variables that make gh use a token instead of the stored sign-in.</summary>
    private static readonly string[] TokenVariables = { "GH_TOKEN", "GITHUB_TOKEN" };
    /// <summary>Set by <see cref="IsAuthenticatedAsync"/>: use GH_TOKEN or GITHUB_TOKEN, because there is no stored sign-in.</summary>
    private static volatile bool _useEnvironmentToken;
    /// <summary>
    /// The first check of which sign-in to use, for processes that run gh before anything called
    /// <see cref="IsAuthenticatedAsync"/> (the fleet agent, AI assistant tools).
    /// </summary>
    private static Task? _tokenChoice;
    private static readonly object TokenChoiceGate = new();

    /// <summary>
    /// Whether gh can use a github.com sign-in, checked the way the app's own gh calls then run. The stored sign-in
    /// (made with "Connect GitHub") is used when it works: GH_TOKEN or GITHUB_TOKEN would otherwise override it, often
    /// with a token made for something else. They are used only when there is no working stored sign-in.
    /// </summary>
    public static async Task<bool> IsAuthenticatedAsync()
    {
        lock (TokenChoiceGate) _tokenChoice ??= Task.CompletedTask;
        var stored = await RunAsync(AuthStatus, environmentToken: false);
        if (stored.ExitCode == 0 || EnvironmentTokenName() == null)
        {
            _useEnvironmentToken = false;
            return stored.ExitCode == 0;
        }
        var token = await RunAsync(AuthStatus, environmentToken: true);
        _useEnvironmentToken = token.ExitCode == 0;
        return token.ExitCode == 0;
    }

    private static readonly string[] AuthStatus = { "auth", "status", "--hostname", "github.com" };

    /// <summary>GH_TOKEN or GITHUB_TOKEN when one is set, otherwise null.</summary>
    public static string? EnvironmentTokenName()
        => TokenVariables.FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    public static void LaunchLogin()
        => LaunchInConsole("auth login --web --hostname github.com --git-protocol https");

    /// <summary>Opens gh to add the admin:org permission that organization runners need to the stored sign-in.</summary>
    public static void LaunchOrgPermissionRefresh()
        => LaunchInConsole($"auth refresh --hostname github.com --scopes {OrgScope}");

    /// <summary>
    /// Runs gh in a console window the user can answer. GH_HOST and the token variables are cleared for it, since
    /// gh login refuses to run while a token variable is set, and the app only works with github.com.
    /// </summary>
    private static void LaunchInConsole(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{ExecutablePath}\" {arguments}\"",
            UseShellExecute = false
        };
        PrepareEnvironment(startInfo, environmentToken: false);
        using var process = Process.Start(startInfo);
    }

    /// <summary>
    /// The app only supports github.com, so GH_HOST (which sends gh api calls to another host) is always cleared. The
    /// token variables are cleared unless <see cref="IsAuthenticatedAsync"/> found them to be the only working sign-in.
    /// </summary>
    private static void PrepareEnvironment(ProcessStartInfo startInfo, bool environmentToken)
    {
        startInfo.Environment.Remove("GH_HOST");
        if (environmentToken) return;
        foreach (var name in TokenVariables) startInfo.Environment.Remove(name);
    }

    private const string OrgScope = "admin:org";

    internal const string OrgScopeMessage = "Organization runners need the admin:org permission, which your GitHub sign-in does not have. " +
        "Run gh auth refresh -h github.com -s admin:org in a terminal, then try again. You also need to be an owner of the organization.";

    /// <summary>
    /// True when gh auth status lists the token's scopes and admin:org is not among them. Fine-grained tokens and
    /// GitHub App tokens have no scope list, so they are not reported as missing it.
    /// </summary>
    internal static bool LacksOrgScope(string authStatus)
    {
        var line = authStatus.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.Contains("Token scopes:", StringComparison.OrdinalIgnoreCase));
        return line != null && !line.Contains(OrgScope, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DisconnectAsync()
    {
        var user = await ApiAsync("user", "--jq", ".login");
        var login = user.Output.Trim();
        if (user.ExitCode != 0 || !GitHubNames.IsOwner(login))
            throw new InvalidOperationException("Could not determine the active GitHub account.");
        var logout = await RunAsync(new[] { "auth", "logout", "--hostname", "github.com", "--user", login });
        if (logout.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(logout.Error)
                ? "Could not disconnect the GitHub account." : logout.Error.Trim());
    }

    /// <summary>What a request says when its repository or organization name failed <see cref="GitHubNames"/>.</summary>
    internal const string InvalidNameMessage =
        "The request was not sent to GitHub, because the repository or organization name is not a valid GitHub name.";
    private const string InvalidEndpointMessage =
        "The request was not sent to GitHub, because its address is not one that Runner Manager uses.";

    /// <param name="scope">"repos/owner/repo" or "orgs/name" (see RunnerService.ApiScope).</param>
    public static async Task<string> GetRunnerTokenAsync(string scope, bool removal)
    {
        if (!GitHubNames.IsScope(scope)) throw new InvalidOperationException(InvalidNameMessage);
        var endpoint = removal
            ? $"{scope}/actions/runners/remove-token"
            : $"{scope}/actions/runners/registration-token";
        var result = await ApiAsync(endpoint, "--method", "POST", "--jq", ".token");
        // A classic token without admin:org gets 403 or 404 from the organization endpoints. A 404 would otherwise look
        // like a deleted organization, so this is checked first.
        if (result.ExitCode != 0 && scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase)
            && (IsNotFound(result.Error) || result.Error.Contains("(HTTP 403)", StringComparison.Ordinal)))
        {
            var status = await RunAsync(AuthStatus);
            if (LacksOrgScope(status.Output + "\n" + status.Error)) throw new GitHubPermissionException(OrgScopeMessage);
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(result.Error.Trim().Length > 0
                ? result.Error.Trim()
                : "GitHub did not return a runner token. Try again.");
        return result.Output.Trim();
    }

    /// <summary>
    /// The runner's state for the dashboard. Read from one list per repository or organization, shared by its runners
    /// and polled with an ETag (unchanged answers do not count against the rate limit), instead of one request per
    /// runner. Runners beyond the first 100 of a scope are looked up one by one.
    /// </summary>
    public static async Task<RemoteRunnerState?> GetRunnerStateAsync(string scope, long agentId)
    {
        if (agentId <= 0 || !GitHubNames.IsScope(scope)) return null;
        var list = await GetRunnerListAsync(scope);
        if (list != null && list.Runners.TryGetValue(agentId, out var state)) return state;
        // A complete list without this runner means GitHub has no such registration, as a 404 from the lookup would.
        if (list is { Complete: true }) return null;
        return (await LookUpRunnerAsync(scope, agentId)).State;
    }

    private sealed record RunnerList(Dictionary<long, RemoteRunnerState> Runners, bool Complete);

    private static readonly TimeSpan RunnerListLifetime = TimeSpan.FromSeconds(8);
    private static readonly object RunnerListLock = new();
    /// <summary>One request per scope and refresh: runners of the same scope share the task. A failed task (null) is not reused.</summary>
    private static readonly Dictionary<string, (DateTime Time, Task<RunnerList?> List)> RunnerLists = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string ETag, RunnerList List)> RunnerListETags = new(StringComparer.OrdinalIgnoreCase);

    private static Task<RunnerList?> GetRunnerListAsync(string scope)
    {
        lock (RunnerListLock)
        {
            if (RunnerLists.TryGetValue(scope, out var cached) && DateTime.UtcNow - cached.Time < RunnerListLifetime
                && !(cached.List.IsFaulted || cached.List.IsCanceled || (cached.List.IsCompletedSuccessfully && cached.List.Result == null)))
                return cached.List;
            var task = Task.Run(() => ReadRunnerListAsync(scope));
            RunnerLists[scope] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<RunnerList?> ReadRunnerListAsync(string scope)
    {
        (string ETag, RunnerList List)? previous;
        lock (RunnerListLock) previous = RunnerListETags.TryGetValue(scope, out var entry) ? entry : null;
        var response = await GetConditionalAsync($"{scope}/actions/runners?per_page=100", previous?.ETag);
        if (response.NotModified && previous != null) return previous.Value.List;
        if (!response.Ok) return null;
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            var list = ParseRunnerList(json.RootElement);
            lock (RunnerListLock) RunnerListETags[scope] = (response.ETag, list);
            return list;
        }
        catch { return null; }
    }

    /// <summary>The runners in a list response by ID; Complete is false when GitHub has more than this page holds.</summary>
    private static RunnerList ParseRunnerList(JsonElement root)
    {
        var runners = new Dictionary<long, RemoteRunnerState>();
        if (root.TryGetProperty("runners", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    runners[id.GetInt64()] = ReadRunnerState(item);
        var total = root.TryGetProperty("total_count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : int.MaxValue;
        return new RunnerList(runners, total <= runners.Count);
    }

    /// <summary>For tests: the IDs in a list response and whether the list is complete.</summary>
    internal static (long[] Ids, bool Complete) ReadRunnerList(JsonElement root)
    {
        var list = ParseRunnerList(root);
        return (list.Runners.Keys.Order().ToArray(), list.Complete);
    }

    /// <summary>
    /// The runner's registration on GitHub. State is null when it could not be read; NotFound tells a confirmed
    /// 404 (the runner or repository is gone) apart from timeouts, rate limits and other failures.
    /// </summary>
    public static async Task<RunnerLookup> LookUpRunnerAsync(string scope, long agentId)
    {
        if (agentId <= 0 || !GitHubNames.IsScope(scope)) return new RunnerLookup(null, NotFound: false);
        var result = await ApiAsync($"{scope}/actions/runners/{GitHubNames.Id(agentId)}", Array.Empty<string>(), poll: true);
        if (result.ExitCode != 0) return new RunnerLookup(null, IsNotFound(result.Error));
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            return new RunnerLookup(ReadRunnerState(root), NotFound: false);
        }
        catch { return new RunnerLookup(null, NotFound: false); }
    }

    internal static RemoteRunnerState ReadRunnerState(JsonElement root) => new()
    {
        Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
        Busy = root.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
        Labels = ReadCustomLabels(root),
        HasDefaultLabels = HasLabel(root, "self-hosted")
    };

    /// <summary>
    /// The IDs of the runners registered with this name in a repository or organization (names are unique per scope,
    /// ignoring case). Null when GitHub could not be read.
    /// </summary>
    public static async Task<long[]?> FindRunnerIdsByNameAsync(string scope, string name)
    {
        if (!GitHubNames.IsScope(scope) || string.IsNullOrWhiteSpace(name)) return null;
        var result = await ApiAsync($"{scope}/actions/runners?name={Uri.EscapeDataString(name)}&per_page=100");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            return ReadRunnerIdsNamed(json.RootElement, name);
        }
        catch { return null; }
    }

    /// <summary>The IDs in a runner list response whose name matches, ignoring case (older APIs ignore the name filter).</summary>
    internal static long[] ReadRunnerIdsNamed(JsonElement list, string name)
    {
        if (!list.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array) return Array.Empty<long>();
        return runners.EnumerateArray()
            .Where(r => r.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0)
            .ToArray();
    }

    /// <summary>gh reports a GitHub 404 as "gh: Not Found (HTTP 404)".</summary>
    internal static bool IsNotFound(string error) => error.Contains("(HTTP 404)", StringComparison.Ordinal);

    private static bool HasLabel(JsonElement root, string label)
        => root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
           && labels.EnumerateArray().Any(l => l.TryGetProperty("name", out var n)
                                               && string.Equals(n.GetString(), label, StringComparison.OrdinalIgnoreCase));

    private static string[] ReadCustomLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var result = new List<string>();
        foreach (var label in labels.EnumerateArray())
        {
            var type = label.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var name = label.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (type == "custom" && !string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result.ToArray();
    }

    /// <summary>
    /// GET with an ETag. GitHub does not count 304 Not Modified responses against the rate limit,
    /// so polling the same resource this way is effectively free while nothing changes.
    /// </summary>
    public static async Task<ConditionalResult> GetConditionalAsync(string endpoint, string? etag)
    {
        var known = GitHubNames.IsETag(etag) ? etag! : string.Empty;
        var options = new List<string> { "-i" };
        if (known.Length > 0)
        {
            // One argument, so the quotes and spaces of the ETag stay inside the header.
            options.Add("-H");
            options.Add($"If-None-Match: {known}");
        }
        var result = await ApiAsync(endpoint, options, poll: true);
        var text = result.Output.Replace("\r\n", "\n");
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = split >= 0 ? text[..split] : text;
        var body = split >= 0 ? text[(split + 2)..] : string.Empty;

        var status = 0;
        var newEtag = string.Empty;
        foreach (var line in headers.Split('\n'))
        {
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 2) int.TryParse(parts[1], out status);
            }
            else if (line.StartsWith("ETag:", StringComparison.OrdinalIgnoreCase))
                newEtag = line[5..].Trim();
        }
        return new ConditionalResult(status, string.IsNullOrEmpty(newEtag) ? known : newEtag, body);
    }

    /// <summary>
    /// Runs gh api for an endpoint, with each option as its own argument. The endpoint is checked here once more, so a
    /// caller that forgot a name check still cannot pass a flag or reach another part of the API.
    /// </summary>
    internal static Task<CommandResult> ApiAsync(string endpoint, params string[] options)
        => ApiAsync(endpoint, options, poll: false);

    /// <param name="poll">A status poll: it waits for the shared gate (at most four gh processes at a time).</param>
    /// <param name="standardInput">The request body, for options that end with "--input", "-".</param>
    internal static async Task<CommandResult> ApiAsync(string endpoint, IReadOnlyList<string> options, bool poll = false,
        string? standardInput = null)
    {
        if (!GitHubNames.IsApiEndpoint(endpoint)) return new CommandResult(-1, string.Empty, InvalidEndpointMessage);
        var arguments = new List<string>(options.Count + 2) { "api", endpoint };
        arguments.AddRange(options);
        if (!poll) return await RunAsync(arguments, standardInput);
        await PollGate.WaitAsync();
        try { return await RunAsync(arguments, standardInput); }
        finally { PollGate.Release(); }
    }

    /// <summary>
    /// Runs gh with one command line. Only for text this code wrote itself: a value from anywhere else (another PC,
    /// GitHub, a file, the user) could add arguments here, so it goes through the overload that takes a list.
    /// </summary>
    public static async Task<CommandResult> RunAsync(string arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        await ChooseTokenOnceAsync();
        var startInfo = new ProcessStartInfo { FileName = ExecutablePath, Arguments = arguments };
        return await StartAsync(startInfo, FirstWords(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), 4),
            _useEnvironmentToken, standardInput, timeout);
    }

    /// <summary>
    /// Runs gh with each argument passed on its own. Whatever an argument contains (spaces, quotes, dashes), it stays
    /// that one argument, so a value cannot add a flag or a second endpoint.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        await ChooseTokenOnceAsync();
        return await RunAsync(arguments, _useEnvironmentToken, standardInput, timeout);
    }

    /// <summary>Decides once per process whether a token variable is needed, when one is set and nothing checked yet.</summary>
    private static Task ChooseTokenOnceAsync()
    {
        if (EnvironmentTokenName() == null) return Task.CompletedTask;
        lock (TokenChoiceGate) return _tokenChoice ??= IsAuthenticatedAsync();
    }

    private static Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, bool environmentToken, string? standardInput = null,
        TimeSpan? timeout = null)
    {
        // Windows ends a command line at the first NUL, which would drop the arguments after it.
        if (arguments.Any(argument => argument.Contains('\0')))
            return Task.FromResult(new CommandResult(-1, string.Empty, "GitHub CLI (gh) was not started, because a value cannot be passed to it."));
        var startInfo = new ProcessStartInfo { FileName = ExecutablePath };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return StartAsync(startInfo, FirstWords(arguments, 2), environmentToken, standardInput, timeout);
    }

    /// <summary>Starts gh. Tests replace it to see the arguments without starting a process.</summary>
    internal static Func<ProcessStartInfo, TimeSpan, string?, Task<CommandResult?>> StartProcess { get; set; } = ProcessRunner.RunAsync;

    /// <param name="what">The start of the command, for the message when gh does not finish in time.</param>
    private static async Task<CommandResult> StartAsync(ProcessStartInfo startInfo, string what, bool environmentToken,
        string? standardInput, TimeSpan? timeout)
    {
        CommandResult? result;
        try
        {
            PrepareEnvironment(startInfo, environmentToken);
            result = await StartProcess(startInfo, timeout ?? DefaultTimeout, standardInput);
        }
        catch (Win32Exception)
        {
            return new CommandResult(-1, string.Empty,
                "GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and restart Runner Manager.");
        }
        return result ?? new CommandResult(-1, string.Empty,
            $"GitHub CLI timed out (gh {what}). Check the internet connection and try again.");
    }

    private static string FirstWords(IEnumerable<string> arguments, int count) => string.Join(' ', arguments.Take(count));
}

/// <summary>Runs a console process with redirected output and a time limit.</summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Starts the process hidden, captures its output and waits for it to exit. Returns null when it did not finish
    /// within the timeout; the whole process tree is then killed. Start failures (Win32Exception) are not caught.
    /// </summary>
    internal static async Task<CommandResult?> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, string? standardInput = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = standardInput != null;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(startInfo.FileName)}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput != null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>"60 seconds" or "10 minutes", for timeout messages.</summary>
    internal static string Describe(TimeSpan timeout)
        => timeout.TotalSeconds < 120 ? $"{(int)timeout.TotalSeconds} seconds" : $"{(int)timeout.TotalMinutes} minutes";
}

public sealed record CommandResult(int ExitCode, string Output, string Error);

/// <summary>GitHub refused a request because the sign-in lacks a permission (not because something is gone).</summary>
public sealed class GitHubPermissionException(string message) : InvalidOperationException(message);

public sealed record ConditionalResult(int StatusCode, string ETag, string Body)
{
    public bool NotModified => StatusCode == 304;
    public bool Ok => StatusCode is >= 200 and < 300;
}

/// <summary>A runner registration read from GitHub; see GitHubCli.LookUpRunnerAsync.</summary>
public sealed record RunnerLookup(RemoteRunnerState? State, bool NotFound);

public sealed class RemoteRunnerState
{
    public string Status { get; init; } = "unknown";
    public bool Busy { get; init; }
    public string[] Labels { get; init; } = Array.Empty<string>();
    /// <summary>False when the runner was registered with --no-default-labels (it has no "self-hosted" label).</summary>
    public bool HasDefaultLabels { get; init; } = true;
}
