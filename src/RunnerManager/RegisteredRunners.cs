using System.Text.Json;

namespace RunnerManager;

/// <summary>A runner registered on GitHub, with all its labels: self-hosted, its system and architecture, and custom ones.</summary>
public sealed record RegisteredRunner(long Id, string Name, IReadOnlySet<string> Labels);

/// <summary>
/// The runners registered for a repository or organization on GitHub, for the health checks: they tell the exact labels
/// of the fleet's runners (an ARM64 PC, a runner without default labels), and which runners outside the fleet could take
/// a job. Read at most once a minute per scope, with an ETag, so an unchanged answer does not count against the rate
/// limit. Labels rarely change, so when GitHub cannot be read the last list is used.
/// </summary>
internal static class RegisteredRunners
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Time, Task<IReadOnlyList<RegisteredRunner>?> List)> Reads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string ETag, IReadOnlyList<RegisteredRunner> Runners)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="scope">"repos/owner/repo" or "orgs/name".</param>
    /// <returns>The first 100 runners, or null when GitHub has not been read for this scope.</returns>
    public static Task<IReadOnlyList<RegisteredRunner>?> GetAsync(string scope)
    {
        lock (Gate)
        {
            if (Reads.TryGetValue(scope, out var cached) && DateTime.UtcNow - cached.Time < Lifetime) return cached.List;
            var task = Task.Run(() => ReadAsync(scope));
            Reads[scope] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<IReadOnlyList<RegisteredRunner>?> ReadAsync(string scope)
    {
        (string ETag, IReadOnlyList<RegisteredRunner> Runners)? known;
        lock (Gate) known = Known.TryGetValue(scope, out var entry) ? entry : null;
        try
        {
            var response = await GitHubCli.GetConditionalAsync($"{scope}/actions/runners?per_page=100", known?.ETag);
            if (response.NotModified && known != null) return known.Value.Runners;
            if (response.Ok)
            {
                using var json = JsonDocument.Parse(response.Body);
                var runners = Parse(json.RootElement);
                lock (Gate) Known[scope] = (response.ETag, runners);
                return runners;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return known?.Runners;
    }

    /// <summary>The runners in a GitHub runner list response.</summary>
    internal static IReadOnlyList<RegisteredRunner> Parse(JsonElement root)
    {
        var list = new List<RegisteredRunner>();
        if (!root.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array) return list;
        foreach (var runner in runners.EnumerateArray())
        {
            if (!runner.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (runner.TryGetProperty("labels", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var label in values.EnumerateArray())
                    if (label.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text) labels.Add(text);
            list.Add(new RegisteredRunner(id.GetInt64(),
                runner.TryGetProperty("name", out var runnerName) ? runnerName.GetString() ?? string.Empty : string.Empty, labels));
        }
        return list;
    }
}
