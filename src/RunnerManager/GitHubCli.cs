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
    /// Whether gh can use a github.com sign-in, checked the way the app's own gh calls then run. The stored sign-in
    /// (made with "Connect GitHub") is used when it works: GH_TOKEN or GITHUB_TOKEN would otherwise override it, often
    /// with a token made for something else. They are used only when there is no working stored sign-in.
    /// </summary>
    public static async Task<bool> IsAuthenticatedAsync()
    {
        var stored = await RunAsync("auth status --hostname github.com", environmentToken: false);
        if (stored.ExitCode == 0 || EnvironmentTokenName() == null)
        {
            _useEnvironmentToken = false;
            return stored.ExitCode == 0;
        }
        var token = await RunAsync("auth status --hostname github.com", environmentToken: true);
        _useEnvironmentToken = token.ExitCode == 0;
        return token.ExitCode == 0;
    }

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
        var user = await RunAsync("api user --jq .login");
        if (user.ExitCode != 0 || string.IsNullOrWhiteSpace(user.Output))
            throw new InvalidOperationException("Could not determine the active GitHub account.");
        var logout = await RunAsync($"auth logout --hostname github.com --user {user.Output.Trim()}");
        if (logout.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(logout.Error)
                ? "Could not disconnect the GitHub account." : logout.Error.Trim());
    }

    /// <param name="scope">"repos/owner/repo" or "orgs/name" (see RunnerService.ApiScope).</param>
    public static async Task<string> GetRunnerTokenAsync(string scope, bool removal)
    {
        var endpoint = removal
            ? $"{scope}/actions/runners/remove-token"
            : $"{scope}/actions/runners/registration-token";
        var result = await RunAsync($"api --method POST {endpoint} --jq .token");
        // A classic token without admin:org gets 403 or 404 from the organization endpoints. A 404 would otherwise look
        // like a deleted organization, so this is checked first.
        if (result.ExitCode != 0 && scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase)
            && (IsNotFound(result.Error) || result.Error.Contains("(HTTP 403)", StringComparison.Ordinal)))
        {
            var status = await RunAsync("auth status --hostname github.com");
            if (LacksOrgScope(status.Output + "\n" + status.Error)) throw new GitHubPermissionException(OrgScopeMessage);
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(result.Error.Trim().Length > 0
                ? result.Error.Trim()
                : "GitHub did not return a runner token. Try again.");
        return result.Output.Trim();
    }

    public static async Task<RemoteRunnerState?> GetRunnerStateAsync(string scope, long agentId)
        => (await LookUpRunnerAsync(scope, agentId)).State;

    /// <summary>
    /// The runner's registration on GitHub. State is null when it could not be read; NotFound tells a confirmed
    /// 404 (the runner or repository is gone) apart from timeouts, rate limits and other failures.
    /// </summary>
    public static async Task<RunnerLookup> LookUpRunnerAsync(string scope, long agentId)
    {
        if (agentId <= 0 || string.IsNullOrWhiteSpace(scope)) return new RunnerLookup(null, NotFound: false);
        var result = await RunPollAsync($"api {scope}/actions/runners/{agentId}");
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
        var result = await RunAsync($"api {scope}/actions/runners?name={Uri.EscapeDataString(name)}&per_page=100");
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
        var header = string.IsNullOrWhiteSpace(etag) ? string.Empty : $" -H \"If-None-Match: {etag.Replace("\"", "\\\"")}\"";
        var result = await RunPollAsync($"api -i{header} {endpoint}");
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
        return new ConditionalResult(status, string.IsNullOrEmpty(newEtag) ? etag ?? string.Empty : newEtag, body);
    }

    /// <summary>Runs a status poll through the shared gate (at most four gh processes at a time).</summary>
    internal static async Task<CommandResult> RunPollAsync(string arguments)
    {
        await PollGate.WaitAsync();
        try { return await RunAsync(arguments); }
        finally { PollGate.Release(); }
    }

    public static Task<CommandResult> RunAsync(string arguments, string? standardInput = null, TimeSpan? timeout = null)
        => RunAsync(arguments, _useEnvironmentToken, standardInput, timeout);

    private static async Task<CommandResult> RunAsync(string arguments, bool environmentToken, string? standardInput = null, TimeSpan? timeout = null)
    {
        CommandResult? result;
        try
        {
            var startInfo = new ProcessStartInfo { FileName = ExecutablePath, Arguments = arguments };
            PrepareEnvironment(startInfo, environmentToken);
            result = await ProcessRunner.RunAsync(startInfo, timeout ?? DefaultTimeout, standardInput);
        }
        catch (Win32Exception)
        {
            return new CommandResult(-1, string.Empty,
                "GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and restart Runner Manager.");
        }
        return result ?? new CommandResult(-1, string.Empty,
            $"GitHub CLI timed out (gh {FirstWords(arguments)}). Check the internet connection and try again.");
    }

    private static string FirstWords(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Take(4));
    }
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
