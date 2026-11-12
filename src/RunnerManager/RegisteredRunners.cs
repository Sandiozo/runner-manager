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
    /// <summary>
    /// GitHub answers 404 for the organization list of a personal account, and for a scope the account cannot see.
    /// That does not change soon, so the scope is not asked again for this long.
    /// </summary>
    private static readonly TimeSpan NotFoundLifetime = TimeSpan.FromHours(6);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Time, Task<IReadOnlyList<RegisteredRunner>?> List)> Reads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string ETag, IReadOnlyList<RegisteredRunner> Runners)> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> NotFound = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Task<IReadOnlyList<RegisteredRunner>?> NoList = Task.FromResult<IReadOnlyList<RegisteredRunner>?>(null);
    /// <summary>Counts the calls of <see cref="Forget"/>, so a read that was sent before one does not store its 404 after it.</summary>
    private static int _generation;

    /// <param name="scope">"repos/owner/repo" or "orgs/name".</param>
    /// <returns>The first 100 runners, or null when GitHub has not been read for this scope.</returns>
    public static Task<IReadOnlyList<RegisteredRunner>?> GetAsync(string scope)
        => GetAsync(scope, GitHubCli.GetConditionalAsync, DateTime.UtcNow);

    /// <param name="get">Reads an endpoint with an ETag; tests replace it.</param>
    internal static Task<IReadOnlyList<RegisteredRunner>?> GetAsync(string scope, Func<string, string?, Task<ConditionalResult>> get, DateTime nowUtc)
    {
        // The scope comes from what a PC reports, so it is checked before it becomes part of a request.
        if (!GitHubNames.IsScope(scope)) return NoList;
        lock (Gate)
        {
            if (NotFound.TryGetValue(scope, out var since) && nowUtc - since < NotFoundLifetime) return NoList;
            if (Reads.TryGetValue(scope, out var cached) && nowUtc - cached.Time < Lifetime) return cached.List;
            var generation = _generation;
            var task = Task.Run(() => ReadAsync(scope, get, nowUtc, generation));
            Reads[scope] = (nowUtc, task);
            return task;
        }
    }

    /// <summary>
    /// Forgets the 404 answers and the reads of the last minute, so every scope is asked again at the next check: after
    /// the user connects GitHub (another account may see more) or asks for a refresh. Called by
    /// <see cref="GitHubCli.ForgetProblems"/>. The last lists stay, for their ETags and for when GitHub cannot be read.
    /// </summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            _generation++;
            NotFound.Clear();
            Reads.Clear();
        }
    }

    private static async Task<IReadOnlyList<RegisteredRunner>?> ReadAsync(string scope, Func<string, string?, Task<ConditionalResult>> get,
        DateTime nowUtc, int generation)
    {
        (string ETag, IReadOnlyList<RegisteredRunner> Runners)? known;
        lock (Gate) known = Known.TryGetValue(scope, out var entry) ? entry : null;
        try
        {
            var response = await get($"{scope}/actions/runners?per_page=100", known?.ETag);
            if (response.StatusCode == 404)
            {
                lock (Gate)
                {
                    if (generation == _generation) NotFound[scope] = nowUtc;
                    Known.Remove(scope);
                }
                return null;
            }
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
