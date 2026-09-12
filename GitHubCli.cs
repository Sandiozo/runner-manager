using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace RunnerWidget;

public static class GitHubCli
{
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
        Process.Start(new ProcessStartInfo
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
                ? "GitHub logout failed." : logout.Error.Trim());
    }

    public static async Task<string> GetRunnerTokenAsync(string repositoryPath, bool removal)
    {
        var endpoint = removal
            ? $"repos/{repositoryPath}/actions/runners/remove-token"
            : $"repos/{repositoryPath}/actions/runners/registration-token";
        var result = await RunAsync($"api --method POST {endpoint} --jq .token");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(result.Error.Trim().Length > 0
                ? result.Error.Trim()
                : "GitHub did not return a runner token.");
        return result.Output.Trim();
    }

    public static async Task<RemoteRunnerState?> GetRunnerStateAsync(string repositoryPath, long agentId)
    {
        if (agentId <= 0) return null;
        var result = await RunAsync($"api repos/{repositoryPath}/actions/runners/{agentId}");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            return new RemoteRunnerState
            {
                Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
                Busy = root.TryGetProperty("busy", out var busy) && busy.GetBoolean(),
                Labels = ReadCustomLabels(root)
            };
        }
        catch { return null; }
    }

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

    public static async Task<CommandResult> RunAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start GitHub CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }
}

public sealed record CommandResult(int ExitCode, string Output, string Error);

public sealed class RemoteRunnerState
{
    public string Status { get; init; } = "unknown";
    public bool Busy { get; init; }
    public string[] Labels { get; init; } = Array.Empty<string>();
}
