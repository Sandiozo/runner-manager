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

    public static async Task<bool> IsAuthenticatedAsync()
    {
        var result = await RunAsync("auth status --hostname github.com");
        return result.ExitCode == 0;
    }

    public static void LaunchLogin()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{ExecutablePath}\" auth login --web --hostname github.com --git-protocol https\"",
            UseShellExecute = true
        });
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
            return new RunnerLookup(new RemoteRunnerState
            {
                Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                Busy = root.TryGetProperty("busy", out var busy) && busy.GetBoolean(),
                Labels = ReadCustomLabels(root)
            }, NotFound: false);
        }
        catch { return new RunnerLookup(null, NotFound: false); }
    }

    /// <summary>gh reports a GitHub 404 as "gh: Not Found (HTTP 404)".</summary>
    internal static bool IsNotFound(string error) => error.Contains("(HTTP 404)", StringComparison.Ordinal);

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

    public static async Task<CommandResult> RunAsync(string arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        CommandResult? result;
        try
        {
            result = await ProcessRunner.RunAsync(
                new ProcessStartInfo { FileName = ExecutablePath, Arguments = arguments }, timeout ?? DefaultTimeout, standardInput);
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
}
